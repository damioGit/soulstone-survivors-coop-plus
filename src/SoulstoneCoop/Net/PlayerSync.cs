using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
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
/// Mirrors each player's own transform onto the ghost clones the others have of it. Snapshots arrive at 20 Hz; a
/// ghost is rendered 100 ms in the past, interpolated between the two snapshots around that time (short
/// extrapolation if the next one is late), so it moves smoothly instead of jumping every packet. The ghost's
/// Animator gets the same locomotion parameters the game's PlayerAnimator (ECM2...PlayerAnimator.OnUpdate,
/// RVA 0x7A7A20) sets on the real player, from the interpolated velocity.
/// Phase 9: a client sends its PlayerState (2) to the host; the host sends everybody one PlayersState (101) per tick
/// with its own position and the latest one of each client. One Sequenced packet per client and tick instead of a
/// relay per origin: the Sequenced channel has one sequence per connection, and many small packets cost headers.
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

    private struct Sample { public float T; public Vector3 Pos; public float Yaw; }

    /// <summary>One other player's interpolation and animation state.</summary>
    private sealed class Motion
    {
        public readonly List<Sample> Buffer = new();
        public readonly RemoteClock Clock = new(); // phase 8, [Coop] SenderClock
        public IntPtr AnimGhostPtr;
        public Animator[]? Animators;
        public Vector3 LastGhostPos;
        public bool HasLastGhostPos;
        public bool AnimErrorLogged;
        // Host: the client's latest raw state, forwarded in the next PlayersState (only once).
        public PlayerTransformState Latest;
        public bool LatestFresh;
    }

    private static readonly Dictionary<byte, Motion> Motions = new();
    private static bool _hooked;

    // Animator parameters of the player's controller (PlayerAnimator..cctor, RVA 0x7A82E0).
    private static readonly int ForwardMovement = Animator.StringToHash("ForwardMovement");
    private static readonly int SidewaysMovement = Animator.StringToHash("SidewaysMovement");
    private static readonly int OnGround = Animator.StringToHash("OnGround");

    private static Motion MotionOf(byte slot)
    {
        if (!Motions.TryGetValue(slot, out var m)) Motions[slot] = m = new Motion();
        return m;
    }

    private static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        Players.Left += slot => Motions.Remove(slot);
    }

    public static void Tick(float deltaTime)
    {
        EnsureHooked();
        foreach (var r in CoopSession.Remotes)
        {
            if (r.Ghost == null || !Motions.TryGetValue(r.Slot, out var m)) continue;
            try { ApplyGhost(r, m, deltaTime); }
            catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][sync] ApplyGhost({r.Slot}): {ex.Message}"); }
        }

        if (!NetworkManager.HasPeer) return;
        _timeSinceLastSend += deltaTime;
        if (_timeSinceLastSend < SendIntervalSeconds) return;
        _timeSinceLastSend = 0f;

        var local = GameState.LocalPlayerEntity;
        bool haveLocal = local != null;
        var own = default(PlayerTransformState);
        if (haveLocal)
        {
            var t = local!.gameObject.transform;
            own = new PlayerTransformState { Position = t.position, RotationY = t.eulerAngles.y, SentMs = RemoteClock.NowMs() };
        }

        var writer = new NetDataWriter();
        if (NetworkManager.Role == NetRole.Host)
        {
            int count = haveLocal ? 1 : 0;
            foreach (var kv in Motions) if (kv.Value.LatestFresh) count++;
            if (count == 0) return;
            writer.Put((byte)PacketType.PlayersState);
            writer.Put((byte)count);
            if (haveLocal) { writer.Put(Players.HostSlot); own.Serialize(writer); }
            foreach (var kv in Motions)
            {
                if (!kv.Value.LatestFresh) continue;
                kv.Value.LatestFresh = false;
                writer.Put(kv.Key);
                kv.Value.Latest.Serialize(writer);
            }
        }
        else
        {
            if (!haveLocal) return;
            writer.Put((byte)PacketType.PlayerState);
            own.Serialize(writer);
        }
        NetworkManager.Send(writer, DeliveryMethod.Sequenced);
        SentCount++;
    }

    /// <summary>Phase 6: a new ghost for a new run. Samples from the previous run (or from the menu) must not place it,
    /// and RemoteProxy/RemoteCaster wait for RemotePlayer.PositionsReceived > 0, i.e. a position from this run.</summary>
    public static void ResetForNewGhost(RemotePlayer r)
    {
        var m = MotionOf(r.Slot);
        m.Buffer.Clear();
        m.Clock.Reset();
        m.HasLastGhostPos = false;
        m.Animators = null;
        m.AnimGhostPtr = IntPtr.Zero;
        r.PositionsReceived = 0;
    }

    /// <summary>Host: a client's own position (PlayerState, from PacketRouter.From).</summary>
    public static void OnPlayerStateReceived(NetDataReader reader)
    {
        var state = PlayerTransformState.Deserialize(reader);
        if (NetworkManager.Role != NetRole.Host) return;
        byte slot = PacketRouter.From;
        var m = MotionOf(slot);
        m.Latest = state;
        m.LatestFresh = true;
        AddSample(slot, state);
    }

    /// <summary>Client: everybody's positions from the host (PlayersState). Our own entry is skipped.</summary>
    public static void OnPlayersStateReceived(NetDataReader reader)
    {
        int count = reader.GetByte();
        for (int i = 0; i < count; i++)
        {
            byte slot = reader.GetByte();
            var state = PlayerTransformState.Deserialize(reader);
            if (NetworkManager.Role != NetRole.Client || slot == Players.MySlot || !Players.IsPresent(slot)) continue;
            AddSample(slot, state);
        }
    }

    private static void AddSample(byte slot, PlayerTransformState state)
    {
        var r = CoopSession.Get(slot);
        if (r == null) return;
        var m = MotionOf(slot);
        ReceivedCount++;
        r.PositionsReceived++;
        r.LastPosition = state.Position;

        float now = Time.unscaledTime;
        // Phase 8: with SenderClock the sample goes where the player took it (jitter no longer bunches samples).
        bool senderClock = Plugin.SenderClock.Value;
        float t = senderClock ? (float)m.Clock.ToLocal(state.SentMs, now) : now;
        var buffer = m.Buffer;
        // A big jump (respawn, scene change, our own pause backlog): snap instead of sliding across the map.
        if (buffer.Count > 0 && (buffer[buffer.Count - 1].Pos - state.Position).sqrMagnitude > TeleportDistance * TeleportDistance)
            buffer.Clear();
        // A faster transit just lowered the clock offset: keep the buffer in order.
        if (senderClock && buffer.Count > 0 && t <= buffer[buffer.Count - 1].T) t = buffer[buffer.Count - 1].T + 0.001f;
        buffer.Add(new Sample { T = t, Pos = state.Position, Yaw = state.RotationY });
        while (buffer.Count > 2 && buffer[0].T < now - 1f) buffer.RemoveAt(0);
        if (buffer.Count == 1) Place(r, m, state.Position, state.RotationY, snap: true);
    }

    private static void ApplyGhost(RemotePlayer r, Motion m, float dt)
    {
        var buffer = m.Buffer;
        if (buffer.Count == 0) return;
        float renderT = Time.unscaledTime - InterpDelay;
        Vector3 pos;
        float yaw;
        var first = buffer[0];
        var last = buffer[buffer.Count - 1];
        if (buffer.Count == 1 || renderT <= first.T)
        {
            pos = first.Pos; yaw = first.Yaw;
            if (buffer.Count == 1) { pos = last.Pos; yaw = last.Yaw; }
        }
        else if (renderT >= last.T)
        {
            var prev = buffer[buffer.Count - 2];
            float span = Mathf.Max(0.001f, last.T - prev.T);
            float ahead = Mathf.Min(renderT - last.T, MaxExtrapolation);
            pos = last.Pos + (last.Pos - prev.Pos) / span * ahead;
            yaw = last.Yaw;
        }
        else
        {
            int i = buffer.Count - 2;
            while (i > 0 && buffer[i].T > renderT) i--;
            var a = buffer[i];
            var b = buffer[i + 1];
            float k = Mathf.Clamp01((renderT - a.T) / Mathf.Max(0.001f, b.T - a.T));
            pos = Vector3.Lerp(a.Pos, b.Pos, k);
            yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, k);
        }
        Place(r, m, pos, yaw, snap: false);
        Animate(r, m, dt);
    }

    private static void Place(RemotePlayer r, Motion m, Vector3 pos, float yaw, bool snap)
    {
        var ghost = r.Ghost;
        if (ghost == null) return;
        var t = ghost.transform;
        t.position = pos;
        t.eulerAngles = new Vector3(0f, yaw, 0f);
        if (snap) m.HasLastGhostPos = false;
    }

    /// <summary>Same parameters PlayerAnimator.OnUpdate writes: local velocity / max speed, damped 0.1 s. The game
    /// writes them on ModifiedCharacter._animator, which PlayerPrefabReferences.Setup points at the model's
    /// AnimatorComponent.Animators[0]: RemotePlayer.ModelAnimator on the ghost. Only that one gets them (the
    /// weapons' Animators have other controllers). 0.8.0 read Animator.parameters first: the interop can't build an
    /// Il2CppReferenceArray of AnimatorControllerParameter (not an Il2Cpp object), which threw and switched the whole
    /// locomotion off. A failure now only skips that frame for that animator and is logged once per ghost.</summary>
    private static void Animate(RemotePlayer r, Motion m, float dt)
    {
        var ghost = r.Ghost;
        if (ghost == null || dt <= 0f) return;
        if (ghost.Pointer != m.AnimGhostPtr || m.Animators == null)
        {
            // The ghost is rebuilt now and then (CoopBehaviour.RebuildGhostIfModelInactive): re-cache.
            m.AnimGhostPtr = ghost.Pointer;
            var model = r.ModelAnimator;
            m.Animators = model != null && model.transform.IsChildOf(ghost.transform)
                ? new[] { model }
                : ghost.GetComponentsInChildren<Animator>(true); // fallback: every animator, as before phase 5
            m.HasLastGhostPos = false;
            m.AnimErrorLogged = false;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][sync] Ghost {r.Slot} locomotion drives {m.Animators.Length} animator(s): " +
                string.Join(", ", m.Animators.Select(a => a != null ? a.gameObject.name : "null")));
        }
        var t = ghost.transform;
        Vector3 p = t.position;
        Vector3 vel = Vector3.zero;
        if (m.HasLastGhostPos && Time.timeScale > 0f) vel = (p - m.LastGhostPos) / dt;
        m.LastGhostPos = p;
        m.HasLastGhostPos = true;
        vel.y = 0f;

        float maxSpeed = 0f;
        var local = GameState.LocalPlayerEntity;
        if (local != null && local._character != null) maxSpeed = local._character.GetMaxSpeed();
        if (maxSpeed <= 0.1f) maxSpeed = 6f;
        Vector3 lv = t.InverseTransformDirection(vel) / maxSpeed;
        float fwd = Mathf.Clamp(lv.z, -1f, 1f), side = Mathf.Clamp(lv.x, -1f, 1f);
        float animDt = Time.deltaTime > 0f ? Time.deltaTime : dt;
        foreach (var a in m.Animators)
        {
            try
            {
                if (a == null || !a.isActiveAndEnabled) continue;
                a.SetFloat(ForwardMovement, fwd, 0.1f, animDt);
                a.SetFloat(SidewaysMovement, side, 0.1f, animDt);
                a.SetBool(OnGround, true);
            }
            catch (System.Exception ex)
            {
                if (m.AnimErrorLogged) continue;
                m.AnimErrorLogged = true;
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][sync] Ghost {r.Slot} animation on '{(a != null ? a.gameObject.name : "?")}': {ex.Message}");
            }
        }
    }
}
