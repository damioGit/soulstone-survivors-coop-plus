using ECM2.Characters;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// Spawns a "ghost" copy of a player Entity: a real player entity (so the game treats it as a
/// legitimate second player for aggro, loot, etc.), but with its combat/input components
/// stripped entirely so it can't autocast skills with a loadout that was never set up through
/// the normal character-select flow. Its transform is meant to be driven externally (network
/// state, for now a scripted test client).
/// </summary>
public static class GhostFactory
{
    public static Entity? Create(Entity source, Vector3 position, Quaternion rotation, string name)
    {
        var sourceGo = source.gameObject;
        var cloneGo = UnityEngine.Object.Instantiate(sourceGo, position, rotation);
        cloneGo.name = name;

        var entity = cloneGo.GetComponent<Entity>();
        if (entity == null)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] GhostFactory: clone '{name}' has no Entity component.");
            UnityEngine.Object.Destroy(cloneGo);
            return null;
        }

        try
        {
            // Must run BEFORE ReInitEntity: CacheReferences()/Setup() re-derive "does this entity
            // have a PlayerSkillAutoCastComponent" etc. from what's actually attached right now.
            // Destroying first means that comes back false, so Setup() skips wiring these into
            // any shared system at all - confirmed via Ghidra that merely disabling afterwards
            // still leaves the entity "known" to Setup() and still blew up the real player's
            // casting.
            StripCombatComponents(entity);
            ReInitEntity(entity);
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] GhostFactory: setup failed for '{name}': {ex}");
        }

        RunDiagnosticDiff(source, entity);

        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] GhostFactory: created '{name}' id={entity.Id} isPlayer={entity.IsPlayer()}");
        return entity;
    }

    // Instantiate() copies ALL serialized state, including the "already initialized" flags,
    // so Awake()/Start() on the clone see HasCachedId/HasCachedReferences/HasBeenSetUp already
    // true and skip real setup, leaving a stale/duplicate Id (0) that confuses id-keyed systems
    // (this caused an infinite level-up bug the first time around). Force a clean re-init.
    private static void ReInitEntity(Entity entity)
    {
        entity.HasCachedId = false;
        entity.HasCachedReferences = false;
        entity.HasBeenSetUp = false;

        entity.CacheId();
        entity.CacheReferences();

        var character = entity._character;
        if (character != null)
        {
            character._isSetUp = false;
            character.Setup();

            // Confirmed via FieldDiff: the clone's _rotationTarget stays null (it's only ever
            // wired up for the camera-followed/active player), and ModifiedCharacter's per-frame
            // FixedUpdateAll() dereferences it unconditionally whenever _rotationMode needs a
            // target - that's the source of the continuous NullReferenceException spam. Switch
            // the ghost to a rotation mode that doesn't need an external target at all.
            character.SetRotationMode(RotationMode.OrientToMovement, null);
        }

        entity.Setup();
    }

    // DIAGNOSTIC: compares the known-good original against the freshly re-initialized clone,
    // one component type at a time, logging any property whose null-ness differs. This is how
    // we find exactly which runtime-only reference (camera rig, input manager, etc.) our
    // ReInitEntity() failed to (re)wire on the clone - something invisible in the decompiled
    // signatures since those don't show method bodies.
    private static void RunDiagnosticDiff(Entity source, Entity clone)
    {
        FieldDiff.Compare(source, clone, "Entity");
        FieldDiff.Compare(source._character, clone._character, "ModifiedCharacter");

        // PlayerAutoAim/SkillInput/SkillAutoCast/SkillSlots/PlayerCameraController are now
        // deliberately destroyed on the clone (see StripCombatComponents) - expect these to show
        // as "clone=null" rather than diffing their fields.
        FieldDiff.Compare(source._skillCaster, clone._skillCaster, "SkillCasterComponent");
        CompareSkillListContents(source._skillCaster, clone._skillCaster);
        FieldDiff.Compare(source._stats, clone._stats, "StatsComponent");
        FieldDiff.Compare(source._inventory, clone._inventory, "InventoryComponent");
        FieldDiff.Compare(source._powerUps, clone._powerUps, "PowerUpComponent");
        FieldDiff.Compare(source._passiveSkills, clone._passiveSkills, "PassiveSkillsComponent");
    }

    // Confirmed via Ghidra decompilation of GameAssembly.dll: PlayerSkillAutoCastComponent.
    // UpdateAll() walks a single SHARED iteration cursor across every registered instance each
    // frame, with no exception handling around AttemptToCastSkill(). The ghost's skill loadout
    // was never set up through the normal character-select flow, so casting on it can throw
    // partway through that shared loop - which aborts the whole frame's iteration and takes the
    // real player's own autocast down with it that tick. Just disabling these components after
    // the fact wasn't enough (confirmed by testing) - Entity.Setup() had already wired them in
    // based on what was attached at CacheReferences() time. Destroying them outright, before
    // CacheReferences()/Setup() run again, means the entity never "knows" it had them.
    private static void StripCombatComponents(Entity entity)
    {
        DestroyIfPresent(entity._playerSkillInput);
        DestroyIfPresent(entity._playerSkillAutoCast);
        DestroyIfPresent(entity._playerAutoAim);
        DestroyIfPresent(entity._playerSkillSlots);

        var cameraController = entity.gameObject.GetComponent<PlayerCameraController>();
        DestroyIfPresent(cameraController);
    }

    private static void DestroyIfPresent(UnityEngine.Object? obj)
    {
        if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
    }

    // Skill is itself a MonoBehaviour (confirmed via dump.cs) - plausibly a pooled object that
    // lives outside the player GameObject hierarchy, in which case Instantiate() never deep-
    // clones it: SkillCasterComponent.Skills (and the other skill lists) would then hold
    // references to the exact same Skill instances as the original caster. FieldDiff only
    // compares the List<Skill> *container* reference, not what's inside it, so this checks each
    // element by Unity instance ID.
    private static void CompareSkillListContents(SkillCasterComponent? source, SkillCasterComponent? clone)
    {
        if (source == null || clone == null) return;

        CompareList("Skills", source.Skills, clone.Skills);
        CompareList("SkillsOnCooldown", source.SkillsOnCooldown, clone.SkillsOnCooldown);
        CompareList("PassiveSkills", source.PassiveSkills, clone.PassiveSkills);
    }

    private static void CompareList(string label, Il2CppSystem.Collections.Generic.List<Skill>? source, Il2CppSystem.Collections.Generic.List<Skill>? clone)
    {
        if (source == null || clone == null)
        {
            Plugin.StaticLog.LogInfo($"[SkillDiff][{label}] skipped: source={(source == null ? "null" : "set")} clone={(clone == null ? "null" : "set")}");
            return;
        }

        Plugin.StaticLog.LogInfo($"[SkillDiff][{label}] source.Count={source.Count} clone.Count={clone.Count}");

        int sharedCount = 0;
        for (int i = 0; i < clone.Count; i++)
        {
            var cloneSkill = clone[i];
            if (cloneSkill == null) continue;

            for (int j = 0; j < source.Count; j++)
            {
                var sourceSkill = source[j];
                if (sourceSkill == null) continue;

                if (cloneSkill.GetInstanceID() == sourceSkill.GetInstanceID())
                {
                    sharedCount++;
                    Plugin.StaticLog.LogWarning(
                        $"[SkillDiff][{label}] clone[{i}] and source[{j}] are the SAME Skill instance (id={cloneSkill.GetInstanceID()}, name={cloneSkill.name})");
                }
            }
        }

        if (sharedCount == 0)
        {
            Plugin.StaticLog.LogInfo($"[SkillDiff][{label}] no shared Skill instances found.");
        }
    }
}
