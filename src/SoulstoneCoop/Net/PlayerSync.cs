using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SoulstoneCoop.Net;

/// <summary>Transform snapshot for one player entity, sent at a fixed tick rate.</summary>
public struct PlayerTransformState
{
    public Vector3 Position;
    public float RotationY;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Position.x);
        writer.Put(Position.y);
        writer.Put(Position.z);
        writer.Put(RotationY);
    }

    public static PlayerTransformState Deserialize(NetDataReader reader)
    {
        return new PlayerTransformState
        {
            Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()),
            RotationY = reader.GetFloat()
        };
    }
}

/// <summary>
/// First networking milestone: mirror each side's own player transform onto the other side's
/// ghost clone. No real input simulation yet - the ghost is a dumb puppet that snaps to whatever
/// position/rotation the network says. Real per-frame player simulation (task #6/#7) builds on
/// top of this once the plumbing is proven.
/// </summary>
public static class PlayerSync
{
    private const float SendIntervalSeconds = 0.05f; // 20Hz
    private static float _timeSinceLastSend;

    public static int SentCount;
    public static int ReceivedCount;
    public static Vector3 LastReceivedPosition;

    public static void Tick(float deltaTime)
    {
        if (NetworkManager.RemotePeer == null) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;

        _timeSinceLastSend += deltaTime;
        if (_timeSinceLastSend < SendIntervalSeconds) return;
        _timeSinceLastSend = 0f;

        var t = local.gameObject.transform;
        var state = new PlayerTransformState { Position = t.position, RotationY = t.eulerAngles.y };

        var writer = new NetDataWriter();
        writer.Put((byte)PacketType.PlayerState);
        state.Serialize(writer);
        NetworkManager.Send(writer, DeliveryMethod.Sequenced);
        SentCount++;
    }

    public static void OnPlayerStateReceived(NetPacketReader reader)
    {
        var state = PlayerTransformState.Deserialize(reader);
        ReceivedCount++;
        LastReceivedPosition = state.Position;

        var ghost = GameState.RemoteGhostObject;
        if (ghost == null) return;

        var t = ghost.transform;
        t.position = state.Position;
        t.eulerAngles = new Vector3(0f, state.RotationY, 0f);
    }
}
