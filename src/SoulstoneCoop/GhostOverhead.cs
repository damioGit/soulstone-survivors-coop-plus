using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoulstoneCoop;

/// <summary>
/// Phase 5: what marks the partner's ghost as the ally.
/// - Name (Steam name from the Hello) and health bar (PartnerHealth packet) above the ghost, on a screen-space
///   overlay canvas of our own. Each frame it's positioned where the camera sees the top of the ghost, so it stays
///   readable over spell effects, at a fixed size. The game's combat text is out (16-character buffer, short-lived),
///   and so is OnGUI (stripped in this build). The game's own overhead bar needs a HealthComponent, and the ghost has none, so
///   only its ally sprite is borrowed. The canvas sorts below the game's own overlays and hides while the game is
///   frozen (pause, level-up, loading barrier, end screen).
/// - Minimap icon: MinimapManager.RequestIcon(Player, ghost transform), removed when the ghost goes. Its OnUpdate
///   checks the transform against null and falls back to the stored position, so a destroyed ghost can't throw
///   in there (decompiled 0x615430). SetupMinimap clears every item on a new map.
/// </summary>
public static class GhostOverhead
{
    internal static readonly Color AllyColor = new(0.35f, 1f, 0.5f, 1f);
    private static readonly Color DownColor = new(0.95f, 0.35f, 0.3f, 1f);
    private const int MaxChars = 24;
    private const float NameSize = 26f;
    private const float BarWidth = 130f, BarHeight = 14f;

    private static GameObject? _root;
    private static RectTransform? _panel;
    private static TextMeshProUGUI? _name;
    private static GameObject? _bar;
    private static RectTransform? _fill;
    private static Image? _fillImage;
    private static bool _fillUsesSprite;
    private static string _shownName = "";
    private static bool _buildFailed;

    private static IntPtr _ghostPtr;
    private static float _height = 2.6f;

    private static int _iconId = -1;
    private static IntPtr _iconGhostPtr;
    private static float _iconRunStart;
    private static bool _iconFailed;

    private static float _telemetryTimer;
    private static bool _visibleNow;

    /// <summary>Called by VisualGhostFactory for each new ghost, with the height of its model.</summary>
    public static void Attach(GameObject ghost, float height)
    {
        _ghostPtr = ghost.Pointer;
        _height = height;
        EnsureBuilt();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] Overhead follows the ghost {height:0.0} above its root (name '{Lobby.CoopSession.PartnerName}', ui={(_root != null)}).");
    }

    public static void Tick(float unscaledDeltaTime)
    {
        try { UpdateMinimapIcon(); }
        catch (Exception ex)
        {
            _iconFailed = true;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][hpbar] Minimap icon disabled: {ex.Message}");
        }
        try { UpdateOverhead(); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][hpbar] Overhead tick: {ex.Message}");
            SetVisible(false);
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < 5f) return;
        _telemetryTimer = 0f;
        if (GameState.RemoteGhostObject == null && !Life.PartnerHealth.Known) return;
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][hpbar] partner hp={Life.PartnerHealth.Health:F0}/{Life.PartnerHealth.MaxHealth:F0} known={Life.PartnerHealth.Known} " +
            $"down={Life.PartnerHealth.Down} shown={_visibleNow} icon={_iconId} {Life.PartnerHealth.TakeStats()}");
    }

    // ---------------- overhead ----------------

    private static void UpdateOverhead()
    {
        var ghost = GameState.RemoteGhostObject;
        if (_root == null || _panel == null || ghost == null || !Lobby.CoopSession.LocalInRun || Time.timeScale < 0.01f)
        {
            SetVisible(false);
            return;
        }
        if (ghost.Pointer != _ghostPtr) { _ghostPtr = ghost.Pointer; _height = 2.6f; } // a ghost Attach didn't see

        var cam = FindCamera();
        if (cam == null) { SetVisible(false); return; }
        Vector3 screen = cam.WorldToScreenPoint(ghost.transform.position + Vector3.up * _height);
        if (screen.z <= 0f) { SetVisible(false); return; }
        _panel.position = new Vector3(Mathf.Round(screen.x), Mathf.Round(screen.y), 0f);

        bool down = Life.CoopLife.RemoteDowned || Life.PartnerHealth.Down || Life.PartnerHealth.Dead;
        string name = Lobby.CoopSession.PartnerName;
        if (name.Length > MaxChars) name = name.Substring(0, MaxChars - 3) + "...";
        if (down) name += " (caído)";
        if (_name != null && name != _shownName)
        {
            _shownName = name;
            _name.text = name;
        }
        if (_name != null) _name.color = down ? DownColor : AllyColor;

        bool hasHp = Life.PartnerHealth.Known && Life.PartnerHealth.MaxHealth > 0f;
        if (_bar != null && _bar.activeSelf != hasHp) _bar.SetActive(hasHp);
        if (hasHp && _fill != null)
        {
            float frac = down ? 0f : Life.PartnerHealth.Fraction;
            _fill.sizeDelta = new Vector2((BarWidth - 4f) * frac, BarHeight - 4f);
            if (_fill.gameObject.activeSelf != frac > 0.005f) _fill.gameObject.SetActive(frac > 0.005f);
            if (_fillImage != null) _fillImage.color = _fillUsesSprite ? Color.white : AllyColor;
        }
        SetVisible(true);
    }

    private static void SetVisible(bool on)
    {
        _visibleNow = on;
        if (_panel != null && _panel.gameObject.activeSelf != on) _panel.gameObject.SetActive(on);
    }

    private static void EnsureBuilt()
    {
        if (_root != null || _buildFailed) return;
        try
        {
            _root = new GameObject("SoulstoneCoop_Overhead");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -100; // under the game's own overlay panels
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _panel = MakeRect(_root.transform, "Panel");
            _panel.sizeDelta = new Vector2(320f, 60f);
            _panel.pivot = new Vector2(0.5f, 0f); // bottom center sits on the top of the ghost

            var nameRt = MakeRect(_panel, "Name");
            nameRt.anchorMin = nameRt.anchorMax = new Vector2(0.5f, 0f);
            nameRt.pivot = new Vector2(0.5f, 0f);
            nameRt.anchoredPosition = new Vector2(0f, BarHeight + 4f);
            nameRt.sizeDelta = new Vector2(320f, 34f);
            var t = nameRt.gameObject.AddComponent<TextMeshProUGUI>();
            var font = FindFont();
            if (font != null) t.font = font;
            t.fontSize = NameSize;
            t.fontStyle = FontStyles.Bold;
            t.alignment = TextAlignmentOptions.Bottom;
            t.enableWordWrapping = false;
            t.richText = false;
            t.raycastTarget = false;
            t.color = AllyColor;
            try
            {
                t.outlineWidth = 0.3f;
                t.outlineColor = new Color32(0, 0, 0, 255);
                var mat = t.fontMaterial; // the instance the outline went into
                mat.EnableKeyword("UNDERLAY_ON");
                mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.8f));
                mat.SetFloat("_UnderlayOffsetX", 0.6f);
                mat.SetFloat("_UnderlayOffsetY", -0.6f);
                mat.SetFloat("_UnderlaySoftness", 0.3f);
            }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][hpbar] Name outline/shadow: {ex.Message}"); }
            _name = t;

            var barRt = MakeRect(_panel, "HealthBar");
            barRt.anchorMin = barRt.anchorMax = new Vector2(0.5f, 0f);
            barRt.pivot = new Vector2(0.5f, 0f);
            barRt.anchoredPosition = Vector2.zero;
            barRt.sizeDelta = new Vector2(BarWidth, BarHeight);
            var bg = barRt.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.8f);
            bg.raycastTarget = false;

            _fill = MakeRect(barRt, "Fill");
            _fill.anchorMin = _fill.anchorMax = new Vector2(0f, 0.5f); // grows from the left edge
            _fill.pivot = new Vector2(0f, 0.5f);
            _fill.anchoredPosition = new Vector2(2f, 0f);
            _fill.sizeDelta = new Vector2(BarWidth - 4f, BarHeight - 4f);
            _fillImage = _fill.gameObject.AddComponent<Image>();
            _fillImage.raycastTarget = false;
            var allySprite = FindAllyBarSprite();
            if (allySprite != null)
            {
                _fillImage.sprite = allySprite;
                _fillImage.type = allySprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
                _fillUsesSprite = true;
            }
            _fillImage.color = _fillUsesSprite ? Color.white : AllyColor;
            _bar = barRt.gameObject;
            _bar.SetActive(false);

            _panel.gameObject.SetActive(false);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] Overhead UI built (font={(font != null ? font.name : "TMP default")}, ally sprite={(allySprite != null ? allySprite.name : "none, flat color")}).");
        }
        catch (Exception ex)
        {
            _buildFailed = true;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][hpbar] Overhead UI could not be built (the ghost goes without name/bar): {ex}");
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _panel = null;
        }
    }

    private static RectTransform MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>The co-op UI's font (the game's menu font, found in the menu), else any TMP font in the scene.</summary>
    private static TMP_FontAsset? FindFont()
    {
        var f = Ui.CoopUi.GameFont;
        if (f != null) return f;
        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>(true))
            {
                if (t != null && t.font != null) return t.font;
            }
        }
        catch { /* TMP default below */ }
        return TMP_Settings.defaultFontAsset;
    }

    private static Camera? _gameCam;
    private static float _camSearchTimer;

    /// <summary>The camera the game's own overhead bars project with (OverheadHealthBarPanelManager.GameplayCamera),
    /// else Camera.main. Looked up again every 2 s while missing (each run has its own).</summary>
    private static Camera? FindCamera()
    {
        if (_gameCam != null && _gameCam.isActiveAndEnabled) return _gameCam;
        _camSearchTimer -= Time.unscaledDeltaTime;
        if (_camSearchTimer <= 0f)
        {
            _camSearchTimer = 2f;
            try
            {
                var mgr = UnityEngine.Object.FindObjectOfType<OverheadHealthBarPanelManager>();
                _gameCam = mgr != null ? mgr.GameplayCamera : null;
            }
            catch { _gameCam = null; }
            if (_gameCam != null) return _gameCam;
        }
        return Camera.main;
    }

    /// <summary>The sprite the game's own overhead bars use for allies (OverheadHealthBarPanelManager.AllyHealthBarSprite).</summary>
    private static Sprite? FindAllyBarSprite()
    {
        try
        {
            var mgr = UnityEngine.Object.FindObjectOfType<OverheadHealthBarPanelManager>(true);
            return mgr != null ? mgr.AllyHealthBarSprite : null;
        }
        catch { return null; }
    }

    // ---------------- minimap ----------------

    private static void UpdateMinimapIcon()
    {
        if (_iconFailed) return;
        var ghost = GameState.RemoteGhostObject;
        bool sameRun = Mathf.Approximately(_iconRunStart, Lobby.CoopSession.LocalRunStartTime);
        if (_iconId >= 0 && (ghost == null || ghost.Pointer != _iconGhostPtr || !sameRun))
        {
            // Another map clears the list itself (SetupMinimap); removing there would only log a game error.
            if (sameRun && GameManagerUtil.HasGameManager) MinimapManager.RequestRemoveIcon(_iconId);
            _iconId = -1;
            _iconGhostPtr = IntPtr.Zero;
        }
        if (_iconId >= 0 || ghost == null || !Lobby.CoopSession.LocalInRun || !GameManagerUtil.HasGameManager) return;
        _iconId = MinimapManager.RequestIcon(MinimapIconType.Player, ghost.transform, ghost.transform.position);
        _iconGhostPtr = ghost.Pointer;
        _iconRunStart = Lobby.CoopSession.LocalRunStartTime;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] Minimap icon {_iconId} (Player) requested for the ghost.");
    }
}
