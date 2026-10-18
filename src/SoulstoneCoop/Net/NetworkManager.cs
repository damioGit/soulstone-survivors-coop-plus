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

public static class NetworkManager
{
    public const int Port = 7777;
    private const string ConnectionKey = "soulstonecoop-v1";

    public static NetRole Role { get; private set; } = NetRole.None;
    public static NetStatus Status { get; private set; } = NetStatus.Idle;
    public static NetManager? Manager { get; private set; }
    public static NetPeer? RemotePeer { get; private set; }

    /// <summary>Last connection problem, in Portuguese, for the panel (null = none).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Address the client is trying to reach (for the panel).</summary>
    public static string? TargetAddress { get; private set; }

    /// <summary>Fired when a peer connects, on both host and client.</summary>
    public static event System.Action? PeerConnected;

    /// <summary>Fired when the peer goes away: remote disconnect, timeout, or our own Stop().</summary>
    public static event System.Action? PeerDisconnected;

    private static EventBasedNetListener? _listener;
    private static bool _stopRequested;

    public static void StartHost()
    {
        Stop();
        LastError = null;
        _listener = new EventBasedNetListener();
        Manager = new NetManager(_listener);
        if (!Manager.Start(Port))
        {
            Manager = null;
            _listener = null;
            LastError = $"Não deu pra abrir a porta {Port} (outra instância já hospedando?)";
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Could not listen on port {Port}.");
            return;
        }

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += peer =>
        {
            RemotePeer = peer;
            Status = NetStatus.Connected;
            LastError = null;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Client connected: {peer.Address}");
            PeerConnected?.Invoke();
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (RemotePeer == null || peer.Id != RemotePeer.Id) return; // a rejected attempt, not our partner
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Client disconnected: {info.Reason}");
            RemotePeer = null;
            Status = NetStatus.Hosting; // keep listening for a new partner
            PeerDisconnected?.Invoke();
        };
        _listener.NetworkReceiveEvent += OnReceive;

        Role = NetRole.Host;
        Status = NetStatus.Hosting;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Hosting on port {Port} (mod {Plugin.HandshakeVersion})");
    }

    /// <summary>Host: accept only the same mod version, and only one partner.</summary>
    private static void OnConnectionRequest(ConnectionRequest request)
    {
        string key = "", version = "";
        try
        {
            request.Data.TryGetString(out key);
            if (!request.Data.TryGetString(out version)) version = "";
        }
        catch { }

        string? refuse = null;
        if (key != ConnectionKey) refuse = "Chave de conexão desconhecida";
        else if (version != Plugin.HandshakeVersion)
            refuse = $"Versão diferente: host {Plugin.HandshakeVersion}, você {(version.Length > 0 ? version : "antiga")}";
        else if (RemotePeer != null) refuse = "O host já tem um parceiro";

        if (refuse == null)
        {
            request.Accept();
            return;
        }

        Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Refused {request.RemoteEndPoint}: {refuse} (client version '{version}').");
        if (key == ConnectionKey && version != Plugin.HandshakeVersion)
            LastError = $"Recusei um cliente com a versão {(version.Length > 0 ? version : "antiga")} (a minha é {Plugin.HandshakeVersion})";
        var w = new NetDataWriter();
        w.Put(refuse);
        request.Reject(w);
    }

    public static void StartClient(string address)
    {
        Stop();
        LastError = null;
        TargetAddress = address;
        _listener = new EventBasedNetListener();
        Manager = new NetManager(_listener);
        Manager.Start();

        _listener.PeerConnectedEvent += peer =>
        {
            RemotePeer = peer;
            Status = NetStatus.Connected;
            LastError = null;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Connected to host {peer.Address}");
            PeerConnected?.Invoke();
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (info.Reason == DisconnectReason.ConnectionRejected)
            {
                string reason = "Conexão recusada pelo host";
                try { if (info.AdditionalData != null && info.AdditionalData.TryGetString(out var s) && s.Length > 0) reason = s; }
                catch { }
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Host refused the connection: {reason}");
                LastError = reason;
                bool hadPeer = RemotePeer != null;
                RemotePeer = null;
                Status = NetStatus.Rejected;
                _stopRequested = true; // don't retry: same answer every time
                if (hadPeer) PeerDisconnected?.Invoke();
                return;
            }

            // Host not up yet: keep retrying instead of giving up after LiteNetLib's ~5s attempt window.
            if (info.Reason == DisconnectReason.ConnectionFailed && RemotePeer == null)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Host not reachable yet, retrying {address}:{Port}...");
                Manager?.Connect(address, Port, ConnectData());
                return;
            }

            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Disconnected from host: {info.Reason}");
            LastError = info.Reason == DisconnectReason.RemoteConnectionClose ? "O host desconectou" : $"Conexão perdida ({info.Reason})";
            RemotePeer = null;
            Status = NetStatus.Idle;
            _stopRequested = true; // back to idle: the role must not stay Client (it blocks solo spawns)
            PeerDisconnected?.Invoke();
        };
        _listener.NetworkReceiveEvent += OnReceive;

        Manager.Connect(address, Port, ConnectData());
        Role = NetRole.Client;
        Status = NetStatus.Connecting;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Connecting to {address}:{Port} (mod {Plugin.HandshakeVersion})");
    }

    private static NetDataWriter ConnectData()
    {
        var w = new NetDataWriter();
        w.Put(ConnectionKey);
        w.Put(Plugin.HandshakeVersion);
        return w;
    }

    /// <summary>Leave co-op: closes the socket and runs every module's disconnect cleanup if a partner was there.</summary>
    public static void Stop()
    {
        bool hadPeer = RemotePeer != null;
        Manager?.Stop();
        Manager = null;
        _listener = null;
        RemotePeer = null;
        Role = NetRole.None;
        Status = NetStatus.Idle;
        _stopRequested = false;
        if (hadPeer)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] Stopped with a partner connected - running disconnect cleanup.");
            PeerDisconnected?.Invoke();
        }
    }

    // Call once per frame (e.g. from a MonoBehaviour Update or a Harmony UpdateAll postfix).
    public static void PollEvents()
    {
        Manager?.PollEvents();
        if (_stopRequested)
        {
            // Deferred: stopping from inside a LiteNetLib event callback is not safe.
            var status = Status;
            var error = LastError;
            Stop();
            if (status == NetStatus.Rejected) Status = NetStatus.Rejected;
            LastError = error;
        }
    }

    public static void Send(NetDataWriter writer, DeliveryMethod method)
    {
        RemotePeer?.Send(writer, method);
    }

    private static void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        PacketRouter.Handle(peer, reader);
        reader.Recycle();
    }
}
