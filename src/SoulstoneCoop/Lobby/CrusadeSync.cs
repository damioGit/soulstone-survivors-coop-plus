using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Lobby;

/// <summary>
/// Titan Hunt (the game's Crusade mode) in co-op: a chain of scenes. The lobby's unstable portal and every map's exit
/// portal open the titan choice (CrusadeMapSelectionPanel); picking one calls GameManagerUtil.LoadNextScene(mapId).
/// Only the host chooses: its LoadNextScene goes to the clients (KindGo), and each client opens its own panel, puts the
/// host's map on the first card and presses it, so its game carries the character over as it does for itself.
/// </summary>
public static class CrusadeSync
{
    internal const string LobbyScene = "SurvivorsCrusadesLobby";
    internal const string FinalScene = "SurvivorsFinalBoss";
    private const byte KindGo = 0, KindChoosing = 1, KindState = 2, KindBlood = 3, KindBloodReq = 4, KindOptions = 5,
        KindSuggest = 6, KindPortalReq = 7;
    private const byte PortalChoice = 0, PortalPrimordial = 1, PortalGoblin = 2;

    // ---- the guest's say: it opens the choice / takes the portals through the host, sees the titans on offer on its
    // own panel, and its press there is a proposal the host sees ----

    /// <summary>"SurvivorsDesertScorpionPit" -> "Desert Scorpion Pit".</summary>
    internal static string Pretty(string mapId)
    {
        string id = mapId.StartsWith("Survivors") ? mapId.Substring(9) : mapId;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < id.Length; i++)
        {
            if (i > 0 && char.IsUpper(id[i]) && !char.IsUpper(id[i - 1])) sb.Append(' ');
            sb.Append(id[i]);
        }
        return sb.ToString();
    }

    /// <summary>Postfix of CrusadeMapSelectionPanel.FillOptions, host: the titans on offer go to the clients.</summary>
    internal static void OnHostOptions(CrusadeMapSelectionPanel panel)
    {
        if (NetworkManager.Role != NetRole.Host || !Active || panel.Items == null) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeEvent);
        w.Put(KindOptions);
        int n = 0;
        foreach (var i in panel.Items) if (i != null && !string.IsNullOrEmpty(i.MapId)) n++;
        w.Put((byte)n);
        foreach (var i in panel.Items) if (i != null && !string.IsNullOrEmpty(i.MapId)) w.Put(i.MapId);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Postfix of CrusadeMapSelectionPanel.Close, host: the clients' view of it closes.</summary>
    internal static void OnHostPanelClosed()
    {
        if (NetworkManager.Role != NetRole.Host || !Active) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeEvent);
        w.Put(KindOptions);
        w.Put((byte)0);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static List<string>? _options;   // client: the host's titans on offer (null: no choice open)
    private static bool _optionsShown, _pressingForHost;
    private static float _viewOpenedAt;
    private static float _lastSuggest;

    private static void OnOptions(NetDataReader r)
    {
        int n = r.GetByte();
        var list = new List<string>();
        for (int i = 0; i < n; i++) list.Add(r.GetString());
        if (n == 0)
        {
            _options = null;
            var panel = Panel();
            if (panel != null && panel.IsOpen && _following == null) { panel.Close(); }
            return;
        }
        _options = list;
        _optionsShown = false;
        CoopSession.Notice(Ui.Loc.Msg("nt.titan_options", ("maps", string.Join(" / ", list.Select(Pretty)))), 10f);
    }

    /// <summary>Client: show the host's titans on our own panel (read only: a press is a proposal).</summary>
    private static void TickOptionsView()
    {
        if (_options == null || _following != null) return;
        var panel = Panel();
        if (panel == null) return;
        if (!panel.IsOpen)
        {
            if (_optionsShown || Time.unscaledTime - _viewOpenedAt < 1f) return; // shown once; the guest may close it
            _viewOpenedAt = Time.unscaledTime;
            _opening = true;
            try
            {
                var lobby = Lobby();
                if (lobby != null) lobby.OnPlayerEnterUnstablePortal();
                else
                {
                    var data = Map()?._crusadeData;
                    if (data != null) panel.Open(data, false);
                }
            }
            finally { _opening = false; }
            return; // filled next frame
        }
        if (panel.Items == null) return;
        // Every frame while it's open: the panel may fill its own options again after we replaced them.
        bool changed = false;
        for (int i = 0; i < panel.Items.Count && i < _options.Count; i++)
        {
            var item = panel.Items[i];
            if (item == null || item.MapId == _options[i]) continue;
            changed = true;
            // The client's own bucket of options has the full MapOption of that map (a bare one throws in Setup).
            bool done = false;
            if (panel._mapOptionsBucket != null)
                foreach (var o in panel._mapOptionsBucket)
                    if (o.MapId == _options[i]) { item.Setup(o); done = true; break; }
            if (!done) item.MapTitleLabel?.SetLocalizedString(Pretty(_options[i]));
            item.MapId = _options[i];
        }
        if (changed && !_optionsShown) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: the host's titans shown on our panel: {string.Join(", ", _options)}.");
        _optionsShown = true;
    }

    /// <summary>Prefix of CrusadeMapSelectionItem.OnPressed. Client: a proposal to the host, not a choice.</summary>
    internal static bool AllowPress(CrusadeMapSelectionItem item)
    {
        if (NetworkManager.Role != NetRole.Client || !Active || _pressingForHost) return true;
        if (Time.unscaledTime - _lastSuggest > 1f)
        {
            _lastSuggest = Time.unscaledTime;
            var w = new NetDataWriter();
            w.Put((byte)PacketType.CrusadeEvent);
            w.Put(KindSuggest);
            w.Put(item.MapId ?? "");
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            CoopSession.Notice(Ui.Loc.Msg("nt.titan_proposed", ("map", Pretty(item.MapId ?? "?"))), 5f);
        }
        return false;
    }

    /// <summary>Prefix of the reroll, client: only the host rerolls (a proposal would be noise).</summary>
    internal static bool AllowReroll()
    {
        if (NetworkManager.Role != NetRole.Client || !Active) return true;
        CoopSession.Notice(Ui.Loc.Msg("nt.titan_host_only"), 4f);
        return false;
    }

    /// <summary>Client walked into a portal of the titan hunt: the host takes it (for everyone).</summary>
    private static void RequestPortal(byte which)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeEvent);
        w.Put(KindPortalReq);
        w.Put(which);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        CoopSession.Notice(Ui.Loc.Msg(which == PortalPrimordial ? "nt.titan_primordial_asked" : "nt.titan_choice_asked"), 5f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: walked into the {(which == PortalPrimordial ? "Primordial" : "titan choice")} portal - asking the host.");
    }

    private static void OnPortalRequest(byte from, byte which)
    {
        string who = CoopSession.Get(from)?.Name ?? $"#{from}";
        var panel = Panel();
        if (panel != null && panel.IsOpen) return; // already choosing
        var lobby = Lobby();
        if (which == PortalGoblin)
        {
            var m = Map();
            if (m?.GoblinPortalTrigger == null || !m.GoblinPortalTrigger.activeInHierarchy) return;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: {who} takes everyone into the goblin cave.");
            m.OnPlayerGoblinCavePortal();
            return;
        }
        if (which == PortalPrimordial)
        {
            if (lobby?.PrimordialPortalObject == null || !lobby.PrimordialPortalObject.activeInHierarchy) return;
            CoopSession.Notice(Ui.Loc.Msg("nt.titan_primordial_by", ("name", who)), 5f);
            lobby.OnPlayerEnterPrimordialPortal();
            return;
        }
        if (lobby != null)
        {
            if (lobby.UnstablePortalTrigger == null || !lobby.UnstablePortalTrigger.activeInHierarchy) return;
            lobby.OnPlayerEnterUnstablePortal();
        }
        else
        {
            var map = Map();
            if (map == null || !map.EnabledPortal) return;
            map.OnPlayerEnterPortal();
        }
        CoopSession.Notice(Ui.Loc.Msg("nt.titan_choice_by", ("name", who)), 6f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: {who} opened the titan choice.");
    }

    // ---- the map's shared progress: the host's key fragments, door, boss room, titan blood ----

    private struct State
    {
        public int Keys, KeysTotal, Blood;
        public byte Door, BossRoom, Runes, Sigils;
        public bool BloodTaken, Primordial, Portal, Home, FinalKilled, BloodOut;
        public Vector3 BloodAt;
    }
    private static State _sent;
    private static float _stateTimer, _refreshTimer;

    /// <summary>The final map's sigil runes (VoidKingRuneManager), in order: each one's hold-to-light trigger.</summary>
    private static List<InteractableOneTimeTrigger?> SigilTriggers()
    {
        var list = new List<InteractableOneTimeTrigger?>();
        var mgr = Map()?.VoidKingRuneManager;
        if (mgr?.RuneInstances == null) return list;
        foreach (var ri in mgr.RuneInstances) list.Add(ri?.RuneTriggerObject?.OneTimeTrigger);
        return list;
    }

    private static (InteractableOneTimeTrigger? door, InteractableOneTimeTrigger? boss) Triggers()
    {
        var d = UnityEngine.Object.FindObjectOfType<CrusadeDoorObject>();
        return d == null ? (null, null) : (d.DoorTrigger, d.BossRoomTrigger);
    }

    private static State Read()
    {
        var st = new State();
        var map = Map();
        if (map != null)
        {
            var k = map.CollectKeyFragmentsObjective;
            if (k != null) { st.Keys = k.Current; st.KeysTotal = k.Total; }
            st.BloodTaken = map._hasCollectedTitanBlood;
            if (map._crusadeData != null) st.Blood = map._crusadeData.CurrentTitanBloodAmount;
            var (door, boss) = Triggers();
            st.Door = door != null ? (byte)door.State : (byte)255;
            st.BossRoom = boss != null ? (byte)boss.State : (byte)255;
            st.Portal = map.EnabledPortal;
            st.FinalKilled = map._crusadeData != null && map._crusadeData.HasKilledFinalBoss;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<CollectableTitanBlood>())
                if (b != null && b.gameObject.activeInHierarchy) { st.BloodOut = true; st.BloodAt = b.transform.position; break; }
            st.Home = map.HomePortalObject != null && map.HomePortalObject.activeInHierarchy;
            var sig = SigilTriggers();
            for (int i = 0; i < sig.Count && i < 8; i++)
                if (sig[i] != null && sig[i]!.State == InteractableTriggerState.Triggered) st.Sigils |= (byte)(1 << i);
        }
        else
        {
            var lobby = Lobby();
            if (lobby?._crusadeData != null) st.Blood = lobby._crusadeData.CurrentTitanBloodAmount;
            if (lobby?.TitanBloodRunes != null)
                for (int i = 0; i < lobby.TitanBloodRunes.Length && i < 8; i++)
                    if (lobby.TitanBloodRunes[i] != null && lobby.TitanBloodRunes[i].RuneActive) st.Runes |= (byte)(1 << i);
            st.Primordial = lobby?.PrimordialPortalObject != null && lobby.PrimordialPortalObject.activeInHierarchy;
        }
        return st;
    }

    private static void HostTick(float dt)
    {
        if (!Active || !GameManagerUtil.HasGameManager) return;
        AssistProxies();
        _stateTimer -= dt; _refreshTimer -= dt;
        if (_stateTimer > 0f) return;
        _stateTimer = 0.25f;
        var st = Read();
        if (st.Equals(_sent) && _refreshTimer > 0f) return;
        _sent = st;
        _refreshTimer = 2f;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeEvent);
        w.Put(KindState);
        w.Put(st.Keys); w.Put(st.KeysTotal); w.Put(st.Blood); w.Put(st.Door); w.Put(st.BossRoom); w.Put(st.BloodTaken);
        w.Put(st.Runes); w.Put(st.Primordial); w.Put(st.Sigils); w.Put(st.Portal); w.Put(st.Home); w.Put(st.FinalKilled);
        w.Put(st.BloodOut); w.Put(st.BloodAt.x); w.Put(st.BloodAt.y); w.Put(st.BloodAt.z);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Host: a client's stand-in at the door or the boss room's entrance works it like a player would (the
    /// game's trigger only takes real players).</summary>
    private static void AssistProxies()
    {
        var lobby = Lobby();
        if (lobby?.TitanBloodRunes != null && lobby._bloodRunesActive == false)
            foreach (var rune in lobby.TitanBloodRunes)
            {
                if (rune == null || rune.RuneActive || !rune.gameObject.activeInHierarchy) continue;
                bool near = false;
                foreach (var rp in CoopSession.Remotes)
                {
                    var proxy = Enemies.RemoteProxy.IsAlive(rp.Slot) ? Enemies.RemoteProxy.ProxyOf(rp.Slot) : null;
                    if (proxy != null && Near(proxy.transform.position, rune.transform.position, 1.6f)) { near = true; break; }
                }
                if (near && !rune.IsBeingInteracted) rune.IsBeingInteracted = true;
            }
        var (door, boss) = Triggers();
        var all = new List<InteractableOneTimeTrigger?> { door, boss };
        all.AddRange(SigilTriggers());
        foreach (var t in all)
        {
            if (t == null || !t.gameObject.activeInHierarchy || t.State == InteractableTriggerState.Triggered) continue;
            var pos = t.transform.position;
            var current = t.InteractingEntity;
            if (current != null)
            {
                if (Enemies.RemoteProxy.TryGetProxySlot(current, out _) && !Near(current.transform.position, pos, 3.5f))
                {
                    t.InteractingEntity = null;
                    t.InteractingEntityTargetTransform = null;
                    t.State = InteractableTriggerState.Idle;
                }
                continue;
            }
            foreach (var rp in CoopSession.Remotes)
            {
                if (!Enemies.RemoteProxy.IsAlive(rp.Slot)) continue;
                var proxy = Enemies.RemoteProxy.ProxyOf(rp.Slot);
                if (proxy == null || !Near(proxy.transform.position, pos, 3f)) continue;
                t.InteractingEntity = proxy;
                t.InteractingEntityTargetTransform = proxy.transform;
                t.State = InteractableTriggerState.BeingTriggered;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: {rp.Name} is working the {(t == door ? "door" : t == boss ? "boss room" : "sigil")}.");
                break;
            }
        }
    }

    private static bool Near(Vector3 a, Vector3 b, float radius) { a.y = b.y = 0f; return (a - b).sqrMagnitude <= radius * radius; }

    private static State _host;
    private static bool _hostKnown;

    private static void ApplyState(NetDataReader r)
    {
        _host = new State { Keys = r.GetInt(), KeysTotal = r.GetInt(), Blood = r.GetInt(), Door = r.GetByte(), BossRoom = r.GetByte(), BloodTaken = r.GetBool() };
        _host.Runes = r.GetByte();
        _host.Primordial = r.GetBool();
        _host.Sigils = r.GetByte();
        _host.Portal = r.GetBool();
        _host.Home = r.GetBool();
        _host.FinalKilled = r.GetBool();
        _host.BloodOut = r.GetBool();
        _host.BloodAt = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        _hostKnown = true;
    }

    /// <summary>Client, every frame: follow the host's progress on this map.</summary>
    private static void ClientTick()
    {
        if (!_hostKnown || !Active || !GameManagerUtil.HasGameManager) return;
        var map = Map();
        if (map == null)
        {
            var lobby = Lobby();
            if (lobby?._crusadeData != null && lobby._crusadeData.CurrentTitanBloodAmount != _host.Blood) lobby._crusadeData.CurrentTitanBloodAmount = _host.Blood;
            if (lobby?.TitanBloodRunes != null)
                for (int i = 0; i < lobby.TitanBloodRunes.Length && i < 8; i++)
                {
                    var rune = lobby.TitanBloodRunes[i];
                    if (rune != null && (_host.Runes & (1 << i)) != 0 && !rune.RuneActive) { rune._currentTimer = rune.TimeToEnter; rune.IsBeingInteracted = true; }
                }
            if (_host.Primordial && lobby?.PrimordialPortalObject != null && !lobby.PrimordialPortalObject.activeSelf)
            {
                lobby.PrimordialPortalObject.SetActive(true);
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Client: the host opened the Primordial Portal.");
            }
            return;
        }
        var k = map.CollectKeyFragmentsObjective;
        if (k != null && _host.KeysTotal > 0 && k.Current != _host.Keys) k.Current = Math.Min(_host.Keys, k.Total);
        var (door, boss) = Triggers();
        if (_host.Door == (byte)InteractableTriggerState.Triggered && door != null && door.State != InteractableTriggerState.Triggered)
            Force(door, "door", UnityEngine.Object.FindObjectOfType<CrusadeDoorObject>());
        if (_host.BossRoom == (byte)InteractableTriggerState.Triggered && boss != null && boss.State != InteractableTriggerState.Triggered
            && (door == null || door.State == InteractableTriggerState.Triggered)) // the door first
        {
            var local = GameState.LocalPlayerEntity;
            if (local != null && !Near(local.transform.position, boss.transform.position, 12f))
            {
                var pos = boss.transform.position + new Vector3(1.5f, 0.3f, 0f);
                var ch = local._character;
                if (ch != null) ch.Teleport(pos); else local.transform.position = pos;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: the host entered the boss room - brought in ({pos}).");
            }
            Force(boss, "boss room", null);
        }
        if (_host.BloodTaken && !map._hasCollectedTitanBlood) CollectBlood(map, "host state");
        MirrorBloodItem();
        var sig = SigilTriggers();
        for (int i = 0; i < sig.Count && i < 8; i++)
        {
            var t = sig[i];
            if (t != null && (_host.Sigils & (1 << i)) != 0 && t.State != InteractableTriggerState.Triggered && t.gameObject.activeInHierarchy) Force(t, "sigil " + i, null);
        }
        if (_host.Portal && !map.EnabledPortal) OpenExitPortal(map);
        if (_host.Home && map.HomePortalObject != null && !map.HomePortalObject.activeInHierarchy)
        {
            if (map.HomePortalHolder != null && !map.HomePortalHolder.activeSelf) map.HomePortalHolder.SetActive(true);
            map.HomePortalObject.SetActive(true);
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Client: the host's Home portal opened here too.");
        }
        if (map._crusadeData != null && map._crusadeData.CurrentTitanBloodAmount < _host.Blood) map._crusadeData.CurrentTitanBloodAmount = _host.Blood;
    }

    /// <summary>Client: the host's exit portal (hunt on) opened: ours too (the game opens it from code that needs
    /// the boss the client never spawned).</summary>
    private static void OpenExitPortal(CrusadeGameManager map)
    {
        map.EnabledPortal = true;
        if (map.TargetPortalTrigger != null) map.TargetPortalTrigger.SetActive(true);
        try { map.TargetPortal?.EnablePortal(); } catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Client: portal: {ex.Message}"); }
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Client: the host's exit portal opened here too.");
    }

    private static float _forcedAt;

    /// <summary>Client: the host worked this trigger; ours completes as if the local player had stood on it.</summary>
    private static void Force(InteractableOneTimeTrigger t, string what, CrusadeDoorObject? door)
    {
        if (Time.unscaledTime - _forcedAt < 0.5f) return;
        _forcedAt = Time.unscaledTime;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        if (!t.gameObject.activeInHierarchy)
        {
            if (door == null) return;
            door.EnableDoorTrigger();
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Client: door trigger enabled from the host's state.");
            return;
        }
        t.InteractingEntity = local;
        t.InteractingEntityTargetTransform = local.transform;
        t.State = InteractableTriggerState.BeingTriggered;
        t.TimeInteracting = Math.Max(t.TimeInteracting, t.InteractionTime);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: {what} worked by the host - ours too.");
    }

    // ---- titan blood: whoever touches it first, everyone gets it ----

    private static bool _bloodFromNet;

    /// <summary>Prefix of CrusadeGameManager.OnTitanBloodCollected. Client: the host decides (and tells everyone).</summary>
    internal static bool AllowBlood(CrusadeGameManager map)
    {
        if (!Active || _bloodFromNet || NetworkManager.Role != NetRole.Client) return !map._hasCollectedTitanBlood || NetworkManager.Role != NetRole.Host;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeEvent);
        w.Put(KindBloodReq);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Client: titan blood touched - asking the host.");
        return false;
    }

    /// <summary>Postfix of OnTitanBloodCollected, host: the clients collect it too.</summary>
    internal static void AfterBlood(CrusadeGameManager map)
    {
        if (NetworkManager.Role != NetRole.Host || !Active) return;
        HideBloodItems();
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeEvent);
        w.Put(KindBlood);
        w.Put(map._crusadeData != null ? map._crusadeData.CurrentTitanBloodAmount : 0);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: titan blood collected ({(map._crusadeData != null ? map._crusadeData.CurrentTitanBloodAmount : -1)}) - clients too.");
    }

    private static void CollectBlood(CrusadeGameManager map, string why)
    {
        // The game's OnTitanBloodCollected throws on a client (it works on the boss the client never spawned): its
        // effects by hand - the flag, the objective, the vial, the exit portal.
        map._hasCollectedTitanBlood = true;
        if (map.CollectBloodObjective != null) map.CollectBloodObjective.Completed = true;
        var data = map._crusadeData;
        if (data != null)
        {
            if (_hostKnown && _host.Blood > data.CurrentTitanBloodAmount) data.CurrentTitanBloodAmount = _host.Blood;
            try { CrusadeGameManager.SaveCrusadeGameData(data); }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Client: saving the run data: {ex.Message}"); }
        }
        HideBloodItems();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: titan blood collected ({why}), vial {(map._crusadeData != null ? map._crusadeData.CurrentTitanBloodAmount : -1)}.");
    }

    /// <summary>Client, Titan Hunt: the host took the Home portal (back to the fortress, the run's end): ours too.
    /// The game's own OnPlayerEnterHomePortal first; if it throws here, its generic part.</summary>
    /// <summary>Client, the host won (its map's game ran the victory branch, OpenPortalAndObjective): the map's
    /// objectives done here too, the final boss flag, and the same victory branch, so the Home portal ends the run as
    /// a victory (not a defeat) on this side.</summary>
    internal static bool ClientVictory()
    {
        var map = Map();
        if (map == null) return false;
        void Done(SimpleObjective? o) { if (o != null) o.Completed = true; }
        void Kill(EliminateSpecificEntitiesObjective? o) { if (o != null) o.Current = Math.Max(o.Total, 1); }
        Done(map.OpenBossRoomObjective); Done(map.EnterBossRoomObjective); Done(map.CollectBloodObjective); Done(map.LightUpSigilObjective);
        Done(map.CollectVoidKingLoot);
        Kill(map.KillLastBossObjective); Kill(map.KillVoidKingSeatedObjective); Kill(map.KillVoidKingObjective); Kill(map.KillVoidKingGiantObjective);
        if (map._crusadeData != null && _hostKnown && _host.FinalKilled) map._crusadeData.HasKilledFinalBoss = true;
        _clientWon = true;
        MarkVictory();
        try { map.StartCoroutine(GameManagerUtil.OpenPortalAndObjective(0f)); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Client: victory branch: {ex.Message}"); }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: the host won - objectives done, final boss {(_hostKnown && _host.FinalKilled)}, victory branch started.");
        return true;
    }

    private static bool _clientWon;

    /// <summary>The end screen reads GameStats.Result: the host's victory is ours.</summary>
    private static void MarkVictory()
    {
        var stats = GameManagerUtil.GameStats;
        if (stats == null) return;
        try { stats.Result = new Il2CppSystem.Nullable<MatchResult>(MatchResult.Victory); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Client: match result: {ex.Message}"); }
    }

    internal static bool EnterHomePortalFromHost()
    {
        if (Global.SelectedGameMode != GameModes.Crusade) return false;
        var map = Map();
        if (map == null) return false;
        Life.CoopLife.FromNetwork = true;
        try
        {
            if (map.HomePortalObject != null && !map.HomePortalObject.activeSelf) map.HomePortalObject.SetActive(true);
            // The host's game made this objective when it opened its portals (code the client doesn't run);
            // ProcessOnPlayerEnterPortal works on it.
            var data = GameManagerUtil.GameData;
            if (data != null && data.EnterPortalObjective == null) data.EnterPortalObjective = new SimpleObjective("EnterPortalObjective");
            if (_clientWon) MarkVictory();
            map.OnPlayerEnterHomePortal();
            if (_clientWon) MarkVictory();
            bool went = data?.EnterPortalObjective != null && data.EnterPortalObjective.Completed;
            if (!went)
            {
                Plugin.StaticLog.LogWarning("[SoulstoneCoop][crusade] Client: the game's Home portal didn't go through - generic end.");
                GameManagerUtil.OnEnterHomePortal(map.HomePortalIdleParticles, map.HomePortalCloseParticles);
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: back to the fortress with the host (Home portal, {(went ? "game's" : "generic")}).");
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Client: Home portal: {ex}"); }
        finally { Life.CoopLife.FromNetwork = false; }
        return true;
    }

    private static GameObject? _bloodCopy;
    private static float _bloodAskAt;

    /// <summary>Client: the titan blood (or the crown) lies on the host's map - its boss died there, not here: a copy
    /// at the same spot, so the guest sees it; walking onto it asks the host (who collects it for everyone).</summary>
    private static void MirrorBloodItem()
    {
        if (!_host.BloodOut)
        {
            if (_bloodCopy != null) { UnityEngine.Object.Destroy(_bloodCopy); _bloodCopy = null; }
            return;
        }
        var local = GameState.LocalPlayerEntity;
        if (_bloodCopy == null)
        {
            CoopSession.Notice(Ui.Loc.Msg("nt.titan_blood_out"), 6f);
            CollectableTitanBlood? src = null;
            foreach (var b in Resources.FindObjectsOfTypeAll<CollectableTitanBlood>()) if (b != null) { src = b; if (b.gameObject.scene.IsValid()) break; }
            if (src != null)
            {
                _bloodCopy = UnityEngine.Object.Instantiate(src.gameObject);
                _bloodCopy.name = "SoulstoneCoop-TitanBlood";
                // only the look: the copy's own pickup would run the client's titan blood code
                foreach (var c in _bloodCopy.GetComponentsInChildren<CollectableTitanBlood>(true)) c.enabled = false;
                foreach (var col in _bloodCopy.GetComponentsInChildren<Collider>(true)) col.enabled = false;
                _bloodCopy.SetActive(true);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: the host's titan blood shown at {_host.BloodAt}.");
            }
            else
            {
                _bloodCopy = new GameObject("SoulstoneCoop-TitanBlood"); // nothing to copy: no look, the walk-on still works
            }
        }
        _bloodCopy.transform.position = _host.BloodAt;
        if (local != null && Near(local.transform.position, _host.BloodAt, 2.5f) && Time.unscaledTime - _bloodAskAt > 1.5f)
        {
            _bloodAskAt = Time.unscaledTime;
            var w = new NetDataWriter();
            w.Put((byte)PacketType.CrusadeEvent);
            w.Put(KindBloodReq);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Client: walked onto the titan blood - asking the host.");
        }
    }

    private static void HideBloodItems()
    {
        foreach (var b in UnityEngine.Object.FindObjectsOfType<CollectableTitanBlood>())
            if (b != null && b.gameObject.activeSelf) b.gameObject.SetActive(false);
    }

    internal static void ResetForScene()
    {
        if (_bloodCopy != null) { UnityEngine.Object.Destroy(_bloodCopy); _bloodCopy = null; }
        _clientWon = false;
        _hostKnown = false;
        _sent = default;
        _stateTimer = _refreshTimer = 0f;
    }

    private static bool _lastCutscene;

    /// <summary>Host: one of the map's presentations / Void King timelines is playing.</summary>
    internal static bool CutscenePlaying()
    {
        if (Global.SelectedGameMode != GameModes.Crusade) return false;
        var map = Map();
        bool on = map != null && map._isPlayingPresentation;
        if (on != _lastCutscene)
        {
            _lastCutscene = on;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: cutscene {(on ? "started - the others wait" : "over")}.");
        }
        return on;
    }

    internal static bool Active => Global.SelectedGameMode == GameModes.Crusade && CoopSession.Connected;

    // ---- host: the titan choice goes to the clients ----

    /// <summary>Prefix of GameManagerUtil.LoadNextScene, host, Titan Hunt: the clients go to the same map.</summary>
    internal static void OnHostLoadNextScene(string mapId)
    {
        if (NetworkManager.Role == NetRole.Client && Active && _goBlood > -2) { try { ApplyGoData(); } catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Client run data: {ex.Message}"); } }
        if (NetworkManager.Role != NetRole.Host || !Active || string.IsNullOrEmpty(mapId)) return;
        RunSync.OnHostStageChanged(mapId);
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeEvent);
        w.Put(KindGo);
        w.Put(mapId);
        bool final = Global.CrusadeIsFinalBossMap || mapId == FinalScene;
        w.Put(final);
        var next = NextData();
        w.Put(next != null ? next.CurrentTitanBloodAmount : -1);
        w.Put(next != null && next.HasUsedPrimordialPortal);
        w.Put(next != null && next.HasKilledFinalBoss);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        PauseSync.ResetForRun();
        CoopSession.BeginStageTransition("titan chosen");
        CoopSession.Notice(Ui.Loc.Msg("nt.titan_going", ("map", mapId)), 5f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: titan chosen - {mapId} (final {final}), clients follow.");
    }

    /// <summary>The run data the next scene starts from: what the portal stored (InitialEndlessData), else this scene's.</summary>
    private static CrusadeData? NextData()
    {
        var init = GameManagerUtil.InitialEndlessData;
        var cd = init != null ? init.TryCast<CrusadeData>() : null;
        return cd ?? Map()?._crusadeData ?? Lobby()?._crusadeData;
    }

    private static int _goBlood = -1;
    private static bool _goPrimordial, _goFinalKilled;

    /// <summary>Client: the run data our next scene starts from gets the host's vial and flags.</summary>
    private static void ApplyGoData()
    {
        var init = GameManagerUtil.InitialEndlessData;
        var cd = init != null ? init.TryCast<CrusadeData>() : null;
        if (cd == null) return;
        if (_goBlood >= 0) cd.CurrentTitanBloodAmount = _goBlood;
        cd.HasUsedPrimordialPortal = _goPrimordial;
        cd.HasKilledFinalBoss = _goFinalKilled;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: next scene's run data: vial {cd.CurrentTitanBloodAmount}, primordial {cd.HasUsedPrimordialPortal}, final boss {cd.HasKilledFinalBoss}, level {cd.CurrentLevel}.");
    }

    /// <summary>Postfix of CrusadeMapSelectionPanel.Open, host: the others see who is choosing.</summary>
    internal static void OnHostPanelOpen()
    {
        if (NetworkManager.Role != NetRole.Host || !Active || _following != null) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeEvent);
        w.Put(KindChoosing);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // ---- client ----

    private static string? _following;    // the host's map we're going to
    private static float _followUntil, _openedAt;
    internal static bool Following => _following != null;

    public static void OnEvent(NetDataReader r, byte from)
    {
        byte kind = r.GetByte();
        if (kind == KindSuggest)
        {
            string mapId = r.GetString();
            if (NetworkManager.Role != NetRole.Host) return;
            string who = CoopSession.Get(from)?.Name ?? $"#{from}";
            CoopSession.Notice(Ui.Loc.Msg("nt.titan_suggested", ("name", who), ("map", Pretty(mapId))), 8f);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: {who} proposes {mapId}.");
            return;
        }
        if (kind == KindPortalReq)
        {
            byte which = r.GetByte();
            if (NetworkManager.Role == NetRole.Host) OnPortalRequest(from, which);
            return;
        }
        if (kind == KindBloodReq)
        {
            if (NetworkManager.Role != NetRole.Host) return;
            var map = Map();
            bool crown = map != null && map._hasCollectedTitanBlood && map.CollectVoidKingLoot != null && !map.CollectVoidKingLoot.Completed;
            if (map == null || (map._hasCollectedTitanBlood && !crown)) return;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: slot {from} touched the titan blood.");
            map.OnTitanBloodCollected();
            HideBloodItems();
            return;
        }
        if (NetworkManager.Role != NetRole.Client) return;
        if (kind == KindState) { ApplyState(r); return; }
        if (kind == KindOptions) { OnOptions(r); return; }
        if (kind == KindBlood)
        {
            int amount = r.GetInt();
            var map = Map();
            if (map != null && !map._hasCollectedTitanBlood) CollectBlood(map, "host");
            if (map?._crusadeData != null && map._crusadeData.CurrentTitanBloodAmount != amount) map._crusadeData.CurrentTitanBloodAmount = amount;
            return;
        }
        if (kind == KindChoosing)
        {
            CoopSession.Notice(Ui.Loc.Msg("nt.titan_choosing"), 6f);
            return;
        }
        if (kind == KindGo)
        {
            string map = r.GetString();
            bool final = r.GetBool();
            _goBlood = r.GetInt();
            _goPrimordial = r.GetBool();
            _goFinalKilled = r.GetBool();
            Global.CrusadeIsFinalBossMap = final;
            _following = map;
            _options = null;
            _followUntil = Time.unscaledTime + 15f;
            _openedAt = 0f;
            CoopSession.BeginStageTransition("following the host's titan");
            CoopSession.Notice(Ui.Loc.Msg("nt.titan_going", ("map", map)), 5f);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: host chose {map} (final {final}) - following.");
        }
    }

    /// <summary>Client: open our own titan choice (the lobby's or this map's), then press the host's map on it.</summary>
    private static void TickFollow()
    {
        if (_following == null) return;
        if (Time.unscaledTime > _followUntil)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Client: the titan choice didn't go through - loading {_following} directly.");
            string map = _following;
            _following = null;
            GameManagerUtil.LoadNextScene(map, 0f, false);
            return;
        }
        if (Lobby() == null)
        {
            string t = _following;
            _following = null;
            LoadFromMap(t);
            return;
        }
        if (_following == FinalScene)
        {
            string t = _following;
            _following = null;
            _opening = true;
            try { Lobby()!.OnPlayerEnterPrimordialPortal(); }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Client: Primordial Portal: {ex.Message}"); }
            finally { _opening = false; }
            _pressedTarget = t; _pressedAt = Time.unscaledTime;
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Client: through the Primordial Portal after the host.");
            return;
        }
        var panel = Panel();
        if (panel == null || !panel.IsOpen)
        {
            if (_openedAt > 0f && Time.unscaledTime - _openedAt < 2f) return;
            _openedAt = Time.unscaledTime;
            _opening = true;
            try
            {
                var lobby = Lobby();
                lobby?.OnPlayerEnterUnstablePortal();
            }
            finally { _opening = false; }
            return;
        }
        if (panel.Items == null || panel.Items.Count == 0 || panel.Items[0] == null || string.IsNullOrEmpty(panel.Items[0].MapId)) return;
        string target = _following;
        _following = null;
        panel.Items[0].MapId = target;
        _pressingForHost = true;
        try { panel.Items[0].OnPressed(); }
        finally { _pressingForHost = false; }
        _pressedTarget = target; _pressedAt = Time.unscaledTime;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: pressed the host's titan ({target}) on our panel.");
    }

    /// <summary>Client, on a map: the game's OnPlayerEnterPortal throws here (it works on the boss the client never
    /// spawned), and a titan choice opened by hand doesn't take presses. What the choice does: the character into the
    /// run data, saved for the next scene, then LoadNextScene.</summary>
    private static void LoadFromMap(string target)
    {
        var old = Map()?._crusadeData ?? Lobby()?._crusadeData;
        var local = GameState.LocalPlayerEntity;
        CrusadeData? next = null;
        if (local != null)
        {
            // What the game's portal stores for the next scene (GameManagerUtil.InitialEndlessData): the character.
            next = GameManagerUtil.SetCrusadeEndlessData(GameManagerUtil.PowerUpData, local);
            if (next != null)
            {
                int blood = Math.Max(Math.Max(old != null ? old.CurrentTitanBloodAmount : 0, _hostKnown ? _host.Blood : 0), _goBlood);
                if (next.CurrentTitanBloodAmount < blood) next.CurrentTitanBloodAmount = blood;
            }
        }
        var panel = Panel();
        if (panel != null && panel.IsOpen) panel.Close();
        if (target == FinalScene) Global.CrusadeIsFinalBossMap = true;
        GameManagerUtil.LoadNextScene(target, 0f, false);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Client: going to {target} from the map (level {(next != null ? next.CurrentLevel : -1)}, vial {(next != null ? next.CurrentTitanBloodAmount : -1)}).");
    }

    private static string? _pressedTarget;
    private static float _pressedAt;
    private static string _sceneAtPress = "";

    /// <summary>Client: the press (or portal) didn't load anything within a few seconds: load it ourselves.</summary>
    private static void TickWatchdog()
    {
        if (_pressedTarget == null) return;
        string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (active == _pressedTarget || GameManagerUtil.GameManagerLoadingScene || !GameManagerUtil.HasGameManager) { _pressedTarget = null; return; }
        if (Time.unscaledTime - _pressedAt < 5f) return;
        string t = _pressedTarget;
        _pressedTarget = null;
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Client: nothing loaded after the press - going to {t} directly.");
        LoadFromMap(t);
    }

    private static bool _opening;
    private static float _lastRefusal;

    /// <summary>Prefix of the portals that open the titan choice: on a client only the host's choice opens it.</summary>
    internal static bool AllowOpenChoice() => AllowPortal(PortalChoice);
    internal static bool AllowPrimordial() => AllowPortal(PortalPrimordial);
    internal static bool AllowGoblin() => AllowPortal(PortalGoblin);

    private static bool AllowPortal(byte which)
    {
        if (NetworkManager.Role != NetRole.Client || !Active || _opening) return true;
        if (Time.unscaledTime - _lastRefusal > 4f && _following == null)
        {
            _lastRefusal = Time.unscaledTime;
            RequestPortal(which);
        }
        return false;
    }

    public static void Tick(float dt)
    {
        if (!GameManagerUtil.HasGameManager && (_hostKnown || _sent.KeysTotal != 0)) ResetForScene();
        if (NetworkManager.Role == NetRole.Host)
        {
            try { HostTick(dt); } catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Host tick: {ex}"); _stateTimer = 5f; }
            return;
        }
        if (NetworkManager.Role != NetRole.Client) return;
        try { TickFollow(); TickWatchdog(); TickOptionsView(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Follow: {ex}"); _following = null; }
        try { ClientTick(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Client tick: {ex}"); _hostKnown = false; }
    }

    internal static CrusadeLobbyGameManager? Lobby() => UnityEngine.Object.FindObjectOfType<CrusadeLobbyGameManager>();
    internal static CrusadeGameManager? Map() => UnityEngine.Object.FindObjectOfType<CrusadeGameManager>();
    internal static CrusadeMapSelectionPanel? Panel()
    {
        foreach (var p in Resources.FindObjectsOfTypeAll<CrusadeMapSelectionPanel>())
            if (p != null && p.gameObject.scene.IsValid()) return p;
        return null;
    }

    internal static string Info()
    {
        var sb = new System.Text.StringBuilder();
        var lp = GameState.LocalPlayerEntity;
        sb.Append($"lvl={(lp?._experience != null ? lp._experience.Level : -1)} ");
        sb.Append($"scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} mode={Global.SelectedGameMode} tier={Global.CrusadeDifficultyTier} level={Global.CrusadeDynamicLevel} final={Global.CrusadeIsFinalBossMap}");
        var lobby = Lobby();
        if (lobby != null)
        {
            var d = lobby._crusadeData;
            sb.Append($" | lobby start={lobby._isGameModeStart} runes={lobby._bloodRunesActive}");
            if (lobby.UnstablePortalTrigger != null) sb.Append($" unstable@{lobby.UnstablePortalTrigger.transform.position:0.0} act={lobby.UnstablePortalTrigger.activeInHierarchy}");
            if (lobby.PrimordialPortalObject != null) sb.Append($" primordial act={lobby.PrimordialPortalObject.activeInHierarchy}");
            if (d != null) sb.Append($" data(tier={d.DifficultyTier} lvl={d.DynamicEndlessLevel} blood={d.CurrentTitanBloodAmount} primordial={d.HasUsedPrimordialPortal} final={d.HasKilledFinalBoss})");
        }
        var map = Map();
        if (map != null)
        {
            var d = map._crusadeData;
            sb.Append($" | map setup={map.IsSetUp} portal={map.EnabledPortal} finalMap={map.FinalBossMap} blood={map._hasCollectedTitanBlood} boss={(map._spawnedBoss != null ? map._spawnedBoss.name : "-")}");
            if (d != null) sb.Append($" data(tier={d.DifficultyTier} lvl={d.DynamicEndlessLevel} blood={d.CurrentTitanBloodAmount})");
            void Obj(string n, SimpleObjective? o) { if (o != null) sb.Append($" {n}={(o.Completed ? "done" : "open")}"); }
            Obj("enter", map.EnterBossRoomObjective); Obj("open", map.OpenBossRoomObjective); Obj("blood", map.CollectBloodObjective);
            var keys = map.CollectKeyFragmentsObjective;
            if (keys != null) sb.Append($" keys={keys.Current}/{keys.Total}");
            var sigs = SigilTriggers();
            if (sigs.Count > 0) sb.Append(" sigils=" + string.Join(",", sigs.Select(x => x == null ? "-" : (x.gameObject.activeInHierarchy ? "" : "off:") + x.State)));
            if (map.LightUpSigilObjective != null) sb.Append($" lightSigil={(map.LightUpSigilObjective.Completed ? "done" : "open")}");
            if (map.HomePortalObject != null) sb.Append($" home@{map.HomePortalObject.transform.position:0.0} act={map.HomePortalObject.activeInHierarchy}");
            if (map.TargetPortalTrigger != null) sb.Append($" portal@{map.TargetPortalTrigger.transform.position:0.0} act={map.TargetPortalTrigger.activeInHierarchy}");
            foreach (var b in UnityEngine.Object.FindObjectsOfType<CollectablePowerUpRequest>())
                if (b != null) sb.Append($" bloodItem({b.GetIl2CppType().Name})@{b.transform.position:0.0} act={b.gameObject.activeInHierarchy}");
            foreach (var door in UnityEngine.Object.FindObjectsOfType<CrusadeDoorObject>())
                if (door.DoorTrigger != null) sb.Append($" door@{door.DoorTrigger.transform.position:0.0} {door.DoorTrigger.State} boss@{(door.BossRoomTrigger != null ? door.BossRoomTrigger.transform.position.ToString("0.0") + " " + door.BossRoomTrigger.State : "-")}");
        }
        var panel = Panel();
        if (panel != null && panel.IsOpen)
        {
            sb.Append(" | panel open:");
            if (panel.Items != null) foreach (var i in panel.Items) if (i != null) sb.Append(' ').Append(i.MapId);
            sb.Append($" requested={panel.RequestedSceneLoadForMapId}");
        }
        return sb.ToString();
    }

    internal static string Test(string[] args)
    {
        string what = args.Length > 0 ? args[0] : "info";
        switch (what)
        {
            case "info": return Info();
            case "portal":
            {
                var lobby = Lobby();
                if (lobby != null) { lobby.OnPlayerEnterUnstablePortal(); return "lobby unstable portal entered"; }
                var map = Map();
                if (map != null) { map.OnPlayerEnterPortal(); return "map portal entered"; }
                return "no crusade manager";
            }
            case "pick":
            {
                var panel = Panel();
                if (panel == null || panel.Items == null) return "no panel";
                int n = args.Length > 1 ? int.Parse(args[1]) : 0;
                if (n >= panel.Items.Count) return "no item " + n;
                panel.Items[n].OnPressed();
                return "pressed " + panel.Items[n].MapId;
            }
            case "keys":
            {
                var o = Map()?.CollectKeyFragmentsObjective;
                var local = GameState.LocalPlayerEntity;
                if (o == null || local == null) return "no keys objective";
                int n = args.Length > 1 ? int.Parse(args[1]) : o.Total - o.Current;
                KeyFragmentProjectile.FireProjectile(local.transform.position + Vector3.right * 3f, local, n);
                return $"fired {n} fragment(s) (now {o.Current}/{o.Total})";
            }
            case "blood":
            {
                int n = args.Length > 1 ? int.Parse(args[1]) : 3;
                var d = Map()?._crusadeData ?? Lobby()?._crusadeData;
                if (d == null) return "no crusade data";
                d.CurrentTitanBloodAmount = n;
                return "blood " + n;
            }
            case "primo":
            {
                var panel = Panel();
                if (panel == null || !panel.IsOpen) return "no open panel";
                panel.OnPressPrimordialTeleport();
                return "primordial teleport pressed";
            }
            case "lobbyprimo":
            {
                var lobby = Lobby();
                if (lobby == null) return "no lobby";
                lobby.OnPlayerEnterPrimordialPortal();
                return "primordial portal entered";
            }
            case "runes":
            {
                var lobby = Lobby();
                if (lobby?.TitanBloodRunes == null) return "no runes";
                var sb2 = new System.Text.StringBuilder($"runesActive={lobby._bloodRunesActive}:");
                foreach (var rn in lobby.TitanBloodRunes) if (rn != null) sb2.Append($" {rn.name}@{rn.transform.position:0.0} act={rn.gameObject.activeInHierarchy} on={rn.RuneActive} in={rn.IsBeingInteracted} t={rn._currentTimer:0.0}/{rn.TimeToEnter:0.0}");
                return sb2.ToString();
            }
            case "goto":
            {
                // stand on a trigger: door / boss / portal / unstable
                var local = GameState.LocalPlayerEntity;
                if (local == null) return "no player";
                Vector3? at = null;
                string t = args.Length > 1 ? args[1] : "door";
                var door = UnityEngine.Object.FindObjectOfType<CrusadeDoorObject>();
                if (t == "door" && door?.DoorTrigger != null) at = door.DoorTrigger.transform.position;
                if (t == "boss" && door?.BossRoomTrigger != null) at = door.BossRoomTrigger.transform.position;
                if (t == "sigil") foreach (var st2 in SigilTriggers()) if (st2 != null && st2.gameObject.activeInHierarchy && st2.State != InteractableTriggerState.Triggered) { at = st2.transform.position; break; }
                if (t == "home" && Map()?.HomePortalObject != null) at = Map()!.HomePortalObject.transform.position;
                if (t == "portal" && Map()?.TargetPortalTrigger != null) at = Map()!.TargetPortalTrigger.transform.position;
                if (t == "blood" && NetworkManager.Role == NetRole.Client && _hostKnown && _host.BloodOut) at = _host.BloodAt;
                if (t == "blood" && at == null) foreach (var b in UnityEngine.Object.FindObjectsOfType<CollectablePowerUpRequest>()) if (b != null && b.gameObject.activeInHierarchy) { at = b.transform.position; break; }
                if (t.StartsWith("rune") && Lobby()?.TitanBloodRunes != null && int.TryParse(t.Substring(4), out var ri) && ri < Lobby()!.TitanBloodRunes.Length) at = Lobby()!.TitanBloodRunes[ri].transform.position;
                if (t == "unstable" && Lobby()?.UnstablePortalTrigger != null) at = Lobby()!.UnstablePortalTrigger.transform.position;
                if (t == "primordial" && Lobby()?.PrimordialPortalObject != null) at = Lobby()!.PrimordialPortalObject.transform.position;
                if (at == null) return "no " + t;
                var ch = local._character;
                if (ch != null) ch.Teleport(at.Value); else local.transform.position = at.Value;
                return $"at {t} {at.Value:0.0}";
            }
        }
        return "crusade info|portal|pick N|keys|goto door|boss|portal|unstable|primordial";
    }
}

[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.LoadNextScene))]
public static class CrusadeLoadNextScenePatch
{
    [HarmonyPrefix]
    public static void Prefix(string mapId)
    {
        try { CrusadeSync.OnHostLoadNextScene(mapId); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] LoadNextScene: {ex}"); }
    }
}

[HarmonyPatch(typeof(CrusadeMapSelectionPanel), nameof(CrusadeMapSelectionPanel.Open))]
public static class CrusadePanelOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { CrusadeSync.OnHostPanelOpen(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Panel open: {ex}"); }
    }
}

[HarmonyPatch(typeof(CrusadeLobbyGameManager), nameof(CrusadeLobbyGameManager.OnPlayerEnterUnstablePortal))]
public static class CrusadeUnstablePortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix() => CrusadeSync.AllowOpenChoice();
}

[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.OnPlayerEnterPortal))]
public static class CrusadeMapPortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix() => CrusadeSync.AllowOpenChoice();
}

[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.OnTitanBloodCollected))]
public static class CrusadeBloodPatch
{
    [HarmonyPrefix]
    public static bool Prefix(CrusadeGameManager __instance)
    {
        try { return CrusadeSync.AllowBlood(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Blood prefix: {ex}"); return true; }
    }

    [HarmonyPostfix]
    public static void Postfix(CrusadeGameManager __instance)
    {
        try { CrusadeSync.AfterBlood(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Blood postfix: {ex}"); }
    }
}

/// <summary>The map's bosses are the host's (puppets here).</summary>
[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.SpawnMapBosses))]
public static class CrusadeClientSpawnBossesPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        if (!CoopRole.IsClientSide) return true;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][spawn] Client: blocked CrusadeGameManager.SpawnMapBosses().");
        return false;
    }
}

[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.SpawnCrusadeBoss))]
public static class CrusadeClientSpawnBossPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref Entity __result)
    {
        if (!CoopRole.IsClientSide) return true;
        __result = null!;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][spawn] Client: blocked CrusadeGameManager.SpawnCrusadeBoss().");
        return false;
    }
}

[HarmonyPatch(typeof(CrusadeLobbyGameManager), nameof(CrusadeLobbyGameManager.OnPlayerEnterPrimordialPortal))]
public static class CrusadePrimordialPortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix() => CrusadeSync.AllowPrimordial();
}

[HarmonyPatch(typeof(CrusadeMapSelectionPanel), nameof(CrusadeMapSelectionPanel.FillOptions))]
public static class CrusadePanelOptionsPatch
{
    [HarmonyPostfix]
    public static void Postfix(CrusadeMapSelectionPanel __instance)
    {
        try { CrusadeSync.OnHostOptions(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Options: {ex}"); }
    }
}

[HarmonyPatch(typeof(CrusadeMapSelectionPanel), nameof(CrusadeMapSelectionPanel.Close))]
public static class CrusadePanelClosePatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { CrusadeSync.OnHostPanelClosed(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Panel close: {ex}"); }
    }
}

[HarmonyPatch(typeof(CrusadeMapSelectionItem), nameof(CrusadeMapSelectionItem.OnPressed))]
public static class CrusadeItemPressPatch
{
    [HarmonyPrefix]
    public static bool Prefix(CrusadeMapSelectionItem __instance)
    {
        try { return CrusadeSync.AllowPress(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Press: {ex}"); return true; }
    }
}

[HarmonyPatch(typeof(CrusadeMapSelectionPanel), nameof(CrusadeMapSelectionPanel.OnRerollPressed))]
public static class CrusadeRerollPatch
{
    [HarmonyPrefix]
    public static bool Prefix() => CrusadeSync.AllowReroll();
}

[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.OnPlayerGoblinCavePortal))]
public static class CrusadeGoblinPortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix() => CrusadeSync.AllowGoblin();
}
