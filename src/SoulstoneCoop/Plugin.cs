using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SoulstoneCoop;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "com.kolber.soulstonecoop";
    public const string PluginName = "Soulstone Coop";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource StaticLog = null!;
    internal static BepInEx.Configuration.ConfigEntry<bool> AutoHost = null!;

    public override void Load()
    {
        StaticLog = Log;
        Log.LogInfo($"{PluginName} {PluginVersion} loading...");

        AutoHost = Config.Bind("Debug", "AutoHost", false,
            "Start hosting automatically when a run begins (instead of pressing Insert).");
        ParseLaunchArgs();

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);

        // Force full stack traces for exceptions so we can actually tell which system is
        // throwing (ghost-clone side effects otherwise show up as a bare "NullReferenceException"
        // with no trace in the log at all).
        UnityEngine.Application.SetStackTraceLogType(UnityEngine.LogType.Exception, UnityEngine.StackTraceLogType.Full);

        // NOTE: CoopBehaviour is intentionally NOT created here. Creating a persistent
        // GameObject this early (during Chainloader startup, before Unity's scene system
        // is fully up) causes the engine to silently disable it before Update ever ticks.
        // It's created instead on the first real gameplay event (see GameplayUIManagerSetupPatch).

        Log.LogInfo($"{PluginName} loaded, harmony patches applied.");
    }

    // Launch args take precedence over the shared config file, so two instances of the game on
    // one PC can play different roles: --coop-host or --coop-connect <address>.
    internal static bool HostArg;
    internal static string? ConnectAddressArg;

    private void ParseLaunchArgs()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--coop-host") HostArg = true;
            else if (args[i] == "--coop-connect" && i + 1 < args.Length) ConnectAddressArg = args[i + 1];
        }
        if (HostArg || ConnectAddressArg != null)
            Log.LogInfo($"Launch args: host={HostArg} connect={ConnectAddressArg ?? "-"}");
    }
}

[HarmonyLib.HarmonyPatch(typeof(GameplayUIManager), nameof(GameplayUIManager.Setup))]
public static class GameplayUIManagerSetupPatch
{
    [HarmonyLib.HarmonyPostfix]
    public static void Postfix(GameplayUIManager __instance, Entity playerEntity, Camera gameplayCamera)
    {
        if (playerEntity == null)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] GameplayUIManager.Setup fired with null playerEntity");
            return;
        }

        GameState.LocalPlayerEntity = playerEntity;
        CoopBehaviour.EnsureCreated();

        var go = playerEntity.gameObject;
        var pos = go.transform.position;
        string prefabRef = playerEntity.PlayerPrefabReference != null ? playerEntity.PlayerPrefabReference.name : "<none>";
        string characterGo = playerEntity._character != null ? playerEntity._character.gameObject.name : "<none>";

        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop] playerEntity found: name={playerEntity.EntityName} id={playerEntity.Id} " +
            $"gameObject={go.name} pos={pos} isPlayer={playerEntity.IsPlayer()} " +
            $"character={characterGo} playerPrefabRef={prefabRef} " +
            $"allEntities={EntityRegistry.AllEntities?.Count ?? -1} camera={gameplayCamera?.name}");
    }
}
