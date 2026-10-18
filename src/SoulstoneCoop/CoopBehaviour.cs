using Il2CppInterop.Runtime.Injection;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop;

/// <summary>
/// Injected IL2CPP MonoBehaviour that gives our managed code an Update() tick
/// inside the game's own loop (pumps LiteNetLib events, ticks player sync, debug hotkeys).
/// </summary>
public class CoopBehaviour : MonoBehaviour
{
    private static CoopBehaviour? _instance;
    private static bool _firstUpdateLogged;
    private static bool _networkEventsHooked;
    private bool _prevPageDown;
    private bool _prevInsert;
    private bool _prevDelete;

    public CoopBehaviour(System.IntPtr ptr) : base(ptr) { }

    private static bool _autoConnectDone;

    /// <summary>Creates the persistent tick object (first menu, or first run in an old save flow) and, once per game
    /// session, applies the launch args / AutoHost. Phase 6: this now runs in the main menu (MenuManager.Awake), so
    /// the connection is up before any run and survives the following ones.</summary>
    public static void EnsureCreated()
    {
        // Without this the unfocused instance stops updating and stops sending its position.
        Application.runInBackground = true;

        if (_instance == null)
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<CoopBehaviour>())
                ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();

            var go = new GameObject("SoulstoneCoop");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<CoopBehaviour>();
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] CoopBehaviour created and attached.");
        }

        if (!_networkEventsHooked)
        {
            _networkEventsHooked = true;
            NetworkManager.PeerDisconnected += DestroyGhost;
            Lobby.CoopSession.EnsureHooked();
            PauseSync.EnsureHooked();
            Enemies.HostEnemySync.EnsureHooked();
            Enemies.ClientPuppets.EnsureHooked();
            Enemies.RemoteProxy.EnsureHooked();
            Skills.RemoteCaster.EnsureHooked();
            Life.CoopLife.EnsureHooked();
        }

        if (!_autoConnectDone && NetworkManager.Role == NetRole.None)
        {
            _autoConnectDone = true;
            if (Plugin.ConnectAddressArg != null)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Auto-connect to {Plugin.ConnectAddressArg}.");
                NetworkManager.StartClient(Plugin.ConnectAddressArg);
            }
            else if (Plugin.HostArg || Plugin.AutoHost.Value)
            {
                Plugin.StaticLog.LogInfo("[SoulstoneCoop] Auto-host: starting host.");
                NetworkManager.StartHost();
            }
        }
    }

    private static int _ghostRebuilds;

    /// <summary>Phase 6: the ghost lives exactly while both players are in a run together. It used to be created on
    /// PeerConnected, which no longer happens inside a run (the connection is made in the menu).</summary>
    private static void UpdateGhostLifecycle()
    {
        var local = GameState.LocalPlayerEntity;
        bool want = Lobby.CoopSession.Connected && Lobby.CoopSession.LocalInRun && Lobby.CoopSession.RemoteInRun && local != null;
        var ghost = GameState.RemoteGhostObject;
        if (want && ghost == null)
        {
            PlayerSync.ResetForNewGhost();
            _ghostRebuilds = 0;
            var t = local!.gameObject.transform;
            var spawnPos = t.position + t.right * 2f;
            GameState.RemoteGhostObject = VisualGhostFactory.Create(local, spawnPos, t.rotation, "SoulstoneCoop_RemoteGhost");
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] Both players in the run - remote ghost created.");
        }
        else if (!want && GameState.RemoteGhostObject is not null)
        {
            // Also covers a ghost destroyed with the previous scene (Unity-null but not C#-null).
            DestroyGhost();
        }
    }

    private static void DestroyGhost()
    {
        var ghost = GameState.RemoteGhostObject;
        GameState.RemoteGhostObject = null;
        if (ghost != null)
        {
            UnityEngine.Object.Destroy(ghost);
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] Remote ghost removed.");
        }
    }

    private void Update()
    {
        try
        {
            if (!_firstUpdateLogged)
            {
                _firstUpdateLogged = true;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop] First Update() tick received.");
            }

            // The game may flip this back off (or pause via timeScale) when its window loses focus.
            Application.runInBackground = true;

            NetworkManager.PollEvents();
            try { Lobby.CoopSession.Tick(Time.unscaledDeltaTime); }
            catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopSession.Tick: {ex}"); }
            UpdateGhostLifecycle();
            Ui.CoopUi.Tick();
            PlayerSync.Tick(Time.unscaledDeltaTime);
            ClientSpawnBlock.Tick(Time.unscaledDeltaTime);
            WorldTelemetry.Tick(Time.unscaledDeltaTime);
            Enemies.HostEnemySync.Tick(Time.unscaledDeltaTime);
            Enemies.ClientPuppets.Tick(Time.unscaledDeltaTime);
            Enemies.RemoteProxy.Tick(Time.unscaledDeltaTime);
            Enemies.ClientIncomingDamage.Tick(Time.unscaledDeltaTime);
            Skills.CastSync.Tick(Time.unscaledDeltaTime);
            Loot.LootSync.Tick(Time.unscaledDeltaTime);
            Life.CoopLife.Tick(Time.unscaledDeltaTime);
            Enemies.EnemyCasts.Tick(Time.unscaledDeltaTime);
            Enemies.SpecialsDebug.Tick(Time.unscaledDeltaTime);
            RebuildGhostIfModelInactive();
            HandleDebugHotkeys();
            LogSyncStats();
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] Exception in Update(): {ex}");
        }
    }

    private float _ghostCheckTimer;

    /// <summary>
    /// The client connects at GameplayUIManager.Setup, before its own StartMatch, while the game still has the
    /// player's model child (e.g. "HoundMasterRegular(Clone)", the one with the Animator) inactive - it's turned
    /// on later. The ghost cloned at that moment keeps the model off for good: only the blue ground ring shows
    /// clone again.
    /// </summary>
    private void RebuildGhostIfModelInactive()
    {
        _ghostCheckTimer += Time.unscaledDeltaTime;
        if (_ghostCheckTimer < 1f) return;
        _ghostCheckTimer = 0f;

        var ghost = GameState.RemoteGhostObject;
        var local = GameState.LocalPlayerEntity;
        if (ghost == null || local == null || _ghostRebuilds >= 3) return;

        var ghostAnimators = ghost.GetComponentsInChildren<Animator>(true);
        var localAnimators = local.gameObject.GetComponentsInChildren<Animator>(true);
        if (ghostAnimators.Length == 0 || localAnimators.Length == 0) return;
        if (ghostAnimators[0].gameObject.activeInHierarchy || !localAnimators[0].gameObject.activeInHierarchy) return;

        _ghostRebuilds++;
        var t = ghost.transform;
        Vector3 pos = t.position;
        Quaternion rot = t.rotation;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Ghost model '{ghostAnimators[0].gameObject.name}' was cloned inactive - rebuilding the ghost (#{_ghostRebuilds}).");
        UnityEngine.Object.Destroy(ghost);
        GameState.RemoteGhostObject = VisualGhostFactory.Create(local, pos, rot, "SoulstoneCoop_RemoteGhost");
    }
    // The pause notice (phase 1) moved to the co-op banner (Ui/CoopUi): IMGUI is partly stripped in this build.

    private float _statsTimer;

    private void LogSyncStats()
    {
        if (NetworkManager.Role == NetRole.None) return;
        _statsTimer += Time.unscaledDeltaTime;
        if (_statsTimer < 2f) return;
        _statsTimer = 0f;

        var local = GameState.LocalPlayerEntity;
        var ghost = GameState.RemoteGhostObject;
        string localPos = local != null ? local.gameObject.transform.position.ToString() : "-";
        string ghostPos = ghost != null ? ghost.transform.position.ToString() : "-";
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][sync] role={NetworkManager.Role} peer={(NetworkManager.RemotePeer != null)} " +
            $"place={Lobby.CoopSession.LocalPlace}/{Lobby.CoopSession.RemotePlace} " +
            $"sent={PlayerSync.SentCount} recv={PlayerSync.ReceivedCount} lastRecv={PlayerSync.LastReceivedPosition} " +
            $"local={localPos} ghost={ghostPos} ghostActive={(ghost != null && ghost.activeInHierarchy)} " +
            $"runInBg={Application.runInBackground} timeScale={Time.timeScale} focused={Application.isFocused}");
    }

    private void HandleDebugHotkeys()
    {
        if (Keyboard.current == null) return;
        if (Ui.CoopUi.IsTyping) return; // Delete/Insert are also editing keys in the IP field

        // Insert = host, Delete = connect to localhost. Debug-only until there's a real UI.
        bool insertNow = Keyboard.current.insertKey.isPressed;
        if (insertNow && !_prevInsert)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] Insert: starting host.");
            NetworkManager.StartHost();
        }
        _prevInsert = insertNow;

        bool deleteNow = Keyboard.current.deleteKey.isPressed;
        if (deleteNow && !_prevDelete)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] Delete: connecting to 127.0.0.1.");
            NetworkManager.StartClient("127.0.0.1");
        }
        _prevDelete = deleteNow;

        // PageDown: manual single-ghost spawn/respawn, independent of networking, for quick visual checks.
        bool pageDownNow = Keyboard.current.pageDownKey.isPressed;
        if (pageDownNow && !_prevPageDown)
        {
            DebugRespawnGhost();
        }
        _prevPageDown = pageDownNow;
    }

    private static void DebugRespawnGhost()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop] PageDown: no local player entity captured yet.");
            return;
        }

        if (GameState.RemoteGhostObject != null)
        {
            UnityEngine.Object.Destroy(GameState.RemoteGhostObject);
            GameState.RemoteGhostObject = null;
        }

        var t = local.gameObject.transform;
        var spawnPos = t.position + t.right * 2f;
        GameState.RemoteGhostObject = VisualGhostFactory.Create(local, spawnPos, t.rotation, "SoulstoneCoop_DebugGhost");
    }
}
