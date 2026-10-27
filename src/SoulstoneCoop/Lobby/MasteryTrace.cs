using System.Reflection;
using HarmonyLib;

namespace SoulstoneCoop.Lobby;

/// <summary>Logs the MasteryGameManager steps as they run (cycle flow investigation, both sides).</summary>
[HarmonyPatch]
public static class MasteryTracePatch
{
    private static readonly string[] Names =
    {
        "AdvanceCycle", "GoToNextCycleRoutine", "CastMasterySkillAndResetPlayer", "ResetPlayer", "OnPlayerEnterPortal",
        "SpawnBoss", "SpawnMainBoss", "CreateLordsObjectivesAfterTrigger", "SpawnMasteryPillar", "ActivateSpecialBossRoutine",
        "ResumeSpawningRoutine", "SaveCycleData",
    };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var n in Names)
        {
            var m = AccessTools.Method(typeof(MasteryGameManager), n);
            if (m != null) yield return m;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(MethodBase __originalMethod, MasteryGameManager __instance)
    {
        try
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] trace {__originalMethod.Name} cycle={__instance.CurrentMasteryCycle} lords={__instance._lordsKilled} t={(GameManagerUtil.GameStats != null ? GameManagerUtil.GameStats.MatchTime : -1):0.0}");
        }
        catch { }
    }
}
