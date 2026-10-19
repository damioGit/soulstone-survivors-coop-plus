using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

public enum NetRole
{
    None,
    Host,
    Client
}

/// <summary>What the co-op panel shows about the transport itself (the lobby state is in CoopSession).</summary>
public enum NetStatus
{
    Idle,
    Hosting,     // listening, no partner yet
    Connecting,  // client: retrying until the host answers
    Connected,
    Rejected,    // client: the host refused us (version, already full); LastError says why
}

/// <summary>
/// The one place the mod talks to the network through. Phase 7: the actual link is an ITransport (LiteNetLib by
/// IP, or Steam P2P); everything above (Send, the events, Role/Status for the panel) is the same for both.
/// </summary>
public static class NetworkManager
{
    public const int Port = LiteNetTransport.Port;

    public static NetRole Role { get; private set; } = NetRole.None;
    public static ITransport? Transport { get; private set; }

    /// <summary>The partner is there (handshake done). Replaces the old `RemotePeer != null`.</summary>
    public static bool HasPeer => Transport?.HasPeer == true;

    public static NetStatus Status => Transport?.Status ?? _idleStatus;

    /// <summary>Last connection problem, in Portuguese, for the panel (null = none).</summary>
    public static string? LastError => Transport != null ? Transport.LastError : _idleError;

    /// <summary>IP the client is trying to reach (for the panel).</summary>
    public static string? TargetAddress { get; private set; }

    /// <summary>"IP" or "Steam" while a transport is up, null otherwise.</summary>
    public static string? Kind => Transport?.Kind;

    public static int PingMs => Transport?.PingMs ?? -1;

    /// <summary>Phase 8: seconds since anything arrived from the partner (0 without one).</summary>
    public static float SilenceSeconds => HasPeer ? Transport!.SilenceSeconds : 0f;

    /// <summary>Phase 8: past this much silence the panel and the banner say "conexão instável" (both transports
    /// send at least a ping per second; the drop itself comes much later: 10 s on IP, 15 s on Steam).</summary>
    public const float UnstableAfterSeconds = 2f;

    public static bool Unstable => HasPeer && SilenceSeconds >= UnstableAfterSeconds;

    /// <summary>Fired when a peer connects, on both host and client.</summary>
    public static event System.Action? PeerConnected;

    /// <summary>Fired when the peer goes away: remote disconnect, timeout, or our own Stop().</summary>
    public static event System.Action? PeerDisconnected;

    // What Status/LastError show once the transport is gone (a refusal must stay on the panel).
    private static NetStatus _idleStatus = NetStatus.Idle;
    private static string? _idleError;

    private static TransportCallbacks Callbacks() => new()
    {
        Connected = () => PeerConnected?.Invoke(),
        Disconnected = () => PeerDisconnected?.Invoke(),
        Data = reader => PacketRouter.Handle(reader),
    };

    public static void StartHost()
    {
        Stop();
        _idleError = null;
        var t = LiteNetTransport.Host(Callbacks(), out var error);
        if (t == null)
        {
            _idleError = error;
            return;
        }
        Begin(t, NetRole.Host);
    }

    public static void StartClient(string address)
    {
        Stop();
        _idleError = null;
        TargetAddress = address;
        Begin(LiteNetTransport.Client(Callbacks(), address), NetRole.Client);
    }

    /// <summary>Steam: host the partner who joins our lobby.</summary>
    public static void StartSteamHost(ulong lobbyId)
    {
        Stop();
        _idleError = null;
        Begin(new SteamTransport(Callbacks(), lobbyId, hostId: 0), NetRole.Host);
    }

    /// <summary>Steam: connect to the owner of the lobby we just entered.</summary>
    public static void StartSteamClient(ulong lobbyId, ulong hostId)
    {
        Stop();
        _idleError = null;
        Begin(new SteamTransport(Callbacks(), lobbyId, hostId), NetRole.Client);
    }

    private static void Begin(ITransport t, NetRole role)
    {
        Transport = t;
        Role = role;
    }

    /// <summary>Leave co-op: closes the transport and runs every module's disconnect cleanup if a partner was there.</summary>
    public static void Stop()
    {
        var t = Transport;
        if (t == null)
        {
            Role = NetRole.None;
            return;
        }
        bool hadPeer = t.HasPeer;
        _idleStatus = NetStatus.Idle;
        _idleError = t.LastError;
        try { t.Stop(); }
        catch (System.Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Transport stop failed: {ex.Message}"); }
        Transport = null;
        Role = NetRole.None;
        if (hadPeer)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] Stopped with a partner connected - running disconnect cleanup.");
            PeerDisconnected?.Invoke();
        }
    }

    // Call once per frame (CoopBehaviour.Update).
    public static void PollEvents()
    {
        var t = Transport;
        if (t == null) return;
        t.Poll();
        if (t.WantsStop && Transport == t)
        {
            // Deferred: stopping from inside a transport callback is not safe.
            var status = t.Status;
            Stop();
            if (status == NetStatus.Rejected) _idleStatus = NetStatus.Rejected;
        }
        LogStability();
    }

    private static bool _wasUnstable;
    private static float _worstSilence;

    /// <summary>Phase 8: one log line when the link goes quiet and one when it comes back (with how long).</summary>
    private static void LogStability()
    {
        bool unstable = Unstable;
        if (unstable)
        {
            float s = SilenceSeconds;
            if (s > _worstSilence) _worstSilence = s;
        }
        if (unstable == _wasUnstable) return;
        _wasUnstable = unstable;
        if (unstable) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][net] Link unstable: nothing from the partner for {SilenceSeconds:F1}s ({Kind}).");
        else
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][net] Link back after {_worstSilence:F1}s of silence (peer={HasPeer}).");
            _worstSilence = 0f;
        }
    }

    private static int _sendErrors;

    /// <summary>Phase 8: never throws. Senders run inside our Update (and some inside game patches); one exception
    /// here used to skip the rest of the mod's frame.</summary>
    public static void Send(NetDataWriter writer, DeliveryMethod method)
    {
        var t = Transport;
        if (t == null) return;
        try { t.Send(writer.Data, writer.Length, method); }
        catch (System.Exception ex)
        {
            if (_sendErrors++ < 10)
                Plugin.StaticLog.LogError($"[SoulstoneCoop] Send of packet {(writer.Length > 0 ? writer.Data[0] : -1)} ({writer.Length} B, {method}) failed: {ex.Message}");
        }
    }
}
