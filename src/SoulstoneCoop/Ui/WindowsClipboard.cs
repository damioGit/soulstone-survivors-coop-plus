using System.Runtime.InteropServices;
using HarmonyLib;

namespace SoulstoneCoop.Ui;

/// <summary>Reads text from the Windows clipboard (the "Colar" button). GUIUtility.systemCopyBuffer lives in the
/// IMGUI module, which is partly stripped in this build, so go straight to user32.</summary>
internal static class WindowsClipboard
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr mem);

    public static string? TryGetText()
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return "";
                var p = GlobalLock(handle);
                if (p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Clipboard: {ex.Message}");
            return null;
        }
    }
}

/// <summary>See CoopUi.KeepSelection. No arguments: safe to patch. The flag is restored right after, so the game
/// keeps its behavior everywhere else.</summary>
[HarmonyPatch(typeof(GameLoop), nameof(GameLoop.Update))]
public static class GameLoopKeepSelectionPatch
{
    private static bool _restore;
    private static bool _logged;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _restore = false;
        try
        {
            if (!CoopUi.KeepSelection || !Global.ForceCleanEventSystemSelectedObjectKeyboard) return;
            Global.ForceCleanEventSystemSelectedObjectKeyboard = false;
            _restore = true;
            if (!_logged)
            {
                _logged = true;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] Co-op panel open: holding the game's per-frame selection clear (ForceCleanEventSystemSelectedObjectKeyboard was on).");
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] GameLoopKeepSelectionPatch.Prefix: {ex.Message}"); }
    }

    [HarmonyPostfix]
    public static void Postfix()
    {
        if (!_restore) return;
        _restore = false;
        try { Global.ForceCleanEventSystemSelectedObjectKeyboard = true; }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] GameLoopKeepSelectionPatch.Postfix: {ex.Message}"); }
    }
}
