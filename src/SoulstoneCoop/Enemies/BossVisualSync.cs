using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// 0.14.0: a boss's phases turn parts of it on and off (Mh'thaeus' body, tentacles and void effects in the finale)
/// and drive its animator; the client's puppet runs no phases (its AI is off), so the guests saw an empty pentagram
/// where the host fought the boss. The phase actions aren't Unity objects, so nothing could watch them: the host
/// watches the result instead. Four times a second, for each tracked boss: which of its own children are active
/// (runtime clones left out) and its animator's bool/int parameters; what changed goes to the clients, which apply it
/// to the puppet by the same child path (sibling indexes: same prefab, same hierarchy).
/// Layout (after the type byte): netId u16 | kind u8 | n u16 | Objects: n x (path string, on bool) |
///   Anim: n x (hash i32, isInt u8, value i32). ReliableOrdered.
/// </summary>
public static class BossVisualSync
{
    private const byte KindObjects = 0, KindAnim = 1;
    private const float Interval = 0.25f;
    private const int PerPacket = 120;

    private sealed class HostBoss
    {
        public Entity Entity = null!;
        public readonly List<(Transform t, string path, bool on)> Parts = new();
        public readonly List<(Renderer r, string path, bool on)> Renderers = new(); // path "r:..." on the wire
        public Animator? Animator;
        public readonly Dictionary<int, (bool isInt, int value)> Params = new();
        public int StateHash;
    }

    private static readonly Dictionary<ushort, HostBoss> Hosted = new();
    private static readonly Dictionary<ushort, List<(string path, bool on)>> PendingObjects = new();
    private static readonly Dictionary<ushort, List<(int hash, bool isInt, int value)>> PendingAnim = new();
    private static float _timer, _pendingTimer;
    private static int _sentParts, _sentParams, _applied, _missing;

    public static void Tick(float dt)
    {
        try
        {
            if (NetworkManager.Role == NetRole.Host) HostTick(dt);
            else if (NetworkManager.Role == NetRole.Client) ClientTick(dt);
            else if (Hosted.Count + PendingObjects.Count + PendingAnim.Count > 0) Reset();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][bossvis] {ex}");
            _timer = 2f;
        }
    }

    internal static void Reset()
    {
        Hosted.Clear();
        PendingObjects.Clear();
        PendingAnim.Clear();
    }

    // ---------------- host ----------------

    private static void HostTick(float dt)
    {
        if (!GameManagerUtil.HasGameManager) { if (Hosted.Count > 0) Hosted.Clear(); return; }
        _timer -= dt;
        if (_timer > 0f) return;
        _timer = Interval;
        var live = new HashSet<ushort>();
        foreach (var t in HostEnemySync.AllTracked)
        {
            if (!t.IsBoss || t.DiedSent || t.Entity == null || !t.Entity.gameObject.activeInHierarchy) continue;
            live.Add(t.NetId);
            if (!Hosted.TryGetValue(t.NetId, out var b) || b.Entity == null || b.Entity.Pointer != t.Entity.Pointer)
            {
                b = Build(t.Entity);
                Hosted[t.NetId] = b;
                // Everything once: the puppet starts from the prefab, the host's boss may already differ.
                if (NetworkManager.HasPeer) SendAll(t.NetId, b, Players.NoSlot);
                continue;
            }
            if (!NetworkManager.HasPeer) continue;
            var parts = new List<(string, bool)>();
            for (int i = 0; i < b.Parts.Count; i++)
            {
                var (tr, path, on) = b.Parts[i];
                if (tr == null) continue;
                bool now = tr.gameObject.activeSelf;
                if (now == on) continue;
                b.Parts[i] = (tr, path, now);
                parts.Add((path, now));
            }
            for (int i = 0; i < b.Renderers.Count; i++)
            {
                var (rd, path, on) = b.Renderers[i];
                if (rd == null) continue;
                bool now = rd.enabled;
                if (now == on) continue;
                b.Renderers[i] = (rd, path, now);
                parts.Add((path, now));
            }
            if (parts.Count > 0) SendObjects(t.NetId, parts, Players.NoSlot);
            var changed = ReadParams(b, onlyChanged: true);
            if (changed.Count > 0) SendAnim(t.NetId, changed, Players.NoSlot);
            int state = StateOf(b);
            if (state != 0 && state != b.StateHash)
            {
                b.StateHash = state;
                SendAnim(t.NetId, new List<(int, bool, int)> { (StateKey, true, state) }, Players.NoSlot);
            }
        }
        foreach (var id in Hosted.Keys.Where(k => !live.Contains(k)).ToList()) Hosted.Remove(id);
    }

    private static HostBoss Build(Entity e)
    {
        var b = new HostBoss { Entity = e };
        var root = e.transform;
        foreach (var tr in e.GetComponentsInChildren<Transform>(true))
        {
            if (tr == null || tr == root || tr.name.Contains("(Clone)")) continue;
            string? path = PathOf(tr, root);
            if (path != null) b.Parts.Add((tr, path, tr.gameObject.activeSelf));
        }
        foreach (var rd in e.GetComponentsInChildren<Renderer>(true))
        {
            if (rd == null || rd.transform.name.Contains("(Clone)")) continue;
            string? path = rd.transform == root ? "" : PathOf(rd.transform, root);
            if (path != null) b.Renderers.Add((rd, "r:" + path, rd.enabled));
        }
        b.Animator = e.GetComponentInChildren<Animator>(true);
        try { ReadParams(b, onlyChanged: false); }
        catch (Exception ex) { b.Animator = null; Plugin.StaticLog.LogWarning($"[SoulstoneCoop][bossvis] Host: animator of '{e.gameObject.name}': {ex.Message}"); }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][bossvis] Host: watching '{e.gameObject.name}' ({b.Parts.Count} parts, {b.Renderers.Count} renderers, {b.Params.Count} animator params).");
        return b;
    }

    /// <summary>Sibling indexes from under the root ("2/0/5"); null when a part sits under a runtime clone.</summary>
    private static string? PathOf(Transform tr, Transform root)
    {
        var idx = new List<int>();
        for (var c = tr; c != null && c != root; c = c.parent)
        {
            if (c.name.Contains("(Clone)")) return null;
            idx.Add(c.GetSiblingIndex());
        }
        idx.Reverse();
        return string.Join("/", idx);
    }

    // The animator's parameters can't be read through the interop on these builds (GetParameter: "Method unstripping
    // failed"), so the state the boss's animator is in (layer 0) goes instead: the guest plays the same one.
    private const int StateKey = int.MinValue;

    private static int StateOf(HostBoss b)
    {
        var a = b.Animator;
        if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return 0;
        try { return a.GetCurrentAnimatorStateInfo(0).fullPathHash; }
        catch { return 0; }
    }

    private static List<(int, bool, int)> ReadParams(HostBoss b, bool onlyChanged)
    {
        var changed = new List<(int, bool, int)>();
        var a = b.Animator;
        if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return changed;
        // Animator.parameters doesn't load through the interop (TypeLoadException): by index.
        for (int i = 0; i < a.parameterCount; i++)
        {
            var p = a.GetParameter(i);
            bool isInt;
            int v;
            if (p.type == AnimatorControllerParameterType.Bool) { isInt = false; v = a.GetBool(p.nameHash) ? 1 : 0; }
            else if (p.type == AnimatorControllerParameterType.Int) { isInt = true; v = a.GetInteger(p.nameHash); }
            else continue;
            if (b.Params.TryGetValue(p.nameHash, out var old) && old.value == v && onlyChanged) continue;
            b.Params[p.nameHash] = (isInt, v);
            changed.Add((p.nameHash, isInt, v));
        }
        return changed;
    }

    /// <summary>Host: a guest entered the run (HostEnemySync.ResyncToPeer): it gets every boss's current state.</summary>
    internal static void ResendTo(byte slot)
    {
        if (NetworkManager.Role != NetRole.Host) return;
        foreach (var kv in Hosted) SendAll(kv.Key, kv.Value, slot);
    }

    private static void SendAll(ushort netId, HostBoss b, byte to)
    {
        SendObjects(netId, b.Parts.Where(p => p.t != null).Select(p => (p.path, p.t.gameObject.activeSelf))
            .Concat(b.Renderers.Where(p => p.r != null).Select(p => (p.path, p.r.enabled))).ToList(), to);
        var ps = b.Params.Select(kv => (kv.Key, kv.Value.isInt, kv.Value.value)).ToList();
        if (ps.Count > 0) SendAnim(netId, ps, to);
    }

    private static void SendObjects(ushort netId, List<(string path, bool on)> parts, byte to)
    {
        for (int start = 0; start < parts.Count; start += PerPacket)
        {
            int n = Math.Min(PerPacket, parts.Count - start);
            var w = Header(netId, KindObjects, n);
            for (int i = 0; i < n; i++) { w.Put(parts[start + i].path); w.Put(parts[start + i].on); }
            Send(w, to);
        }
        _sentParts += parts.Count;
    }

    private static void SendAnim(ushort netId, List<(int hash, bool isInt, int value)> ps, byte to)
    {
        var w = Header(netId, KindAnim, ps.Count);
        foreach (var (hash, isInt, value) in ps) { w.Put(hash); w.Put((byte)(isInt ? 1 : 0)); w.Put(value); }
        Send(w, to);
        _sentParams += ps.Count;
    }

    private static NetDataWriter Header(ushort netId, byte kind, int n)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.BossVisual);
        w.Put(netId);
        w.Put(kind);
        w.Put((ushort)n);
        return w;
    }

    private static void Send(NetDataWriter w, byte to)
    {
        if (to != Players.NoSlot) NetworkManager.SendTo(to, w, DeliveryMethod.ReliableOrdered);
        else NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // ---------------- client ----------------

    public static void OnBossVisual(NetDataReader r)
    {
        ushort netId = r.GetUShort();
        byte kind = r.GetByte();
        int n = r.GetUShort();
        if (kind == KindObjects)
        {
            var list = new List<(string, bool)>(n);
            for (int i = 0; i < n; i++) list.Add((r.GetString(), r.GetBool()));
            if (!CoopRole.IsClientSide) return;
            if (!ApplyObjects(netId, list))
            {
                if (!PendingObjects.TryGetValue(netId, out var pend)) PendingObjects[netId] = pend = new();
                pend.AddRange(list);
            }
        }
        else
        {
            var list = new List<(int, bool, int)>(n);
            for (int i = 0; i < n; i++) list.Add((r.GetInt(), r.GetByte() != 0, r.GetInt()));
            if (!CoopRole.IsClientSide) return;
            if (!ApplyAnim(netId, list))
            {
                if (!PendingAnim.TryGetValue(netId, out var pend)) PendingAnim[netId] = pend = new();
                pend.AddRange(list);
            }
        }
    }

    /// <summary>The puppet may come a moment after (pending spawns wait for the match): retry once a second.</summary>
    private static void ClientTick(float dt)
    {
        if (PendingObjects.Count + PendingAnim.Count == 0) return;
        _pendingTimer -= dt;
        if (_pendingTimer > 0f) return;
        _pendingTimer = 1f;
        foreach (var id in PendingObjects.Keys.ToList())
            if (ApplyObjects(id, PendingObjects[id])) PendingObjects.Remove(id);
        foreach (var id in PendingAnim.Keys.ToList())
            if (ApplyAnim(id, PendingAnim[id])) PendingAnim.Remove(id);
    }

    private static bool ApplyObjects(ushort netId, List<(string path, bool on)> list)
    {
        var e = ClientPuppets.TryGetPuppetEntity(netId);
        if (e == null) return false;
        var root = e.transform;
        int applied = 0, missing = 0;
        foreach (var (path, on) in list)
        {
            bool renderer = path.StartsWith("r:");
            var tr = Resolve(root, renderer ? path.Substring(2) : path);
            if (tr == null) { missing++; continue; }
            if (renderer)
            {
                var rd = tr.GetComponent<Renderer>();
                if (rd == null) { missing++; continue; }
                if (rd.enabled != on) { rd.enabled = on; applied++; }
            }
            else if (tr.gameObject.activeSelf != on) { tr.gameObject.SetActive(on); applied++; }
        }
        _applied += applied;
        _missing += missing;
        if (applied > 0 || missing > 0)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][bossvis] Client: '{e.gameObject.name}' #{netId}: {applied} part(s) switched, {missing} not found ({list.Count} sent).");
        return true;
    }

    private static Transform? Resolve(Transform root, string path)
    {
        var t = root;
        if (path.Length == 0) return t;
        foreach (var s in path.Split('/'))
        {
            if (!int.TryParse(s, out int i) || i < 0 || i >= t.childCount) return null;
            t = t.GetChild(i);
        }
        return t;
    }

    private static bool ApplyAnim(ushort netId, List<(int hash, bool isInt, int value)> list)
    {
        var e = ClientPuppets.TryGetPuppetEntity(netId);
        if (e == null) return false;
        var a = e.GetComponentInChildren<Animator>(true);
        if (a == null || a.runtimeAnimatorController == null) return true; // nothing to drive
        foreach (var (hash, isInt, value) in list)
        {
            if (hash == StateKey)
            {
                if (a.isActiveAndEnabled && a.GetCurrentAnimatorStateInfo(0).fullPathHash != value && a.HasState(0, value)) a.CrossFade(value, 0.1f, 0);
                continue;
            }
            if (isInt) a.SetInteger(hash, value);
            else a.SetBool(hash, value != 0);
        }
        return true;
    }

    internal static string TestInfo() =>
        $"hosted={Hosted.Count} ({string.Join(",", Hosted.Select(kv => $"#{kv.Key}:{kv.Value.Parts.Count}p/{kv.Value.Params.Count}a"))}) sentParts={_sentParts} sentParams={_sentParams} applied={_applied} missing={_missing} pending={PendingObjects.Count}/{PendingAnim.Count}";
}
