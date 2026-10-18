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

    public static void EnsureCreated()
    {
        if (NetworkManager.Role == NetRole.None)
        {
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

        // Without this the unfocused instance stops updating and stops sending its position.
        Application.runInBackground = true;

        if (_instance != null) return;

        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<CoopBehaviour>())
            ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();

        var go = new GameObject("SoulstoneCoop");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _instance = go.AddComponent<CoopBehaviour>();
        Plugin.StaticLog.LogInfo("[SoulstoneCoop] CoopBehaviour created and attached.");

        if (!_networkEventsHooked)
        {
            _networkEventsHooked = true;
            NetworkManager.PeerConnected += OnPeerConnected;
            NetworkManager.PeerDisconnected += OnPeerDisconnected;
            PauseSync.EnsureHooked();
            Enemies.HostEnemySync.EnsureHooked();
            Enemies.ClientPuppets.EnsureHooked();
            Enemies.RemoteProxy.EnsureHooked();
            Skills.RemoteCaster.EnsureHooked();
        }
    }

    private static void OnPeerConnected()
    {
        // Whichever side we are, a peer connecting means "someone else just showed up" -
        // represent them with a ghost clone of our own local player, right next to us.
        if (GameState.RemoteGhostObject != null) return;

        var local = GameState.LocalPlayerEntity;
        if (local == null)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop] Peer connected but no local player entity yet - ghost not spawned.");
            return;
        }

        var t = local.gameObject.transform;
        var spawnPos = t.position + t.right * 2f;
        GameState.RemoteGhostObject = VisualGhostFactory.Create(local, spawnPos, t.rotation, "SoulstoneCoop_RemoteGhost");
    }

    private static void OnPeerDisconnected()
    {
        if (GameState.RemoteGhostObject != null)
        {
            UnityEngine.Object.Destroy(GameState.RemoteGhostObject);
            GameState.RemoteGhostObject = null;
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
            PlayerSync.Tick(Time.unscaledDeltaTime);
            ClientSpawnBlock.Tick(Time.unscaledDeltaTime);
            WorldTelemetry.Tick(Time.unscaledDeltaTime);
            Enemies.HostEnemySync.Tick(Time.unscaledDeltaTime);
            Enemies.ClientPuppets.Tick(Time.unscaledDeltaTime);
            Enemies.RemoteProxy.Tick(Time.unscaledDeltaTime);
            Enemies.ClientIncomingDamage.Tick(Time.unscaledDeltaTime);
            Skills.CastSync.Tick(Time.unscaledDeltaTime);
            Loot.LootSync.Tick(Time.unscaledDeltaTime);
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
    private int _ghostRebuilds;

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
    private GUIStyle? _pauseLabelStyle;
    private bool _onGuiFailed;

    private void OnGUI()
    {
        if (_onGuiFailed || !PauseSync.RemotePaused) return;
        try
        {
            if (_pauseLabelStyle == null)
            {
                _pauseLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleCenter };
                _pauseLabelStyle.normal.textColor = Color.white;
            }
            string text = PauseSync.RemoteReason == PauseReason.LevelUp ? "Parceiro escolhendo upgrade..." : "Parceiro pausou o jogo";
            GUI.Label(new Rect(0f, Screen.height * 0.15f, Screen.width, 60f), text, _pauseLabelStyle);
        }
        catch (System.Exception ex)
        {
            // IMGUI may be stripped in this build; the notice is optional, so give up on it for good.
            _onGuiFailed = true;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] OnGUI pause notice disabled: {ex.Message}");
        }
    }

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
            $"sent={PlayerSync.SentCount} recv={PlayerSync.ReceivedCount} lastRecv={PlayerSync.LastReceivedPosition} " +
            $"local={localPos} ghost={ghostPos} ghostActive={(ghost != null && ghost.activeInHierarchy)} " +
            $"runInBg={Application.runInBackground} timeScale={Time.timeScale} focused={Application.isFocused}");
    }

    private void HandleDebugHotkeys()
    {
        if (Keyboard.current == null) return;

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
