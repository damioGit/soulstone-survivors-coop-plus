using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Boss statues on the clients. Before a boss appears, BossSpawnProcessor.SpawnBossRoutine puts up its statue (the
/// purple see-through "&lt;Boss&gt;-Statue" with a StatueComponent) for TimeToBreakStatue seconds, then breaks it and spawns
/// the boss. The client never runs that routine (ClientSpawnBlock), so it saw nothing until the boss puppet showed up.
/// Host: scans the live statues and sends StatueSync spawn (id, prefab name, position, yaw) / gone (id).
/// Client: the same prefab (every client has the statue assets loaded, BossSpawnProcessor.Setup preloads them) put up
/// and taken down with the host's.
/// Layout after the type byte: kind u8 (0 spawn, 1 gone) | id u16 | [name string | pos v3 | yaw f32]. ReliableOrdered.
/// </summary>
public static class StatueSync
{
    private const byte KindSpawn = 0, KindGone = 1;
    private const float ScanInterval = 1f; // 0.14.0: was 0.25 s, a 3 ms FindObjectsOfType four times a second
    private const float MaxSeconds = 12f; // the game breaks its statues after a few seconds

    private static readonly Dictionary<IntPtr, ushort> _hostIds = new();
    private static readonly Dictionary<ushort, GameObject> _copies = new();
    private static ushort _nextId = 1;
    private static float _scanTimer;
    private static int _sent, _shown, _missing;

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None || !GameManagerUtil.HasGameManager)
        {
            if (_hostIds.Count + _copies.Count > 0) Clear();
            return;
        }
        if (NetworkManager.Role != NetRole.Host) return;
        _scanTimer -= dt;
        if (_scanTimer > 0f) return;
        _scanTimer = ScanInterval;
        try
        {
            var live = new HashSet<IntPtr>();
            foreach (var s in UnityEngine.Object.FindObjectsOfType<StatueComponent>())
            {
                if (s == null || !s.gameObject.activeInHierarchy) continue;
                live.Add(s.Pointer);
                if (_hostIds.ContainsKey(s.Pointer)) continue;
                ushort id = _nextId++;
                _hostIds[s.Pointer] = id;
                var t = s.transform;
                var w = new NetDataWriter();
                w.Put((byte)PacketType.StatueSync);
                w.Put(KindSpawn);
                w.Put(id);
                w.Put(EnemyPrefabKeys.StripClone(s.gameObject.name));
                w.Put(t.position.x); w.Put(t.position.y); w.Put(t.position.z);
                w.Put(t.eulerAngles.y);
                NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
                _sent++;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][statues] Host: '{s.gameObject.name}' #{id} at {t.position}.");
            }
            List<IntPtr>? gone = null;
            foreach (var kv in _hostIds) if (!live.Contains(kv.Key)) (gone ??= new List<IntPtr>()).Add(kv.Key);
            if (gone == null) return;
            foreach (var p in gone)
            {
                var w = new NetDataWriter();
                w.Put((byte)PacketType.StatueSync);
                w.Put(KindGone);
                w.Put(_hostIds[p]);
                NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
                _hostIds.Remove(p);
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][statues] Host scan: {ex.Message}");
        }
    }

    private static void Clear()
    {
        _hostIds.Clear();
        foreach (var go in _copies.Values) if (go != null) UnityEngine.Object.Destroy(go);
        _copies.Clear();
    }

    public static void OnStatueSync(NetDataReader r)
    {
        byte kind = r.GetByte();
        ushort id = r.GetUShort();
        if (kind == KindGone)
        {
            if (_copies.TryGetValue(id, out var go) && go != null) UnityEngine.Object.Destroy(go);
            _copies.Remove(id);
            return;
        }
        string name = r.GetString();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        float yaw = r.GetFloat();
        if (NetworkManager.Role != NetRole.Client || !GameManagerUtil.HasGameManager || _copies.ContainsKey(id)) return;
        try
        {
            StatueComponent? prefab = null;
            foreach (var s in Resources.FindObjectsOfTypeAll<StatueComponent>())
                if (s != null && !s.gameObject.scene.IsValid() && s.gameObject.name == name) { prefab = s; break; }
            if (prefab == null)
            {
                _missing++;
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][statues] Client: no statue asset '{name}'.");
                return;
            }
            var copy = UnityEngine.Object.Instantiate(prefab.gameObject, pos, Quaternion.Euler(0f, yaw, 0f));
            copy.name = $"SoulstoneCoop_{name}_{id}";
            copy.SetActive(true);
            // A lost "gone" never leaves a statue standing. 0.14.0: not in Mastery, whose lord statues stand the whole
            // cycle (the guests lost them after 12 s); the host's "gone" and the scene change take them down there.
            if (Global.SelectedGameMode != GameModes.Mastery) UnityEngine.Object.Destroy(copy, MaxSeconds);
            _copies[id] = copy;
            _shown++;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][statues] Client: '{name}' #{id} shown at {pos}.");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][statues] Client '{name}': {ex.Message}");
        }
    }

    /// <summary>Test bridge: "cmd statuesync".</summary>
    internal static string TestInfo(string[] args) => $"hostLive={_hostIds.Count} copies={_copies.Count} sent={_sent} shown={_shown} missing={_missing}";
}
