using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

public enum NetRole
{
    None,
    Host,
    Client
}

public static class NetworkManager
{
    public const int Port = 7777;
    private const string ConnectionKey = "soulstonecoop-v1";

    public static NetRole Role { get; private set; } = NetRole.None;
    public static NetManager? Manager { get; private set; }
    public static NetPeer? RemotePeer { get; private set; }

    /// <summary>Fired when a peer connects, on both host and client. Game code hooks this to spawn the ghost.</summary>
    public static event System.Action? PeerConnected;

    /// <summary>Fired when the peer disconnects.</summary>
    public static event System.Action? PeerDisconnected;

    private static EventBasedNetListener? _listener;

    public static void StartHost()
    {
        Stop();
        _listener = new EventBasedNetListener();
        Manager = new NetManager(_listener);
        Manager.Start(Port);

        _listener.ConnectionRequestEvent += request => request.AcceptIfKey(ConnectionKey);
        _listener.PeerConnectedEvent += peer =>
        {
            RemotePeer = peer;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Client connected: {peer.Address}");
            PeerConnected?.Invoke();
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Client disconnected: {info.Reason}");
            RemotePeer = null;
            PeerDisconnected?.Invoke();
        };
        _listener.NetworkReceiveEvent += OnReceive;

        Role = NetRole.Host;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Hosting on port {Port}");
    }

    public static void StartClient(string address)
    {
        Stop();
        _listener = new EventBasedNetListener();
        Manager = new NetManager(_listener);
        Manager.Start();

        _listener.PeerConnectedEvent += peer =>
        {
            RemotePeer = peer;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Connected to host {peer.Address}");
            PeerConnected?.Invoke();
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            // Host not up yet: keep retrying instead of giving up after LiteNetLib's ~5s attempt window.
            if (info.Reason == DisconnectReason.ConnectionFailed && RemotePeer == null)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Host not reachable yet, retrying {address}:{Port}...");
                Manager?.Connect(address, Port, ConnectionKey);
                return;
            }

            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Disconnected from host: {info.Reason}");
            RemotePeer = null;
            PeerDisconnected?.Invoke();
        };
        _listener.NetworkReceiveEvent += OnReceive;

        Manager.Connect(address, Port, ConnectionKey);
        Role = NetRole.Client;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Connecting to {address}:{Port}");
    }

    public static void Stop()
    {
        Manager?.Stop();
        Manager = null;
        RemotePeer = null;
        Role = NetRole.None;
    }

    // Call once per frame (e.g. from a MonoBehaviour Update or a Harmony UpdateAll postfix).
    public static void PollEvents()
    {
        Manager?.PollEvents();
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
