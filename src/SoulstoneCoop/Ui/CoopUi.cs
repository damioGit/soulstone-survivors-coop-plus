using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SoulstoneCoop.Ui;

/// <summary>
/// Phase 6 UI (uGUI + TextMeshPro; IMGUI is partly stripped in this build: GUI.* throws "Method unstripping failed").
/// - A "Co-op" tab at the end of the main menu's top bar (MainMenu-Panel/Top-Menu-Holder), cloned from the
///   Profile tab (History-Button) so it has the game's sprite, font, enable animation and click sound. The clone's
///   own wiring is removed: LocalizedLabel (would rewrite the text), EventTrigger and the Button's persistent onClick
///   (MainMenuPanel.OnHistoryPressed). It mirrors the Profile tab's visibility, which the menu animates.
/// - The panel (Host / Join + IP / Disconnect / status) on our own overlay canvas, dressed with the sprites of the
///   game's Confirmation-Panel (frame and title) and the menu tab sprite for buttons.
/// - A banner on the same DontDestroyOnLoad canvas for notices, in the menu and in runs (partner loading, paused,
///   disconnected, blocked actions).
/// If the tab can't be cloned, a plain button in the top-right corner opens the panel instead.
/// </summary>
public static class CoopUi
{
    private const string MenuScene = "Menu-Survivors";
    private const string DefaultAddress = "127.0.0.1";
    private static string TabLabel => Loc.T("ui.tab");

    private static readonly Color PanelFallbackColor = new(0.06f, 0.05f, 0.08f, 0.94f);
    private static readonly Color ButtonFallbackColor = new(0.30f, 0.12f, 0.12f, 1f);
    private static readonly Color DisabledTint = new(0.45f, 0.45f, 0.45f, 0.8f);
    private static readonly Color DisabledTextColor = new(1f, 1f, 1f, 0.45f);
    private static readonly Color AccentColor = new(0.95f, 0.80f, 0.45f, 1f);
    private static readonly Color BannerColor = new(0f, 0f, 0f, 0.7f);

    // Panel layout, in the game's 1920x1080 reference: the mirror of the hub's character panel (Fight-Panel-Survivors/
    // Character-Holder, 500 wide, title frame centered on its top edge) on the left side of the screen.
    private const float PanelWidth = 500f, PanelLeft = 36f, PanelTop = 228f, PanelPad = 28f;
    private const float RowHeight = 58f, RowGap = 12f;
    private const float ButtonFont = 30f;

    // overlay canvas
    private static GameObject? _root;
    private static TMP_FontAsset? _font;
    private static FontType _fontType = FontType.Regular; // the FightButton label's, for the per-language font
    // Texts set once at build time, redone on a language change (Loc.Version).
    private static readonly List<(TextMeshProUGUI Text, string Key)> _labels = new();
    private static int _locVersion = -1, _fontVersion = -1;
    /// <summary>The game's menu font once the UI found it (phase 5: the ghost's name tag uses it too).</summary>
    internal static TMP_FontAsset? GameFont => _font;
    private static Sprite? _tabSprite, _tabSelectedSprite;
    // The game's own art, found by name (see the sprite list in FindGameLook); sliced copies are made at build time.
    private static Sprite? _frameSprite, _titleSprite, _glowSprite, _sectionSprite, _closeSprite;
    private static Sprite? _buttonSprite, _buttonHotSprite, _inputSprite;
    private static GameObject? _fallbackButton;
    private static GameObject? _panel;
    // Rows shown or hidden by state (RefreshPanel); the panel's layout group closes the gaps.
    private static GameObject? _steamHeader, _steamRow, _directHeader, _directRow, _directButtons, _groupHeader, _leaveRow;
    private static TextMeshProUGUI? _status;
    private static TMP_InputField? _ipField;
    private static Button? _hostButton, _joinButton, _leaveButton, _pasteButton;
    private static Button? _inviteButton, _copyCodeButton; // phase 7 (Steam)
    private static TextMeshProUGUI? _inviteLabel; // phase 9: "Convidar amigo (n/8)"
    private static RectTransform? _bannerRt;
    private static TextMeshProUGUI? _bannerText;

    // tab in the game's menu
    private static GameObject? _tab;          // our clone (Unity-null once the menu scene is gone)
    private static GameObject? _tabSource;    // the Profile tab it mirrors
    private static Image? _tabImage;
    private static CanvasGroup? _tabGroup, _tabSourceGroup;
    private static bool _tabLogged;
    private static TextMeshProUGUI? _tabText;
    private static MainMenuPanel? _menuPanel;
    private static MainMenuState _menuStateWhenOpened;
    private static float _tabRetryTimer;
    private static int _tabFailures;

    private static bool _panelOpen;
    private static bool _loggedFocus;
    private static bool _failed;
    private static float _sceneCheckTimer;
    private static bool _inMenu;
    private static string _lastStatus = "";

    /// <summary>The IP field has keyboard focus: debug hotkeys (Insert/Delete/PageDown) must not fire.</summary>
    public static bool IsTyping => _ipField != null && _ipField.isFocused;

    public static void Tick()
    {
        if (_failed) return;
        try
        {
            _sceneCheckTimer -= Time.unscaledDeltaTime;
            if (_sceneCheckTimer <= 0f)
            {
                _sceneCheckTimer = 0.5f;
                _inMenu = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == MenuScene;
            }
            if (_root == null)
            {
                if (!_inMenu) return; // build in the menu, where the game's font and sprites are around
                Build();
                if (_root == null) return;
            }

            if (_locVersion != Loc.Version) ApplyLanguage();
            if (_inMenu) UpdateMenuTab();
            else _panelOpen = false;

            bool tabOk = _tab != null;
            _fallbackButton!.SetActive(_inMenu && !tabOk && _tabFailures >= 3);
            // Built once the hub is up (the tab exists), when the game's menu sprites are loaded.
            if (_panel == null && _inMenu && (tabOk || _panelOpen)) BuildPanel();
            if (_panel != null) _panel.SetActive(_inMenu && _panelOpen);
            if (_inMenu && _panelOpen && _panel != null) RefreshPanel();
            if (!_loggedFocus && _ipField != null && _ipField.isFocused)
            {
                _loggedFocus = true;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] IP field got keyboard focus.");
            }
            RefreshBanner();
            UiProbe.Tick(_inMenu, _root != null);
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][ui] Co-op UI disabled: {ex}");
        }
    }

    // ---------------- the tab in the game's top menu ----------------

    private static void UpdateMenuTab()
    {
        if (_tab == null)
        {
            _tabRetryTimer -= Time.unscaledDeltaTime;
            if (_tabRetryTimer > 0f || _tabFailures >= 3) return;
            _tabRetryTimer = 1f;
            if (!TryCreateMenuTab()) _tabFailures++;
            return;
        }

        // The menu shows/hides and fades its tabs through MainMenuPanel.FadeAnimationGroup, which only knows its
        // own tabs: ours copies the Profile tab's visibility and fade every frame (first test: alpha stuck at 0).
        bool srcActive = _tabSource != null && _tabSource.activeSelf;
        if (_tab.activeSelf != srcActive) _tab.SetActive(srcActive);
        if (!srcActive) _panelOpen = false;
        if (_tabGroup != null && _tabSourceGroup != null)
        {
            if (_tabGroup.alpha != _tabSourceGroup.alpha) _tabGroup.alpha = _tabSourceGroup.alpha;
            _tabGroup.interactable = _tabSourceGroup.interactable;
            _tabGroup.blocksRaycasts = _tabSourceGroup.blocksRaycasts;
        }
        if (srcActive) PlaceTabAfterSource();
        if (srcActive && !_tabLogged && (_tabGroup == null || _tabGroup.alpha > 0.99f))
        {
            _tabLogged = true;
            var rt = _tab.GetComponent<RectTransform>();
            var srt = _tabSource!.GetComponent<RectTransform>();
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][ui] Co-op tab visible: pos={rt.position} size={rt.rect.size} alpha={_tabGroup?.alpha ?? -1} " +
                $"(Profile tab pos={srt.position} size={srt.rect.size}) siblings={_tab.transform.parent.childCount}");
        }

        if (_tabText != null && _tabText.text != TabLabel) _tabText.text = TabLabel;
        if (_tabImage != null)
        {
            var want = _panelOpen ? _tabSelectedSprite : _tabSprite;
            if (want != null && _tabImage.sprite != want) _tabImage.sprite = want;
        }

        // Another tab picked in the game's menu: get out of the way.
        if (_panelOpen && _menuPanel != null && _menuPanel.CurrentState != _menuStateWhenOpened) _panelOpen = false;
    }

    /// <summary>The tabs are not laid out by the holder's layout group after their slide-in animation (second test:
    /// the clone sat on top of Profile, ~150 px above the screen): put ours one tab-step to the right of Profile,
    /// following its slide-in, with the same spacing as Achievements → Profile.</summary>
    private static void PlaceTabAfterSource()
    {
        var rt = _tab!.GetComponent<RectTransform>();
        var srt = _tabSource!.GetComponent<RectTransform>();
        if (rt == null || srt == null) return;
        float stepX = srt.rect.width + 8f;
        var parent = srt.parent;
        int idx = srt.GetSiblingIndex();
        if (parent != null && idx > 0)
        {
            var prev = parent.GetChild(idx - 1).GetComponent<RectTransform>();
            if (prev != null && prev.gameObject != _tab)
            {
                float d = srt.anchoredPosition.x - prev.anchoredPosition.x;
                if (d > 1f) stepX = d;
            }
        }
        var want = srt.anchoredPosition + new Vector2(stepX, 0f);
        if ((rt.anchoredPosition - want).sqrMagnitude > 0.01f) rt.anchoredPosition = want;
    }

    /// <summary>The top bar is sized for the game's 8 tabs and ours, a 9th, ran under the options gear on the right.
    /// Shrink the holder so the 9 tabs take the room the 8 did, and shift it left by half a tab to stay centered.</summary>
    private static void FitTabBar(RectTransform? srcTab)
    {
        // GetComponent, not "as": an IL2CPP Transform proxy doesn't cast to RectTransform.
        var holder = srcTab != null && srcTab.parent != null ? srcTab.parent.GetComponent<RectTransform>() : null;
        if (holder == null || holder.localScale.x < 0.99f) return; // already done for this holder
        float step = srcTab!.rect.width - 5f; // the holder's spacing: 200 wide tabs, 195 apart
        float width = holder.rect.width;
        if (width < 1f) return;
        float scale = Mathf.Min(1f, width / (width + step + 40f));
        holder.localScale = new Vector3(scale, scale, 1f);
        holder.anchoredPosition += new Vector2(-step * 0.5f * scale, 0f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Top bar scaled to {scale:0.000} for the Co-op tab.");
    }

    private static bool TryCreateMenuTab()
    {
        try
        {
            var mm = UnityEngine.Object.FindObjectOfType<MenuManager>();
            var panel = mm != null ? mm.MainMenuPanel : null;
            var src = panel != null && panel.HistoryButton != null ? panel.HistoryButton.gameObject : null;
            if (src == null) return false;

            var clone = UnityEngine.Object.Instantiate(src, src.transform.parent);
            clone.name = "Coop-Button";
            clone.transform.SetAsLastSibling();

            foreach (var l in clone.GetComponentsInChildren<LocalizedLabel>(true)) UnityEngine.Object.DestroyImmediate(l);
            foreach (var t in clone.GetComponentsInChildren<EventTrigger>(true)) UnityEngine.Object.DestroyImmediate(t);
            // Its fade/slide-in animations are driven by the menu's animation group, which never drives the clone.
            foreach (var a in clone.GetComponentsInChildren<GameSmithing.UIAnimation>(true)) UnityEngine.Object.DestroyImmediate(a);
            // Placed by hand (PlaceTabAfterSource): keep the holder's layout/size fitter from moving the game's tabs.
            var le = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            _tabGroup = clone.GetComponent<CanvasGroup>();
            _tabSourceGroup = src.GetComponent<CanvasGroup>();
            _tabLogged = false;

            var button = clone.GetComponent<Button>();
            if (button == null) { UnityEngine.Object.Destroy(clone); return false; }
            button.onClick = new Button.ButtonClickedEvent(); // drops the persistent OnHistoryPressed
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            AddClick(button, OnToggle, "tab");

            _tabText = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            if (_tabText != null) _tabText.text = TabLabel;
            _tabImage = clone.GetComponent<Image>();
            _tabSprite = panel!.ButtonRegularSprite;
            _tabSelectedSprite = panel.ButtonSelectedSprite;
            _menuPanel = panel;
            _tabSource = src;
            _tab = clone;
            clone.SetActive(src.activeSelf);
            FitTabBar(src.GetComponent<RectTransform>());
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Co-op tab added to the main menu ({clone.transform.parent.name}).");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Could not add the Co-op tab: {ex.Message}");
            return false;
        }
    }

    // ---------------- state -> widgets ----------------

    private static void RefreshPanel()
    {
        var role = NetworkManager.Role;
        var status = NetworkManager.Status;
        bool steam = NetworkManager.Kind == "Steam";
        var lobbyState = Steam.SteamLobby.State;
        string text;
        if (role == NetRole.None)
        {
            string? error = NetworkManager.LastError ?? Steam.SteamLobby.LastError;
            if (lobbyState == Steam.SteamLobby.LobbyState.Creating) text = Loc.T("st.creating_lobby");
            else if (lobbyState == Steam.SteamLobby.LobbyState.Joining) text = Loc.T("st.joining_lobby");
            else if (error != null) text = $"<color=#ff8080>{Loc.Show(error)}</color>";
            else text = Loc.T(Steam.SteamLobby.Available ? "st.idle" : "st.idle_no_steam");
        }
        else if (status == NetStatus.Hosting)
            text = steam
                ? Loc.T("st.lobby_open", ("max", NetworkManager.MaxPlayers))
                : Loc.T("st.hosting_port", ("port", NetworkManager.Port), ("max", NetworkManager.MaxPlayers));
        else if (status == NetStatus.Connecting)
            text = steam
                ? Loc.T("st.connecting_steam", ("name", Steam.SteamLobby.FriendName(Steam.SteamLobby.HostId)))
                : Loc.T("st.connecting_ip", ("addr", $"{NetworkManager.TargetAddress}:{NetworkManager.Port}"));
        else if (status == NetStatus.Connected)
        {
            if (role == NetRole.Client)
            {
                var h = CoopSession.Host;
                int ping = NetworkManager.PingMs;
                text = Loc.T("st.connected", ("name", $"<color=#f2cc73>{Who(h)}</color>"),
                    ("link", $"{NetworkManager.Kind}{(ping >= 0 ? $", {ping} ms" : "")}"));
                if (NetworkManager.Unstable)
                    text += $" <color=#ff8080>{Loc.T("st.unstable", ("s", NetworkManager.SilenceSeconds.ToString("0")))}</color>";
                var others = new List<string>();
                foreach (var r in CoopSession.Remotes) if (!r.IsHost) others.Add(Who(r));
                if (others.Count > 0)
                    text += "\n" + Loc.T("st.group", ("n", Players.Count), ("max", Players.SessionMax), ("names", string.Join(", ", others)));
                text += "\n" + Loc.T(h == null || h.Place == LobbyPlace.Menu ? "st.wait_host" : "st.host_in_run");
            }
            else
            {
                text = Loc.T("st.hosting", ("kind", NetworkManager.Kind), ("n", Players.Count), ("max", NetworkManager.MaxPlayers));
                bool allInMenu = true;
                foreach (var r in CoopSession.Remotes)
                {
                    int ping = NetworkManager.PingOf(r.Slot);
                    string unstable = NetworkManager.IsUnstable(r.Slot) ? $" <color=#ff8080>{Loc.T("st.unstable_tag")}</color>" : "";
                    text += $"\n<color=#f2cc73>{Who(r)}</color>  {(ping >= 0 ? $"{ping} ms" : "-")}  {PlaceLabel(r.Place)}{unstable}";
                    allInMenu &= r.Place == LobbyPlace.Menu;
                }
                text += "\n" + Loc.T(allInMenu ? "st.all_in_menu" : "st.wait_all_menu");
            }
        }
        else text = "...";
        if (role == NetRole.Host && NetworkManager.LastError != null && status == NetStatus.Hosting)
            text += $"\n<color=#ff8080>{Loc.Show(NetworkManager.LastError)}</color>";
        if (role == NetRole.Host && steam && Steam.SteamLobby.LastError != null)
            text += $"\n<color=#ff8080>{Loc.Show(Steam.SteamLobby.LastError)}</color>";
        if (NetSim.Enabled) text += $"\n<color=#ffb060>{Loc.T("st.netsim", ("desc", NetSim.Describe(local: true)))}</color>";

        bool busy = lobbyState == Steam.SteamLobby.LobbyState.Creating || lobbyState == Steam.SteamLobby.LobbyState.Joining;
        bool idle = role == NetRole.None && !busy;
        bool steamHosting = role == NetRole.Host && steam;
        // Phase 9: anyone in the Steam lobby can invite its own friends, while there's a free seat.
        bool steamMember = steamHosting || (role == NetRole.Client && steam && Steam.SteamLobby.State == Steam.SteamLobby.LobbyState.Joined);
        bool steamAvailable = Steam.SteamLobby.Available;
        bool hasCode = Steam.SteamLobby.LobbyId != 0;
        int count = Players.Count, max = Players.SessionMax;
        if (TestState != null) FakeState(TestState, ref text, ref idle, ref steamMember, ref steamAvailable, ref hasCode, ref count, ref max);

        if (text != _lastStatus)
        {
            _lastStatus = text;
            _status!.text = text;
        }

        // Out of a session: the ways to start one. In a session: who's in it, the invite while there's room, leaving.
        bool full = count >= max;
        bool showInvite = steamAvailable && (idle || steamMember);
        SetActive(_steamHeader, showInvite);
        SetActive(_steamRow, showInvite);
        SetActive(_copyCodeButton!.gameObject, steamMember && hasCode);
        SetInteractable(_inviteButton!, idle || !full);
        string inviteLabel = steamMember ? Loc.T("ui.invite_count", ("n", count), ("max", max)) : Loc.T("ui.invite");
        if (_inviteLabel != null && _inviteLabel.text != inviteLabel) _inviteLabel.text = inviteLabel;
        SetInteractable(_copyCodeButton, true);
        SetActive(_directHeader, idle);
        SetActive(_directRow, idle);
        SetActive(_directButtons, idle);
        SetActive(_groupHeader, !idle);
        SetActive(_leaveRow, !idle);
        _ipField!.interactable = idle;
    }

    private static void SetActive(GameObject? go, bool on)
    {
        if (go != null && go.activeSelf != on) go.SetActive(on);
    }

    /// <summary>UiProbe screenshots: each state of the panel without a real session.</summary>
    private static void FakeState(string state, ref string text, ref bool idle, ref bool steamMember, ref bool steamAvailable,
        ref bool hasCode, ref int count, ref int max)
    {
        steamAvailable = true;
        max = 8;
        string row(string name, string character, int ping, LobbyPlace place) =>
            $"\n<color=#f2cc73>{name} ({character})</color>  {ping} ms  {PlaceLabel(place)}";
        switch (state)
        {
            case "hosting":
                idle = false; steamMember = true; hasCode = true; count = 3;
                text = Loc.T("st.hosting", ("kind", "Steam"), ("n", 3), ("max", 8))
                    + row("Kasia", "Pyromancer", 38, LobbyPlace.Menu) + row("Tomek_PL", "Sentinel", 112, LobbyPlace.Loading)
                    + "\n" + Loc.T("st.wait_all_menu");
                break;
            case "connected":
                idle = false; steamMember = true; hasCode = true; count = 2;
                text = Loc.T("st.connected", ("name", "<color=#f2cc73>Damian (Barbarian)</color>"), ("link", "Steam, 41 ms"))
                    + "\n" + Loc.T("st.wait_host");
                break;
            case "full":
                idle = false; steamMember = true; hasCode = true; count = 8;
                text = Loc.T("st.hosting", ("kind", "Steam"), ("n", 8), ("max", 8)) + "\n" + Loc.T("st.all_in_menu");
                break;
            case "error":
                text = $"<color=#ff8080>{Loc.T("st.idle")} Connection timed out.</color>";
                break;
            default: // idle
                text = Loc.T("st.idle");
                break;
        }
    }

    private static string Who(RemotePlayer? r)
    {
        if (r == null || !r.Info.Known) return r?.Name ?? Loc.T("name.partner_lc");
        return r.Info.CharacterName.Length > 0 ? $"{r.Name} ({r.Info.CharacterName})" : r.Name;
    }

    private static string PlaceLabel(LobbyPlace p) => p switch
    {
        LobbyPlace.Menu => Loc.T("place.menu"),
        LobbyPlace.Loading => Loc.T("place.loading"),
        LobbyPlace.InRun => Loc.T("place.in_run"),
        LobbyPlace.Transition => Loc.T("place.transition"),
        _ => "?",
    };

    private static void RefreshBanner()
    {
        string? text;
        string names;
        // Phase 8: silence well before the transport gives up (10 s IP, 15 s Steam). Not while someone is loading
        // a run: a scene load freezes the game, and the Steam pings with it.
        if (NetworkManager.Unstable && !CoopSession.LocalLoading && !CoopSession.AnyRemoteChangingScene)
        {
            names = NetworkManager.Role == NetRole.Host
                ? CoopSession.NamesWhere(r => NetworkManager.IsUnstable(r.Slot))
                : CoopSession.NameOf(Players.HostSlot);
            text = Loc.T("bn.unstable", ("names", names), ("s", NetworkManager.SilenceSeconds.ToString("0")));
        }
        else if (RunSync.HoldForPartner) text = Loc.T("bn.loading", ("names", CoopSession.NamesWhere(r => r.ChangingScene)));
        else if ((names = CoopSession.NamesWhere(r => PauseSync.Freezes(r.Slot) && PauseSync.ReasonOf(r.Slot) == PauseReason.Pause)).Length > 0)
            text = Loc.T(NetworkManager.Role == NetRole.Host ? "bn.paused_host" : "bn.paused", ("names", names));
        else if ((names = CoopSession.NamesWhere(r => PauseSync.Freezes(r.Slot) && PauseSync.ReasonOf(r.Slot) == PauseReason.Cutscene)).Length > 0)
            text = Loc.T("bn.cutscene", ("names", names));
        else if ((names = CoopSession.NamesWhere(r => PauseSync.Freezes(r.Slot) && PauseSync.ReasonOf(r.Slot) == PauseReason.LevelUp)).Length > 0)
            text = Loc.T("bn.levelup_wait", ("names", names));
        else if ((names = CoopSession.NamesWhere(r => PauseSync.IsChoosing(r.Slot))).Length > 0)
            text = Loc.T("bn.choosing", ("names", names));
        else if (LevelUpOverlay.BannerText != null) text = LevelUpOverlay.BannerText;
        else if (CoopSession.ActiveNotice != null) text = CoopSession.ActiveNotice;
        else if ((names = CoopSession.NamesWhere(r => PauseSync.IsPausedAlone(r.Slot))).Length > 0)
            text = Loc.T("bn.pause_menu", ("names", names));
        else text = null;

        bool show = text != null;
        var go = _bannerRt!.gameObject;
        if (go.activeSelf != show) go.SetActive(show);
        if (!show) return;
        if (_bannerText!.text != text) _bannerText.text = text;
        // In the menu just above the Fight button, between our panel and the character panel; in runs near the top.
        float y = _inMenu ? -860f : -140f, w = _inMenu ? 820f : 1000f;
        if (Mathf.Abs(_bannerRt.anchoredPosition.y - y) > 0.5f) _bannerRt.anchoredPosition = new Vector2(0f, y);
        if (Mathf.Abs(_bannerRt.sizeDelta.x - w) > 0.5f) _bannerRt.sizeDelta = new Vector2(w, _bannerRt.sizeDelta.y);
    }

    private static void SetInteractable(Button b, bool on)
    {
        if (b.interactable == on) return;
        b.interactable = on;
        var img = b.targetGraphic;
        if (img != null) img.color = on ? (_buttonSprite != null ? Color.white : ButtonFallbackColor) : DisabledTint;
        var label = b.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.color = on ? Color.white : DisabledTextColor;
    }

    // ---------------- actions ----------------

    private static void OnToggle()
    {
        _panelOpen = !_panelOpen;
        if (_panelOpen && _menuPanel != null) _menuStateWhenOpened = _menuPanel.CurrentState;
        if (_panelOpen)
        {
            bool flag = false;
            try { flag = Global.ForceCleanEventSystemSelectedObjectKeyboard; } catch { }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Panel opened (ForceCleanEventSystemSelectedObjectKeyboard={flag}).");
        }
    }

    private static void OnHost()
    {
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] Host pressed.");
        NetworkManager.StartHost();
    }

    private static void OnJoin()
    {
        string addr = _ipField != null ? _ipField.text.Trim() : "";
        if (addr.Length == 0) addr = DefaultAddress;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Join pressed: {addr}");
        try { Plugin.LastAddress.Value = addr; } catch { }
        // Phase 7: a long number is a Steam lobby code ("Copiar código" on the host), anything else an IP.
        if (Steam.SteamLobby.TryParseCode(addr, out var lobby)) Steam.SteamLobby.Join(lobby);
        else NetworkManager.StartClient(addr);
    }

    private static void OnInvite()
    {
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] Invite pressed.");
        Steam.SteamLobby.HostAndInvite();
    }

    private static void OnCopyCode()
    {
        ulong lobby = Steam.SteamLobby.LobbyId;
        if (lobby == 0) return;
        bool ok = WindowsClipboard.TrySetText(lobby.ToString());
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Copy code pressed: {lobby} (clipboard {(ok ? "ok" : "failed")}).");
        CoopSession.Notice(Loc.Msg(ok ? "nt.code_copied" : "nt.copy_failed"), 5f);
    }

    private static void OnPaste()
    {
        string? clip = WindowsClipboard.TryGetText();
        string text = clip?.Trim() ?? "";
        // Only the first line / word: an address, not a whole paragraph.
        int cut = text.IndexOfAny(new[] { '\r', '\n', ' ', '\t' });
        if (cut > 0) text = text.Substring(0, cut);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Paste pressed: '{text}' (clipboard {(clip == null ? "unavailable" : $"{clip.Length} chars")}).");
        if (text.Length == 0) { CoopSession.Notice(Loc.Msg("nt.clipboard_empty")); return; }
        if (text.Length > 64) text = text.Substring(0, 64);
        _ipField!.text = text;
    }

    /// <summary>GameLoop.Update clears the EventSystem selection every frame while
    /// Global.ForceCleanEventSystemSelectedObjectKeyboard is set and the input is keyboard/mouse, which kept the IP
    /// field from ever keeping focus (third test). While the panel is open, GameLoopKeepSelectionPatch turns the flag
    /// off for that call only.</summary>
    internal static bool KeepSelection => _inMenu && _panelOpen && _ipField != null && _panel != null && _panel.activeSelf;

    private static void OnLeave()
    {
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] Disconnect pressed.");
        NetworkManager.Stop();
        Steam.SteamLobby.Leave(); // also cancels a lobby still being created or joined

    }

    private static void OnClose() => _panelOpen = false;

    /// <summary>UiProbe: fake panel state for screenshots ("idle", "hosting", "connected"), null = real state.</summary>
    internal static string? TestState;
    internal static string TestFieldText => _ipField != null ? _ipField.text : "";
    internal static bool TestTabVisible => _tab != null && _tab.activeInHierarchy && (_tabGroup == null || _tabGroup.alpha > 0.99f);
    /// <summary>UiProbe: the pointer over the invite button (its gold hover sprite) and a notice in the banner.</summary>
    internal static void TestHoverAndNotice()
    {
        if (_inviteButton != null)
            ExecuteEvents.Execute(_inviteButton.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerEnterHandler);
        CoopSession.Notice(Loc.Msg("nt.code_copied"), 10f);
    }

    internal static void TestOpen(bool open)
    {
        _panelOpen = open;
        if (open && _menuPanel != null) _menuStateWhenOpened = _menuPanel.CurrentState;
    }

    // ---------------- build ----------------

    private static void Build()
    {
        FindGameLook();
        if (_font == null)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][ui] No TMP font found yet - retrying.");
            return;
        }

        var root = new GameObject("SoulstoneCoop_UI");
        UnityEngine.Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f; // like the menu's canvases: the panel lines up with the hub on any aspect
        root.AddComponent<GraphicRaycaster>();
        var rootRt = root.GetComponent<RectTransform>();

        _labels.Clear();
        _fontVersion = Loc.Version;

        // Fallback opener, top right (only if the tab can't be added).
        var open = Track(MakeButton(rootRt, "CoopButton", TabLabel, OnToggle, 34f), "ui.tab");
        Place(open.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(260f, RowHeight));
        _fallbackButton = open.gameObject;
        _fallbackButton.SetActive(false);

        BuildBanner(rootRt);
        _root = root;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Co-op UI built (font {_font.name} [{_fontType}], language {Loc.Codes[(int)Loc.Current]}).");
        Loc.CheckGlyphs(_font, "co-op UI");
    }

    /// <summary>Notices in the menu and in runs, on the game's title frame (the bar behind "Twoje runy"/character name).</summary>
    private static void BuildBanner(RectTransform rootRt)
    {
        _bannerRt = MakeRect(rootRt, "CoopBanner");
        Place(_bannerRt, new Vector2(0.5f, 1f), new Vector2(0f, -140f), new Vector2(1000f, 72f));
        var bimg = _bannerRt.gameObject.AddComponent<Image>();
        bimg.raycastTarget = false;
        if (!Dress(bimg, _titleSprite, 72f)) bimg.color = BannerColor;
        _bannerText = MakeText(_bannerRt, "Text", "", 32f, TextAlignmentOptions.Center);
        _bannerText.raycastTarget = false;
        AutoSize(_bannerText, 32f);
        Stretch(_bannerText.rectTransform, 0f);
        _bannerText.rectTransform.offsetMin = new Vector2(90f, 6f);
        _bannerText.rectTransform.offsetMax = new Vector2(-90f, -6f);
        _bannerRt.gameObject.SetActive(false);
    }

    /// <summary>The panel, dressed like the hub's character panel: dark frame with the yellow glow at the top, title
    /// frame on its top edge, sections under the gold line of the skill lists, the game's buttons (gold when hovered)
    /// and the rune preset name field. A vertical layout sizes it to the rows shown in each state.</summary>
    private static void BuildPanel()
    {
        FindGameLook(); // the hub is up now: its sprites are loaded
        var rootRt = _root!.GetComponent<RectTransform>();
        if (_titleSprite != null && _bannerRt != null) Dress(_bannerRt.GetComponent<Image>(), _titleSprite, 72f);

        var panel = MakeRect(rootRt, "CoopPanel");
        panel.anchorMin = panel.anchorMax = new Vector2(0f, 1f);
        panel.pivot = new Vector2(0f, 1f);
        panel.anchoredPosition = new Vector2(PanelLeft, -PanelTop);
        panel.sizeDelta = new Vector2(PanelWidth, 400f);
        var bg = panel.gameObject.AddComponent<Image>();
        if (!Dress(bg, _frameSprite, 0f)) bg.color = PanelFallbackColor;
        var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset { left = (int)PanelPad, right = (int)PanelPad, top = 54, bottom = 18 };
        layout.spacing = RowGap;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        var fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        _panel = panel.gameObject;

        if (_glowSprite != null)
        {
            var glow = MakeRect(panel, "Glow");
            Ignore(glow);
            PlaceTopCenter(glow, 0f, 150f, PanelWidth - 8f, 300f);
            var gimg = glow.gameObject.AddComponent<Image>();
            gimg.sprite = _glowSprite;
            gimg.raycastTarget = false;
        }

        var titleHolder = MakeRect(panel, "Title");
        Ignore(titleHolder);
        PlaceTopCenter(titleHolder, 0f, 0f, 367f, 65f);
        var timg = titleHolder.gameObject.AddComponent<Image>();
        if (_titleSprite != null) timg.sprite = _titleSprite;
        else timg.color = Color.clear;
        timg.raycastTarget = false;
        var title = MakeText(titleHolder, "Label", TabLabel, 36f, TextAlignmentOptions.Center);
        AutoSize(title, 36f);
        Stretch(title.rectTransform, 0f);
        title.rectTransform.offsetMin = new Vector2(70f, 4f);
        title.rectTransform.offsetMax = new Vector2(-70f, -2f);
        _labels.Add((title, "ui.tab"));

        var close = MakeCloseButton(panel);
        Ignore(close.GetComponent<RectTransform>());
        Place(close.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(14f, 14f), new Vector2(46f, 46f));

        // Steam: the real way to play.
        _steamHeader = MakeSection(panel, "ui.sec_steam");
        var steamRow = MakeRow(panel, "SteamRow");
        _steamRow = steamRow.gameObject;
        _inviteButton = MakeButton(steamRow, "Invite", Loc.T("ui.invite"), OnInvite, ButtonFont); // label set by RefreshPanel
        _inviteLabel = _inviteButton.GetComponentInChildren<TextMeshProUGUI>(true);
        _copyCodeButton = Track(MakeButton(steamRow, "CopyCode", Loc.T("ui.copy_code"), OnCopyCode, ButtonFont), "ui.copy_code");

        // Direct: IP or a Steam lobby code (local tests, LAN, VPN).
        _directHeader = MakeSection(panel, "ui.sec_direct");
        var directRow = MakeRow(panel, "AddressRow");
        _directRow = directRow.gameObject;
        string lastAddress = DefaultAddress;
        try { if (!string.IsNullOrWhiteSpace(Plugin.LastAddress.Value)) lastAddress = Plugin.LastAddress.Value.Trim(); } catch { }
        _ipField = MakeInputField(directRow, "Address", lastAddress);
        Flex(_ipField.gameObject, 1f);
        _pasteButton = Track(MakeButton(directRow, "Paste", Loc.T("ui.paste"), OnPaste, 26f), "ui.paste");
        Flex(_pasteButton.gameObject, 0f, 140f);
        var directButtons = MakeRow(panel, "DirectButtons");
        _directButtons = directButtons.gameObject;
        _joinButton = Track(MakeButton(directButtons, "Join", Loc.T("ui.join"), OnJoin, ButtonFont), "ui.join");
        _hostButton = Track(MakeButton(directButtons, "Host", Loc.T("ui.host_ip"), OnHost, ButtonFont), "ui.host_ip");

        // Status (and the group, in a session).
        _groupHeader = MakeSection(panel, "ui.sec_group");
        var statusBox = MakeRect(panel, "StatusBox");
        var sbg = statusBox.gameObject.AddComponent<Image>();
        if (!Dress(sbg, _frameSprite, 0f)) sbg.color = new Color(0f, 0f, 0f, 0.4f);
        sbg.color = new Color(1f, 1f, 1f, 0.55f);
        sbg.raycastTarget = false;
        var sl = statusBox.gameObject.AddComponent<VerticalLayoutGroup>();
        sl.padding = new RectOffset { left = 18, right = 18, top = 12, bottom = 12 };
        sl.childControlWidth = true;
        sl.childControlHeight = true;
        sl.childForceExpandWidth = true;
        statusBox.gameObject.AddComponent<LayoutElement>().minHeight = 56f;
        _status = MakeText(statusBox, "Status", "", 26f, TextAlignmentOptions.Center);
        _status.enableWordWrapping = true;
        _status.lineSpacing = 8f;
        _status.color = new Color(0.92f, 0.92f, 0.92f, 1f);

        var leaveRow = MakeRow(panel, "LeaveRow");
        _leaveRow = leaveRow.gameObject;
        _leaveButton = Track(MakeButton(leaveRow, "Leave", Loc.T("ui.disconnect"), OnLeave, ButtonFont), "ui.disconnect");

        // Co-op options: the host's (they shape the run for everyone) and the player's own; folded by default.
        var optRow = MakeRow(panel, "OptionsToggleRow");
        _optionsButton = MakeButton(optRow, "Options", Loc.T("ui.options"), OnToggleOptions, ButtonFont);
        var box = MakeRect(panel, "OptionsBox");
        var bl = box.gameObject.AddComponent<VerticalLayoutGroup>();
        bl.spacing = 8f;
        bl.childControlWidth = true;
        bl.childControlHeight = true;
        bl.childForceExpandWidth = true;
        bl.childForceExpandHeight = false;
        _optionsBox = box.gameObject;
        _optButtons.Clear();
        MakeSection(box, "ui.sec_host_opts");
        AddOptions(box, Options.Where(o => o.HostOnly).ToList());
        MakeSection(box, "ui.sec_my_opts");
        AddOptions(box, Options.Where(o => !o.HostOnly).ToList());
        _optionsBox.SetActive(false);
        RefreshOptions();

        var version = MakeText(panel, "Version", $"mod {Plugin.HandshakeVersion}", 18f, TextAlignmentOptions.Right);
        version.color = new Color(1f, 1f, 1f, 0.35f);
        version.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;

        _panel.SetActive(false);
        _lastStatus = "";
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Co-op panel built (frame={_frameSprite?.name ?? "-"}, title={_titleSprite?.name ?? "-"}, " +
            $"button={_buttonSprite?.name ?? "-"}/{_buttonHotSprite?.name ?? "-"}, input={_inputSprite?.name ?? "-"}, section={_sectionSprite?.name ?? "-"}).");
    }

    // ---- co-op options ----

    private sealed class Opt
    {
        public string Key = "";
        public bool HostOnly;
        public Func<string> Value = () => "";
        public Action Next = () => { };
    }

    private static string OnOff(bool v) => Loc.T(v ? "opt.on" : "opt.off");

    private static T Cycle<T>(T[] values, T current) where T : IEquatable<T>
    {
        int i = Array.FindIndex(values, v => v.Equals(current));
        return values[(i + 1) % values.Length];
    }

    private static readonly float[] HealthSteps = { 0f, 25f, 50f, 75f, 100f, 150f, 200f, 300f };
    private static readonly float[] CrowdSteps = { 0f, 0.25f, 0.5f, 1f };

    private static readonly Opt[] Options =
    {
        new() { Key = "opt.enemy_hp", HostOnly = true,
            Value = () => $"+{Plugin.EnemyHealthBonus.Value:0}%",
            Next = () => Plugin.EnemyHealthBonus.Value = Cycle(HealthSteps, Plugin.EnemyHealthBonus.Value) },
        new() { Key = "opt.enemy_count", HostOnly = true,
            Value = () => Plugin.EnemyScalePerPlayer.Value <= 0f ? Loc.T("opt.off") : $"+{Plugin.EnemyScalePerPlayer.Value * 100f:0}%",
            Next = () => Plugin.EnemyScalePerPlayer.Value = Cycle(CrowdSteps, Plugin.EnemyScalePerPlayer.Value) },
        new() { Key = "opt.rejoin", HostOnly = true,
            Value = () => OnOff(Plugin.AllowRejoin.Value),
            Next = () => Plugin.AllowRejoin.Value = !Plugin.AllowRejoin.Value },
        new() { Key = "opt.max_players", HostOnly = true,
            Value = () => Plugin.MaxPlayers.Value.ToString(),
            Next = () => Plugin.MaxPlayers.Value = Plugin.MaxPlayers.Value >= 8 ? 2 : Plugin.MaxPlayers.Value + 1 },
        new() { Key = "opt.lobby", HostOnly = true,
            Value = () => Loc.T("opt.lobby_" + Plugin.LobbyType.Value.ToString().ToLowerInvariant()),
            Next = () => Plugin.LobbyType.Value = Plugin.LobbyType.Value == Plugin.SteamLobbyType.FriendsOnly ? Plugin.SteamLobbyType.Public
                : Plugin.LobbyType.Value == Plugin.SteamLobbyType.Public ? Plugin.SteamLobbyType.Private : Plugin.SteamLobbyType.FriendsOnly },
        new() { Key = "opt.levelup_pause", HostOnly = false,
            Value = () => OnOff(Net.LevelUpPolicy.PauseOnLevelUp.Value),
            Next = () => Net.LevelUpPolicy.PauseOnLevelUp.Value = !Net.LevelUpPolicy.PauseOnLevelUp.Value },
        new() { Key = "opt.dash_dodge", HostOnly = false,
            Value = () => OnOff(Plugin.DashDodge.Value),
            Next = () => Plugin.DashDodge.Value = !Plugin.DashDodge.Value },
        new() { Key = "opt.end_host_left", HostOnly = false,
            Value = () => OnOff(Plugin.EndRunWhenHostLeaves.Value),
            Next = () => Plugin.EndRunWhenHostLeaves.Value = !Plugin.EndRunWhenHostLeaves.Value },
    };

    private static Button? _optionsButton;
    private static GameObject? _optionsBox;
    private static readonly List<(TextMeshProUGUI label, Opt opt)> _optButtons = new();

    /// <summary>Two options per row, rows lower than the panel's main ones: the panel stays on screen.</summary>
    private static void AddOptions(RectTransform box, List<Opt> opts)
    {
        for (int i = 0; i < opts.Count; i += 2)
        {
            var row = MakeRow(box, "Opts-" + i);
            var le = row.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = 46f;
            AddOption(row, opts[i]);
            if (i + 1 < opts.Count) AddOption(row, opts[i + 1]);
        }
    }

    private static void AddOption(RectTransform row, Opt o)
    {
        TextMeshProUGUI? label = null;
        var b = MakeButton(row, o.Key, "", () =>
        {
            try { o.Next(); }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Option {o.Key}: {ex.Message}"); }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Option {o.Key} -> {o.Value()}.");
            RefreshOptions();
        }, 20f);
        label = b.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) { label.enableWordWrapping = true; label.fontSizeMin = 14f; label.enableAutoSizing = true; label.fontSizeMax = 20f; }
        if (label != null) _optButtons.Add((label, o));
    }

    private static void RefreshOptions()
    {
        foreach (var (label, o) in _optButtons)
            if (label != null) label.text = $"{Loc.T(o.Key)}\n<color=#f2cc73>{o.Value()}</color>";
    }

    private static void OnToggleOptions()
    {
        if (_optionsBox == null) return;
        _optionsBox.SetActive(!_optionsBox.activeSelf);
        RefreshOptions();
    }

    /// <summary>Test bridge: "cmd options [n]" opens the options and presses the n-th one.</summary>
    internal static string TestOptions(string[] args)
    {
        if (_optionsBox == null) return "no panel";
        if (!_optionsBox.activeSelf) OnToggleOptions();
        if (args.Length > 0 && int.TryParse(args[0], out int n) && n >= 0 && n < _optButtons.Count)
        {
            _optButtons[n].opt.Next();
            RefreshOptions();
        }
        return string.Join(" | ", _optButtons.Select(o => $"{o.opt.Key}={o.opt.Value()}"));
    }

    /// <summary>Section title on the gold fading line the game puts behind its skill list titles.</summary>
    private static GameObject MakeSection(Transform parent, string key)
    {
        var rt = MakeRect(parent, "Section-" + key);
        rt.gameObject.AddComponent<LayoutElement>().preferredHeight = 38f;
        if (_sectionSprite != null)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _sectionSprite;
            img.raycastTarget = false;
        }
        var t = MakeText(rt, "Label", Loc.T(key), 28f, TextAlignmentOptions.Center);
        AutoSize(t, 28f);
        t.raycastTarget = false;
        Stretch(t.rectTransform, 2f);
        _labels.Add((t, key));
        return rt.gameObject;
    }

    /// <summary>A row of equal-width controls, one row high.</summary>
    private static RectTransform MakeRow(Transform parent, string name)
    {
        var rt = MakeRect(parent, name);
        var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = RowGap;
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = true;
        h.childForceExpandHeight = true;
        rt.gameObject.AddComponent<LayoutElement>().preferredHeight = RowHeight;
        return rt;
    }

    private static void Flex(GameObject go, float flexible, float width = -1f)
    {
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        le.flexibleWidth = flexible;
        if (width > 0f) { le.preferredWidth = width; le.minWidth = width; }
    }

    private static void Ignore(RectTransform rt)
    {
        var le = rt.GetComponent<LayoutElement>() ?? rt.gameObject.AddComponent<LayoutElement>();
        le.ignoreLayout = true;
    }

    private static Button MakeCloseButton(Transform parent)
    {
        if (_closeSprite == null) return MakeTextButton(parent, "Close", "X", OnClose, 34f);
        var rt = MakeRect(parent, "Close");
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = _closeSprite;
        img.preserveAspect = true;
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        var colors = b.colors;
        colors.highlightedColor = new Color(1f, 0.85f, 0.85f, 1f);
        colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        b.colors = colors;
        AddClick(b, OnClose, "Close");
        return b;
    }

    /// <summary>Puts one of the game's sprites on an image: as is when it's already 9-sliced (or when height is 0,
    /// with its own borders), else as a sliced copy whose end caps keep their shape at any width
    /// (<see cref="Sliced"/>). False when there is no sprite.</summary>
    private static bool Dress(Image img, Sprite? sprite, float height)
    {
        if (sprite == null) return false;
        var s = height > 0f ? Sliced(sprite) : sprite;
        img.sprite = s;
        img.type = s.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        // Scale the caps with the height (the art is drawn ~140 px high, the controls are ~60).
        if (height > 0f) img.pixelsPerUnitMultiplier = sprite.rect.height / height;
        img.color = Color.white;
        return true;
    }

    private static readonly Dictionary<string, Sprite> _sliced = new();

    /// <summary>The game draws its buttons and bars as Simple images in one aspect ratio; ours come in other widths,
    /// so they get a copy of the sprite with left/right borders around the pointed end caps (top/bottom 0).</summary>
    private static Sprite Sliced(Sprite src)
    {
        if (src.border.x > 0f || src.border.z > 0f) return src;
        if (_sliced.TryGetValue(src.name, out var cached) && cached != null) return cached;
        float cap = Mathf.Min(src.rect.height * 0.75f, src.rect.width * 0.3f);
        var s = Sprite.Create(src.texture, src.textureRect, new Vector2(0.5f, 0.5f), src.pixelsPerUnit, 0u,
            SpriteMeshType.FullRect, new Vector4(cap, 0f, cap, 0f));
        s.name = src.name + "_Sliced";
        _sliced[src.name] = s;
        return s;
    }

    private static Button Track(Button b, string key)
    {
        var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
        if (t != null) _labels.Add((t, key));
        return b;
    }

    private static void AutoSize(TextMeshProUGUI t, float size)
    {
        // Translations are longer or shorter than the Portuguese the layout was made for.
        t.enableAutoSizing = true;
        t.fontSizeMax = size;
        t.fontSizeMin = Mathf.Round(size * 0.55f);
    }

    /// <summary>The language (or the game's font for it) changed: texts set at build time, the font of every text
    /// of ours (the game swaps fonts per language: Cyrillic, CJK) and the tab clone's own font.</summary>
    private static void ApplyLanguage()
    {
        _locVersion = Loc.Version;
        if (_root == null) return;
        if (_fontVersion != Loc.Version)
        {
            _fontVersion = Loc.Version;
            var font = Loc.GameFont(_fontType);
            if (font != null && (_font == null || font.Pointer != _font.Pointer))
            {
                _font = font;
                foreach (var t in _root.GetComponentsInChildren<TextMeshProUGUI>(true)) t.font = font;
                if (_ipField != null) _ipField.fontAsset = font;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Font for {Loc.Codes[(int)Loc.Current]}: {font.name}.");
            }
            if (_tab != null)
                foreach (var lr in _tab.GetComponentsInChildren<LabelReferences>(true))
                    try { lr.UpdateFont(); } catch { /* the game's own label: leave it */ }
            Loc.CheckGlyphs(_font, "co-op UI");
        }
        foreach (var (t, key) in _labels) if (t != null) t.text = Loc.T(key);
        if (_optionsButton != null) { var ol = _optionsButton.GetComponentInChildren<TextMeshProUGUI>(true); if (ol != null) ol.text = Loc.T("ui.options"); }
        RefreshOptions();
        _lastStatus = "";
    }

    /// <summary>The game's main font (the one on the menu tabs) and the art of its hub panels, by sprite name:
    /// frame and glow of the character panel, its title frame, the skill list title line, the Button/Button_Selected
    /// pair of the menu's buttons (wishlist, "learn more", back), the rune preset name field and the close button.</summary>
    private static void FindGameLook()
    {
        try
        {
            var mm = UnityEngine.Object.FindObjectOfType<MenuManager>();
            var panel = mm != null ? mm.MainMenuPanel : null;
            if (panel != null)
            {
                _tabSprite = panel.ButtonRegularSprite;
                _tabSelectedSprite = panel.ButtonSelectedSprite;
                var label = panel.FightButton != null ? panel.FightButton.GetComponentInChildren<TextMeshProUGUI>(true) : null;
                if (label != null && label.font != null) _font = label.font;
                // The game picks that label's font per language by its LabelReferences.Type (LocalizationManager).
                var refs = panel.FightButton != null ? panel.FightButton.GetComponentInChildren<LabelReferences>(true) : null;
                if (refs != null) _fontType = refs.Type;
            }

            var byName = new Dictionary<string, Sprite>();
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s != null && s.texture != null && !byName.ContainsKey(s.name)) byName[s.name] = s;
            Sprite? Get(string name) => byName.TryGetValue(name, out var s) ? s : null;
            _frameSprite = Get("Menu_Frame_NotTransparent") ?? _frameSprite;
            _titleSprite = Get("Title_Frame") ?? _titleSprite;
            _glowSprite = Get("Yellow_Glow_01") ?? _glowSprite;
            _sectionSprite = Get("SkillList_Title_Frame") ?? _sectionSprite;
            _closeSprite = Get("Close_Button") ?? _closeSprite;
            _buttonSprite = Get("Button") ?? _buttonSprite;
            _buttonHotSprite = Get("Button_Selected") ?? _buttonHotSprite;
            _inputSprite = Get("Craft_Background_List_Frame") ?? _inputSprite;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Reading the game's UI look: {ex.Message}");
        }

        if (_font == null)
        {
            // Most used font in the scene.
            var counts = new Dictionary<IntPtr, (TMP_FontAsset Font, int N)>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>(true))
            {
                var f = t != null ? t.font : null;
                if (f == null) continue;
                counts[f.Pointer] = counts.TryGetValue(f.Pointer, out var c) ? (f, c.N + 1) : (f, 1);
            }
            int bestN = 0;
            foreach (var kv in counts.Values) if (kv.N > bestN) { _font = kv.Font; bestN = kv.N; }
        }
        _font ??= TMP_Settings.defaultFontAsset;
    }

    private static void AddClick(Button b, Action onClick, string name)
    {
        b.onClick.AddListener((UnityAction)new Action(() =>
        {
            try { onClick(); }
            catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][ui] {name}: {ex}"); }
        }));
    }

    private static RectTransform MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    private static TextMeshProUGUI MakeText(Transform parent, string name, string text, float size, TextAlignmentOptions align)
    {
        var rt = MakeRect(parent, name);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.font = _font;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.enableWordWrapping = false;
        t.richText = true;
        t.text = text;
        return t;
    }

    /// <summary>The game's menu button (Button), turning gold (Button_Selected) when hovered or pressed, like its
    /// wishlist and "learn more" buttons; fallback: flat color.</summary>
    private static Button MakeButton(Transform parent, string name, string label, Action onClick, float size)
    {
        var rt = MakeRect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        if (Dress(img, _buttonSprite, RowHeight))
        {
            if (_buttonHotSprite != null)
            {
                var hot = Sliced(_buttonHotSprite);
                b.transition = Selectable.Transition.SpriteSwap;
                // SpriteState is boxed in the interop and its pressedSprite setter was stripped: set the fields.
                var state = new SpriteState();
                state.m_HighlightedSprite = hot;
                state.m_PressedSprite = hot;
                state.m_SelectedSprite = hot;
                b.spriteState = state;
            }
        }
        else img.color = ButtonFallbackColor;
        AddClick(b, onClick, name);
        var t = MakeText(rt, "Label", label, size, TextAlignmentOptions.Center);
        AutoSize(t, size);
        t.raycastTarget = false;
        Stretch(t.rectTransform, 0f);
        // Inside the pointed end caps.
        t.rectTransform.offsetMin = new Vector2(30f, 4f);
        t.rectTransform.offsetMax = new Vector2(-30f, -2f);
        return b;
    }

    /// <summary>Text-only button, like the Yes/Cancel of the game's confirmation panel.</summary>
    private static Button MakeTextButton(Transform parent, string name, string label, Action onClick, float size)
    {
        var t = MakeText(parent, name, label, size, TextAlignmentOptions.Center);
        var b = t.gameObject.AddComponent<Button>();
        b.targetGraphic = t;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        AddClick(b, onClick, name);
        return b;
    }

    /// <summary>TMP_InputField wired by hand on the frame of the game's rune preset name field: background, a masked
    /// text area, placeholder and text. The component is added with the GameObject inactive and enabled only once its
    /// references are set.</summary>
    private static TMP_InputField MakeInputField(Transform parent, string name, string initial)
    {
        var rt = MakeRect(parent, name);
        rt.gameObject.SetActive(false);
        var bg = rt.gameObject.AddComponent<Image>();
        if (!Dress(bg, _inputSprite, RowHeight)) bg.color = new Color(0f, 0f, 0f, 0.65f);

        var area = MakeRect(rt, "TextArea");
        Stretch(area, 0f);
        area.offsetMin = new Vector2(36f, 6f);
        area.offsetMax = new Vector2(-36f, -6f);
        area.gameObject.AddComponent<RectMask2D>();

        var placeholder = MakeText(area, "Placeholder", Loc.T("ui.ip_hint"), 28f, TextAlignmentOptions.Left);
        _labels.Add((placeholder, "ui.ip_hint"));
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        placeholder.fontStyle = FontStyles.Italic;
        Stretch(placeholder.rectTransform, 0f);

        var text = MakeText(area, "Text", "", 28f, TextAlignmentOptions.Left);
        Stretch(text.rectTransform, 0f);

        var input = rt.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.fontAsset = _font;
        input.pointSize = 28f;
        input.characterLimit = 64;
        input.contentType = TMP_InputField.ContentType.Standard;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.navigation = new Navigation { mode = Navigation.Mode.None };
        input.caretColor = AccentColor;
        input.customCaretColor = true;
        input.selectionColor = new Color(0.95f, 0.80f, 0.45f, 0.35f);
        rt.gameObject.SetActive(true);
        input.text = initial;
        return input;
    }

    private static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// <summary>Child placed from its parent's top-left corner (y grows downward).</summary>
    private static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    /// <summary>Child centered horizontally on its parent's top edge, center at y.</summary>
    private static void PlaceTopCenter(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }
}
