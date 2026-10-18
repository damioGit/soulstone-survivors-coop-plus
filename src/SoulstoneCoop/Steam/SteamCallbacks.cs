using System.Runtime.InteropServices;
using HarmonyLib;

namespace SoulstoneCoop.Steam;

/// <summary>
/// Phase 7: Steam callback pump. The game runs Steamworks.NET in manual-dispatch mode and its
/// CallbackDispatcher.RunFrame drains the whole callback queue every LateUpdate, dropping everything it has no
/// handler for (Callback&lt;T&gt; was stripped, so that is every callback). A second reader would race it, so this
/// takes the pump over: a prefix on SteamAPI.RunCallbacks runs the same loop, handles our callback ids and our
/// call results, and forwards everything else to the game's own registries exactly like RunFrame does (today that
/// is only mod.io's EncryptedAppTicket call result). If our loop can't start, the game's original runs.
/// </summary>
internal static unsafe class SteamCallbacks
{
    public delegate void CallbackHandler(IntPtr data, int size);
    public delegate void CallResultHandler(IntPtr data, bool ioFailure);

    private static readonly Dictionary<int, CallbackHandler> Handlers = new();
    private static readonly Dictionary<ulong, (int expected, CallResultHandler handler)> CallResults = new();
    private static readonly Dictionary<int, int> SeenCounts = new();

    /// <summary>Our loop has run at least once (the takeover works).</summary>
    public static bool Active { get; private set; }
    private static bool _broken;
    private static int _forwardedResults, _forwardedCallbacks;

    public static void On(int callbackId, CallbackHandler handler) => Handlers[callbackId] = handler;

    /// <summary>Route the result of an async Steam call (SteamAPICall_t) to us.</summary>
    public static void Expect(ulong apiCall, int expectedCallbackId, CallResultHandler handler)
    {
        if (apiCall == 0) return; // k_uAPICallInvalid
        CallResults[apiCall] = (expectedCallbackId, handler);
    }

    /// <summary>Returns false when the original RunFrame should run instead (Steam off, or our loop broke).</summary>
    internal static bool RunFrame()
    {
        if (_broken) return false;
        if (!SteamManager.Initialized) return false;

        int pipe;
        try
        {
            pipe = SteamNative.SteamAPI_GetHSteamPipe();
            SteamNative.SteamAPI_ManualDispatch_RunFrame(pipe);
        }
        catch (Exception ex)
        {
            _broken = true;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][steam] Callback pump unavailable, leaving it to the game: {ex.Message}");
            return false;
        }

        if (!Active)
        {
            Active = true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Callback pump taken over (pipe {pipe}).");
        }

        SteamNative.CallbackMsg msg;
        while (SteamNative.SteamAPI_ManualDispatch_GetNextCallback(pipe, &msg) != 0)
        {
            try
            {
                Count(msg.Callback);
                if (msg.Callback == SteamNative.SteamAPICallCompleted)
                    OnCallCompleted(pipe, msg.Param);
                else if (Handlers.TryGetValue(msg.Callback, out var h))
                    h(msg.Param, msg.ParamSize);
                else
                    ForwardCallback(msg.Callback, msg.Param);
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogError($"[SoulstoneCoop][steam] Callback {msg.Callback} failed: {ex}");
            }
            finally
            {
                SteamNative.SteamAPI_ManualDispatch_FreeLastCallback(pipe);
            }
        }
        return true;
    }

    private static void OnCallCompleted(int pipe, IntPtr param)
    {
        ulong call = *(ulong*)param;
        int expected = *(int*)((byte*)param + 8);
        int size = (int)*(uint*)((byte*)param + 12);

        // Same as the game's RunFrame: always fetch the result, then hand it to whoever registered it.
        IntPtr buffer = Marshal.AllocHGlobal(Math.Max(size, 1));
        try
        {
            byte failed = 0;
            if (SteamNative.SteamAPI_ManualDispatch_GetAPICallResult(pipe, call, buffer, size, expected, &failed) == 0)
                return;

            if (CallResults.TryGetValue(call, out var mine))
            {
                CallResults.Remove(call);
                if (mine.expected != expected)
                    Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] Call {call} completed as callback {expected}, expected {mine.expected}.");
                mine.handler(buffer, failed != 0);
                return;
            }

            ForwardCallResult(call, buffer, failed != 0, expected);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ForwardCallResult(ulong call, IntPtr buffer, bool failed, int callbackId)
    {
        var registry = Steamworks.CallbackDispatcher.m_registeredCallResults;
        if (registry == null) return;
        if (!registry.ContainsKey(call)) return;
        var list = registry[call];
        registry.Remove(call);
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var cr = list[i];
            if (cr == null) continue;
            cr.OnRunCallResult(buffer, failed, call);
            cr.SetUnregistered();
        }
        _forwardedResults++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Forwarded call result {callbackId} to the game ({list.Count} handler(s), total {_forwardedResults}).");
    }

    private static void ForwardCallback(int id, IntPtr param)
    {
        var registry = Steamworks.CallbackDispatcher.m_registeredCallbacks;
        if (registry == null || !registry.ContainsKey(id)) return;
        var list = registry[id];
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
            list[i]?.OnRunCallback(param);
        _forwardedCallbacks++;
        if (_forwardedCallbacks <= 5)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Forwarded callback {id} to the game.");
    }

    private static void Count(int id)
    {
        SeenCounts.TryGetValue(id, out int n);
        SeenCounts[id] = n + 1;
        if (n == 0 && id != SteamNative.SteamAPICallCompleted)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] First callback {id}{(Handlers.ContainsKey(id) ? " (ours)" : "")}.");
    }

    internal static string Summary() =>
        string.Join(" ", SeenCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}")) +
        $" fwdResults={_forwardedResults} fwdCallbacks={_forwardedCallbacks}";
}

/// <summary>The pump takeover: no arguments, returns void, so the Il2CppInterop trampoline is safe. Patched by hand
/// in Plugin.Load (not PatchAll): if it fails, the game keeps its own pump and the other patches still apply.</summary>
internal static class SteamRunCallbacksPatch
{
    public static bool Prefix()
    {
        try { return !SteamCallbacks.RunFrame(); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][steam] RunCallbacks prefix: {ex}");
            return false; // the queue may be half drained; running the original now would not help
        }
    }
}
