using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SoulstoneCoop.Net;

/// <summary>Transform snapshot for one player entity, sent at a fixed tick rate.</summary>
public struct PlayerTransformState
{
    public Vector3 Position;
    public float RotationY;
    public uint SentMs; // phase 8: the sender's clock (RemoteClock.NowMs)

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Position.x);
        writer.Put(Position.y);
        writer.Put(Position.z);
        writer.Put(RotationY);
        writer.Put(SentMs);
    }

    public static PlayerTransformState Deserialize(NetDataReader reader)
    {
        return new PlayerTransformState
        {
            Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()),
            RotationY = reader.GetFloat(),
            SentMs = reader.GetUInt(),
        };
    }
}

/// <summary>
/// Mirrors each side's own player transform onto the other side's ghost clone. Snapshots arrive at 20 Hz; the
/// ghost is rendered 100 ms in the past, interpolated between the two snapshots around that time (short
/// extrapolation if the next one is late), so it moves smoothly instead of jumping every packet. The ghost's
/// Animator gets the same locomotion parameters the game's PlayerAnimator (ECM2...PlayerAnimator.OnUpdate,
/// RVA 0x7A7A20) sets on the real player, from the interpolated velocity.
/// </summary>
public static class PlayerSync
{
    private const float SendIntervalSeconds = 0.05f; // 20Hz
    private const float InterpDelay = 0.1f;
    private const float MaxExtrapolation = 0.15f;
    private const float TeleportDistance = 8f;
    private static float _timeSinceLastSend;

    public static int SentCount;
    public static int ReceivedCount;
    public static Vector3 LastReceivedPosition;

    private struct Sample { public float T; public Vector3 Pos; public float Yaw; }
    private static readonly List<Sample> Buffer = new();
    private static readonly RemoteClock Clock = new(); // phase 8, [Coop] SenderClock

    // Animator parameters of the player's controller (PlayerAnimator..cctor, RVA 0x7A82E0).
    private static readonly int ForwardMovement = Animator.StringToHash("ForwardMovement");
    private static readonly int SidewaysMovement = Animator.StringToHash("SidewaysMovement");
    private static readonly int OnGround = Animator.StringToHash("OnGround");
    private static readonly int Dash = Animator.StringToHash("Dash");
    private static IntPtr _animGhostPtr;
    private static Animator[]? _ghostAnimators;

    // Dash (phase 5): the sender's ModifiedCharacter.IsDashing() (the _isDashing flag PlayerAnimator.OnUpdate writes
    // into the "Dash" bool), sent on change as DashState. Applied InterpDelay after arrival, like the positions,
    // so the ghost's dash pose lines up with the dash movement it's showing.
    private struct DashEvent { public float At; public bool On; }
    private static readonly List<DashEvent> DashEvents = new();
    private static bool _ghostDashing;
    private static float _ghostDashSince;
    private static bool _lastSentDash;
    private const float MaxDashSeconds = 1.5f; // safety: a lost "end" never leaves the ghost dashing
    public static int DashesSent, DashesReceived;

    private static Vector3 _lastGhostPos;
    private static bool _hasLastGhostPos;
    private static bool _animErrorLogged;

    public static void Tick(float deltaTime)
    {
        try { ApplyGhost(deltaTime); }
        catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][sync] ApplyGhost: {ex.Message}"); }

        if (!NetworkManager.HasPeer) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;

        try { SendDashIfChanged(local); }
        catch (System.Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][sync] Dash state: {ex.Message}"); }

        _timeSinceLastSend += deltaTime;
        if (_timeSinceLastSend < SendIntervalSeconds) return;
        _timeSinceLastSend = 0f;

        var t = local.gameObject.transform;
        var state = new PlayerTransformState { Position = t.position, RotationY = t.eulerAngles.y, SentMs = RemoteClock.NowMs() };

        var writer = new NetDataWriter();
        writer.Put((byte)PacketType.PlayerState);
        state.Serialize(writer);
        NetworkManager.Send(writer, DeliveryMethod.Sequenced);
        SentCount++;
    }

    /// <summary>Phase 6: a new ghost for a new run. Samples from the previous run (or from the menu) must not place it,
    /// and RemoteProxy waits for ReceivedCount > 0, i.e. a position from this run.</summary>
    public static void ResetForNewGhost()
    {
        Buffer.Clear();
        Clock.Reset();
        ReceivedCount = 0;
        _hasLastGhostPos = false;
        _ghostAnimators = null;
        _animGhostPtr = IntPtr.Zero;
        DashEvents.Clear();
        _ghostDashing = false;
    }

    // ---------------- dash ----------------

    private static void SendDashIfChanged(Entity local)
    {
        var ch = local._character;
        bool dashing = ch != null && ch.IsDashing();
        if (dashing == _lastSentDash) return;
        _lastSentDash = dashing;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.DashState);
        w.Put(dashing);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        if (dashing) DashesSent++;
    }

    public static void OnDashStateReceived(NetDataReader reader)
    {
        bool on = reader.GetBool();
        if (on) DashesReceived++;
        DashEvents.Add(new DashEvent { At = Time.unscaledTime + InterpDelay, On = on });
        if (DashEvents.Count > 16) DashEvents.RemoveAt(0);
    }

    /// <summary>Applies the dash events that are due; true while the ghost should show the dash.</summary>
    private static bool UpdateGhostDash()
    {
        float now = Time.unscaledTime;
        while (DashEvents.Count > 0 && DashEvents[0].At <= now)
        {
            bool on = DashEvents[0].On;
            DashEvents.RemoveAt(0);
            if (on && !_ghostDashing) _ghostDashSince = now;
            _ghostDashing = on;
        }
        if (_ghostDashing && now - _ghostDashSince > MaxDashSeconds) _ghostDashing = false;
        return _ghostDashing;
    }

    public static void OnPlayerStateReceived(NetDataReader reader)
    {
        var state = PlayerTransformState.Deserialize(reader);
        ReceivedCount++;
        LastReceivedPosition = state.Position;

        float now = Time.unscaledTime;
        // Phase 8: with SenderClock the sample goes where the partner took it (jitter no longer bunches samples).
        bool senderClock = Plugin.SenderClock.Value;
        float t = senderClock ? (float)Clock.ToLocal(state.SentMs, now) : now;
        // A big jump (respawn, scene change, our own pause backlog): snap instead of sliding across the map.
        if (Buffer.Count > 0 && (Buffer[Buffer.Count - 1].Pos - state.Position).sqrMagnitude > TeleportDistance * TeleportDistance)
            Buffer.Clear();
        // A faster transit just lowered the clock offset: keep the buffer in order.
        if (senderClock && Buffer.Count > 0 && t <= Buffer[Buffer.Count - 1].T) t = Buffer[Buffer.Count - 1].T + 0.001f;
        Buffer.Add(new Sample { T = t, Pos = state.Position, Yaw = state.RotationY });
        while (Buffer.Count > 2 && Buffer[0].T < now - 1f) Buffer.RemoveAt(0);
        if (Buffer.Count == 1) Place(state.Position, state.RotationY, snap: true);
    }

    private static void ApplyGhost(float dt)
    {
        if (Buffer.Count == 0) return;
        float renderT = Time.unscaledTime - InterpDelay;
        Vector3 pos;
        float yaw;
        var first = Buffer[0];
        var last = Buffer[Buffer.Count - 1];
        if (Buffer.Count == 1 || renderT <= first.T)
        {
            pos = first.Pos; yaw = first.Yaw;
            if (Buffer.Count == 1) { pos = last.Pos; yaw = last.Yaw; }
        }
        else if (renderT >= last.T)
        {
            var prev = Buffer[Buffer.Count - 2];
            float span = Mathf.Max(0.001f, last.T - prev.T);
            float ahead = Mathf.Min(renderT - last.T, MaxExtrapolation);
            pos = last.Pos + (last.Pos - prev.Pos) / span * ahead;
            yaw = last.Yaw;
        }
        else
        {
            int i = Buffer.Count - 2;
            while (i > 0 && Buffer[i].T > renderT) i--;
            var a = Buffer[i];
            var b = Buffer[i + 1];
            float k = Mathf.Clamp01((renderT - a.T) / Mathf.Max(0.001f, b.T - a.T));
            pos = Vector3.Lerp(a.Pos, b.Pos, k);
            yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, k);
        }
        Place(pos, yaw, snap: false);
        Animate(dt);
    }

    private static void Place(Vector3 pos, float yaw, bool snap)
    {
        var ghost = GameState.RemoteGhostObject;
        if (ghost == null) return;
        var t = ghost.transform;
        t.position = pos;
        t.eulerAngles = new Vector3(0f, yaw, 0f);
        if (snap) _hasLastGhostPos = false;
    }

    /// <summary>Same parameters PlayerAnimator.OnUpdate writes: local velocity / max speed, damped 0.1 s. The game
    /// writes them on ModifiedCharacter._animator, which PlayerPrefabReferences.Setup points at the model's
    /// AnimatorComponent.Animators[0]: VisualGhostFactory.ModelAnimator on the ghost. Only that one gets them (the
    /// weapons' Animators have other controllers). 0.8.0 read Animator.parameters first: the interop can't build an
    /// Il2CppReferenceArray of AnimatorControllerParameter (not an Il2Cpp object), which threw and switched the whole
    /// locomotion off. A failure now only skips that frame for that animator and is logged once per ghost.</summary>
    private static void Animate(float dt)
    {
        var ghost = GameState.RemoteGhostObject;
        if (ghost == null || dt <= 0f) return;
        if (ghost.Pointer != _animGhostPtr || _ghostAnimators == null)
        {
            // The ghost is rebuilt now and then (CoopBehaviour.RebuildGhostIfModelInactive): re-cache.
            _animGhostPtr = ghost.Pointer;
            var model = VisualGhostFactory.ModelAnimator;
            _ghostAnimators = model != null && model.transform.IsChildOf(ghost.transform)
                ? new[] { model }
                : ghost.GetComponentsInChildren<Animator>(true); // fallback: every animator, as before phase 5
            _hasLastGhostPos = false;
            _animErrorLogged = false;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][sync] Ghost locomotion drives {_ghostAnimators.Length} animator(s): " +
                string.Join(", ", _ghostAnimators.Select(a => a != null ? a.gameObject.name : "null")));
        }
        var t = ghost.transform;
        Vector3 p = t.position;
        Vector3 vel = Vector3.zero;
        if (_hasLastGhostPos && Time.timeScale > 0f) vel = (p - _lastGhostPos) / dt;
        _lastGhostPos = p;
        _hasLastGhostPos = true;
        vel.y = 0f;

        float maxSpeed = 0f;
        var local = GameState.LocalPlayerEntity;
        if (local != null && local._character != null) maxSpeed = local._character.GetMaxSpeed();
        if (maxSpeed <= 0.1f) maxSpeed = 6f;
        Vector3 lv = t.InverseTransformDirection(vel) / maxSpeed;
        float fwd = Mathf.Clamp(lv.z, -1f, 1f), side = Mathf.Clamp(lv.x, -1f, 1f);
        float animDt = Time.deltaTime > 0f ? Time.deltaTime : dt;
        bool dashing = UpdateGhostDash();
        foreach (var a in _ghostAnimators)
        {
            try
            {
                if (a == null || !a.isActiveAndEnabled) continue;
                a.SetFloat(ForwardMovement, fwd, 0.1f, animDt);
                a.SetFloat(SidewaysMovement, side, 0.1f, animDt);
                a.SetBool(OnGround, true);
                a.SetBool(Dash, dashing); // PlayerAnimator writes it every frame too, so the player controller has it
            }
            catch (System.Exception ex)
            {
                if (_animErrorLogged) continue;
                _animErrorLogged = true;
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][sync] Ghost animation on '{(a != null ? a.gameObject.name : "?")}': {ex.Message}");
            }
        }
    }
}
