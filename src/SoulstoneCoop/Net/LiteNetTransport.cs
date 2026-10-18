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
    // LiteNetLib's Ping is half the (averaged) round trip; the interface, the panel and Steam show the whole trip.
    public int PingMs => _peer != null ? _peer.Ping * 2 : -1;
    public float SilenceSeconds => _peer != null ? _peer.TimeSinceLastPacket / 1000f : 0f; // LiteNetLib counts in ms
    public bool WantsStop { get; private set; }

    /// <summary>Phase 8: LiteNetLib's default is 5 s, which a Wi-Fi hiccup on a real network can exceed. Its pings
    /// run on its own thread, so a frozen game (scene load) doesn't count as silence.</summary>
    private const int DisconnectTimeoutMs = 10000;

    private readonly TransportCallbacks _cb;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private NetPeer? _peer;

    private LiteNetTransport(TransportCallbacks cb)
    {
        _cb = cb;
        _manager = new NetManager(_listener) { DisconnectTimeout = DisconnectTimeoutMs };
        _listener.NetworkReceiveEvent += OnReceive;
        ApplySimulation(_manager);
    }

    /// <summary>Phase 8: LiteNetLib's own simulation (compiled into the 1.3.1 release build), applied to what this
    /// game RECEIVES: every UDP packet, acks and pings included, so reliable messages really get resent.</summary>
    private static void ApplySimulation(NetManager m)
    {
        if (!NetSim.Enabled) return;
        if (NetSim.LatencyMs > 0 || NetSim.JitterMs > 0)
        {
            m.SimulateLatency = true;
            m.SimulationMinLatency = Math.Max(0, NetSim.LatencyMs - NetSim.JitterMs);
            m.SimulationMaxLatency = NetSim.LatencyMs + NetSim.JitterMs;
        }
        if (NetSim.LossPercent > 0)
        {
            m.SimulatePacketLoss = true;
            m.SimulationPacketLossChance = NetSim.LossPercent;
        }
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][netsim] IP transport: simulating {NetSim.Describe()} on incoming packets.");
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

    private int _oversizeWarnings;

    /// <summary>Phase 8: LiteNetLib 1.3.1 keeps the MTU at 1024 (no MTU discovery by default) and throws
    /// TooBigPacketException for an Unreliable/Sequenced packet above it. An oversized one goes reliable instead
    /// (it fragments), so it is never lost and never throws into the caller's frame.</summary>
    public void Send(byte[] data, int length, DeliveryMethod method)
    {
        var peer = _peer;
        if (peer == null) return;
        if ((method == DeliveryMethod.Unreliable || method == DeliveryMethod.Sequenced) && length > peer.GetMaxSinglePacketSize(method))
        {
            if (_oversizeWarnings++ < 5)
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop] {length} B {method} packet (type {data[0]}) is over the {peer.GetMaxSinglePacketSize(method)} B limit: sent ReliableUnordered.");
            method = DeliveryMethod.ReliableUnordered;
        }
        peer.Send(data, 0, length, method);
    }

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
