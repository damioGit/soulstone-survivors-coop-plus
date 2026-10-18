using Il2CppInterop.Runtime;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Stable cross-machine key for an enemy prefab. Both sides run EnemyPrefabsUtil.Setup for the same
/// map, so the tier lists (EnemyPrefabByPowerWithData.PrefabAddress / group PrefabAddresses) match.
/// Key formats:
///   "Some/Prefab/Address" - a PrefabAddress from the tier lists (preferred; carries PrefabData).
///   "@PrefabName"         - fallback for entities spawned outside the tier lists (bosses, summons...).
/// </summary>
public static class EnemyPrefabKeys
{
    public const string NamePrefix = "@";

    public sealed class Resolved
    {
        public Entity Prefab = null!;
        public EnemyPrefabData? PrefabData; // null when the key didn't come from the tier lists
    }

    private static readonly Dictionary<IntPtr, string> HostKeyByPrefabPtr = new();
    private static readonly Dictionary<string, Resolved> ClientCache = new();

    public static void Reset()
    {
        HostKeyByPrefabPtr.Clear();
        ClientCache.Clear();
    }

    public static string StripClone(string name)
    {
        int i = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return (i >= 0 ? name.Substring(0, i) : name).Trim();
    }

    private static string LastSegment(string address)
    {
        int i = address.LastIndexOf('/');
        return i >= 0 ? address.Substring(i + 1) : address;
    }

    private static IEnumerable<Il2CppSystem.Collections.Generic.List<EnemyPrefabByPowerWithData>?> SingleLists()
    {
        yield return EnemyPrefabsUtil.Tier1; yield return EnemyPrefabsUtil.Tier2; yield return EnemyPrefabsUtil.Tier3;
        yield return EnemyPrefabsUtil.Tier4; yield return EnemyPrefabsUtil.Tier5;
        yield return EnemyPrefabsUtil.Tier1Elite; yield return EnemyPrefabsUtil.Tier2Elite; yield return EnemyPrefabsUtil.Tier3Elite;
        yield return EnemyPrefabsUtil.Tier4Elite; yield return EnemyPrefabsUtil.Tier5Elite;
    }

    private static IEnumerable<Il2CppSystem.Collections.Generic.List<EnemyPrefabGroupByPowerWithData>?> GroupLists()
    {
        yield return EnemyPrefabsUtil.Tier1EliteGroups; yield return EnemyPrefabsUtil.Tier2EliteGroups;
        yield return EnemyPrefabsUtil.Tier3EliteGroups; yield return EnemyPrefabsUtil.Tier4EliteGroups;
        yield return EnemyPrefabsUtil.Tier5EliteGroups;
    }

    /// <summary>True once EnemyPrefabsUtil.Setup has filled at least one tier list.</summary>
    public static bool ListsReady()
    {
        foreach (var l in SingleLists())
            if (l != null && l.Count > 0) return true;
        return false;
    }

    // ---------------- host ----------------

    /// <summary>Host: key for an entity. <paramref name="prefab"/> is the prefab captured from
    /// EnemyPrefabsUtil.SpawnEnemyEntity when available.</summary>
    public static string HostKeyFor(Entity instance, Entity? prefab)
    {
        if (prefab != null)
        {
            if (HostKeyByPrefabPtr.TryGetValue(prefab.Pointer, out var cached)) return cached;
            string? key = FindAddressByLoadedPrefab(prefab) ?? FindAddressByName(StripClone(prefab.name));
            key ??= NamePrefix + StripClone(prefab.name);
            HostKeyByPrefabPtr[prefab.Pointer] = key;
            return key;
        }

        string name = StripClone(instance.gameObject.name);
        return FindAddressByName(name) ?? NamePrefix + name;
    }

    private static string? FindAddressByLoadedPrefab(Entity prefab)
    {
        foreach (var list in SingleLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var p = e._prefab; // field, not the Prefab getter: don't force-load every enemy
                if (p != null && p.Pointer == prefab.Pointer) return e.PrefabAddress;
            }
        }
        return null;
    }

    private static string? FindAddressByName(string name)
    {
        foreach (var list in SingleLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var p = e._prefab;
                if (p != null && StripClone(p.name) == name) return e.PrefabAddress;
                var addr = e.PrefabAddress;
                if (addr != null && LastSegment(addr) == name) return addr;
            }
        }
        foreach (var list in GroupLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var addrs = list[i].PrefabAddresses;
                if (addrs == null) continue;
                for (int j = 0; j < addrs.Length; j++)
                    if (addrs[j] != null && LastSegment(addrs[j]) == name) return addrs[j];
            }
        }
        return null;
    }

    // ---------------- client ----------------

    /// <summary>Client: prefab (+ PrefabData when known) for a key, or null.</summary>
    public static Resolved? ClientResolve(string key)
    {
        if (ClientCache.TryGetValue(key, out var cached) && cached.Prefab != null) return cached;

        Resolved? r = key.StartsWith(NamePrefix, StringComparison.Ordinal)
            ? ResolveByName(key.Substring(NamePrefix.Length))
            : ResolveByAddress(key) ?? ResolveByName(LastSegment(key));

        if (r != null) ClientCache[key] = r;
        return r;
    }

    private static EnemyPrefabData? PrefabDataAt(int index)
    {
        var datas = EnemyPrefabsUtil._prefabDatas;
        if (datas == null || index < 0 || index >= datas.Count) return null;
        return datas[index];
    }

    private static Resolved? ResolveByAddress(string address)
    {
        foreach (var list in SingleLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.PrefabAddress != address) continue;
                var prefab = e.Prefab; // loads it if needed
                if (prefab == null) continue;
                return new Resolved { Prefab = prefab, PrefabData = PrefabDataAt(e.PrefabDataIndex) };
            }
        }
        foreach (var list in GroupLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                var addrs = g.PrefabAddresses;
                if (addrs == null) continue;
                for (int j = 0; j < addrs.Length; j++)
                {
                    if (addrs[j] != address) continue;
                    var prefab = ResourcesUtil.Load<Entity>(address);
                    if (prefab == null) continue;
                    int dataIndex = g.PrefabDataIndex != null && j < g.PrefabDataIndex.Length ? g.PrefabDataIndex[j] : -1;
                    return new Resolved { Prefab = prefab, PrefabData = PrefabDataAt(dataIndex) };
                }
            }
        }
        return null;
    }

    private static Resolved? ResolveByName(string name)
    {
        foreach (var list in SingleLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var p = e._prefab;
                bool match = (p != null && StripClone(p.name) == name)
                          || (e.PrefabAddress != null && LastSegment(e.PrefabAddress) == name);
                if (!match) continue;
                var prefab = e.Prefab;
                if (prefab != null) return new Resolved { Prefab = prefab, PrefabData = PrefabDataAt(e.PrefabDataIndex) };
            }
        }

        // Anything else already loaded as an asset (prefabs have no valid scene).
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Entity>());
        for (int i = 0; i < all.Length; i++)
        {
            var ent = all[i]?.TryCast<Entity>();
            if (ent == null || ent.gameObject.scene.IsValid()) continue;
            if (ent.name == name) return new Resolved { Prefab = ent };
        }
        return null;
    }
}
