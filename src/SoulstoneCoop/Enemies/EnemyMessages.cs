using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>Host -> client: an enemy appeared. ReliableOrdered.</summary>
public struct EnemySpawnMsg
{
    public ushort NetId;
    public string Key;      // see EnemyPrefabKeys
    public sbyte Tier;      // EntityByEnemyPrefabData.Tier, -1 when unknown
    public bool IsElite;
    public Vector3 Position;
    public float Yaw;
    public float MaxHealth;
    public float HealthPercent;

    // Phase 4D, optional boss block (HasBoss): what SpawnBossRoutine did on the host before Entity.Setup.
    public bool HasBoss;
    public sbyte BossTier, BossSkillsTier, BossLootTier; // -1 = SetupWithTier wasn't seen
    public float BossMultMaxHealth;                       // MultiplicativeStats.MaxHealth (HealthMultiplier applied), 0 = unknown
    public sbyte HostPendingBosses;                       // BossSpawnProcessor._pendingBosses on the host at spawn time
    public bool HostHasPendingBosses;

    public void Write(NetDataWriter w)
    {
        w.Put((byte)PacketType.EnemySpawn);
        w.Put(NetId);
        w.Put(Key);
        w.Put(Tier);
        w.Put(IsElite);
        w.Put(Position.x); w.Put(Position.y); w.Put(Position.z);
        w.Put(Yaw);
        w.Put(MaxHealth);
        w.Put(HealthPercent);
        w.Put(HasBoss);
        if (!HasBoss) return;
        w.Put(BossTier); w.Put(BossSkillsTier); w.Put(BossLootTier);
        w.Put(BossMultMaxHealth);
        w.Put(HostPendingBosses);
        w.Put(HostHasPendingBosses);
    }

    public static EnemySpawnMsg Read(NetDataReader r)
    {
        var m = new EnemySpawnMsg
        {
            NetId = r.GetUShort(),
            Key = r.GetString(),
            Tier = r.GetSByte(),
            IsElite = r.GetBool(),
            Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
            Yaw = r.GetFloat(),
            MaxHealth = r.GetFloat(),
            HealthPercent = r.GetFloat(),
            HasBoss = r.GetBool(),
        };
        if (m.HasBoss)
        {
            m.BossTier = r.GetSByte(); m.BossSkillsTier = r.GetSByte(); m.BossLootTier = r.GetSByte();
            m.BossMultMaxHealth = r.GetFloat();
            m.HostPendingBosses = r.GetSByte();
            m.HostHasPendingBosses = r.GetBool();
        }
        return m;
    }
}

/// <summary>
/// Snapshot slice layout (Unreliable; every slice is self-contained, so a lost or reordered slice only
/// costs the entries in it, and per-puppet sequence checks drop stale ones):
///   type u8 | seq u16 | base x,y,z f32 | count u8 | count * entry
///   entry: netId u16 | dx i16 (1/32 u) | dz i16 (1/32 u) | dy i16 (1/64 u) | yaw u8 | hp u16 (0..65535 = 0..100%)
/// (hp was u8 until phase 4D: 1/255 of a 470k-HP boss is ~1.8k HP, visible as different numbers on the two boss bars)
/// Offsets are relative to the base (the host player), so the range is about +-1000 u around it; the
/// game recycles enemies beyond 35 u anyway.
/// </summary>
public static class EnemySnapshotFormat
{
    public const int HeaderBytes = 1 + 2 + 12 + 1;
    public const int EntryBytes = 11;
    public const int MaxPacketBytes = 1200;
    public const int MaxEntriesPerSlice = (MaxPacketBytes - HeaderBytes) / EntryBytes; // 107

    private const float XzScale = 32f;
    private const float YScale = 64f;

    public static void WriteHeader(NetDataWriter w, ushort seq, Vector3 basePos, byte count)
    {
        w.Put((byte)PacketType.EnemySnapshot);
        w.Put(seq);
        w.Put(basePos.x); w.Put(basePos.y); w.Put(basePos.z);
        w.Put(count);
    }

    public static void WriteEntry(NetDataWriter w, ushort netId, Vector3 pos, Vector3 basePos, float yaw, float hpPercent)
    {
        w.Put(netId);
        w.Put(Quant(pos.x - basePos.x, XzScale));
        w.Put(Quant(pos.z - basePos.z, XzScale));
        w.Put(Quant(pos.y - basePos.y, YScale));
        w.Put((byte)(Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 360f * 256f) & 0xFF));
        w.Put((ushort)Mathf.Clamp(Mathf.RoundToInt(hpPercent * 65535f), 0, 65535));
    }

    public static void ReadEntry(NetDataReader r, Vector3 basePos, out ushort netId, out Vector3 pos, out float yaw, out float hpPercent)
    {
        netId = r.GetUShort();
        float dx = r.GetShort() / XzScale;
        float dz = r.GetShort() / XzScale;
        float dy = r.GetShort() / YScale;
        pos = new Vector3(basePos.x + dx, basePos.y + dy, basePos.z + dz);
        yaw = r.GetByte() * (360f / 256f);
        hpPercent = r.GetUShort() / 65535f;
    }

    private static short Quant(float v, float scale) =>
        (short)Mathf.Clamp(Mathf.RoundToInt(v * scale), short.MinValue, short.MaxValue);
}
