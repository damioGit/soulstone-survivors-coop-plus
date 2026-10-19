using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Net;

public enum LevelUpPauseMode
{
    Always,
    NotWhenFullBuild,
    Never,
    Overlay,
}

/// <summary>
/// Whether the local level-up popup stops the time in co-op ([Coop] LevelUpPause, docs/levelup-notas.md), and
/// whether it then shows as the compact overlay (Ui/LevelUpOverlay: smaller, see-through, the player keeps moving).
/// The popup (LevelUpPanelManager.OnUpdate) writes TimeUtil.LevelUpTime = 0 while it's visible, and nothing but
/// TimeUtil.SetTime reads it; so "no pause" is PauseSync recomputing timeScale without that factor. Esc and the
/// death guard factors are untouched. Solo (no partner in the run) always keeps the game's own pause.
///   Always = pause. NotWhenFullBuild = pause until every skill slot is taken, then the overlay.
///   Overlay = never pause, always the overlay. Never = never pause, the game's full-screen popup (0.8.0 behaviour).
/// </summary>
public static class LevelUpPolicy
{
    internal static ConfigEntry<LevelUpPauseMode> Mode = null!;

    private const float FullBuildRecheckSeconds = 0.5f;

    private static bool _open, _skip, _overlay, _fullBuild, _coop;
    private static float _openedAt, _nextFullCheck = float.NegativeInfinity;
    private static int _queueAtOpen;
    // Session telemetry (reset with the run).
    private static int _popups, _popupsNoPause;
    private static float _secondsPaused, _secondsNoPause;

    private static bool _prevF6;

    internal static void Bind(ConfigFile cfg)
    {
        Mode = cfg.Bind("Coop", "LevelUpPause", LevelUpPauseMode.NotWhenFullBuild,
            "Co-op: does your level-up screen pause both games? Always = always pauses (as before). " +
            "NotWhenFullBuild = pauses until all your skill slots are full; after that it opens as a small see-through " +
            "panel and you keep playing (1-4 pick, Tab hides it). Overlay = never pauses, always the small panel. " +
            "Never = never pauses, with the game's full-screen panel (you stand still while choosing). " +
            "Solo games always pause. Each player's setting applies to their own level-ups.");
        Ui.LevelUpOverlay.Bind(cfg);
    }

    /// <summary>A new run: forget the session counters.</summary>
    internal static void ResetForRun()
    {
        _open = false;
        _popups = _popupsNoPause = 0;
        _secondsPaused = _secondsNoPause = 0f;
        Ui.LevelUpOverlay.ResetForRun();
    }

    /// <summary>This frame's decision for an open popup: skip the pause? and if so, as the compact overlay?</summary>
    internal static bool Decide(out bool overlay, out bool coop)
    {
        coop = NetworkManager.HasPeer && Lobby.CoopSession.LocalInRun && Lobby.CoopSession.RemoteInRun;
        var mode = Mode.Value;
        float now = Time.realtimeSinceStartup;
        if (mode == LevelUpPauseMode.NotWhenFullBuild && now >= _nextFullCheck)
        {
            _fullBuild = IsFullBuild();
            _nextFullCheck = now + FullBuildRecheckSeconds;
        }
        bool skip = coop && (mode == LevelUpPauseMode.Never || mode == LevelUpPauseMode.Overlay
            || (mode == LevelUpPauseMode.NotWhenFullBuild && _fullBuild));
        overlay = skip && mode != LevelUpPauseMode.Never;
        return skip;
    }

    /// <summary>Called every frame from the TimeUtil.SetTime postfix. True when this frame's level-up popup must
    /// NOT stop the time; <paramref name="overlay"/> = it shows as the compact overlay.</summary>
    internal static bool SkipPause(bool popupOpen, out bool overlay)
    {
        overlay = false;
        float now = Time.realtimeSinceStartup;
        if (!popupOpen)
        {
            if (_open) OnClosed(now);
            return false;
        }

        if (!_open) _nextFullCheck = float.NegativeInfinity; // fresh popup: re-read the build now
        bool skip = Decide(out overlay, out bool coop);
        var mode = Mode.Value;

        if (!_open)
        {
            _open = true;
            _openedAt = now;
            _queueAtOpen = QueueCount();
            _skip = skip;
            _overlay = overlay;
            _coop = coop;
            if (NetworkManager.Role != NetRole.None)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Popup opened: mode={mode} fullBuild={_fullBuild} " +
                    $"coop={coop} -> {Describe(skip, overlay)} (queued={_queueAtOpen}, level={LocalLevel()}).");
        }
        else if (skip != _skip || overlay != _overlay || coop != _coop)
        {
            // E.g. the last slot got filled while more choices were queued, or the partner left / joined the run.
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Policy changed with the popup open: fullBuild={_fullBuild} " +
                $"coop={coop} -> {Describe(skip, overlay)}.");
            _skip = skip;
            _overlay = overlay;
            _coop = coop;
        }
        return skip;
    }

    private static string Describe(bool skip, bool overlay) => !skip ? "pause" : overlay ? "NO PAUSE (overlay)" : "NO PAUSE (full panel)";

    private static void OnClosed(float now)
    {
        _open = false;
        float secs = now - _openedAt;
        _popups++;
        if (_skip) { _popupsNoPause++; _secondsNoPause += secs; }
        else _secondsPaused += secs;
        if (NetworkManager.Role == NetRole.None) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Popup closed after {secs:F1} s ({(_skip ? (_overlay ? "overlay" : "no pause") : "paused")}, " +
            $"queued at open={_queueAtOpen}, level={LocalLevel()}). Run so far: {_popups} popups, " +
            $"{_popups - _popupsNoPause} paused ({_secondsPaused:F0} s), {_popupsNoPause} without pause ({_secondsNoPause:F0} s).");
    }

    /// <summary>"Full build" = every active skill slot taken: LevelUpPanelManager.IsFullOfSkills, the game's own test
    /// (slots from GameProperties plus the inventory's extra slots). Any failure counts as not full (keep the pause).</summary>
    private static bool IsFullBuild()
    {
        try
        {
            if (!GameManagerUtil.HasGameManager) return false;
            var ui = GameManagerUtil.GameplayUIManager;
            var panel = ui != null ? ui.LevelUpPanel : null;
            if (panel == null || panel.PlayerEntity == null) return false;
            return panel.IsFullOfSkills();
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][levelup] IsFullOfSkills failed (keeping the pause): {ex.Message}");
            return false;
        }
    }

    private static int QueueCount()
    {
        try
        {
            var ui = GameManagerUtil.GameplayUIManager;
            var q = ui != null && ui.LevelUpPanel != null ? ui.LevelUpPanel.PowerUpRequests : null;
            return q != null ? q.Count : -1;
        }
        catch { return -1; }
    }

    private static int LocalLevel()
    {
        try
        {
            var xp = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity._experience : null;
            return xp != null ? xp.Level : -1;
        }
        catch { return -1; }
    }

    /// <summary>Debug hotkey (co-op only, in a match): F6 = +1 level for the local player, Shift+F6 = +5. Goes
    /// straight through ExperienceComponent.LevelUpMultipleTimes, so it is NOT shared as team XP.</summary>
    internal static void DebugHotkey()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f6 = kb.f6Key.isPressed;
        bool pressed = f6 && !_prevF6;
        _prevF6 = f6;
        if (!pressed || NetworkManager.Role == NetRole.None) return;
        try
        {
            var local = GameState.LocalPlayerEntity;
            var xp = local != null ? local._experience : null;
            if (xp == null || !GameManagerUtil.HasGameManager) return;
            int n = kb.shiftKey.isPressed ? 5 : 1;
            int before = xp.Level;
            xp.LevelUpMultipleTimes(n);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] F6 (debug): +{n} level(s), {before} -> {xp.Level}.");
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][levelup] F6: {ex.Message}");
        }
    }
}
