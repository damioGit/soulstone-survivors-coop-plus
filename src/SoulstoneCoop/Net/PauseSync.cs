using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Net;

public enum PauseReason : byte
{
    None = 0,
    Pause = 1,
    /// <summary>No longer sent (0.9.2, issue #1: level-ups never pause in co-op). Kept on the wire; never freezes.</summary>
    LevelUp = 2,
    /// <summary>Level-up popup open on the full-screen panel without stopping the time ([Debug] LevelUpFullPanel):
    /// the others see a banner, never a freeze. The overlay sends None.</summary>
    Choosing = 3,
}

/// <summary>
/// Synchronized pause: if anyone pauses, everyone freezes.
/// TimeUtil.SetTime runs every frame and sets Time.timeScale to the product of the game's pause
/// factors; our postfix reads the LOCAL state from those factors (before the remote state is applied,
/// so two players can't latch each other paused) and then forces timeScale to 0 while someone else is paused.
/// Level-ups never freeze anyone in co-op (LevelUpPolicy, issue #1): the popup's own factor is left out of timeScale,
/// and the state sent is None (overlay) or Choosing (debug full panel). Only Esc pauses.
/// Phase 9 (user's rule): Esc by anyone freezes everyone, and the host can unfreeze: when it closes its own pause
/// menu, or presses F4, whoever was pausing stops counting (PauseOverride) until it pauses again.
/// The host forwards every client's PauseState to the others (PacketRouter).
/// </summary>
public static class PauseSync
{
    public static PauseReason LocalReason { get; private set; }

    private static readonly PauseReason[] Reasons = new PauseReason[Players.MaxSlots];

    /// <summary>Host: slots whose current pause the host lifted (bit per slot). Clients get it in PauseOverride.</summary>
    private static int _overrideMask;

    /// <summary>That player's last reported pause state.</summary>
    public static PauseReason ReasonOf(byte slot) => slot < Players.MaxSlots ? Reasons[slot] : PauseReason.None;

    /// <summary>That player's state freezes us: an Esc pause inside a run next to ours, not lifted by the host. A state
    /// latched from the end screen must not freeze the next run (phase 6).</summary>
    public static bool Freezes(byte slot)
    {
        if (slot >= Players.MaxSlots || (_overrideMask & (1 << slot)) != 0) return false;
        return Reasons[slot] == PauseReason.Pause
            && NetworkManager.HasPeer && Lobby.CoopSession.LocalInRun && Lobby.CoopSession.InRun(slot);
    }

    /// <summary>Someone else's pause freezes us right now.</summary>
    public static bool RemotePaused
    {
        get
        {
            foreach (var s in Players.Others) if (Freezes(s)) return true;
            return false;
        }
    }

    /// <summary>Someone else is choosing an upgrade on the full-screen panel (debug) with the game running: no freeze, just the banner.</summary>
    public static bool IsChoosing(byte slot)
    {
        if (slot >= Players.MaxSlots || !NetworkManager.HasPeer || !Lobby.CoopSession.LocalInRun || !Lobby.CoopSession.InRun(slot)) return false;
        var r = Reasons[slot];
        return r == PauseReason.Choosing || r == PauseReason.LevelUp;
    }

    /// <summary>That player is in its pause menu but the host lifted the freeze.</summary>
    public static bool IsPausedAlone(byte slot) =>
        slot < Players.MaxSlots && Reasons[slot] == PauseReason.Pause && (_overrideMask & (1 << slot)) != 0
        && Lobby.CoopSession.InRun(slot);

    /// <summary>A new run is starting: forget the others' old state (and the host's lifts) and resend ours.</summary>
    internal static void ResetForRun()
    {
        Array.Clear(Reasons);
        _overrideMask = 0;
        _sentOnce = false;
        LevelUpPolicy.ResetForRun();
    }

    private static bool _sentOnce;
    private static PauseReason _lastSent;
    private static bool _hooked;
    private static bool _prevF4;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerConnected += slot =>
        {
            _sentOnce = false; // resend our state to the new peer
            if (NetworkManager.Role != NetRole.Host) return;
            // A newcomer gets the others' current state too, and the host's lifts.
            foreach (var s in Players.Others)
            {
                if (s == slot || Reasons[s] == PauseReason.None) continue;
                var w = new NetDataWriter();
                w.Put((byte)PacketType.PauseState);
                w.Put((byte)Reasons[s]);
                NetworkManager.SendRelayedTo(slot, s, w, DeliveryMethod.ReliableOrdered);
            }
            if (_overrideMask != 0) SendOverride();
        };
        Players.Left += slot =>
        {
            if (slot >= Players.MaxSlots) return;
            if (Reasons[slot] != PauseReason.None)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][pause] Slot {slot} left - clearing its pause.");
            Reasons[slot] = PauseReason.None;
            SetOverride(slot, false);
        };
    }

    private static GameplayUIManager? _ui;
    private static float _infoPanelUntil;

    private static bool EscPauseOpen()
    {
        var pause = _ui != null ? _ui.PausePanel : null;
        return pause != null && pause.IsPaused;
    }
    private static int _infoPanelErrors;

    /// <summary>A tutorial hint or the visibility notice is up, and the pause menu is not (Esc stays a real pause).</summary>
    private static bool InfoPanelOpen()
    {
        try
        {
            if (_ui == null) _ui = UnityEngine.Object.FindObjectOfType<GameplayUIManager>();
            if (_ui == null) return false;
            var pause = _ui.PausePanel;
            if (pause != null && pause.IsPaused) return false;
            var hint = _ui.TutorialHintPanel;
            if (hint != null && hint.IsOpen) return true;
            var vis = _ui.VisibilityInformationHintPanel;
            return vis != null && vis.AnimationGroup != null && vis.AnimationGroup.TargetVisibility > 0f;
        }
        catch (Exception ex)
        {
            if (++_infoPanelErrors <= 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][pause] Info panel check: {ex.Message}");
            return false;
        }
    }

    /// <summary>Test bridge, "cmd hint on|off": set the tutorial hint panel's open flag (as its own OnUpdate would).</summary>
    internal static string TestHint(string[] args)
    {
        if (_ui == null) _ui = UnityEngine.Object.FindObjectOfType<GameplayUIManager>();
        var hint = _ui != null ? _ui.TutorialHintPanel : null;
        if (hint == null)
        {
            if (_ui == null) return "no gameplay UI";
            _ui.StartCoroutine(_ui.TutorialHintSetupRoutine()); // the game loads it only while tutorials are pending
            return "loading the tutorial hint panel, ask again";
        }
        hint.IsOpen = args.Length == 0 || args[0] != "off";
        return $"hint open={hint.IsOpen} pauseTime={TimeUtil.PauseTime} local={LocalReason}";
    }

    internal static void OnLocalTimeComputed()
    {
        bool levelUpPopup = TimeUtil.LevelUpTime < 0.001f;
        bool noPause = LevelUpPolicy.SkipPause(levelUpPopup, out bool overlay);
        if (noPause)
        {
            // TimeUtil.SetTime's product (0x729F60) minus LevelUpTime: Esc, death guard, slow motion etc. still apply.
            Time.timeScale = TimeUtil.PauseTime * TimeUtil.DeathGuardTime * TimeUtil.SlowMotionTime
                * TimeUtil.ControllerDisconnectedTime * TimeUtil.CreditsTime * TimeUtil.PlayerDataTime;
        }
        // #21: the tutorial hints and the visibility notice also write TimeUtil.PauseTime, so they counted as Esc and
        // froze every player. In co-op they are read like the level-up overlay: nobody freezes, the game goes on.
        // The panels keep the pause factor a few frames while they fade out: the 0.5 s after one closes count too.
        bool infoPanel = false;
        if (TimeUtil.PauseTime < 0.001f && NetworkManager.HasPeer && Lobby.CoopSession.LocalInRun)
        {
            if (InfoPanelOpen()) { infoPanel = true; _infoPanelUntil = Time.unscaledTime + 0.5f; }
            else infoPanel = Time.unscaledTime < _infoPanelUntil && !EscPauseOpen();
        }
        if (infoPanel)
            Time.timeScale = (noPause ? 1f : TimeUtil.LevelUpTime) * TimeUtil.DeathGuardTime * TimeUtil.SlowMotionTime
                * TimeUtil.ControllerDisconnectedTime * TimeUtil.CreditsTime * TimeUtil.PlayerDataTime;
        var previous = LocalReason;
        // A level-up is never a pause for the others (issue #1). Without co-op (nobody else in the run) the popup
        // still pauses our own game, and that's nobody else's business either.
        LocalReason = TimeUtil.PauseTime < 0.001f && !infoPanel ? PauseReason.Pause
                    : levelUpPopup && noPause && !overlay ? PauseReason.Choosing // debug full panel: banner only
                    : PauseReason.None;
        LevelUpPolicy.DebugHotkey();

        if (NetworkManager.HasPeer && (!_sentOnce || LocalReason != _lastSent))
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.PauseState);
            writer.Put((byte)LocalReason);
            NetworkManager.Send(writer, DeliveryMethod.ReliableOrdered);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][pause] Sent local state {LocalReason} (timeScale={Time.timeScale}).");
            _lastSent = LocalReason;
            _sentOnce = true;
        }

        if (NetworkManager.Role == NetRole.Host)
        {
            // The host closed its own pause menu: whoever else was pausing is lifted too (until it pauses again).
            if (previous == PauseReason.Pause && LocalReason != PauseReason.Pause) LiftOthers("host closed its pause menu", includeLevelUp: false);
            HostUnpauseHotkey();
        }

        // Phase 6: also hold our match while someone else is still loading (RunSync, bounded).
        if (RemotePaused || Lobby.RunSync.HoldForPartner)
            Time.timeScale = 0f;
    }

    /// <summary>F4 on the host (F7/F8 are the specials debug keys): lift every other player's pause right away (the menu stays open on their side).</summary>
    private static void HostUnpauseHotkey()
    {
        var kb = Keyboard.current;
        bool f4 = kb != null && kb.f4Key.isPressed;
        if (f4 && !_prevF4 && !Ui.CoopUi.IsTyping) LiftOthers("F4");
        _prevF4 = f4;
    }

    /// <param name="includeLevelUp">F4 lifts a pausing level-up too; closing the host's own menu only lifts Esc pauses.</param>
    private static void LiftOthers(string why, bool includeLevelUp = true)
    {
        int mask = _overrideMask;
        foreach (var s in Players.Others)
            if (Reasons[s] == PauseReason.Pause || (includeLevelUp && Reasons[s] == PauseReason.LevelUp)) mask |= 1 << s;
        if (mask == _overrideMask) return;
        _overrideMask = mask;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][pause] Host lifted the others' pause ({why}): mask={mask:X2}.");
        Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.you_unpaused"), 3f);
        SendOverride();
    }

    private static void SetOverride(byte slot, bool on)
    {
        int bit = 1 << slot;
        int mask = on ? _overrideMask | bit : _overrideMask & ~bit;
        if (mask == _overrideMask) return;
        _overrideMask = mask;
        if (NetworkManager.Role == NetRole.Host) SendOverride();
    }

    /// <summary>Host -> clients: which players' pause the host lifted (bit per slot).</summary>
    private static void SendOverride()
    {
        if (NetworkManager.Role != NetRole.Host || !NetworkManager.HasPeer) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.PauseOverride);
        w.Put((byte)_overrideMask);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnPauseOverride(NetDataReader reader)
    {
        int mask = reader.GetByte();
        if (NetworkManager.Role != NetRole.Client) return;
        if (mask != _overrideMask) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][pause] Host lifted pauses: mask={mask:X2}.");
        if ((mask & ~_overrideMask & (1 << Players.MySlot)) != 0)
            Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.host_unpaused"), 5f);
        _overrideMask = mask;
    }

    public static void OnPauseStateReceived(NetDataReader reader)
    {
        var reason = (PauseReason)reader.GetByte();
        byte slot = PacketRouter.From;
        if (slot >= Players.MaxSlots || !Players.IsPresent(slot)) return;
        if (reason != Reasons[slot])
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][pause] Slot {slot} state {Reasons[slot]} -> {reason}.");
        Reasons[slot] = reason;
        // A lifted pause lasts until that player leaves it: its next pause freezes everyone again.
        if (NetworkManager.Role == NetRole.Host && reason != PauseReason.Pause && reason != PauseReason.LevelUp) SetOverride(slot, false);
    }
}

/// <summary>
/// With GamePreferences.PauseOnFocus on, GameplayUIManager.OnApplicationFocus(false) opens the pause
/// menu (GameplayPanelManager.OnPauseButtonPressed) - indistinguishable from Esc in TimeUtil.PauseTime.
/// In co-op, focus loss is constant (two windows, alt-tab), and the sync would freeze the partner,
/// so skip the auto-pause entirely while co-op is active.
/// </summary>
[HarmonyPatch(typeof(GameplayUIManager), nameof(GameplayUIManager.OnApplicationFocus))]
public static class GameplayUIFocusPausePatch
{
    [HarmonyPrefix]
    public static bool Prefix(bool focus)
    {
        try
        {
            bool coop = NetworkManager.Role != NetRole.None || Plugin.HostArg || Plugin.ConnectAddressArg != null;
            if (!coop || focus) return true;
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][pause] Focus lost - skipped the game's pause-on-focus (co-op).");
            return false;
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] GameplayUIFocusPausePatch: {ex}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(TimeUtil), nameof(TimeUtil.SetTime))]
public static class TimeUtilSetTimePatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try
        {
            PauseSync.OnLocalTimeComputed();
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] TimeUtilSetTimePatch: {ex}");
        }
    }
}
