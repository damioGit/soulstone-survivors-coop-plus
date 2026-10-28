using System.Reflection;
using HarmonyLib;

namespace SoulstoneCoop.Lobby;

/// <summary>Logs the Titan Hunt (Crusade) steps as they run: lobby, map selection, maps, door, blood, final bosses.</summary>
[HarmonyPatch]
public static class CrusadeTracePatch
{
    private static readonly string[] Skip = { "UpdateAll", "OnUpdate", "Update", "PostAudioEvent", "GetCollidersIDs", "GetSpawnedBossName", "GetSpawnedBossEntity", "HasHomePortal", "OnEnable", "OnDisable" };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        var types = new[]
        {
            typeof(CrusadeGameManager), typeof(CrusadeLobbyGameManager), typeof(CrusadeMapSelectionPanel), typeof(CrusadeDoorObject),
            typeof(CrusadePlayerBarrier), typeof(MapSelectionCrusadeScreenPanel),
        };
        foreach (var t in types)
            foreach (var m in AccessTools.GetDeclaredMethods(t))
            {
                if (m.IsConstructor || m.IsSpecialName || m.IsAbstract || m.ContainsGenericParameters) continue;
                if (Skip.Contains(m.Name) || m.Name.StartsWith("Native") || m.Name.StartsWith("<")) continue;
                yield return m;
            }
        var cmds = typeof(CrusadeGameManager).Assembly.GetType("SceneCommands");
        if (cmds != null)
            foreach (var m in AccessTools.GetDeclaredMethods(cmds))
                if (m.Name.StartsWith("Load") || m.Name.StartsWith("Unload")) yield return m;
        foreach (var ty in new[] { typeof(CollectableTitanBlood), typeof(CollectableSurvivorsBossPowerUp), typeof(CollectablePowerUpRequest) })
        {
            var m = AccessTools.DeclaredMethod(ty, "DoOnReachedTarget");
            if (m != null) yield return m;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            string args = __args == null || __args.Length == 0 ? "" : string.Join(", ", __args.Select(a => a?.ToString() ?? "null"));
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] trace {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({args}) scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} t={(GameManagerUtil.GameStats != null ? GameManagerUtil.GameStats.MatchTime : -1):0.0}");
        }
        catch { }
    }
}
