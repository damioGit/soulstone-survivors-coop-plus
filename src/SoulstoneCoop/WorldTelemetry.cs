namespace SoulstoneCoop;

/// <summary>Phase 1: log how many entities actually exist on the host, to size the phase 2 snapshots.
/// The spawn limits live in prefabs, so the code defaults (30-40 alive) may not be the real values.</summary>
public static class WorldTelemetry
{
    private const float IntervalSeconds = 5f;
    private static float _timer;
    private static int _peakEnemies;

    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsHostSide) return;
        _timer += unscaledDeltaTime;
        if (_timer < IntervalSeconds) return;
        _timer = 0f;

        if (!GameManagerUtil.HasGameManager) return;
        var all = EntityRegistry.AllEntities;
        if (all == null) return;

        int enemies = 0, allies = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (e == null) continue;
            var team = e._team;
            if (team == null) continue;
            int t = (int)team.Team;
            if ((t & 12) != 0) enemies++;
            else if ((t & 3) != 0) allies++;
        }
        if (enemies > _peakEnemies) _peakEnemies = enemies;

        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][volume] allEntities={all.Count} activeCount={EntityRegistry.GetCurrentActiveEntitiesCount()} " +
            $"enemies={enemies} allies={allies} peakEnemies={_peakEnemies}");
    }
}
