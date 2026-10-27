using HarmonyLib;
using UnityEngine;

namespace SoulstoneCoop.Loot;

/// <summary>Counters for the item (material) path of ProcessLoot: projectiles fired and items added. Test bridge.</summary>
internal static class ItemDiag
{
    internal static int FiredAt, FiredOne, Reached, Added;
    internal static string LastTargets = "";
    internal static string Info(string[] args) => $"firedAtEntities={FiredAt} firedOne={FiredOne} reached={Reached} added={Added} lastTargets=[{LastTargets}]";
}

[HarmonyPatch(typeof(ItemProjectile), nameof(ItemProjectile.FireProjectileAtEntities))]
public static class ItemDiagFireAtPatch
{
    [HarmonyPostfix]
    public static void Postfix(Il2CppSystem.Collections.Generic.List<Entity> targetEntities, ItemId itemId, int amount)
    {
        try
        {
            ItemDiag.FiredAt++;
            var names = new List<string>();
            if (targetEntities != null) foreach (var e in targetEntities) names.Add(e != null ? e.gameObject.name : "null");
            ItemDiag.LastTargets = $"{itemId}x{amount}: " + string.Join(",", names);
        }
        catch { }
    }
}

[HarmonyPatch(typeof(ItemProjectile), nameof(ItemProjectile.FireProjectile))]
public static class ItemDiagFirePatch
{
    [HarmonyPostfix] public static void Postfix() => ItemDiag.FiredOne++;
}

[HarmonyPatch(typeof(ItemProjectile), nameof(ItemProjectile.DoOnReachedTarget))]
public static class ItemDiagReachedPatch
{
    [HarmonyPostfix] public static void Postfix() => ItemDiag.Reached++;
}

[HarmonyPatch(typeof(LootUtil), nameof(LootUtil.AddItemToEntity))]
public static class ItemDiagAddPatch
{
    [HarmonyPostfix] public static void Postfix() => ItemDiag.Added++;
}
