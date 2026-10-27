using UnityEngine;
using HarmonyLib;
using SoulstoneCoop.Net;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Tougher enemies in co-op ([Coop] EnemyHealthBonus): every extra player in the run adds that percentage of health
/// to each enemy (2 players at 50 = +50 %, 3 players = +100 %), since two players deal about twice the damage.
/// Done on the host as damage taken / (1 + bonus) in UnitUtil.ChangeEntityHealth, which every hit, DoT tick and the
/// guests' forwarded damage go through: the same as more health, without touching the enemies' stats (adding
/// multiplicative MaxHealth doubled some enemies and missed others, and pooled entities would stack it). Health
/// bars stay right on every screen (they are percentages); the damage numbers keep showing the hit itself.
/// </summary>
public static class EnemyHealthBonus
{
    private static float _bonus;
    private static float _refreshTimer;
    private static long _hits;

    /// <summary>Bonus right now: the percentage x players in the run besides the host.</summary>
    internal static float CurrentBonus()
    {
        float pct = Plugin.EnemyHealthBonus?.Value ?? 0f;
        if (pct <= 0f || NetworkManager.Role != NetRole.Host) return 0f;
        int extra = 0;
        foreach (var r in Lobby.CoopSession.Remotes) if (r.InRun || r.ChangingScene) extra++;
        return pct / 100f * extra;
    }

    public static void Tick(float dt)
    {
        _refreshTimer -= dt;
        if (_refreshTimer > 0f) return;
        _refreshTimer = 1f;
        float b = CurrentBonus();
        if (!Mathf.Approximately(b, _bonus))
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbonus] Enemy health bonus now +{b:P0}.");
        _bonus = b;
    }

    internal static void Scale(Entity target, ref float healthChange)
    {
        if (_bonus <= 0f || healthChange >= 0f || target == null) return;
        var team = target._team;
        if (team == null || (team.Team & Team.Monsters) == 0 || target.IsPlayer()) return;
        healthChange /= 1f + _bonus;
        _hits++;
    }

    internal static string TestInfo(string[] args) => $"bonus=+{_bonus:P0} scaledHits={_hits}";
}

/// <summary>Host: damage on enemies divided by (1 + bonus). A prefix on the same funnel the 3a/4D patches use
/// (pointers and floats only: trampoline-safe).</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class EnemyHealthBonusPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity targetEntity, ref float healthChange)
    {
        try { EnemyHealthBonus.Scale(targetEntity, ref healthChange); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] EnemyHealthBonusPatch: {ex.Message}"); }
    }
}
