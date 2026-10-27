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
/// XP is the exception (team XP): each drop's orbs exist in ONE world - the nearest living player's - and every
/// AddExperience of the local player is shared with the partner (XpShare = 17). See docs/fase4b-notas.md.
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

    // Telemetry window
    private static float _telemetryTimer;
    private static int _sent, _sentBytes, _sentItems, _sentPowerUps, _skippedPersonal, _captureErrors;
    private static long _sentXp;
    private static int _recv, _replayed, _replayErrors, _dropped;
    private static long _recvXp;
    private static int _blockedPuppetLoot, _blockedPortal;

    // ---------------- host: capture ----------------

    /// <summary>ProcessLoot prefix on the host. Layout: type u8 | netId u16 | pos 3*f32 | xpOwner u8 (phase 9) | xp i32 | prestige f32 |
    /// flags u8 (1 instant, 2 currencies) | interval i32 | [6 * i32] | nItems u8 * (id i32, chance f32, min i32,
    /// max i32) | nPowerUps u8 * (level i32, source i32, flags u8).</summary>
    internal static void CaptureOnHost(ref int experience, float prestige, PlayerProfileCurrencies? currencies,
        Il2CppList.List<ItemDropRuntimeData>? possibleItems, Il2CppList.List<PowerUpRequest>? powerUps,
        Vector3 sourcePosition, bool instant, int experienceInterval)
    {
        if (!NetworkManager.HasPeer) return;
        if (InPersonalLoot) { _skippedPersonal++; return; }

        // Team XP: each orb exists in ONE world only - the nearest living player's - and whoever collects it
        // shares the amount (XpShare). Client's: the host drops nothing and sends the XP; host's: sends 0.
        int sendXp = 0;
        byte xpOwner = Net.Players.HostSlot;
        if (experience > 0)
        {
            xpOwner = OwnerOfXp(sourcePosition);
            if (xpOwner != Net.Players.HostSlot)
            {
                sendXp = experience;
                experience = 0;
                _xpToClient += sendXp;
                _dropsToClient++;
            }
            else { _xpKeptHost += experience; _dropsKept++; }
        }

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

        if (sendXp <= 0 && prestige <= 0f && cur == null && items.Count == 0 && pus.Count == 0) return;

        ushort netId = Enemies.HostEnemySync.FindKilledAt(sourcePosition);

        var w = new NetDataWriter();
        w.Put((byte)PacketType.LootDrop);
        w.Put(netId);
        w.Put(sourcePosition.x); w.Put(sourcePosition.y); w.Put(sourcePosition.z);
        w.Put(xpOwner); // phase 9: only that client drops the XP orb (team XP: whoever collects it shares it)
        w.Put(sendXp);
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
        _sentXp += sendXp;
        _sentItems += items.Count;
        _sentPowerUps += pus.Count;
    }

    /// <summary>Nearest living player owns the orb (phase 9: the host or any living client). Nobody alive = the host's.</summary>
    private static byte OwnerOfXp(Vector3 pos)
    {
        byte best = Net.Players.HostSlot;
        float bestD = float.MaxValue;
        var host = GameState.LocalPlayerEntity;
        bool hostUp = host != null && !(host._health != null && host._health.HasBeenKilled) && !Life.CoopLife.LocalDowned;
        if (hostUp) bestD = (host!.transform.position - pos).sqrMagnitude;
        foreach (var slot in Net.Players.Others)
        {
            if (!Enemies.RemoteProxy.IsAlive(slot) || !Enemies.RemoteProxy.TryGetPosition(slot, out var p, out _)) continue;
            float d = (p - pos).sqrMagnitude;
            if (d < bestD) { bestD = d; best = slot; }
        }
        return best;
    }

    // ---------------- both: shared XP ----------------

    private const float ShareFlushInterval = 0.1f;

    /// <summary>Set while we add the partner's collected XP, so it isn't echoed back.</summary>
    internal static bool ApplyingSharedXp;

    private static float _shareOut, _shareIn, _shareFlushTimer;
    private static int _sharePackets, _shareRecvPackets;
    private static double _shareSentSum, _shareRecvSum;
    private static long _xpToClient, _xpKeptHost;
    private static int _dropsToClient, _dropsKept;
    private static double _shareAppliedSum, _shareDiscardedSum;

    /// <summary>ExperienceComponent.AddExperience prefix (the single funnel of XP collection: orbs in both XpDropper
    /// modes, ExperienceProjectile). Raw amount, before the collector's XP stat: the partner applies its own.
    /// Returns false (skip the original) for XP our own DOWNED player picks up (phase 4A): it still goes to the
    /// partner, and is held for us like the partner's shared XP (applied on revive, no echo).</summary>
    internal static bool OnLocalAddExperience(ExperienceComponent comp, float amount)
    {
        if (ApplyingSharedXp || amount <= 0f || !NetworkManager.HasPeer) return true;
        var local = GameState.LocalPlayerEntity;
        if (local == null || comp.Entity == null || comp.Entity.Pointer != local.Pointer) return true;
        _shareOut += amount;
        if (!Life.CoopLife.LocalDowned) return true;
        _shareIn += amount;
        _ownHeldWhileDown += amount;
        return false;
    }

    private static double _ownHeldWhileDown;

    private static void FlushShare(float unscaledDeltaTime)
    {
        _shareFlushTimer += unscaledDeltaTime;
        if (_shareFlushTimer < ShareFlushInterval) return;
        _shareFlushTimer = 0f;
        if (_shareOut <= 0f) return;
        if (!NetworkManager.HasPeer) { _shareOut = 0f; return; }
        var w = new NetDataWriter();
        w.Put((byte)PacketType.XpShare);
        w.Put(_shareOut);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sharePackets++;
        _shareSentSum += _shareOut;
        _shareOut = 0f;
    }

    /// <summary>Layout: type u8 | amount f32 (raw XP the partner collected since the last packet).</summary>
    public static void OnXpShare(NetDataReader r)
    {
        float amount = r.GetFloat();
        if (amount <= 0f) return;
        _shareRecvPackets++;
        _shareRecvSum += amount;
        _shareIn += amount;
        ApplyShareIn();
    }

    private static void ApplyShareIn()
    {
        if (_shareIn <= 0f) return;
        if (!GameManagerUtil.HasGameManager) { _shareDiscardedSum += _shareIn; _shareIn = 0f; return; } // run over
        var local = GameState.LocalPlayerEntity;
        var xp = local != null ? local._experience : null;
        if (xp == null) return; // keep it until our player is up
        // Never on a dead (or, later, downed) player: AddExperience can open the level-up popup, which pins
        // LevelUpTime at 0 and freezes both games through the synchronized pause. Held until it revives.
        if ((local!._health != null && local._health.HasBeenKilled) || Life.CoopLife.LocalDowned) return;
        float amount = _shareIn;
        _shareIn = 0f;
        try
        {
            ApplyingSharedXp = true;
            xp.AddExperience(amount);
            _shareAppliedSum += amount;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][loot] Applying shared XP {amount:F0}: {ex.Message}");
        }
        finally
        {
            ApplyingSharedXp = false;
        }
    }

    // ---------------- client: replay ----------------

    public static void OnLootDrop(NetDataReader r)
    {
        var m = new LootMsg
        {
            NetId = r.GetUShort(),
            Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
        };
        byte xpOwner = r.GetByte();
        int xp = r.GetInt();
        m.Xp = xpOwner == Net.Players.MySlot ? xp : 0; // phase 9: the orb exists in its owner's world only
        m.Prestige = r.GetFloat();
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

    /// <summary>Client: the host's old scene is gone (Lobby.PortalSync, Endless/BossRush portal or Restart): loot
    /// still waiting from it would be replayed in the next scene.</summary>
    internal static void DropPendingFromOldScene()
    {
        if (Pending.Count == 0) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][loot] Dropping {Pending.Count} pending loot replays from the host's old scene.");
        _dropped += Pending.Count;
        Pending.Clear();
    }

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

        FlushShare(unscaledDeltaTime);
        ApplyShareIn();

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

            // Materials (copper, iron...): a List<ItemDropRuntimeData> built here came out garbled through the interop
            // (chance 0, quantity garbage: the client never got a single material). Rolled and handed out below,
            // the way ProcessLoot does it, with this player's own roll ("one per player", as the game does for N).
            Il2CppList.List<ItemDropRuntimeData>? items = null;

            Il2CppList.List<PowerUpRequest>? pus = null;
            if (m.PowerUps.Count > 0)
            {
                pus = new Il2CppList.List<PowerUpRequest>();
                foreach (var p in m.PowerUps)
                    pus.Add(new PowerUpRequest(p.Level, (PowerUpSpecialSource)p.Source, p.Lock, p.Banish));
            }

            ReplayingNetworkLoot = true;
            LootUtil.ProcessLoot(m.Xp, m.Prestige, cur!, items!, pus!, m.Pos, m.Instant, m.Interval);
            if (m.Items.Count > 0) GiveItems(m);
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

    /// <summary>ProcessLoot's item step for the local player: roll each item's chance and quantity, then add it right
    /// away (instant loot) or send the game's item projectile, which adds it to the inventory when it arrives.</summary>
    private static void GiveItems(LootMsg m)
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        foreach (var it in m.Items)
        {
            if (UnityEngine.Random.value > it.Chance) continue;
            int qty = UnityEngine.Random.Range(it.Min, Mathf.Max(it.Min, it.Max) + 1);
            if (qty <= 0) continue;
            var id = (ItemId)it.Id;
            if (m.Instant) LootUtil.AddItemToEntity(local, id, qty);
            else ItemProjectile.FireProjectile(m.Pos, local, id, qty);
            _givenItems += qty;
        }
    }

    private static int _givenItems;

    private static void LogTelemetry()
    {
        bool host = CoopRole.IsHostSide, client = CoopRole.IsClientSide;
        if (host && (_sent > 0 || _sharePackets > 0 || _shareRecvPackets > 0 || _skippedPersonal > 0 || _captureErrors > 0))
        {
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][loot] Host: sent={_sent} bytes={_sentBytes} xp={_sentXp} items={_sentItems} powerUps={_sentPowerUps} " +
                $"skippedPersonal={_skippedPersonal} captureErrors={_captureErrors} xpDropsToClient={_dropsToClient} ({_xpToClient}xp) xpDropsToHost={_dropsKept} ({_xpKeptHost}xp) {ShareStats()} {LocalProgress()}");
        }
        else if (client && (_recv > 0 || _sharePackets > 0 || _shareRecvPackets > 0 || _blockedPuppetLoot > 0 || Pending.Count > 0 || _replayErrors > 0))
        {
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][loot] Client: recv={_recv} replayed={_replayed} pending={Pending.Count} dropped={_dropped} xp={_recvXp} " +
                $"givenItems={_givenItems} errors={_replayErrors} blockedPuppetLoot={_blockedPuppetLoot} " +
                $"blockedPortal={_blockedPortal} {ShareStats()} {LocalProgress()}");
        }
        _sent = _sentBytes = _sentItems = _sentPowerUps = _skippedPersonal = 0;
        _sentXp = 0;
        _recv = _replayed = _givenItems = _dropped = 0;
        _recvXp = 0;
        _blockedPuppetLoot = _blockedPortal = 0;
        _xpToClient = _xpKeptHost = 0;
        _dropsToClient = _dropsKept = 0;
        _shareAppliedSum = _shareDiscardedSum = 0;
        _sharePackets = _shareRecvPackets = 0;
        _shareSentSum = _shareRecvSum = 0;
    }

    private static string ShareStats() =>
        $"xpShareSent={_shareSentSum:F0}/{_sharePackets}p xpShareRecv={_shareRecvSum:F0}/{_shareRecvPackets}p xpShareApplied={_shareAppliedSum:F0} xpShareHeld={_shareIn:F0} xpShareDiscarded={_shareDiscardedSum:F0} ownXpHeldWhileDown={_ownHeldWhileDown:F0}";

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
    public static bool Prefix(ref int experience, float prestige, PlayerProfileCurrencies currencies,
        Il2CppList.List<ItemDropRuntimeData> possibleItems, Il2CppList.List<PowerUpRequest> powerUps,
        Vector3 sourcePosition, bool instant, int experienceInterval)
    {
        try
        {
            if (CoopRole.IsHostSide)
            {
                LootSync.CaptureOnHost(ref experience, prestige, currencies, possibleItems, powerUps, sourcePosition, instant, experienceInterval);
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

/// <summary>Both sides: team XP - whatever the local player collects is shared with the partner (XpShare).</summary>
[HarmonyPatch(typeof(ExperienceComponent), nameof(ExperienceComponent.AddExperience))]
public static class LootSyncAddExperiencePatch
{
    [HarmonyPrefix]
    public static bool Prefix(ExperienceComponent __instance, float experienceAmount)
    {
        try { return LootSync.OnLocalAddExperience(__instance, experienceAmount); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] LootSyncAddExperiencePatch: {ex.Message}"); return true; }
    }
}
