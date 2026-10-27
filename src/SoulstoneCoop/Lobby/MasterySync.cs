using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Lobby;

/// <summary>
/// Mastery in co-op. A Mastery run waits at its portal (MasteryBossSpawner): a player stands in it for TimeToEnter,
/// it fires OnPlayerTriggerSpawn and the MasteryGameManager starts (objectives, lords, cycles). Only the host's game
/// spawns anything, so a guest's MasteryGameManager never started: no objectives, timer at 0:00.
/// Host: when its portal fires, MasteryEvent(Trigger) makes every client fire its own the same way (the client's
/// spawning stays blocked by ClientSpawnBlock; the lords come as puppets). A guest standing in the portal fires the
/// host's through its stand-in, as with chests (the game's trigger takes real players only).
/// Once a second the host also sends the run's state (MasteryEvent State): cycle, lords killed, match time, the
/// objectives' counters and the ritual pillars (positions, destroyed flags). The client starts its own objectives
/// on the first one (its spawn routine is blocked, so it never got there), mirrors the counters and the timer, and
/// shows the pillars as copies of the same prefab, taken down when the host's are destroyed.
/// Layout after the type byte: kind u8 | State: cycle u8 | total u8 | lords u8 | time f32 | main cur/total u16 |
///   n u8 x (cur u16, total u16) | pillars u8 x (pos v3, destroyed u8). ReliableOrdered.
/// </summary>
public static class MasterySync
{
    private const byte KindTrigger = 0, KindState = 1, KindCycle = 2;
    private static bool _advancingFromHost;
    private static int _lastHostCycle;
    private static int _cyclesSent, _cyclesApplied;
    private static float _stateTimer;
    private static readonly List<GameObject> _pillarCopies = new();
    private static bool _clientObjectives;

    private static IntPtr _spawnerPtr;
    private static bool _sentTrigger;
    private static float _proxyTimer;
    private static int _triggers, _proxyTriggers, _clientTriggers;

    private static MasteryBossSpawner? Spawner()
    {
        foreach (var s in UnityEngine.Object.FindObjectsOfType<MasteryBossSpawner>()) if (s != null) return s;
        return null;
    }

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.Client && !GameManagerUtil.HasGameManager && (_pillarCopies.Count > 0 || _clientObjectives)) ResetClient();
        if (NetworkManager.Role != NetRole.Host || !CoopSession.Connected || Global.SelectedGameMode != GameModes.Mastery) return;
        if (!GameManagerUtil.HasGameManager) { _spawnerPtr = IntPtr.Zero; _sentTrigger = false; return; }
        try
        {
            var sp = Spawner();
            if (sp == null) return;
            if (sp.Pointer != _spawnerPtr) { _spawnerPtr = sp.Pointer; _sentTrigger = false; _proxyTimer = 0f; }
            _stateTimer -= dt;
            if (_stateTimer <= 0f) { _stateTimer = 1f; SendState(); }
            var gmc = Manager();
            if (gmc != null && gmc.CurrentMasteryCycle != _lastHostCycle)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Host: cycle {_lastHostCycle} -> {gmc.CurrentMasteryCycle} (lords {gmc._lordsKilled}).");
                _lastHostCycle = gmc.CurrentMasteryCycle;
            }
            if (sp._triggered)
            {
                if (_sentTrigger) return;
                _sentTrigger = true;
                Send(KindTrigger);
                _triggers++;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][mastery] Host: the portal fired - clients follow.");
                return;
            }
            _sentTrigger = false; // every cycle starts at the portal again
            // A guest in the portal: fire it as the game does after TimeToEnter.
            if (!sp.gameObject.activeInHierarchy || !AnyProxyIn(sp)) { _proxyTimer = 0f; return; }
            _proxyTimer += dt;
            if (_proxyTimer < Mathf.Max(0.5f, sp.TimeToEnter)) return;
            Fire(sp);
            _proxyTriggers++;
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][mastery] Host: a guest stood in the portal - fired it.");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][mastery] Host: {ex.Message}");
        }
    }

    private static bool AnyProxyIn(MasteryBossSpawner sp)
    {
        var col = sp.TriggerCollider;
        float r = 2.5f;
        if (col != null)
        {
            var ext = col.bounds.extents;
            if (ext.x > 0.1f) r = Mathf.Max(ext.x, ext.z);
        }
        var c = sp.transform.position;
        foreach (var rp in CoopSession.Remotes)
        {
            if (!Enemies.RemoteProxy.IsAlive(rp.Slot)) continue;
            var proxy = Enemies.RemoteProxy.ProxyOf(rp.Slot);
            if (proxy == null) continue;
            var d = proxy.transform.position - c;
            d.y = 0f;
            if (d.sqrMagnitude <= r * r) return true;
        }
        return false;
    }

    /// <summary>What the portal does when a player has stood in it long enough.</summary>
    private static void Fire(MasteryBossSpawner sp)
    {
        if (sp._triggered) { Plugin.StaticLog.LogInfo("[SoulstoneCoop][mastery] Portal already fired here."); return; }
        sp._triggered = true;
        sp.EnterOnTriggerVFXState();
        sp.OnPlayerTriggerSpawn?.Invoke();
    }

    private static void Send(byte kind)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.MasteryEvent);
        w.Put(kind);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static MasteryGameManager? Manager()
    {
        foreach (var g in UnityEngine.Object.FindObjectsOfType<MasteryGameManager>()) if (g != null) return g;
        return null;
    }

    private static void SendState()
    {
        var gm = Manager();
        if (gm == null) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.MasteryEvent);
        w.Put(KindState);
        w.Put((byte)gm.CurrentMasteryCycle);
        w.Put((byte)gm.TotalCyclesAmount);
        w.Put((byte)gm._lordsKilled);
        w.Put(GameManagerUtil.GameStats != null ? GameManagerUtil.GameStats.MatchTime : 0f);
        var mo = gm.MainObjective;
        w.Put((ushort)(mo != null ? mo.Current : 0));
        w.Put((ushort)(mo != null ? mo.Total : 0));
        var el = gm.EliminationObjectives;
        int n = el != null ? Mathf.Min(el.Count, 16) : 0;
        w.Put((byte)n);
        for (int i = 0; i < n; i++) { var o = el![i]; w.Put((ushort)(o != null ? o.Current : 0)); w.Put((ushort)(o != null ? o.Total : 0)); }
        var pillars = gm.CreatedMasteryPillarsReference;
        int np = pillars != null ? Mathf.Min(pillars.Count, 16) : 0;
        w.Put((byte)np);
        for (int i = 0; i < np; i++)
        {
            var p = pillars![i];
            var pos = p != null ? p.transform.position : Vector3.zero;
            w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
            w.Put((byte)(p == null || p.Destroyed || !p.gameObject.activeInHierarchy ? 1 : 0));
        }
        // The bosses that have their health bar on the host right now.
        var bars = new List<ushort>();
        var bl = GameManagerUtil.BossEntities;
        if (bl != null) foreach (var b in bl) if (b != null && Enemies.HostEnemySync.TryGetNetId(b, out var id)) bars.Add(id);
        w.Put((byte)Mathf.Min(bars.Count, 32));
        for (int i = 0; i < Mathf.Min(bars.Count, 32); i++) w.Put(bars[i]);
        WriteObjectives(w);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void ApplyState(NetDataReader r)
    {
        int cycle = r.GetByte(), total = r.GetByte(), lords = r.GetByte();
        float time = r.GetFloat();
        int mainCur = r.GetUShort(), mainTotal = r.GetUShort();
        int n = r.GetByte();
        var elim = new List<(int cur, int tot)>();
        for (int i = 0; i < n; i++) elim.Add((r.GetUShort(), r.GetUShort()));
        int np = r.GetByte();
        var pillars = new List<(Vector3 pos, bool destroyed)>();
        for (int i = 0; i < np; i++) pillars.Add((new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()), r.GetByte() != 0));
        var bars = new HashSet<ushort>();
        int nb = r.GetByte();
        for (int i = 0; i < nb; i++) bars.Add(r.GetUShort());
        var hostObjectives = ReadObjectives(r);
        if (NetworkManager.Role != NetRole.Client || !GameManagerUtil.HasGameManager) return;
        var gm = Manager();
        if (gm == null) return;

        // The cycle moves only with KindCycle: each side runs its own AdvanceCycle (reset, Mastery power pick,
        // objectives) once per host call.
        gm._lordsKilled = lords;
        var stats = GameManagerUtil.GameStats;
        if (stats != null) stats.MatchTime = time; // the client's clock stops between cycles; the host's is the run's
        if (gm._bossSpawnTriggered && !_clientObjectives && (gm.EliminationObjectives == null || gm.EliminationObjectives.Count == 0))
        {
            // Our spawn routine is blocked before it finishes: finish the start ourselves.
            _clientObjectives = true;
            gm._bossSpawnFinished = true;
            try { gm.CreateLordsObjectivesAfterTrigger(); }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][mastery] Client objectives: {ex.Message}"); }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Client: objectives started ({gm.EliminationObjectives?.Count ?? 0} elimination).");
        }
        var mo = gm.MainObjective;
        if (mo != null && mainTotal > 0) { mo.Total = mainTotal; mo.Current = mainCur; }
        var el = gm.EliminationObjectives;
        if (el != null)
            for (int i = 0; i < Mathf.Min(el.Count, elim.Count); i++)
                if (el[i] != null) { el[i].Total = elim[i].tot; el[i].Current = elim[i].cur; }
        SyncPillars(gm, pillars);
        Enemies.ClientPuppets.ReconcileBossBars(bars);
        ReconcileObjectives(hostObjectives);
    }

    // ---------------- the objectives panel ----------------
    // The client's own MasteryGameManager made its objectives on its own schedule (the ritual one never completed on
    // its side and piled up each cycle). The host's panel is the truth: same ids, same counters.

    private struct Obj { public byte Kind; public string Id; public int Cur, Total; public bool Done; }
    private const byte OSimple = 0, OProgress = 1, OElimNonCum = 2, OElimSpecific = 3;

    private static ObjectivesPanelReferences? Panel() => UnityEngine.Object.FindObjectOfType<ObjectivesPanelReferences>();

    private static void WriteObjectives(NetDataWriter w)
    {
        var list = new List<Obj>();
        var panel = Panel();
        if (panel != null && panel.ActiveItems != null)
            foreach (var item in panel.ActiveItems)
            {
                var o = item != null ? item.ReferenceObjective : null;
                if (o == null || string.IsNullOrEmpty(o.Id)) continue;
                list.Add(Describe(o));
            }
        int n = Mathf.Min(list.Count, 12);
        w.Put((byte)n);
        for (int i = 0; i < n; i++)
        {
            var o = list[i];
            w.Put(o.Kind); w.Put(o.Id); w.Put(o.Cur); w.Put(o.Total); w.Put(o.Done);
        }
    }

    private static Obj Describe(ObjectiveBase o)
    {
        var d = new Obj { Id = o.Id, Done = o.IsComplete() };
        var sp = o.TryCast<SimpleObjectiveWithProgression>();
        if (sp != null) { d.Kind = OProgress; d.Cur = sp.Current; d.Total = sp.Total; return d; }
        var en = o.TryCast<EliminateEnemiesNonCumulativeObjective>();
        if (en != null) { d.Kind = OElimNonCum; d.Cur = en.Current; d.Total = en.Total; return d; }
        var es = o.TryCast<EliminateSpecificEntitiesObjective>();
        if (es != null) { d.Kind = OElimSpecific; d.Cur = es.Current; d.Total = es.Total; return d; }
        d.Kind = OSimple;
        return d;
    }

    private static List<Obj> ReadObjectives(NetDataReader r)
    {
        var list = new List<Obj>();
        int n = r.GetByte();
        for (int i = 0; i < n; i++)
            list.Add(new Obj { Kind = r.GetByte(), Id = r.GetString(), Cur = r.GetInt(), Total = r.GetInt(), Done = r.GetBool() });
        return list;
    }

    private static void Apply(ObjectiveBase o, Obj h)
    {
        var sp = o.TryCast<SimpleObjectiveWithProgression>();
        if (sp != null) { sp.Total = h.Total; sp.Current = h.Cur; sp.Completed = h.Done; return; }
        var en = o.TryCast<EliminateEnemiesNonCumulativeObjective>();
        if (en != null) { en.Total = h.Total; en.Current = h.Done ? h.Total : h.Cur; return; }
        var es = o.TryCast<EliminateSpecificEntitiesObjective>();
        if (es != null) { es.Total = h.Total; es.Current = h.Done ? h.Total : h.Cur; return; }
        var so = o.TryCast<SimpleObjective>();
        if (so != null) so.Completed = h.Done;
    }

    private static void Finish(ObjectiveBase o)
    {
        var sp = o.TryCast<SimpleObjectiveWithProgression>();
        if (sp != null) { sp.Current = sp.Total; sp.Completed = true; return; }
        var en = o.TryCast<EliminateEnemiesNonCumulativeObjective>();
        if (en != null) { en.Current = en.Total; return; }
        var es = o.TryCast<EliminateSpecificEntitiesObjective>();
        if (es != null) { es.Current = es.Total; return; }
        var so = o.TryCast<SimpleObjective>();
        if (so != null) so.Completed = true;
    }

    private static void ReconcileObjectives(List<Obj> host)
    {
        var panel = Panel();
        if (panel == null || panel.ActiveItems == null) return;
        var byId = new Dictionary<string, Obj>();
        foreach (var h in host) byId[h.Id] = h;
        var shown = new HashSet<string>();
        foreach (var item in panel.ActiveItems)
        {
            var o = item != null ? item.ReferenceObjective : null;
            if (o == null || string.IsNullOrEmpty(o.Id)) continue;
            if (byId.TryGetValue(o.Id, out var h) && !shown.Contains(o.Id)) { Apply(o, h); shown.Add(o.Id); }
            else Finish(o); // not on the host's panel (or a duplicate): let the game fade it out
        }
        foreach (var h in host)
        {
            if (shown.Contains(h.Id) || h.Done) continue;
            ObjectiveBase? o = h.Kind switch
            {
                OProgress => new SimpleObjectiveWithProgression(h.Id, 0, h.Total),
                OElimNonCum => new EliminateEnemiesNonCumulativeObjective(h.Id, 0, h.Total),
                OElimSpecific => new EliminateSpecificEntitiesObjective(h.Id, h.Total),
                _ => new SimpleObjective(h.Id),
            };
            Apply(o, h);
            panel.TrackObjective(o);
            _objectivesAdded++;
        }
    }

    private static int _objectivesAdded;

    private static void SyncPillars(MasteryGameManager gm, List<(Vector3 pos, bool destroyed)> pillars)
    {
        while (_pillarCopies.Count < pillars.Count)
        {
            var prefab = gm.MasteryPillarPrefab;
            if (prefab == null) return;
            var (pos, destroyed) = pillars[_pillarCopies.Count];
            var go = UnityEngine.Object.Instantiate(prefab.gameObject, pos, Quaternion.identity);
            go.name = "SoulstoneCoop_MasteryPillar";
            var mp = go.GetComponent<MasteryPillar>();
            if (mp != null) mp.GameManager = gm;
            if (destroyed) go.SetActive(false);
            _pillarCopies.Add(go);
        }
        for (int i = 0; i < Mathf.Min(_pillarCopies.Count, pillars.Count); i++)
        {
            var go = _pillarCopies[i];
            if (go == null) continue;
            go.transform.position = pillars[i].pos;
            if (pillars[i].destroyed && go.activeSelf) go.SetActive(false);
        }
    }

    /// <summary>A new run (or the menu): forget the client's pillars and objectives flag.</summary>
    internal static void ResetClient()
    {
        foreach (var go in _pillarCopies) if (go != null) UnityEngine.Object.Destroy(go);
        _pillarCopies.Clear();
        _clientObjectives = false;
    }

    public static void OnMasteryEvent(NetDataReader r)
    {
        byte kind = r.GetByte();
        if (kind == KindState)
        {
            try { ApplyState(r); }
            catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][mastery] Client state: {ex.Message}"); }
            return;
        }
        if (NetworkManager.Role != NetRole.Client) return;
        try
        {
            if (kind == KindCycle)
            {
                int hostCycle = r.GetByte();
                var gm = Manager();
                if (gm == null) return;
                int before = gm.CurrentMasteryCycle;
                _advancingFromHost = true;
                try { gm.AdvanceCycle(); }
                finally { _advancingFromHost = false; }
                _cyclesApplied++;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Client: AdvanceCycle {before} -> {gm.CurrentMasteryCycle} (host {hostCycle}).");
                return;
            }
            if (kind == KindTrigger)
            {
                var sp = Spawner();
                if (sp == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][mastery] Client: no portal to fire."); return; }
                Fire(sp);
                _clientTriggers++;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][mastery] Client: the host's portal fired - ours too.");
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][mastery] Client event {kind}: {ex}");
        }
    }

    /// <summary>Host, postfix of MasteryGameManager.AdvanceCycle: the clients advance theirs too.</summary>
    internal static void AfterAdvanceCycle(MasteryGameManager gm)
    {
        if (NetworkManager.Role != NetRole.Host || !CoopSession.Connected || _advancingFromHost) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.MasteryEvent);
        w.Put(KindCycle);
        w.Put((byte)gm.CurrentMasteryCycle);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _cyclesSent++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Host: cycle {gm.CurrentMasteryCycle} - clients follow.");
    }

    internal static string TestInfo(string[] args) => $"objectivesAdded={_objectivesAdded} triggers={_triggers} proxyTriggers={_proxyTriggers} clientTriggers={_clientTriggers} cyclesSent={_cyclesSent} cyclesApplied={_cyclesApplied}";
}

[HarmonyLib.HarmonyPatch(typeof(MasteryGameManager), nameof(MasteryGameManager.AdvanceCycle))]
public static class MasteryAdvanceCyclePatch
{
    [HarmonyLib.HarmonyPostfix]
    public static void Postfix(MasteryGameManager __instance)
    {
        try { MasterySync.AfterAdvanceCycle(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] MasteryAdvanceCyclePatch: {ex.Message}"); }
    }
}
