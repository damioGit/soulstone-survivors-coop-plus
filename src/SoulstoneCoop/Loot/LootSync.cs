using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace SoulstoneCoop.Loot;

/// <summary>
/// Phase 4B: XP and loot for the client. Every enemy dies on the host, so LootUtil.ProcessLoot only runs there.
/// The host sends each ProcessLoot call's arguments (LootDrop = 16) and the client runs the same call locally:
/// XP orbs, prestige, soulstones, item rolls and power-ups appear in its own world, with its own runes/stats
/// multipliers, and its player collects them (XpDropper and the collectables use PlayerEntities[0] = local).
/// ProcessLoot already hands items/prestige/minor soulstones to every Players-team entity, so "each player gets
/// their own" is the game's native N-player behaviour. See docs/fase4b-investigacao.md.
/// </summary>
public static class LootSync
{
    private const float TelemetryInterval = 5f;
    private const int MaxReplaysPerFrame = 32;

    /// <summary>Client: set while we run a ProcessLoot received from the host.</summary>
    internal static bool ReplayingNetworkLoot;

    /// <summary>Host: set during SoulstoneItem.OnCollect. Its RunBreakdown step 0 runs synchronously inside and
    /// spawns minerals from the COLLECTOR's rune - personal loot, the client gets its own from its own copy.</summary>
    internal static bool InPersonalLoot;

    private struct ItemMsg { public int Id; public float Chance; public int Min, Max; }
    private struct PowerUpMsg { public int Level; public int Source; public bool Lock, Banish; }

    private sealed class LootMsg
    {
        public ushort NetId;
        public Vector3 Pos;
        public int Xp;
        public float Prestige;
        public bool Instant;
        public int Interval;
        public int[]? Currencies; // minor, corrupted, vile, wicked, hateful, rogue
        public readonly List<ItemMsg> Items = new();
        public readonly List<PowerUpMsg> PowerUps = new();
    }

    private static readonly List<LootMsg> Pending = new();
    private static bool _itemListBroken;

    // Telemetry window
    private static float _telemetryTimer;
    private static int _sent, _sentBytes, _sentItems, _sentPowerUps, _skippedPersonal, _captureErrors;
    private static long _sentXp;
    private static int _recv, _replayed, _replayErrors, _builtItems, _itemFallbacks, _dropped;
    private static long _recvXp;
    private static int _blockedPuppetLoot, _blockedPortal;

    // ---------------- host: capture ----------------

    /// <summary>ProcessLoot prefix on the host. Layout: type u8 | netId u16 | pos 3*f32 | xp i32 | prestige f32 |
    /// flags u8 (1 instant, 2 currencies) | interval i32 | [6 * i32] | nItems u8 * (id i32, chance f32, min i32,
    /// max i32) | nPowerUps u8 * (level i32, source i32, flags u8).</summary>
    internal static void CaptureOnHost(int experience, float prestige, PlayerProfileCurrencies? currencies,
        Il2CppList.List<ItemDropRuntimeData>? possibleItems, Il2CppList.List<PowerUpRequest>? powerUps,
        Vector3 sourcePosition, bool instant, int experienceInterval)
    {
        if (NetworkManager.RemotePeer == null) return;
        if (InPersonalLoot) { _skippedPersonal++; return; }

        int[]? cur = null;
        if (currencies != null)
        {
            cur = new[]
            {
                currencies.MinorSoulstones, currencies.CorruptedSoulstones, currencies.VileSoulstones,
                currencies.WickedSoulstones, currencies.HatefulSoulstones, currencies.RogueSoulstones,
            };
            bool any = false;
            foreach (int c in cur) if (c > 0) { any = true; break; }
            if (!any) cur = null;
        }

        var items = new List<ItemMsg>();
        if (possibleItems != null)
        {
            try
            {
                int n = Math.Min(possibleItems.Count, 255);
                for (int i = 0; i < n; i++)
                {
                    var d = possibleItems[i];
                    var def = d.ItemDefinition;
                    if (def == null) continue;
                    items.Add(new ItemMsg { Id = (int)def.ItemId, Chance = d.Chance, Min = d.QuantityMin, Max = d.QuantityMax });
                }
            }
            catch (Exception ex)
            {
                if (_captureErrors++ == 0) Plugin.StaticLog.LogError($"[SoulstoneCoop][loot] Reading possibleItems: {ex.Message}");
                items.Clear();
            }
        }

        var pus = new List<PowerUpMsg>();
        if (powerUps != null)
        {
            int n = Math.Min(powerUps.Count, 255);
            for (int i = 0; i < n; i++)
            {
                var r = powerUps[i];
                pus.Add(new PowerUpMsg { Level = r.PlayerLevel, Source = (int)r.SpecialSource, Lock = r.CanBeLocked, Banish = r.CanBeBanished });
            }
        }

        if (experience <= 0 && prestige <= 0f && cur == null && items.Count == 0 && pus.Count == 0) return;

        ushort netId = Enemies.HostEnemySync.FindKilledAt(sourcePosition);

        var w = new NetDataWriter();
        w.Put((byte)PacketType.LootDrop);
        w.Put(netId);
        w.Put(sourcePosition.x); w.Put(sourcePosition.y); w.Put(sourcePosition.z);
        w.Put(experience);
        w.Put(prestige);
        w.Put((byte)((instant ? 1 : 0) | (cur != null ? 2 : 0)));
        w.Put(experienceInterval);
        if (cur != null) foreach (int c in cur) w.Put(c);
        w.Put((byte)items.Count);
        foreach (var it in items) { w.Put(it.Id); w.Put(it.Chance); w.Put(it.Min); w.Put(it.Max); }
        w.Put((byte)pus.Count);
        foreach (var p in pus) { w.Put(p.Level); w.Put(p.Source); w.Put((byte)((p.Lock ? 1 : 0) | (p.Banish ? 2 : 0))); }

        // Same channel as EnemyDied: ProcessLoot runs inside the death, Died is sent later by the polling tick,
        // so the loot of an enemy always arrives before its death.
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sent++;
        _sentBytes += w.Length;
        _sentXp += Math.Max(0, experience);
        _sentItems += items.Count;
        _sentPowerUps += pus.Count;
    }

    // ---------------- client: replay ----------------

    public static void OnLootDrop(NetDataReader r)
    {
        var m = new LootMsg
        {
            NetId = r.GetUShort(),
            Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
            Xp = r.GetInt(),
            Prestige = r.GetFloat(),
        };
        byte flags = r.GetByte();
        m.Instant = (flags & 1) != 0;
        m.Interval = r.GetInt();
        if ((flags & 2) != 0)
        {
            m.Currencies = new int[6];
            for (int i = 0; i < 6; i++) m.Currencies[i] = r.GetInt();
        }
        int ni = r.GetByte();
        for (int i = 0; i < ni; i++)
            m.Items.Add(new ItemMsg { Id = r.GetInt(), Chance = r.GetFloat(), Min = r.GetInt(), Max = r.GetInt() });
        int np = r.GetByte();
        for (int i = 0; i < np; i++)
        {
            int level = r.GetInt(), src = r.GetInt();
            byte pf = r.GetByte();
            m.PowerUps.Add(new PowerUpMsg { Level = level, Source = src, Lock = (pf & 1) != 0, Banish = (pf & 2) != 0 });
        }
        if (!CoopRole.IsClientSide) return;

        _recv++;
        _recvXp += Math.Max(0, m.Xp);
        // Replay right away when possible: the puppet (items fallback) is still alive, its Died comes next.
        if (Ready()) Replay(m);
        else Pending.Add(m);
    }

    private static bool Ready() =>
        GameManagerUtil.HasGameManager && Enemies.ClientPuppets.MatchStarted && GameState.LocalPlayerEntity != null;

    public static void Tick(float unscaledDeltaTime)
    {
        if (CoopRole.IsClientSide && Pending.Count > 0)
        {
            if (!GameManagerUtil.HasGameManager) { _dropped += Pending.Count; Pending.Clear(); }
            else if (Ready())
            {
                int n = 0;
                while (Pending.Count > 0 && n++ < MaxReplaysPerFrame)
                {
                    var m = Pending[0];
                    Pending.RemoveAt(0);
                    Replay(m);
                }
            }
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        LogTelemetry();
    }

    private static void Replay(LootMsg m)
    {
        try
        {
            PlayerProfileCurrencies? cur = null;
            if (m.Currencies != null)
            {
                cur = new PlayerProfileCurrencies
                {
                    MinorSoulstones = m.Currencies[0],
                    CorruptedSoulstones = m.Currencies[1],
                    VileSoulstones = m.Currencies[2],
                    WickedSoulstones = m.Currencies[3],
                    HatefulSoulstones = m.Currencies[4],
                    RogueSoulstones = m.Currencies[5],
                };
            }

            var items = m.Items.Count > 0 ? BuildItems(m) : null;

            Il2CppList.List<PowerUpRequest>? pus = null;
            if (m.PowerUps.Count > 0)
            {
                pus = new Il2CppList.List<PowerUpRequest>();
                foreach (var p in m.PowerUps)
                    pus.Add(new PowerUpRequest(p.Level, (PowerUpSpecialSource)p.Source, p.Lock, p.Banish));
            }

            ReplayingNetworkLoot = true;
            LootUtil.ProcessLoot(m.Xp, m.Prestige, cur!, items!, pus!, m.Pos, m.Instant, m.Interval);
            _replayed++;
        }
        catch (Exception ex)
        {
            if (_replayErrors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][loot] Replay (netId {m.NetId}, xp {m.Xp}): {ex}");
        }
        finally
        {
            ReplayingNetworkLoot = false;
        }
    }

    /// <summary>Items by ItemId (same build, same ItemCollectionSO). ItemDropRuntimeData is a non-blittable struct
    /// in the interop; if building the list fails once, fall back to the dying puppet's own LootComponent list.</summary>
    private static Il2CppList.List<ItemDropRuntimeData>? BuildItems(LootMsg m)
    {
        if (!_itemListBroken)
        {
            try
            {
                var collection = Global.ItemCollection;
                var list = new Il2CppList.List<ItemDropRuntimeData>();
                foreach (var it in m.Items)
                {
                    var def = collection != null ? collection.GetItemDefinitionById((ItemId)it.Id) : null;
                    if (def == null) continue;
                    var d = new ItemDropRuntimeData
                    {
                        ItemDefinition = def,
                        Chance = it.Chance,
                        QuantityMin = it.Min,
                        QuantityMax = it.Max,
                    };
                    list.Add(d);
                }
                _builtItems += list.Count;
                return list;
            }
            catch (Exception ex)
            {
                _itemListBroken = true;
                Plugin.StaticLog.LogError($"[SoulstoneCoop][loot] Building List<ItemDropRuntimeData> failed, using the puppet's list from now on: {ex}");
            }
        }

        _itemFallbacks++;
        var puppet = Enemies.ClientPuppets.TryGetPuppetEntity(m.NetId);
        var loot = puppet != null ? puppet._loot : null;
        return loot != null ? loot.PossibleItems : null;
    }

    private static void LogTelemetry()
    {
        bool host = CoopRole.IsHostSide, client = CoopRole.IsClientSide;
        if (host && (_sent > 0 || _skippedPersonal > 0 || _captureErrors > 0))
        {
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][loot] Host: sent={_sent} bytes={_sentBytes} xp={_sentXp} items={_sentItems} powerUps={_sentPowerUps} " +
                $"skippedPersonal={_skippedPersonal} captureErrors={_captureErrors} {LocalProgress()}");
        }
        else if (client && (_recv > 0 || _blockedPuppetLoot > 0 || Pending.Count > 0 || _replayErrors > 0))
        {
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][loot] Client: recv={_recv} replayed={_replayed} pending={Pending.Count} dropped={_dropped} xp={_recvXp} " +
                $"builtItems={_builtItems} itemFallbacks={_itemFallbacks} errors={_replayErrors} blockedPuppetLoot={_blockedPuppetLoot} " +
                $"blockedPortal={_blockedPortal} {LocalProgress()}");
        }
        _sent = _sentBytes = _sentItems = _sentPowerUps = _skippedPersonal = 0;
        _sentXp = 0;
        _recv = _replayed = _builtItems = _itemFallbacks = _dropped = 0;
        _recvXp = 0;
        _blockedPuppetLoot = _blockedPortal = 0;
    }

    private static string LocalProgress()
    {
        try
        {
            var p = GameState.LocalPlayerEntity;
            string lvl = "-", kills = "-", runeKills = "-";
            var xp = p != null ? p._experience : null;
            if (xp != null) lvl = $"{xp.Level} ({xp._experience:F0}+{xp._experienceToAdd:F0})";
            var runes = p != null ? p._runes : null;
            if (runes != null) runeKills = runes.KillCount.ToString();
            var stats = GameManagerUtil.HasGameManager ? GameManagerUtil.GameStats : null;
            if (stats != null) kills = $"{stats.EnemiesKilled}/{stats.ElitesKilled}/{stats.BossesKilled}";
            return $"level={lvl} kills={kills} runeKillCount={runeKills}";
        }
        catch (Exception ex)
        {
            return $"progress=<{ex.Message}>";
        }
    }

    internal static void CountBlockedPuppetLoot() => _blockedPuppetLoot++;
    internal static void CountBlockedPortal() => _blockedPortal++;
}

// ---------------- Harmony patches ----------------

/// <summary>Host: send every ProcessLoot to the client. Client: block the puppet's own loot while we play the
/// host's death (yieldLoot=true is only there for the on-kill procs); the host's LootDrop brings it instead.</summary>
[HarmonyPatch(typeof(LootUtil), nameof(LootUtil.ProcessLoot))]
public static class LootSyncProcessLootPatch
{
    [HarmonyPrefix]
    public static bool Prefix(int experience, float prestige, PlayerProfileCurrencies currencies,
        Il2CppList.List<ItemDropRuntimeData> possibleItems, Il2CppList.List<PowerUpRequest> powerUps,
        Vector3 sourcePosition, bool instant, int experienceInterval)
    {
        try
        {
            if (CoopRole.IsHostSide)
            {
                LootSync.CaptureOnHost(experience, prestige, currencies, possibleItems, powerUps, sourcePosition, instant, experienceInterval);
            }
            else if (CoopRole.IsClientSide && Enemies.ClientPuppets.ApplyingNetworkDeath && !LootSync.ReplayingNetworkLoot)
            {
                LootSync.CountBlockedPuppetLoot();
                return false;
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] LootSyncProcessLootPatch: {ex}");
        }
        return true;
    }
}

/// <summary>Host: mark the soulstone pickup's synchronous breakdown step as personal loot (not replicated).</summary>
[HarmonyPatch(typeof(SoulstoneItem), nameof(SoulstoneItem.OnCollect))]
public static class LootSyncSoulstoneCollectPatch
{
    [HarmonyPrefix]
    public static void Prefix() => LootSync.InPersonalLoot = true;

    [HarmonyFinalizer]
    public static void Finalizer() => LootSync.InPersonalLoot = false;
}

/// <summary>Client: every LootDropperByHealthPercent here belongs to a puppet (bosses), whose HP comes from the
/// host's snapshots. Running it would drop XP locally on top of the host's replicated drops.</summary>
[HarmonyPatch(typeof(LootDropperByHealthPercentComponent), nameof(LootDropperByHealthPercentComponent.UpdateAll))]
public static class LootSyncBossDropperPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        return !CoopRole.IsClientSide;
    }
}

/// <summary>Client: the boss power-up (CollectableSurvivorsBossPowerUp) opens the portal through this. The portal
/// belongs to the host; the power-up choice itself is already queued by the base DoOnReachedTarget.</summary>
[HarmonyPatch(typeof(SurvivorsGameManager), nameof(SurvivorsGameManager.OnPowerUpCollected))]
public static class LootSyncBossPortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        if (!CoopRole.IsClientSide) return true;
        LootSync.CountBlockedPortal();
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][loot] Client picked a boss power-up: OpenPortalAndObjective skipped (portal is the host's).");
        return false;
    }
}
