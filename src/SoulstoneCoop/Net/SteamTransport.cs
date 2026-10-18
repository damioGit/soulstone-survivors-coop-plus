using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Steam;
using UnityEngine;

namespace SoulstoneCoop.Net;

/// <summary>
/// Phase 7: co-op over Steam P2P (ISteamNetworkingMessages, Steam Datagram Relay: no port forwarding, no server
/// of ours). The partner is addressed by SteamID, taken from the lobby. Channel 0 carries the normal payloads
/// (reliable = ordered, unreliable = no Nagle); channel 1 carries LiteNetLib's Sequenced traffic (player state)
/// with a u16 sequence in front so stale packets are dropped. Packets 40-45 are this transport's own handshake
/// and keepalive and never reach PacketRouter. Docs: docs/fase7-investigacao.md, section 4.
/// </summary>
public sealed unsafe class SteamTransport : ITransport
{
    private enum T : byte
    {
        Hello = 40,   // client -> host: key, mod version, transport protocol
        Accept = 41,  // host -> client
        Reject = 42,  // host -> client: reason
        Bye = 43,     // either: reason (clean leave)
        Ping = 44,    // either: u32 local ms
        Pong = 45,    // either: u32 echoed ms
    }

    private const string ConnectionKey = "soulstonecoop-v1";
    private const int ChannelMain = 0, ChannelSequenced = 1;
    private const float HelloInterval = 1f, ConnectTimeout = 30f, PingInterval = 1f, SilenceTimeout = 15f;

    public string Kind => "Steam";
    public bool HasPeer => _connected;
    public NetStatus Status { get; private set; }
    public string? LastError { get; private set; }
    public int PingMs { get; private set; } = -1;
    public bool WantsStop { get; private set; }

    public ulong PeerId => _peer;

    private readonly TransportCallbacks _cb;
    private readonly bool _isHost;
    private readonly ulong _lobby;
    private readonly IntPtr _msgs;
    private ulong _peer; // client: the host from the start; host: set by the accepted Hello
    private bool _connected;
    private float _helloTimer, _connectElapsed, _pingTimer, _lastHeard, _sessionTimer;
    private ushort _seqOut;
    private ushort _seqIn;
    private bool _seqInValid;
    private readonly byte[] _scratch = new byte[2048];
    private readonly IntPtr[] _batchStore = new IntPtr[64];

    private static SteamTransport? _current;

    /// <param name="hostId">0 = we host; otherwise the lobby owner we connect to.</param>
    public SteamTransport(TransportCallbacks cb, ulong lobby, ulong hostId)
    {
        _cb = cb;
        _lobby = lobby;
        _isHost = hostId == 0;
        _peer = hostId;
        _msgs = SteamNative.SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        _current = this;
        Status = _isHost ? NetStatus.Hosting : NetStatus.Connecting;
        _lastHeard = Time.unscaledTime;
        Plugin.StaticLog.LogInfo(_isHost
            ? $"[SoulstoneCoop][steam] Steam host up on lobby {lobby} (mod {Plugin.HandshakeVersion})."
            : $"[SoulstoneCoop][steam] Connecting to host {hostId} over Steam (mod {Plugin.HandshakeVersion}).");
        if (!_isHost) SendHello();
    }

    // ---------------- pump ----------------

    public void Poll()
    {
        if (_msgs == IntPtr.Zero) return;
        Receive(ChannelMain);
        Receive(ChannelSequenced);

        float dt = Time.unscaledDeltaTime;
        if (!_isHost && !_connected && !WantsStop)
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

        if (_connected)
        {
            _pingTimer += dt;
            if (_pingTimer >= PingInterval)
            {
                _pingTimer = 0f;
                var w = new NetDataWriter();
                w.Put((byte)T.Ping);
                w.Put(NowMs());
                Raw(_peer, w, SteamNative.SendUnreliableNoNagle, ChannelMain);
            }
            if (Time.unscaledTime - _lastHeard > SilenceTimeout)
            {
                PeerGone("Conexão perdida (tempo esgotado)");
                return;
            }
            _sessionTimer += dt;
            if (_sessionTimer >= 1f)
            {
                _sessionTimer = 0f;
                int state = SessionState(_peer);
                if (state == SteamNative.ConnClosedByPeer || state == SteamNative.ConnProblemDetectedLocally)
                    PeerGone(state == SteamNative.ConnClosedByPeer ? "O parceiro fechou a conexão" : "Conexão perdida (problema na rede)");
            }
        }
    }

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
            if (!_connected || from != _peer || size < 3) return;
            ushort seq = *(ushort*)data;
            if (_seqInValid && (short)(seq - _seqIn) <= 0) return; // older than what we already used
            _seqIn = seq;
            _seqInValid = true;
            Deliver(data + 2, size - 2);
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

        if (!_connected || from != _peer) return;
        _lastHeard = Time.unscaledTime;
        Deliver(data, size);
    }

    private void Deliver(byte* data, int size)
    {
        _lastHeard = Time.unscaledTime;
        _cb.Data(new NetDataReader(Copy(data, size)));
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
        switch (type)
        {
            case T.Hello when _isHost:
                OnHello(from, r);
                break;
            case T.Accept when !_isHost && from == _peer && !_connected:
                _connected = true;
                Status = NetStatus.Connected;
                LastError = null;
                _lastHeard = Time.unscaledTime;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Connected to host {from} ({SteamLobby.FriendName(from)}) over Steam.");
                _cb.Connected();
                break;
            case T.Reject when !_isHost && from == _peer:
            {
                string reason = SafeString(r) ?? "Conexão recusada pelo host";
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] Host refused: {reason}");
                Fail(NetStatus.Rejected, reason);
                break;
            }
            case T.Bye when from == _peer && _connected:
            {
                string reason = SafeString(r) ?? "";
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Partner left: '{reason}'.");
                PeerGone(_isHost ? "O parceiro saiu" : "O host desconectou");
                break;
            }
            case T.Ping when from == _peer && _connected:
            {
                _lastHeard = Time.unscaledTime;
                var w = new NetDataWriter();
                w.Put((byte)T.Pong);
                w.Put(r.TryGetUInt(out var t) ? t : 0u);
                Raw(_peer, w, SteamNative.SendUnreliableNoNagle, ChannelMain);
                break;
            }
            case T.Pong when from == _peer && _connected:
                _lastHeard = Time.unscaledTime;
                if (r.TryGetUInt(out var sent)) PingMs = (int)Math.Min(NowMs() - sent, 9999u);
                break;
        }
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

        if (_connected && from == _peer) { SendAccept(from); return; } // our Accept was lost; Hello is resent every second

        string? refuse = null;
        if (key != ConnectionKey) refuse = "Chave de conexão desconhecida";
        else if (version != Plugin.HandshakeVersion)
            refuse = $"Versão diferente: host {Plugin.HandshakeVersion}, você {(version.Length > 0 ? version : "antiga")}";
        else if (proto != SteamLobby.Proto) refuse = "Protocolo de rede diferente";
        else if (_connected) refuse = "O host já tem um parceiro";
        else if (!SteamLobby.IsMember(from)) refuse = "Entre pelo convite ou pelo código do lobby";

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

        _peer = from;
        _connected = true;
        _seqInValid = false;
        Status = NetStatus.Connected;
        LastError = null;
        _lastHeard = Time.unscaledTime;
        SendAccept(from);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Client {from} ({SteamLobby.FriendName(from)}) connected over Steam.");
        _cb.Connected();
    }

    private void SendHello()
    {
        var w = new NetDataWriter();
        w.Put((byte)T.Hello);
        w.Put(ConnectionKey);
        w.Put(Plugin.HandshakeVersion);
        w.Put(SteamLobby.Proto);
        Raw(_peer, w, SteamNative.SendReliable, ChannelMain);
    }

    private void SendAccept(ulong to)
    {
        var w = new NetDataWriter();
        w.Put((byte)T.Accept);
        Raw(to, w, SteamNative.SendReliable, ChannelMain);
    }

    private static string? SafeString(NetDataReader r)
    {
        try { return r.TryGetString(out var s) && s.Length > 0 ? s : null; }
        catch { return null; }
    }

    // ---------------- loss of the partner ----------------

    /// <summary>SteamLobby: a member left the lobby (or we were removed).</summary>
    internal void OnMemberLeft(ulong id)
    {
        if (_isHost && id == _peer && _connected) PeerGone("O parceiro saiu do lobby");
        else if (!_isHost && id == _peer) { if (_connected) PeerGone("O host saiu"); else Fail(NetStatus.Idle, "O host saiu do lobby"); }
    }

    private void PeerGone(string reason)
    {
        if (!_connected) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Partner {_peer} gone: {reason}.");
        _connected = false;
        PingMs = -1;
        if (_isHost)
        {
            CloseSession(_peer);
            _peer = 0;
            Status = NetStatus.Hosting; // the lobby stays up for a new partner
            _cb.Disconnected();
        }
        else
        {
            LastError = reason;
            Status = NetStatus.Idle;
            WantsStop = true;
            _cb.Disconnected();
        }
    }

    private void Fail(NetStatus status, string error)
    {
        LastError = error;
        Status = status;
        WantsStop = true;
    }

    // ---------------- send ----------------

    public void Send(byte[] data, int length, DeliveryMethod method)
    {
        if (!_connected || length <= 0) return;
        switch (method)
        {
            case DeliveryMethod.Sequenced:
            {
                if (length + 2 > _scratch.Length) { SendRaw(_peer, data, length, SteamNative.SendUnreliableNoNagle, ChannelMain); return; }
                _seqOut++;
                _scratch[0] = (byte)_seqOut;
                _scratch[1] = (byte)(_seqOut >> 8);
                Buffer.BlockCopy(data, 0, _scratch, 2, length);
                SendRaw(_peer, _scratch, length + 2, SteamNative.SendUnreliableNoNagle, ChannelSequenced);
                break;
            }
            case DeliveryMethod.Unreliable:
                SendRaw(_peer, data, length, SteamNative.SendUnreliableNoNagle, ChannelMain);
                break;
            default: // ReliableOrdered, ReliableUnordered, ReliableSequenced: Steam's reliable is ordered
                SendRaw(_peer, data, length, SteamNative.SendReliable, ChannelMain);
                break;
        }
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
        if (_connected)
        {
            // No CloseSessionWithUser here: it would drop this Bye. The idle session expires by itself, and the
            // partner also sees us leave the lobby.
            var w = new NetDataWriter();
            w.Put((byte)T.Bye);
            w.Put("stop");
            Raw(_peer, w, SteamNative.SendReliable, ChannelMain);
        }
        _connected = false;
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
        if (t != null && who == t._peer)
        {
            if (t._connected) t.PeerGone("Conexão perdida (sessão Steam caiu)");
            else if (!t._isHost) t.Fail(NetStatus.Idle, "Não deu pra falar com o host pela Steam");
        }
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
