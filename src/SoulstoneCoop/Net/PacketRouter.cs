using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

public enum PacketType : byte
{
    PlayerInput = 1,   // client -> host: movement/aim/skill input for this frame
    PlayerState = 2,   // host -> client: authoritative transform/anim state for the remote player entity
    WorldSnapshot = 3, // host -> client: batched enemy/drop state
    LevelUpChoice = 4, // client -> host: chosen skill on level up
    Revive = 5,        // host -> client: target u8 (1 = client) is revived (phase 4A, ReliableOrdered)
    PauseState = 6,    // either way: sender's LOCAL pause reason (none / pause / level-up)
    EnemySpawn = 7,    // host -> client: enemy netId + prefab key + initial state (ReliableOrdered)
    EnemyDespawn = 8,  // host -> client: enemy recycled without dying (ReliableOrdered)
    EnemyDied = 9,     // host -> client: enemy killed, play the death (ReliableOrdered)
    EnemySnapshot = 10, // host -> client: positions/yaw/HP of live enemies, sliced (Unreliable)
    ClientDamage = 11, // client -> host: damage the client dealt to puppets, summed per netId per tick (ReliableUnordered)
    DamageToClient = 12, // host -> client: a hit on the client's proxy (healthChange f32, attacker netId u16) (ReliableOrdered)
    ClientState = 13,  // client -> host: local player dead flag + hp/maxHp/armor/damageReduction/block, every 0.5 s (ReliableOrdered)
    SkillCast = 14,    // either way: the sender's local player cast a skill, replayed visual-only on the RemoteCaster (ReliableOrdered)
    SkillChannelEnd = 15, // either way: a channel started by SkillCast stopped (ReliableOrdered, same channel as SkillCast)
    LootDrop = 16,     // host -> client: one LootUtil.ProcessLoot call's arguments, replayed locally (ReliableOrdered, before EnemyDied)
    XpShare = 17,      // either way: raw XP the sender's player collected (team XP: the receiver adds it too) (ReliableOrdered)
    CoopLife = 18,     // host -> client: hostDown u8 | clientDown u8 | revive progress f32, on change and 5 Hz while someone is down (ReliableOrdered)
    RunEnd = 19,       // host -> client: the run ends (Defeat / Victory / Surrender / PortalHome) for both (ReliableOrdered)
    RunEndRequest = 20, // client -> host: the client surrendered or took the Home portal (ReliableOrdered)
    EnemyCast = 21,    // host -> client: a boss/elite/special enemy cast, replayed visual-only on its puppet (phase 4D, ReliableUnordered)
    // 22-29 reserved for phase 4D
    Hello = 30,        // either way: name, character, weapon, skins, mod version (phase 6, on connect and when it changes) (ReliableOrdered)
    LobbyState = 31,   // either way: sender is in the menu / loading a run / in a run (phase 6, on change) (ReliableOrdered)
    RunStart = 32,     // host -> client: scene, mode, mastery tier/level, Survivors affixes: start the same run (phase 6) (ReliableOrdered)
    RunStartRefused = 33, // client -> host: could not follow RunStart, with the reason (phase 6) (ReliableOrdered)
    // 50-59: phase 5
    PartnerHealth = 50, // either way: sender's hp f32 | maxHp f32 | flags u8 (1 down, 2 dead), on change at <= 5 Hz + every 1 s (ReliableOrdered)
}

public static class PacketRouter
{
    public static void Handle(NetDataReader reader)
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
                Life.CoopLife.OnRevive(reader);
                break;
            case PacketType.CoopLife:
                Life.CoopLife.OnLife(reader);
                break;
            case PacketType.RunEnd:
                Life.CoopLife.OnRunEnd(reader);
                break;
            case PacketType.RunEndRequest:
                Life.CoopLife.OnRunEndRequest(reader);
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
            case PacketType.EnemyCast:
                Enemies.EnemyCasts.OnEnemyCast(reader);
                break;
            case PacketType.EnemySnapshot:
                Enemies.ClientPuppets.OnSnapshot(reader, reader.RawDataSize);
                break;
            case PacketType.ClientDamage:
                Enemies.HostEnemySync.OnClientDamage(reader);
                break;
            case PacketType.DamageToClient:
                Enemies.ClientIncomingDamage.OnDamageToClient(reader);
                break;
            case PacketType.ClientState:
                Enemies.RemoteProxy.OnClientState(reader);
                break;
            case PacketType.SkillCast:
                Skills.CastSync.OnSkillCast(reader);
                break;
            case PacketType.SkillChannelEnd:
                Skills.CastSync.OnSkillChannelEnd(reader);
                break;
            case PacketType.LootDrop:
                Loot.LootSync.OnLootDrop(reader);
                break;
            case PacketType.XpShare:
                Loot.LootSync.OnXpShare(reader);
                break;
            case PacketType.Hello:
                Lobby.CoopSession.OnHello(reader);
                break;
            case PacketType.LobbyState:
                Lobby.CoopSession.OnLobbyState(reader);
                break;
            case PacketType.RunStart:
                Lobby.RunSync.OnRunStart(reader);
                break;
            case PacketType.RunStartRefused:
                Lobby.RunSync.OnRunStartRefused(reader);
                break;
            case PacketType.PartnerHealth:
                Life.PartnerHealth.OnPacket(reader);
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
