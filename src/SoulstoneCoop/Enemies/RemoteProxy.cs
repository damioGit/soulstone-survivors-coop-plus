using ECM2.Characters;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 3b, host side: an invisible allied stand-in for the client's player, so the host's enemies chase and
/// attack the client. It's a "Creeps/Dummy" (the CheatManager.SpawnDummy recipe) with Team = Players - NOT a
/// player: no Inventory, so IsPlayer() stays false. It goes through the game's pipeline (pool + Entity.Setup),
/// is moved to the remote ghost's position every frame, has its renderers off (the ghost is the visual) and
/// never takes damage: hits on it are forwarded to the client as DamageToClient. See docs/fase3-notas.md.
/// </summary>
public static class RemoteProxy
{
    private const string ProxyPrefabPath = "Creeps/Dummy";
    private const float RecycleRadiusSqr = 35f * 35f; // EntityRegistry.DefaultContinuousRecycler
    private const float TelemetryInterval = 5f;

    private static Entity? _proxy;
    private static IntPtr _proxyPtr;
    private static ModifiedCharacter? _proxyCharacter;
    private static Team _originalTeam;
    private static VfxReferences? _originalVfxOnSetup;
    private static bool _matchStarted;
    private static bool _hooked;
    private static float _retryTimer;
    private static int _createFailures;

    // Telemetry window
    private static float _telemetryTimer;
    private static int _proxyHits, _hostHits, _spawnRedirects, _spawnCalls, _recyclerProtected, _seekerRetargets;
    private static double _proxyDamage, _hostDamage;
    private static float _maxHit;
    private static string _maxHitBy = "-";

    public static bool Active => _proxy != null;
    /// <summary>Proxy exists and the client's player is alive: only then is it a target / spawn / recycle reference.</summary>
    private static bool Targetable => _proxy != null && _remoteState.HasValue && !_remoteState.Value.Dead;

    /// <summary>Phase 4B: the client's player exists on our side (proxy up) and isn't dead.</summary>
    internal static bool RemoteAlive => Targetable;

    /// <summary>Phase 4A: the client said it's down (or dead). ClientState.dead covers both.</summary>
    internal static bool RemoteDownOrDead => _remoteState.HasValue && _remoteState.Value.Dead;

    // ---------------- client state (ClientState packet) ----------------

    public struct RemotePlayerState
    {
        public bool Dead;
        public float Health, MaxHealth, Armor, DamageReduction, BlockChance;
        // pendências: status resistance (StatsComponent.AbnormalStatusDampening / Factor). The game has no dodge stat
        // (only the dash dodge, a timing thing) and no elemental resistances: these are the remaining defensive numbers.
        public bool HasDampening;
        public float StatusDampening, StatusDampeningFactor;
    }

    private static RemotePlayerState? _remoteState;
    private static bool _appliedDead;

    /// <summary>Client -> host, every 0.5 s: alive/dead plus the defensive numbers the host's damage math reads
    /// from the TARGET (the proxy). Layout: type u8 | dead u8 | hp, maxHp, armor, damageReduction, block f32
    /// | [statusDampening, statusDampeningFactor f32] (optional tail, pendências).</summary>
    public static void OnClientState(NetDataReader r)
    {
        var s = new RemotePlayerState
        {
            Dead = r.GetBool(),
            Health = r.GetFloat(),
            MaxHealth = r.GetFloat(),
            Armor = r.GetFloat(),
            DamageReduction = r.GetFloat(),
            BlockChance = r.GetFloat(),
        };
        if (r.AvailableBytes >= 8)
        {
            s.HasDampening = true;
            s.StatusDampening = r.GetFloat();
            s.StatusDampeningFactor = r.GetFloat();
        }
        if (!CoopRole.IsHostSide) return;
        bool changed = !_remoteState.HasValue || _remoteState.Value.Dead != s.Dead;
        _remoteState = s;
        if (changed) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Client player is now {(s.Dead ? "DEAD" : "alive")} (hp={s.Health:F0}/{s.MaxHealth:F0}).");
        try { if (_proxy != null) ApplyRemoteState(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][proxy] ApplyRemoteState: {ex}"); }
    }

    private static void ApplyRemoteState()
    {
        if (_proxy == null || !_remoteState.HasValue) return;
        var s = _remoteState.Value;

        var st = _proxy._stats;
        if (st != null && st.Stats != null && s.MaxHealth > 0f)
        {
            st.Stats.ArmorModifier = s.Armor;
            st.Stats.DamageReduction = s.DamageReduction;
            st.Stats.BlockChance = s.BlockChance;
            if (s.HasDampening && !float.IsNaN(s.StatusDampening) && !float.IsNaN(s.StatusDampeningFactor))
            {
                // The proxy's statuses scale the DoT damage the host forwards (DamageToClient): resist like the client.
                st.AbnormalStatusDampening = s.StatusDampening;
                st.AbnormalStatusDampeningFactor = s.StatusDampeningFactor;
            }
            // Percent-of-max-HP skills read the target's max health (the Dummy has 100M -> 1.67M hits).
            float cur = st.GetMaxHealth();
            if (cur > 0f && Mathf.Abs(cur - s.MaxHealth) > 0.5f) st.Stats.MaxHealth *= s.MaxHealth / cur;
        }
        var h = _proxy._health;
        if (h != null && !h.HasBeenKilled) h.CurrentHealth = Mathf.Max(1f, s.Health); // never 0: the proxy can't die

        if (s.Dead == _appliedDead) return;
        _appliedDead = s.Dead;
        // Dead client: out of every target search, no body to bump into, and enemies drop it as target.
        _proxy.IgnoredByEntitySearchQueries = s.Dead;
        var col = _proxy._physical != null ? _proxy._physical.Collider : null;
        if (col != null) col.enabled = !s.Dead;
        if (s.Dead) DropAsTarget();
    }

    private static void DropAsTarget()
    {
        var all = EntityRegistry.AllEntities;
        if (all == null || _proxy == null) return;
        int dropped = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var ai = all[i] != null ? all[i]._basicAI : null;
            if (ai == null) continue;
            var t = ai._entityToFollow;
            if (t == null || t.Pointer != _proxyPtr) continue;
            ai.RemoveTarget(); // the next RefreshTarget picks the closest valid target again
            dropped++;
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Client dead: {dropped} enemies dropped the proxy as target.");
    }

    // ---------------- loot: the proxy must not look like a player there ----------------

    private static bool _lootTeamSwapped;

    /// <summary>LootUtil.ProcessLoot walks AIUtil.GetAllEntitiesInSpecificTeam(Players) and reads each one's
    /// Inventory without a null check: the Inventory-less proxy made every kill throw there. So while loot is
    /// processed the proxy is in the Summons team (enemies target Allies = Players|Summons anyway).</summary>
    internal static void BeforeLoot()
    {
        if (_proxy == null || _lootTeamSwapped) return;
        var team = _proxy._team;
        if (team == null) return;
        team.Team = Team.Summons;
        _lootTeamSwapped = true;
    }

    internal static void AfterLoot()
    {
        if (!_lootTeamSwapped) return;
        _lootTeamSwapped = false;
        var team = _proxy != null ? _proxy._team : null;
        if (team != null) team.Team = Team.Players;
    }
    internal static bool IsProxy(Entity e) => _proxyPtr != IntPtr.Zero && e.Pointer == _proxyPtr;
    internal static Entity? ProxyEntity => _proxy; // pendências: ProxyStatusForward caches its components

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerDisconnected += () =>
        {
            if (!CoopRole.IsHostSide) return;
            RemoveProxy("peer disconnected");
            _remoteState = null;
        };
    }

    public static void OnMatchStarted()
    {
        if (!CoopRole.IsHostSide) return;
        // A new match: any proxy from the previous scene is gone with it.
        _proxy = null;
        _proxyPtr = IntPtr.Zero;
        _proxyCharacter = null;
        _matchStarted = true;
        _createFailures = 0;
        _remoteState = null; // the client resends within 0.5 s
        _lootTeamSwapped = false;
    }

    /// <summary>Remote player's position: the ghost follows the client's PlayerState packets.</summary>
    internal static bool TryGetRemotePosition(out Vector3 pos, out Quaternion rot)
    {
        var ghost = GameState.RemoteGhostObject;
        if (ghost == null || !NetworkManager.HasPeer || PlayerSync.ReceivedCount == 0)
        {
            pos = default; rot = default;
            return false;
        }
        var t = ghost.transform;
        pos = t.position;
        rot = t.rotation;
        return true;
    }

    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsHostSide) return;
        try
        {
            if (!GameManagerUtil.HasGameManager) { _matchStarted = false; _proxy = null; _proxyPtr = IntPtr.Zero; _proxyCharacter = null; }
            else if (_proxyPtr != IntPtr.Zero && (_proxy == null || !_proxy.gameObject.activeInHierarchy))
            {
                Plugin.StaticLog.LogWarning("[SoulstoneCoop][proxy] Proxy went inactive/destroyed - will recreate.");
                _proxy = null; _proxyPtr = IntPtr.Zero; _proxyCharacter = null;
            }

            bool haveRemote = TryGetRemotePosition(out var pos, out var rot);
            if (_proxy == null)
            {
                _retryTimer -= unscaledDeltaTime;
                if (haveRemote && _matchStarted && GameState.LocalPlayerEntity != null && _retryTimer <= 0f && _createFailures < 5)
                {
                    _retryTimer = 2f;
                    CreateProxy(pos, rot);
                }
            }
            else if (haveRemote)
            {
                AfterLoot(); // safety net if ProcessLoot threw past its postfix
                MoveProxy(pos, rot);
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][proxy] Tick: {ex}");
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer >= TelemetryInterval)
        {
            _telemetryTimer = 0f;
            if (NetworkManager.HasPeer)
            {
                string where = _proxy != null ? _proxy.transform.position.ToString() : "-";
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][proxy] Host: active={Active} pos={where} hitsToClient={_proxyHits} dmgToClient={_proxyDamage:F0} " +
                    $"hostHits={_hostHits} hostDmg={_hostDamage:F0} spawnRedirects={_spawnRedirects}/{_spawnCalls} " +
                    $"recyclerProtected={_recyclerProtected} seekerRetargets={_seekerRetargets} maxHit={_maxHit:F0} by {_maxHitBy} " +
                    $"clientDead={(_remoteState.HasValue ? _remoteState.Value.Dead.ToString() : "?")}");
            }
            _proxyHits = _hostHits = _spawnRedirects = _spawnCalls = _recyclerProtected = _seekerRetargets = 0;
            _proxyDamage = _hostDamage = 0;
            _maxHit = 0f; _maxHitBy = "-";
        }
    }

    private static void CreateProxy(Vector3 pos, Quaternion rot)
    {
        Entity? inst = null;
        try
        {
            var prefab = ResourcesUtil.Load<Entity>(ProxyPrefabPath);
            if (prefab == null) throw new Exception($"ResourcesUtil.Load<Entity>(\"{ProxyPrefabPath}\") returned null");
            ResourcesUtil.GetPrefabInstance<Entity>(prefab, pos, rot, out inst);
            if (inst == null) throw new Exception("GetPrefabInstance returned null");

            // Same order as CheatManager.SpawnDummy (team, CacheReferences, stats, Setup), with Team = Players.
            var team = inst._team ?? inst.GetComponent<TeamComponent>();
            if (team == null) throw new Exception("Dummy has no TeamComponent");
            _originalTeam = team.Team;
            team.Team = Team.Players;
            inst.CacheReferences();
            var stats = inst._stats;
            if (stats != null && stats.Stats != null) stats.Stats.CopyStats(stats.Stats);
            if (stats != null && stats.MultiplicativeStats != null) stats.MultiplicativeStats.SetToOne();

            // Before Setup: RegisterEntity / HealthComponent.Setup read these.
            inst.CanBeRecycledByDistance = false;
            inst.IgnoredByUnitLimit = true;
            inst.IgnoredByEntitySearchQueries = false; // the enemies' target search must find it
            inst.HasSpawnSound = false;
            _originalVfxOnSetup = inst.VfxOnSetup;
            inst.VfxOnSetup = null!;
            var health = inst._health;
            if (health != null)
            {
                health.AvoidCreatingHealthBar = true;
                health.IgnoreMinimapTracking = true;
            }

            inst.Setup();

            // It's a stand-in, not an actor: no AI, no visuals (the ghost is the visual).
            var ai = inst.GetComponent<BasicAIComponent>();
            if (ai != null) { ai.StopRefreshingTarget(); ai.enabled = false; }
            var staticAi = inst.GetComponent<StaticAIComponent>();
            if (staticAi != null) staticAi.enabled = false;
            int renderers = 0;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; renderers++; }

            _proxy = inst;
            _proxyPtr = inst.Pointer;
            _proxyCharacter = inst._character;
            var dst = stats != null ? stats.Stats : null;
            string dummyStats = dst != null
                ? $"maxHP={stats!.GetMaxHealth():F0} armor={dst.ArmorModifier} dr={dst.DamageReduction} block={dst.BlockChance}"
                : "no stats";
            _appliedDead = false;
            ApplyRemoteState();

            var comps = inst.GetComponents<Component>();
            var names = new List<string>();
            foreach (var c in comps) if (c != null) names.Add(c.GetIl2CppType().Name);
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][proxy] Created proxy '{inst.gameObject.name}' at {pos}: team {_originalTeam}->{team.Team} isPlayer={inst.IsPlayer()} " +
                $"character={(_proxyCharacter != null)} collider={(inst._physical != null && inst._physical.Collider != null)} " +
                $"ai={(ai != null)} staticAi={(staticAi != null)} renderersOff={renderers} dummy[{dummyStats}] " +
                $"now[maxHP={(stats != null ? stats.GetMaxHealth() : -1):F0} hp={(health != null ? health.CurrentHealth : -1):F0} clientState={_remoteState.HasValue}] " +
                $"components=[{string.Join(",", names)}]");
        }
        catch (Exception ex)
        {
            _createFailures++;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][proxy] CreateProxy failed (#{_createFailures}): {ex}");
            if (inst != null && _proxy == null)
            {
                try { RestoreForPool(inst); inst.Recycle(); } catch { /* best effort */ }
            }
        }
    }

    private static void MoveProxy(Vector3 pos, Quaternion rot)
    {
        var tr = _proxy!.transform;
        if (_proxyCharacter != null) _proxyCharacter.SetPosition(pos);
        else tr.position = pos;
        tr.rotation = rot;
    }

    private static void RemoveProxy(string reason)
    {
        var e = _proxy;
        if (e != null)
        {
            // Phase 8: enemies chasing the proxy would keep a pooled, inactive entity as target until their next
            // refresh (seekers forever: RetargetSeeker stops once the proxy is gone). Point them back at the host.
            try { ReleaseTargets(e); }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][proxy] Releasing the proxy's chasers: {ex.Message}"); }
        }
        _proxy = null;
        _proxyPtr = IntPtr.Zero;
        _proxyCharacter = null;
        if (e == null) return;
        try
        {
            RestoreForPool(e);
            e.Recycle();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Proxy recycled ({reason}).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][proxy] RemoveProxy: {ex.Message}");
        }
    }

    /// <summary>Seekers (always after a player) go to the host like RetargetSeeker does; every other AI drops the
    /// target, as when the client dies (DropAsTarget), and its next RefreshTarget picks the closest one.</summary>
    private static void ReleaseTargets(Entity proxy)
    {
        var all = EntityRegistry.AllEntities;
        if (all == null) return;
        var host = GameState.LocalPlayerEntity;
        int seekers = 0, dropped = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var ai = all[i] != null ? all[i]._basicAI : null;
            if (ai == null) continue;
            var t = ai._entityToFollow;
            if (t == null || t.Pointer != proxy.Pointer) continue;
            var seeker = ai.TryCast<PlayerSeekerAIComponent>();
            if (seeker != null && host != null)
            {
                seeker._entityToFollow = host;
                seeker._hasEntityToFollow = true;
                seeker._transformToFollow = host.transform;
                seeker._hasTransformToFollow = true;
                var phys = host._physical;
                seeker._entityToFollowRadius = phys != null ? phys.ColliderRadius : 0f;
                seekers++;
            }
            else
            {
                ai.RemoveTarget();
                dropped++;
            }
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Proxy going away: {seekers} seekers back on the host, {dropped} enemies dropped it as target.");
    }

    /// <summary>The instance goes back to the Dummy pool: undo what would leak into a cheat-spawned dummy.</summary>
    private static void RestoreForPool(Entity e)
    {
        var team = e._team ?? e.GetComponent<TeamComponent>();
        if (team != null) team.Team = _originalTeam;
        if (_originalVfxOnSetup != null) e.VfxOnSetup = _originalVfxOnSetup;
        foreach (var r in e.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
    }

    // ---------------- patch bodies ----------------

    /// <summary>Patch #9: damage aimed at the proxy goes to the client; the proxy itself never changes.</summary>
    internal static void ForwardDamage(Entity? attacker, float healthChange)
    {
        if (healthChange >= 0f || !NetworkManager.HasPeer) return;
        ushort attackerNetId = 0;
        if (attacker != null) HostEnemySync.TryGetNetId(attacker, out attackerNetId);
        var w = new NetDataWriter();
        w.Put((byte)PacketType.DamageToClient);
        w.Put(healthChange);
        w.Put(attackerNetId);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _proxyHits++;
        _proxyDamage -= healthChange;
        if (-healthChange > _maxHit) { _maxHit = -healthChange; _maxHitBy = attacker != null ? attacker.gameObject.name : "null"; }
    }

    internal static void CountHostHit(float healthChange)
    {
        if (healthChange >= 0f) return;
        _hostHits++;
        _hostDamage -= healthChange;
    }

    private static int _spawnAlternator;
    internal static bool InSpawnRedirect;

    /// <summary>Patch #7: every other spawn point taken around the host player is re-rolled around the client.</summary>
    internal static void MaybeRedirectSpawn(Vector3 referencePoint, float extraDistanceMultiplier, ref Vector3 result)
    {
        if (!Targetable || InSpawnRedirect) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null || !TryGetRemotePosition(out var remote, out _)) return;
        // Only reference points that are the host player (other callers pass their own areas).
        if ((referencePoint - local.transform.position).sqrMagnitude > 4f) return;
        _spawnCalls++;
        // Downed host (phase 4A): every spawn goes around the client, the only one still fighting.
        if ((++_spawnAlternator & 1) == 0 && !Life.CoopLife.LocalDowned) return;
        InSpawnRedirect = true;
        try { result = GameManagerUtil.GetRandomSpawnPointOutsideOfView(remote, extraDistanceMultiplier); }
        finally { InSpawnRedirect = false; }
        _spawnRedirects++;
    }

    private static readonly List<Entity> RecyclerProtected = new();

    /// <summary>Patch #8, before the recycler: enemies within 35 u of the client are temporarily not recyclable.</summary>
    internal static void BeforeRecycler()
    {
        AfterRecycler(); // in case the last call threw before its postfix
        if (!Targetable || !TryGetRemotePosition(out var remote, out _)) return;
        var all = EntityRegistry.AllEntities;
        if (all == null) return;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (e == null || !e.CanBeRecycledByDistance) continue;
            if ((e.transform.position - remote).sqrMagnitude >= RecycleRadiusSqr) continue;
            e.CanBeRecycledByDistance = false;
            RecyclerProtected.Add(e);
        }
        _recyclerProtected += RecyclerProtected.Count > 0 ? 1 : 0;
    }

    internal static void AfterRecycler()
    {
        if (RecyclerProtected.Count == 0) return;
        foreach (var e in RecyclerProtected)
            if (e != null) e.CanBeRecycledByDistance = true;
        RecyclerProtected.Clear();
    }

    /// <summary>Patch #13: PlayerSeekerAI always targets PlayerEntities[0]; make it chase the closer of host and proxy.</summary>
    internal static void RetargetSeeker(PlayerSeekerAIComponent ai)
    {
        if (_proxy == null) return;
        var host = GameState.LocalPlayerEntity;
        if (!Targetable)
        {
            // Dead client: a seeker still on the proxy goes back to the host.
            var on = ai._entityToFollow;
            if (on == null || on.Pointer != _proxyPtr || host == null) return;
        }
        var self = ai._transform;
        if (host == null || self == null) return;
        Vector3 p = self.position;
        float dHost = (host.transform.position - p).sqrMagnitude;
        float dProxy = (_proxy.transform.position - p).sqrMagnitude;
        bool hostDown = Life.CoopLife.LocalDowned; // phase 4A: a downed host isn't worth chasing
        if (hostDown && !Targetable) return;
        var want = Targetable && (hostDown || dProxy < dHost) ? _proxy : host;
        var cur = ai._entityToFollow;
        if (cur != null && cur.Pointer == want.Pointer) return;
        ai._entityToFollow = want;
        ai._hasEntityToFollow = true;
        ai._transformToFollow = want.transform;
        ai._hasTransformToFollow = true;
        var phys = want._physical;
        ai._entityToFollowRadius = phys != null ? phys.ColliderRadius : 0f;
        _seekerRetargets++;
    }
}

/// <summary>
/// Phase 3b, client side: damage the host's enemies dealt to our proxy, applied to the local player through the
/// game's own funnel, so death guard, UI and the local game over follow the normal path.
/// </summary>
public static class ClientIncomingDamage
{
    internal static bool ApplyingNetworkDamage;
    private static float _telemetryTimer;
    private static int _hits, _applied, _immune, _ignored;
    private static double _damage, _effective;

    public static void OnDamageToClient(NetDataReader reader)
    {
        float healthChange = reader.GetFloat();
        ushort attackerNetId = reader.GetUShort();
        if (!CoopRole.IsClientSide) return;
        _hits++;
        _damage -= healthChange;

        var local = GameState.LocalPlayerEntity;
        var health = local != null ? local._health : null;
        if (local == null || health == null || health.HasBeenKilled || !ClientPuppets.MatchStarted || healthChange >= 0f || float.IsNaN(healthChange))
        {
            _ignored++;
            return;
        }

        Entity? attacker = attackerNetId != 0 ? ClientPuppets.TryGetLivePuppet(attackerNetId) : null;
        try
        {
            ApplyingNetworkDamage = true;
            UnitUtil.ChangeEntityHealth(attacker!, local, healthChange, false, out float effective, out _, out bool immune);
            if (immune) _immune++;
            else { _applied++; _effective -= effective; }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][damage] Client: applying {healthChange:F1} from #{attackerNetId}: {ex.Message}");
        }
        finally
        {
            ApplyingNetworkDamage = false;
        }
    }

    private static float _stateTimer;
    private static bool _lastSentDead;

    /// <summary>Sends ClientState (see RemoteProxy.OnClientState) every 0.5 s, and right away when we die or revive.</summary>
    private static void SendState(float unscaledDeltaTime)
    {
        var local = GameState.LocalPlayerEntity;
        var h = local != null ? local._health : null;
        var st = local != null ? local._stats : null;
        if (!NetworkManager.HasPeer || h == null || st == null || st.Stats == null || !ClientPuppets.MatchStarted) return;
        // Phase 4A: downed counts as dead for the host (proxy out of the fight, XP ownership, revive/defeat logic).
        bool dead = h.HasBeenKilled || Life.CoopLife.LocalDowned;
        _stateTimer += unscaledDeltaTime;
        if (_stateTimer < 0.5f && dead == _lastSentDead) return;
        _stateTimer = 0f;
        _lastSentDead = dead;

        var w = new NetDataWriter();
        w.Put((byte)PacketType.ClientState);
        w.Put(dead);
        w.Put(h.CurrentHealth);
        w.Put(st.GetMaxHealth());
        w.Put(st.Stats.ArmorModifier);
        w.Put(st.Stats.DamageReduction);
        w.Put(st.Stats.BlockChance);
        w.Put(st.AbnormalStatusDampening);
        w.Put(st.AbnormalStatusDampeningFactor);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsClientSide) return;
        try { SendState(unscaledDeltaTime); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][damage] Client SendState: {ex.Message}"); }
        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < 5f) return;
        _telemetryTimer = 0f;
        if (_hits > 0)
        {
            var h = GameState.LocalPlayerEntity?._health;
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][damage] Client: incoming hits={_hits} damage={_damage:F0} applied={_applied} effective={_effective:F0} " +
                $"immune={_immune} ignored={_ignored} localHP={(h != null ? h.CurrentHealth : -1):F0} dead={(h != null && h.HasBeenKilled)}");
        }
        _hits = _applied = _immune = _ignored = 0;
        _damage = _effective = 0;
    }
}

// ---------------- Harmony patches (host side; every one wrapped in try/catch, see fase0 risk 6) ----------------

/// <summary>Patch #9: the proxy never takes damage or healing; damage on it is sent to the client.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class HostProxyDamagePatch
{
    [HarmonyPrefix]
    public static bool Prefix(Entity attackerEntity, Entity targetEntity, float healthChange,
        ref float effectiveHealthChange, ref float absorbedDamage, ref bool isImmune)
    {
        try
        {
            if (!CoopRole.IsHostSide || targetEntity == null) return true;
            // The remote caster's replays are visual only (RemoteCasterDamageBlockPatch skips the original too).
            if (Skills.RemoteCaster.IsCaster(attackerEntity) || Skills.RemoteCaster.IsCaster(targetEntity)) return false;
            if (RemoteProxy.IsProxy(targetEntity))
            {
                RemoteProxy.ForwardDamage(attackerEntity, healthChange);
                // Report it as dealt, so the caller shows the damage number (at the ghost) and runs on-hit logic.
                effectiveHealthChange = healthChange < 0f ? healthChange : 0f;
                absorbedDamage = 0f;
                isImmune = false;
                return false;
            }
            if (RemoteProxy.Active && targetEntity == GameState.LocalPlayerEntity) RemoteProxy.CountHostHit(healthChange);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostProxyDamagePatch: {ex}");
        }
        return true;
    }
}

/// <summary>See RemoteProxy.BeforeLoot: ProcessLoot NREs on a Players-team entity without Inventory.</summary>
[HarmonyPatch(typeof(LootUtil), nameof(LootUtil.ProcessLoot))]
public static class HostLootProxyTeamPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (CoopRole.IsHostSide) RemoteProxy.BeforeLoot(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] HostLootProxyTeamPatch.Prefix: {ex}"); }
    }

    [HarmonyFinalizer]
    public static void Finalizer()
    {
        try { RemoteProxy.AfterLoot(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] HostLootProxyTeamPatch.Finalizer: {ex}"); }
    }
}

/// <summary>Patch #7.</summary>
[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.GetRandomSpawnPointOutsideOfView))]
public static class HostSpawnPointPatch
{
    [HarmonyPostfix]
    public static void Postfix(Vector3 referencePoint, float extraDistanceMultiplier, ref Vector3 __result)
    {
        try
        {
            if (!CoopRole.IsHostSide) return;
            RemoteProxy.MaybeRedirectSpawn(referencePoint, extraDistanceMultiplier, ref __result);
        }
        catch (Exception ex)
        {
            RemoteProxy.InSpawnRedirect = false;
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostSpawnPointPatch: {ex}");
        }
    }
}

/// <summary>Patch #8.</summary>
[HarmonyPatch(typeof(EntityRegistry), nameof(EntityRegistry.DefaultContinuousRecycler))]
public static class HostRecyclerPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (CoopRole.IsHostSide) RemoteProxy.BeforeRecycler(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] HostRecyclerPatch.Prefix: {ex}"); }
    }

    [HarmonyPostfix]
    public static void Postfix()
    {
        try { RemoteProxy.AfterRecycler(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] HostRecyclerPatch.Postfix: {ex}"); }
    }
}

/// <summary>Patch #13: runs every frame before the seeker moves (DoOnUpdate reads _transformToFollow).</summary>
[HarmonyPatch(typeof(PlayerSeekerAIComponent), nameof(PlayerSeekerAIComponent.DoOnUpdate))]
public static class HostPlayerSeekerPatch
{
    [HarmonyPrefix]
    public static void Prefix(PlayerSeekerAIComponent __instance)
    {
        try
        {
            if (!CoopRole.IsHostSide || __instance == null) return;
            RemoteProxy.RetargetSeeker(__instance);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostPlayerSeekerPatch: {ex}");
        }
    }
}
