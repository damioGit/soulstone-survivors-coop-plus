using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Enemies;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Skills;

/// <summary>One local cast, enough to replay it on the remote caster. Layout (after the type byte):
/// key string | skillType i32 | level u8 | countA u8 | countB u8 | scale f32 | yaw f32 | flags u8 |
/// [source v3] [ground v3] [front v3] [direction v3] | targetKind u8 | [targetNetId u16 | targetSlot u8]</summary>
public sealed class CastMessage
{
    public const byte FSource = 1, FGround = 2, FFront = 4, FDirection = 8, FChannel = 16, FFrontFunc = 32, FGroundFunc = 64;
    public const byte TargetNone = 0, TargetSelf = 1, TargetEnemy = 2, TargetPlayer = 3; // phase 9: TargetPlayer carries the slot

    public string Key = "";
    public int SkillType;
    public byte Level, CountA, CountB;
    public float Scale, Yaw;
    public byte Flags;
    public Vector3 Source, Ground, Front, Direction;
    public byte TargetKind;
    public ushort TargetNetId;
    public byte TargetSlot;

    public bool Has(byte flag) => (Flags & flag) != 0;

    public void Write(NetDataWriter w)
    {
        w.Put(Key);
        w.Put(SkillType);
        w.Put(Level); w.Put(CountA); w.Put(CountB);
        w.Put(Scale); w.Put(Yaw);
        w.Put(Flags);
        if (Has(FSource)) PutV3(w, Source);
        if (Has(FGround)) PutV3(w, Ground);
        if (Has(FFront)) PutV3(w, Front);
        if (Has(FDirection)) PutV3(w, Direction);
        w.Put(TargetKind);
        if (TargetKind == TargetEnemy) w.Put(TargetNetId);
        else if (TargetKind == TargetPlayer) w.Put(TargetSlot);
    }

    public static CastMessage Read(NetDataReader r)
    {
        var m = new CastMessage
        {
            Key = r.GetString(),
            SkillType = r.GetInt(),
            Level = r.GetByte(), CountA = r.GetByte(), CountB = r.GetByte(),
            Scale = r.GetFloat(), Yaw = r.GetFloat(),
            Flags = r.GetByte(),
        };
        if (m.Has(FSource)) m.Source = GetV3(r);
        if (m.Has(FGround)) m.Ground = GetV3(r);
        if (m.Has(FFront)) m.Front = GetV3(r);
        if (m.Has(FDirection)) m.Direction = GetV3(r);
        m.TargetKind = r.GetByte();
        if (m.TargetKind == TargetEnemy) m.TargetNetId = r.GetUShort();
        else if (m.TargetKind == TargetPlayer) m.TargetSlot = r.GetByte();
        return m;
    }

    private static void PutV3(NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
    private static Vector3 GetV3(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());
}

/// <summary>
/// Phase 4C, both directions: every skill the LOCAL player casts (the SkillCasterComponent.CastSkill funnel:
/// auto-cast, CastSkillAtTarget, input, procs, chain casts, multicasts) is sent as SkillCast and replayed on
/// the other side's RemoteCaster. Visual only - the damage keeps flowing through phase 3 (ClientDamage /
/// DamageToClient). Channeled skills also get a SkillChannelEnd when the local channel stops.
/// SkillCast and SkillChannelEnd share ReliableOrdered: an end must never overtake its start.
/// </summary>
public static class CastSync
{
    private const float TelemetryInterval = 5f;

    private static IntPtr _localCasterPtr;
    private static IntPtr _localPlayerPtr;
    private static readonly HashSet<string> LoggedKeys = new();

    private sealed class OpenChannel
    {
        public string Key = "";
        public Il2CppSystem.Func<bool> Func = null!;
        public ChannelingSkill? Skill;
        public bool SeenTrue;
        public float Age;
        // #10: the live aim of the channel (the funcs the skill polls), streamed to the others while it lasts.
        public Skill? Owner;
        public Il2CppSystem.Func<Skill, Vector3>? FrontFunc, GroundFunc;
        public float AimTimer;
        public Vector3 LastFront;
    }

    private const float AimInterval = 0.1f; // 10 Hz while a channel is open: ~30 B each

    private static readonly Dictionary<string, OpenChannel> LocalChannels = new();

    // Telemetry window
    private static float _telemetryTimer;
    private static int _sent, _recv, _played, _noCaster, _noSkill, _rejected, _errors, _skippedSummon, _captureErrors;
    private static int _channelEndsSent, _channelEndsRecv, _blockedEffects, _blockedDamage, _bytesSent, _culledFar;

    internal static void CountBlockedEffects() => _blockedEffects++;
    internal static void CountBlockedDamage() => _blockedDamage++;

    /// <summary>Cheap filter for the CastSkill postfix, which runs for every caster in the game.</summary>
    internal static bool IsLocalCaster(SkillCasterComponent c) => _localCasterPtr != IntPtr.Zero && c.Pointer == _localCasterPtr;

    public static void Tick(float unscaledDeltaTime)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try
        {
            var local = GameState.LocalPlayerEntity;
            var caster = local != null ? local._skillCaster : null;
            _localCasterPtr = caster != null ? caster.Pointer : IntPtr.Zero;
            _localPlayerPtr = local != null ? local.Pointer : IntPtr.Zero;
            PollChannels(unscaledDeltaTime);
            AnnounceAuras(unscaledDeltaTime);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] CastSync.Tick: {ex}");
        }
        RemoteCaster.Tick(unscaledDeltaTime);

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        int failed = _noCaster + _noSkill + _rejected + _errors;
        int numbersHidden = ReplicaCombatText.TakeHidden();
        if (_sent + _recv + failed + _blockedEffects + _blockedDamage + _captureErrors + numbersHidden > 0)
        {
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][casts] {NetworkManager.Role}: sent={_sent} ({_bytesSent} B) recv={_recv} played={_played} failed={failed} " +
                $"(noCaster={_noCaster} noSkill={_noSkill} rejected={_rejected} error={_errors}) skippedSummon={_skippedSummon} " +
                $"captureErrors={_captureErrors} channelEnds={_channelEndsSent}/{_channelEndsRecv} openChannels={RemoteCaster.OpenChannels}/{LocalChannels.Count} " +
                $"blockedEffects={_blockedEffects} blockedDamage={_blockedDamage} numbersHidden={numbersHidden} culledFar={_culledFar} caster={RemoteCaster.Active} copies={RemoteCaster.CopyCount} " +
                $"ghostAnim={RemoteCaster.GhostAnimPlayed}/{RemoteCaster.GhostAnimPlayed + RemoteCaster.GhostAnimMissing}");
            RemoteCaster.GhostAnimPlayed = RemoteCaster.GhostAnimMissing = 0;
        }
        _sent = _recv = _played = _noCaster = _noSkill = _rejected = _errors = _skippedSummon = _captureErrors = 0;
        _channelEndsSent = _channelEndsRecv = _blockedEffects = _blockedDamage = _bytesSent = _culledFar = 0;
    }

    // ---------------- sender ----------------

    /// <summary>"ArcaneMissiles-Skill(Clone)" -> "ArcaneMissiles-Skill", the name under Resources/Skill-Prefabs.</summary>
    private static string KeyOf(Skill skill)
    {
        string name = skill.gameObject.name;
        int i = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return (i >= 0 ? name.Substring(0, i) : name).Trim();
    }

    // #10b: always-on skills (the rotating shields) are started once by the game and never go through CastSkill
    // again, so the others never saw them. Announced as a plain cast every few seconds; the receiver starts each
    // one once on that player's stand-in (RemoteCaster.Play), so late arrivals get them too.
    private const float AuraInterval = 10f;
    private static float _auraTimer = 2f;
    private static int _aurasAnnounced;

    private static void AnnounceAuras(float dt)
    {
        _auraTimer -= dt;
        if (_auraTimer > 0f) return;
        _auraTimer = AuraInterval;
        var local = GameState.LocalPlayerEntity;
        if (local == null || !NetworkManager.HasPeer || !Lobby.CoopSession.LocalInRun) return;
        foreach (var s in local.GetComponentsInChildren<Skill>(false))
        {
            if (s == null || !s.isActiveAndEnabled || s.TryCast<RotatingShieldSkill>() == null) continue;
            OnLocalCast(s, new SkillActivationParameters());
            _aurasAnnounced++;
        }
    }

    /// <summary>Test bridge, "cmd auras": always-on skills announced so far.</summary>
    internal static string TestAuras(string[] args) => $"announced={_aurasAnnounced}";

    internal static void OnLocalCast(Skill skill, SkillActivationParameters p)
    {
        if (!NetworkManager.HasPeer || skill == null || p == null) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;

        string key = KeyOf(skill);
        var type = skill.SkillType;
        // A summon would be a real Entity on the other side, dealing its own damage: not replicated.
        if ((type & SkillType.Summon) != 0) { _skippedSummon++; return; }

        var m = new CastMessage { Key = key, SkillType = (int)type, Yaw = local.transform.eulerAngles.y };
        m.Level = (byte)Mathf.Clamp(skill.SkillLevel, 0, 255);
        var proj = skill.TryCast<ProjectileSkill>();
        if (proj != null) m.CountA = ClampByte(proj.ProjectilesToFire);
        var area = skill.TryCast<AreaSkill>();
        if (area != null) m.CountA = ClampByte(area.AreasToSpawn);
        var multi = skill.TryCast<MultiProjectileSkill>();
        if (multi != null) { m.CountA = ClampByte(multi.MinProjectilesQuantity); m.CountB = ClampByte(multi.MaxProjectilesQuantity); }
        var stats = local._stats;
        if (stats != null) m.Scale = UnitUtil.GetAdjustedScale(skill.Modifiers, stats);

        // Raw reads: the interop getters for these Nullable<Vector3> fields throw (see ActivationParamsRaw).
        if (ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.SourcePositionOverride, out m.Source)) m.Flags |= CastMessage.FSource;
        if (ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetGroundPosition, out m.Ground)) m.Flags |= CastMessage.FGround;
        if (ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetFrontPosition, out m.Front)) m.Flags |= CastMessage.FFront;
        if (ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetDirection, out m.Direction)) m.Flags |= CastMessage.FDirection;
        var channel = ActivationParamsRaw.HasReference(p, ActivationParamsRaw.IsBeingChanneled) ? p.IsBeingChanneled : null;
        if (channel != null) m.Flags |= CastMessage.FChannel;
        if (ActivationParamsRaw.HasReference(p, ActivationParamsRaw.GetTargetFrontPosition)) m.Flags |= CastMessage.FFrontFunc;
        if (ActivationParamsRaw.HasReference(p, ActivationParamsRaw.GetTargetFrontPositionGround)) m.Flags |= CastMessage.FGroundFunc;

        var target = p.TargetEntity;
        if (target != null)
        {
            if (target.Pointer == _localPlayerPtr) m.TargetKind = CastMessage.TargetSelf;
            else if (CoopRole.IsHostSide && RemoteProxy.TryGetProxySlot(target, out m.TargetSlot)) m.TargetKind = CastMessage.TargetPlayer;
            else if (CoopRole.IsHostSide ? HostEnemySync.TryGetNetId(target, out m.TargetNetId) : ClientPuppets.TryGetNetId(target, out m.TargetNetId))
                m.TargetKind = CastMessage.TargetEnemy;
        }

        var w = new NetDataWriter();
        w.Put((byte)PacketType.SkillCast);
        m.Write(w);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sent++;
        _bytesSent += w.Length;

        if (channel != null && !LocalChannels.ContainsKey(key))
            LocalChannels[key] = new OpenChannel
            {
                Key = key, Func = channel, Skill = skill.TryCast<ChannelingSkill>(), Owner = skill,
                FrontFunc = (m.Flags & CastMessage.FFrontFunc) != 0 ? p.GetTargetFrontPosition : null,
                GroundFunc = (m.Flags & CastMessage.FGroundFunc) != 0 ? p.GetTargetFrontPositionGround : null,
                LastFront = m.Front,
            };

        if (LoggedKeys.Add(key))
        {
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][casts] First local cast of '{key}' ({skill.GetIl2CppType().Name}, type={type}, level={m.Level}, " +
                $"counts={m.CountA}/{m.CountB}, scale={m.Scale:F2}, flags={m.Flags}, target={m.TargetKind}).");
        }
    }

    /// <summary>A channel ends when the func the skill polls says so (the auto-cast's IsChanneling), or when the
    /// skill itself stopped channeling (max duration) after having started.</summary>
    private static void PollChannels(float dt)
    {
        if (LocalChannels.Count == 0) return;
        List<string>? ended = null;
        foreach (var ch in LocalChannels.Values)
        {
            ch.Age += dt;
            bool on;
            try
            {
                on = ch.Func.Invoke();
                if (ch.Skill != null)
                {
                    if (ch.Skill.LastBeingChanneled) ch.SeenTrue = true;
                    else if (ch.SeenTrue || ch.Age > 1f) on = false;
                }
            }
            catch { on = false; }
            if (on) { SendAim(ch, dt); continue; }
            (ended ??= new List<string>()).Add(ch.Key);
        }
        if (ended == null) return;
        foreach (var key in ended)
        {
            LocalChannels.Remove(key);
            if (!NetworkManager.HasPeer) continue;
            var w = new NetDataWriter();
            w.Put((byte)PacketType.SkillChannelEnd);
            w.Put(key);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            _channelEndsSent++;
        }
    }

    /// <summary>#10: the receivers kept aiming a channel at the point where it started; send where it aims now.</summary>
    private static void SendAim(OpenChannel ch, float dt)
    {
        if (ch.FrontFunc == null && ch.GroundFunc == null) return;
        ch.AimTimer -= dt;
        if (ch.AimTimer > 0f || !NetworkManager.HasPeer || ch.Owner == null) return;
        ch.AimTimer = AimInterval;
        try
        {
            Vector3 front = ch.FrontFunc != null ? ch.FrontFunc.Invoke(ch.Owner) : ch.LastFront;
            Vector3 ground = ch.GroundFunc != null ? ch.GroundFunc.Invoke(ch.Owner) : front;
            if ((front - ch.LastFront).sqrMagnitude < 0.04f) return; // hasn't moved 20 cm
            ch.LastFront = front;
            var w = new NetDataWriter();
            w.Put((byte)PacketType.SkillChannelAim);
            w.Put(ch.Key);
            w.Put(front.x); w.Put(front.y); w.Put(front.z);
            w.Put(ground.x); w.Put(ground.y); w.Put(ground.z);
            NetworkManager.Send(w, DeliveryMethod.Sequenced);
            _aimSent++;
        }
        catch { ch.FrontFunc = ch.GroundFunc = null; } // that skill's funcs don't work outside its own call: stop
    }

    private static int _aimSent, _aimRecv;

    public static void OnSkillChannelAim(NetDataReader reader)
    {
        string key = reader.GetString();
        var front = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        var ground = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        _aimRecv++;
        RemoteCaster.OnChannelAim(PacketRouter.From, key, front, ground);
    }

    /// <summary>Test bridge, "cmd aims": channel aim updates sent / received so far.</summary>
    internal static string TestAims(string[] args) => $"aimSent={_aimSent} aimRecv={_aimRecv} openLocal={LocalChannels.Count}";

    private static byte ClampByte(int v) => (byte)Mathf.Clamp(v, 0, 255);

    // ---------------- receiver ----------------

    public static void OnSkillCast(NetDataReader reader)
    {
        var m = CastMessage.Read(reader);
        _recv++;
        switch (RemoteCaster.Play(PacketRouter.From, m))
        {
            case RemoteCaster.PlayResult.Played: _played++; break;
            case RemoteCaster.PlayResult.NoCaster: _noCaster++; break;
            case RemoteCaster.PlayResult.NoSkill: _noSkill++; break;
            case RemoteCaster.PlayResult.Rejected: _rejected++; break;
            case RemoteCaster.PlayResult.Culled: _culledFar++; break;
            default: _errors++; break;
        }
    }

    public static void OnSkillChannelEnd(NetDataReader reader)
    {
        string key = reader.GetString();
        _channelEndsRecv++;
        RemoteCaster.OnChannelEnd(PacketRouter.From, key);
    }

    internal static void CountCaptureError() => _captureErrors++;
}

/// <summary>Capture point: the funnel every player cast goes through, with the parameters already complete
/// (CastSkillAtTarget fills them in before calling it). Runs for every caster, so the filter is a pointer compare.</summary>
[HarmonyPatch(typeof(SkillCasterComponent), nameof(SkillCasterComponent.CastSkill))]
public static class LocalCastCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(SkillCasterComponent __instance, Skill skill, SkillActivationParameters parameters, bool __result)
    {
        try
        {
            if (!__result || __instance == null || !CastSync.IsLocalCaster(__instance)) return;
            CastSync.OnLocalCast(skill, parameters);
        }
        catch (Exception ex)
        {
            CastSync.CountCaptureError();
            Plugin.StaticLog.LogError($"[SoulstoneCoop] LocalCastCapturePatch: {ex}");
        }
    }
}
