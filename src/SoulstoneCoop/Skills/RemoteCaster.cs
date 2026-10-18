using ECM2.Characters;
using HarmonyLib;
using SoulstoneCoop.Enemies;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Skills;

/// <summary>
/// Phase 4C, both sides: a stand-in that plays the OTHER player's skills, visual only. It's a "Creeps/Dummy"
/// (same recipe as RemoteProxy: pool + Entity.Setup) in the Summons team - allied, so its projectiles fly at
/// Monsters, but outside LootUtil's Players-team walk (the phase 3b NRE) - invisible, without a collider and
/// ignored by target searches. It follows the remote ghost every frame. Each remote skill becomes a copy:
/// the "Skill-Prefabs/&lt;key&gt;" prefab instantiated under the caster and set up with Skill.Setup(caster)
/// (no AddSkill, so it never casts by itself). Every game effect of the caster is blocked (RemoteCasterDamageBlockPatch,
/// RemoteCasterStatusBlockPatch, RemoteCasterPushbackBlockPatch): damage stays on the phase 3 paths.
/// See docs/fase4c-investigacao.md.
/// </summary>
public static class RemoteCaster
{
    private const string CasterPrefabPath = "Creeps/Dummy";
    private const string SkillPrefabFolder = "Skill-Prefabs/";

    private static Entity? _caster;
    private static IntPtr _casterPtr;
    private static IntPtr _casterStatsPtr;
    private static ModifiedCharacter? _casterCharacter;
    private static Team _originalTeam;
    private static VfxReferences? _originalVfxOnSetup;
    private static bool _matchStarted;
    private static bool _hooked;
    private static float _retryTimer;
    private static int _createFailures;

    private sealed class Copy
    {
        public string Key = "";
        public Skill Skill = null!;
        public IntPtr Ptr;
        public Il2CppSystem.Func<bool>? ChannelFunc;
        public Il2CppSystem.Func<Skill, Vector3>? FrontFunc, GroundFunc;
        public Vector3 Front, Ground;
        public float AppliedScale = -1f;
        public string Kind = "";
    }

    private static readonly Dictionary<string, Copy> Copies = new();
    private static readonly HashSet<string> FailedKeys = new();
    private static readonly Dictionary<string, bool> Channeling = new();

    public static bool Active => _caster != null;
    internal static Entity? CasterEntity => _caster; // phase 4D: stand-in target for enemy casts aimed at the host player
    public static int CopyCount => Copies.Count;
    public static int OpenChannels { get { int n = 0; foreach (var on in Channeling.Values) if (on) n++; return n; } }

    internal static bool IsCaster(Entity? e) => e != null && _casterPtr != IntPtr.Zero && e.Pointer == _casterPtr;
    internal static bool IsCasterStats(StatsComponent? s) => s != null && _casterStatsPtr != IntPtr.Zero && s.Pointer == _casterStatsPtr;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerDisconnected += () => RemoveCaster("peer disconnected");
    }

    public static void OnMatchStarted()
    {
        // A new match: the caster and its copies from the previous scene are gone with it.
        ForgetCaster();
        _matchStarted = true;
        _createFailures = 0;
    }

    public static void Tick(float unscaledDeltaTime)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try
        {
            if (!GameManagerUtil.HasGameManager) { _matchStarted = false; ForgetCaster(); }
            else if (_casterPtr != IntPtr.Zero && (_caster == null || !_caster.gameObject.activeInHierarchy))
            {
                Plugin.StaticLog.LogWarning("[SoulstoneCoop][casts] Remote caster went inactive/destroyed - will recreate.");
                ForgetCaster();
            }

            bool haveRemote = RemoteProxy.TryGetRemotePosition(out var pos, out var rot);
            if (_caster == null)
            {
                _retryTimer -= unscaledDeltaTime;
                if (haveRemote && _matchStarted && GameState.LocalPlayerEntity != null && _retryTimer <= 0f && _createFailures < 5)
                {
                    _retryTimer = 2f;
                    CreateCaster(pos, rot);
                }
            }
            else if (haveRemote)
            {
                if (_casterCharacter != null) _casterCharacter.SetPosition(pos);
                else _caster.transform.position = pos;
                _caster.transform.rotation = rot;
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] RemoteCaster.Tick: {ex}");
        }
    }

    // ---------------- the caster entity ----------------

    private static void CreateCaster(Vector3 pos, Quaternion rot)
    {
        Entity? inst = null;
        try
        {
            var prefab = ResourcesUtil.Load<Entity>(CasterPrefabPath);
            if (prefab == null) throw new Exception($"ResourcesUtil.Load<Entity>(\"{CasterPrefabPath}\") returned null");
            ResourcesUtil.GetPrefabInstance<Entity>(prefab, pos, rot, out inst);
            if (inst == null) throw new Exception("GetPrefabInstance returned null");

            var team = inst._team ?? inst.GetComponent<TeamComponent>();
            if (team == null) throw new Exception("Dummy has no TeamComponent");
            _originalTeam = team.Team;
            team.Team = Team.Summons;
            inst.CacheReferences();
            var stats = inst._stats;
            if (stats != null && stats.Stats != null) stats.Stats.CopyStats(stats.Stats);
            if (stats != null && stats.MultiplicativeStats != null) stats.MultiplicativeStats.SetToOne();

            inst.CanBeRecycledByDistance = false;
            inst.IgnoredByUnitLimit = true;
            inst.IgnoredByEntitySearchQueries = true; // nobody targets it: it's only a launch point
            inst.HasSpawnSound = false;
            _originalVfxOnSetup = inst.VfxOnSetup;
            inst.VfxOnSetup = null!;
            var health = inst._health;
            if (health != null)
            {
                health.AvoidCreatingHealthBar = true;
                health.IgnoreMinimapTracking = true;
            }

            inst.Setup();

            var ai = inst.GetComponent<BasicAIComponent>();
            if (ai != null) { ai.StopRefreshingTarget(); ai.enabled = false; }
            var staticAi = inst.GetComponent<StaticAIComponent>();
            if (staticAi != null) staticAi.enabled = false;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            var col = inst._physical != null ? inst._physical.Collider : null;
            if (col != null) col.enabled = false; // no body: enemies walk through, their hits can't find it

            _caster = inst;
            _casterPtr = inst.Pointer;
            _casterStatsPtr = stats != null ? stats.Pointer : IntPtr.Zero;
            _casterCharacter = inst._character;
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][casts] Created remote caster '{inst.gameObject.name}' at {pos}: team {_originalTeam}->{team.Team} " +
                $"isPlayer={inst.IsPlayer()} skillCaster={(inst._skillCaster != null)} animator={inst.Animator.HasReference} " +
                $"stats={(stats != null)} areaStat={(stats?.Stats != null ? stats.Stats.AreaModifier : -1f)}");
        }
        catch (Exception ex)
        {
            _createFailures++;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] CreateCaster failed (#{_createFailures}): {ex}");
            if (inst != null && _caster == null)
            {
                try { RestoreForPool(inst); inst.Recycle(); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Scene gone: just drop the references (the objects went with it).</summary>
    private static void ForgetCaster()
    {
        _caster = null;
        _casterStatsPtr = IntPtr.Zero;
        _casterPtr = IntPtr.Zero;
        _casterCharacter = null;
        Copies.Clear();
        FailedKeys.Clear();
        Channeling.Clear();
    }

    private static void RemoveCaster(string reason)
    {
        var e = _caster;
        var copies = Copies.Values.ToArray();
        ForgetCaster();
        if (e == null) return;
        try
        {
            // The Dummy goes back to the pool: our skill copies must not ride along.
            foreach (var c in copies) DestroyCopy(c);
            RestoreForPool(e);
            e.Recycle();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Remote caster recycled ({reason}).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] RemoveCaster: {ex.Message}");
        }
    }

    private static void DestroyCopy(Copy c)
    {
        try
        {
            if (c.Skill == null) return;
            c.Skill.CancelExecution();
            c.Skill.Recycle();
            UnityEngine.Object.Destroy(c.Skill.gameObject);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Destroying copy '{c.Key}': {ex.Message}");
        }
    }

    private static void RestoreForPool(Entity e)
    {
        var team = e._team ?? e.GetComponent<TeamComponent>();
        if (team != null) team.Team = _originalTeam;
        if (_originalVfxOnSetup != null) e.VfxOnSetup = _originalVfxOnSetup;
        e.IgnoredByEntitySearchQueries = false;
        foreach (var r in e.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
        var col = e._physical != null ? e._physical.Collider : null;
        if (col != null) col.enabled = true;
    }

    // ---------------- skill copies ----------------

    private static Copy? GetOrCreateCopy(string key, SkillType skillType)
    {
        if (Copies.TryGetValue(key, out var c))
        {
            if (c.Skill != null) return c;
            Copies.Remove(key);
        }
        if (FailedKeys.Contains(key) || _caster == null) return null;

        try
        {
            var inst = CreateSkillInstance(key, skillType, out string how);
            inst.FillPools(3);

            c = new Copy { Key = key, Skill = inst, Ptr = inst.Pointer, Kind = inst.GetIl2CppType().Name };
            Copies[key] = c;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Copy '{key}' ({c.Kind}, type={skillType}, via {how}) set up on the remote caster.");
            return c;
        }
        catch (Exception ex)
        {
            FailedKeys.Add(key);
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] Could not copy skill '{key}' (won't retry this match): {ex}");
            return null;
        }
    }

    /// <summary>The game's own "give this entity a skill" helper first: UnitUtil.SetupExtraSkillForEntity does
    /// LoadSkillPrefab ("Skill-Prefabs/&lt;id&gt;-Skill") + Instantiate under the caster + Skill.Setup with the caster's
    /// references, and has no Nullable in its signature. It needs the power-up in this match's MatchPowerUpData;
    /// when it isn't there (another character's skill), fall back to doing those steps by hand.</summary>
    private static Skill CreateSkillInstance(string key, SkillType skillType, out string how)
    {
        string id = key.EndsWith("-Skill", StringComparison.Ordinal) ? key.Substring(0, key.Length - "-Skill".Length) : key;
        Skill? inst = null;
        try { inst = UnitUtil.SetupExtraSkillForEntity(_caster!, id, new SkillModifiers(), 0f); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] SetupExtraSkillForEntity('{id}'): {ex.Message}"); }
        if (inst != null && inst.IsSetUp) { how = "SetupExtraSkillForEntity"; return inst; }

        if (inst == null)
        {
            var prefab = ResourcesUtil.Load<Skill>(SkillPrefabFolder + key);
            if (prefab == null) throw new Exception($"no power-up '{id}' and no prefab at Resources '{SkillPrefabFolder}{key}'");
            inst = UnityEngine.Object.Instantiate(prefab, _caster!.transform);
            if (inst == null) throw new Exception("Instantiate returned null");
        }
        var phys = _caster!.Physical;
        Collider? col = phys.HasReference && phys.Reference != null ? phys.Reference.Collider : null;
        inst.Setup(_caster, skillType, _caster.Team, _caster.Stats, _caster.PassiveSkills, _caster.Animator, col!,
            new Il2CppSystem.Nullable<float>(), new Il2CppSystem.Nullable<bool>());
        how = "manual Setup";
        return inst;
    }

    /// <summary>Il2Cpp delegates for the funcs some skills require (channeling, updated front position). Built
    /// lazily and guarded: if the conversion fails only those skills are affected.</summary>
    private static bool TryGetFuncs(Copy c, bool channel, bool front, bool ground)
    {
        try
        {
            var copy = c;
            if (channel && c.ChannelFunc == null)
                c.ChannelFunc = (Func<bool>)(() => _caster != null && Channeling.TryGetValue(copy.Key, out bool on) && on);
            if (front && c.FrontFunc == null) c.FrontFunc = (Func<Skill, Vector3>)(_ => copy.Front);
            if (ground && c.GroundFunc == null) c.GroundFunc = (Func<Skill, Vector3>)(_ => copy.Ground);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] Delegates for '{c.Key}': {ex.Message}");
            return false;
        }
    }

    // ---------------- playing a cast ----------------

    public enum PlayResult { Played, NoCaster, NoSkill, Rejected, Error }

    internal static PlayResult Play(CastMessage m)
    {
        if (_caster == null || _caster._skillCaster == null) return PlayResult.NoCaster;
        var c = GetOrCreateCopy(m.Key, (SkillType)m.SkillType);
        if (c == null) return PlayResult.NoSkill;

        try
        {
            var tr = _caster.transform;
            tr.rotation = Quaternion.Euler(0f, m.Yaw, 0f); // front-of-caster skills aim with the caster's forward
            ApplyFidelity(c, m);

            var p = new SkillActivationParameters();
            // Raw writes: the interop setters for these Nullable<Vector3> fields throw (see ActivationParamsRaw).
            if (m.Has(CastMessage.FSource)) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.SourcePositionOverride, m.Source);
            if (m.Has(CastMessage.FGround)) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetGroundPosition, m.Ground);
            if (m.Has(CastMessage.FFront)) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetFrontPosition, m.Front);
            if (m.Has(CastMessage.FDirection)) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetDirection, m.Direction);

            var target = ResolveTarget(m);
            if (target != null)
            {
                p.TargetEntity = target;
                p.TargetTransform = target.transform;
            }

            c.Front = m.Has(CastMessage.FFront) ? m.Front : tr.position + tr.forward * 5f;
            c.Ground = m.Has(CastMessage.FGround) ? m.Ground : c.Front;
            bool wantChannel = m.Has(CastMessage.FChannel), wantFront = m.Has(CastMessage.FFrontFunc), wantGround = m.Has(CastMessage.FGroundFunc);
            if ((wantChannel || wantFront || wantGround) && !TryGetFuncs(c, wantChannel, wantFront, wantGround)) return PlayResult.Error;
            if (wantChannel)
            {
                Channeling[m.Key] = true;
                p.IsBeingChanneled = c.ChannelFunc!;
            }
            if (wantFront) p.GetTargetFrontPosition = c.FrontFunc!;
            if (wantGround) p.GetTargetFrontPositionGround = c.GroundFunc!;

            // IgnoreAll: no cooldown, busy or combat checks, and no procs / chain casts / extra multicasts -
            // those arrive as their own cast messages from the other side.
            bool ok = _caster._skillCaster.CastSkill(c.Skill, p, SkillActivationOptions.IgnoreAll);
            if (!ok) return PlayResult.Rejected;
            PlayGhostAnimation(c.Skill);
            return PlayResult.Played;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] Playing '{m.Key}': {ex}");
            return PlayResult.Error;
        }
    }

    internal static void OnChannelEnd(string key)
    {
        if (Channeling.ContainsKey(key)) Channeling[key] = false;
    }

    private static Entity? ResolveTarget(CastMessage m)
    {
        switch (m.TargetKind)
        {
            case CastMessage.TargetSelf:
                return _caster;
            case CastMessage.TargetEnemy:
                if (CoopRole.IsHostSide) return HostEnemySync.TryGet(m.TargetNetId, out var e) ? e : null;
                return ClientPuppets.TryGetLivePuppet(m.TargetNetId);
            case CastMessage.TargetOtherPlayer:
                return GameState.LocalPlayerEntity;
            default:
                return null;
        }
    }

    /// <summary>Level, projectile/area counts and the sender's area scale (UnitUtil.GetAdjustedScale), put
    /// into the copy's own modifiers so the receiver's math lands on the same size.</summary>
    private static void ApplyFidelity(Copy c, CastMessage m)
    {
        var s = c.Skill;
        if (m.Level > 0) s.SkillLevel = m.Level;
        if (m.CountA > 0)
        {
            var proj = s.TryCast<ProjectileSkill>();
            if (proj != null) proj.ProjectilesToFire = m.CountA;
            var area = s.TryCast<AreaSkill>();
            if (area != null) area.AreasToSpawn = m.CountA;
            var multi = s.TryCast<MultiProjectileSkill>();
            if (multi != null && m.CountB >= m.CountA)
            {
                multi.MinProjectilesQuantity = m.CountA;
                multi.MaxProjectilesQuantity = m.CountB;
            }
        }

        if (m.Scale <= 0f || Mathf.Abs(m.Scale - c.AppliedScale) < 0.001f) return;
        var trait = s.Modifiers;
        var mods = trait != null ? trait.Modifiers : null;
        var stats = _caster!._stats;
        if (mods == null || stats == null) return;
        // scale = (statArea + modArea) * k  (k = multiplicative stat, runes, shrink, 1 + modAreaMult).
        // Measure the caster's s0 and k with modArea = 0 / 1 and AreaMult = 0, then solve for modArea.
        mods.AreaMultiplicativeModifier = 0f;
        mods.AreaModifier = 0f;
        float s0 = UnitUtil.GetAdjustedScale(trait, stats);
        mods.AreaModifier = 1f;
        float k = UnitUtil.GetAdjustedScale(trait, stats) - s0;
        mods.AreaModifier = k > 0.0001f ? (m.Scale - s0) / k : 0f;
        c.AppliedScale = m.Scale;
    }

    private static GameObject? _animGhost;
    private static Animator? _ghostAnimator;

    /// <summary>Bonus: the cast animation on the ghost (SkillAnimationTrait.Activate does a CrossFade on the
    /// caster's AnimatorComponent; the ghost only has a plain Animator).</summary>
    private static void PlayGhostAnimation(Skill s)
    {
        try
        {
            SkillAnimationTrait? trait = s.TryCast<ProjectileSkill>()?.AnimationTrait
                ?? s.TryCast<AreaSkill>()?.AnimationTrait
                ?? s.TryCast<MultiProjectileSkill>()?.AnimationTrait;
            if (trait == null || string.IsNullOrEmpty(trait.AnimationName)) return;
            var ghost = GameState.RemoteGhostObject;
            if (ghost == null) return;
            if (_animGhost == null || _animGhost.Pointer != ghost.Pointer || _ghostAnimator == null)
            {
                _animGhost = ghost;
                var animators = ghost.GetComponentsInChildren<Animator>(true);
                _ghostAnimator = animators.Length > 0 ? animators[0] : null;
            }
            var anim = _ghostAnimator;
            if (anim == null || !anim.isActiveAndEnabled) return;
            int layer = Mathf.Clamp(trait.AnimationLayer, 0, Mathf.Max(0, anim.layerCount - 1));
            if (!anim.HasState(layer, Animator.StringToHash(trait.AnimationName))) return; // the ghost may be another character
            anim.CrossFade(trait.AnimationName, trait.Fade, layer);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Ghost animation: {ex.Message}");
        }
    }
}

// ---------------- Harmony patches: the remote caster is visual only (every one wrapped in try/catch) ----------------

// NOT patched on purpose: Skill.ApplyEffects / DoApplyEffects and SkillEffect.ApplyEffect. ApplyEffects and
// DoApplyEffects end in a by-value Nullable<int>: an 8-byte struct, which the x64 ABI passes in a register, but the
// Il2CppInterop native->managed trampoline reads it as a pointer (NRE in Buffer.Memmove), so the original never
// ran and NO skill applied any effect anymore (fase 4C, first test). Effects are blocked downstream instead,
// at entry points whose structs are all > 8 bytes (passed by pointer, like the patches that already work).

/// <summary>Safety net and main damage block: nothing the remote caster does changes anyone's health, and
/// nothing changes its own. The phase 3 ChangeEntityHealth patches also skip it (no ClientDamage from it).
/// DoT ticks keep the applying entity as attacker, but statuses are blocked at the source below anyway.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class RemoteCasterDamageBlockPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(Entity attackerEntity, Entity targetEntity,
        ref float effectiveHealthChange, ref float absorbedDamage, ref bool isImmune)
    {
        try
        {
            if (!RemoteCaster.IsCaster(attackerEntity) && !RemoteCaster.IsCaster(targetEntity)) return true;
            CastSync.CountBlockedDamage();
            effectiveHealthChange = 0f;
            absorbedDamage = 0f;
            isImmune = false;
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] RemoteCasterDamageBlockPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Every status a skill effect applies (burn, poison, bleed, slow, doom... and the DoT ticks that follow)
/// enters through here with the applying entity. Signature is safe for the trampoline: AbnormalStatusApplyData
/// is 0x6C bytes (passed by pointer), the rest are pointers, floats, ushorts, an enum and bools.</summary>
[HarmonyPatch(typeof(AbnormalStatusComponent), nameof(AbnormalStatusComponent.ApplyEffects))]
public static class RemoteCasterStatusBlockPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Entity applyingEntity)
    {
        try
        {
            if (!RemoteCaster.IsCaster(applyingEntity)) return true;
            CastSync.CountBlockedEffects();
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] RemoteCasterStatusBlockPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Pushback scales with this multiplier, computed from the caster's stats: zero for the remote caster,
/// so replayed hits don't shove the host's enemies (or the client's puppets against their snapshots).</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.CalculatePushbackForceMultiplier))]
public static class RemoteCasterPushbackBlockPatch
{
    [HarmonyPostfix]
    public static void Postfix(StatsComponent casterStats, ref float __result)
    {
        try
        {
            if (!RemoteCaster.IsCasterStats(casterStats)) return;
            CastSync.CountBlockedEffects();
            __result = 0f;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] RemoteCasterPushbackBlockPatch: {ex}");
        }
    }
}
