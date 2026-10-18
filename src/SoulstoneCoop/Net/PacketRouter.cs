using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

public enum PacketType : byte
{
    PlayerInput = 1,   // client -> host: movement/aim/skill input for this frame
    PlayerState = 2,   // host -> client: authoritative transform/anim state for the remote player entity
    WorldSnapshot = 3, // host -> client: batched enemy/drop state
    LevelUpChoice = 4, // client -> host: chosen skill on level up
    Revive = 5,        // either way: a down player was revived
    PauseState = 6,    // either way: sender's LOCAL pause reason (none / pause / level-up)
    EnemySpawn = 7,    // host -> client: enemy netId + prefab key + initial state (ReliableOrdered)
    EnemyDespawn = 8,  // host -> client: enemy recycled without dying (ReliableOrdered)
    EnemyDied = 9,     // host -> client: enemy killed, play the death (ReliableOrdered)
    EnemySnapshot = 10, // host -> client: positions/yaw/HP of live enemies, sliced (Unreliable)
    ClientDamage = 11, // client -> host: damage the client dealt to puppets, summed per netId per tick (ReliableUnordered)
}

public static class PacketRouter
{
    public static void Handle(NetPeer peer, NetPacketReader reader)
    {
        if (reader.AvailableBytes < 1) return;
        var type = (PacketType)reader.GetByte();

        switch (type)
        {
            case PacketType.PlayerInput:
                // Task #6/#7: real input-driven simulation, not wired yet (current milestone mirrors raw transforms).
                break;
            case PacketType.PlayerState:
                PlayerSync.OnPlayerStateReceived(reader);
                break;
            case PacketType.WorldSnapshot:
                // Task #6: host-authoritative world sync, not wired yet.
                break;
            case PacketType.LevelUpChoice:
                // Task #7: co-op level-up patches, not wired yet.
                break;
            case PacketType.Revive:
                // Task #7: revive mechanic, not wired yet.
                break;
            case PacketType.EnemySpawn:
                Enemies.ClientPuppets.OnSpawn(reader);
                break;
            case PacketType.EnemyDespawn:
                Enemies.ClientPuppets.OnDespawn(reader);
                break;
            case PacketType.EnemyDied:
                Enemies.ClientPuppets.OnDied(reader);
                break;
            case PacketType.EnemySnapshot:
                Enemies.ClientPuppets.OnSnapshot(reader, reader.RawDataSize);
                break;
            case PacketType.ClientDamage:
                Enemies.HostEnemySync.OnClientDamage(reader);
                break;
            case PacketType.PauseState:
                PauseSync.OnPauseStateReceived(reader);
                break;
            default:
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Unknown packet type {type}");
                break;
        }
    }
}
