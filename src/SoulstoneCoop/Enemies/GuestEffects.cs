using ECM2.Characters;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// The guests' crowd control on the real enemies. A guest hits the host's enemies through their puppets, and only the
/// damage reached the host (ClientDamage): its slows, weakness/fragility, stuns and knockback landed on the puppets
/// alone, whose movement the host's snapshots overwrite, so control builds did nothing in co-op.
/// Client: postfixes on the same entries the game uses for any hit (AbnormalStatusComponent.ApplyEffects,
/// StunnableComponent.SetToRagdoll, ModifiedCharacter.AddForce) catch what lands on a puppet from the guest's own
/// skills, and send it as GuestEffect (the mirror of ProxyStatusForward). DoTs are left out: their damage already
/// travels in ClientDamage. Forces are summed per puppet and sent at 20 Hz.
/// Host: replays them on the real enemy, with the guest's stand-in (RemoteProxy) as the applier of statuses.
/// Layout after the type byte: kind u8 |
///   0 abnormal: netId u16 | flags u8 | count u8 | count x (status u8, stacks i32) | [brittle f32]
///   1 stun:     netId u16 | duration f32 | penetrate u8
///   2 forces:   count u8 | count x (netId u16, force v3, mode u8)
/// ReliableUnordered (forces: Unreliable, they are re-summed every tick anyway).
/// </summary>
public static class GuestEffects
{
    private const byte KindAbnormal = 0, KindStun = 1, KindForces = 2;
    private const int MaxPerSecond = 60;
    private const float ForceInterval = 0.05f;

    internal static bool Applying; // host: our own replays, not to be captured again

    private static readonly Dictionary<(ushort, byte), Vector3> _forces = new();
    private static float _forceTimer, _budgetTimer, _telemetryTimer;
    private static int _budget = MaxPerSecond;
    private static int _sentStatus, _sentStun, _sentForces, _recvStatus, _recvStun, _recvForces, _noEnemy, _throttled, _errors;
    private static SkillModifiers? _emptyModifiers;

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None) return;
        _budgetTimer += dt;
        if (_budgetTimer >= 1f) { _budgetTimer = 0f; _budget = MaxPerSecond; }
        if (CoopRole.IsClientSide) FlushForces(dt);
        _telemetryTimer += dt;
        if (_telemetryTimer < 10f) return;
        _telemetryTimer = 0f;
        if (_sentStatus + _sentStun + _sentForces + _recvStatus + _recvStun + _recvForces + _errors > 0)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][guestcc] {NetworkManager.Role}: sent status={_sentStatus} stun={_sentStun} forces={_sentForces} " +
                $"recv status={_recvStatus} stun={_recvStun} forces={_recvForces} noEnemy={_noEnemy} throttled={_throttled} errors={_errors}");
        _sentStatus = _sentStun = _sentForces = _recvStatus = _recvStun = _recvForces = _noEnemy = _throttled = _errors = 0;
    }

    // ---------------- client: capture ----------------

    private static bool PuppetId(Component c, out ushort netId)
    {
        netId = 0;
        if (!CoopRole.IsClientSide || !NetworkManager.HasPeer || c == null) return false;
        var e = c.GetComponent<Entity>();
        return e != null && ClientPuppets.TryGetNetId(e, out netId);
    }

    private static bool TakeBudget()
    {
        if (_budget <= 0) { _throttled++; return false; }
        _budget--;
        return true;
    }

    internal static void OnClientAbnormal(AbnormalStatusComponent target, Entity applier, in AbnormalStatusApplyData d, bool ignoreWounds, bool ignoreSynergies)
    {
        if (applier != null && ClientPuppets.TryGetNetId(applier, out _)) return; // a puppet's replayed skill, not ours
        if (!PuppetId(target, out ushort netId)) return;
        var list = ProxyStatusForward.CountStatuses(d);
        if (list.Count == 0 || !TakeBudget()) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.GuestEffect);
        w.Put(KindAbnormal);
        w.Put(netId);
        w.Put((byte)((ignoreWounds ? 1 : 0) | (ignoreSynergies ? 2 : 0)));
        w.Put((byte)list.Count);
        bool brittle = false;
        foreach (var (slot, stacks) in list)
        {
            w.Put(slot);
            w.Put(stacks);
            brittle |= slot == ProxyStatusForward.BrittleSlot;
        }
        if (brittle) w.Put(d.BrittleApplyData.ExtraDamageToTake);
        NetworkManager.Send(w, DeliveryMethod.ReliableUnordered);
        _sentStatus++;
    }

    internal static void OnClientStun(StunnableComponent target, float duration, bool penetrate)
    {
        if (duration <= 0f || !PuppetId(target, out ushort netId) || !TakeBudget()) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.GuestEffect);
        w.Put(KindStun);
        w.Put(netId);
        w.Put(duration);
        w.Put(penetrate);
        NetworkManager.Send(w, DeliveryMethod.ReliableUnordered);
        _sentStun++;
    }

    internal static void OnClientForce(ModifiedCharacter ch, Vector3 force, ForceMode mode)
    {
        if (!PuppetId(ch, out ushort netId)) return;
        var key = (netId, (byte)mode);
        _forces[key] = (_forces.TryGetValue(key, out var f) ? f : Vector3.zero) + force;
    }

    private static void FlushForces(float dt)
    {
        _forceTimer -= dt;
        if (_forceTimer > 0f || _forces.Count == 0) return;
        _forceTimer = ForceInterval;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.GuestEffect);
        w.Put(KindForces);
        int n = Mathf.Min(_forces.Count, 60);
        w.Put((byte)n);
        int i = 0;
        foreach (var kv in _forces)
        {
            if (i++ >= n) break;
            w.Put(kv.Key.Item1);
            w.Put(kv.Value.x); w.Put(kv.Value.y); w.Put(kv.Value.z);
            w.Put(kv.Key.Item2);
        }
        _forces.Clear();
        NetworkManager.Send(w, DeliveryMethod.Unreliable);
        _sentForces += n;
    }

    // ---------------- host: replay ----------------

    public static void OnGuestEffect(NetDataReader r)
    {
        byte kind = r.GetByte();
        byte from = PacketRouter.From;
        bool host = NetworkManager.Role == NetRole.Host;
        try
        {
            Applying = true;
            if (kind == KindForces)
            {
                int n = r.GetByte();
                for (int i = 0; i < n; i++)
                {
                    ushort id = r.GetUShort();
                    var force = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    var mode = (ForceMode)r.GetByte();
                    if (!host) continue;
                    _recvForces++;
                    var e = HostEnemySync.EntityOf(id);
                    if (e == null || e._character == null) { _noEnemy++; continue; }
                    e._character.AddForce(force, mode);
                }
                return;
            }
            ushort netId = r.GetUShort();
            if (kind == KindStun)
            {
                float duration = r.GetFloat();
                bool penetrate = r.GetBool();
                if (!host) return;
                _recvStun++;
                var e = HostEnemySync.EntityOf(netId);
                if (e == null || e._stunnable == null) { _noEnemy++; return; }
                e._stunnable.SetToRagdoll(duration, penetrate);
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
                ProxyStatusForward.SetStatus(ref d, slot, stacks);
                brittle |= slot == ProxyStatusForward.BrittleSlot;
            }
            if (brittle) d.BrittleApplyData.ExtraDamageToTake = r.GetFloat();
            if (!host) return;
            _recvStatus++;
            var enemy = HostEnemySync.EntityOf(netId);
            var abn = enemy != null ? enemy._abnormalStatus : null;
            if (enemy == null || abn == null) { _noEnemy++; return; }
            var applier = RemoteProxy.ProxyOf(from) ?? GameState.LocalPlayerEntity;
            if (applier == null) { _noEnemy++; return; }
            _emptyModifiers ??= new SkillModifiers();
            abn.ApplyEffects(applier, _emptyModifiers, 0f, 0f, 0, 0, SkillTreeId.None, d, (flags & 1) != 0, (flags & 2) != 0, false);
        }
        catch (Exception ex)
        {
            if (++_errors <= 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][guestcc] kind {kind} from slot {from}: {ex.Message}");
        }
        finally { Applying = false; }
    }

    /// <summary>Test bridge, client: "cmd guestcc": slow + fragility, a 1 s stun and a push on the nearest puppet.</summary>
    internal static string TestApply(string[] args)
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return "no local player";
        Entity? best = null;
        ushort bestId = 0;
        float bestD = float.MaxValue;
        foreach (var e in UnityEngine.Object.FindObjectsOfType<Entity>())
        {
            if (e == null || !ClientPuppets.TryGetNetId(e, out var id)) continue;
            float d = (e.transform.position - local.transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = e; bestId = id; }
        }
        if (best == null) return "no puppet";
        var data = new AbnormalStatusApplyData();
        ProxyStatusForward.SetStatus(ref data, 1, 5); // slow
        ProxyStatusForward.SetStatus(ref data, 2, 5); // fragility
        _emptyModifiers ??= new SkillModifiers();
        best._abnormalStatus?.ApplyEffects(local, _emptyModifiers, 0f, 0f, 0, 0, SkillTreeId.None, data, false, false, false);
        best._stunnable?.SetToRagdoll(1f, true);
        best._character?.AddForce((best.transform.position - local.transform.position).normalized * 30f, ForceMode.VelocityChange);
        return $"applied to #{bestId} {best.gameObject.name} (sent status={_sentStatus} stun={_sentStun})";
    }

    /// <summary>Test bridge, host: "cmd enemycc ID": that real enemy's slow/fragility stacks and stun state.</summary>
    internal static string TestInspect(string[] args)
    {
        if (args.Length == 0 || !ushort.TryParse(args[0], out var id)) return "usage: enemycc NETID";
        var e = HostEnemySync.EntityOf(id);
        if (e == null) return "no enemy #" + id;
        var abn = e._abnormalStatus;
        return $"#{id} {e.gameObject.name} slow={abn?.Slow?.Count} fragility={abn?.Fragility?.Count} stunned={e._stunnable?.Stunned}";
    }
}

// ---------------- Harmony patches (client capture; every one wrapped in try/catch) ----------------

[HarmonyPatch(typeof(AbnormalStatusComponent), nameof(AbnormalStatusComponent.ApplyEffects))]
public static class GuestStatusCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(AbnormalStatusComponent __instance, Entity applyingEntity, AbnormalStatusApplyData data, bool ignoreWounds, bool ignoreSynergies)
    {
        try
        {
            if (!CoopRole.IsClientSide || ProxyStatusForward.ApplyingNetworkStatus) return;
            GuestEffects.OnClientAbnormal(__instance, applyingEntity, data, ignoreWounds, ignoreSynergies);
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] GuestStatusCapturePatch: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(StunnableComponent), nameof(StunnableComponent.SetToRagdoll))]
public static class GuestStunCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(StunnableComponent __instance, float duration, bool penetrateSoftImmunity)
    {
        try
        {
            if (!CoopRole.IsClientSide) return;
            GuestEffects.OnClientStun(__instance, duration, penetrateSoftImmunity);
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] GuestStunCapturePatch: {ex.Message}"); }
    }
}

/// <summary>AddForce takes a Vector3 (12 bytes) and an enum: trampoline-safe.</summary>
[HarmonyPatch(typeof(ModifiedCharacter), nameof(ModifiedCharacter.AddForce))]
public static class GuestForceCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(ModifiedCharacter __instance, Vector3 force, ForceMode forceMode)
    {
        try
        {
            if (!CoopRole.IsClientSide) return;
            GuestEffects.OnClientForce(__instance, force, forceMode);
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] GuestForceCapturePatch: {ex.Message}"); }
    }
}
