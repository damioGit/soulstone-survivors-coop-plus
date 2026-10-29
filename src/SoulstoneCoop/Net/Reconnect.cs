using SoulstoneCoop.Lobby;
using UnityEngine;

namespace SoulstoneCoop.Net;

/// <summary>
/// Guest: the link to the host dropped in the middle of a run (silence timeout, a failed Steam session, the host's
/// game crashed): instead of ending the run, connect again for up to <see cref="GiveUpAfter"/> seconds. The run and the
/// character stay as they are here (they are local); while away the host's enemies are gone and nothing spawns (the
/// spawners stay stopped, CoopRole.IsClientSide latches). Back on, the usual join brings the world back: the host sees
/// a new peer whose LobbyState is InRun and resends every live enemy (HostEnemySync.ResyncToPeer), the ghosts come from
/// the Hello. Steam: the guest stays in the lobby (SteamTransport.Stop keeps it while this is active), and the host takes
/// a Hello from a player of this run back even if the lobby dropped it.
/// Not tried when the host said Bye (it quit), refused us, or we are leaving the run ourselves. Giving up ends the run
/// as before (CoopLife.HostLostNow).
/// </summary>
public static class Reconnect
{
    private const float RetryInterval = 5f;
    private const float GiveUpAfter = 120f;
    private const float NoticeInterval = 1f;
    private const float FlapWindow = 30f; // a drop this soon after coming back continues the same countdown

    /// <summary>Waiting for the host to come back.</summary>
    public static bool Active { get; private set; }

    private static float _elapsed, _retryTimer, _noticeTimer;
    private static string? _address;
    private static ulong _lobby, _host;
    private static int _attempts;
    private static float _lastSuccessAt = -1000f, _elapsedAtSuccess;
    internal static int Successes, Failures;

    /// <summary>Client, from CoopLife.OnPeerDisconnected: the host is gone and our match is up. True = we try to get
    /// back, so the run must not end now.</summary>
    internal static bool TryBegin()
    {
        if (Active) return true;
        var t = NetworkManager.Transport;
        if (NetworkManager.Role != NetRole.Client || t == null || !t.DropRecoverable) return false;
        if (t.Kind == "Steam")
        {
            _lobby = Steam.SteamLobby.LobbyId;
            _host = Steam.SteamLobby.HostId;
            _address = null;
            if (_lobby == 0 || _host == 0) return false;
        }
        else
        {
            _address = NetworkManager.TargetAddress;
            if (string.IsNullOrEmpty(_address)) return false;
        }
        Active = true;
        // A link that keeps coming back and dropping again must not wait forever: same countdown.
        _elapsed = Time.unscaledTime - _lastSuccessAt < FlapWindow ? _elapsedAtSuccess : 0f;
        _retryTimer = 1f; // the old transport is stopped right after this frame's Poll
        _noticeTimer = 0f;
        _attempts = 0;
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][reconnect] Lost the host mid-run ({Plain(t.LastError)}): reconnecting for up to {GiveUpAfter:0}s " +
            $"({(_address != null ? "IP " + _address : $"Steam host {_host} in lobby {_lobby}")}).");
        return true;
    }

    public static void Tick(float dt)
    {
        if (!Active) return;
        try { TickActive(dt); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][reconnect] {ex}");
            GiveUp("error");
        }
    }

    private static void TickActive(float dt)
    {
        if (NetworkManager.Role == NetRole.Client && NetworkManager.HasPeer)
        {
            Active = false;
            Successes++;
            _lastSuccessAt = Time.unscaledTime;
            _elapsedAtSuccess = _elapsed;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][reconnect] Back with the host after {_elapsed:0.0}s ({_attempts} attempt(s)).");
            CoopSession.Notice(Ui.Loc.Msg("nt.reconnected"), 5f);
            return;
        }
        // Our own run ended meanwhile (surrender, death, menu): nothing to go back to.
        if (!Life.CoopLife.InMatchNow || Life.CoopLife.RunEnding || !CoopSession.LocalInRun)
        {
            GiveUp("the local run ended", endRun: false);
            return;
        }
        _elapsed += dt;
        if (_elapsed >= GiveUpAfter)
        {
            GiveUp($"no host after {GiveUpAfter:0}s");
            return;
        }
        _noticeTimer -= dt;
        if (_noticeTimer <= 0f)
        {
            _noticeTimer = NoticeInterval;
            CoopSession.Notice(Ui.Loc.Msg("nt.reconnecting", ("s", Mathf.CeilToInt(GiveUpAfter - _elapsed))), 1.5f);
        }
        // One attempt at a time: LiteNetLib keeps retrying an unreachable host by itself, a Steam client resends its
        // Hello every second until its ConnectTimeout. A new one only when the last one gave up (back to idle).
        _retryTimer -= dt;
        if (_retryTimer > 0f || NetworkManager.Role != NetRole.None) return;
        _retryTimer = RetryInterval;
        _attempts++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][reconnect] Attempt {_attempts} ({_elapsed:0}s).");
        if (_address != null) NetworkManager.StartClient(_address);
        else NetworkManager.StartSteamClient(_lobby, _host);
    }

    private static void GiveUp(string why, bool endRun = true)
    {
        Active = false;
        Failures++;
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][reconnect] Giving up: {why}.");
        NetworkManager.Stop();
        Steam.SteamLobby.Leave(); // kept while we tried
        if (!endRun) return;
        CoopSession.Notice(Ui.Loc.Msg("nt.reconnect_failed"), 6f);
        Life.CoopLife.HostLostNow();
    }

    /// <summary>The co-op panel's Leave (or any deliberate stop): no more attempts.</summary>
    internal static void Cancel(string why)
    {
        if (!Active) return;
        Active = false;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][reconnect] Cancelled: {why}.");
    }

    private static string Plain(string? s) => s == null ? "-" : Ui.Loc.Plain(s);

    internal static string TestInfo() =>
        $"active={Active} elapsed={_elapsed:0.0} attempts={_attempts} ok={Successes} failed={Failures} role={NetworkManager.Role} status={NetworkManager.Status}";
}
