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
    private const string TabLabel = "Co-op";

    private static readonly Color PanelTint = new(1f, 1f, 1f, 1f);
    private static readonly Color PanelFallbackColor = new(0.06f, 0.05f, 0.08f, 0.94f);
    private static readonly Color ButtonFallbackColor = new(0.30f, 0.12f, 0.12f, 1f);
    private static readonly Color DisabledTint = new(0.45f, 0.45f, 0.45f, 0.8f);
    private static readonly Color AccentColor = new(0.95f, 0.80f, 0.45f, 1f);
    private static readonly Color BannerColor = new(0f, 0f, 0f, 0.7f);

    // overlay canvas
    private static GameObject? _root;
    private static TMP_FontAsset? _font;
    /// <summary>The game's menu font once the UI found it (phase 5: the ghost's name tag uses it too).</summary>
    internal static TMP_FontAsset? GameFont => _font;
    private static Sprite? _frameSprite, _titleSprite, _tabSprite, _tabSelectedSprite;
    private static GameObject? _fallbackButton;
    private static GameObject? _panel;
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

            if (_inMenu) UpdateMenuTab();
            else _panelOpen = false;

            bool tabOk = _tab != null;
            _fallbackButton!.SetActive(_inMenu && !tabOk && _tabFailures >= 3);
            _panel!.SetActive(_inMenu && _panelOpen);
            if (_inMenu && _panelOpen) RefreshPanel();
            if (!_loggedFocus && _ipField != null && _ipField.isFocused)
            {
                _loggedFocus = true;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] IP field got keyboard focus.");
            }
            RefreshBanner();
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
            if (lobbyState == Steam.SteamLobby.LobbyState.Creating) text = "Criando o lobby na Steam...";
            else if (lobbyState == Steam.SteamLobby.LobbyState.Joining) text = "Entrando no lobby da Steam...";
            else if (error != null) text = $"<color=#ff8080>{error}</color>";
            else text = Steam.SteamLobby.Available ? "Fora do co-op." : "Fora do co-op. Steam indisponível: só por IP.";
        }
        else if (status == NetStatus.Hosting)
            text = steam
                ? $"Lobby aberto na Steam (1/{NetworkManager.MaxPlayers}). Convide amigos (ou mande o código)."
                : $"Hospedando na porta {NetworkManager.Port} (1/{NetworkManager.MaxPlayers}). Aguardando jogadores...";
        else if (status == NetStatus.Connecting)
            text = steam
                ? $"Conectando com {Steam.SteamLobby.FriendName(Steam.SteamLobby.HostId)} pela Steam..."
                : $"Conectando a {NetworkManager.TargetAddress}:{NetworkManager.Port}...";
        else if (status == NetStatus.Connected)
        {
            if (role == NetRole.Client)
            {
                var h = CoopSession.Host;
                int ping = NetworkManager.PingMs;
                text = $"Conectado com <color=#f2cc73>{Who(h)}</color> ({NetworkManager.Kind}{(ping >= 0 ? $", {ping} ms" : "")}).";
                if (NetworkManager.Unstable)
                    text += $" <color=#ff8080>Conexão instável ({NetworkManager.SilenceSeconds:0} s sem resposta)</color>";
                var others = new List<string>();
                foreach (var r in CoopSession.Remotes) if (!r.IsHost) others.Add(Who(r));
                if (others.Count > 0) text += $"\nNo grupo ({Players.Count}/{Players.SessionMax}): {string.Join(", ", others)}";
                text += h == null || h.Place == LobbyPlace.Menu
                    ? "\nAguardando o host começar a partida."
                    : "\nO host está numa partida; espere ele voltar ao menu.";
            }
            else
            {
                text = $"Hospedando ({NetworkManager.Kind}): {Players.Count}/{NetworkManager.MaxPlayers} jogadores";
                bool allInMenu = true;
                foreach (var r in CoopSession.Remotes)
                {
                    int ping = NetworkManager.PingOf(r.Slot);
                    string unstable = NetworkManager.IsUnstable(r.Slot) ? " <color=#ff8080>instável</color>" : "";
                    text += $"\n<color=#f2cc73>{Who(r)}</color>  {(ping >= 0 ? $"{ping} ms" : "-")}  {PlaceLabel(r.Place)}{unstable}";
                    allInMenu &= r.Place == LobbyPlace.Menu;
                }
                text += allInMenu
                    ? "\nComece a partida normalmente: todos entram juntos."
                    : "\nEspere todos voltarem ao menu pra começar.";
            }
        }
        else text = "...";
        if (role == NetRole.Host && NetworkManager.LastError != null && status == NetStatus.Hosting)
            text += $"\n<color=#ff8080>{NetworkManager.LastError}</color>";
        if (role == NetRole.Host && steam && Steam.SteamLobby.LastError != null)
            text += $"\n<color=#ff8080>{Steam.SteamLobby.LastError}</color>";
        if (NetSim.Enabled) text += $"\n<color=#ffb060>Simulação de rede ligada: {NetSim.Describe()}</color>";

        if (text != _lastStatus)
        {
            _lastStatus = text;
            _status!.text = text;
        }

        bool busy = lobbyState == Steam.SteamLobby.LobbyState.Creating || lobbyState == Steam.SteamLobby.LobbyState.Joining;
        bool idle = role == NetRole.None && !busy;
        bool steamHosting = role == NetRole.Host && steam;
        // Phase 9: anyone in the Steam lobby can invite its own friends, while there's a free seat.
        bool steamMember = steamHosting || (role == NetRole.Client && steam && Steam.SteamLobby.State == Steam.SteamLobby.LobbyState.Joined);
        bool full = Players.Count >= Players.SessionMax;
        SetInteractable(_inviteButton!, Steam.SteamLobby.Available && (idle || (steamMember && !full)));
        SetInteractable(_copyCodeButton!, steamMember && Steam.SteamLobby.LobbyId != 0);
        string inviteLabel = steamMember ? $"Convidar amigo ({Players.Count}/{Players.SessionMax})" : "Convidar amigo";
        if (_inviteLabel != null && _inviteLabel.text != inviteLabel) _inviteLabel.text = inviteLabel;
        SetInteractable(_hostButton!, idle);
        SetInteractable(_joinButton!, idle);
        SetInteractable(_pasteButton!, idle);
        SetInteractable(_leaveButton!, !idle);
        _ipField!.interactable = idle;
    }

    private static string Who(RemotePlayer? r)
    {
        if (r == null || !r.Info.Known) return r?.Name ?? "parceiro";
        return r.Info.CharacterName.Length > 0 ? $"{r.Info.Name} ({r.Info.CharacterName})" : r.Info.Name;
    }

    private static string PlaceLabel(LobbyPlace p) => p switch
    {
        LobbyPlace.Menu => "no menu",
        LobbyPlace.Loading => "carregando",
        LobbyPlace.InRun => "na partida",
        LobbyPlace.Transition => "trocando de fase",
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
            text = $"Conexão instável com {names} ({NetworkManager.SilenceSeconds:0} s sem resposta)...";
        }
        else if (RunSync.HoldForPartner) text = $"Aguardando {CoopSession.NamesWhere(r => r.ChangingScene)} carregar...";
        else if ((names = CoopSession.NamesWhere(r => PauseSync.Freezes(r.Slot) && PauseSync.ReasonOf(r.Slot) == PauseReason.Pause)).Length > 0)
            text = NetworkManager.Role == NetRole.Host
                ? $"{names} pausou o jogo (Esc duas vezes ou F4 despausa)"
                : $"{names} pausou o jogo";
        else if ((names = CoopSession.NamesWhere(r => PauseSync.Freezes(r.Slot))).Length > 0)
            text = $"{names} escolhendo upgrade...";
        else if ((names = CoopSession.NamesWhere(r => PauseSync.IsChoosing(r.Slot))).Length > 0)
            text = $"{names} escolhendo upgrade (sem pausa)";
        else if (LevelUpOverlay.BannerText != null) text = LevelUpOverlay.BannerText;
        else if (CoopSession.ActiveNotice != null) text = CoopSession.ActiveNotice;
        else if ((names = CoopSession.NamesWhere(r => PauseSync.IsPausedAlone(r.Slot))).Length > 0)
            text = $"{names} no menu de pausa (o jogo segue)";
        else text = null;

        bool show = text != null;
        var go = _bannerRt!.gameObject;
        if (go.activeSelf != show) go.SetActive(show);
        if (!show) return;
        if (_bannerText!.text != text) _bannerText.text = text;
        // Under the panel when it's open, else near the top of the screen.
        float y = _inMenu && _panelOpen ? -820f : -140f;
        if (Mathf.Abs(_bannerRt.anchoredPosition.y - y) > 0.5f) _bannerRt.anchoredPosition = new Vector2(0f, y);
    }

    private static void SetInteractable(Button b, bool on)
    {
        if (b.interactable == on) return;
        b.interactable = on;
        var img = b.targetGraphic;
        if (img != null) img.color = on ? (_tabSprite != null ? Color.white : ButtonFallbackColor) : DisabledTint;
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
        CoopSession.Notice(ok ? "Código do lobby copiado: mande pro amigo colar em Entrar" : "Não deu pra copiar o código", 5f);
    }

    private static void OnPaste()
    {
        string? clip = WindowsClipboard.TryGetText();
        string text = clip?.Trim() ?? "";
        // Only the first line / word: an address, not a whole paragraph.
        int cut = text.IndexOfAny(new[] { '\r', '\n', ' ', '\t' });
        if (cut > 0) text = text.Substring(0, cut);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Paste pressed: '{text}' (clipboard {(clip == null ? "unavailable" : $"{clip.Length} chars")}).");
        if (text.Length == 0) { CoopSession.Notice("A área de transferência está vazia"); return; }
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
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();
        var rootRt = root.GetComponent<RectTransform>();

        // Fallback opener, top right (only if the tab can't be added).
        var open = MakeButton(rootRt, "CoopButton", TabLabel, OnToggle, 34f);
        Place(open.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(220f, 70f));
        _fallbackButton = open.gameObject;
        _fallbackButton.SetActive(false);

        // Panel, centered under the top menu.
        var panel = MakeRect(rootRt, "CoopPanel");
        Place(panel, new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(720f, 640f)); // phase 9: room for the player list
        var bg = panel.gameObject.AddComponent<Image>();
        if (_frameSprite != null) { bg.sprite = _frameSprite; bg.color = PanelTint; }
        else bg.color = PanelFallbackColor;
        _panel = panel.gameObject;

        var titleHolder = MakeRect(panel, "TitleHolder");
        PlaceTopCenter(titleHolder, 0f, -38f, 380f, 84f);
        if (_titleSprite != null) titleHolder.gameObject.AddComponent<Image>().sprite = _titleSprite;
        var title = MakeText(titleHolder, "Title", TabLabel, 46f, TextAlignmentOptions.Center);
        Stretch(title.rectTransform, 0f);

        var version = MakeText(panel, "Version", $"mod {Plugin.HandshakeVersion}", 20f, TextAlignmentOptions.Right);
        version.color = new Color(1f, 1f, 1f, 0.45f);
        PlaceTopLeft(version.rectTransform, 470f, 18f, 220f, 30f);

        var close = MakeTextButton(panel, "Close", "X", OnClose, 34f);
        PlaceTopLeft(close.GetComponent<RectTransform>(), 650f, 46f, 50f, 50f);

        // Phase 7: Steam invite first (the real way to play), IP below it (local tests, LAN, VPN).
        _inviteButton = MakeButton(panel, "Invite", "Convidar amigo", OnInvite, 30f);
        _inviteLabel = _inviteButton.GetComponentInChildren<TextMeshProUGUI>(true);
        PlaceTopLeft(_inviteButton.GetComponent<RectTransform>(), 60f, 70f, 390f, 70f);

        _copyCodeButton = MakeButton(panel, "CopyCode", "Copiar código", OnCopyCode, 28f);
        PlaceTopLeft(_copyCodeButton.GetComponent<RectTransform>(), 460f, 70f, 200f, 70f);

        _hostButton = MakeButton(panel, "Host", "Hospedar por IP", OnHost, 30f);
        PlaceTopLeft(_hostButton.GetComponent<RectTransform>(), 60f, 152f, 600f, 62f);

        string lastAddress = DefaultAddress;
        try { if (!string.IsNullOrWhiteSpace(Plugin.LastAddress.Value)) lastAddress = Plugin.LastAddress.Value.Trim(); } catch { }
        _ipField = MakeInputField(panel, "Address", lastAddress);
        PlaceTopLeft(_ipField.GetComponent<RectTransform>(), 60f, 230f, 290f, 62f);

        _pasteButton = MakeButton(panel, "Paste", "Colar", OnPaste, 30f);
        PlaceTopLeft(_pasteButton.GetComponent<RectTransform>(), 360f, 226f, 120f, 70f);

        _joinButton = MakeButton(panel, "Join", "Entrar", OnJoin, 34f);
        PlaceTopLeft(_joinButton.GetComponent<RectTransform>(), 490f, 226f, 170f, 70f);

        _leaveButton = MakeButton(panel, "Leave", "Desconectar", OnLeave, 30f);
        PlaceTopLeft(_leaveButton.GetComponent<RectTransform>(), 60f, 312f, 600f, 62f);

        _status = MakeText(panel, "Status", "", 22f, TextAlignmentOptions.Top);
        _status.enableWordWrapping = true;
        PlaceTopLeft(_status.rectTransform, 60f, 388f, 600f, 236f);

        // Banner (menu and runs).
        _bannerRt = MakeRect(rootRt, "CoopBanner");
        Place(_bannerRt, new Vector2(0.5f, 1f), new Vector2(0f, -140f), new Vector2(1000f, 70f));
        var bimg = _bannerRt.gameObject.AddComponent<Image>();
        bimg.color = BannerColor;
        bimg.raycastTarget = false;
        _bannerText = MakeText(_bannerRt, "Text", "", 34f, TextAlignmentOptions.Center);
        _bannerText.raycastTarget = false;
        Stretch(_bannerText.rectTransform, 12f);
        _bannerRt.gameObject.SetActive(false);

        _panel.SetActive(false);
        _root = root;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Co-op UI built (font {_font.name}, frame={_frameSprite?.name ?? "-"}, title={_titleSprite?.name ?? "-"}, tab={_tabSprite?.name ?? "-"}).");
    }

    /// <summary>The game's main font (the one on the menu tabs) and the sprites of its Confirmation-Panel and tabs.</summary>
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
            }
            var conf = UnityEngine.Object.FindObjectOfType<ConfirmationPanel>(true);
            if (conf != null)
            {
                var frame = conf.transform.Find("Text-Background");
                var titleHolder = conf.transform.Find("Settings-Title-Holder");
                _frameSprite = frame != null ? frame.GetComponent<Image>()?.sprite : null;
                _titleSprite = titleHolder != null ? titleHolder.GetComponent<Image>()?.sprite : null;
            }
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

    /// <summary>Button with the game's menu tab sprite (fallback: flat color) and a centered label.</summary>
    private static Button MakeButton(Transform parent, string name, string label, Action onClick, float size)
    {
        var rt = MakeRect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        if (_tabSprite != null) { img.sprite = _tabSprite; img.color = Color.white; }
        else img.color = ButtonFallbackColor;
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        AddClick(b, onClick, name);
        var t = MakeText(rt, "Label", label, size, TextAlignmentOptions.Center);
        t.raycastTarget = false;
        Stretch(t.rectTransform, 4f);
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

    /// <summary>TMP_InputField wired by hand: background, a masked text area, placeholder and text. The component
    /// is added with the GameObject inactive and enabled only once its references are set.</summary>
    private static TMP_InputField MakeInputField(Transform parent, string name, string initial)
    {
        var rt = MakeRect(parent, name);
        rt.gameObject.SetActive(false);
        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.65f);

        var area = MakeRect(rt, "TextArea");
        Stretch(area, 12f);
        area.gameObject.AddComponent<RectMask2D>();

        var placeholder = MakeText(area, "Placeholder", "IP ou código", 30f, TextAlignmentOptions.Left);
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        placeholder.fontStyle = FontStyles.Italic;
        Stretch(placeholder.rectTransform, 0f);

        var text = MakeText(area, "Text", "", 30f, TextAlignmentOptions.Left);
        Stretch(text.rectTransform, 0f);

        var input = rt.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.fontAsset = _font;
        input.pointSize = 30f;
        input.characterLimit = 64;
        input.contentType = TMP_InputField.ContentType.Standard;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.navigation = new Navigation { mode = Navigation.Mode.None };
        input.caretColor = AccentColor;
        input.customCaretColor = true;
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
