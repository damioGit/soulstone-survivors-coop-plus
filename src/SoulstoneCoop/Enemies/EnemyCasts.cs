using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using SoulstoneCoop.Skills;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 4D: the host's boss / elite / special enemy casts, replayed on the client's puppet so the client sees the
/// attack: the SkillChargingTimeTrait telegraph (the area warning decal, Skill.Activate 0x5619B0 +
/// ActivateChargedAttack 0x56BE20), the projectiles and the attack animation. Visual only: the damage keeps coming
/// from the host (DamageToClient, phase 3b). On the client every effect of a puppet is blocked (damage, statuses,
/// pushback), except the host's own forwarded hits (ClientIncomingDamage.ApplyingNetworkDamage). Summon skills are
/// never replayed: the summons arrive as entities. Same mechanism as the phase 4C RemoteCaster.
/// Layout (after the type byte): netId u16 | skill key string | yaw f32 | flags u8 | [source v3] [ground v3]
/// [front v3] [direction v3] | target u8 | [target netId u16]. ReliableUnordered: a late warning is better than none,
/// and casts of different enemies don't depend on each other.
/// </summary>
public static class EnemyCasts
{
    private const byte TargetNone = 0, TargetSelf = 1, TargetEnemy = 2, TargetHostPlayer = 3, TargetClientPlayer = 4;
    private const float MinIntervalPerEnemy = 0.1f; // s: cap at 10 casts/s per enemy (multicast bursts)
    private const float ChannelReplaySeconds = 1.5f; // no channel-end message for enemies: keep the beam this long
    private const float TelemetryInterval = 5f;

    private static readonly Dictionary<ushort, float> LastSent = new();
    private static readonly Dictionary<IntPtr, Dictionary<string, Skill?>> SkillsByPuppet = new();
    private static readonly Dictionary<IntPtr, bool> SummonSkill = new();
    private static readonly HashSet<string> LoggedKeys = new();

    private static float _telemetryTimer;
    private static int _sent, _bytes, _throttled, _hostSkippedSummon, _captureErrors;
    private static int _recv, _played, _noPuppet, _noSkill, _inactive, _rejected, _errors, _skippedSummon;
    private static int _blockedDamage, _blockedStatus, _blockedPush;

    internal static void CountBlockedDamage() => _blockedDamage++;
    internal static void CountBlockedStatus() => _blockedStatus++;
    internal static void CountBlockedPush() => _blockedPush++;
    internal static void CountCaptureError() => _captureErrors++;

    internal static void ForgetPuppet(IntPtr puppetPtr) => SkillsByPuppet.Remove(puppetPtr);

    private static string KeyOf(Skill skill) => EnemyPrefabKeys.StripClone(skill.gameObject.name);

    // ---------------- host ----------------

    internal static void OnHostCast(HostEnemySync.Tracked t, Skill skill, SkillActivationParameters p)
    {
        if (!NetworkManager.HasPeer || skill == null || p == null) return;
        if ((skill.SkillType & SkillType.Summon) != 0) { _hostSkippedSummon++; return; }
        float now = Time.unscaledTime;
        if (LastSent.TryGetValue(t.NetId, out float last) && now - last < MinIntervalPerEnemy) { _throttled++; return; }
        LastSent[t.NetId] = now;

        var e = t.Entity;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.EnemyCast);
        w.Put(t.NetId);
        string key = KeyOf(skill);
        w.Put(key);
        w.Put(e.transform.eulerAngles.y);

        byte flags = 0;
        bool hasSource = ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.SourcePositionOverride, out var source);
        bool hasGround = ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetGroundPosition, out var ground);
        bool hasFront = ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetFrontPosition, out var front);
        bool hasDir = ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetDirection, out var dir);
        if (hasSource) flags |= CastMessage.FSource;
        if (hasGround) flags |= CastMessage.FGround;
        if (hasFront) flags |= CastMessage.FFront;
        if (hasDir) flags |= CastMessage.FDirection;
        if (ActivationParamsRaw.HasReference(p, ActivationParamsRaw.IsBeingChanneled)) flags |= CastMessage.FChannel;
        if (ActivationParamsRaw.HasReference(p, ActivationParamsRaw.GetTargetFrontPosition)) flags |= CastMessage.FFrontFunc;
        if (ActivationParamsRaw.HasReference(p, ActivationParamsRaw.GetTargetFrontPositionGround)) flags |= CastMessage.FGroundFunc;
        w.Put(flags);
        if (hasSource) PutV3(w, source);
        if (hasGround) PutV3(w, ground);
        if (hasFront) PutV3(w, front);
        if (hasDir) PutV3(w, dir);

        byte kind = TargetNone;
        ushort targetNetId = 0;
        var target = p.TargetEntity;
        if (target != null)
        {
            if (target.Pointer == e.Pointer) kind = TargetSelf;
            else if (GameState.LocalPlayerEntity != null && target.Pointer == GameState.LocalPlayerEntity.Pointer) kind = TargetHostPlayer;
            else if (RemoteProxy.IsProxy(target)) kind = TargetClientPlayer;
            else if (HostEnemySync.TryGetNetId(target, out targetNetId)) kind = TargetEnemy;
        }
        w.Put(kind);
        if (kind == TargetEnemy) w.Put(targetNetId);

        NetworkManager.Send(w, DeliveryMethod.ReliableUnordered);
        _sent++;
        _bytes += w.Length;
        if (LoggedKeys.Add(t.Key + "|" + key))
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][enemycasts] Host: first cast of '{key}' by '{t.Key}' (#{t.NetId}, flags={flags}, target={kind}, {skill.GetIl2CppType().Name}).");
    }

    // ---------------- client ----------------

    public static void OnEnemyCast(NetDataReader r)
    {
        ushort netId = r.GetUShort();
        string key = r.GetString();
        float yaw = r.GetFloat();
        byte flags = r.GetByte();
        Vector3 source = default, ground = default, front = default, dir = default;
        if ((flags & CastMessage.FSource) != 0) source = GetV3(r);
        if ((flags & CastMessage.FGround) != 0) ground = GetV3(r);
        if ((flags & CastMessage.FFront) != 0) front = GetV3(r);
        if ((flags & CastMessage.FDirection) != 0) dir = GetV3(r);
        byte kind = r.GetByte();
        ushort targetNetId = kind == TargetEnemy ? r.GetUShort() : (ushort)0;
        if (!CoopRole.IsClientSide) return;
        _recv++;

        try
        {
            var puppet = ClientPuppets.TryGetLive(netId);
            if (puppet == null || Time.timeScale < 0.0001f) { _noPuppet++; return; }
            var e = puppet.Entity;
            var caster = e._skillCaster;
            var skill = FindSkill(e, key);
            if (skill == null || caster == null) { _noSkill++; return; }
            if (IsSummonSkill(skill)) { _skippedSummon++; return; }
            if (!skill.isActiveAndEnabled) { _inactive++; return; }

            e.transform.rotation = Quaternion.Euler(0f, yaw, 0f); // front-of-caster skills aim with the caster's forward

            var p = new SkillActivationParameters();
            // Raw writes: the interop setters for these Nullable<Vector3> fields throw (see ActivationParamsRaw).
            if ((flags & CastMessage.FSource) != 0) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.SourcePositionOverride, source);
            if ((flags & CastMessage.FGround) != 0) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetGroundPosition, ground);
            if ((flags & CastMessage.FFront) != 0) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetFrontPosition, front);
            if ((flags & CastMessage.FDirection) != 0) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetDirection, dir);

            Entity? target = kind switch
            {
                TargetSelf => e,
                TargetEnemy => ClientPuppets.TryGetLivePuppet(targetNetId),
                TargetHostPlayer => RemoteCaster.CasterEntity, // the ghost has no Entity; its stand-in follows it
                TargetClientPlayer => GameState.LocalPlayerEntity,
                _ => null,
            };
            if (target != null)
            {
                p.TargetEntity = target;
                p.TargetTransform = target.transform;
            }

            Vector3 frontPoint = (flags & CastMessage.FFront) != 0 ? front : target != null ? target.transform.position : e.transform.position + e.transform.forward * 5f;
            Vector3 groundPoint = (flags & CastMessage.FGround) != 0 ? ground : frontPoint;
            if ((flags & CastMessage.FChannel) != 0)
            {
                float until = Time.time + ChannelReplaySeconds;
                p.IsBeingChanneled = (Func<bool>)(() => Time.time < until);
            }
            if ((flags & CastMessage.FFrontFunc) != 0) p.GetTargetFrontPosition = (Func<Skill, Vector3>)(_ => frontPoint);
            if ((flags & CastMessage.FGroundFunc) != 0) p.GetTargetFrontPositionGround = (Func<Skill, Vector3>)(_ => groundPoint);

            // IgnoreAll: no cooldown / busy / combat checks, no procs or extra multicasts.
            bool ok = caster.CastSkill(skill, p, SkillActivationOptions.IgnoreAll);
            if (ok) _played++;
            else _rejected++;
            if (LoggedKeys.Add(puppet.Key + "|" + key))
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][enemycasts] Client: first replay of '{key}' on '{puppet.Key}' (#{netId}) ok={ok} " +
                    $"charging={(skill.ChargingTrait != null)} target={kind}.");
        }
        catch (Exception ex)
        {
            _errors++;
            if (_errors <= 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][enemycasts] Replaying '{key}' on #{netId}: {ex}");
        }
    }

    private static Skill? FindSkill(Entity puppet, string key)
    {
        if (!SkillsByPuppet.TryGetValue(puppet.Pointer, out var byKey))
        {
            byKey = new Dictionary<string, Skill?>();
            foreach (var s in puppet.GetComponentsInChildren<Skill>(true))
            {
                if (s == null) continue;
                string k = KeyOf(s);
                if (!byKey.ContainsKey(k)) byKey[k] = s;
            }
            SkillsByPuppet[puppet.Pointer] = byKey;
        }
        return byKey.TryGetValue(key, out var skill) ? skill : null;
    }

    private static bool IsSummonSkill(Skill s)
    {
        if (SummonSkill.TryGetValue(s.Pointer, out bool cached)) return cached;
        bool summon = (s.SkillType & SkillType.Summon) != 0
            || s.GetComponentsInChildren<SummonEntityEffect>(true).Length > 0
            || s.GetComponentsInChildren<SummonEntityPositionalEffect>(true).Length > 0
            || s.GetComponentsInChildren<SummonEntityAndForgetPositionalEffect>(true).Length > 0;
        SummonSkill[s.Pointer] = summon;
        return summon;
    }

    public static void Tick(float unscaledDeltaTime)
    {
        if (NetworkManager.Role == NetRole.None) return;
        GodCasts.Tick(unscaledDeltaTime); // own try/catch
        ProxyStatusForward.Tick(unscaledDeltaTime); // own try/catch
        if (!GameManagerUtil.HasGameManager && (SkillsByPuppet.Count > 0 || LastSent.Count > 0))
        {
            SkillsByPuppet.Clear();
            SummonSkill.Clear();
            LastSent.Clear();
        }
        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        if (_sent + _recv + _captureErrors + _blockedDamage + _blockedStatus + _blockedPush > 0)
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][enemycasts] {NetworkManager.Role}: sent={_sent} ({_bytes} B) throttled={_throttled} hostSkippedSummon={_hostSkippedSummon} " +
                $"captureErrors={_captureErrors} recv={_recv} played={_played} noPuppet={_noPuppet} noSkill={_noSkill} inactive={_inactive} " +
                $"rejected={_rejected} errors={_errors} skippedSummon={_skippedSummon} blockedDamage={_blockedDamage} blockedStatus={_blockedStatus} blockedPush={_blockedPush}");
        _sent = _bytes = _throttled = _hostSkippedSummon = _captureErrors = 0;
        _recv = _played = _noPuppet = _noSkill = _inactive = _rejected = _errors = _skippedSummon = 0;
        _blockedDamage = _blockedStatus = _blockedPush = 0;
    }

    private static void PutV3(NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
    private static Vector3 GetV3(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());
}

// ---------------- Harmony patches (every one wrapped in try/catch) ----------------

/// <summary>Host: capture the casts of tracked enemies flagged ReplayCasts (bosses, elites, specials). Same funnel
/// and filter style as the phase 4C LocalCastCapturePatch (its own postfix on the same method).</summary>
[HarmonyPatch(typeof(SkillCasterComponent), nameof(SkillCasterComponent.CastSkill))]
public static class HostEnemyCastCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(SkillCasterComponent __instance, Skill skill, SkillActivationParameters parameters, bool __result)
    {
        try
        {
            if (!__result || !CoopRole.IsHostSide || __instance == null || !NetworkManager.HasPeer) return;
            var e = __instance.Entity;
            if (e == null || !HostEnemySync.TryGetTracked(e.Pointer, out var t) || !t.ReplayCasts || t.DiedSent) return;
            EnemyCasts.OnHostCast(t, skill, parameters);
        }
        catch (Exception ex)
        {
            EnemyCasts.CountCaptureError();
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostEnemyCastCapturePatch: {ex}");
        }
    }
}

/// <summary>Client: nothing a puppet does changes anyone's health (its replayed casts, its on-death skills, DoT ticks
/// it applied). The only puppet-attributed hits allowed are the host's forwarded ones (DamageToClient), applied with
/// ClientIncomingDamage.ApplyingNetworkDamage.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class ClientPuppetEffectDamageBlockPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(Entity attackerEntity, Entity targetEntity, float healthChange,
        ref float effectiveHealthChange, ref float absorbedDamage, ref bool isImmune)
    {
        try
        {
            if (!CoopRole.IsClientSide || attackerEntity == null || ClientIncomingDamage.ApplyingNetworkDamage) return true;
            if (!ClientPuppets.IsPuppet(attackerEntity)) return true;
            EnemyCasts.CountBlockedDamage();
            // An enemy attacker only draws a number when the target is a player (DamageOnPlayer).
            if (targetEntity != null && targetEntity.IsPlayer()) ReplicaCombatText.OnBlockedHit(targetEntity, healthChange);
            effectiveHealthChange = 0f;
            absorbedDamage = 0f;
            isImmune = false;
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientPuppetEffectDamageBlockPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Client: statuses (and the DoTs they start) applied by a puppet. Same trampoline-safe entry point as the
/// phase 4C RemoteCasterStatusBlockPatch.</summary>
[HarmonyPatch(typeof(AbnormalStatusComponent), nameof(AbnormalStatusComponent.ApplyEffects))]
public static class ClientPuppetStatusBlockPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Entity applyingEntity)
    {
        try
        {
            if (!CoopRole.IsClientSide || applyingEntity == null || !ClientPuppets.IsPuppet(applyingEntity)) return true;
            if (ProxyStatusForward.ApplyingNetworkStatus) return true; // the host's status on our proxy, forwarded
            EnemyCasts.CountBlockedStatus();
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientPuppetStatusBlockPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Client: no pushback from a puppet's replayed hits.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.CalculatePushbackForceMultiplier))]
public static class ClientPuppetPushbackBlockPatch
{
    [HarmonyPostfix]
    public static void Postfix(StatsComponent casterStats, ref float __result)
    {
        try
        {
            if (!CoopRole.IsClientSide || !ClientPuppets.IsPuppetStats(casterStats)) return;
            EnemyCasts.CountBlockedPush();
            __result = 0f;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientPuppetPushbackBlockPatch: {ex}");
        }
    }
}
