using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Loot;

/// <summary>
/// The map's questline runs on the host only (ClientQuestManagerPatch). Questlines (Resources/Quests, one per map):
///   ValentinesSeasonal (Forest): an altar trigger, then 8 altars broken by the Cupids' arrows. Entities: puppets.
///   CollectEggs (Cavern): a cage trigger, then 50 eggs (CollectableSecretObject, not entities) carried to a receiver.
///   BossRunes (Snow): 5 runes (QuestBossRuneObject, children of the questline); a soulstone of the rune's colour
///   collected within RuneRange activates it.
/// Host: every 0.5 s each guest gets QuestState: the questline's path and pose, the eggs (id, position), the deliveries,
/// that guest's carried eggs and the activated runes. Guests show an inert copy of the questline prefab (the runes)
/// and of the eggs and receiver, and answer with QuestAction: collect egg (after standing on it for its
/// TimeToCollect), deliver (at the receiver) or activate rune (a matching soulstone collected near it). The host
/// keeps each guest's carried eggs apart from its own (the game has one carrier, the host).
/// Rewards: when the host's questline grants its rewards, the guests grant the same ones to their own profile
/// (QuestReward); portal rewards are skipped there, the portal is a world object PortalSync takes them through.
/// </summary>
public static class QuestSync
{
    private const byte ActCollect = 1, ActDeliver = 2, ActRune = 3;
    private const float StateInterval = 0.5f;
    private const float PickupRadius = 1.6f, DeliverRadius = 3.5f;

    internal static int Granted, Skipped;

    // ---------------- host ----------------

    private static readonly Dictionary<IntPtr, ushort> _eggIds = new();
    private static readonly Dictionary<byte, int> _guestCarried = new();
    private static readonly HashSet<IntPtr> _guestEggs = new(); // collected for a guest: not the host's carry
    private static ushort _nextEggId = 1;
    private static Questline? _hostQuestline;
    private static string? _hostPath;
    private static float _stateTimer;

    private static Questline? ActiveQuestline()
    {
        foreach (var qm in UnityEngine.Object.FindObjectsOfType<QuestManager>())
            if (qm != null && qm.ActiveQuestline != null) return qm.ActiveQuestline;
        return null;
    }

    private static T? Action<T>(Questline? ql) where T : QuestAction
    {
        if (ql?.Quests == null) return null;
        foreach (var q in ql.Quests)
        {
            if (q?.QuestActions == null) continue;
            foreach (var a in q.QuestActions)
            {
                var t = a?.TryCast<T>();
                if (t != null) return t;
            }
        }
        return null;
    }

    private static T? Reward<T>(Questline? ql) where T : QuestlineReward
    {
        if (ql?.Rewards == null) return null;
        foreach (var r in ql.Rewards)
        {
            var t = r?.TryCast<T>();
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>Host: enter the questline's portal (a guest asked, PortalSync). False when it is not open.</summary>
    internal static bool EnterHostQuestPortal()
    {
        var reward = Reward<UnlockPortalQuestlineReward>(ActiveQuestline());
        if (reward?.PortalObject == null || !reward.PortalObject.activeInHierarchy) return false;
        reward.OnSpecialPortalEntered();
        return true;
    }

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None || !GameManagerUtil.HasGameManager)
        {
            ClearHost();
            ClearClient();
            return;
        }
        if (NetworkManager.Role == NetRole.Host) TickHost(dt);
        else TickClient(dt);
    }

    private static void ClearHost()
    {
        _eggIds.Clear();
        _guestCarried.Clear();
        _guestEggs.Clear();
        _hostQuestline = null;
        _hostPath = null;
    }

    private static void TickHost(float dt)
    {
        if (!NetworkManager.HasPeer) return;
        _stateTimer -= dt;
        if (_stateTimer > 0f) return;
        _stateTimer = StateInterval;

        var ql = ActiveQuestline();
        if (ql == null) return;
        if (_hostQuestline == null || _hostQuestline.Pointer != ql.Pointer)
        {
            ClearHost();
            _hostQuestline = ql;
            _hostPath = Enemies.EnemyPrefabKeys.QuestlinePath(Enemies.EnemyPrefabKeys.StripClone(ql.name));
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][quest] Host: questline {ql.name} -> {_hostPath ?? "(no path: guests won't see it)"}.");
        }
        if (_hostPath == null) return;

        var eggs = Action<CollectAndDeliverCollectablesOverProgressionQuestAction>(ql);
        var runes = Action<ActivateBossRunesQuestAction>(ql);
        foreach (byte slot in NetworkManager.PeerSlots)
        {
            if (!Lobby.CoopSession.InRun(slot)) continue;
            var w = new NetDataWriter();
            w.Put((byte)PacketType.QuestState);
            w.Put(_hostPath);
            var t = ql.transform;
            w.Put(t.position.x); w.Put(t.position.y); w.Put(t.position.z); w.Put(t.eulerAngles.y);
            WriteEggs(w, eggs, slot);
            WriteRunes(w, runes);
            var portal = Reward<UnlockPortalQuestlineReward>(ql);
            w.Put(portal?.PortalObject != null && portal.PortalObject.activeInHierarchy);
            NetworkManager.SendTo(slot, w, DeliveryMethod.ReliableOrdered);
        }
    }

    private static void WriteEggs(NetDataWriter w, CollectAndDeliverCollectablesOverProgressionQuestAction? cd, byte slot)
    {
        bool on = cd != null && cd.State == QuestState.Ongoing;
        w.Put(on);
        if (!on) return;
        var live = new List<CollectableObject>();
        if (cd!.ActiveCollectables != null)
            foreach (var c in cd.ActiveCollectables)
                if (c != null && !c.HasBeenCollected && c.gameObject.activeInHierarchy) live.Add(c);
        w.Put((ushort)live.Count);
        foreach (var c in live)
        {
            if (!_eggIds.TryGetValue(c.Pointer, out var id)) { id = _nextEggId++; if (_nextEggId == 0) _nextEggId = 1; _eggIds[c.Pointer] = id; }
            var p = c.transform.position;
            w.Put(id); w.Put(p.x); w.Put(p.y); w.Put(p.z);
        }
        w.Put((ushort)cd.QuantityDelivered);
        w.Put((ushort)cd.QuantityToDeliverForCompletion);
        w.Put((byte)(_guestCarried.TryGetValue(slot, out var n) ? n : 0));
        w.Put((byte)Mathf.Clamp(cd.MaxObjectsPlayerCanCarry, 0, 255));
        var rc = cd.Receiver;
        w.Put(rc != null);
        if (rc != null) { var p = rc.transform.position; w.Put(p.x); w.Put(p.y); w.Put(p.z); }
    }

    private static void WriteRunes(NetDataWriter w, ActivateBossRunesQuestAction? br)
    {
        int count = br?.Runes != null ? Mathf.Min(br.Runes.Length, 8) : 0;
        w.Put((byte)count);
        byte mask = 0;
        for (int i = 0; i < count; i++) if (br!.Runes[i] != null && br.Runes[i].Activated) mask |= (byte)(1 << i);
        w.Put(mask);
    }

    /// <summary>Host: a guest's request.</summary>
    public static void OnQuestAction(NetDataReader r)
    {
        byte slot = PacketRouter.From;
        byte kind = r.GetByte();
        ushort arg = r.GetUShort();
        if (NetworkManager.Role != NetRole.Host) return;
        var ql = ActiveQuestline();
        if (ql == null) return;
        if (kind == ActCollect) GuestCollect(Action<CollectAndDeliverCollectablesOverProgressionQuestAction>(ql), slot, arg);
        else if (kind == ActDeliver) GuestDeliver(Action<CollectAndDeliverCollectablesOverProgressionQuestAction>(ql), slot);
        else if (kind == ActRune)
        {
            var br = Action<ActivateBossRunesQuestAction>(ql);
            if (br != null && br.State == QuestState.Ongoing && ActivateRune(br, arg))
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][quest] Host: slot {slot} activated rune {arg}.");
        }
    }

    private static void GuestCollect(CollectAndDeliverCollectablesOverProgressionQuestAction? cd, byte slot, ushort id)
    {
        if (cd == null || cd.State != QuestState.Ongoing || cd.ActiveCollectables == null) return;
        int carried = _guestCarried.TryGetValue(slot, out var n) ? n : 0;
        if (carried >= cd.MaxObjectsPlayerCanCarry) return;
        foreach (var c in cd.ActiveCollectables)
        {
            if (c == null || c.HasBeenCollected || !_eggIds.TryGetValue(c.Pointer, out var cid) || cid != id) continue;
            _guestEggs.Add(c.Pointer);
            _guestCarried[slot] = carried + 1;
            // The game's own pickup (the egg flies to the guest's stand-in and the quest frees its spawn point); the
            // carry it adds to the host is taken back in OnCollectableCollected's postfix.
            var collector = Enemies.RemoteProxy.ProxyOf(slot) ?? GameState.LocalPlayerEntity;
            c.OnCollect(collector!);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][quest] Host: slot {slot} picked egg #{id} (carries {carried + 1}).");
            return;
        }
    }

    /// <summary>Postfix of OnCollectableCollected: an egg picked for a guest doesn't load (or slow) the host.</summary>
    internal static void AfterCollected(CollectAndDeliverCollectablesOverProgressionQuestAction cd, CollectableObject collected)
    {
        if (collected == null || !_guestEggs.Remove(collected.Pointer)) return;
        cd.CurrentCarriedItemsQuantity = Mathf.Max(0, cd.CurrentCarriedItemsQuantity - 1);
        cd.RestorePlayerSpeed();
        if (cd.CurrentCarriedItemsQuantity > 0) cd.DecreasePlayerSpeed();
        cd.UpdateCurrentCarriedItemsText();
    }

    private static void GuestDeliver(CollectAndDeliverCollectablesOverProgressionQuestAction? cd, byte slot)
    {
        if (cd == null || cd.State != QuestState.Ongoing) return;
        int n = _guestCarried.TryGetValue(slot, out var c) ? c : 0;
        if (n <= 0) return;
        _guestCarried[slot] = 0;
        cd.QuantityDelivered += n;
        try { cd.Receiver?.SetVisualFeedback(Mathf.Clamp01((float)cd.QuantityDelivered / Mathf.Max(1, cd.QuantityToDeliverForCompletion))); } catch { }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][quest] Host: slot {slot} delivered {n} eggs ({cd.QuantityDelivered}/{cd.QuantityToDeliverForCompletion}).");
        cd.CheckForCompletion();
    }

    /// <summary>What CheckForRuneActivation does for a soulstone collected near the rune: the rune lights up and the
    /// action's DoUpdate completes the quest once all are on.</summary>
    internal static bool ActivateRune(ActivateBossRunesQuestAction br, int index)
    {
        if (br.Runes == null || index < 0 || index >= br.Runes.Length) return false;
        var rune = br.Runes[index];
        if (rune == null || rune.Activated) return false;
        rune.Activated = true;
        if (rune.RuneActiveVisual != null) rune.RuneActiveVisual.SetActive(true);
        return true;
    }

    /// <summary>Host: Questline.GrantRewards ran (the questline was completed).</summary>
    internal static void AfterHostGrant(Questline ql)
    {
        if (NetworkManager.Role != NetRole.Host || !NetworkManager.HasPeer) return;
        string? path = Enemies.EnemyPrefabKeys.QuestlinePath(Enemies.EnemyPrefabKeys.StripClone(ql.name));
        if (path == null)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][quest] Host: no path for questline '{ql.name}', guests get no reward.");
            return;
        }
        var w = new NetDataWriter();
        w.Put((byte)PacketType.QuestReward);
        w.Put(path);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][quest] Host: questline {path} completed, rewards sent to the guests.");
    }

    // ---------------- client ----------------

    private static string? _copyPath;
    private static GameObject? _copy;
    private static Questline? _copyQuestline;
    private static GameObject? _eggPrefab, _receiverPrefab;
    private static float _eggCollectTime = 1f;
    private static readonly Dictionary<ushort, GameObject> _eggCopies = new();
    private static GameObject? _receiverCopy;
    private static int _carried, _maxCarry, _delivered = -1, _total, _runesOn = -1;
    private static ushort _standingOn;
    private static float _standTimer, _actionCooldown;
    private static readonly HashSet<ushort> _requested = new();
    private static bool _portalOpen;
    private static float _portalTimer;

    private static void ClearClient()
    {
        foreach (var go in _eggCopies.Values) if (go != null) UnityEngine.Object.Destroy(go);
        _eggCopies.Clear();
        if (_receiverCopy != null) UnityEngine.Object.Destroy(_receiverCopy);
        if (_copy != null) UnityEngine.Object.Destroy(_copy);
        _receiverCopy = null;
        _copy = null;
        _copyQuestline = null;
        _copyPath = null;
        _eggPrefab = _receiverPrefab = null;
        _carried = _maxCarry = _total = 0;
        _delivered = _runesOn = -1;
        _requested.Clear();
        _standingOn = 0;
        _portalOpen = false;
        _portalTimer = 0f;
    }

    /// <summary>Instantiated under an inactive holder, its scripts that would act on their own are switched off before
    /// it ever wakes up; what stays is the look (meshes, VFX).</summary>
    private static GameObject Inert(GameObject prefab, Vector3 pos, Quaternion rot, string name)
    {
        var holder = new GameObject("SoulstoneCoop_QuestBuild");
        holder.SetActive(false);
        var go = UnityEngine.Object.Instantiate(prefab, holder.transform);
        go.name = name;
        foreach (var c in go.GetComponentsInChildren<Questline>(true)) c.enabled = false;
        foreach (var c in go.GetComponentsInChildren<CollectableSecretObject>(true)) c.enabled = false;
        foreach (var c in go.GetComponentsInChildren<CollectableSecretObjectReceiver>(true)) c.enabled = false;
        foreach (var c in go.GetComponentsInChildren<CollectableSecretObjectReceiverTrigger>(true)) c.enabled = false;
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;
        go.transform.SetParent(null, false);
        go.transform.SetPositionAndRotation(pos, rot);
        UnityEngine.Object.Destroy(holder);
        go.SetActive(true);
        return go;
    }

    public static void OnQuestState(NetDataReader r)
    {
        if (NetworkManager.Role != NetRole.Client || !GameManagerUtil.HasGameManager) return;
        string path = r.GetString();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        float yaw = r.GetFloat();
        if (path != _copyPath) SetupCopy(path, pos, Quaternion.Euler(0f, yaw, 0f));

        bool eggsOn = r.GetBool();
        var seen = new HashSet<ushort>();
        if (eggsOn)
        {
            int n = r.GetUShort();
            for (int i = 0; i < n; i++)
            {
                ushort id = r.GetUShort();
                var p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                seen.Add(id);
                if (_eggCopies.TryGetValue(id, out var go) && go != null) { go.transform.position = p; continue; }
                if (_eggPrefab != null) _eggCopies[id] = Inert(_eggPrefab, p, Quaternion.identity, "SoulstoneCoop_Egg" + id);
            }
            int delivered = r.GetUShort(), total = r.GetUShort(), carried = r.GetByte();
            _maxCarry = r.GetByte();
            if (r.GetBool())
            {
                var rp = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                if (_receiverCopy == null && _receiverPrefab != null) _receiverCopy = Inert(_receiverPrefab, rp, Quaternion.identity, "SoulstoneCoop_EggReceiver");
                if (_receiverCopy != null) _receiverCopy.transform.position = rp;
            }
            if (carried != _carried && carried > 0) Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.eggs_carried", ("n", carried), ("max", _maxCarry)));
            if (_delivered >= 0 && delivered != _delivered) Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.eggs_delivered", ("n", delivered), ("total", total)));
            _carried = carried; _delivered = delivered; _total = total;
            if (_receiverCopy != null)
                try { _receiverCopy.GetComponent<CollectableSecretObjectReceiver>()?.SetVisualFeedback(Mathf.Clamp01((float)delivered / Mathf.Max(1, total))); } catch { }
        }
        foreach (var id in _eggCopies.Keys.ToList())
        {
            if (seen.Contains(id)) continue;
            if (_eggCopies[id] != null) UnityEngine.Object.Destroy(_eggCopies[id]);
            _eggCopies.Remove(id);
            _requested.Remove(id);
        }
        if (!eggsOn && _receiverCopy != null) { UnityEngine.Object.Destroy(_receiverCopy); _receiverCopy = null; }

        int runeCount = r.GetByte();
        byte mask = r.GetByte();
        var runes = Action<ActivateBossRunesQuestAction>(_copyQuestline)?.Runes;
        int on = 0;
        for (int i = 0; i < runeCount; i++)
        {
            bool active = (mask & (1 << i)) != 0;
            if (active) on++;
            var rune = runes != null && i < runes.Length ? runes[i] : null;
            if (rune == null) continue;
            rune.Activated = active;
            if (rune.RuneActiveVisual != null && rune.RuneActiveVisual.activeSelf != active) rune.RuneActiveVisual.SetActive(active);
        }
        if (runeCount > 0 && _runesOn >= 0 && on > _runesOn) Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.rune_on", ("n", on), ("total", runeCount)));
        if (runeCount > 0) _runesOn = on;

        bool portalOpen = r.AvailableBytes > 0 && r.GetBool();
        var copyPortal = Reward<UnlockPortalQuestlineReward>(_copyQuestline)?.PortalObject;
        if (copyPortal != null && copyPortal.activeSelf != portalOpen) copyPortal.SetActive(portalOpen);
        if (portalOpen && !_portalOpen) Plugin.StaticLog.LogInfo("[SoulstoneCoop][quest] Client: the host's quest portal is open.");
        _portalOpen = portalOpen;
    }

    private static void SetupCopy(string path, Vector3 pos, Quaternion rot)
    {
        ClearClient();
        _copyPath = path;
        Questline? prefab = null;
        try { prefab = ResourcesUtil.Load<Questline>(path); } catch { }
        if (prefab == null)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][quest] Client: questline {path} not loadable.");
            return;
        }
        var cd = Action<CollectAndDeliverCollectablesOverProgressionQuestAction>(prefab);
        if (cd != null)
        {
            _eggPrefab = cd.CollectablePrefab != null ? cd.CollectablePrefab.gameObject : null;
            _receiverPrefab = cd.ReceiverEntity != null ? cd.ReceiverEntity.gameObject : null;
            var egg = cd.CollectablePrefab?.TryCast<CollectableSecretObject>();
            if (egg != null) _eggCollectTime = Mathf.Max(0.2f, egg.TimeToCollect);
        }
        _copy = Inert(prefab.gameObject, pos, rot, "SoulstoneCoop_Questline");
        _copyQuestline = _copy.GetComponent<Questline>();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][quest] Client: showing questline {path} (eggs={_eggPrefab != null}, runes={Action<ActivateBossRunesQuestAction>(_copyQuestline)?.Runes?.Length ?? 0}).");
    }

    private static void TickClient(float dt)
    {
        _actionCooldown -= dt;
        var me = GameState.LocalPlayerEntity;
        if (me == null) return;
        var pos = me.transform.position;
        TickQuestPortal(dt, pos);
        if (_eggCopies.Count + (_receiverCopy != null ? 1 : 0) == 0) return;

        // Pick-up: stand on an egg for its TimeToCollect, like the game's trigger.
        ushort on = 0;
        if (_carried < _maxCarry)
            foreach (var kv in _eggCopies)
            {
                if (kv.Value == null || _requested.Contains(kv.Key)) continue;
                var d = kv.Value.transform.position - pos; d.y = 0f;
                if (d.sqrMagnitude <= PickupRadius * PickupRadius) { on = kv.Key; break; }
            }
        if (on != _standingOn) { _standingOn = on; _standTimer = 0f; }
        if (on != 0)
        {
            _standTimer += dt;
            if (_standTimer >= _eggCollectTime)
            {
                _requested.Add(on);
                SendAction(ActCollect, on);
                _standingOn = 0;
            }
        }

        // Delivery: at the receiver with eggs on us.
        if (_receiverCopy != null && _carried > 0 && _actionCooldown <= 0f)
        {
            var d = _receiverCopy.transform.position - pos; d.y = 0f;
            if (d.sqrMagnitude <= DeliverRadius * DeliverRadius)
            {
                SendAction(ActDeliver, 0);
                _actionCooldown = 1f;
            }
        }
    }

    /// <summary>Standing in the copied quest portal for its TimeToEnter asks the host to take it (PortalSync).</summary>
    private static void TickQuestPortal(float dt, Vector3 pos)
    {
        var portal = _portalOpen ? Reward<UnlockPortalQuestlineReward>(_copyQuestline)?.Portal : null;
        if (portal == null) { _portalTimer = 0f; return; }
        var d = portal.transform.position - pos; d.y = 0f;
        if (d.sqrMagnitude > 2.5f * 2.5f) { _portalTimer = 0f; return; }
        _portalTimer += dt;
        if (_portalTimer < Mathf.Max(0.5f, portal.TimeToEnter)) return;
        _portalTimer = 0f;
        Lobby.PortalSync.AskHostQuestPortal();
    }

    private static void SendAction(byte kind, ushort arg)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.QuestAction);
        w.Put(kind);
        w.Put(arg);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Client: a soulstone of ours was collected. Near a rune of the host's questline that wants its colour,
    /// ask the host to light it (the guest has no questline to do CheckForRuneActivation).</summary>
    internal static void AfterSoulstoneCollected(SoulstoneItem item)
    {
        if (NetworkManager.Role != NetRole.Client || item == null) return;
        var br = Action<ActivateBossRunesQuestAction>(_copyQuestline);
        var me = GameState.LocalPlayerEntity;
        if (br?.Runes == null || me == null) return;
        var pos = me.transform.position;
        var value = item.SoulstoneValue;
        for (int i = 0; i < br.Runes.Length; i++)
        {
            var rune = br.Runes[i];
            if (rune == null || rune.Activated) continue;
            var d = rune.transform.position - pos; d.y = 0f;
            if (d.magnitude > br.RuneRange || !Matches(rune.ExpectedValue, value)) continue;
            SendAction(ActRune, (ushort)i);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][quest] Client: soulstone collected near rune {i} ({rune.name}).");
        }
    }

    /// <summary>The soulstone carries every colour the rune expects.</summary>
    private static bool Matches(PlayerProfileCurrencies? want, PlayerProfileCurrencies? have)
    {
        if (want == null || have == null) return false;
        bool any = false;
        bool Need(float w, float h) { if (w <= 0f) return true; any = true; return h > 0f; }
        bool ok = Need(want.CorruptedSoulstones, have.CorruptedSoulstones) && Need(want.VileSoulstones, have.VileSoulstones)
               && Need(want.WickedSoulstones, have.WickedSoulstones) && Need(want.HatefulSoulstones, have.HatefulSoulstones)
               && Need(want.RogueSoulstones, have.RogueSoulstones);
        return ok && any;
    }

    public static void OnQuestReward(NetDataReader r)
    {
        string path = r.GetString();
        Questline? ql = null;
        try { ql = ResourcesUtil.Load<Questline>(path); } catch { }
        if (ql?.Rewards == null)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][quest] Client: questline {path} not loadable, no reward.");
            return;
        }
        foreach (var reward in ql.Rewards)
        {
            if (reward == null) continue;
            if (reward.TryCast<UnlockPortalQuestlineReward>() != null) { Skipped++; continue; }
            try
            {
                reward.GrantReward();
                Granted++;
                var d = reward.TryCast<GrantDestroyObjectAchievementQuestlineReward>();
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][quest] Client: granted {reward.GetIl2CppType().Name}{(d != null ? $" ({d.EliminatedEntityName} x{d.EliminatedAmount})" : "")} from {path}.");
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogError($"[SoulstoneCoop][quest] Client: reward {reward.GetIl2CppType().Name} of {path}: {ex}");
            }
        }
    }

    internal static QuestBossRuneObject? TestRune(int i)
    {
        var runes = Action<ActivateBossRunesQuestAction>(_copyQuestline)?.Runes;
        return runes != null && i >= 0 && i < runes.Length ? runes[i] : null;
    }

    internal static string TestInfo() =>
        $"portalOpen={_portalOpen} copy={_copyPath ?? "-"} eggs={_eggCopies.Count} receiver={(_receiverCopy != null)} carried={_carried}/{_maxCarry} delivered={_delivered}/{_total} runesOn={_runesOn} " +
        $"hostGuestCarry=[{string.Join(",", _guestCarried.Select(kv => kv.Key + ":" + kv.Value))}]";
}

[HarmonyPatch(typeof(Questline), nameof(Questline.GrantRewards))]
public static class QuestlineGrantRewardsPatch
{
    [HarmonyPostfix]
    public static void Postfix(Questline __instance)
    {
        try { QuestSync.AfterHostGrant(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] QuestlineGrantRewardsPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(CollectAndDeliverCollectablesOverProgressionQuestAction), nameof(CollectAndDeliverCollectablesOverProgressionQuestAction.OnCollectableCollected))]
public static class QuestEggCollectedPatch
{
    [HarmonyPostfix]
    public static void Postfix(CollectAndDeliverCollectablesOverProgressionQuestAction __instance, CollectableObject collectedObject)
    {
        try { QuestSync.AfterCollected(__instance, collectedObject); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] QuestEggCollectedPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(SoulstoneItem), nameof(SoulstoneItem.OnCollect))]
public static class QuestSoulstoneCollectedPatch
{
    [HarmonyPostfix]
    public static void Postfix(SoulstoneItem __instance)
    {
        try { QuestSync.AfterSoulstoneCollected(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] QuestSoulstoneCollectedPatch: {ex}"); }
    }
}
