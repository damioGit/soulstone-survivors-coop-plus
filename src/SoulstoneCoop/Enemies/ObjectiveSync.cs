using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 4D fix (bug seen in 0.7.0: "0/15 bosses" on the client the whole run). The boss counter on the panel is
/// SurvivorsGameManager.MainObjective, an EliminateSpecificEntitiesObjective: its Current only grows in
/// ObjectivesPanelReferences.RunEliminateSpecificEntitiesObjective (0x618670), +1 per entity of its OWN list that died,
/// and IsComplete (0x612B60) is Total &lt;= Current. On the host SpawnBossRoutine puts every boss in that list; on the
/// client the boss puppets stay out of it on purpose (the run end belongs to the host, phase 4A RunEnd), so it never
/// moved. Nothing recomputes Current, so a value written from outside sticks.
///
/// The host sends what the objectives are built from, whenever it changes (and every 2 s as a refresh):
///   GameStats kills (enemy / elite / boss) | GameData.Tier | Progression.CompletedProgressObjectiveProgression |
///   MainObjective Current/Total | has an active kill objective (+ its Starting/Total, for the log).
/// The client:
///   - copies the kills into its GameStats: its own UpdateAll sets the kill objective's Current from them, so the kill
///     counter and its completion follow the host's;
///   - raises Tier and the completed-objective count to the host's (never lowers);
///   - raises MainObjective.Current, capped at Total - 1: the mirror can NEVER complete it (that would run the local
///     victory, see CoopLife.DebugVictory); the end stays with RunEnd;
///   - safety net for "the next kill objective after the bosses": normally the client's own BossSpawnProcessor.OnUpdate
///     creates it when the mirrored _pendingBosses hits 0 (phase 4D); if the host already has one, the client has none
///     and no boss puppet is alive for 3 s, it calls SurvivorsGameManager.CreateNextProgressionObjective itself.
/// Layout (after the type byte): 6 x i32 | mainCurrent i32 | mainTotal i32 | hasProgress u8 | progStarting i32 | progTotal i32.
/// </summary>
public static class ObjectiveSync
{
    private const float CheckInterval = 0.25f;
    private const float RefreshInterval = 2f;
    private const float CatchUpDelay = 3f;
    private const float TelemetryInterval = 10f;

    private struct State
    {
        public int Enemies, Elites, Bosses, Tier, Completed, MainCurrent, MainTotal;
        public bool HasProgress;
        public int ProgStarting, ProgTotal;

        public bool SameAs(in State o) =>
            Enemies == o.Enemies && Elites == o.Elites && Bosses == o.Bosses && Tier == o.Tier && Completed == o.Completed &&
            MainCurrent == o.MainCurrent && MainTotal == o.MainTotal && HasProgress == o.HasProgress &&
            ProgStarting == o.ProgStarting && ProgTotal == o.ProgTotal;
    }

    private static State _lastSent;
    private static bool _hasLastSent;
    private static float _checkTimer, _refreshTimer, _telemetryTimer;

    private static State _lastRecv;
    private static bool _hasRecv;
    private static float _missingSince = -1f;

    private static int _sent, _recv, _applied, _mainRaised, _catchUps, _errors;

    public static void Tick(float dt)
    {
        try
        {
            if (CoopRole.IsHostSide) HostTick(dt);
            else if (CoopRole.IsClientSide) ClientTick(dt);
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][objectives] Tick: {ex}");
        }

        _telemetryTimer += dt;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        if (_sent + _recv + _errors > 0)
        {
            var s = _hasRecv ? _lastRecv : _lastSent;
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][objectives] {NetworkManager.Role}: sent={_sent} recv={_recv} applied={_applied} mainRaised={_mainRaised} " +
                $"catchUps={_catchUps} errors={_errors} | kills={s.Enemies}/{s.Elites}/{s.Bosses} tier={s.Tier} completed={s.Completed} " +
                $"main={s.MainCurrent}/{s.MainTotal} progress={(s.HasProgress ? $"start {s.ProgStarting} total {s.ProgTotal}" : "-")}{LocalSummary()}");
        }
        _sent = _recv = _applied = _mainRaised = _catchUps = 0;
    }

    private static string LocalSummary()
    {
        if (!CoopRole.IsClientSide) return "";
        var sgm = GameManagerUtil.GameData?.SurvivorsGameManager;
        var main = sgm != null ? sgm.MainObjective : null;
        var progs = sgm != null ? sgm.ProgressObjectives : null;
        return $" | local main={(main != null ? $"{main.Current}/{main.Total}" : "-")} localProgress={progs?.Count ?? -1} liveBosses={ClientPuppets.LiveBossCount}";
    }

    // ---------------- host ----------------

    private static void HostTick(float dt)
    {
        if (!NetworkManager.HasPeer || !GameManagerUtil.HasGameManager) { _hasLastSent = false; return; }
        _checkTimer += dt;
        _refreshTimer += dt;
        if (_checkTimer < CheckInterval) return;
        _checkTimer = 0f;
        if (!TryRead(out var s)) return;
        if (_hasLastSent && s.SameAs(_lastSent) && _refreshTimer < RefreshInterval) return;
        _refreshTimer = 0f;

        var w = new NetDataWriter();
        w.Put((byte)PacketType.ObjectiveSync);
        w.Put(s.Enemies); w.Put(s.Elites); w.Put(s.Bosses);
        w.Put(s.Tier); w.Put(s.Completed);
        w.Put(s.MainCurrent); w.Put(s.MainTotal);
        w.Put(s.HasProgress);
        w.Put(s.ProgStarting); w.Put(s.ProgTotal);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _lastSent = s;
        _hasLastSent = true;
        _sent++;
    }

    private static bool TryRead(out State s)
    {
        s = default;
        var data = GameManagerUtil.GameData;
        var stats = GameManagerUtil.GameStats;
        var sgm = data?.SurvivorsGameManager;
        if (data == null || stats == null || sgm == null) return false;
        s.Enemies = stats.EnemiesKilled;
        s.Elites = stats.ElitesKilled;
        s.Bosses = stats.BossesKilled;
        s.Tier = data.Tier;
        s.Completed = data.Progression != null ? data.Progression.CompletedProgressObjectiveProgression : 0;
        var main = sgm.MainObjective;
        s.MainCurrent = main != null ? main.Current : -1;
        s.MainTotal = main != null ? main.Total : -1;
        var progs = sgm.ProgressObjectives;
        if (progs != null && progs.Count > 0 && progs[0] != null)
        {
            s.HasProgress = true;
            s.ProgStarting = progs[0].Starting;
            s.ProgTotal = progs[0].Total;
        }
        return true;
    }

    // ---------------- client ----------------

    public static void OnObjectiveSync(NetDataReader r)
    {
        var s = new State
        {
            Enemies = r.GetInt(), Elites = r.GetInt(), Bosses = r.GetInt(),
            Tier = r.GetInt(), Completed = r.GetInt(),
            MainCurrent = r.GetInt(), MainTotal = r.GetInt(),
            HasProgress = r.GetBool(),
            ProgStarting = r.GetInt(), ProgTotal = r.GetInt(),
        };
        if (!CoopRole.IsClientSide) return;
        _recv++;
        _lastRecv = s;
        _hasRecv = true;
        try { Apply(s); }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][objectives] Applying host objectives: {ex}");
        }
    }

    private static void Apply(in State s)
    {
        if (!ClientPuppets.MatchStarted || !GameManagerUtil.HasGameManager) return;
        var data = GameManagerUtil.GameData;
        var stats = GameManagerUtil.GameStats;
        var sgm = data?.SurvivorsGameManager;
        if (data == null || stats == null || sgm == null) return;

        stats.EnemiesKilled = s.Enemies;
        stats.ElitesKilled = s.Elites;
        stats.BossesKilled = s.Bosses;
        if (s.Tier > data.Tier) data.Tier = s.Tier;
        var prog = data.Progression;
        if (prog != null && s.Completed > prog.CompletedProgressObjectiveProgression) prog.CompletedProgressObjectiveProgression = s.Completed;

        var main = sgm.MainObjective;
        if (main != null && s.MainCurrent >= 0 && main.Total > 0)
        {
            int target = Math.Min(s.MainCurrent, main.Total - 1); // never complete it here
            if (target > main.Current)
            {
                main.Current = target;
                _mainRaised++;
            }
        }
        _applied++;
    }

    /// <summary>Safety net for the next kill objective after a boss wave (see the class summary).</summary>
    private static void ClientTick(float dt)
    {
        if (!_hasRecv || !ClientPuppets.MatchStarted || !GameManagerUtil.HasGameManager) { _missingSince = -1f; return; }
        var data = GameManagerUtil.GameData;
        var sgm = data?.SurvivorsGameManager;
        var progs = sgm != null ? sgm.ProgressObjectives : null;
        bool missing = _lastRecv.HasProgress && progs != null && progs.Count == 0 && ClientPuppets.LiveBossCount == 0;
        if (!missing) { _missingSince = -1f; return; }
        float now = UnityEngine.Time.unscaledTime;
        if (_missingSince < 0f) { _missingSince = now; return; }
        if (now - _missingSince < CatchUpDelay) return;
        _missingSince = -1f;

        var bsp = data!.BossSpawnDetails;
        if (bsp != null) { bsp._hasPendingBosses = false; bsp._pendingBosses = 0; } // the normal path must not create a second one
        int completed = data.Progression != null ? data.Progression.CompletedProgressObjectiveProgression : _lastRecv.Completed;
        sgm!.CreateNextProgressionObjective(completed);
        _catchUps++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][objectives] Client: host has a kill objective and we didn't - created the next one (completed={completed}).");
    }
}
