using LiteNetLib;
using LiteNetLib.Utils;

// Minimal stand-in for a real game client: connects to the SoulstoneCoop host, listens for the
// host's real player position (broadcast every tick by PlayerSync), then orbits around that
// point and reports its own fake position back. Lets us prove the network + ghost-mirroring
// plumbing works inside the real game without needing a second licensed game instance.

const byte PlayerStatePacketType = 2; // must match SoulstoneCoop.Net.PacketType.PlayerState
const string ConnectionKey = "soulstonecoop-v1"; // must match SoulstoneCoop.Net.NetworkManager.ConnectionKey
const int Port = 7777;
const float OrbitRadius = 2.5f;
const float OrbitSpeed = 1.0f; // radians/sec

float[]? hostOrigin = null; // x, y, z
var listener = new EventBasedNetListener();
var manager = new NetManager(listener);
manager.Start();

listener.PeerConnectedEvent += peer =>
{
    Console.WriteLine($"[TestClient] Connected to host {peer.Address}");
};
listener.PeerDisconnectedEvent += (peer, info) =>
{
    Console.WriteLine($"[TestClient] Disconnected: {info.Reason}");
};
listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
{
    byte type = reader.GetByte();
    if (type == PlayerStatePacketType)
    {
        float x = reader.GetFloat();
        float y = reader.GetFloat();
        float z = reader.GetFloat();
        reader.GetFloat(); // rotationY, unused here
        hostOrigin = new[] { x, y, z };
    }
    reader.Recycle();
};

Console.WriteLine($"[TestClient] Connecting to 127.0.0.1:{Port} ...");
manager.Connect("127.0.0.1", Port, ConnectionKey);

var sw = System.Diagnostics.Stopwatch.StartNew();
float lastSend = 0f;

while (true)
{
    manager.PollEvents();

    float t = (float)sw.Elapsed.TotalSeconds;
    if (hostOrigin != null && t - lastSend > 0.05f) // 20Hz
    {
        lastSend = t;
        float angle = t * OrbitSpeed;
        float x = hostOrigin[0] + MathF.Cos(angle) * OrbitRadius;
        float z = hostOrigin[2] + MathF.Sin(angle) * OrbitRadius;
        float y = hostOrigin[1];

        var writer = new NetDataWriter();
        writer.Put(PlayerStatePacketType);
        writer.Put(x);
        writer.Put(y);
        writer.Put(z);
        writer.Put(angle * Mathf_Rad2Deg());

        manager.FirstPeer?.Send(writer, DeliveryMethod.Sequenced);
    }

    Thread.Sleep(10);
}

static float Mathf_Rad2Deg() => 57.29578f;
