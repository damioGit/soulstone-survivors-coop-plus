using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Life;

/// <summary>
/// Heal orbs for the clients (#8). HealthProjectile.FireProjectileToAllEnemies(origin, amount) sends one heal orb to
/// every entity of the Players team. On the host the clients are only stand-ins (the proxy ignores healing and the
/// TeamQueryGuard keeps it out of that query), so a client never got the heal. The host now forwards each call as
/// HealOrb (origin + amount) and every client fires a real orb from the same point at its own player
/// (HealthProjectile.FireProjectile), so the heal and its visuals happen where they belong.
/// A client's own local calls (from puppets dying on its side) are counted in the telemetry: if they show up, the
/// client already heals itself and those would double.
/// Layout after the type byte: origin v3 | amount f32. ReliableUnordered.
/// </summary>
public static class HealOrbs
{
    private static bool _replaying;
    private static int _sent, _recv, _played, _localCalls, _errors;
    private static float _telemetryTimer;

    internal static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None) return;
        _telemetryTimer += dt;
        if (_telemetryTimer < 10f) return;
        _telemetryTimer = 0f;
        if (_sent + _recv + _localCalls + _errors > 0)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][healorbs] sent={_sent} recv={_recv} played={_played} localCalls={_localCalls} errors={_errors}");
    }

    /// <summary>Postfix of FireProjectileToAllEnemies (static; a Vector3 and a float by value: safe to patch).</summary>
    internal static void AfterFireToAll(Vector3 origin, float amount)
    {
        if (_replaying) return;
        if (NetworkManager.Role == NetRole.Client) { _localCalls++; return; }
        if (NetworkManager.Role != NetRole.Host || !Lobby.CoopSession.Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.HealOrb);
        w.Put(origin.x); w.Put(origin.y); w.Put(origin.z);
        w.Put(amount);
        NetworkManager.Send(w, DeliveryMethod.ReliableUnordered);
        _sent++;
    }

    public static void OnHealOrb(NetDataReader r)
    {
        var origin = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        float amount = r.GetFloat();
        _recv++;
        if (NetworkManager.Role != NetRole.Client) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null || !Lobby.CoopSession.LocalInRun || CoopLife.LocalOut) return;
        try
        {
            _replaying = true;
            HealthProjectile.FireProjectile(origin, local, amount);
            _played++;
        }
        catch (Exception ex)
        {
            if (++_errors <= 3) Plugin.StaticLog.LogError($"[SoulstoneCoop][healorbs] replay: {ex}");
        }
        finally { _replaying = false; }
    }

    /// <summary>Test bridge, host: "cmd healorb AMOUNT" fires the game's heal orbs to all players from the host.</summary>
    internal static string TestFire(string[] args)
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return "no local player";
        float amount = args.Length > 0 && float.TryParse(args[0], out var a) ? a : 25f;
        HealthProjectile.FireProjectileToAllEnemies(local.transform.position, amount);
        return $"fired {amount} (sent={_sent})";
    }
}

[HarmonyPatch(typeof(HealthProjectile), nameof(HealthProjectile.FireProjectileToAllEnemies))]
public static class HealOrbsFireToAllPatch
{
    [HarmonyPostfix]
    public static void Postfix(Vector3 originPosition, float healthAmount)
    {
        try { HealOrbs.AfterFireToAll(originPosition, healthAmount); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][healorbs] {ex}"); }
    }
}
