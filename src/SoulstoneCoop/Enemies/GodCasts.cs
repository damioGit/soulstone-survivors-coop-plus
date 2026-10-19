using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using SoulstoneCoop.Skills;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// God Meteors on the client (pendências). GameManagerGodMeteorsComponent (OnUpdate 0x80DA10 /
/// LaunchGodMeteorAroundPlayer 0x80D820) makes the scene's God-Entity cast its "Meteor" god skill
/// (GameManagerUtil.GetGodSkillById(god, "Meteor"), resolved in Setup 0x80DE00) on the ground within ±16 u of the
/// host player, with SkillActivationOptions.IgnoreAll. It's driven by kills and by the map affix, so only the host's
/// fires. The host captures that cast and sends GodCast (skill key + the same vectors as EnemyCast); the client plays
/// a copy of its own God-Entity's meteor skill on the 4C RemoteCaster, which is visual only (damage, statuses and
/// pushback of the caster are blocked, its numbers hidden). The damage keeps coming from the host: a meteor that hits
/// the client's proxy arrives as DamageToClient. Not cast on the client's God-Entity on purpose: its hits would be
/// real local damage, indistinguishable from the God-Entity's own.
/// Layout (after the type byte): skill key string | flags u8 | [source v3] [ground v3] [front v3] [direction v3].
/// ReliableUnordered, like EnemyCast.
/// </summary>
public static class GodCasts
{
    private const string MeteorSkillId = "Meteor"; // the literal GameManagerGodMeteorsComponent.Setup passes
    private const float RefreshInterval = 1f;
    private const float TelemetryInterval = 5f;

    private static IntPtr _godPtr, _meteorPtr;
    private static Skill? _meteor;
    private static float _refreshTimer, _telemetryTimer;
    private static bool _loggedMissing, _loggedFirst;
    private static int _lookups;
    private const int MaxLookupsPerGod = 10; // the God skills are children of the scene's God-Entity: there from the start
    private static int _sent, _bytes, _captureErrors, _recv, _played, _paused, _noMeteor, _failed, _errors;

    internal static bool IsMeteor(Skill skill) => _meteorPtr != IntPtr.Zero && skill.Pointer == _meteorPtr;
    internal static void CountCaptureError() => _captureErrors++;

    /// <summary>Both sides: find this scene's God-Entity meteor skill (the host to filter its casts, the client as the
    /// template of the copy). Called from EnemyCasts.Tick.</summary>
    public static void Tick(float unscaledDeltaTime)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try
        {
            _refreshTimer -= unscaledDeltaTime;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = RefreshInterval;
                Refresh();
            }
        }
        catch (Exception ex)
        {
            _errors++;
            if (_errors <= 3) Plugin.StaticLog.LogError($"[SoulstoneCoop][godcasts] Tick: {ex}");
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        if (_sent + _recv + _captureErrors + _errors > 0)
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][godcasts] {NetworkManager.Role}: sent={_sent} ({_bytes} B) captureErrors={_captureErrors} recv={_recv} " +
                $"played={_played} paused={_paused} noMeteor={_noMeteor} failed={_failed} errors={_errors} meteor={(_meteor != null)}");
        _sent = _bytes = _captureErrors = _recv = _played = _paused = _noMeteor = _failed = _errors = 0;
    }

    private static void Refresh()
    {
        var god = GameManagerUtil.HasGameManager ? GameManagerUtil.GameData?.GodEntity : null;
        if (god == null || !god.gameObject.activeInHierarchy)
        {
            // No match, or the God-Entity was recycled (OpenPortalAndObjective at the end of the run).
            if (_godPtr != IntPtr.Zero) { _godPtr = _meteorPtr = IntPtr.Zero; _meteor = null; _loggedMissing = _loggedFirst = false; }
            return;
        }
        if (god.Pointer == _godPtr && (_meteor != null || _lookups >= MaxLookupsPerGod)) return;
        if (god.Pointer != _godPtr) _lookups = 0;
        _lookups++;
        _godPtr = god.Pointer;
        _meteor = GameManagerUtil.GetGodSkillById(god, MeteorSkillId);
        _meteorPtr = _meteor != null ? _meteor.Pointer : IntPtr.Zero;
        if (_meteor != null)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][godcasts] God meteor skill: '{KeyOf(_meteor)}' ({_meteor.GetIl2CppType().Name}, type={_meteor.SkillType}).");
        else if (!_loggedMissing)
        {
            _loggedMissing = true;
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][godcasts] This scene's God-Entity has no 'Meteor' skill (yet): no God meteor replay until it shows up.");
        }
    }

    private static string KeyOf(Skill skill) => EnemyPrefabKeys.StripClone(skill.gameObject.name);

    // ---------------- host ----------------

    internal static void OnHostCast(Skill skill, SkillActivationParameters p)
    {
        if (p == null) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.GodCast);
        w.Put(KeyOf(skill));
        byte flags = 0;
        bool hasSource = ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.SourcePositionOverride, out var source);
        bool hasGround = ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetGroundPosition, out var ground);
        bool hasFront = ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetFrontPosition, out var front);
        bool hasDir = ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetDirection, out var dir);
        if (hasSource) flags |= CastMessage.FSource;
        if (hasGround) flags |= CastMessage.FGround;
        if (hasFront) flags |= CastMessage.FFront;
        if (hasDir) flags |= CastMessage.FDirection;
        w.Put(flags);
        if (hasSource) PutV3(w, source);
        if (hasGround) PutV3(w, ground);
        if (hasFront) PutV3(w, front);
        if (hasDir) PutV3(w, dir);
        NetworkManager.Send(w, DeliveryMethod.ReliableUnordered);
        _sent++;
        _bytes += w.Length;
        if (!_loggedFirst)
        {
            _loggedFirst = true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][godcasts] Host: first meteor sent (flags={flags}, ground={(hasGround ? ground.ToString() : "-")}).");
        }
    }

    // ---------------- client ----------------

    public static void OnGodCast(NetDataReader r)
    {
        string key = r.GetString();
        byte flags = r.GetByte();
        var m = new CastMessage { Flags = (byte)(flags & (CastMessage.FSource | CastMessage.FGround | CastMessage.FFront | CastMessage.FDirection)) };
        if (m.Has(CastMessage.FSource)) m.Source = GetV3(r);
        if (m.Has(CastMessage.FGround)) m.Ground = GetV3(r);
        if (m.Has(CastMessage.FFront)) m.Front = GetV3(r);
        if (m.Has(CastMessage.FDirection)) m.Direction = GetV3(r);
        if (!CoopRole.IsClientSide) return;
        _recv++;

        try
        {
            if (Time.timeScale < 0.0001f) { _paused++; return; }
            var template = _meteor;
            if (template == null) { _noMeteor++; return; }
            m.Key = RemoteCaster.GodKeyPrefix + key;
            m.SkillType = (int)template.SkillType;
            m.TargetKind = CastMessage.TargetNone;
            var caster = RemoteCaster.CasterOf(Players.HostSlot); // the meteors fall around the host: its stand-in plays them
            m.Yaw = caster != null ? caster.transform.eulerAngles.y : 0f;
            var result = RemoteCaster.Play(Players.HostSlot, m, template);
            if (result == RemoteCaster.PlayResult.Played) _played++;
            else _failed++;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][godcasts] Client: first meteor '{key}' (local '{KeyOf(template)}') -> {result}.");
            }
        }
        catch (Exception ex)
        {
            _errors++;
            if (_errors <= 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][godcasts] Playing '{key}': {ex}");
        }
    }

    private static void PutV3(NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
    private static Vector3 GetV3(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());
}

/// <summary>Host: capture the God-Entity's meteor casts (pointer filter: every other cast of the game returns at the
/// first comparison). Its own postfix on the same method as the 4C and 4D capture patches.</summary>
[HarmonyPatch(typeof(SkillCasterComponent), nameof(SkillCasterComponent.CastSkill))]
public static class HostGodCastCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(Skill skill, SkillActivationParameters parameters, bool __result)
    {
        try
        {
            if (!__result || skill == null || !GodCasts.IsMeteor(skill)) return;
            if (!CoopRole.IsHostSide || !NetworkManager.HasPeer) return;
            GodCasts.OnHostCast(skill, parameters);
        }
        catch (Exception ex)
        {
            GodCasts.CountCaptureError();
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostGodCastCapturePatch: {ex}");
        }
    }
}
