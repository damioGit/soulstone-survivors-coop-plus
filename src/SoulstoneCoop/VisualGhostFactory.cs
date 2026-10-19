using Il2CppInterop.Runtime;
using SoulstoneCoop.Lobby;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// Spawns a purely visual stand-in for a remote player: a clone of the player GameObject with
/// every gameplay component stripped out, keeping only what's needed to see it move and animate.
///
/// Why not clone the full Entity (see GhostFactory, the earlier attempt): confirmed via Ghidra
/// decompilation of GameAssembly.dll that the game's central per-frame Update() orchestrator
/// (which drives ~150 systems, including every "UpdateAll()" loop) does an early check against a
/// single globally-tracked reference before running any of them. A second entity with
/// IsPlayer()==true destabilizes that check badly enough to abort the whole frame's update - not
/// something fixable by stripping individual components, since it's the game's own core loop,
/// not any one system we touched. A visual-only clone never has an Entity component at all, so it
/// never registers as a player and never reaches that code path.
///
/// Phase 5: the clone's character model (the PlayerPrefabReferences child) is the LOCAL player's, so it's swapped
/// for the partner's, following CharacterModelHolder.SpawnCharacter (RVA 0x5AD130) for the real player:
/// CharacterSkinCollection.GetSkinById, CharacterSkinData.LoadCharacterSkin ("Characters/&lt;skin id&gt;"), Instantiate,
/// scale by CharacterSkinSize, and the weapons from CharacterModelHolder.SpawnWeaponsAsyncRoutine, which only touches
/// the model. PlayerPrefabReferences.Setup is never called, because it writes into the Entity. The model is instantiated under an
/// inactive holder and stripped before it's ever activated, so none of its gameplay scripts run. If the partner's
/// character can't be resolved the local model stays (the pre-phase-5 ghost) and the reason is logged.
/// See docs/fase5-investigacao.md.
/// </summary>
public static class VisualGhostFactory
{
    /// <summary>The partner identity the current ghost was built for (KeyFor(CoopSession.Remote) at build time).</summary>
    public static string BuiltForKey { get; private set; } = "";
    /// <summary>The ghost shows the partner's own character (false: the local-clone fallback).</summary>
    public static bool BuiltFromRemote { get; private set; }
    /// <summary>The character model's own Animator on the current ghost (cast CrossFade, rebuild check).</summary>
    public static Animator? ModelAnimator { get; private set; }

    public static string KeyFor(PlayerInfo p) => p.Known ? $"{p.CharacterId}|{p.SkinId}|{p.WeaponId}" : "unknown";

    private static GameObject? _buildHolder;

    public static GameObject Create(Entity source, Vector3 position, Quaternion rotation, string name)
    {
        var sourceGo = source.gameObject;
        var cloneGo = UnityEngine.Object.Instantiate(sourceGo, position, rotation);
        cloneGo.name = name;

        var remote = CoopSession.Remote;
        BuiltForKey = KeyFor(remote);
        BuiltFromRemote = false;
        ModelAnimator = null;

        string what;
        try
        {
            var model = TryBuildRemoteModel(remote, out what, out var modelAnimator);
            if (model != null)
            {
                // Out with the local character (and any ascension model the game parked next to it)...
                foreach (var p in cloneGo.GetComponentsInChildren<PlayerPrefabReferences>(true))
                {
                    if (p != null) UnityEngine.Object.DestroyImmediate(p.gameObject);
                }
                // ...in with the partner's, already stripped, where SpawnCharacter puts it (child of the entity).
                model.transform.SetParent(cloneGo.transform, false);
                model.SetActive(true);
                BuiltFromRemote = true;
                ModelAnimator = modelAnimator;
            }
        }
        catch (Exception ex)
        {
            what = $"building the partner's model threw: {ex.Message}";
        }
        finally
        {
            if (_buildHolder != null) UnityEngine.Object.Destroy(_buildHolder);
            _buildHolder = null;
        }

        StripToVisualOnly(cloneGo, BuiltFromRemote ? ModelRoot(cloneGo) : null);
        if (BuiltFromRemote) DescribeModel(cloneGo);

        if (!BuiltFromRemote)
        {
            var animators = cloneGo.GetComponentsInChildren<Animator>(true);
            ModelAnimator = animators.Length > 0 ? animators[0] : null;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] VisualGhostFactory: '{name}' uses the LOCAL character as fallback ({what}).");
        }
        else
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] VisualGhostFactory: '{name}' built as the partner: {what}.");
        }

        TintBodyEffects(cloneGo);
        GhostOverhead.Attach(cloneGo, EstimateHeight(cloneGo));
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] VisualGhostFactory: created '{name}' (visual-only, no Entity, key={BuiltForKey}).");
        return cloneGo;
    }

    /// <summary>The partner's model, stripped and still under the inactive build holder (null + reason if not possible).</summary>
    private static GameObject? TryBuildRemoteModel(PlayerInfo r, out string what, out Animator? modelAnimator)
    {
        modelAnimator = null;
        if (!r.Known) { what = "partner's Hello not received yet"; return null; }
        if (r.CharacterId == 0) { what = "the Hello has no character (partner's profile not loaded?)"; return null; }
        var ct = (CharactersType)r.CharacterId;

        var skins = Global.CharacterSkinCollection;
        if (skins == null) { what = "Global.CharacterSkinCollection is null"; return null; }

        string skinNote = "";
        CharacterSkinData? skin = null;
        if (!string.IsNullOrEmpty(r.SkinId))
        {
            if (skins.TryGetCharacterSkinById(r.SkinId, out var s, out var skinCharacter) && s != null)
            {
                if (skinCharacter != ct) skinNote = $" (skin '{r.SkinId}' belongs to {skinCharacter}, using the default)";
                else if (s.IsMod()) skinNote = $" (skin '{r.SkinId}' is a mod skin, not loadable here; using the default)";
                else if (s.IsSprite2D) skinNote = $" (skin '{r.SkinId}' is 2D, needs CharacterSkin2DController; using the default)";
                else skin = s;
            }
            else skinNote = $" (skin '{r.SkinId}' not in this game's collection, using the default)";
        }
        if (skin == null)
        {
            skin = skins.GetDefaultCharacterSkinByCharacterId(ct);
            if (skin == null) { what = $"no default skin for {ct}"; return null; }
            if (skin.IsMod() || skin.IsSprite2D) { what = $"default skin of {ct} is mod/2D"; return null; }
        }

        var prefab = skin.LoadCharacterSkin(false);
        if (prefab == null) { what = $"Resources 'Characters/{skin.CharacterSkinID}' not found"; return null; }

        WeaponDefinition? weapon = null;
        var weapons = Global.WeaponCollection;
        if (weapons != null)
        {
            if (!string.IsNullOrEmpty(r.WeaponId)
                && weapons.TryGetWeaponDefinitionById(r.WeaponId, out var wd, out var weaponCharacter)
                && wd != null && weaponCharacter == ct)
                weapon = wd;
            weapon ??= weapons.GetDefaultWeaponDefinitionByCharacterId(ct);
        }

        _buildHolder = new GameObject("SoulstoneCoop_GhostModelBuild");
        _buildHolder.SetActive(false); // nothing instantiated under it gets Awake/OnEnable until we've stripped it
        var refs = UnityEngine.Object.Instantiate(prefab, _buildHolder.transform);
        if (refs == null) { what = "Instantiate of the character prefab returned null"; return null; }
        var modelGo = refs.gameObject;
        float size = skin.CharacterSkinSize > 0.01f ? skin.CharacterSkinSize : 1f;
        modelGo.transform.localScale = Vector3.one * size;

        // The fields SpawnCharacter fills before Setup. SpawnWeaponsAsyncRoutine reads WeaponDefinition (0xA8).
        refs.SpawnedCharacterId = ct;
        refs.SpawnedCharacterSkinId = skin.CharacterSkinID;
        // FirstTimeSetup (0x812600) fills this one, and SetHandOffsetToMatchDefaultRig (inside the weapon routine) loops over
        // its Followers without a null check: 0.8.0 left it empty, hence its NRE and the ghost without weapons.
        if (refs.ItemFollowBone == null) refs.ItemFollowBone = modelGo.GetComponentInChildren<ItemFollowBoneUtil>(true);
        string followNote = refs.ItemFollowBone != null ? "bone follower ok" : "NO ItemFollowBoneUtil";
        string weaponNote = weapon == null ? "no weapon definition" : SpawnWeapons(refs, weapon, skin.CharacterSkinID);

        try
        {
            var list = refs.AnimatorComponent != null ? refs.AnimatorComponent.Animators : null;
            if (list != null && list.Count > 0) modelAnimator = list[0];
        }
        catch { /* fall back below */ }
        modelAnimator ??= modelGo.GetComponentInChildren<Animator>(true);

        StripToVisualOnly(modelGo, modelGo.transform);

        // PlayerAnimator (stripped) drives these on the real player. With nothing handling OnAnimatorMove, the clips'
        // root motion would slide the model away from the ghost's root.
        foreach (var a in modelGo.GetComponentsInChildren<Animator>(true))
        {
            try { if (a != null) a.applyRootMotion = false; } catch { }
        }

        what = $"{ct} skin '{skin.CharacterSkinID}'{skinNote}, weapon '{(weapon != null ? weapon.WeaponId : "-")}' ({weaponNote}, {followNote}), size {size:0.##}";
        return modelGo;
    }

    /// <summary>The game's own weapon spawn for a character model (non-mod path): TryLoadWeaponReferences main/offhand,
    /// Instantiate under the attachments, hand offsets from the default rig, BaseWeaponScaler. The non-mod path does
    /// everything in its first step, so it's driven to the end right here. The weapon skin id is sent empty on purpose:
    /// a mod weapon skin takes ModManager's async path, and its failure branch rewrites the LOCAL profile's weapon skin.</summary>
    private static string SpawnWeapons(PlayerPrefabReferences refs, WeaponDefinition weapon, string characterSkinId)
    {
        try
        {
            refs.WeaponDefinition = weapon;
            var skinData = new SkinData();
            skinData.CharacterSkinId = characterSkinId;
            skinData.WeaponSkinId = "";
            var it = CharacterModelHolder.SpawnWeaponsAsyncRoutine(skinData, false, refs, false);
            int steps = 0;
            while (steps < 8 && it.MoveNext())
            {
                steps++;
                if (it.Current != null) return "weapon routine yielded a sub-routine, stopped";
            }
            bool main = refs.MainWeaponInstance != null, off = refs.OffhandWeaponInstance != null;
            return $"main={main} offhand={off}";
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] VisualGhostFactory: weapon spawn failed, the ghost goes without weapons: {ex.Message}");
            return "weapon spawn failed";
        }
    }

    private static readonly string[] TintProperties = { "_BaseColor", "_Color", "_TintColor", "_MainColor" };

    /// <summary>The direct child of the ghost root that holds ModelAnimator (the character model), if any.</summary>
    private static Transform? ModelRoot(GameObject root)
    {
        if (ModelAnimator == null) return null;
        var t = ModelAnimator.transform;
        while (t.parent != null && t.parent.Pointer != root.transform.Pointer) t = t.parent;
        return t.parent != null ? t : null;
    }

    /// <summary>Diagnostics for the 0.8.0 "no cast animation" report. It also sets AlwaysAnimate, so the model never stops
    /// animating because Unity thinks its renderers are off screen. Logs the model animator (controller, avatar, layers,
    /// culling) and whether each skinned mesh is bound to bones under that animator.</summary>
    private static void DescribeModel(GameObject root)
    {
        try
        {
            var a = ModelAnimator;
            if (a == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop] VisualGhostFactory: no model animator to describe."); return; }
            var culling = a.cullingMode;
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var ctrl = a.runtimeAnimatorController;
            var avatar = a.avatar;
            var layers = new List<string>();
            for (int i = 0; i < a.layerCount; i++) layers.Add($"{a.GetLayerName(i)}={a.GetLayerWeight(i):0.##}");
            int smr = 0, bound = 0;
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null) continue;
                smr++;
                var rb = r.rootBone;
                if (rb != null && rb.IsChildOf(a.transform)) bound++;
            }
            string ctrlName = ctrl != null ? ctrl.name : "NONE";
            string avatarName = avatar != null ? avatar.name : "NONE";
            bool human = avatar != null && avatar.isHuman, valid = avatar != null && avatar.isValid;
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop] VisualGhostFactory: model animator '{a.gameObject.name}' active={a.isActiveAndEnabled} initialized={a.isInitialized} " +
                $"controller='{ctrlName}' avatar='{avatarName}' human={human} valid={valid} culling={culling}->AlwaysAnimate " +
                $"speed={a.speed:0.##} layers=[{string.Join(", ", layers)}] skinnedMeshes={smr} boundUnderAnimator={bound}.");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] VisualGhostFactory: describing the model animator: {ex.Message}");
        }
    }

    /// <summary>The body effects the clone kept from the local entity (the ground ring and friends: everything that
    /// isn't the character model) get the ally color, so the ghost doesn't wear the same ring as the local player.
    /// Material instances, so the local player's shared materials are untouched. Each tinted renderer is logged.</summary>
    private static void TintBodyEffects(GameObject root)
    {
        try
        {
            var modelRoot = ModelRoot(root);
            var tinted = new List<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.TryCast<SkinnedMeshRenderer>() != null || r.TryCast<TrailRenderer>() != null || r.TryCast<LineRenderer>() != null) continue;
                if (modelRoot != null && r.transform.IsChildOf(modelRoot)) continue;
                bool any = false;
                foreach (var m in r.materials)
                {
                    if (m == null) continue;
                    foreach (var prop in TintProperties)
                    {
                        if (!m.HasProperty(prop)) continue;
                        var c = m.GetColor(prop);
                        var a = GhostOverhead.AllyColor;
                        m.SetColor(prop, new Color(a.r, a.g, a.b, c.a));
                        any = true;
                    }
                }
                if (any) tinted.Add(r.gameObject.name);
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] VisualGhostFactory: ally tint on {tinted.Count} body renderer(s): {string.Join(", ", tinted)}.");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] VisualGhostFactory: ally tint skipped: {ex.Message}");
        }
    }

    /// <summary>Top of the model's renderers above the ghost root, for the name tag.</summary>
    private static float EstimateHeight(GameObject root)
    {
        float baseY = root.transform.position.y, top = float.MinValue;
        try
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || r.TryCast<ParticleSystemRenderer>() != null || r.TryCast<TrailRenderer>() != null || r.TryCast<LineRenderer>() != null) continue;
                top = Mathf.Max(top, r.bounds.max.y);
            }
        }
        catch { /* default below */ }
        float h = top > float.MinValue ? top - baseY : 0f;
        return h < 1.2f || h > 8f ? 2.6f : h + 0.4f;
    }

    /// <summary><paramref name="keepBoneFollowersUnder"/>: the partner's model, whose ItemFollowBoneUtil stays alive. It is what
    /// makes the weapon attachments follow the hands (LateUpdateAll moves each Follower to its bone), and it only touches
    /// transforms of that same model. Elsewhere (the local-clone fallback) it goes, as before phase 5.</summary>
    private static void StripToVisualOnly(GameObject root, Transform? keepBoneFollowersUnder = null)
    {
        // Ragdoll joints must go first: Unity refuses to remove a Rigidbody a Joint still depends on.
        foreach (var joint in root.GetComponentsInChildren<Joint>(true))
            UnityEngine.Object.DestroyImmediate(joint);

        var allComponents = root.GetComponentsInChildren<Component>(true);
        foreach (var c in allComponents)
        {
            if (c == null || IsVisualComponent(c)) continue;
            if (keepBoneFollowersUnder != null && c.TryCast<ItemFollowBoneUtil>() != null && c.transform.IsChildOf(keepBoneFollowersUnder)) continue;

            try
            {
                UnityEngine.Object.DestroyImmediate(c);
            }
            catch (System.Exception ex)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop] StripToVisualOnly: failed to destroy component: {ex.Message}");
            }
        }
    }

    // GetComponentsInChildren<Component> hands back every element wrapped as the array's static
    // type (Component), so c.GetType()/`c is Transform` always see "Component" regardless of the
    // real IL2CPP runtime type - that's why the old allowlist-by-GetType() check matched nothing
    // and silently tried to destroy everything, Transform included (Unity just refuses that one
    // loudly). TryCast<T>() asks IL2CPP for the real type instead of trusting the wrapper's static type.
    private static bool IsVisualComponent(Component c)
    {
        return c.TryCast<Transform>() != null
            || c.TryCast<Animator>() != null
            || c.TryCast<SkinnedMeshRenderer>() != null
            || c.TryCast<MeshRenderer>() != null
            || c.TryCast<MeshFilter>() != null
            || c.TryCast<ParticleSystem>() != null
            || c.TryCast<ParticleSystemRenderer>() != null
            || c.TryCast<TrailRenderer>() != null
            || c.TryCast<LineRenderer>() != null;
    }
}
