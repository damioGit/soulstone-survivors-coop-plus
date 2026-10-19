using SoulstoneCoop.Net;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 9, host: more enemies alive with more players ([Coop] EnemyScalePerPlayer, 0 = off, the default).
/// The game knows nothing about player count: the regular wave only goes out while
/// GetCurrentActiveEntitiesCount() is below min(MaxTargetEnemiesAlive, difficulty x TargetEnemiesAliveDifficultyFactor)
/// (GameManagerRegularEnemyContinuousSpawnComponent), and the spawn queues only empty while it's at most AliveUnitLimit
/// (GameManagerSpawnQueuesBaseComponent). Both limits are global, and the phase 3 spawn rotation spreads the enemies
/// over every living player, so with 8 players far apart each one would see a handful. This multiplies those three
/// fields by 1 + k x (players in the run - 1), capped at MaxAlive. Plain int/float fields of the components in
/// GameData.GameManagerComponents: no patch. Checked every second; the game's own value is taken as the new base
/// whenever it differs from what was last written (the game changed it), so it's never multiplied twice. Elites,
/// bosses and enemy health are left alone. See docs/fase9-investigacao.md, section 5.
/// </summary>
public static class EnemyScaling
{
    private const int MaxAlive = 200; // ModifiedCharacter.kMaxEntities is 300; the snapshot grows with it
    private const float CheckInterval = 1f;

    private sealed class Applied
    {
        public int BaseMax, WroteMax;
        public float BaseFactor, WroteFactor;
        public int BaseLimit, WroteLimit;
        public bool HasRegular, HasQueue;
    }

    private static readonly Dictionary<IntPtr, Applied> Seen = new();
    private static float _timer;
    private static float _lastMult = 1f;
    private static int _lastPlayers = 1;

    /// <summary>A new match: new components.</summary>
    internal static void OnMatchStarted()
    {
        Seen.Clear();
        _lastMult = 1f;
        _timer = 0f;
    }

    public static void Tick(float dt)
    {
        if (!CoopRole.IsHostSide) return;
        _timer -= dt;
        if (_timer > 0f) return;
        _timer = CheckInterval;
        try { Apply(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][balance] {ex.Message}"); }
    }

    private static void Apply()
    {
        float k = Math.Clamp(Plugin.EnemyScalePerPlayer?.Value ?? 0f, 0f, 2f);
        if (!GameManagerUtil.HasGameManager || !Lobby.CoopSession.LocalInRun) return;
        int players = 1;
        foreach (var r in Lobby.CoopSession.Remotes) if (r.InRun) players++;
        float mult = 1f + k * (players - 1);
        // Off and never touched: nothing to restore.
        if (Math.Abs(mult - 1f) < 0.001f && Math.Abs(_lastMult - 1f) < 0.001f) return;

        var comps = GameManagerUtil.GameData?.GameManagerComponents;
        if (comps == null) return;
        var parts = new List<string>();
        for (int i = 0; i < comps.Count; i++)
        {
            var c = comps[i];
            if (c == null) continue;
            if (!Seen.TryGetValue(c.Pointer, out var a))
            {
                a = new Applied();
                Seen[c.Pointer] = a;
            }

            var regular = c.TryCast<GameManagerRegularEnemyContinuousSpawnComponent>();
            if (regular != null)
            {
                if (!a.HasRegular || regular.MaxTargetEnemiesAlive != a.WroteMax) a.BaseMax = regular.MaxTargetEnemiesAlive;
                if (!a.HasRegular || Math.Abs(regular.TargetEnemiesAliveDifficultyFactor - a.WroteFactor) > 0.0001f) a.BaseFactor = regular.TargetEnemiesAliveDifficultyFactor;
                a.HasRegular = true;
                int max = Math.Min(MaxAlive, (int)Math.Round(a.BaseMax * mult));
                float factor = a.BaseFactor * mult;
                if (regular.MaxTargetEnemiesAlive != max) regular.MaxTargetEnemiesAlive = max;
                if (Math.Abs(regular.TargetEnemiesAliveDifficultyFactor - factor) > 0.0001f) regular.TargetEnemiesAliveDifficultyFactor = factor;
                a.WroteMax = max;
                a.WroteFactor = factor;
                parts.Add($"MaxTarget {a.BaseMax}->{max} factor {a.BaseFactor:0.##}->{factor:0.##}");
            }

            var queue = c.TryCast<GameManagerSpawnQueuesBaseComponent>();
            if (queue != null)
            {
                if (!a.HasQueue || queue.AliveUnitLimit != a.WroteLimit) a.BaseLimit = queue.AliveUnitLimit;
                a.HasQueue = true;
                int limit = Math.Min(MaxAlive, (int)Math.Round(a.BaseLimit * mult));
                if (queue.AliveUnitLimit != limit) queue.AliveUnitLimit = limit;
                a.WroteLimit = limit;
                parts.Add($"AliveUnitLimit {a.BaseLimit}->{limit}");
            }
        }
        if (Math.Abs(mult - _lastMult) > 0.001f || players != _lastPlayers)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][balance] players={players} k={k:0.##} mult={mult:0.##}: {string.Join(", ", parts)}");
        _lastMult = mult;
        _lastPlayers = players;
    }
}
