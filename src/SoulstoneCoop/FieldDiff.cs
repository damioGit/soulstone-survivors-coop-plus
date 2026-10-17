using System.Reflection;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// Live reflection-based diff: compares every public property declared directly on T between
/// a known-good "original" instance and a "clone" instance, logging any property whose
/// null-ness differs. Used to find exactly which runtime-only references (camera rigs, input
/// managers, etc.) Instantiate() + our manual re-init failed to wire up on a ghost clone -
/// something we can't see from decompiled signatures alone since Il2CppInterop stubs only
/// expose structure, not method bodies.
/// </summary>
public static class FieldDiff
{
    public static void Compare<T>(T? original, T? clone, string label) where T : class
    {
        if (original == null || clone == null)
        {
            Plugin.StaticLog.LogWarning($"[FieldDiff][{label}] skipped: original={(original == null ? "null" : "set")} clone={(clone == null ? "null" : "set")}");
            return;
        }

        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        int diffCount = 0;

        foreach (var p in props)
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;

            object? origVal, cloneVal;
            try { origVal = p.GetValue(original); }
            catch (System.Exception ex) { origVal = $"<threw {ex.GetType().Name}>"; }

            try { cloneVal = p.GetValue(clone); }
            catch (System.Exception ex) { cloneVal = $"<threw {ex.GetType().Name}>"; }

            bool origNull = origVal == null;
            bool cloneNull = cloneVal == null;

            if (origNull != cloneNull)
            {
                diffCount++;
                Plugin.StaticLog.LogWarning(
                    $"[FieldDiff][{label}] {p.Name}: original={(origNull ? "null" : "SET")} clone={(cloneNull ? "null" : "SET")}");
                continue;
            }

            // Both set: check whether they're literally the SAME underlying object rather than
            // separate per-instance copies. A shared Skill/Caster/Stats reference between the
            // original and the clone is the likely cause of the "clone locks the real player's
            // skills" symptom - Instantiate() copies the reference, not a deep clone, so if the
            // original source data isn't itself per-instance, both entities end up driving the
            // same shared state.
            if (!origNull && IsSharedReference(origVal, cloneVal, out string? sharedId))
            {
                Plugin.StaticLog.LogWarning($"[FieldDiff][{label}] {p.Name}: SHARED reference (id={sharedId})");
            }
        }

        Plugin.StaticLog.LogInfo($"[FieldDiff][{label}] done, {props.Length} properties checked, {diffCount} differ.");
    }

    private static bool IsSharedReference(object? a, object? b, out string? id)
    {
        id = null;
        if (a == null || b == null) return false;

        if (a is UnityEngine.Object uoA && b is UnityEngine.Object uoB)
        {
            int idA = uoA.GetInstanceID();
            int idB = uoB.GetInstanceID();
            if (idA == idB) { id = idA.ToString(); return true; }
            return false;
        }

        if (ReferenceEquals(a, b))
        {
            id = "ReferenceEquals";
            return true;
        }

        return false;
    }
}
