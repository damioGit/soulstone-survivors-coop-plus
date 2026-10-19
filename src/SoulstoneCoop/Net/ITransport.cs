using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

/// <summary>
/// Phase 7: one way of reaching the other players. NetworkManager is the facade the rest of the mod talks to; behind
/// it sits either LiteNetTransport (direct IP, local tests) or SteamTransport (Steam P2P relay, invites). Both carry
/// the same payloads: the first byte is the PacketType, read by PacketRouter.
/// Phase 9: a host transport holds up to 7 clients, each on a slot (1-7) it hands out when it accepts them; a client
/// transport has one peer, the host, on slot 0.
/// </summary>
public interface ITransport
{
    /// <summary>"IP" or "Steam", for the panel and the logs.</summary>
    string Kind { get; }

    /// <summary>At least one peer finished the handshake (client: the host is there).</summary>
    bool HasPeer { get; }

    /// <summary>Slots of the peers that finished the handshake (client: just 0).</summary>
    IReadOnlyList<byte> PeerSlots { get; }

    NetStatus Status { get; }

    /// <summary>Last connection problem, in Portuguese, for the panel (null = none).</summary>
    string? LastError { get; }

    /// <summary>Round trip to that peer in ms, -1 if unknown.</summary>
    int PingMs(byte slot);

    /// <summary>Phase 8: seconds since anything arrived from that peer (0 if it isn't there). Feeds the
    /// "conexão instável" warning, well before the transport gives up.</summary>
    float SilenceSeconds(byte slot);

    /// <summary>Set from inside a transport callback when it wants to be torn down; NetworkManager stops it right
    /// after Poll (stopping from inside a LiteNetLib event is not safe).</summary>
    bool WantsStop { get; }

    /// <summary>Pump events once per frame.</summary>
    void Poll();

    /// <summary>Send one payload to one peer. The data is copied before returning.</summary>
    void Send(byte slot, byte[] data, int length, DeliveryMethod method);

    /// <summary>Close everything. Must not invoke the disconnected callbacks (NetworkManager handles that).</summary>
    void Stop();
}

/// <summary>What a transport reports back to NetworkManager, with the slot of the peer concerned.</summary>
public sealed class TransportCallbacks
{
    public Action<byte> Connected = _ => { };
    public Action<byte> Disconnected = _ => { };
    public Action<NetDataReader, byte> Data = (_, _) => { };
}

/// <summary>Hands out client slots on the host: the lowest free one in 1..MaxClients.</summary>
internal sealed class SlotTable
{
    private readonly bool[] _used = new bool[Players.MaxSlots];
    private readonly List<byte> _connected = new();

    public IReadOnlyList<byte> Connected => _connected;

    /// <summary>A free slot, or NoSlot when the session is full (NetworkManager.MaxClients).</summary>
    public byte Take()
    {
        int max = NetworkManager.MaxClients;
        for (byte s = 1; s <= max && s < Players.MaxSlots; s++)
        {
            if (_used[s]) continue;
            _used[s] = true;
            return s;
        }
        return Players.NoSlot;
    }

    public bool HasRoom
    {
        get
        {
            int max = NetworkManager.MaxClients;
            for (int s = 1; s <= max && s < Players.MaxSlots; s++) if (!_used[s]) return true;
            return false;
        }
    }

    public void MarkConnected(byte slot)
    {
        if (!_connected.Contains(slot)) { _connected.Add(slot); _connected.Sort(); }
    }

    public void Release(byte slot)
    {
        if (slot >= Players.MaxSlots) return;
        _used[slot] = false;
        _connected.Remove(slot);
    }

    public void Clear()
    {
        Array.Clear(_used);
        _connected.Clear();
    }
}
