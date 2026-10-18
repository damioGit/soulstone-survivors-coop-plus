using HarmonyLib;
using SoulstoneCoop.Skills;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Pendências: keeps the mod's stand-ins (the host's proxy, Players team, and the RemoteCaster, Summons team, on both
/// sides) out of AIUtil.GetAllEntitiesInSpecificTeam (0x804170). Its callers, besides cheats and tests (fase3-notas):
/// - LootUtil.ProcessLoot / SearchForPlayerEntities: read each entity's Inventory without a null check (the phase 3b
///   NRE; ProcessLoot also keeps its Summons-team swap, HostLootProxyTeamPatch);
/// - MoveEntityTeamToCenterAction: moves each entity with MoveTowards until it is within 0.001 u of the target, one
///   entity at a time, and only then goes to the next one. The proxy and the caster are put back on the ghost every
///   frame, so the coroutine would never get past them (cutscene/boss sequence stuck, entities after them never moved);
/// - StopEntityTeamAction / ResumeEntityTeamAction (invulnerability, cancel casts, movement mode), KillAllEntitiesAction
///   (ChangeEntityHealth(-MaxValue): through patch 9 that would kill the client with an absurd hit),
///   PlayLineVisualEffectAction, ApplyForceFromEntityAction and HealthProjectile.FireProjectileToAllEnemies (a heal orb
///   for each Players entity: the proxy ignores heals). Harmless or cosmetic for a stand-in, so dropping them is safe.
/// Postfix on a static (Team enum, List pointer) method: trampoline-safe. On the client only the caster can be there.
/// </summary>
[HarmonyPatch(typeof(AIUtil), nameof(AIUtil.GetAllEntitiesInSpecificTeam))]
public static class TeamQueryGuardPatch
{
    private static float _lastLog = -100f;
    private static int _removed;

    [HarmonyPostfix]
    public static void Postfix(Team team, Il2CppSystem.Collections.Generic.List<Entity> listToFill)
    {
        try
        {
            if (listToFill == null || (team & Team.Allies) == 0) return; // stand-ins are Players / Summons only
            bool proxy = RemoteProxy.Active, caster = RemoteCaster.Active;
            if (!proxy && !caster) return;
            for (int i = listToFill.Count - 1; i >= 0; i--)
            {
                var e = listToFill[i];
                if (e == null) continue;
                if ((proxy && RemoteProxy.IsProxy(e)) || (caster && RemoteCaster.IsCaster(e)))
                {
                    listToFill.RemoveAt(i);
                    _removed++;
                }
            }
            if (_removed > 0 && Time.unscaledTime - _lastLog > 10f)
            {
                _lastLog = Time.unscaledTime;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Kept the stand-ins out of a team query ({team}): {_removed} removal(s) since the last log.");
                _removed = 0;
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] TeamQueryGuardPatch: {ex}");
        }
    }
}
