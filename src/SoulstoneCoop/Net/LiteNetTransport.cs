using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

/// <summary>
/// Direct IP over LiteNetLib (UDP 7777): what the mod used up to phase 6, now behind ITransport. Kept for local
/// tests (two instances on one PC) and LAN/VPN. The phase 6 handshake (key + mod version in the connect request,
/// refusal with a reason) lives here unchanged.
/// </summary>
public sealed class LiteNetTransport : ITransport
{
    public const int Port = 7777;
    private const string ConnectionKey = "soulstonecoop-v1";

    public string Kind => "IP";
    public bool HasPeer => _peer != null;
    public NetStatus Status { get; private set; } = NetStatus.Idle;
    public string? LastError { get; private set; }
    public int PingMs => _peer?.Ping ?? -1;
    public bool WantsStop { get; private set; }

    private readonly TransportCallbacks _cb;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private NetPeer? _peer;

    private LiteNetTransport(TransportCallbacks cb)
    {
        _cb = cb;
        _manager = new NetManager(_listener);
        _listener.NetworkReceiveEvent += OnReceive;
    }

    /// <summary>Listen on the port. Returns null (with the error in <paramref name="error"/>) if it is taken.</summary>
    public static LiteNetTransport? Host(TransportCallbacks cb, out string? error)
    {
        var t = new LiteNetTransport(cb);
        if (!t._manager.Start(Port))
        {
            error = $"Não deu pra abrir a porta {Port} (outra instância já hospedando?)";
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Could not listen on port {Port}.");
            return null;
        }
        error = null;

        t._listener.ConnectionRequestEvent += t.OnConnectionRequest;
        t._listener.PeerConnectedEvent += peer =>
        {
            t._peer = peer;
            t.Status = NetStatus.Connected;
            t.LastError = null;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Client connected: {peer.Address}");
            t._cb.Connected();
        };
        t._listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (t._peer == null || peer.Id != t._peer.Id) return; // a rejected attempt, not our partner
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Client disconnected: {info.Reason}");
            t._peer = null;
            t.Status = NetStatus.Hosting; // keep listening for a new partner
            t._cb.Disconnected();
        };

        t.Status = NetStatus.Hosting;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Hosting on port {Port} (mod {Plugin.HandshakeVersion})");
        return t;
    }

    /// <summary>Host: accept only the same mod version, and only one partner.</summary>
    private void OnConnectionRequest(ConnectionRequest request)
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
        else if (_peer != null) refuse = "O host já tem um parceiro";

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

    public static LiteNetTransport Client(TransportCallbacks cb, string address)
    {
        var t = new LiteNetTransport(cb);
        t._manager.Start();

        t._listener.PeerConnectedEvent += peer =>
        {
            t._peer = peer;
            t.Status = NetStatus.Connected;
            t.LastError = null;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Connected to host {peer.Address}");
            t._cb.Connected();
        };
        t._listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (info.Reason == DisconnectReason.ConnectionRejected)
            {
                string reason = "Conexão recusada pelo host";
                try { if (info.AdditionalData != null && info.AdditionalData.TryGetString(out var s) && s.Length > 0) reason = s; }
                catch { }
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Host refused the connection: {reason}");
                t.LastError = reason;
                bool hadPeer = t._peer != null;
                t._peer = null;
                t.Status = NetStatus.Rejected;
                t.WantsStop = true; // don't retry: same answer every time
                if (hadPeer) t._cb.Disconnected();
                return;
            }

            // Host not up yet: keep retrying instead of giving up after LiteNetLib's ~5s attempt window.
            if (info.Reason == DisconnectReason.ConnectionFailed && t._peer == null)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Host not reachable yet, retrying {address}:{Port}...");
                t._manager.Connect(address, Port, ConnectData());
                return;
            }

            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Disconnected from host: {info.Reason}");
            t.LastError = info.Reason == DisconnectReason.RemoteConnectionClose ? "O host desconectou" : $"Conexão perdida ({info.Reason})";
            t._peer = null;
            t.Status = NetStatus.Idle;
            t.WantsStop = true; // back to idle: the role must not stay Client (it blocks solo spawns)
            t._cb.Disconnected();
        };

        t._manager.Connect(address, Port, ConnectData());
        t.Status = NetStatus.Connecting;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Connecting to {address}:{Port} (mod {Plugin.HandshakeVersion})");
        return t;
    }

    private static NetDataWriter ConnectData()
    {
        var w = new NetDataWriter();
        w.Put(ConnectionKey);
        w.Put(Plugin.HandshakeVersion);
        return w;
    }

    public void Poll() => _manager.PollEvents();

    public void Send(byte[] data, int length, DeliveryMethod method) => _peer?.Send(data, 0, length, method);

    public void Stop()
    {
        _peer = null;
        _manager.Stop();
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try { _cb.Data(reader); }
        finally { reader.Recycle(); }
    }
}
