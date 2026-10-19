using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Steam;
using UnityEngine;

namespace SoulstoneCoop.Net;

/// <summary>
/// Phase 7: co-op over Steam P2P (ISteamNetworkingMessages, Steam Datagram Relay: no port forwarding, no server
/// of ours). Peers are addressed by SteamID, taken from the lobby. Channel 0 carries the normal payloads
/// (reliable = ordered, unreliable = no Nagle); channel 1 carries LiteNetLib's Sequenced traffic (player state)
/// with a u16 sequence in front so stale packets are dropped. Packets 40-45 are this transport's own handshake
/// and keepalive and never reach PacketRouter. Docs: docs/fase7-investigacao.md, section 4.
/// Phase 9: the host keeps one session per client (up to NetworkManager.MaxClients), each with its own slot, ping,
/// silence timer and sequence numbers. A client has a single peer, the host (slot 0).
/// </summary>
public sealed unsafe class SteamTransport : ITransport
{
    private enum T : byte
    {
        Hello = 40,   // client -> host: key, mod version, transport protocol
        Accept = 41,  // host -> client: [slot u8] (phase 9; the Welcome packet carries it too)
        Reject = 42,  // host -> client: reason
        Bye = 43,     // either: reason (clean leave)
        Ping = 44,    // either: u32 local ms
        Pong = 45,    // either: u32 echoed ms
    }

    private const string ConnectionKey = "soulstonecoop-v2"; // phase 9: v1 builds speak the two-player format
    private const int ChannelMain = 0, ChannelSequenced = 1;
    private const float HelloInterval = 1f, ConnectTimeout = 30f, PingInterval = 1f, SilenceTimeout = 15f;
    /// <summary>Phase 8: our pings are sent from the game's main thread, which a scene load can freeze for a while:
    /// while either side is loading a run, allow a longer silence before calling the peer gone.</summary>
    private const float LoadingSilenceTimeout = 45f;

    private sealed class Peer
    {
        public ulong Id;
        public byte Slot;
        public float LastHeard, PingTimer, SessionTimer;
        public int PingMs = -1;
        public ushort SeqOut, SeqIn;
        public bool SeqInValid;
    }

    public string Kind => "Steam";
    public bool HasPeer => _byId.Count > 0;
    public IReadOnlyList<byte> PeerSlots => _slots.Connected;
    public NetStatus Status { get; private set; }
    public string? LastError { get; private set; }
    public bool WantsStop { get; private set; }

    public int PingMs(byte slot) => _bySlot.TryGetValue(slot, out var p) ? p.PingMs : -1;
    public float SilenceSeconds(byte slot) => _bySlot.TryGetValue(slot, out var p) ? Time.unscaledTime - p.LastHeard : 0f;

    // Phase 8: simulated latency/loss on what we send (null = off). Handshake packets (40-43) skip it.
    private readonly SimSendQueue? _sim;
    private readonly SimSendQueue.SendNow _sendNow;

    private readonly TransportCallbacks _cb;
    private readonly bool _isHost;
    private readonly ulong _lobby;
    private readonly ulong _hostId; // client: the lobby owner we connect to
    private readonly IntPtr _msgs;
    private readonly Dictionary<ulong, Peer> _byId = new();
    private readonly Dictionary<byte, Peer> _bySlot = new();
    private readonly SlotTable _slots = new();
    private float _helloTimer, _connectElapsed;
    private readonly byte[] _scratch = new byte[2048];
    private readonly IntPtr[] _batchStore = new IntPtr[64];
    private readonly List<Peer> _gone = new();

    private static SteamTransport? _current;

    /// <param name="hostId">0 = we host; otherwise the lobby owner we connect to.</param>
    public SteamTransport(TransportCallbacks cb, ulong lobby, ulong hostId)
    {
        _cb = cb;
        _lobby = lobby;
        _isHost = hostId == 0;
        _hostId = hostId;
        _msgs = SteamNative.SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        _sendNow = SendRaw;
        if (NetSim.Enabled)
        {
            _sim = new SimSendQueue();
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][netsim] Steam transport: simulating {NetSim.Describe()} on outgoing messages.");
        }
        _current = this;
        Status = _isHost ? NetStatus.Hosting : NetStatus.Connecting;
        Plugin.StaticLog.LogInfo(_isHost
            ? $"[SoulstoneCoop][steam] Steam host up on lobby {lobby} (mod {Plugin.HandshakeVersion}, up to {NetworkManager.MaxPlayers} players)."
            : $"[SoulstoneCoop][steam] Connecting to host {hostId} over Steam (mod {Plugin.HandshakeVersion}).");
        if (!_isHost) SendHello();
    }

    // ---------------- pump ----------------

    public void Poll()
    {
        if (_msgs == IntPtr.Zero) return;
        _sim?.Pump(_sendNow);
        Receive(ChannelMain);
        Receive(ChannelSequenced);

        float dt = Time.unscaledDeltaTime;
        if (!_isHost && _byId.Count == 0 && !WantsStop)
        {
            _connectElapsed += dt;
            _helloTimer += dt;
            if (_connectElapsed >= ConnectTimeout)
            {
                Fail(NetStatus.Idle, "O host não respondeu pela Steam");
                return;
            }
            if (_helloTimer >= HelloInterval) { _helloTimer = 0f; SendHello(); }
        }

        if (_byId.Count == 0) return;
        bool localLoading = Lobby.CoopSession.LocalLoading;
        _gone.Clear();
        foreach (var p in _byId.Values)
        {
            p.PingTimer += dt;
            if (p.PingTimer >= PingInterval)
            {
                p.PingTimer = 0f;
                var w = new NetDataWriter();
                w.Put((byte)T.Ping);
                w.Put(NowMs());
                Out(p.Id, w.Data, w.Length, SteamNative.SendUnreliableNoNagle, ChannelMain);
            }
            bool loading = localLoading || Lobby.CoopSession.ChangingScene(p.Slot);
            if (Time.unscaledTime - p.LastHeard > (loading ? LoadingSilenceTimeout : SilenceTimeout))
            {
                _gone.Add(p);
                continue;
            }
            p.SessionTimer += dt;
            if (p.SessionTimer >= 1f)
            {
                p.SessionTimer = 0f;
                int state = SessionState(p.Id);
                if (state == SteamNative.ConnClosedByPeer || state == SteamNative.ConnProblemDetectedLocally)
                {
                    _gone.Add(p);
                    LastGoneReason = state == SteamNative.ConnClosedByPeer ? "O parceiro fechou a conexão" : "Conexão perdida (problema na rede)";
                }
            }
        }
        foreach (var p in _gone)
        {
            PeerGone(p, LastGoneReason ?? "Conexão perdida (tempo esgotado)");
            LastGoneReason = null;
            if (WantsStop) return;
        }
    }

    private string? LastGoneReason;

    private void Receive(int channel)
    {
        fixed (IntPtr* batch = _batchStore)
        {
            for (int round = 0; round < 8; round++)
            {
                int n = SteamNative.SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(_msgs, channel, batch, _batchStore.Length);
                if (n <= 0) return;
                for (int i = 0; i < n; i++)
                {
                    IntPtr m = batch[i];
                    try
                    {
                        byte* data = *(byte**)((byte*)m + SteamNative.MsgData);
                        int size = *(int*)((byte*)m + SteamNative.MsgSize);
                        ulong from = SteamNative.IdentitySteamId((byte*)m + SteamNative.MsgPeer);
                        if (data != null && size > 0) OnMessage(from, channel, data, size);
                    }
                    catch (Exception ex)
                    {
                        Plugin.StaticLog.LogError($"[SoulstoneCoop][steam] Message handling failed: {ex}");
                    }
                    finally
                    {
                        SteamNative.SteamAPI_SteamNetworkingMessage_t_Release(m);
                    }
                }
                if (n < _batchStore.Length) return;
            }
        }
    }

    private void OnMessage(ulong from, int channel, byte* data, int size)
    {
        if (channel == ChannelSequenced)
        {
            if (!_byId.TryGetValue(from, out var sp) || size < 3) return;
            ushort seq = *(ushort*)data;
            if (sp.SeqInValid && (short)(seq - sp.SeqIn) <= 0) return; // older than what we already used
            sp.SeqIn = seq;
            sp.SeqInValid = true;
            Deliver(sp, data + 2, size - 2);
            return;
        }

        byte type = data[0];
        if (type >= (byte)T.Hello && type <= (byte)T.Pong)
        {
            var r = new NetDataReader(Copy(data, size));
            r.GetByte();
            OnTransportPacket(from, (T)type, r);
            return;
        }

        if (!_byId.TryGetValue(from, out var p)) return;
        Deliver(p, data, size);
    }

    private void Deliver(Peer p, byte* data, int size)
    {
        p.LastHeard = Time.unscaledTime;
        _cb.Data(new NetDataReader(Copy(data, size)), p.Slot);
    }

    private static byte[] Copy(byte* data, int size)
    {
        var bytes = new byte[size];
        fixed (byte* dst = bytes) Buffer.MemoryCopy(data, dst, size, size);
        return bytes;
    }

    // ---------------- handshake ----------------

    private void OnTransportPacket(ulong from, T type, NetDataReader r)
    {
        _byId.TryGetValue(from, out var p);
        switch (type)
        {
            case T.Hello when _isHost:
                OnHello(from, r);
                break;
            case T.Accept when !_isHost && from == _hostId && p == null:
            {
                p = AddPeer(from, Players.HostSlot);
                Status = NetStatus.Connected;
                LastError = null;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Connected to host {from} ({SteamLobby.FriendName(from)}) over Steam.");
                _cb.Connected(Players.HostSlot);
                break;
            }
            case T.Reject when !_isHost && from == _hostId:
            {
                string reason = SafeString(r) ?? "Conexão recusada pelo host";
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] Host refused: {reason}");
                if (p != null) { RemovePeer(p); _cb.Disconnected(Players.HostSlot); }
                Fail(NetStatus.Rejected, reason);
                break;
            }
            case T.Bye when p != null:
            {
                string reason = SafeString(r) ?? "";
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Peer {from} (slot {p.Slot}) left: '{reason}'.");
                PeerGone(p, _isHost ? "O parceiro saiu" : "O host desconectou");
                break;
            }
            case T.Ping when p != null:
            {
                p.LastHeard = Time.unscaledTime;
                var w = new NetDataWriter();
                w.Put((byte)T.Pong);
                w.Put(r.TryGetUInt(out var t) ? t : 0u);
                Out(from, w.Data, w.Length, SteamNative.SendUnreliableNoNagle, ChannelMain);
                break;
            }
            case T.Pong when p != null:
                p.LastHeard = Time.unscaledTime;
                if (r.TryGetUInt(out var sent)) p.PingMs = (int)Math.Min(NowMs() - sent, 9999u);
                break;
        }
    }

    private Peer AddPeer(ulong id, byte slot)
    {
        var p = new Peer { Id = id, Slot = slot, LastHeard = Time.unscaledTime };
        _byId[id] = p;
        _bySlot[slot] = p;
        _slots.MarkConnected(slot);
        return p;
    }

    private void RemovePeer(Peer p)
    {
        _byId.Remove(p.Id);
        _bySlot.Remove(p.Slot);
        _slots.Release(p.Slot);
    }

    private void OnHello(ulong from, NetDataReader r)
    {
        string key = "", version = "", proto = "";
        try
        {
            r.TryGetString(out key);
            if (!r.TryGetString(out version)) version = "";
            if (!r.TryGetString(out proto)) proto = "";
        }
        catch { }

        if (_byId.TryGetValue(from, out var known)) { SendAccept(from, known.Slot); return; } // our Accept was lost; Hello is resent every second

        string? refuse = null;
        if (key != ConnectionKey) refuse = "Chave de conexão desconhecida";
        else if (version != Plugin.HandshakeVersion)
            refuse = $"Versão diferente: host {Plugin.HandshakeVersion}, você {(version.Length > 0 ? version : "antiga")}";
        else if (proto != SteamLobby.Proto) refuse = "Protocolo de rede diferente";
        else if (!SteamLobby.IsMember(from)) refuse = "Entre pelo convite ou pelo código do lobby";

        byte slot = Players.NoSlot;
        if (refuse == null)
        {
            slot = _slots.Take();
            if (slot == Players.NoSlot)
                refuse = NetworkManager.MaxPlayers <= 2 ? "O host já tem um parceiro" : $"O host está cheio ({NetworkManager.MaxPlayers}/{NetworkManager.MaxPlayers})";
        }

        if (refuse != null)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] Refused {from} ({SteamLobby.FriendName(from)}): {refuse} (version '{version}').");
            if (key == ConnectionKey && version != Plugin.HandshakeVersion)
                LastError = $"Recusei um cliente com a versão {(version.Length > 0 ? version : "antiga")} (a minha é {Plugin.HandshakeVersion})";
            var w = new NetDataWriter();
            w.Put((byte)T.Reject);
            w.Put(refuse);
            Raw(from, w, SteamNative.SendReliable, ChannelMain);
            return;
        }

        AddPeer(from, slot);
        Status = NetStatus.Connected;
        LastError = null;
        SendAccept(from, slot);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Client {from} ({SteamLobby.FriendName(from)}) connected over Steam -> slot {slot} ({_byId.Count} client(s)).");
        _cb.Connected(slot);
    }

    private void SendHello()
    {
        var w = new NetDataWriter();
        w.Put((byte)T.Hello);
        w.Put(ConnectionKey);
        w.Put(Plugin.HandshakeVersion);
        w.Put(SteamLobby.Proto);
        Raw(_hostId, w, SteamNative.SendReliable, ChannelMain);
    }

    private void SendAccept(ulong to, byte slot)
    {
        var w = new NetDataWriter();
        w.Put((byte)T.Accept);
        w.Put(slot);
        Raw(to, w, SteamNative.SendReliable, ChannelMain);
    }

    private static string? SafeString(NetDataReader r)
    {
        try { return r.TryGetString(out var s) && s.Length > 0 ? s : null; }
        catch { return null; }
    }

    // ---------------- loss of a peer ----------------

    /// <summary>SteamLobby: a member left the lobby (or we were removed).</summary>
    internal void OnMemberLeft(ulong id)
    {
        if (_byId.TryGetValue(id, out var p)) PeerGone(p, _isHost ? "O parceiro saiu do lobby" : "O host saiu");
        else if (!_isHost && id == _hostId) Fail(NetStatus.Idle, "O host saiu do lobby");
    }

    private void PeerGone(Peer p, string reason)
    {
        if (!_byId.ContainsKey(p.Id)) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Peer {p.Id} (slot {p.Slot}) gone: {reason}.");
        RemovePeer(p);
        if (_isHost)
        {
            CloseSession(p.Id);
            if (_byId.Count == 0)
            {
                _sim?.Clear();
                Status = NetStatus.Hosting; // the lobby stays up for new players
            }
            _cb.Disconnected(p.Slot);
        }
        else
        {
            _sim?.Clear();
            LastError = reason;
            Status = NetStatus.Idle;
            WantsStop = true;
            _cb.Disconnected(p.Slot);
        }
    }

    private void Fail(NetStatus status, string error)
    {
        LastError = error;
        Status = status;
        WantsStop = true;
    }

    // ---------------- send ----------------

    public void Send(byte slot, byte[] data, int length, DeliveryMethod method)
    {
        if (length <= 0 || !_bySlot.TryGetValue(slot, out var p)) return;
        switch (method)
        {
            case DeliveryMethod.Sequenced:
            {
                if (length + 2 > _scratch.Length) { Out(p.Id, data, length, SteamNative.SendUnreliableNoNagle, ChannelMain); return; }
                p.SeqOut++;
                _scratch[0] = (byte)p.SeqOut;
                _scratch[1] = (byte)(p.SeqOut >> 8);
                Buffer.BlockCopy(data, 0, _scratch, 2, length);
                Out(p.Id, _scratch, length + 2, SteamNative.SendUnreliableNoNagle, ChannelSequenced);
                break;
            }
            case DeliveryMethod.Unreliable:
                Out(p.Id, data, length, SteamNative.SendUnreliableNoNagle, ChannelMain);
                break;
            default: // ReliableOrdered, ReliableUnordered, ReliableSequenced: Steam's reliable is ordered
                Out(p.Id, data, length, SteamNative.SendReliable, ChannelMain);
                break;
        }
    }

    /// <summary>Game traffic and pings: through the simulation queue when it is on (it copies the bytes).</summary>
    private void Out(ulong to, byte[] data, int length, int flags, int channel)
    {
        if (_sim != null) _sim.Enqueue(to, data, length, flags, channel, reliable: flags == SteamNative.SendReliable);
        else SendRaw(to, data, length, flags, channel);
    }

    private void Raw(ulong to, NetDataWriter w, int flags, int channel) => SendRaw(to, w.Data, w.Length, flags, channel);

    private int _sendErrors;

    private void SendRaw(ulong to, byte[] data, int length, int flags, int channel)
    {
        if (to == 0 || _msgs == IntPtr.Zero) return;
        byte* id = stackalloc byte[SteamNative.IdentitySize];
        SteamNative.MakeIdentity(id, to);
        int result;
        fixed (byte* p = data)
            result = SteamNative.SteamAPI_ISteamNetworkingMessages_SendMessageToUser(_msgs, id, p, (uint)length, flags, channel);
        if (result != 1 && _sendErrors++ < 10)
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] SendMessageToUser({length} B, ch {channel}) = EResult {result}.");
    }

    private int SessionState(ulong who)
    {
        byte* id = stackalloc byte[SteamNative.IdentitySize];
        SteamNative.MakeIdentity(id, who);
        return SteamNative.SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(_msgs, id, IntPtr.Zero, IntPtr.Zero);
    }

    private void CloseSession(ulong who)
    {
        if (who == 0) return;
        byte* id = stackalloc byte[SteamNative.IdentitySize];
        SteamNative.MakeIdentity(id, who);
        SteamNative.SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(_msgs, id);
    }

    // ---------------- stop ----------------

    public void Stop()
    {
        if (_byId.Count > 0)
        {
            _sim?.FlushReliable(_sendNow); // a simulated delay must not swallow our last reliable messages
            // No CloseSessionWithUser here: it would drop this Bye. The idle session expires by itself, and the
            // peers also see us leave the lobby.
            var w = new NetDataWriter();
            w.Put((byte)T.Bye);
            w.Put("stop");
            foreach (var p in _byId.Values) Raw(p.Id, w, SteamNative.SendReliable, ChannelMain);
        }
        _byId.Clear();
        _bySlot.Clear();
        _slots.Clear();
        if (_current == this) _current = null;
        SteamLobby.Leave();
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][steam] Steam transport stopped.");
    }

    private static uint NowMs() => (uint)(Time.realtimeSinceStartupAsDouble * 1000.0);

    // ---------------- Steam callbacks ----------------

    /// <summary>A lobby member messaged us before we messaged them: accept if they're in our lobby.</summary>
    internal static void OnSessionRequest(IntPtr p, int size)
    {
        ulong from = SteamNative.IdentitySteamId((byte*)p);
        bool member = SteamLobby.IsMember(from);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Session request from {from} ({SteamLobby.FriendName(from)}), lobby member={member}.");
        if (!member || _current == null) return;
        SteamNative.SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(_current._msgs, (byte*)p);
    }

    internal static void OnSessionFailed(IntPtr p, int size)
    {
        ulong who = SteamNative.IdentitySteamId((byte*)p);
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] Session with {who} failed.");
        var t = _current;
        if (t == null) return;
        if (t._byId.TryGetValue(who, out var peer)) t.PeerGone(peer, "Conexão perdida (sessão Steam caiu)");
        else if (!t._isHost && who == t._hostId) t.Fail(NetStatus.Idle, "Não deu pra falar com o host pela Steam");
    }

    // ---------------- loopback self-test ----------------

    private static int _selfTestState; // 0 = not run, 1 = waiting, 2 = done
    private static uint _stA, _stB;
    private static int _stFrames, _stGot;
    private static IntPtr _sockets;

    /// <summary>Once per game session: a local socket pair (no network) sends three messages through Steam's
    /// stack and reads them back, which checks our SteamNetworkingMessage_t offsets and Release.</summary>
    internal static void SelfTestTick()
    {
        if (_selfTestState == 2) return;
        try
        {
            if (_selfTestState == 0)
            {
                _sockets = SteamNative.SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
                uint a, b;
                if (_sockets == IntPtr.Zero || SteamNative.SteamAPI_ISteamNetworkingSockets_CreateSocketPair(_sockets, &a, &b, 0, null, null) == 0)
                {
                    Plugin.StaticLog.LogWarning("[SoulstoneCoop][steam] Self-test: CreateSocketPair failed.");
                    _selfTestState = 2;
                    return;
                }
                _stA = a; _stB = b;
                SelfTestSend(16, SteamNative.SendReliable, 1);
                SelfTestSend(1200, SteamNative.SendUnreliableNoNagle, 2);
                SelfTestSend(4000, SteamNative.SendReliable, 3);
                _selfTestState = 1;
                return;
            }

            IntPtr* batch = stackalloc IntPtr[8];
            int n = SteamNative.SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(_sockets, _stB, batch, 8);
            for (int i = 0; i < n; i++)
            {
                IntPtr m = batch[i];
                byte* data = *(byte**)((byte*)m + SteamNative.MsgData);
                int size = *(int*)((byte*)m + SteamNative.MsgSize);
                bool ok = data != null && size > 0 && data[0] == size % 251 && data[size - 1] == 0xAB;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Self-test: got {size} B, content {(ok ? "ok" : "WRONG")}.");
                if (ok) _stGot++;
                SteamNative.SteamAPI_SteamNetworkingMessage_t_Release(m);
            }

            if (_stGot >= 3 || ++_stFrames > 300)
            {
                Plugin.StaticLog.LogInfo(_stGot >= 3
                    ? "[SoulstoneCoop][steam] Self-test OK: 3/3 messages through the Steam stack."
                    : $"[SoulstoneCoop][steam] Self-test FAILED: {_stGot}/3 messages after {_stFrames} frames.");
                SteamNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(_sockets, _stA, 0, null, 0);
                SteamNative.SteamAPI_ISteamNetworkingSockets_CloseConnection(_sockets, _stB, 0, null, 0);
                _selfTestState = 2;
            }
        }
        catch (Exception ex)
        {
            _selfTestState = 2;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][steam] Self-test crashed: {ex.Message}");
        }
    }

    private static void SelfTestSend(int size, int flags, int tag)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)(size % 251);
        for (int i = 1; i < size - 1; i++) bytes[i] = (byte)(i * tag);
        bytes[size - 1] = 0xAB;
        long num;
        fixed (byte* p = bytes)
            SteamNative.SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(_sockets, _stA, p, (uint)size, flags, &num);
    }
}
