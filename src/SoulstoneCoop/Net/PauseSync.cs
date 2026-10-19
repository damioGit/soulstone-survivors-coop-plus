using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SoulstoneCoop.Net;

public enum PauseReason : byte
{
    None = 0,
    Pause = 1,
    LevelUp = 2,
    /// <summary>Level-up popup open WITHOUT stopping the time ([Coop] LevelUpPause): shown, never freezes the partner.</summary>
    Choosing = 3,
}

/// <summary>
/// Synchronized pause: if either side pauses or opens the level-up screen, both freeze.
/// TimeUtil.SetTime runs every frame and sets Time.timeScale to the product of the game's pause
/// factors; our postfix reads the LOCAL state from those factors (before the remote state is applied,
/// so the two sides can't latch each other paused) and then forces timeScale to 0 while the remote is paused.
/// In co-op the level-up popup may skip the pause (LevelUpPolicy, [Coop] LevelUpPause): it is then sent as Choosing.
/// </summary>
public static class PauseSync
{
    public static PauseReason LocalReason { get; private set; }
    public static PauseReason RemoteReason { get; private set; }
    /// <summary>The partner paused (or is choosing an upgrade) inside a run next to ours. Only counts while both are in
    /// a run: a state latched from the end screen must not freeze the next run or the menu (phase 6).</summary>
    public static bool RemotePaused =>
        RemoteReason != PauseReason.None && RemoteReason != PauseReason.Choosing && NetworkManager.HasPeer
        && Lobby.CoopSession.LocalInRun && Lobby.CoopSession.RemoteInRun;
    /// <summary>The partner is choosing an upgrade with the game running: no freeze, just the banner.</summary>
    public static bool RemoteChoosing =>
        RemoteReason == PauseReason.Choosing && NetworkManager.HasPeer
        && Lobby.CoopSession.LocalInRun && Lobby.CoopSession.RemoteInRun;

    /// <summary>A new run is starting: forget the partner's old state and resend ours.</summary>
    internal static void ResetForRun()
    {
        RemoteReason = PauseReason.None;
        _sentOnce = false;
        LevelUpPolicy.ResetForRun();
    }

    private static bool _sentOnce;
    private static PauseReason _lastSent;
    private static bool _hooked;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerConnected += () => _sentOnce = false; // resend our state to the new peer
        NetworkManager.PeerDisconnected += () =>
        {
            if (RemoteReason != PauseReason.None)
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][pause] Peer disconnected - clearing remote pause.");
            RemoteReason = PauseReason.None;
        };
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
        LocalReason = levelUpPopup && !noPause ? PauseReason.LevelUp
                    : TimeUtil.PauseTime < 0.001f ? PauseReason.Pause
                    : levelUpPopup && !overlay ? PauseReason.Choosing // the overlay plays on: no banner for the partner
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

        // Phase 6: also hold our match while the partner's is still loading (RunSync, bounded).
        if (RemotePaused || Lobby.RunSync.HoldForPartner)
            Time.timeScale = 0f;
    }

    public static void OnPauseStateReceived(NetDataReader reader)
    {
        var reason = (PauseReason)reader.GetByte();
        if (reason != RemoteReason)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][pause] Remote state {RemoteReason} -> {reason}.");
        RemoteReason = reason;
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
