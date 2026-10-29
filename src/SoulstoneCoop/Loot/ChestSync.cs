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
/// Map tasks (MapTask: kill elites / bosses in an area, survive the void presences) work the same way: their trigger
/// (MapTask._taskTriggerObjectInstance, an InteractableOneTimeTrigger like the chest's) is shown and opened by the
/// guests too, with the task's minimap icon; the task's stages come as notices (TaskStage), its enemies are puppets and
/// its reward is a reward object, i.e. a chest above.
/// </summary>
public static class ChestSync
{
    // 0.14.0: the scan was every 0.5 s, two FindObjectsOfType calls with 7-13 ms spikes on the host; chests and map
    // tasks appear seconds apart, so once a second and a half is enough.
    private const float ScanInterval = 1.5f, StateInterval = 0.2f, ReannounceInterval = 4f;
    private const float AssistRadius = 2.6f;

    private const byte KindChest = 0, KindTask = 1;

    private sealed class HostChest
    {
        public ushort Id;
        public byte Kind;
        public InteractableRewardObject? Obj;
        public MapTask? Task;
        public GameObject? Root;           // what stands on the map: the chest, or the task's trigger
        public MapTaskCompletionStage LastStage = MapTaskCompletionStage.None;
        public bool TriggerGone;
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
        public int IconId = -1;
    }

    private static InteractableOneTimeTrigger? Trig(HostChest c)
    {
        if (c.Kind == KindTask)
        {
            var t = c.Task != null ? c.Task._taskTriggerObjectInstance : null;
            return t != null ? t.OneTimeTrigger : null;
        }
        return c.Obj != null && c.Obj.TriggerObject != null ? c.Obj.TriggerObject.OneTimeTrigger : null;
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
        foreach (var c in _client.Values) RemoveCopy(c);
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
                var c = new HostChest { Kind = KindChest, Obj = o, Root = o.gameObject, Name = StripClone(o.gameObject.name) };
                if (!Placed(Anchor(c).position)) continue; // not placed yet (parked far away): next scan
                c.Id = _nextId++;
                _host[o.Pointer] = c;
                SendSpawn(c);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: chest #{c.Id} '{c.Name}' at {o.transform.position}.");
            }
            foreach (var task in UnityEngine.Object.FindObjectsOfType<MapTask>())
            {
                if (task == null || _host.ContainsKey(task.Pointer) || task.Stage != MapTaskCompletionStage.Spawned) continue;
                var trig = task._taskTriggerObjectInstance;
                if (trig == null) continue;
                var c = new HostChest { Id = _nextId++, Kind = KindTask, Task = task, Root = trig.gameObject, Name = StripClone(task.gameObject.name), LastStage = task.Stage };
                _host[task.Pointer] = c;
                SendSpawn(c);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: map task #{c.Id} '{c.Name}' at {trig.transform.position}.");
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
                if (c.Kind == KindTask && TaskTick(c)) { (gone ??= new List<IntPtr>()).Add(kv.Key); continue; }
                if (c.TriggerGone) continue;
                bool alive = c.Root != null && c.Root.activeInHierarchy;
                // HasTriggered flips when someone starts interacting; the chest is open once its trigger completes.
                var trig = alive ? Trig(c) : null;
                bool opened = !alive || (trig != null ? trig.State == InteractableTriggerState.Triggered : c.Obj != null && c.Obj.HasTriggered);
                byte pct = 0;
                if (trig != null)
                {
                    pct = (byte)Mathf.Clamp(Mathf.RoundToInt(trig.GetInteractionPercent() * 100f), 0, 100);
                    LogInteracting(c, trig);
                }
                if (opened)
                {
                    SendState(c.Id, 100, true);
                    c.TriggerGone = true;
                    if (c.Kind == KindChest) (gone ??= new List<IntPtr>()).Add(kv.Key); // a task is watched until it ends
                    _opened++;
                    Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: {(c.Kind == KindTask ? "task trigger" : "chest")} #{c.Id} opened.");
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
            foreach (var c in _host.Values) if (c.Root != null && !c.TriggerGone) SendSpawn(c);
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
            if (c.TriggerGone) continue;
            var trig = Trig(c);
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
        var t = Anchor(c);
        var w = new NetDataWriter();
        w.Put((byte)PacketType.ChestSpawn);
        w.Put(c.Id);
        w.Put(c.Kind);
        w.Put(c.Name);
        w.Put(t.position.x); w.Put(t.position.y); w.Put(t.position.z);
        w.Put(t.eulerAngles.y);
        w.Put(c.Task != null ? (byte)c.Task.TaskMetadata.Difficulty : (byte)0);
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

    /// <summary>A map task's stage changed: tell the guests. True when the task is over (stop watching it).</summary>
    private static bool TaskTick(HostChest c)
    {
        var task = c.Task;
        bool exists = task != null && task.gameObject.activeInHierarchy;
        var stage = exists ? task!.Stage : MapTaskCompletionStage.Failed;
        if (stage != c.LastStage)
        {
            c.LastStage = stage;
            var w = new NetDataWriter();
            w.Put((byte)PacketType.TaskStage);
            w.Put(c.Id);
            w.Put((byte)stage);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: map task #{c.Id} -> {stage}.");
        }
        if (stage == MapTaskCompletionStage.Completed || stage == MapTaskCompletionStage.Failed || !exists)
        {
            if (!c.TriggerGone) SendState(c.Id, 100, true);
            return true;
        }
        return false;
    }

    /// <summary>Where the object really stands: a task reward's root sits at infinity (its parts are placed on their
    /// own), so the trigger's position wins when the root's isn't a real one.</summary>
    private static Transform Anchor(HostChest c)
    {
        var root = c.Root!.transform;
        var p = root.position;
        if (Placed(p)) return root;
        var trig = Trig(c);
        return trig != null ? trig.transform : root;
    }

    private static bool Placed(Vector3 p) => !float.IsInfinity(p.x) && !float.IsNaN(p.x) && Mathf.Abs(p.x) < 1e5f && Mathf.Abs(p.z) < 1e5f;

    private static string StripClone(string n) => n.EndsWith("(Clone)") ? n.Substring(0, n.Length - 7).Trim() : n;

    // ---------------- client ----------------

    public static void OnChestSpawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        byte kind = r.GetByte();
        string name = r.GetString();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        float yaw = r.GetFloat();
        byte difficulty = r.GetByte();
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
            c.Copy = BuildCopy(kind, name, pos, yaw, c);
            if (c.Copy != null) _spawned++;
            if (c.Copy != null && kind == KindTask)
            {
                // The game's task icons: TaskEasiest..TaskHardest follow MapTaskDifficulty's order.
                var icon = (MinimapIconType)((int)MinimapIconType.TaskEasiest + Mathf.Clamp((int)difficulty, 0, 4));
                try { c.IconId = MinimapManager.RequestIcon(icon, c.Copy.transform, pos); } catch { c.IconId = -1; }
            }
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
            RemoveCopy(c);
            _client.Remove(id);
            _opened++;
            return;
        }
        SetBar(c, pct / 100f);
    }

    private static void RemoveCopy(ClientChest c)
    {
        if (c.IconId >= 0)
        {
            try { if (GameManagerUtil.HasGameManager) MinimapManager.RequestRemoveIcon(c.IconId); } catch { }
            c.IconId = -1;
        }
        if (c.Copy != null) UnityEngine.Object.Destroy(c.Copy);
        c.Copy = null;
    }

    private static int _taskStages;
    private static Dictionary<string, InteractableRewardObject>? _rewardPrefabs;

    /// <summary>Client: a map task changed stage on the host (TaskStage = 126): a notice like the game's own.</summary>
    public static void OnTaskStage(NetDataReader r)
    {
        ushort id = r.GetUShort();
        var stage = (MapTaskCompletionStage)r.GetByte();
        if (NetworkManager.Role != NetRole.Client) return;
        string? key = stage == MapTaskCompletionStage.Triggered ? "nt.task_started"
            : stage == MapTaskCompletionStage.Completed ? "nt.task_done"
            : stage == MapTaskCompletionStage.Failed ? "nt.task_failed" : null;
        if (key != null) Lobby.CoopSession.Notice(Ui.Loc.Msg(key), 5f);
        _taskStages++;
        if ((stage == MapTaskCompletionStage.Completed || stage == MapTaskCompletionStage.Failed) && _client.TryGetValue(id, out var c))
        {
            RemoveCopy(c);
            _client.Remove(id);
        }
    }

    private static GameObject? BuildCopy(byte kind, string name, Vector3 pos, float yaw, ClientChest c)
    {
        GameObject? prefab = null;
        if (kind == KindTask)
        {
            var task = Resources.Load<MapTask>("MapTasks/Tasks/" + name);
            var trigPrefab = task != null ? task.TaskTriggerObjectPrefab : null;
            prefab = trigPrefab != null ? trigPrefab.gameObject : null;
        }
        else
        {
            var prefabObj = Resources.Load<InteractableRewardObject>("Interactables/" + name);
            // Task rewards live in subfolders: look them up by name among every reward prefab once.
            if (prefabObj == null)
            {
                _rewardPrefabs ??= Resources.LoadAll<InteractableRewardObject>("Interactables").Where(x => x != null)
                    .GroupBy(x => x.gameObject.name).ToDictionary(g => g.Key, g => g.First());
                _rewardPrefabs.TryGetValue(name, out prefabObj);
            }
            prefab = prefabObj != null ? prefabObj.gameObject : null;
        }
        if (prefab == null)
        {
            _loadFailed++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: no prefab for '{name}' (kind {kind}).");
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
            var trig = c.TriggerGone ? null : Trig(c);
            parts.Add($"host#{c.Id}({(c.Kind == KindTask ? "task:" + c.LastStage : "chest")})@{(c.Root != null ? Anchor(c).position.ToString("0.0") : "-")}:{trig?.State}:{(trig != null ? trig.GetInteractionPercent() : -1):0.00}:who={(trig?.InteractingEntity != null ? trig.InteractingEntity.gameObject.name : "-")}");
        }
        foreach (var c in _client.Values)
            parts.Add($"copy#{c.Id}@{(c.Copy != null ? c.Copy.transform.position.ToString("0.0") : "-")}:{c.Percent}%:icon={c.IconId}");
        if (_taskStages > 0) parts.Add($"taskStages={_taskStages}");
        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }
}
