using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Loot;

/// <summary>
/// Chests for the clients (#4). The reward objects (InteractableRewardObject: the guarded chests of
/// GameManagerInteractableGuardedRewardSpawnerComponent, Interactables/Interactable-Guarded-Chest-&lt;Map&gt;) only
/// exist on the host: they are MonoBehaviours, not Entities, so the puppet pipeline never saw them.
/// Host: scans the live reward objects and sends ChestSpawn (id, prefab name, position, yaw), ChestState (id,
/// interaction %, opened) and keeps re-announcing them every few seconds for clients that arrive late.
/// Client: a visual copy of the same prefab with its logic and colliders stripped (built under an inactive parent
/// so the game's own UpdateAll never sees it), its progress bar driven from the host, removed when opened.
/// Opening: the client walks to the chest; on the host its stand-in (RemoteProxy, Players team) stands in the
/// chest's trigger, and when the game's trigger only accepts real players the host hands the interaction to the
/// stand-in (<see cref="AssistProxies"/>). The reward goes through LootUtil.ProcessLoot, which LootSync already
/// replicates, so every player gets its share.
/// </summary>
public static class ChestSync
{
    private const float ScanInterval = 0.5f, StateInterval = 0.2f, ReannounceInterval = 4f;
    private const float AssistRadius = 2.6f;

    private sealed class HostChest
    {
        public ushort Id;
        public InteractableRewardObject Obj = null!;
        public byte LastPercent = 255;
        public bool Opened;
        public string Name = "";
        public float Radius;
    }

    private sealed class ClientChest
    {
        public ushort Id;
        public GameObject? Copy;
        public Renderer? Bar;
        public string? BarProperty;
        public byte Percent;
    }

    private static readonly Dictionary<IntPtr, HostChest> _host = new();
    private static readonly Dictionary<ushort, ClientChest> _client = new();
    private static ushort _nextId = 1;
    private static float _scanTimer, _stateTimer, _announceTimer, _telemetryTimer;
    private static int _spawnSent, _stateSent, _spawned, _loadFailed, _opened, _assists, _errors;
    private static GameObject? _stash; // inactive parent for building copies
    private static string _lastInteracting = "";

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None) { if (_host.Count + _client.Count > 0) Clear(); return; }
        try
        {
            if (!GameManagerUtil.HasGameManager) { if (_host.Count + _client.Count > 0) Clear(); return; }
            if (NetworkManager.Role == NetRole.Host) HostTick(dt);
        }
        catch (Exception ex)
        {
            if (++_errors <= 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][chests] {ex}");
        }
        _telemetryTimer += dt;
        if (_telemetryTimer >= 10f)
        {
            _telemetryTimer = 0f;
            if (_spawnSent + _stateSent + _spawned + _opened + _assists + _loadFailed + _errors > 0)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] {NetworkManager.Role}: tracked={_host.Count} copies={_client.Count} spawnSent={_spawnSent} stateSent={_stateSent} " +
                    $"spawned={_spawned} loadFailed={_loadFailed} opened={_opened} assists={_assists} errors={_errors}");
        }
    }

    private static void Clear()
    {
        _host.Clear();
        foreach (var c in _client.Values) if (c.Copy != null) UnityEngine.Object.Destroy(c.Copy);
        _client.Clear();
    }

    // ---------------- host ----------------

    private static void HostTick(float dt)
    {
        _scanTimer -= dt;
        if (_scanTimer <= 0f)
        {
            _scanTimer = ScanInterval;
            foreach (var o in UnityEngine.Object.FindObjectsOfType<InteractableRewardObject>())
            {
                if (o == null || _host.ContainsKey(o.Pointer)) continue;
                var t0 = o.TriggerObject != null ? o.TriggerObject.OneTimeTrigger : null;
                if (t0 != null ? t0.State == InteractableTriggerState.Triggered : o.HasTriggered) continue;
                var c = new HostChest { Id = _nextId++, Obj = o, Name = StripClone(o.gameObject.name) };
                _host[o.Pointer] = c;
                SendSpawn(c);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: chest #{c.Id} '{c.Name}' at {o.transform.position}.");
            }
        }

        AssistProxies();

        _stateTimer -= dt;
        if (_stateTimer <= 0f)
        {
            _stateTimer = StateInterval;
            List<IntPtr>? gone = null;
            foreach (var kv in _host)
            {
                var c = kv.Value;
                var o = c.Obj;
                bool alive = o != null && o.gameObject.activeInHierarchy;
                // HasTriggered flips when someone starts interacting; the chest is open once its trigger completes.
                var trig = alive && o!.TriggerObject != null ? o.TriggerObject.OneTimeTrigger : null;
                bool opened = !alive || (trig != null ? trig.State == InteractableTriggerState.Triggered : o!.HasTriggered);
                byte pct = 0;
                if (trig != null)
                {
                    pct = (byte)Mathf.Clamp(Mathf.RoundToInt(trig.GetInteractionPercent() * 100f), 0, 100);
                    LogInteracting(c, trig);
                }
                if (opened)
                {
                    SendState(c.Id, 100, true);
                    (gone ??= new List<IntPtr>()).Add(kv.Key);
                    _opened++;
                    Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: chest #{c.Id} opened.");
                    continue;
                }
                if (pct != c.LastPercent && (Mathf.Abs(pct - c.LastPercent) >= 2 || pct == 0 || pct == 100))
                {
                    c.LastPercent = pct;
                    SendState(c.Id, pct, false);
                }
            }
            if (gone != null) foreach (var k in gone) _host.Remove(k);
        }

        _announceTimer -= dt;
        if (_announceTimer <= 0f)
        {
            _announceTimer = ReannounceInterval;
            foreach (var c in _host.Values) if (c.Obj != null) SendSpawn(c);
        }
    }

    private static void LogInteracting(HostChest c, InteractableOneTimeTrigger? trig)
    {
        if (trig == null) return;
        var e = trig.InteractingEntity;
        string who = e == null ? "-" : Enemies.RemoteProxy.TryGetProxySlot(e, out var slot) ? $"proxy{slot}" : e.IsPlayer() ? "host" : e.gameObject.name;
        string s = $"#{c.Id}:{who}:{trig.State}";
        if (s == _lastInteracting) return;
        _lastInteracting = s;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: chest {s} ({trig.GetInteractionPercent():P0}).");
    }

    /// <summary>When a client's stand-in stands at a chest nobody is opening, hand it the interaction (the game's
    /// trigger takes real players only: the stand-in has no Inventory, so IsPlayer() is false).</summary>
    private static void AssistProxies()
    {
        foreach (var c in _host.Values)
        {
            var o = c.Obj;
            var trig = o != null && o.TriggerObject != null ? o.TriggerObject.OneTimeTrigger : null;
            if (trig == null) continue;
            var pos = trig.transform.position;
            var current = trig.InteractingEntity;
            if (current != null)
            {
                // Keep the stand-in's interaction only while it stays there (OnTriggerExit never comes for it).
                if (Enemies.RemoteProxy.TryGetProxySlot(current, out var s) && !Near(current.transform.position, pos, c.Radius + 0.5f))
                {
                    trig.InteractingEntity = null;
                    trig.InteractingEntityTargetTransform = null;
                    trig.State = InteractableTriggerState.Idle;
                }
                continue;
            }
            if (trig.State == InteractableTriggerState.Triggered) continue;
            if (c.Radius <= 0f) c.Radius = TriggerRadius(trig);
            foreach (var r in Lobby.CoopSession.Remotes)
            {
                if (!Enemies.RemoteProxy.IsAlive(r.Slot)) continue;
                var proxy = Enemies.RemoteProxy.ProxyOf(r.Slot);
                if (proxy == null || !Near(proxy.transform.position, pos, c.Radius)) continue;
                trig.InteractingEntity = proxy;
                trig.InteractingEntityTargetTransform = proxy.transform;
                trig.State = InteractableTriggerState.BeingTriggered;
                _assists++;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: slot {r.Slot} ({r.Name}) is opening chest #{c.Id}.");
                break;
            }
        }
    }

    private static bool Near(Vector3 a, Vector3 b, float radius)
    {
        a.y = b.y = 0f;
        return (a - b).sqrMagnitude <= radius * radius;
    }

    /// <summary>The horizontal reach of the chest's own trigger collider (the red circle), else a default.</summary>
    private static float TriggerRadius(InteractableOneTimeTrigger trig)
    {
        var holder = trig.ColliderHolder;
        var col = holder != null ? holder.GetComponentInChildren<Collider>(true) : null;
        if (col == null) return AssistRadius;
        // bounds are empty while the collider is still disabled (DelayToEnable): use the shape itself.
        var sphere = col.TryCast<SphereCollider>();
        var scale = col.transform.lossyScale;
        float r = sphere != null ? sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)) : Mathf.Max(col.bounds.extents.x, col.bounds.extents.z);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Trigger radius {r:0.0} ({col.GetIl2CppType().Name}).");
        return r > 0.5f ? r : AssistRadius;
    }

    private static void SendSpawn(HostChest c)
    {
        var t = c.Obj.transform;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.ChestSpawn);
        w.Put(c.Id);
        w.Put(c.Name);
        w.Put(t.position.x); w.Put(t.position.y); w.Put(t.position.z);
        w.Put(t.eulerAngles.y);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _spawnSent++;
    }

    private static void SendState(ushort id, byte percent, bool opened)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.ChestState);
        w.Put(id);
        w.Put(percent);
        w.Put(opened);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered); // same channel as the spawn: never before it
        _stateSent++;
    }

    private static string StripClone(string n) => n.EndsWith("(Clone)") ? n.Substring(0, n.Length - 7).Trim() : n;

    // ---------------- client ----------------

    public static void OnChestSpawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        string name = r.GetString();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        float yaw = r.GetFloat();
        if (NetworkManager.Role != NetRole.Client || !GameManagerUtil.HasGameManager) return;
        try
        {
            if (_client.TryGetValue(id, out var existing))
            {
                if (existing.Copy != null) existing.Copy.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
                return;
            }
            var c = new ClientChest { Id = id };
            _client[id] = c;
            c.Copy = BuildCopy(name, pos, yaw, c);
            if (c.Copy != null) _spawned++;
        }
        catch (Exception ex)
        {
            if (++_errors <= 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][chests] spawn #{id} '{name}': {ex}");
        }
    }

    public static void OnChestState(NetDataReader r)
    {
        ushort id = r.GetUShort();
        byte pct = r.GetByte();
        bool opened = r.GetBool();
        if (NetworkManager.Role != NetRole.Client) return;
        if (!_client.TryGetValue(id, out var c)) return;
        c.Percent = pct;
        if (opened)
        {
            if (c.Copy != null) UnityEngine.Object.Destroy(c.Copy);
            _client.Remove(id);
            _opened++;
            return;
        }
        SetBar(c, pct / 100f);
    }

    private static GameObject? BuildCopy(string name, Vector3 pos, float yaw, ClientChest c)
    {
        var prefabObj = Resources.Load<InteractableRewardObject>("Interactables/" + name);
        var prefab = prefabObj != null ? prefabObj.gameObject : null;
        if (prefab == null)
        {
            _loadFailed++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: no prefab Interactables/{name}.");
            return null;
        }
        if (_stash == null)
        {
            _stash = new GameObject("SoulstoneCoop_ChestStash");
            _stash.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_stash);
        }
        var go = UnityEngine.Object.Instantiate(prefab, _stash.transform);
        go.name = $"SoulstoneCoop_Chest{c.Id}";
        // Keep the bar renderer before its owner goes away.
        var trig = go.GetComponentInChildren<InteractableOneTimeTrigger>(true);
        if (trig != null) c.Bar = trig.ProgressBarRenderer;
        foreach (var b in go.GetComponentsInChildren<InteractableRewardObject>(true)) UnityEngine.Object.DestroyImmediate(b);
        foreach (var b in go.GetComponentsInChildren<InteractableTriggerObject>(true)) UnityEngine.Object.DestroyImmediate(b);
        foreach (var b in go.GetComponentsInChildren<InteractableOneTimeTrigger>(true)) UnityEngine.Object.DestroyImmediate(b);
        foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false;
        go.transform.SetParent(null, false);
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        go.SetActive(true);
        FindBarProperty(c);
        SetBar(c, 0f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Client: chest #{c.Id} '{name}' shown at {pos} (bar={(c.Bar != null ? c.Bar.name + "/" + (c.BarProperty ?? "-") : "-")}).");
        return go;
    }

    private static readonly string[] BarProperties = { "_Progress", "_Fill", "_FillAmount", "_Percent", "_Value", "_Amount", "_Cutoff" };

    private static void FindBarProperty(ClientChest c)
    {
        var mat = c.Bar != null ? c.Bar.material : null;
        if (mat == null) return;
        foreach (var p in BarProperties)
            if (mat.HasProperty(p)) { c.BarProperty = p; return; }
    }

    private static void SetBar(ClientChest c, float v)
    {
        if (c.Bar == null) return;
        c.Bar.enabled = v > 0.001f;
        if (c.BarProperty != null) c.Bar.material.SetFloat(c.BarProperty, v);
    }

    // ---------------- test bridge ----------------

    /// <summary>"cmd chest": host spawns a guarded chest now (the spawner's own coroutine, no delay).</summary>
    internal static string TestSpawn(string[] args)
    {
        var comp = UnityEngine.Object.FindObjectOfType<GameManagerInteractableGuardedRewardSpawnerComponent>();
        if (comp != null && args.Length == 0)
        {
            comp.StartCoroutine(comp.SpawnReward(false));
            return "spawning through the spawner";
        }
        // No spawner on this map/run: load the map's chest prefab and set it up next to the local player.
        var local = GameState.LocalPlayerEntity;
        if (local == null) return "no local player";
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (args.Length > 0 && args[0] == "list")
        {
            var names = new List<string>();
            foreach (var o in Resources.LoadAll<InteractableRewardObject>("Interactables")) if (o != null) names.Add(o.gameObject.name);
            return names.Count == 0 ? "no reward prefabs under Interactables" : string.Join(",", names);
        }
        string path = args.Length > 0 ? "Interactables/" + args[0] : "Interactables/Interactable-Guarded-Chest-" + scene.Replace("Survivors", "");
        // Loaded as the component: these prefabs' main asset is the reward object (Load<GameObject> throws).
        var prefabObj = Resources.Load<InteractableRewardObject>(path);
        if (prefabObj == null) return "no prefab " + path;
        var prefab = prefabObj.gameObject;
        var go = UnityEngine.Object.Instantiate(prefab, local.transform.position + new Vector3(25f, 0f, 0f), Quaternion.identity);
        var guarded = go.GetComponent<InteractableGuardedRewardObject>();
        if (guarded != null) guarded.Setup(1);
        else go.GetComponent<InteractableAutoRewardObject>()?.Setup(1);
        return $"spawned {path} at {go.transform.position:0.0}";
    }

    /// <summary>"cmd chests": what this side knows about chests.</summary>
    internal static string TestInfo(string[] args)
    {
        var parts = new List<string>();
        foreach (var c in _host.Values)
        {
            var trig = c.Obj != null && c.Obj.TriggerObject != null ? c.Obj.TriggerObject.OneTimeTrigger : null;
            parts.Add($"host#{c.Id}@{(c.Obj != null ? c.Obj.transform.position.ToString("0.0") : "-")}:{trig?.State}:{(trig != null ? trig.GetInteractionPercent() : -1):0.00}:hasTriggered={c.Obj?.HasTriggered}:who={(trig?.InteractingEntity != null ? trig.InteractingEntity.gameObject.name : "-")}");
        }
        foreach (var c in _client.Values)
            parts.Add($"copy#{c.Id}@{(c.Copy != null ? c.Copy.transform.position.ToString("0.0") : "-")}:{c.Percent}%");
        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }
}
