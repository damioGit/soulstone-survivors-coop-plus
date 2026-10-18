using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

/// <summary>
/// Phase 7: one way of reaching the partner. NetworkManager is the facade the rest of the mod talks to; behind it
/// sits either LiteNetTransport (direct IP, local tests) or SteamTransport (Steam P2P relay, invites). Both carry
/// the same payloads: the first byte is the PacketType, read by PacketRouter.
/// </summary>
public interface ITransport
{
    /// <summary>"IP" or "Steam", for the panel and the logs.</summary>
    string Kind { get; }

    /// <summary>The handshake finished and the partner is there.</summary>
    bool HasPeer { get; }

    NetStatus Status { get; }

    /// <summary>Last connection problem, in Portuguese, for the panel (null = none).</summary>
    string? LastError { get; }

    /// <summary>Round trip to the partner in ms, -1 if unknown.</summary>
    int PingMs { get; }

    /// <summary>Set from inside a transport callback when it wants to be torn down; NetworkManager stops it right
    /// after Poll (stopping from inside a LiteNetLib event is not safe).</summary>
    bool WantsStop { get; }

    /// <summary>Pump events once per frame.</summary>
    void Poll();

    /// <summary>Send one payload. The data is copied before returning.</summary>
    void Send(byte[] data, int length, DeliveryMethod method);

    /// <summary>Close everything. Must not invoke the disconnected callback (NetworkManager handles that).</summary>
    void Stop();
}

/// <summary>What a transport reports back to NetworkManager.</summary>
public sealed class TransportCallbacks
{
    public Action Connected = () => { };
    public Action Disconnected = () => { };
    public Action<NetDataReader> Data = _ => { };
}
