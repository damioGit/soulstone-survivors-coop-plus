using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SoulstoneCoop.Ui;

public enum LevelUpOverlayPosition
{
    Bottom,
    BottomLeft,
    BottomRight,
    Top,
}

/// <summary>
/// The level-up popup as a compact overlay you play under: every upgrade choice in co-op (Net/LevelUpPolicy, issue #1;
/// docs/levelup-notas.md). Nothing is recreated: the game's own popup (LevelUpPanelManager.PowerUpAnimation)
/// is scaled, moved to an edge of the screen and made see-through every frame after the game's OnUpdate, its
/// full-screen backdrop images are switched off, and the HUD stays visible (HasOverlayPanel ignores the popup).
///
/// Input: with the popup fully open the game runs InputManager.SwitchToUIMap (UI map on, gameplay maps off), so the
/// player stands still. Here the gameplay map is switched back on, and the clashes are sorted out per action, by the
/// controls they are bound to: the UI actions sharing a key with a gameplay action (navigation on WASD, submit on the
/// dash key...) are switched off, except the option keys (SelectPowerup1..7), the pause menu and cancel, which win over
/// gameplay. Gameplay mouse buttons are switched off only while the pointer is over the panel, so a click picks an
/// option instead of casting. 1-4 (and the numpad) pick an option when the game's own option keys are bound elsewhere,
/// through LevelUpPanelItem.Selected, the same flag the game's keys set. Tab hides / shows the panel.
/// Gamepad: the left stick keeps moving the character (the UI navigation sharing it is switched off); the cards are on
/// the D-pad, which is reserved like 1-4: left / right move the game's own highlight, up picks it, down hides / shows.
/// </summary>
public static class LevelUpOverlay
{
    // i18n: every text this overlay shows (the banner lines), from Ui/LocTable.cs ("bn.levelup*").
    private static class Texts
    {
        internal static string HiddenKeyboard(int queued) => Loc.T("bn.levelup", ("n", queued));
        internal static string HiddenGamepad(int queued) => Loc.T("bn.levelup_pad", ("n", queued));
        internal static string GamepadHint => Loc.T("bn.levelup_pad_hint");
    }

    internal static ConfigEntry<float> Scale = null!;
    internal static ConfigEntry<float> Opacity = null!;
    internal static ConfigEntry<LevelUpOverlayPosition> Position = null!;

    private const float MarginFraction = 0.02f;

    private static bool _visual, _input, _minimized, _treeLogged, _hovered;
    private static readonly Target PopupTarget = new("popup");
    private static readonly Target ReplaceTarget = new("skill swap");
    private static readonly List<RectTransform> Content = new();
    private static readonly List<RectTransform> ReplaceContent = new();
    private static readonly Il2CppStructArray<Vector3> Corners = new(4);

    // Input bookkeeping: what we switched off and must switch back on.
    private static InputManager? _im;
    private static readonly List<InputAction> OffUi = new();
    private static readonly List<InputAction> OffGameplay = new();
    private static readonly List<InputAction> HoverActions = new();
    private static readonly List<InputAction> OffHover = new();
    private static readonly List<InputAction> OffMinimized = new();

    /// <summary>The overlay styling is on the popup this frame (HasOverlayPanel then ignores the popup).</summary>
    public static bool Visible => _visual;
    /// <summary>Banner line while the overlay is hidden with Tab, else null.</summary>
    public static string? BannerText { get; private set; }

    internal static void Bind(ConfigFile cfg)
    {
        Scale = cfg.Bind("Coop", "LevelUpOverlayScale", 0.6f,
            "Size of the compact level-up panel (1 = the game's size). It also shrinks to fit the screen width.");
        Opacity = cfg.Bind("Coop", "LevelUpOverlayOpacity", 0.8f,
            "Opacity of the compact level-up panel while the mouse is not over it (over it, it is fully opaque).");
        Position = cfg.Bind("Coop", "LevelUpOverlayPosition", LevelUpOverlayPosition.Bottom,
            "Where the compact level-up panel sits: Bottom, BottomLeft, BottomRight or Top.");
    }

    internal static void ResetForRun()
    {
        _minimized = false;
        BannerText = null;
    }

    // =====================================================================================
    // Per frame (LevelUpPanelManager.OnUpdate prefix / postfix)
    // =====================================================================================

    internal static void BeforeUpdate(LevelUpPanelManager panel, bool isPaused, bool endGame)
    {
        if (!_visual || isPaused || endGame) return;
        var kb = Keyboard.current;
        var pad = Gamepad.current;
        bool toggle = (kb != null && kb.tabKey.wasPressedThisFrame) || (pad != null && pad.dpad.down.wasPressedThisFrame);
        if (toggle)
        {
            _minimized = !_minimized;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Overlay {(_minimized ? "hidden" : "shown")}.");
        }
        if (_minimized) return;
        if (kb != null)
        {
            if (kb.digit1Key.wasPressedThisFrame || kb.numpad1Key.wasPressedThisFrame) Pick(panel, 0);
            else if (kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame) Pick(panel, 1);
            else if (kb.digit3Key.wasPressedThisFrame || kb.numpad3Key.wasPressedThisFrame) Pick(panel, 2);
            else if (kb.digit4Key.wasPressedThisFrame || kb.numpad4Key.wasPressedThisFrame) Pick(panel, 3);
        }
        if (pad != null)
        {
            // The stick moves the character; the cards are on the D-pad (left / right highlight, up picks).
            if (pad.dpad.left.wasPressedThisFrame) MoveCursor(panel, -1);
            else if (pad.dpad.right.wasPressedThisFrame) MoveCursor(panel, +1);
            else if (pad.dpad.up.wasPressedThisFrame) Pick(panel, CursorIndex(panel));
        }
    }

    internal static void AfterUpdate(LevelUpPanelManager panel, bool isPaused, bool endGame)
    {
        var anim = panel.PowerUpAnimation;
        if (anim == null) { StopAll(false, false); return; }
        float cur = anim.CurrentTime, target = anim.TargetVisibility;
        bool shown = cur > 0f || target > 0f;
        bool overlay = false;
        bool want = shown && !endGame && Net.LevelUpPolicy.Decide(out overlay, out _) && overlay;

        if (want) ApplyVisuals(panel, Mathf.Clamp01(cur));
        else if (_visual) RestoreVisuals();

        bool gamepad = false;
        try { gamepad = MainUIManager.Instance != null && MainUIManager.Instance.InputManager != null
                        && MainUIManager.Instance.InputManager.ActiveInputType == ActiveInputType.Gamepad; }
        catch { }
        bool inputWanted = want && target >= 1f && !isPaused && TimeUtil.PauseTime >= 0.001f;
        if (inputWanted)
        {
            if (!_input) _input = BeginInput();
            if (_input) ApplyInput();
        }
        else if (_input)
        {
            EndInput(popupStillOpen: target >= 1f, mayReturnToGameplay: !isPaused && !endGame);
        }

        int queued = 0;
        try { queued = panel.PowerUpRequests != null ? panel.PowerUpRequests.Count : 0; } catch { }
        queued = Mathf.Max(1, queued);
        BannerText = !_visual ? null
                   : _minimized ? (gamepad ? Texts.HiddenGamepad(queued) : Texts.HiddenKeyboard(queued))
                   : gamepad ? Texts.GamepadHint
                   : null;
    }

    /// <summary>The panel is gone (scene change / destroyed): undo everything without touching it.</summary>
    private static void StopAll(bool popupStillOpen, bool mayReturnToGameplay)
    {
        if (_visual) RestoreVisuals();
        if (_input) EndInput(popupStillOpen, mayReturnToGameplay);
        BannerText = null;
    }

    // =====================================================================================
    // Visuals
    // =====================================================================================

    private static void ApplyVisuals(LevelUpPanelManager panel, float fade)
    {
        var rt = panel.PowerUpAnimation.transform.TryCast<RectTransform>();
        var canvas = panel.Canvas != null ? panel.Canvas.rootCanvas : null;
        var canvasRt = canvas != null ? canvas.transform.TryCast<RectTransform>() : null;
        if (rt == null || canvasRt == null) return;
        bool first = !_visual;
        _visual = true;
        if (first && !_treeLogged) { _treeLogged = true; LogTree(rt); }

        float alpha = (_minimized ? 0f : _hovered ? 1f : Mathf.Clamp01(Opacity.Value)) * fade;
        float scale = Mathf.Clamp(Scale.Value, 0.2f, 1f);

        // Skill swap (shown when a new skill is picked with every slot full): part of the popup, or its own group.
        var swapAnim = panel.ReplaceSkillsAnimation;
        var swapRt = swapAnim != null ? swapAnim.transform.TryCast<RectTransform>() : null;
        bool swapShown = swapAnim != null && (swapAnim.CurrentTime > 0f || swapAnim.TargetVisibility > 0f);
        bool swapInside = swapRt != null && swapRt.IsChildOf(rt);

        Content.Clear();
        var items = panel.Items;
        if (items != null)
            for (int i = 0; i < items.Count; i++)
                AddContent(Content, items[i] != null ? items[i].transform : null, rt);
        AddContent(Content, panel.SkipButton != null ? panel.SkipButton.transform : null, rt);
        AddContent(Content, panel.LockButton != null ? panel.LockButton.transform : null, rt);
        AddContent(Content, panel.BanishButton != null ? panel.BanishButton.transform : null, rt);
        AddContent(Content, panel.IgnoreActiveSkillsToggle != null ? panel.IgnoreActiveSkillsToggle.transform : null, rt);
        AddContent(Content, panel.RerollCount != null ? panel.RerollCount.transform : null, rt);

        ReplaceContent.Clear();
        if (swapShown)
        {
            var swapItems = panel.ReplaceableSkillItems;
            if (swapItems != null)
                for (int i = 0; i < swapItems.Count; i++)
                    AddContent(ReplaceContent, swapItems[i] != null ? swapItems[i].transform : null, swapRt);
            AddContent(ReplaceContent, panel.ReplacingSkillItem != null ? panel.ReplacingSkillItem.transform : null, swapRt);
            if (swapInside) Content.AddRange(ReplaceContent);
        }

        PopupTarget.Apply(rt, Content, canvasRt, scale, alpha, _minimized, fade >= 0.99f, null, first);
        if (swapShown && !swapInside && swapRt != null)
            ReplaceTarget.Apply(swapRt, ReplaceContent, canvasRt, scale, alpha, _minimized,
                swapAnim!.CurrentTime >= 0.99f, PopupTarget.Placed, !ReplaceTarget.Active);
        else if (ReplaceTarget.Active) ReplaceTarget.Restore();

        _hovered = !_minimized && PointerOver(canvas!, canvasRt);
    }

    private static void RestoreVisuals()
    {
        _visual = false;
        _hovered = false;
        PopupTarget.Restore();
        ReplaceTarget.Restore();
    }

    private static void AddContent(List<RectTransform> list, Transform? t, RectTransform? under)
    {
        if (t == null || under == null || !t.gameObject.activeInHierarchy || !t.IsChildOf(under)) return;
        var r = t.TryCast<RectTransform>();
        if (r != null) list.Add(r);
    }

    private static bool PointerOver(Canvas canvas, RectTransform canvasRt)
    {
        var mouse = Mouse.current;
        if (mouse == null) return false;
        var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, mouse.position.ReadValue(), cam, out var p))
            return false;
        return PopupTarget.Contains(p) || ReplaceTarget.Contains(p);
    }

    /// <summary>Bounds of the rects in the canvas' local space.</summary>
    private static bool Bounds(List<RectTransform> rts, RectTransform canvasRt, out Rect bounds)
    {
        bounds = default;
        bool any = false;
        float minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var r in rts)
        {
            if (r == null) continue;
            r.GetWorldCorners(Corners);
            for (int i = 0; i < 4; i++)
            {
                var c = canvasRt.InverseTransformPoint(Corners[i]);
                if (!any) { minX = maxX = c.x; minY = maxY = c.y; any = true; continue; }
                minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
                minY = Mathf.Min(minY, c.y); maxY = Mathf.Max(maxY, c.y);
            }
        }
        if (any) bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return any && bounds.width > 1f && bounds.height > 1f;
    }

    /// <summary>One animated group of the popup we restyle. The game's UIAnimations only write when their time changes,
    /// so the "base" scale / position is whatever the game last wrote (anything that differs from what we set).</summary>
    private sealed class Target
    {
        private readonly string _name;
        private RectTransform? _rt;
        private Vector3 _baseScale, _basePos, _lastScale, _lastPos;
        private bool _haveBase;
        private CanvasGroup? _cg;
        private bool _cgAdded, _cgBlocks, _cgInteractable;
        private float _cgAlpha;
        private readonly List<Image> _backdrops = new();
        private Rect _placed;
        private bool _hasPlaced, _scannedOpen;

        public Target(string name) { _name = name; }

        public bool Active => _rt != null;
        /// <summary>Where the content ended up last frame (canvas space), if placed.</summary>
        public Rect? Placed => _hasPlaced && _rt != null ? _placed : null;

        public bool Contains(Vector2 p) => _hasPlaced && _rt != null
            && p.x >= _placed.xMin - 4f && p.x <= _placed.xMax + 4f && p.y >= _placed.yMin - 4f && p.y <= _placed.yMax + 4f;

        public void Apply(RectTransform rt, List<RectTransform> content, RectTransform canvasRt, float scale, float alpha,
            bool hidden, bool fullyOpen, Rect? stackOn, bool log)
        {
            if (_rt != null && _rt.Pointer != rt.Pointer) Restore();
            bool begin = _rt == null;
            if (begin) Begin(rt);

            var s = rt.localScale;
            var p = rt.localPosition;
            if (!_haveBase || (s - _lastScale).sqrMagnitude > 1e-8f) _baseScale = s;
            if (!_haveBase || (p - _lastPos).sqrMagnitude > 1e-4f) _basePos = p;
            _haveBase = true;

            rt.localPosition = _basePos;
            rt.localScale = _baseScale;
            // Look for the backdrop at the game's own scale: at the start, and again once fully open (an opening
            // scale animation could make it look smaller than the screen at first).
            if (begin || (fullyOpen && !_scannedOpen))
            {
                ScanBackdrops(rt, canvasRt);
                if (fullyOpen) _scannedOpen = true;
            }
            rt.localScale = _baseScale * scale;
            var area = canvasRt.rect;
            float margin = area.height * MarginFraction;
            if (content.Count == 0) content.Add(rt);
            if (Bounds(content, canvasRt, out var b))
            {
                float maxW = area.width - 2f * margin;
                if (b.width > maxW)
                {
                    rt.localScale = _baseScale * (scale * maxW / b.width);
                    Bounds(content, canvasRt, out b);
                }
                var pos = Position.Value;
                float targetX = pos == LevelUpOverlayPosition.BottomLeft ? area.xMin + margin + b.width / 2f
                              : pos == LevelUpOverlayPosition.BottomRight ? area.xMax - margin - b.width / 2f
                              : area.center.x;
                bool top = pos == LevelUpOverlayPosition.Top;
                float targetY = top ? area.yMax - margin - b.height / 2f : area.yMin + margin + b.height / 2f;
                if (stackOn.HasValue) // the skill swap goes next to the cards, toward the middle of the screen
                    targetY = top ? stackOn.Value.yMin - margin - b.height / 2f : stackOn.Value.yMax + margin + b.height / 2f;
                var delta = new Vector3(targetX - b.center.x, targetY - b.center.y, 0f);
                var world = canvasRt.TransformVector(delta);
                var parent = rt.parent;
                var local = parent != null ? parent.InverseTransformVector(world) : world;
                rt.localPosition = _basePos + local;
                _placed = new Rect(b.x + delta.x, b.y + delta.y, b.width, b.height);
                _hasPlaced = true;
                if (log)
                    Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Overlay on the {_name} '{rt.name}': scale {_baseScale.x:F2}->" +
                        $"{rt.localScale.x:F2}, content {content.Count} rects {b.width:F0}x{b.height:F0} placed at ({_placed.x:F0},{_placed.y:F0}) " +
                        $"in canvas {area.width:F0}x{area.height:F0}, {Position.Value}, backdrops off={_backdrops.Count}, canvasGroup " +
                        $"{(_cgAdded ? "added" : "existing")}.");
            }
            _lastScale = rt.localScale;
            _lastPos = rt.localPosition;

            if (_cg != null)
            {
                _cg.alpha = hidden ? 0f : alpha;
                _cg.blocksRaycasts = !hidden;
                _cg.interactable = !hidden;
            }
            foreach (var img in _backdrops)
                if (img != null && img.enabled) img.enabled = false;
        }

        private void Begin(RectTransform rt)
        {
            _rt = rt;
            _haveBase = false;
            _hasPlaced = false;
            _scannedOpen = false;
            _backdrops.Clear();
            _cg = rt.GetComponent<CanvasGroup>();
            _cgAdded = _cg == null;
            if (_cg == null) _cg = rt.gameObject.AddComponent<CanvasGroup>();
            _cgAlpha = _cg.alpha;
            _cgBlocks = _cg.blocksRaycasts;
            _cgInteractable = _cg.interactable;
        }

        /// <summary>Full-screen images (the dark backdrop behind the cards) would still cover the screen: switch them off.</summary>
        private void ScanBackdrops(RectTransform rt, RectTransform canvasRt)
        {
            var area = canvasRt.rect;
            var one = new List<RectTransform>(1);
            int before = _backdrops.Count;
            foreach (var img in rt.GetComponentsInChildren<Image>(true))
            {
                if (img == null || !img.enabled) continue;
                one.Clear();
                one.Add(img.rectTransform);
                if (Bounds(one, canvasRt, out var b) && b.width >= area.width * 0.75f && b.height >= area.height * 0.75f)
                {
                    bool known = false;
                    foreach (var k in _backdrops) if (k.Pointer == img.Pointer) { known = true; break; }
                    if (!known) _backdrops.Add(img);
                }
            }
            if (_backdrops.Count > before)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Overlay {_name}: {_backdrops.Count - before} full-screen image(s) switched off.");
        }

        public void Restore()
        {
            if (_rt == null) return;
            try
            {
                if (_rt != null && _haveBase)
                {
                    _rt.localScale = _baseScale;
                    _rt.localPosition = _basePos;
                }
                if (_cg != null)
                {
                    _cg.alpha = _cgAdded ? 1f : _cgAlpha;
                    _cg.blocksRaycasts = _cgAdded || _cgBlocks;
                    _cg.interactable = _cgAdded || _cgInteractable;
                }
                foreach (var img in _backdrops)
                    if (img != null) img.enabled = true;
            }
            catch (System.Exception ex)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][levelup] Restoring the {_name}: {ex.Message}");
            }
            _rt = null;
            _cg = null;
            _backdrops.Clear();
            _haveBase = false;
            _hasPlaced = false;
        }
    }

    // =====================================================================================
    // Input
    // =====================================================================================

    /// <summary>Works out which actions clash, once per opening of the popup.</summary>
    private static bool BeginInput()
    {
        OffUi.Clear(); OffGameplay.Clear(); HoverActions.Clear(); OffHover.Clear(); OffMinimized.Clear();
        _im = MainUIManager.Instance != null ? MainUIManager.Instance.InputManager : null;
        var im = _im;
        if (im == null || im.GameplayMap == null || im.UIMap == null) return false;

        // Gameplay controls by path (mouse buttons apart: they're handled by hovering).
        var gameplayByPath = new Dictionary<string, List<InputAction>>();
        var gpActions = im.GameplayMap.actions;
        for (int i = 0; i < gpActions.Count; i++)
        {
            var g = gpActions[i];
            if (g == null) continue;
            var ctrls = g.controls;
            for (int c = 0; c < ctrls.Count; c++)
            {
                var path = ctrls[c] != null ? ctrls[c].path : null;
                if (string.IsNullOrEmpty(path)) continue;
                if (path!.StartsWith("/Mouse"))
                {
                    if (IsMouseButton(path) && !HoverActions.Contains(g)) HoverActions.Add(g);
                    continue;
                }
                if (!gameplayByPath.TryGetValue(path, out var list)) gameplayByPath[path] = list = new List<InputAction>();
                if (!list.Contains(g)) list.Add(g);
            }
        }

        // Keys this overlay itself uses (1-4, numpad 1-4, Tab, the gamepad's D-pad) win over gameplay and over the
        // game's other UI actions (the game's own option keys excepted, see Pick).
        var reservedPaths = new HashSet<string>(ReservedPaths);
        var pad = Gamepad.current;
        if (pad != null)
            foreach (var b in new[] { pad.dpad.up, pad.dpad.down, pad.dpad.left, pad.dpad.right })
                if (b != null && !string.IsNullOrEmpty(b.path)) reservedPaths.Add(b.path);
        foreach (var reserved in reservedPaths)
            if (gameplayByPath.TryGetValue(reserved, out var list))
                foreach (var g in list) AddOnce(OffGameplay, g);

        var priority = new List<System.IntPtr>();
        foreach (var a in new[] { im.SelectPowerup1, im.SelectPowerup2, im.SelectPowerup3, im.SelectPowerup4,
                                  im.SelectPowerup5, im.SelectPowerup6, im.SelectPowerup7, im.PauseMenuInputAction, im.CancelAction })
            if (a != null) priority.Add(a.Pointer);

        var uiActions = im.UIMap.actions;
        for (int i = 0; i < uiActions.Count; i++)
        {
            var u = uiActions[i];
            if (u == null) continue;
            bool isPriority = priority.Contains(u.Pointer);
            bool clash = false;
            var ctrls = u.controls;
            for (int c = 0; c < ctrls.Count; c++)
            {
                var path = ctrls[c] != null ? ctrls[c].path : null;
                if (string.IsNullOrEmpty(path) || path!.StartsWith("/Mouse")) continue; // pointer / click: kept
                if (!isPriority && reservedPaths.Contains(path)) clash = true;
                if (!gameplayByPath.TryGetValue(path, out var list)) continue;
                if (isPriority) foreach (var g in list) AddOnce(OffGameplay, g);
                else clash = true;
            }
            if (clash) AddOnce(OffUi, u);
        }

        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Overlay input: moving allowed. UI off (clash with gameplay): " +
            $"{Names(OffUi)}. Gameplay off (option / pause keys): {Names(OffGameplay)}. Off while the mouse is over " +
            $"the panel: {Names(HoverActions)}.");
        return true;
    }

    private static readonly string[] ReservedPaths =
    {
        "/Keyboard/1", "/Keyboard/2", "/Keyboard/3", "/Keyboard/4",
        "/Keyboard/numpad1", "/Keyboard/numpad2", "/Keyboard/numpad3", "/Keyboard/numpad4", "/Keyboard/tab",
    };

    private static void ApplyInput()
    {
        var im = _im;
        if (im == null) return;
        try
        {
            var gm = im.GameplayMap;
            if (gm != null && !gm.enabled) gm.Enable(); // the game's SwitchToUIMap turned it off
            foreach (var a in OffGameplay) if (a != null && a.enabled) a.Disable();
            foreach (var a in OffUi) if (a != null && a.enabled) a.Disable();

            if (_hovered)
            {
                foreach (var a in HoverActions)
                    if (a != null && a.enabled) { a.Disable(); AddOnce(OffHover, a); }
            }
            else if (OffHover.Count > 0)
            {
                foreach (var a in OffHover) if (a != null && !a.enabled && !OffGameplay.Contains(a)) a.Enable();
                OffHover.Clear();
            }

            if (_minimized)
            {
                foreach (var a in new[] { im.SelectPowerup1, im.SelectPowerup2, im.SelectPowerup3, im.SelectPowerup4,
                                          im.SelectPowerup5, im.SelectPowerup6, im.SelectPowerup7, im.RerollKey, im.BanishKey,
                                          im.LockSkillKey, im.SkipKey, im.FilterKey, im.SubmitAction, im.NavigationAction })
                    if (a != null && a.enabled) { a.Disable(); AddOnce(OffMinimized, a); }
            }
            else if (OffMinimized.Count > 0)
            {
                foreach (var a in OffMinimized) if (a != null && !a.enabled && !OffUi.Contains(a)) a.Enable();
                OffMinimized.Clear();
            }
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][levelup] Overlay input: {ex.Message}");
        }
    }

    /// <summary>Gives the actions back. Popup still open (Esc menu, mode change): back to the game's own state (UI map
    /// only). Popup closed: the game only switches back when the gameplay map is off, which we kept on, so do it here.</summary>
    private static void EndInput(bool popupStillOpen, bool mayReturnToGameplay)
    {
        _input = false;
        var im = _im;
        _im = null;
        try
        {
            if (im == null) return;
            var gm = im.GameplayMap;
            var ui = im.UIMap;
            bool gmOn = gm != null && gm.enabled, uiOn = ui != null && ui.enabled;
            foreach (var a in OffUi) if (a != null && uiOn && !a.enabled) a.Enable();
            foreach (var a in OffMinimized) if (a != null && uiOn && !a.enabled) a.Enable();
            foreach (var a in OffGameplay) if (a != null && gmOn && !a.enabled) a.Enable();
            foreach (var a in OffHover) if (a != null && gmOn && !a.enabled) a.Enable();
            if (popupStillOpen)
            {
                if (uiOn && gmOn) gm!.Disable();
            }
            else if (mayReturnToGameplay && uiOn)
            {
                im.SwitchToGameplayMap();
            }
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][levelup] Overlay input restore: {ex.Message}");
        }
        finally
        {
            OffUi.Clear(); OffGameplay.Clear(); HoverActions.Clear(); OffHover.Clear(); OffMinimized.Clear();
        }
    }

    /// <summary>1-4: same flag the game's own option keys set (LevelUpPanelItem.Selected / the skill swap's Selected),
    /// so the game's checks (minimum time, lock / banish mode, skill swap) all still apply.</summary>
    internal static void Pick(LevelUpPanelManager panel, int n)
    {
        try
        {
            var im = MainUIManager.Instance != null ? MainUIManager.Instance.InputManager : null;
            var native = im == null ? null : n switch
            {
                0 => im.SelectPowerup1, 1 => im.SelectPowerup2, 2 => im.SelectPowerup3, 3 => im.SelectPowerup4, _ => null,
            };
            if (native != null && native.enabled && native.WasPressedThisFrame()) return; // the game picks it itself

            if (panel.SkillToEquip != null)
            {
                var swap = panel.ReplaceableSkillItems;
                if (swap == null || n >= swap.Count || swap[n] == null || !swap[n].gameObject.activeInHierarchy) return;
                swap[n].Selected = true;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Key {n + 1}: skill swap slot {n + 1}.");
                return;
            }
            var items = panel.Items;
            var options = panel.PowerUpList;
            if (items == null || options == null || n >= items.Count || n >= options.Count) return;
            var item = items[n];
            if (item == null || !item.gameObject.activeInHierarchy) return;
            item.Selected = true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Key {n + 1}: option {n + 1}.");
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][levelup] Key {n + 1}: {ex.Message}");
        }
    }

    /// <summary>Highlighted card (or skill swap slot) for the gamepad: the game's own index, the one its stick
    /// navigation moves.</summary>
    private static int CursorIndex(LevelUpPanelManager panel)
    {
        try { return panel.SkillToEquip != null ? panel.SelectedLevelUpSkillReplaceableIndex : panel.SelectedLevelUpPanelItemIndex; }
        catch { return 0; }
    }

    /// <summary>D-pad left / right: moves the game's highlight (index + EventSystem selection, as its stick navigation does).</summary>
    private static void MoveCursor(LevelUpPanelManager panel, int dir)
    {
        try
        {
            bool swapping = panel.SkillToEquip != null;
            int count = 0;
            if (swapping)
            {
                var swap = panel.ReplaceableSkillItems;
                if (swap != null) for (int i = 0; i < swap.Count; i++) if (swap[i] != null && swap[i].gameObject.activeInHierarchy) count = i + 1;
            }
            else
            {
                var items = panel.Items;
                int options = panel.PowerUpList != null ? panel.PowerUpList.Count : 0;
                if (items != null)
                    for (int i = 0; i < items.Count && i < options; i++)
                        if (items[i] != null && items[i].gameObject.activeInHierarchy) count = i + 1;
            }
            if (count == 0) return;
            int idx = Mathf.Clamp(CursorIndex(panel) + dir, 0, count - 1);
            GameObject? go;
            if (swapping)
            {
                panel.SetSelectedReplaceableItemIndex(idx);
                var b = panel.ReplaceableSkillItems[idx].Button;
                go = b != null ? b.gameObject : null;
            }
            else
            {
                panel.SetSelectedLevelUpPanelItemIndex(idx);
                var b = panel.Items[idx].Button;
                go = b != null ? b.gameObject : null;
            }
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && go != null) es.SetSelectedGameObject(go);
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][levelup] D-pad: {ex.Message}");
        }
    }

    private static bool IsMouseButton(string path) =>
        path.EndsWith("Button") || path.EndsWith("/press") || path.Contains("/leftButton") || path.Contains("/rightButton")
        || path.Contains("/middleButton") || path.Contains("/forwardButton") || path.Contains("/backButton");

    private static void AddOnce(List<InputAction> list, InputAction a)
    {
        foreach (var x in list) if (x.Pointer == a.Pointer) return;
        list.Add(a);
    }

    private static string Names(List<InputAction> list)
    {
        if (list.Count == 0) return "none";
        var names = new List<string>(list.Count);
        foreach (var a in list)
        {
            string n;
            try { n = a.name; } catch { n = "?"; }
            names.Add(n);
        }
        return string.Join(", ", names);
    }

    /// <summary>One-time dump of the popup's hierarchy, to tune the overlay from a tester's log.</summary>
    private static void LogTree(Transform root)
    {
        var sb = new System.Text.StringBuilder("[SoulstoneCoop][levelup] Popup hierarchy:");
        int lines = 0;
        void Walk(Transform t, int depth)
        {
            if (lines >= 60 || depth > 4) return;
            lines++;
            var r = t.TryCast<RectTransform>();
            string size = r != null ? $"{r.rect.width:F0}x{r.rect.height:F0} a({r.anchorMin.x:F1},{r.anchorMin.y:F1})-({r.anchorMax.x:F1},{r.anchorMax.y:F1})" : "";
            var comps = new List<string>();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetIl2CppType().Name;
                if (n != "RectTransform" && n != "CanvasRenderer") comps.Add(n);
            }
            sb.Append($"\n  {new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")} {size} [{string.Join(",", comps)}]");
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
        }
        try { Walk(root, 0); Plugin.StaticLog.LogInfo(sb.ToString()); }
        catch (System.Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][levelup] Hierarchy dump: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(LevelUpPanelManager), nameof(LevelUpPanelManager.OnUpdate))]
public static class LevelUpPanelOverlayPatch
{
    [HarmonyPrefix]
    public static void Prefix(LevelUpPanelManager __instance, bool isPaused, bool endGamePanelActive)
    {
        try { LevelUpOverlay.BeforeUpdate(__instance, isPaused, endGamePanelActive); }
        catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] LevelUpPanelOverlayPatch.Prefix: {ex.Message}"); }
    }

    [HarmonyPostfix]
    public static void Postfix(LevelUpPanelManager __instance, bool isPaused, bool endGamePanelActive)
    {
        try { LevelUpOverlay.AfterUpdate(__instance, isPaused, endGamePanelActive); }
        catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] LevelUpPanelOverlayPatch.Postfix: {ex.Message}"); }
    }
}

/// <summary>GameplayUIManager.HasOverlayPanel is true while the level-up popup is visible, and the HUD panels (health,
/// rage, mana...) hide on it. With the overlay the game keeps running, so answer as if the popup were closed: same
/// checks as the original (0x605150) minus the popup's.</summary>
[HarmonyPatch(typeof(GameplayUIManager), nameof(GameplayUIManager.HasOverlayPanel))]
public static class GameplayOverlayPanelPatch
{
    [HarmonyPostfix]
    public static void Postfix(GameplayUIManager __instance, ref bool __result)
    {
        if (!__result || !LevelUpOverlay.Visible) return;
        try
        {
            var end = __instance.EndGamePanel;
            bool r = end != null && end.AnimationGroup != null && end.AnimationGroup.TargetVisibility > 0f;
            var pause = __instance.PausePanel;
            r |= pause != null && pause.IsPaused;
            var crusade = __instance.CrusadeMapSelection;
            r |= crusade != null && (crusade.IsOpen || (end != null && end.EndingScreenTriggered));
            var tutorial = __instance.TutorialHintPanel;
            r |= tutorial != null && tutorial.IsOpen;
            __result = r;
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] GameplayOverlayPanelPatch: {ex.Message}");
        }
    }
}
