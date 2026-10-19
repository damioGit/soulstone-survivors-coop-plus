using ECM2.Characters;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Life;

/// <summary>
/// Phase 4A: downed / revive / shared end of run. See docs/fase4a-investigacao.md.
///
/// Downed = the game's own ReviveOnDeath state: a lethal ChangeEntityHealth on an entity with
/// HealthComponent.ReviveOnDeath leaves it at 1 HP, unregisters it from EntityRegistry (no enemy finds it) and
/// never sets HasBeenKilled (no death sequence, no game over). The flag is only on DURING ChangeEntityHealth calls
/// on the local player (HealthComponent.UpdateAll would otherwise animate "Cowering" every frame). On top of it:
/// huge invulnerability, ragdoll, autocast off, input off, camera on an anchor that follows the partner's ghost.
///
/// The host is the authority: it runs the proximity revive for both players, and decides the defeat (both down).
/// Victory, the Home portal and surrender are mirrored to the other side.
/// </summary>
public static class CoopLife
{
    public enum EndKind : byte { None = 0, Defeat = 1, Victory = 2, Surrender = 3, PortalHome = 4 }

    private const float ReviveRadius = 2.5f;
    private const float ReviveTime = 3f;
    private const float DownedInvulnerability = 1e6f;
    private const float ReviveInvulnerability = 2f;
    private const float ReviveHealthFraction = 0.5f;
    private const float DefeatGrace = 0.5f;
    private const float LifePacketInterval = 0.2f;

    // ---- local state ----
    internal static bool LocalDowned;
    private static bool _enterPending;      // downed inside ChangeEntityHealth; the heavy part runs in Tick
    internal static bool RunEnding;         // defeat/surrender in progress: no more downing
    private static bool _matchStarted;
    private static bool _hooked;

    // ---- host-side view of the client / client-side view of the host ----
    private static bool _remoteDownedFromHost; // client: host's downed flag from CoopLife packets
    private static bool _clientDownedFromHost; // client: its own downed flag as the host sees it (UI only)
    private static float _progress;            // host: revive progress 0..1 (whoever is down); client: from packet
    private static float _bothDownTimer;
    private static float _clientRevivePendingTimer;
    private static float _lifeTimer;
    private static bool _lastSentHostDown, _lastSentClientDown;

    // ---- camera ----
    private static GameObject? _anchor;
    private static Transform? _savedTarget;
    private static bool _cameraSwapped;
    private static int _camRetargets;

    // ---- telemetry ----
    private static float _telemetryTimer;
    private static int _downs, _revives, _armed, _finalDeaths, _portalQueued;

    // ---- re-entrancy for mirrored actions ----
    internal static bool FromNetwork;
    private static float _pendingPortalTimer;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerDisconnected += OnPeerDisconnected;
    }

    public static void OnMatchStarted()
    {
        Reset("match started");
        _matchStarted = true;
    }

    private static void Reset(string why)
    {
        if (LocalDowned || RunEnding || _cameraSwapped)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Reset ({why}).");
        LocalDowned = false;
        _enterPending = false;
        RunEnding = false;
        _remoteDownedFromHost = false;
        _clientDownedFromHost = false;
        _progress = 0f;
        _bothDownTimer = 0f;
        _clientRevivePendingTimer = 0f;
        _pendingPortalTimer = 0f;
        _hostLost = false;
        _armDepth = 0;
        _cameraSwapped = false;
        _savedTarget = null;
        if (_anchor != null) { UnityEngine.Object.Destroy(_anchor); _anchor = null; }
    }

    private static bool Connected => NetworkManager.HasPeer;

    private static bool InMatch =>
        _matchStarted && GameManagerUtil.HasGameManager && GameState.LocalPlayerEntity != null;

    /// <summary>The partner is down (or dead). Host: from ClientState; client: from the host's CoopLife packets.</summary>
    internal static bool RemoteDowned => CoopRole.IsHostSide ? Enemies.RemoteProxy.RemoteDownOrDead : _remoteDownedFromHost;

    /// <summary>Local player down or dead for real.</summary>
    internal static bool LocalOut
    {
        get
        {
            var h = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity._health : null;
            return LocalDowned || (h != null && h.HasBeenKilled);
        }
    }

    // =====================================================================================
    // ChangeEntityHealth: arm ReviveOnDeath for the duration of a call on the local player
    // =====================================================================================

    private static int _armDepth;
    private static bool _origReviveOnDeath;
    private static float _origReviveDelay;

    internal static bool TryArm(Entity target)
    {
        if (LocalDowned || RunEnding || !Connected || !InMatch) return false;
        var local = GameState.LocalPlayerEntity;
        if (local == null || target.Pointer != local.Pointer) return false;
        var h = local._health;
        if (h == null || h.HasBeenKilled) return false;
        if (_armDepth == 0)
        {
            _origReviveOnDeath = h.ReviveOnDeath;
            _origReviveDelay = h.ReviveDelay;
            h.ReviveOnDeath = true;
            h.ReviveDelay = DownedInvulnerability;
        }
        _armDepth++;
        return true;
    }

    internal static void Disarm()
    {
        if (_armDepth <= 0) { _armDepth = 0; return; }
        if (--_armDepth > 0) return;
        var local = GameState.LocalPlayerEntity;
        var h = local != null ? local._health : null;
        if (h == null) return;
        bool downedNow = !LocalDowned && h.DelayToRevive > 0f && !h.HasBeenKilled;
        h.ReviveOnDeath = _origReviveOnDeath;
        h.ReviveDelay = _origReviveDelay;
        _armed++;
        if (!downedNow) return;
        // Minimal, can't-fail part right here (we're inside the game's damage code): flags + invulnerability.
        LocalDowned = true;
        _enterPending = true;
        h.InvulnerabilityTime = DownedInvulnerability;
        _downs++;
    }

    // =====================================================================================
    // Tick
    // =====================================================================================

    public static void Tick(float dt)
    {
        try
        {
            if (!GameManagerUtil.HasGameManager)
            {
                if (_matchStarted) Reset("no game manager");
                _matchStarted = false;
            }
            if (_enterPending) { _enterPending = false; EnterDownedVisuals(); }
            if (LocalDowned) UpdateSpectator();
            if (CoopRole.IsHostSide && Connected && InMatch) HostTick(dt);
            if (_pendingPortalTimer > 0f) RetryPendingPortal(dt);
            HostLostTick(dt);
            FeedbackTick(dt);
            HandleHotkeys();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Tick: {ex}");
        }

        _telemetryTimer += dt;
        if (_telemetryTimer >= 5f)
        {
            _telemetryTimer = 0f;
            if (Connected && InMatch)
            {
                var h = GameState.LocalPlayerEntity!._health;
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][life] localDowned={LocalDowned} remoteDowned={RemoteDowned} progress={_progress:F2} " +
                    $"hp={(h != null ? h.CurrentHealth : -1):F0} killed={(h != null && h.HasBeenKilled)} delayToRevive={(h != null ? h.DelayToRevive : -1):F0} " +
                    $"downs={_downs} revives={_revives} texts={_texts} textErrors={_feedbackErrors} vfx={_vfx} camRetargets={_camRetargets} armedCalls={_armed} finalDeaths={_finalDeaths} runEnding={RunEnding} portalQueued={_portalQueued}");
            }
            _armed = 0; _texts = 0; _vfx = 0; _camRetargets = 0;
        }
    }

    // ---------------- entering / leaving the downed state ----------------

    private static void EnterDownedVisuals()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        bool ragdolled = false;
        try
        {
            var rag = local._ragdoll;
            if (rag != null && !rag.ImmuneToRagdollEffect)
            {
                rag.SetToRagdoll(-1f, true);
                ragdolled = rag.Ragdolled;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] SetToRagdoll: {ex.Message}"); }
        try { if (local._character != null) local._character.SetMovementMode(MovementMode.None); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] SetMovementMode(None): {ex.Message}"); }
        try { if (local._playerSkillAutoCast != null) local._playerSkillAutoCast.ForceStopAutoCasting = true; }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] ForceStopAutoCasting: {ex.Message}"); }
        if (CoopRole.IsHostSide) DropEnemiesTargeting(local);
        bool registered = EntityRegistry.TryGetEntity(local.Id, out var reg) && reg != null && reg.Pointer == local.Pointer;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Local player DOWNED (hp={local._health.CurrentHealth:F0}, ragdoll={ragdolled}, stillRegistered={registered}).");
        if (CoopRole.IsHostSide) SendLife(force: true);
    }

    /// <summary>Enemies already chasing us keep their target until RefreshTarget; we're unregistered, so drop it now.</summary>
    private static void DropEnemiesTargeting(Entity who)
    {
        var all = EntityRegistry.AllEntities;
        if (all == null) return;
        int dropped = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var ai = all[i] != null ? all[i]._basicAI : null;
            if (ai == null) continue;
            var t = ai._entityToFollow;
            if (t == null || t.Pointer != who.Pointer) continue;
            ai.RemoveTarget();
            dropped++;
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] {dropped} enemies dropped the downed host as target.");
    }

    /// <summary>Back on our feet: undo the downed state (and what the native ReviveOnDeath branch did).</summary>
    internal static void ReviveLocal(string why)
    {
        if (!LocalDowned) return;
        var local = GameState.LocalPlayerEntity;
        LocalDowned = false;
        _enterPending = false;
        if (local == null) return;
        var h = local._health;
        try
        {
            if (h != null)
            {
                h.DelayToRevive = 0f;
                h.TriggeredRevive = false;
                float max = local._stats != null ? local._stats.GetMaxHealth() : h.CurrentHealth;
                h.CurrentHealth = Mathf.Max(1f, Mathf.Round(max * ReviveHealthFraction));
                h.InvulnerabilityTime = ReviveInvulnerability;
                if (h.AttackerList != null) h.AttackerList.Clear();
            }
            bool registered = EntityRegistry.TryGetEntity(local.Id, out var reg) && reg != null && reg.Pointer == local.Pointer;
            if (!registered) EntityRegistry.RegisterEntity(local);
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Revive (health/registry): {ex}"); }

        try
        {
            var rag = local._ragdoll;
            if (rag != null && rag.Ragdolled) rag.SetToAnimator(); // get-up animation, then Walking + Ragdolled=false
            else if (local._character != null) local._character.SetMovementMode(MovementMode.Walking);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Revive (ragdoll): {ex}");
            try { if (local._character != null) local._character.SetMovementMode(MovementMode.Walking); } catch { /* best effort */ }
        }
        try { if (local._playerSkillAutoCast != null) local._playerSkillAutoCast.ForceStopAutoCasting = false; }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] ForceStopAutoCasting off: {ex.Message}"); }
        RestoreCamera();
        if (why != "victory" && why != "portal") OnLocalRevivedEffect();
        _revives++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Local player REVIVED ({why}): hp={(h != null ? h.CurrentHealth : -1):F0}.");
        if (CoopRole.IsHostSide) SendLife(force: true);
    }

    /// <summary>Real death through the game's own path (ragdoll is already on), so its UpdateAll sees "all dead".</summary>
    internal static void FinalDeathLocal(string why)
    {
        RunEnding = true;
        var local = GameState.LocalPlayerEntity;
        var h = local != null ? local._health : null;
        bool wasDowned = LocalDowned;
        LocalDowned = false;
        _enterPending = false;
        RestoreCamera();
        if (local == null || h == null || h.HasBeenKilled) return;
        try
        {
            h.InvulnerabilityTime = 0f;
            h.DelayToRevive = 0f;
            h.ReviveOnDeath = false;
            UnitUtil.ChangeEntityHealth(null!, local, -(h.CurrentHealth + 1e9f), false, out _, out _, out _);
            if (!h.HasBeenKilled)
            {
                // Death guard absorbed it (charge gained while down): end it like GameManagerUtil.Surrender does.
                h.CurrentHealth = 0f;
                h.HasBeenKilled = true;
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][life] FinalDeath: {ex}");
            h.HasBeenKilled = true;
        }
        _finalDeaths++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Local player DEAD for real ({why}, wasDowned={wasDowned}).");
    }

    // ---------------- spectator camera ----------------

    private static void UpdateSpectator()
    {
        var cam = GameManagerUtil.PlayerCamera;
        var ghost = GameState.RemoteGhostObject;
        if (cam == null) return;
        if (_anchor == null)
        {
            _anchor = new GameObject("SoulstoneCoop_SpectatorAnchor");
            var local = GameState.LocalPlayerEntity;
            if (local != null) _anchor.transform.position = local.transform.position;
        }
        // Only follow a live ghost; the anchor itself is ours and never destroyed while the camera points at it.
        if (ghost != null && Connected) _anchor.transform.position = ghost.transform.position;
        if (!_cameraSwapped)
        {
            _savedTarget = cam.Target;
            _cameraSwapped = true;
        }
        if (cam.Target == null || cam.Target.Pointer != _anchor.transform.Pointer) { cam.Target = _anchor.transform; _camRetargets++; }
    }

    private static void RestoreCamera()
    {
        try
        {
            if (_cameraSwapped)
            {
                var cam = GameManagerUtil.PlayerCamera;
                var local = GameState.LocalPlayerEntity;
                Transform? back = _savedTarget != null ? _savedTarget : (local != null ? local.transform : null);
                if (cam != null && back != null) cam.Target = back;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] RestoreCamera: {ex.Message}"); }
        _cameraSwapped = false;
        _savedTarget = null;
        if (_anchor != null) { UnityEngine.Object.Destroy(_anchor); _anchor = null; }
    }

    // =====================================================================================
    // Host authority: revive by proximity, defeat when both are down
    // =====================================================================================

    private static void HostTick(float dt)
    {
        if (Time.timeScale <= 0f) { SendLife(force: false); return; } // paused: nothing progresses
        bool hostDown = LocalOut;
        bool clientDown = Enemies.RemoteProxy.RemoteDownOrDead;
        if (_clientRevivePendingTimer > 0f)
        {
            _clientRevivePendingTimer -= dt;
            if (!clientDown) _clientRevivePendingTimer = 0f;
            else clientDown = false; // Revive sent, waiting for its ClientState: don't count it as down meanwhile
        }

        if (RunEnding) { SendLife(force: false); return; }

        if (hostDown && clientDown)
        {
            _progress = 0f;
            _bothDownTimer += dt;
            if (_bothDownTimer >= DefeatGrace) EndRun(EndKind.Defeat);
        }
        else
        {
            _bothDownTimer = 0f;
            if (hostDown != clientDown && LocalDowned == hostDown /* host really dead can't be revived */ &&
                !(hostDown && LocalPlayerKilled()))
            {
                var local = GameState.LocalPlayerEntity!;
                if (Enemies.RemoteProxy.TryGetRemotePosition(out var clientPos, out _))
                {
                    Vector3 a = local.transform.position, b = clientPos;
                    a.y = b.y = 0f;
                    bool near = (a - b).sqrMagnitude <= ReviveRadius * ReviveRadius;
                    _progress = Mathf.Clamp01(_progress + (near ? dt : -2f * dt) / ReviveTime);
                    if (_progress >= 1f)
                    {
                        _progress = 0f;
                        if (hostDown) ReviveLocal("partner stood by");
                        else SendRevive();
                    }
                }
            }
            else _progress = 0f;
        }
        SendLife(force: false);
    }

    private static bool LocalPlayerKilled()
    {
        var h = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity._health : null;
        return h != null && h.HasBeenKilled;
    }

    private static void SendRevive()
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.Revive);
        w.Put((byte)1); // target: the client
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _clientRevivePendingTimer = 3f;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] Host: client revived (Revive sent).");
    }

    /// <summary>CoopLife packet, host -> client: hostDown u8 | clientDown u8 | progress f32.</summary>
    private static void SendLife(bool force)
    {
        if (!Connected) return;
        bool hostDown = LocalOut, clientDown = Enemies.RemoteProxy.RemoteDownOrDead;
        _lifeTimer += Time.unscaledDeltaTime;
        bool changed = hostDown != _lastSentHostDown || clientDown != _lastSentClientDown;
        bool anyDown = hostDown || clientDown;
        if (!force && !changed && (!anyDown || _lifeTimer < LifePacketInterval)) return;
        _lifeTimer = 0f;
        _lastSentHostDown = hostDown;
        _lastSentClientDown = clientDown;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CoopLife);
        w.Put(hostDown);
        w.Put(clientDown);
        w.Put(_progress);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // =====================================================================================
    // End of run
    // =====================================================================================

    /// <summary>Host: decide/mirror an end, then apply it locally.</summary>
    internal static void EndRun(EndKind kind)
    {
        if (CoopRole.IsHostSide)
        {
            var w = new NetDataWriter();
            w.Put((byte)PacketType.RunEnd);
            w.Put((byte)kind);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Run end: {kind} ({(CoopRole.IsHostSide ? "host" : "client")}).");
        ApplyEnd(kind);
    }

    private static void ApplyEnd(EndKind kind)
    {
        switch (kind)
        {
            case EndKind.Defeat:
                FinalDeathLocal("both players down");
                break;
            case EndKind.Surrender:
                RunEnding = true;
                if (LocalDowned) { LocalDowned = false; RestoreCamera(); }
                RunMirrored(() => GameManagerUtil.Surrender());
                break;
            case EndKind.Victory:
                ReviveLocal("victory");
                if (CoopRole.IsClientSide) ForceLocalVictory();
                break;
            case EndKind.PortalHome:
                ReviveLocal("portal");
                TryEnterPortalLocally();
                break;
        }
    }

    private static void RunMirrored(System.Action a)
    {
        try { FromNetwork = true; a(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Mirrored action: {ex}"); }
        finally { FromNetwork = false; }
    }

    /// <summary>Client: complete the boss objective; the game's own UpdateAll then runs its victory branch.</summary>
    private static void ForceLocalVictory()
    {
        var data = GameManagerUtil.GameData;
        var sgm = data != null ? data.SurvivorsGameManager : null;
        var obj = sgm != null ? sgm.MainObjective : null;
        if (obj == null)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][life] Victory: no MainObjective on this side.");
            return;
        }
        obj.Current = obj.Total;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Victory: MainObjective {obj.Current}/{obj.Total}.");
    }

    private static void TryEnterPortalLocally()
    {
        var data = GameManagerUtil.GameData;
        if (data == null) return;
        var obj = data.EnterPortalObjective;
        if (obj == null)
        {
            // The portal isn't open on this side yet (victory still in flight): retry for a while.
            if (_pendingPortalTimer <= 0f) _portalQueued++;
            _pendingPortalTimer = 30f;
            return;
        }
        _pendingPortalTimer = 0f;
        if (obj.Completed) return;
        var sgm = data.SurvivorsGameManager;
        if (sgm == null) return;
        RunMirrored(() => sgm.OnPlayerEnterPortal());
    }

    private static void RetryPendingPortal(float dt)
    {
        _pendingPortalTimer -= dt;
        var data = GameManagerUtil.GameData;
        if (data != null && data.EnterPortalObjective != null) TryEnterPortalLocally();
    }

    /// <summary>A local Home-portal entry (from the HomePortal trigger): tell the other side.</summary>
    internal static void OnLocalPortalEntered()
    {
        if (!Connected) return;
        if (CoopRole.IsHostSide)
        {
            var w = new NetDataWriter();
            w.Put((byte)PacketType.RunEnd);
            w.Put((byte)EndKind.PortalHome);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        }
        else SendRequest(EndKind.PortalHome);
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] Local player entered the Home portal: mirroring.");
    }

    internal static void SendRequest(EndKind kind)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.RunEndRequest);
        w.Put((byte)kind);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Host, local victory (OpenPortalAndObjective started by the game's own UpdateAll).</summary>
    internal static void OnHostVictory()
    {
        if (!Connected || !InMatch || RunEnding) return;
        if (Enemies.RemoteProxy.RemoteDownOrDead) SendRevive();
        EndRun(EndKind.Victory);
    }

    // =====================================================================================
    // Packets
    // =====================================================================================

    public static void OnRevive(NetDataReader r)
    {
        byte target = r.GetByte();
        if (!CoopRole.IsClientSide || target != 1) return;
        ReviveLocal("host says so");
    }

    public static void OnLife(NetDataReader r)
    {
        bool hostDown = r.GetBool();
        bool clientDown = r.GetBool();
        float progress = r.GetFloat();
        if (!CoopRole.IsClientSide) return;
        _remoteDownedFromHost = hostDown;
        _clientDownedFromHost = clientDown;
        _progress = progress;
    }

    public static void OnRunEnd(NetDataReader r)
    {
        var kind = (EndKind)r.GetByte();
        if (!CoopRole.IsClientSide) return;
        if (!InMatch) { Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEnd {kind} ignored: not in a match (phase 6: late packet after the run)."); return; }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEnd from host: {kind}.");
        try { ApplyEnd(kind); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] ApplyEnd({kind}): {ex}"); }
    }

    public static void OnRunEndRequest(NetDataReader r)
    {
        var kind = (EndKind)r.GetByte();
        if (!CoopRole.IsHostSide) return;
        if (!InMatch) { Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEndRequest {kind} ignored: not in a match."); return; }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEndRequest from client: {kind}.");
        try
        {
            if (kind == EndKind.Surrender) { if (!RunEnding) EndRun(EndKind.Surrender); }
            else if (kind == EndKind.PortalHome) { ReviveLocal("partner took the portal"); TryEnterPortalLocally(); }
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Request {kind}: {ex}"); }
    }

    private static void OnPeerDisconnected()
    {
        try
        {
            _remoteDownedFromHost = false;
            _progress = 0f;
            // Alone now: solo rules. A downed player has nobody to revive it.
            if (LocalDowned && InMatch) FinalDeathLocal("partner disconnected");
            // Phase 8: the client's run can't go on without the host (no enemies, no spawns): end it cleanly.
            if (CoopRole.IsClientSide && InMatch && !RunEnding) BeginHostLost();
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] OnPeerDisconnected: {ex}"); }
    }

    // =====================================================================================
    // Phase 8: the host left in the middle of the client's run
    // =====================================================================================

    private const float HostLostDelay = 4f; // s: long enough to read the notice before the end screen

    private static bool _hostLost;
    private static float _hostLostTimer;
    private static bool _hostLostWaitingLogged;

    private static void BeginHostLost()
    {
        _hostLost = true;
        _hostLostTimer = 0f;
        _hostLostWaitingLogged = false;
        bool won = VictoryReached();
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] Host lost mid-run (victory reached={won}, auto end={Plugin.EndRunWhenHostLeaves.Value}).");
        if (won) Lobby.CoopSession.Notice("O host desconectou. Entre no portal pra terminar a partida.", 10f);
        else if (Plugin.EndRunWhenHostLeaves.Value) Lobby.CoopSession.Notice("O host desconectou. Encerrando a partida...", 10f);
        else Lobby.CoopSession.Notice("O host desconectou. Desista pelo menu de pausa pra voltar.", 10f);
        if (won || !Plugin.EndRunWhenHostLeaves.Value) _hostLost = false; // nothing to do automatically
    }

    /// <summary>The run was already won here (the boss objective is done and the Home portal is open): the
    /// player keeps that and walks into the portal; surrendering would throw the victory away.</summary>
    internal static bool VictoryReached()
    {
        try
        {
            var data = GameManagerUtil.GameData;
            if (data == null) return false;
            if (data.EnterPortalObjective != null) return true;
            // RunEnd(Victory) applied, the game's victory branch (which opens the portal) still on its way.
            var obj = data.SurvivorsGameManager != null ? data.SurvivorsGameManager.MainObjective : null;
            return obj != null && obj.Total > 0 && obj.Current >= obj.Total;
        }
        catch { return false; }
    }

    /// <summary>Surrender through the game's own path (GameManagerUtil.Surrender: LossBySurrender, every player
    /// killed, the normal end screen and the profile save) once nothing is in the way: not in the level-up
    /// screen or the pause menu (the player can also press Desistir there themselves).</summary>
    private static void HostLostTick(float dt)
    {
        if (!_hostLost) return;
        if (!InMatch || RunEnding || NetworkManager.HasPeer || LocalPlayerKilled()) { _hostLost = false; return; }
        if (VictoryReached())
        {
            _hostLost = false;
            Lobby.CoopSession.Notice("O host desconectou. Entre no portal pra terminar a partida.", 10f);
            return;
        }
        _hostLostTimer += dt;
        if (_hostLostTimer < HostLostDelay) return;
        if (PauseSync.LocalReason != PauseReason.None)
        {
            if (!_hostLostWaitingLogged)
            {
                _hostLostWaitingLogged = true;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Host lost: waiting for the local {PauseSync.LocalReason} screen to close before ending.");
                Lobby.CoopSession.Notice(PauseSync.LocalReason is PauseReason.LevelUp or PauseReason.Choosing
                    ? "O host desconectou. Escolha o upgrade pra encerrar a partida."
                    : "O host desconectou. Feche a pausa (ou desista) pra encerrar.", 10f);
            }
            return;
        }
        _hostLost = false;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] Host lost: ending the client's run (Surrender).");
        RunEnding = true;
        if (LocalDowned) { LocalDowned = false; RestoreCamera(); }
        RunMirrored(() => GameManagerUtil.Surrender());
    }

    // =====================================================================================
    // Debug hotkeys: F9 lethal hit on the local player, F10 (host) victory, F11 (host) revive everyone
    // =====================================================================================

    private static bool _prevF9, _prevF10, _prevF11;

    private static void HandleHotkeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f9 = kb.f9Key.isPressed, f10 = kb.f10Key.isPressed, f11 = kb.f11Key.isPressed;
        if (f9 && !_prevF9) DebugLethalHit();
        if (f10 && !_prevF10 && CoopRole.IsHostSide) DebugVictory();
        if (f11 && !_prevF11 && CoopRole.IsHostSide) DebugReviveAll();
        _prevF9 = f9; _prevF10 = f10; _prevF11 = f11;
    }

    private static void DebugLethalHit()
    {
        var local = GameState.LocalPlayerEntity;
        var h = local != null ? local._health : null;
        if (local == null || h == null || !InMatch) return;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] F9: lethal hit on the local player.");
        h.InvulnerabilityTime = 0f;
        UnitUtil.ChangeEntityHealth(null!, local, -(h.CurrentHealth + 1e9f), false, out _, out _, out _);
    }

    private static void DebugVictory()
    {
        var data = GameManagerUtil.GameData;
        var obj = data != null && data.SurvivorsGameManager != null ? data.SurvivorsGameManager.MainObjective : null;
        if (obj == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][life] F10: no MainObjective yet."); return; }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] F10: forcing the boss objective ({obj.Current}/{obj.Total}).");
        obj.Current = obj.Total;
    }

    private static void DebugReviveAll()
    {
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] F11: revive everyone.");
        ReviveLocal("F11");
        if (Connected && Enemies.RemoteProxy.RemoteDownOrDead) SendRevive();
    }

    // =====================================================================================
    // Feedback in both windows, with the game's own systems (IMGUI is partly stripped in this build:
    // GUI calls throw "Method unstripping failed"). Floating text = the damage-number system
    // (OverheadCombatTextPanelManager.RequestCombatTextWithString); revive = the death guard VFX.
    // =====================================================================================

    private const float MarkerInterval = 0.9f;
    private const float ProgressInterval = 0.45f;
    private static float _markerTimer, _progressTimer;
    private static bool _prevRemoteDown;
    private static int _texts, _vfx, _feedbackErrors;

    private static void FeedbackTick(float dt)
    {
        if (!Connected || !InMatch || RunEnding) { _prevRemoteDown = false; return; }
        bool remoteDown = RemoteDowned;
        if (_prevRemoteDown && !remoteDown) OnPartnerRevivedEffect();
        _prevRemoteDown = remoteDown;
        if (!LocalDowned && !remoteDown) { _markerTimer = _progressTimer = 0f; return; }

        var local = GameState.LocalPlayerEntity;
        var ghost = GameState.RemoteGhostObject;
        Transform? downedBody = LocalDowned ? (local != null ? local.transform : null) : (ghost != null ? ghost.transform : null);
        if (downedBody == null) return;

        _markerTimer -= dt;
        if (_markerTimer <= 0f)
        {
            _markerTimer = MarkerInterval;
            // Short on purpose: the GPU combat text has a fixed per-text buffer (MaxCharacterCountPerText).
            string label = LocalDowned && remoteDown ? "CAIDOS" : "CAIDO";
            FloatText(CombatTextType.StatusEffect, downedBody, label);
            if (LocalDowned && remoteDown && ghost != null) FloatText(CombatTextType.StatusEffect, ghost.transform, "CAIDO");
        }
        if (_progress > 0.01f && !(LocalDowned && remoteDown))
        {
            _progressTimer -= dt;
            if (_progressTimer <= 0f)
            {
                _progressTimer = ProgressInterval;
                FloatText(CombatTextType.Healing, downedBody, $"REVIVE {Mathf.RoundToInt(_progress * 100f)}%");
            }
        }
    }

    private static void FloatText(CombatTextType type, Transform t, string text)
    {
        try
        {
            int max = MaxCombatTextChars();
            if (max <= 0 || max > MaxCombatTextFallback) max = MaxCombatTextFallback;
            if (text.Length > max) text = text.Substring(0, max);
            OverheadCombatTextPanelManager.RequestCombatTextWithString(type, t, text, false);
            _texts++;
        }
        catch (Exception ex)
        {
            if (_feedbackErrors++ < 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] Floating text: {ex.Message}");
        }
    }

    /// <summary>GPUIndirectInstancedCombatTextManager.CreateCombatTextRequest (RVA 0x601550) only logs "Combat Text is
    /// bigger than the buffer capacity" and then writes past its per-text buffer (IndexOutOfRange).</summary>
    private const int MaxCombatTextFallback = 16; // observed: "buffer capacity 16"

    private static int MaxCombatTextChars()
    {
        try
        {
            var ui = GameManagerUtil.GameplayUIManager;
            var gpu = ui != null ? ui.GPUIndirectWorldSpaceOverheadCombatTextPanel : null;
            return gpu != null ? gpu.MaxCharacterCountPerText : 0;
        }
        catch { return 0; }
    }

    /// <summary>The local player's InventoryComponent.DeathGuardVfx (the "second chance" effect), at a position.</summary>
    private static void PlayReviveVfx(Transform where, bool onLocalPlayer)
    {
        try
        {
            var local = GameState.LocalPlayerEntity;
            var trait = local != null && local._inventory != null ? local._inventory.DeathGuardVfx : null;
            var runner = JobRunner.Instance;
            if (trait == null || runner == null) return;
            var e = onLocalPlayer
                ? SkillUtil.RunVfx(trait, 1f, new SkillTimedVfxTraitData())
                : SkillUtil.RunVfx(trait, where.position, where.rotation, 1f, null!, new SkillTimedVfxTraitData());
            if (e != null) { runner.StartCoroutine(e); _vfx++; }
        }
        catch (Exception ex)
        {
            if (_feedbackErrors++ < 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] Revive VFX: {ex.Message}");
        }
    }

    private static void OnLocalRevivedEffect()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        PlayReviveVfx(local.transform, onLocalPlayer: true);
        FloatText(CombatTextType.Healing, local.transform, "REVIVEU!");
    }

    private static void OnPartnerRevivedEffect()
    {
        var ghost = GameState.RemoteGhostObject;
        if (ghost == null) return;
        PlayReviveVfx(ghost.transform, onLocalPlayer: false);
        FloatText(CombatTextType.Healing, ghost.transform, "REVIVEU!");
    }
}

// =====================================================================================
// Harmony patches (every body wrapped: several run inside the game's UpdateAll systems)
// =====================================================================================

/// <summary>Arms ReviveOnDeath only for this call; the finalizer always disarms (and detects the down).</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class CoopLifeDownPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity targetEntity, float healthChange, out bool __state)
    {
        __state = false;
        try
        {
            if (targetEntity == null || healthChange >= 0f) return;
            __state = CoopLife.TryArm(targetEntity);
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeDownPatch.Prefix: {ex}"); }
    }

    [HarmonyFinalizer]
    public static void Finalizer(bool __state)
    {
        if (!__state) return;
        try { CoopLife.Disarm(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeDownPatch.Finalizer: {ex}"); }
    }
}

/// <summary>No movement/dash/click-to-move input while downed. Only the local player is in this list.</summary>
[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.UpdateAll))]
public static class CoopLifeInputBlockPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return !CoopLife.LocalDowned; }
        catch { return true; }
    }
}

/// <summary>Host: the game's own victory branch starts OpenPortalAndObjective; mirror it to the client.</summary>
[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.OpenPortalAndObjective))]
public static class CoopLifeVictoryPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { if (CoopRole.IsHostSide) CoopLife.OnHostVictory(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeVictoryPatch: {ex}"); }
    }
}

/// <summary>Home portal (UnityEvent from HomePortal.OnTriggerStay): whoever enters takes the other one along.</summary>
[HarmonyPatch(typeof(SurvivorsGameManager), nameof(SurvivorsGameManager.OnPlayerEnterPortal))]
public static class CoopLifeHomePortalPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (!CoopLife.FromNetwork) CoopLife.OnLocalPortalEntered(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeHomePortalPatch: {ex}"); }
    }
}

/// <summary>Endless / BossRush portals pick a random next map on each machine: blocked in co-op for now.</summary>
[HarmonyPatch(typeof(SurvivorsGameManager), nameof(SurvivorsGameManager.OnPlayerEnterEndlessPortal))]
public static class CoopLifeEndlessPortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        if (!NetworkManager.HasPeer) return true;
        Plugin.StaticLog.LogWarning("[SoulstoneCoop][life] Endless portal is not supported in co-op yet (blocked).");
        return false;
    }
}

[HarmonyPatch(typeof(SurvivorsGameManager), nameof(SurvivorsGameManager.OnPlayerEnterBossRushPortal))]
public static class CoopLifeBossRushPortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        if (!NetworkManager.HasPeer) return true;
        Plugin.StaticLog.LogWarning("[SoulstoneCoop][life] Boss Rush portal is not supported in co-op yet (blocked).");
        return false;
    }
}

/// <summary>Surrender ends the run for both: the client asks the host, the host mirrors it.</summary>
[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.Surrender))]
public static class CoopLifeSurrenderPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try
        {
            if (CoopLife.FromNetwork || !NetworkManager.HasPeer) return true;
            if (CoopRole.IsClientSide)
            {
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] Surrender pressed: asking the host.");
                CoopLife.SendRequest(CoopLife.EndKind.Surrender);
                return false;
            }
            if (CoopLife.RunEnding) return false;
            CoopLife.EndRun(CoopLife.EndKind.Surrender); // runs the original through RunMirrored
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeSurrenderPatch: {ex}");
            return true;
        }
    }
}
