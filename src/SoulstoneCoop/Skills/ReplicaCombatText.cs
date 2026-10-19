using HarmonyLib;
using UnityEngine;

namespace SoulstoneCoop.Skills;

/// <summary>
/// Hides the damage number of a replica's blocked hit (phase 4C RemoteCaster, phase 4D puppet casts). The game's
/// ChangeHealthEffect.DoApplyEffects (0x521460) calls ChangeEntityHealth and then, in the same call,
/// OverheadCombatTextPanelManager.RequestCombatText with UnitUtil.GetTargetTransform(target, Overhead). For an allied
/// attacker it shows the REQUESTED damage, not the effective one, so a blocked replica hit still drew its full number.
/// The block patches register the target's overhead transform here (only for the hits that draw a number: caster as
/// attacker, or a puppet hitting a player), and the next number requested for that transform in the same frame is
/// dropped. Real hits are untouched: the replica's request comes right after its block, synchronously, and consumes it.
/// Only RequestCombatText is patched (enum, pointer, floats and a bool: safe for the trampoline); ChangeHealthEffect.
/// DoApplyEffects is NOT, on purpose: it runs on every hit of the game and a broken trampoline there would zero all damage.
/// </summary>
internal static class ReplicaCombatText
{
    private const int MaxPending = 64;
    private static readonly List<IntPtr> Pending = new();
    private static int _frame = -1;
    private static int _hidden, _registerErrors;

    /// <summary>Numbers hidden since the last call (telemetry).</summary>
    internal static int TakeHidden() { int n = _hidden; _hidden = 0; return n; }

    internal static bool HasPending => Pending.Count > 0 && _frame == Time.frameCount;

    internal static void OnBlockedHit(Entity? target, float healthChange)
    {
        if (target == null || healthChange >= 0f) return; // heals/zero: the game draws no number for a zero heal
        try
        {
            int f = Time.frameCount;
            if (f != _frame) { Pending.Clear(); _frame = f; }
            if (Pending.Count >= MaxPending) return;
            var t = UnitUtil.GetTargetTransform(target, PlaceholderType.Overhead);
            if (t != null) Pending.Add(t.Pointer);
        }
        catch (Exception ex)
        {
            if (++_registerErrors <= 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] ReplicaCombatText.OnBlockedHit: {ex.Message}");
        }
    }

    internal static bool TryConsume(Transform? t)
    {
        if (t == null || !HasPending) return false;
        int i = Pending.IndexOf(t.Pointer);
        if (i < 0) return false;
        Pending.RemoveAt(i);
        _hidden++;
        return true;
    }
}

/// <summary>Drops the number of a replica's blocked hit (see ReplicaCombatText). Cheap when nothing is pending.</summary>
[HarmonyPatch(typeof(OverheadCombatTextPanelManager), nameof(OverheadCombatTextPanelManager.RequestCombatText))]
public static class ReplicaCombatTextPatch
{
    [HarmonyPrefix]
    public static bool Prefix(CombatTextType type, Transform transform)
    {
        try
        {
            if (!ReplicaCombatText.HasPending) return true;
            if (type != CombatTextType.Damage && type != CombatTextType.DamageOnPlayer) return true;
            return !ReplicaCombatText.TryConsume(transform);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ReplicaCombatTextPatch: {ex}");
            return true;
        }
    }
}
