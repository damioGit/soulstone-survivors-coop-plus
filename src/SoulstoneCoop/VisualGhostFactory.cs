using Il2CppInterop.Runtime;
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
/// </summary>
public static class VisualGhostFactory
{
    public static GameObject Create(Entity source, Vector3 position, Quaternion rotation, string name)
    {
        var sourceGo = source.gameObject;
        var cloneGo = UnityEngine.Object.Instantiate(sourceGo, position, rotation);
        cloneGo.name = name;

        StripToVisualOnly(cloneGo);

        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] VisualGhostFactory: created '{name}' (visual-only, no Entity).");
        return cloneGo;
    }

    private static void StripToVisualOnly(GameObject root)
    {
        // Ragdoll joints must go first: Unity refuses to remove a Rigidbody a Joint still depends on.
        foreach (var joint in root.GetComponentsInChildren<Joint>(true))
            UnityEngine.Object.DestroyImmediate(joint);

        var allComponents = root.GetComponentsInChildren<Component>(true);
        foreach (var c in allComponents)
        {
            if (c == null || IsVisualComponent(c)) continue;

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
