using ECM2.Characters;
using HarmonyLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 2, client side: the host's enemies as "puppets" - real enemy Entities created through the
/// game's own pipeline (pool + SetEntityBasedOnPrefabData + Entity.Setup, like SpawnEnemyEntity), so hit
/// detection, auto-aim and health bars work, but with the AI off and transform/HP driven by the host.
/// The client never kills a puppet itself: ChangeEntityHealth is clamped to keep it at >= 1 HP, and
/// death is only played when the host says Died. See docs/fase2-notas.md.
/// </summary>
public static class ClientPuppets
{
    private const int MaxCreatesPerFrame = 8;
    private const float TeleportError = 3f;     // u, horizontal
    private const float ArriveDistance = 0.15f;  // u: closer than this = stand still, face the host's yaw
    private const float LeadSeconds = 0.1f;      // MoveToLocation aims slightly ahead; the agent smooths it
    private const float MaxExtrapolation = 0.25f;
    private const float TelemetryInterval = 5f;

    public sealed class Puppet
    {
        public ushort NetId;
        public Entity Entity = null!;
        public IntPtr Ptr;
        public ModifiedCharacter? Character;
        public bool UsePathFollowing;
        public string Key = "";
        public float HostMaxHealth;

        public ushort LastSeq;
        public bool HasSample;
        public Vector3 PrevPos, LatestPos;
        public float PrevTime, LatestTime;
        public float Yaw;
        public float HealthPercent = 1f;
        public bool HealthDirty = true;

        public bool Dead;        // host said Died; playing the local death, local dissolve recycles it
        public bool Despawning;  // we called Recycle ourselves
    }

    private static readonly Dictionary<ushort, Puppet> ByNetId = new();
    private static readonly Dictionary<IntPtr, Puppet> ByPtr = new();
    private static readonly List<EnemySpawnMsg> Pending = new();
    private static readonly Dictionary<string, int> Unresolved = new();
    private static readonly HashSet<string> LoggedKeys = new();
    private static bool _hooked;

    /// <summary>Set while we apply the host's death ourselves, so the HP clamp lets it through.</summary>
    internal static bool ApplyingNetworkDeath;

    // Telemetry window
    private static float _telemetryTimer;
    private static int _snapshotsRecv, _slicesRecv, _snapshotBytes, _unknownNetIds, _teleports, _localDeaths, _clampedHits;
    private static double _errSum;
    private static int _errCount;
    private static float _errMax;
    private static ushort _lastSnapshotSeqSeen;

    // Phase 3a: damage dealt to puppets, summed per netId and flushed to the host at 15 Hz.
    private const float DamageFlushInterval = 1f / 15f;
    private const int MaxDamageEntriesPerPacket = 200; // 1 + 1 + 200 * 6 B
    private static readonly Dictionary<ushort, float> DamageOut = new();
    private static float _damageFlushTimer;
    private static int _dmgHits, _dmgPackets, _dmgSkippedEnemyAttacker;
    private static double _dmgSum;

    public static int AliveCount
    {
        get { int n = 0; foreach (var p in ByNetId.Values) if (!p.Dead) n++; return n; }
    }

    internal static bool IsPuppet(Entity e) => ByPtr.ContainsKey(e.Pointer);

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerDisconnected += () =>
        {
            if (CoopRole.IsClientSide) DespawnAll("peer disconnected");
        };
    }

    /// <summary>The client connects (GameplayUIManager.Setup) before its own StartMatch runs, so spawns can
    /// arrive first. They wait in Pending until the local match has started, so puppets get the match's
    /// scaling. Cleared whenever there's no game manager (menu between runs).</summary>
    private static bool _matchStarted;
    internal static bool MatchStarted => _matchStarted;

    internal static Entity? TryGetLivePuppet(ushort netId) =>
        ByNetId.TryGetValue(netId, out var p) && !p.Dead && p.Entity != null ? p.Entity : null;

    public static void OnMatchStarted(string mapId)
    {
        if (!CoopRole.IsClientSide) return;
        _matchStarted = true;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][puppets] StartMatch({mapId}): creating puppets now ({Pending.Count} pending, {ByNetId.Count} live).");
    }

    private static void DespawnAll(string reason)
    {
        int n = 0;
        foreach (var p in ByNetId.Values.ToArray())
        {
            if (p.Dead) continue;
            DespawnPuppet(p);
            n++;
        }
        ByNetId.Clear();
        ByPtr.Clear();
        Pending.Clear();
        DamageOut.Clear();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][puppets] Despawned {n} puppets ({reason}).");
    }

    // ---------------- messages ----------------

    public static void OnSpawn(NetDataReader reader)
    {
        var msg = EnemySpawnMsg.Read(reader);
        if (!CoopRole.IsClientSide) return;
        if (ByNetId.TryGetValue(msg.NetId, out var existing))
        {
            // Resync after a reconnect, or a netId reused after wrap: replace.
            if (existing.Key == msg.Key && !existing.Dead) return;
            RemoveTracking(existing);
            if (!existing.Dead) DespawnPuppet(existing);
        }
        Pending.RemoveAll(m => m.NetId == msg.NetId);
        Pending.Add(msg);
    }

    public static void OnDied(NetDataReader reader)
    {
        ushort netId = reader.GetUShort();
        if (!CoopRole.IsClientSide) return;
        if (Pending.RemoveAll(m => m.NetId == netId) > 0) return;
        if (!ByNetId.TryGetValue(netId, out var p) || p.Dead) return;
        KillPuppet(p);
    }

    public static void OnDespawn(NetDataReader reader)
    {
        ushort netId = reader.GetUShort();
        if (!CoopRole.IsClientSide) return;
        if (Pending.RemoveAll(m => m.NetId == netId) > 0) return;
        if (!ByNetId.TryGetValue(netId, out var p)) return;
        RemoveTracking(p);
        // A dying puppet is recycled by its own dissolve; recycling it now would cut the death short.
        if (!p.Dead) DespawnPuppet(p);
    }

    public static void OnSnapshot(NetDataReader reader, int packetBytes)
    {
        ushort seq = reader.GetUShort();
        var basePos = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        int count = reader.GetByte();
        if (!CoopRole.IsClientSide) return;

        _slicesRecv++;
        _snapshotBytes += packetBytes;
        if (seq != _lastSnapshotSeqSeen) { _snapshotsRecv++; _lastSnapshotSeqSeen = seq; }

        float now = Time.unscaledTime;
        for (int i = 0; i < count; i++)
        {
            EnemySnapshotFormat.ReadEntry(reader, basePos, out ushort netId, out var pos, out float yaw, out float hp);
            if (!ByNetId.TryGetValue(netId, out var p))
            {
                bool pending = false;
                for (int k = 0; k < Pending.Count; k++) if (Pending[k].NetId == netId) { pending = true; break; }
                if (!pending) _unknownNetIds++;
                continue;
            }
            if (p.HasSample && !SeqNewer(seq, p.LastSeq)) continue;
            p.LastSeq = seq;

            if (p.HasSample) { p.PrevPos = p.LatestPos; p.PrevTime = p.LatestTime; }
            else { p.PrevPos = pos; p.PrevTime = now; p.HasSample = true; }
            p.LatestPos = pos;
            p.LatestTime = now;
            p.Yaw = yaw;
            if (Mathf.Abs(hp - p.HealthPercent) > 0.0001f) { p.HealthPercent = hp; p.HealthDirty = true; }
        }
    }

    private static bool SeqNewer(ushort a, ushort b) => (short)(a - b) > 0;

    // ---------------- per frame ----------------

    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsClientSide) return;

        ProcessPending();

        _damageFlushTimer += unscaledDeltaTime;
        if (_damageFlushTimer >= DamageFlushInterval)
        {
            _damageFlushTimer = 0f;
            FlushDamage();
        }

        bool paused = Time.timeScale < 0.0001f;
        float now = Time.unscaledTime;

        foreach (var p in ByNetId.Values.ToArray())
        {
            try
            {
                var e = p.Entity;
                if (e == null || !e.gameObject.activeInHierarchy)
                {
                    RemoveTracking(p);
                    continue;
                }
                if (p.Dead) continue;

                ApplyHealth(p);
                if (!p.HasSample || paused) continue;
                DriveMovement(p, now);
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] Tick puppet #{p.NetId} ({p.Key}): {ex.Message}");
                RemoveTracking(p);
            }
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer >= TelemetryInterval)
        {
            float secs = _telemetryTimer;
            _telemetryTimer = 0f;
            if (ByNetId.Count > 0 || Pending.Count > 0 || _slicesRecv > 0)
            {
                string unresolved = Unresolved.Count == 0 ? "-" : string.Join(",", Unresolved.Select(kv => $"{kv.Key}x{kv.Value}"));
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][puppets] alive={AliveCount} dying={ByNetId.Count - AliveCount} pending={Pending.Count} " +
                    $"snapshots={_snapshotsRecv / secs:F1}/s bytesPerSnapshot={(_snapshotsRecv > 0 ? _snapshotBytes / _snapshotsRecv : 0)} " +
                    $"avgErr={(_errCount > 0 ? _errSum / _errCount : 0):F2}u maxErr={_errMax:F2}u teleports={_teleports} " +
                    $"unknownNetIds={_unknownNetIds} clampedHits={_clampedHits} localDeaths={_localDeaths} unresolved={unresolved}");
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][damage] Client: hits={_dmgHits} damage={_dmgSum:F0} packets={_dmgPackets} skippedEnemyAttacker={_dmgSkippedEnemyAttacker}");
            }
            _snapshotsRecv = _slicesRecv = _snapshotBytes = _unknownNetIds = _teleports = _clampedHits = 0;
            _dmgHits = _dmgPackets = _dmgSkippedEnemyAttacker = 0;
            _dmgSum = 0;
            _errSum = 0; _errCount = 0; _errMax = 0f;
        }
    }

    private static void ApplyHealth(Puppet p)
    {
        if (!p.HealthDirty) return;
        var health = p.Entity._health;
        var stats = p.Entity._stats;
        if (health == null || stats == null || health.HasBeenKilled) return;
        float max = stats.GetMaxHealth();
        // Never write 0: only the host's Died message kills a puppet.
        health.CurrentHealth = Mathf.Max(1f, p.HealthPercent * max);
        p.HealthDirty = false;
    }

    private static void DriveMovement(Puppet p, float now)
    {
        var tr = p.Entity.transform;
        Vector3 actual = tr.position;

        Vector3 vel = Vector3.zero;
        float dt = p.LatestTime - p.PrevTime;
        if (dt > 0.001f && dt < 0.5f) vel = (p.LatestPos - p.PrevPos) / dt;

        float age = Mathf.Clamp(now - p.LatestTime, 0f, MaxExtrapolation);
        Vector3 hostNow = p.LatestPos + vel * age;
        Vector3 target = p.LatestPos + vel * Mathf.Clamp(age + LeadSeconds, 0f, MaxExtrapolation + LeadSeconds);

        float err = Horizontal(actual - hostNow);
        _errSum += err; _errCount++;
        if (err > _errMax) _errMax = err;

        var ch = p.Character;
        if (ch == null) { tr.position = hostNow; return; }

        if (err > TeleportError)
        {
            ch.Teleport(hostNow);
            _teleports++;
            return;
        }

        if (Horizontal(target - actual) > ArriveDistance)
        {
            if (p.UsePathFollowing) ch.MoveToLocation(target);
            else ch.SetPosition(Vector3.MoveTowards(actual, target, Mathf.Max(vel.magnitude, 1f) * Time.deltaTime * 1.5f));
        }
        else
        {
            if (p.UsePathFollowing) ch.StopMovement();
            float rad = p.Yaw * Mathf.Deg2Rad;
            ch.RotateTowards(new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)), true, true);
        }
    }

    private static float Horizontal(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);

    // ---------------- create / kill / despawn ----------------

    private static void ProcessPending()
    {
        if (!GameManagerUtil.HasGameManager) { _matchStarted = false; return; }
        if (Pending.Count == 0) return;
        if (!_matchStarted || GameState.LocalPlayerEntity == null || !EnemyPrefabKeys.ListsReady()) return;

        int created = 0;
        while (Pending.Count > 0 && created < MaxCreatesPerFrame)
        {
            var msg = Pending[0];
            Pending.RemoveAt(0);
            if (TryCreatePuppet(msg)) created++;
        }
    }

    private static bool TryCreatePuppet(EnemySpawnMsg msg)
    {
        EnemyPrefabKeys.Resolved? res;
        try { res = EnemyPrefabKeys.ClientResolve(msg.Key); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] Resolving '{msg.Key}': {ex.Message}");
            res = null;
        }
        if (res == null)
        {
            Unresolved.TryGetValue(msg.Key, out int n);
            Unresolved[msg.Key] = n + 1;
            if (n == 0) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] Unresolved prefab key '{msg.Key}' (netId {msg.NetId}) - skipped.");
            return false;
        }

        Entity? inst = null;
        ClientSpawnBlock.NetworkSpawnInProgress = true;
        try
        {
            var rot = Quaternion.Euler(0f, msg.Yaw, 0f);
            ResourcesUtil.GetPrefabInstance<Entity>(res.Prefab, msg.Position, rot, out inst);
            if (inst == null) throw new Exception("GetPrefabInstance returned null");

            bool fromTierLists = res.PrefabData != null && msg.Tier >= 0;
            if (fromTierLists)
            {
                var dpt = res.PrefabData!.DataPerTier;
                if (dpt != null && msg.Tier < dpt.Length)
                {
                    var data = new EntityByEnemyPrefabData { Tier = msg.Tier, Prefab = res.Prefab, PrefabData = res.PrefabData };
                    UnitUtil.SetEntityBasedOnPrefabData(inst, data);
                }
                UnitUtil.ApplyGenericEnemyScaling(inst, msg.IsElite, true, true, true);
                UnitUtil.ApplyGenericGameModeEnemyScaling(inst);
            }

            // Before Setup: RegisterEntity reads these.
            inst.CanBeRecycledByDistance = false;
            inst.IgnoredByUnitLimit = true;

            inst.Setup();
            DisableAI(inst);

            var p = new Puppet
            {
                NetId = msg.NetId,
                Entity = inst,
                Ptr = inst.Pointer,
                Key = msg.Key,
                HostMaxHealth = msg.MaxHealth,
                Character = inst._character,
                HealthPercent = msg.HealthPercent,
                Yaw = msg.Yaw,
            };
            p.UsePathFollowing = p.Character != null && p.Character._hasPathFollowingBehaviour;
            ByNetId[p.NetId] = p;
            ByPtr[p.Ptr] = p;
            ApplyHealth(p);

            RequestHealthBar(inst, msg.IsElite);

            if (LoggedKeys.Add(msg.Key))
            {
                float localMax = inst._stats != null ? inst._stats.GetMaxHealth() : 0f;
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][puppets] First '{msg.Key}' -> {inst.gameObject.name} tier={msg.Tier} elite={msg.IsElite} " +
                    $"prefabData={(res.PrefabData != null)} pathFollowing={p.UsePathFollowing} hostMaxHP={msg.MaxHealth:F0} localMaxHP={localMax:F0}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] Creating puppet #{msg.NetId} '{msg.Key}': {ex}");
            if (inst != null)
            {
                try { inst.Recycle(); } catch { /* best effort */ }
            }
            return false;
        }
        finally
        {
            ClientSpawnBlock.NetworkSpawnInProgress = false;
        }
    }

    private static void DisableAI(Entity e)
    {
        // OnDisable removes them from ComponentList.Instances, so UpdateAll skips them. Disabling doesn't stop
        // coroutines, hence StopRefreshingTarget (fase0 patch #12). PlayerSeekerAIComponent derives from BasicAI.
        var ai = e.GetComponent<BasicAIComponent>();
        if (ai != null)
        {
            ai.StopRefreshingTarget();
            ai.enabled = false;
        }
        var staticAi = e.GetComponent<StaticAIComponent>();
        if (staticAi != null) staticAi.enabled = false;
    }

    private static void RequestHealthBar(Entity e, bool isElite)
    {
        try
        {
            var team = e._team != null ? e._team.Team : default;
            OverheadHealthBarPanelManager.RequestHealthBar(
                UnitUtil.GetTargetTransform(e, PlaceholderType.Overhead), Vector3.zero,
                e.Health, e.AbnormalStatus, e.BuffStatus, e.Ragdoll, e.Stunnable, team, isElite);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] RequestHealthBar failed: {ex.Message}");
        }
    }

    /// <summary>Plays the death through the game's own funnel (sounds, ragdoll, death anim, dissolve,
    /// then the game recycles it). No loot (yieldLoot=false) and no on-kill procs (attacker list cleared):
    /// kill credit is phase 4.</summary>
    private static void KillPuppet(Puppet p)
    {
        p.Dead = true;
        var e = p.Entity;
        var health = e != null ? e._health : null;
        if (health == null) { RemoveTracking(p); return; }
        try
        {
            health.AttackerList?.Clear();
            health.InvulnerabilityTime = 0f;
            ApplyingNetworkDeath = true;
            UnitUtil.ChangeEntityHealth(null!, e, -(health.CurrentHealth + 1f), false, out _, out _, out _);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] KillPuppet #{p.NetId}: {ex.Message}");
        }
        finally
        {
            ApplyingNetworkDeath = false;
        }

        if (!health.HasBeenKilled)
        {
            // Shield/immunity ate it: just remove the puppet.
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] #{p.NetId} survived the network death - recycling instead.");
            RemoveTracking(p);
            DespawnPuppet(p);
        }
    }

    private static void DespawnPuppet(Puppet p)
    {
        var e = p.Entity;
        if (e == null) return;
        try
        {
            p.Despawning = true;
            // Same as the game's distance recycler: Entity.Recycle marks it killed+instant-dissolve, and
            // HealthComponent.UpdateAll returns it to the pool.
            e.Recycle();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] Despawn #{p.NetId}: {ex.Message}");
        }
    }

    private static void RemoveTracking(Puppet p)
    {
        if (ByNetId.TryGetValue(p.NetId, out var cur) && cur == p) ByNetId.Remove(p.NetId);
        ByPtr.Remove(p.Ptr);
    }

    /// <summary>Entity.Recycle prefix: the pooled instance may come back as a different puppet, so drop it now.</summary>
    internal static void OnEntityRecycle(Entity e)
    {
        if (!ByPtr.TryGetValue(e.Pointer, out var p)) return;
        if (!p.Dead && !p.Despawning)
        {
            _localDeaths++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] #{p.NetId} ({p.Key}) recycled locally without the host saying so.");
        }
        RemoveTracking(p);
    }

    internal static void CountClampedHit() => _clampedHits++;

    /// <summary>ChangeEntityHealth prefix, BEFORE the clamp: the full (already calculated) damage goes to the host.
    /// Hits from enemy-team attackers (puppets hitting each other, the map's God-Entity) are skipped: the host
    /// simulates those on its own enemies already.</summary>
    internal static void RecordDamage(Entity? attacker, Entity target, float healthChange)
    {
        if (healthChange >= 0f || !ByPtr.TryGetValue(target.Pointer, out var p) || p.Dead) return;
        if (attacker != null && ClientSpawnBlock.IsEnemyTeam(attacker)) { _dmgSkippedEnemyAttacker++; return; }
        DamageOut.TryGetValue(p.NetId, out float sum);
        DamageOut[p.NetId] = sum - healthChange;
        _dmgHits++;
        _dmgSum -= healthChange;
    }

    /// <summary>Layout: type u8 | count u8 | count * (netId u16 | damage f32). Damage is positive.</summary>
    private static void FlushDamage()
    {
        if (DamageOut.Count == 0) return;
        if (NetworkManager.RemotePeer == null) { DamageOut.Clear(); return; }

        var entries = DamageOut.ToArray();
        DamageOut.Clear();
        for (int start = 0; start < entries.Length; start += MaxDamageEntriesPerPacket)
        {
            int count = Math.Min(MaxDamageEntriesPerPacket, entries.Length - start);
            var w = new NetDataWriter();
            w.Put((byte)PacketType.ClientDamage);
            w.Put((byte)count);
            for (int k = 0; k < count; k++)
            {
                w.Put(entries[start + k].Key);
                w.Put(entries[start + k].Value);
            }
            NetworkManager.Send(w, LiteNetLib.DeliveryMethod.ReliableUnordered);
            _dmgPackets++;
        }
    }
}

// ---------------- Harmony patches ----------------

[HarmonyPatch(typeof(Entity), nameof(Entity.Recycle))]
public static class EnemySyncEntityRecyclePatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity __instance)
    {
        try
        {
            if (__instance == null) return;
            if (CoopRole.IsHostSide) HostEnemySync.OnEntityRecycle(__instance);
            else if (CoopRole.IsClientSide) ClientPuppets.OnEntityRecycle(__instance);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] EnemySyncEntityRecyclePatch: {ex}");
        }
    }
}

/// <summary>Client: a puppet can't die from local damage. The full damage is forwarded to the host (phase 3a),
/// which kills its real enemy and sends Died back. Locally the hit still runs - damage number, flash, sound -
/// but HP stays >= 1.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class ClientPuppetHealthClampPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity attackerEntity, Entity targetEntity, ref float healthChange)
    {
        try
        {
            if (healthChange >= 0f || ClientPuppets.ApplyingNetworkDeath || !CoopRole.IsClientSide) return;
            if (targetEntity == null || !ClientPuppets.IsPuppet(targetEntity)) return;
            ClientPuppets.RecordDamage(attackerEntity, targetEntity, healthChange);
            var health = targetEntity._health;
            if (health == null) return;
            float floor = 1f - health.CurrentHealth; // most negative change that still leaves 1 HP
            if (healthChange < floor)
            {
                healthChange = Mathf.Min(0f, floor);
                ClientPuppets.CountClampedHit();
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientPuppetHealthClampPatch: {ex}");
        }
    }
}
