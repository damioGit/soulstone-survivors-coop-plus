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
    // Checked in the connection handshake (phase 6): host and client must match exactly.
    public const string PluginVersion = "0.7.0";

    internal static ManualLogSource StaticLog = null!;
    internal static BepInEx.Configuration.ConfigEntry<bool> AutoHost = null!;
    internal static BepInEx.Configuration.ConfigEntry<string> LastAddress = null!;

    public override void Load()
    {
        StaticLog = Log;
        Log.LogInfo($"{PluginName} {PluginVersion} loading...");

        AutoHost = Config.Bind("Debug", "AutoHost", false,
            "Start hosting automatically when the main menu opens (instead of the Co-op panel or Insert).");
        LastAddress = Config.Bind("Coop", "LastAddress", "127.0.0.1",
            "Host address last typed in the Co-op panel (filled in automatically next time).");
        ParseLaunchArgs();

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);

        // Patched by hand: its by-value struct parameter (EntityByEnemyPrefabData) is the kind of signature
        // Il2CppInterop trampolines can choke on, and a failure here must not take the other patches down.
        // Without it, enemy keys fall back to the prefab name and tier/elite are unknown.
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(EnemyPrefabsUtil), nameof(EnemyPrefabsUtil.SpawnEnemyEntity)),
                prefix: new HarmonyMethod(typeof(Enemies.HostSpawnEnemyEntityPatch), nameof(Enemies.HostSpawnEnemyEntityPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(Enemies.HostSpawnEnemyEntityPatch), nameof(Enemies.HostSpawnEnemyEntityPatch.Postfix)));
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Could not patch EnemyPrefabsUtil.SpawnEnemyEntity (enemy keys fall back to prefab names): {ex}");
        }

        // Phase 6: the client swaps the host's Survivors affixes in while its AffixGameplayData is built. A
        // constructor, so patched by hand: if this fails, runs still start, just with the client's own affixes.
        try
        {
            var ctor = AccessTools.Constructor(typeof(AffixGameplayData),
                new[] { typeof(Il2CppSystem.Collections.Generic.List<string>), typeof(int) });
            harmony.Patch(ctor,
                prefix: new HarmonyMethod(typeof(Lobby.RunSync), nameof(Lobby.RunSync.AffixCtorPrefix)),
                postfix: new HarmonyMethod(typeof(Lobby.RunSync), nameof(Lobby.RunSync.AffixCtorPostfix)));
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Could not patch AffixGameplayData..ctor (the client keeps its own affixes): {ex}");
        }

        // Phase 7: take over the Steam callback pump (lobby invites, P2P sessions). By hand: if it fails, the game
        // keeps pumping its own callbacks and only the Steam invite is lost.
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(Steamworks.SteamAPI), nameof(Steamworks.SteamAPI.RunCallbacks)),
                prefix: new HarmonyMethod(typeof(Steam.SteamRunCallbacksPatch), nameof(Steam.SteamRunCallbacksPatch.Prefix)));
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Could not patch SteamAPI.RunCallbacks (Steam invites off, IP still works): {ex}");
        }

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

    /// <summary>Test only: pretend to be another mod version in the handshake (--coop-fake-version 0.5.0), to check
    /// the refusal with two instances that load the same DLL.</summary>
    internal static string? FakeVersionArg;

    /// <summary>Version sent and checked in the connection handshake.</summary>
    internal static string HandshakeVersion => FakeVersionArg ?? PluginVersion;

    private void ParseLaunchArgs()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--coop-host") HostArg = true;
            else if (args[i] == "--coop-connect" && i + 1 < args.Length) ConnectAddressArg = args[i + 1];
            else if (args[i] == "--coop-fake-version" && i + 1 < args.Length) FakeVersionArg = args[i + 1];
        }
        if (HostArg || ConnectAddressArg != null || FakeVersionArg != null)
            Log.LogInfo($"Launch args: host={HostArg} connect={ConnectAddressArg ?? "-"} fakeVersion={FakeVersionArg ?? "-"}");
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

/// <summary>Phase 6: the co-op object (network pump, session, UI) is created in the main menu, so the connection is
/// made there and survives runs. Awake: the menu scene is fully up by then (the Load()-time problem doesn't apply).</summary>
[HarmonyLib.HarmonyPatch(typeof(MenuManager), nameof(MenuManager.Awake))]
public static class MenuManagerAwakePatch
{
    [HarmonyLib.HarmonyPostfix]
    public static void Postfix()
    {
        try { CoopBehaviour.EnsureCreated(); }
        catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] MenuManagerAwakePatch: {ex}"); }
    }
}
