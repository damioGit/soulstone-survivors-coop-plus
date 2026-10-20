using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

/// <summary>
/// Direct IP over LiteNetLib (UDP 7777): what the mod used up to phase 6, now behind ITransport. Kept for local
/// tests (several instances on one PC) and LAN/VPN. The phase 6 handshake (key + mod version in the connect request,
/// refusal with a reason) lives here. Phase 9: the host takes up to NetworkManager.MaxClients clients, one slot each.
/// </summary>
public sealed class LiteNetTransport : ITransport
{
    public const int Port = 7777;
    private const string ConnectionKey = "soulstonecoop-v2"; // phase 9: v1 builds speak the two-player format

    public string Kind => "IP";
    public bool HasPeer => _bySlot.Count > 0;
    public IReadOnlyList<byte> PeerSlots => _slots.Connected;
    public NetStatus Status { get; private set; } = NetStatus.Idle;
    public string? LastError { get; private set; }
    public bool WantsStop { get; private set; }

    // LiteNetLib's Ping is half the (averaged) round trip; the interface, the panel and Steam show the whole trip.
    public int PingMs(byte slot) => _bySlot.TryGetValue(slot, out var p) ? p.Ping * 2 : -1;
    public float SilenceSeconds(byte slot) => _bySlot.TryGetValue(slot, out var p) ? p.TimeSinceLastPacket / 1000f : 0f; // LiteNetLib counts in ms

    /// <summary>Phase 8: LiteNetLib's default is 5 s, which a Wi-Fi hiccup on a real network can exceed. Its pings
    /// run on its own thread, so a frozen game (scene load) doesn't count as silence.</summary>
    private const int DisconnectTimeoutMs = 10000;

    private readonly TransportCallbacks _cb;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private readonly bool _isHost;
    private readonly SlotTable _slots = new();
    private readonly Dictionary<byte, NetPeer> _bySlot = new();
    private readonly Dictionary<int, byte> _slotByPeerId = new();

    private LiteNetTransport(TransportCallbacks cb, bool isHost)
    {
        _cb = cb;
        _isHost = isHost;
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
        var t = new LiteNetTransport(cb, isHost: true);
        if (!t._manager.Start(Port))
        {
            error = Ui.Loc.Msg("net.port_busy", ("port", Port));
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Could not listen on port {Port}.");
            return null;
        }
        error = null;

        t._listener.ConnectionRequestEvent += t.OnConnectionRequest;
        t._listener.PeerConnectedEvent += peer =>
        {
            byte slot = t._slots.Take();
            if (slot == Players.NoSlot)
            {
                // Two requests accepted in the same frame for the last seat: the second one goes.
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop] {peer.Address} connected with the session already full - dropped.");
                var w = new NetDataWriter();
                w.Put(Ui.Loc.Msg("net.full", ("max", NetworkManager.MaxPlayers)));
                peer.Disconnect(w);
                return;
            }
            t._bySlot[slot] = peer;
            t._slotByPeerId[peer.Id] = slot;
            t._slots.MarkConnected(slot);
            t.Status = NetStatus.Connected;
            t.LastError = null;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Client connected: {peer.Address} -> slot {slot} ({t._bySlot.Count} client(s)).");
            t._cb.Connected(slot);
        };
        t._listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (!t._slotByPeerId.TryGetValue(peer.Id, out byte slot)) return; // a rejected attempt, not one of ours
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Client on slot {slot} disconnected: {info.Reason}");
            t._slotByPeerId.Remove(peer.Id);
            t._bySlot.Remove(slot);
            t._slots.Release(slot);
            if (t._bySlot.Count == 0) t.Status = NetStatus.Hosting; // keep listening for new players
            t._cb.Disconnected(slot);
        };

        t.Status = NetStatus.Hosting;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Hosting on port {Port} (mod {Plugin.HandshakeVersion}, up to {NetworkManager.MaxPlayers} players)");
        return t;
    }

    /// <summary>Host: accept only the same mod version, and only while there is a free seat.</summary>
    private void OnConnectionRequest(ConnectionRequest request)
    {
        string key = "", version = "";
        try
        {
            request.Data.TryGetString(out key);
            if (!request.Data.TryGetString(out version)) version = "";
        }
        catch { }

        // Refusals are Ui.Loc.Msg: the client shows them in its language (an older mod shows the English inside).
        string? refuse = null;
        if (key != ConnectionKey) refuse = Ui.Loc.Msg("net.bad_key");
        else if (version != Plugin.HandshakeVersion) refuse = NetworkManager.VersionRefusal(version);
        else if (!_slots.HasRoom) refuse = NetworkManager.FullRefusal();

        if (refuse == null)
        {
            request.Accept();
            return;
        }

        Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Refused {request.RemoteEndPoint}: {Ui.Loc.Plain(refuse)} (client version '{version}').");
        if (key == ConnectionKey && version != Plugin.HandshakeVersion) LastError = NetworkManager.RefusedClientError(version);
        var w = new NetDataWriter();
        w.Put(refuse);
        request.Reject(w);
    }

    public static LiteNetTransport Client(TransportCallbacks cb, string address)
    {
        var t = new LiteNetTransport(cb, isHost: false);
        t._manager.Start();

        t._listener.PeerConnectedEvent += peer =>
        {
            t._bySlot[Players.HostSlot] = peer;
            t._slotByPeerId[peer.Id] = Players.HostSlot;
            t._slots.MarkConnected(Players.HostSlot);
            t.Status = NetStatus.Connected;
            t.LastError = null;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Connected to host {peer.Address}");
            t._cb.Connected(Players.HostSlot);
        };
        t._listener.PeerDisconnectedEvent += (peer, info) =>
        {
            bool hadPeer = t._bySlot.ContainsKey(Players.HostSlot);
            if (info.Reason == DisconnectReason.ConnectionRejected || (hadPeer && info.Reason == DisconnectReason.RemoteConnectionClose && info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0))
            {
                // A Ui.Loc.Msg from a host with this mod version, plain text from an older one: shown as it comes.
                string reason = Ui.Loc.Msg("net.refused");
                try { if (info.AdditionalData != null && info.AdditionalData.TryGetString(out var s) && s.Length > 0) reason = s; }
                catch { }
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Host refused the connection: {Ui.Loc.Plain(reason)}");
                t.LastError = reason;
                t.ForgetHost();
                t.Status = NetStatus.Rejected;
                t.WantsStop = true; // don't retry: same answer every time
                if (hadPeer) t._cb.Disconnected(Players.HostSlot);
                return;
            }

            // Host not up yet: keep retrying instead of giving up after LiteNetLib's ~5s attempt window.
            if (info.Reason == DisconnectReason.ConnectionFailed && !hadPeer)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Host not reachable yet, retrying {address}:{Port}...");
                t._manager.Connect(address, Port, ConnectData());
                return;
            }

            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Disconnected from host: {info.Reason}");
            t.LastError = info.Reason == DisconnectReason.RemoteConnectionClose ? Ui.Loc.Msg("net.host_left") : Ui.Loc.Msg("net.lost", ("why", info.Reason));
            t.ForgetHost();
            t.Status = NetStatus.Idle;
            t.WantsStop = true; // back to idle: the role must not stay Client (it blocks solo spawns)
            if (hadPeer) t._cb.Disconnected(Players.HostSlot);
        };

        t._manager.Connect(address, Port, ConnectData());
        t.Status = NetStatus.Connecting;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Connecting to {address}:{Port} (mod {Plugin.HandshakeVersion})");
        return t;
    }

    private void ForgetHost()
    {
        _bySlot.Clear();
        _slotByPeerId.Clear();
        _slots.Clear();
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
    public void Send(byte slot, byte[] data, int length, DeliveryMethod method)
    {
        if (!_bySlot.TryGetValue(slot, out var peer)) return;
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
        ForgetHost();
        _manager.Stop();
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            if (_slotByPeerId.TryGetValue(peer.Id, out byte slot)) _cb.Data(reader, slot);
        }
        finally { reader.Recycle(); }
    }
}
