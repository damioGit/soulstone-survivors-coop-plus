using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Lobby;

/// <summary>Where a player is, as the partner sees it (LobbyState packet).</summary>
public enum LobbyPlace : byte
{
    Menu = 0,
    Loading = 1, // between RunStart and the local StartMatch
    InRun = 2,   // from StartMatch until the scene's game manager is gone (end screen included)
    Transition = 3, // still in the run's scene, but a portal (Endless/BossRush) or Restart was taken: the next scene is coming
}

/// <summary>Identity of one player: what the Hello packet carries. Phase 5 builds the ghost from this.</summary>
public sealed class PlayerInfo
{
    public bool Known;
    public string Name = "";
    public byte CharacterId;      // CharactersType (1-byte enum)
    public string CharacterName = "";
    public string WeaponId = "";
    public string SkinId = "";
    public string WeaponSkinId = "";
    public string ModVersion = "";

    public bool SameAs(PlayerInfo o) =>
        Name == o.Name && CharacterId == o.CharacterId && WeaponId == o.WeaponId && SkinId == o.SkinId && WeaponSkinId == o.WeaponSkinId;
}

/// <summary>
/// Phase 6: the co-op session as a whole, independent of any one run. The connection is made in the menu and
/// survives several runs, so "the partner connected" is no longer "the partner entered my match": this keeps
/// who the partner is (Hello) and where each side is (LobbyState), and owns the per-run lifecycle that used to
/// hang off PeerConnected (the remote ghost, the host's enemy resync). See docs/fase6-investigacao.md.
/// </summary>
public static class CoopSession
{
    public static readonly PlayerInfo Local = new();
    public static PlayerInfo Remote { get; private set; } = new();
    public static LobbyPlace RemotePlace { get; private set; } = LobbyPlace.Menu;

    public static bool Connected => NetworkManager.HasPeer;

    /// <summary>Our match is running (StartMatch seen, game manager still alive).</summary>
    public static bool LocalInRun { get; private set; }
    public static bool RemoteInRun => Connected && RemotePlace == LobbyPlace.InRun;
    public static bool LocalLoading => _loadingSince >= 0f;
    public static float LocalRunStartTime { get; private set; }

    /// <summary>This run was started as the co-op client: keeps CoopRole.IsClientSide true for the rest of the
    /// run even if the connection drops (the spawners are already stopped; turning host-like mid-run is phase 8).</summary>
    internal static bool ClientRunLatched;

    /// <summary>A scene change inside the run is under way (Endless/BossRush portal or co-op Restart): from the portal
    /// until the next StartMatch. While the old scene still lives the partner sees Transition, then Loading.</summary>
    public static bool StageTransition { get; private set; }

    public static LobbyPlace LocalPlace =>
        LocalInRun ? (StageTransition ? LobbyPlace.Transition : LobbyPlace.InRun)
        : LocalLoading ? LobbyPlace.Loading : LobbyPlace.Menu;

    /// <summary>The partner is on its way to the next scene (still in the old one, or loading).</summary>
    public static bool RemoteChangingScene => Connected && (RemotePlace == LobbyPlace.Loading || RemotePlace == LobbyPlace.Transition);

    public static string PartnerName => Remote.Known && Remote.Name.Length > 0 ? Remote.Name : "Parceiro";

    internal const string MenuScene = "Menu-Survivors";
    private const float LoadingTimeout = 90f;
    private const float InfoRefreshInterval = 1f;

    private static float _loadingSince = -1f;
    private static float _sceneTimer;
    private static bool _loggedStaleGameManager;

    /// <summary>The active scene is the main menu (checked every 0.25 s).</summary>
    public static bool InMenuScene { get; private set; }
    private static bool _hooked;
    private static bool _helloSent, _placeSent;
    private static LobbyPlace _lastSentPlace;
    private static float _infoTimer;

    // Transient on-screen notice (CoopUi banner).
    private static string? _notice;
    private static float _noticeUntil;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerConnected += () =>
        {
            Remote = new PlayerInfo();
            RemotePlace = LobbyPlace.Menu;
            _helloSent = false;
            _placeSent = false;
            RefreshLocalInfo();
            RunSync.OnPeerConnected();
        };
        NetworkManager.PeerDisconnected += () =>
        {
            string who = PartnerName;
            bool partnerWasInRun = RemotePlace != LobbyPlace.Menu;
            Remote = new PlayerInfo();
            RemotePlace = LobbyPlace.Menu;
            // Phase 8: the host keeps playing (the client's case is CoopLife's: its run ends).
            Notice(LocalInRun && partnerWasInRun && NetworkManager.Role == NetRole.Host
                ? $"{who} desconectou. Você continua sozinho."
                : $"{who} desconectou", 6f);
        };
    }

    public static void Notice(string text, float seconds = 4f)
    {
        _notice = text;
        _noticeUntil = Time.unscaledTime + seconds;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Notice: {text}");
    }

    internal static string? ActiveNotice => _notice != null && Time.unscaledTime < _noticeUntil ? _notice : null;

    // ---------------- local lifecycle ----------------

    /// <summary>RunStart sent (host) or accepted (client): we're on our way into a match.</summary>
    internal static void BeginLoading()
    {
        _loadingSince = Time.unscaledTime;
        SendPlace(force: true);
    }

    /// <summary>A portal or Restart was taken inside the run (PortalSync/RunSync): the game will change scene by itself
    /// (LoadNextScene). Counts as loading from now on, so the barrier holds whoever reaches the next StartMatch first.</summary>
    internal static void BeginStageTransition(string why)
    {
        if (!StageTransition)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Stage transition ({why}): next scene coming (partner {RemotePlace}).");
        StageTransition = true;
        _loadingSince = Time.unscaledTime;
        SendPlace(force: true);
    }

    /// <summary>The scene change announced by BeginStageTransition isn't happening after all (the client couldn't
    /// follow the host's portal). Only while the old scene is still up.</summary>
    internal static void CancelStageTransition(string why)
    {
        if (!StageTransition || !LocalInRun) return;
        StageTransition = false;
        _loadingSince = -1f;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Stage transition cancelled ({why}).");
        SendPlace(force: true);
    }

    /// <summary>GameManagerUtil.UnsetGameManager postfix: the scene's game manager was just destroyed. A direct
    /// scene-to-scene change (portal, Restart) may never show a frame without a game manager to the tick (the new
    /// scene's Start runs SetupGameData before the next Update), so the run is left here, at the exact moment.</summary>
    internal static void OnGameManagerUnset()
    {
        if (LocalInRun) OnLocalRunLeft();
    }

    /// <summary>GameManagerUtil.StartMatch postfix.</summary>
    internal static void OnLocalMatchStarted()
    {
        StageTransition = false;
        LocalInRun = true;
        InMenuScene = false; // the 0.25 s scene check may still hold the menu from before the load
        _sceneTimer = 0f;
        LocalRunStartTime = Time.unscaledTime;
        _loadingSince = -1f;
        ClientRunLatched = NetworkManager.Role == NetRole.Client;
        RunSync.OnLocalMatchStarted();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Local run started (role={NetworkManager.Role} connected={Connected} partner={RemotePlace}).");
        SendPlace(force: true);
    }

    private static void OnLocalRunLeft()
    {
        LocalInRun = false;
        ClientRunLatched = false;
        GameState.LocalPlayerEntity = null;
        RunSync.OnLocalRunLeft();
        Plugin.StaticLog.LogInfo(StageTransition
            ? $"[SoulstoneCoop][session] Left the run's scene (stage transition, loading the next). connected={Connected}"
            : $"[SoulstoneCoop][session] Local run over (back to menu). connected={Connected}");
        SendPlace(force: true);
    }

    public static void Tick(float dt)
    {
        _sceneTimer -= dt;
        if (_sceneTimer <= 0f)
        {
            _sceneTimer = 0.25f;
            InMenuScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == MenuScene;
            if (InMenuScene && GameManagerUtil.HasGameManager && !_loggedStaleGameManager)
            {
                _loggedStaleGameManager = true;
                Plugin.StaticLog.LogWarning("[SoulstoneCoop][session] HasGameManager is still true in the menu scene.");
            }
        }
        // Back in the menu scene counts too, in case HasGameManager stays true there (docs/fase6-investigacao.md, 5).
        if (LocalInRun && (!GameManagerUtil.HasGameManager || InMenuScene)) OnLocalRunLeft();
        if (LocalLoading && Time.unscaledTime - _loadingSince > LoadingTimeout)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][session] Loading never reached StartMatch - back to menu state (stageTransition={StageTransition}).");
            _loadingSince = -1f;
            StageTransition = false;
            RunSync.OnLocalRunLeft();
        }

        if (!Connected) return;
        SendPlace(force: false);

        _infoTimer += dt;
        if (_infoTimer >= InfoRefreshInterval || !_helloSent)
        {
            _infoTimer = 0f;
            // The character can change in the menu; in a run it's fixed (and the profile reads are wasted work).
            bool changed = !LocalInRun && RefreshLocalInfo();
            if (changed || !_helloSent) SendHello();
        }

        RunSync.HostRejoinTick(); // phase 8, AllowRejoin only
    }

    private static void SendPlace(bool force)
    {
        if (!Connected) return;
        var place = LocalPlace;
        if (!force && _placeSent && place == _lastSentPlace) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.LobbyState);
        w.Put((byte)place);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _lastSentPlace = place;
        _placeSent = true;
    }

    // ---------------- identity ----------------

    /// <summary>Reads name, character, weapon and skins from the local profile. True if anything changed.</summary>
    internal static bool RefreshLocalInfo()
    {
        var info = new PlayerInfo { Known = true, ModVersion = Plugin.HandshakeVersion };
        info.Name = SteamName.TryGet() ?? (NetworkManager.Role == NetRole.Host ? "Host" : "Cliente");
        try
        {
            var prof = Global.PlayerProfile;
            if (prof != null && prof.SelectedCharacter != null)
            {
                var ct = prof.SelectedCharacter.CharacterIdAsEnum;
                info.CharacterId = (byte)ct;
                info.CharacterName = ct.ToString();
                try { info.WeaponId = prof.SelectedWeaponPerCharacter?.GetWeaponIdByCharacterId(ct) ?? ""; } catch { }
                try { info.SkinId = prof.SelectedSkinPerCharacter?.GetCharacterSkinIdByCharacterId(ct) ?? ""; } catch { }
                try { info.WeaponSkinId = prof.SelectedSkinPerCharacter?.GetWeaponSkinIdByCharacterId(ct) ?? ""; } catch { }
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][session] Reading the profile's character: {ex.Message}");
        }

        bool changed = !Local.Known || !Local.SameAs(info);
        Local.Known = true;
        Local.Name = info.Name;
        Local.CharacterId = info.CharacterId;
        Local.CharacterName = info.CharacterName;
        Local.WeaponId = info.WeaponId;
        Local.SkinId = info.SkinId;
        Local.WeaponSkinId = info.WeaponSkinId;
        Local.ModVersion = info.ModVersion;
        return changed;
    }

    private static void SendHello()
    {
        if (!Connected) return;
        if (!Local.Known) RefreshLocalInfo();
        var w = new NetDataWriter();
        w.Put((byte)PacketType.Hello);
        w.Put(Local.Name);
        w.Put(Local.CharacterId);
        w.Put(Local.CharacterName);
        w.Put(Local.WeaponId);
        w.Put(Local.SkinId);
        w.Put(Local.WeaponSkinId);
        w.Put(Local.ModVersion);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _helloSent = true;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Hello sent: {Local.Name} {Local.CharacterName} weapon={Local.WeaponId} skin={Local.SkinId}/{Local.WeaponSkinId}");
    }

    public static void OnHello(NetDataReader r)
    {
        var info = new PlayerInfo
        {
            Known = true,
            Name = r.GetString(),
            CharacterId = r.GetByte(),
            CharacterName = r.GetString(),
            WeaponId = r.GetString(),
            SkinId = r.GetString(),
            WeaponSkinId = r.GetString(),
            ModVersion = r.GetString(),
        };
        bool first = !Remote.Known;
        Remote = info;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Hello from partner: {info.Name} {info.CharacterName} weapon={info.WeaponId} skin={info.SkinId}/{info.WeaponSkinId} mod={info.ModVersion}");
        if (first) Notice(info.CharacterName.Length > 0 ? $"Conectado com {info.Name} ({info.CharacterName})" : $"Conectado com {info.Name}", 5f);
    }

    public static void OnLobbyState(NetDataReader r)
    {
        var place = (LobbyPlace)r.GetByte();
        var prev = RemotePlace;
        RemotePlace = place;
        if (prev == place) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Partner: {prev} -> {place} (local {LocalPlace}).");

        // The partner's match just started next to ours: resend every live enemy (this used to happen on
        // PeerConnected, when the connection was made inside the run). Duplicates are ignored by the client.
        // Not while our own old scene is still up (portal taken): those enemies are about to be gone.
        if (place == LobbyPlace.InRun && LocalInRun && !StageTransition && NetworkManager.Role == NetRole.Host)
            Enemies.HostEnemySync.ResyncToPeer("partner entered the run");

        // Client: the host's old scene is gone. Everything it sent from that scene came before this packet (same
        // ordered channel), so spawns/loot still around are stale (portal or Restart inside the run).
        if (place == LobbyPlace.Loading && (prev == LobbyPlace.InRun || prev == LobbyPlace.Transition) && NetworkManager.Role == NetRole.Client)
            PortalSync.OnHostLeftScene();
    }
}
