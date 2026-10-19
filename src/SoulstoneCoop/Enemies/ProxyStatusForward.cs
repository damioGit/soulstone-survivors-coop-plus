using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using SoulstoneCoop.Skills;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Negative statuses on the client (pendências). The host's enemies apply them to the client's PROXY: every status
/// enters AbnormalStatusComponent.ApplyEffects (0x4F1E40, also the entry the 4C/4D blocks use) and a stun is
/// StunnableComponent.SetToRagdoll (0x801BA0, called by SetToRagdollEffect). Both stayed on the host. Now the host
/// forwards the INPUT of those calls (ProxyStatus = 71) and the client replays it on its local player, with the
/// attacker's puppet as the applying entity: the client's own immunities, resistances and rolls apply, like in single
/// player. Only the count statuses travel (slow, weakness, fragility, dazed, disoriented, exposed, distracted,
/// brittle, debilitated, crippled, disarray, shattered, wound, condemned): burn, bleed, poison and doom are zeroed,
/// because their damage already arrives from the host (DamageToClient). Belt and braces: with a puppet as applier, any
/// local DoT tick or instant hit would be blocked by ClientPuppetEffectDamageBlockPatch anyway. A status whose applier
/// has no puppet on the client (God-Entity, unknown) is dropped.
/// Layout (after the type byte): kind u8 (0 = abnormal, 1 = stun) |
///   abnormal: applier netId u16 | flags u8 (1 = ignoreWounds, 2 = ignoreSynergies) | count u8 | count x (slot u8, stacks i32) | [brittle extra f32]
///   stun: applier netId u16 | duration f32 | penetrateSoftImmunity u8
/// ReliableUnordered.
/// </summary>
public static class ProxyStatusForward
{
    private const byte KindAbnormal = 0, KindStun = 1;
    private const byte FIgnoreWounds = 1, FIgnoreSynergies = 2;
    private const int MaxPerSecond = 40; // a swarm on the proxy: the statuses stack fast anyway
    private const float TelemetryInterval = 5f;

    // Slots of the forwarded count statuses (wire ids, keep stable).
    private const byte SShattered = 0, SSlow = 1, SFragility = 2, SWeakness = 3, SDazed = 4, SDisoriented = 5, SExposed = 6,
        SDistracted = 7, SBrittle = 8, SDebilitated = 9, SCrippled = 10, SDisarray = 11, SWound = 12, SCondemned = 13;

    internal static bool ApplyingNetworkStatus;

    /// <summary>Phase 9: one proxy per client. Cached per slot: the proxy and its component pointers, plus a send budget.</summary>
    private sealed class Watched
    {
        public IntPtr ProxyPtr, AbnormalPtr, StunnablePtr;
        public int Budget = MaxPerSecond;
    }

    private static readonly Dictionary<byte, Watched> BySlot = new();
    private static readonly Dictionary<IntPtr, byte> SlotByAbnormal = new();
    private static readonly Dictionary<IntPtr, byte> SlotByStunnable = new();
    private static SkillModifiers? _emptyModifiers;
    private static float _budgetTimer, _telemetryTimer;
    private static int _sentAbnormal, _sentStun, _throttled, _onlyDot, _noApplier, _captureErrors;
    private static int _recv, _applied, _stuns, _noPuppet, _ignored, _errors;
    private static readonly List<(byte slot, int stacks)> Scratch = new();

    internal static bool IsProxyAbnormal(AbnormalStatusComponent c, out byte slot) => SlotByAbnormal.TryGetValue(c.Pointer, out slot);
    internal static bool IsProxyStunnable(StunnableComponent c, out byte slot) => SlotByStunnable.TryGetValue(c.Pointer, out slot);
    internal static void CountCaptureError() => _captureErrors++;

    public static void Tick(float unscaledDeltaTime)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try
        {
            // Host: cache each proxy's component pointers (cheap filters for patches that run for every entity).
            if (CoopRole.IsHostSide) RefreshWatched();
            else if (BySlot.Count > 0) { BySlot.Clear(); SlotByAbnormal.Clear(); SlotByStunnable.Clear(); }
            _budgetTimer += unscaledDeltaTime;
            if (_budgetTimer >= 1f)
            {
                _budgetTimer = 0f;
                foreach (var w in BySlot.Values) w.Budget = MaxPerSecond;
            }
        }
        catch (Exception ex)
        {
            _errors++;
            if (_errors <= 3) Plugin.StaticLog.LogError($"[SoulstoneCoop][status] Tick: {ex}");
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        if (_sentAbnormal + _sentStun + _throttled + _noApplier + _captureErrors + _recv + _errors > 0)
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][status] {NetworkManager.Role}: sent={_sentAbnormal} stunsSent={_sentStun} throttled={_throttled} onlyDot={_onlyDot} " +
                $"noApplier={_noApplier} captureErrors={_captureErrors} recv={_recv} applied={_applied} stuns={_stuns} noPuppet={_noPuppet} " +
                $"ignored={_ignored} errors={_errors}");
        _sentAbnormal = _sentStun = _throttled = _onlyDot = _noApplier = _captureErrors = 0;
        _recv = _applied = _stuns = _noPuppet = _ignored = _errors = 0;
    }

    // ---------------- host ----------------

    private static readonly List<byte> GoneSlots = new();

    private static void RefreshWatched()
    {
        GoneSlots.Clear();
        foreach (var slot in BySlot.Keys) if (!Players.IsPresent(slot)) GoneSlots.Add(slot);
        foreach (var slot in GoneSlots) BySlot.Remove(slot);
        bool changed = GoneSlots.Count > 0;
        foreach (var slot in Players.Others)
        {
            var proxy = RemoteProxy.ProxyOf(slot);
            IntPtr p = proxy != null ? proxy.Pointer : IntPtr.Zero;
            if (!BySlot.TryGetValue(slot, out var w)) BySlot[slot] = w = new Watched();
            if (p == w.ProxyPtr) continue;
            changed = true;
            w.ProxyPtr = p;
            var abn = proxy != null ? proxy._abnormalStatus : null;
            var stun = proxy != null ? proxy._stunnable : null;
            w.AbnormalPtr = abn != null ? abn.Pointer : IntPtr.Zero;
            w.StunnablePtr = stun != null ? stun.Pointer : IntPtr.Zero;
            if (proxy != null)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][status] Proxy {slot} components: abnormal={(abn != null)} stunnable={(stun != null)}.");
        }
        if (!changed) return;
        SlotByAbnormal.Clear();
        SlotByStunnable.Clear();
        foreach (var kv in BySlot)
        {
            if (kv.Value.AbnormalPtr != IntPtr.Zero) SlotByAbnormal[kv.Value.AbnormalPtr] = kv.Key;
            if (kv.Value.StunnablePtr != IntPtr.Zero) SlotByStunnable[kv.Value.StunnablePtr] = kv.Key;
        }
    }


    private static bool TryApplierNetId(Entity? applier, out ushort netId)
    {
        netId = 0;
        if (applier == null || RemoteCaster.IsCaster(applier)) return false; // the replica's statuses are blocked anyway
        if (HostEnemySync.TryGetNetId(applier, out netId) && netId != 0) return true;
        _noApplier++;
        return false;
    }

    private static bool TakeBudget(byte slot)
    {
        if (!BySlot.TryGetValue(slot, out var w) || w.Budget <= 0) { _throttled++; return false; }
        w.Budget--;
        return true;
    }

    internal static void OnHostAbnormal(byte slot, Entity applier, in AbnormalStatusApplyData d, bool ignoreWounds, bool ignoreSynergies)
    {
        Scratch.Clear();
        Add(SShattered, d.ShatteredApplyData.StacksToApply);
        Add(SSlow, d.SlowApplyData.StacksToApply);
        Add(SFragility, d.FragilityApplyData.StacksToApply);
        Add(SWeakness, d.weaknessApplyData.StacksToApply);
        Add(SDazed, d.DazedApplyData.StacksToApply);
        Add(SDisoriented, d.DisorientedApplyData.StacksToApply);
        Add(SExposed, d.ExposedApplyData.StacksToApply);
        Add(SDistracted, d.DistractedApplyData.StacksToApply);
        Add(SBrittle, d.BrittleApplyData.StacksToApply);
        Add(SDebilitated, d.DebilitatedApplyData.StacksToApply);
        Add(SCrippled, d.CrippledApplyData.StacksToApply);
        Add(SDisarray, d.DisarrayApplyData.StacksToApply);
        Add(SWound, d.WoundApplyData.StacksToApply);
        Add(SCondemned, d.CondemnedApplyData.StacksToApply);
        if (Scratch.Count == 0) { _onlyDot++; return; } // only DoTs (or nothing): their damage comes as DamageToClient
        if (!TryApplierNetId(applier, out ushort netId) || !TakeBudget(slot)) return;

        var w = new NetDataWriter();
        w.Put((byte)PacketType.ProxyStatus);
        w.Put(KindAbnormal);
        w.Put(netId);
        w.Put((byte)((ignoreWounds ? FIgnoreWounds : 0) | (ignoreSynergies ? FIgnoreSynergies : 0)));
        w.Put((byte)Scratch.Count);
        bool brittle = false;
        foreach (var (statusSlot, stacks) in Scratch)
        {
            w.Put(statusSlot);
            w.Put(stacks);
            brittle |= statusSlot == SBrittle;
        }
        if (brittle) w.Put(d.BrittleApplyData.ExtraDamageToTake);
        NetworkManager.SendTo(slot, w, DeliveryMethod.ReliableUnordered); // only that client: it is its proxy
        _sentAbnormal++;
    }

    private static void Add(byte slot, int stacks)
    {
        if (stacks > 0) Scratch.Add((slot, stacks));
    }

    /// <summary>SetToRagdoll has no applier: the stun goes with netId 0 (the client doesn't need one).</summary>
    internal static void OnHostStun(byte slot, float duration, bool penetrateSoftImmunity)
    {
        if (duration <= 0f || !TakeBudget(slot)) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.ProxyStatus);
        w.Put(KindStun);
        w.Put((ushort)0);
        w.Put(duration);
        w.Put(penetrateSoftImmunity);
        NetworkManager.SendTo(slot, w, DeliveryMethod.ReliableUnordered); // only that client: it is its proxy
        _sentStun++;
    }

    // ---------------- client ----------------

    public static void OnProxyStatus(NetDataReader r)
    {
        byte kind = r.GetByte();
        ushort netId = r.GetUShort();
        if (kind == KindStun)
        {
            float duration = r.GetFloat();
            bool penetrate = r.GetBool();
            if (CoopRole.IsClientSide) ApplyStun(netId, duration, penetrate);
            return;
        }
        if (kind != KindAbnormal) return;
        byte flags = r.GetByte();
        int count = r.GetByte();
        var d = new AbnormalStatusApplyData();
        bool brittle = false;
        for (int i = 0; i < count; i++)
        {
            byte slot = r.GetByte();
            int stacks = r.GetInt();
            Set(ref d, slot, stacks);
            brittle |= slot == SBrittle;
        }
        if (brittle) d.BrittleApplyData.ExtraDamageToTake = r.GetFloat();
        if (!CoopRole.IsClientSide) return;
        _recv++;

        var local = GameState.LocalPlayerEntity;
        var abn = local != null ? local._abnormalStatus : null;
        var health = local != null ? local._health : null;
        if (abn == null || health == null || health.HasBeenKilled || !ClientPuppets.MatchStarted) { _ignored++; return; }
        var puppet = ClientPuppets.TryGetLivePuppet(netId);
        if (puppet == null) { _noPuppet++; return; } // ApplyEffects dereferences the applier
        try
        {
            _emptyModifiers ??= new SkillModifiers();
            ApplyingNetworkStatus = true;
            // Source indices 0 / SkillTreeId.None and logStats false: they only feed the DoT stats, and DoTs aren't sent.
            abn.ApplyEffects(puppet, _emptyModifiers, 0f, 0f, 0, 0, SkillTreeId.None, d,
                (flags & FIgnoreWounds) != 0, (flags & FIgnoreSynergies) != 0, false);
            _applied++;
        }
        catch (Exception ex)
        {
            _errors++;
            if (_errors <= 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][status] Client: applying a status from #{netId}: {ex.Message}");
        }
        finally
        {
            ApplyingNetworkStatus = false;
        }
    }

    private static void Set(ref AbnormalStatusApplyData d, byte slot, int stacks)
    {
        switch (slot)
        {
            case SShattered: d.ShatteredApplyData.StacksToApply = stacks; break;
            case SSlow: d.SlowApplyData.StacksToApply = stacks; break;
            case SFragility: d.FragilityApplyData.StacksToApply = stacks; break;
            case SWeakness: d.weaknessApplyData.StacksToApply = stacks; break;
            case SDazed: d.DazedApplyData.StacksToApply = stacks; break;
            case SDisoriented: d.DisorientedApplyData.StacksToApply = stacks; break;
            case SExposed: d.ExposedApplyData.StacksToApply = stacks; break;
            case SDistracted: d.DistractedApplyData.StacksToApply = stacks; break;
            case SBrittle: d.BrittleApplyData.StacksToApply = stacks; break;
            case SDebilitated: d.DebilitatedApplyData.StacksToApply = stacks; break;
            case SCrippled: d.CrippledApplyData.StacksToApply = stacks; break;
            case SDisarray: d.DisarrayApplyData.StacksToApply = stacks; break;
            case SWound: d.WoundApplyData.StacksToApply = stacks; break;
            case SCondemned: d.CondemnedApplyData.StacksToApply = stacks; break;
        }
    }

    private static void ApplyStun(ushort netId, float duration, bool penetrate)
    {
        _recv++;
        var local = GameState.LocalPlayerEntity;
        var stun = local != null ? local._stunnable : null;
        var health = local != null ? local._health : null;
        // No StunnableComponent on the player = the game never stuns players: nothing to do.
        if (stun == null || health == null || health.HasBeenKilled || !ClientPuppets.MatchStarted) { _ignored++; return; }
        try
        {
            stun.SetToRagdoll(duration, penetrate); // its own immunity checks (ImmuneToStunEffect, soft immunity)
            _stuns++;
        }
        catch (Exception ex)
        {
            _errors++;
            if (_errors <= 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][status] Client: stun ({duration:F2} s) from #{netId}: {ex.Message}");
        }
    }
}

// ---------------- Harmony patches (host side, every one wrapped in try/catch) ----------------

/// <summary>Host: a status applied to the client's proxy. Postfix, so the 4C block (prefix returning false for the
/// RemoteCaster) still wins; the replica's statuses are skipped by the applier check too. Same signature the 4C/4D
/// prefixes already patch: AbnormalStatusApplyData is a 0x6C-byte blittable struct, passed by pointer.</summary>
[HarmonyPatch(typeof(AbnormalStatusComponent), nameof(AbnormalStatusComponent.ApplyEffects))]
public static class HostProxyStatusCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(AbnormalStatusComponent __instance, Entity applyingEntity, AbnormalStatusApplyData data,
        bool ignoreWounds, bool ignoreSynergies)
    {
        try
        {
            if (__instance == null || !ProxyStatusForward.IsProxyAbnormal(__instance, out byte slot)) return;
            if (!CoopRole.IsHostSide || !NetworkManager.HasPeer || !RemoteProxy.IsAlive(slot)) return;
            ProxyStatusForward.OnHostAbnormal(slot, applyingEntity, data, ignoreWounds, ignoreSynergies);
        }
        catch (Exception ex)
        {
            ProxyStatusForward.CountCaptureError();
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostProxyStatusCapturePatch: {ex}");
        }
    }
}

/// <summary>Host: a stun on the client's proxy (SetToRagdollEffect -> StunnableComponent.SetToRagdoll). A float and a
/// bool: trampoline-safe. The client replays it on its player's StunnableComponent, whose own immunity flags decide,
/// exactly as when the same enemy effect hits a player in single player.</summary>
[HarmonyPatch(typeof(StunnableComponent), nameof(StunnableComponent.SetToRagdoll))]
public static class HostProxyStunCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(StunnableComponent __instance, float duration, bool penetrateSoftImmunity)
    {
        try
        {
            if (__instance == null || !ProxyStatusForward.IsProxyStunnable(__instance, out byte slot)) return;
            if (!CoopRole.IsHostSide || !NetworkManager.HasPeer || !RemoteProxy.IsAlive(slot)) return;
            ProxyStatusForward.OnHostStun(slot, duration, penetrateSoftImmunity);
        }
        catch (Exception ex)
        {
            ProxyStatusForward.CountCaptureError();
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostProxyStunCapturePatch: {ex}");
        }
    }
}
