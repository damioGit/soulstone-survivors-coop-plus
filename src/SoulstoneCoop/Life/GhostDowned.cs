using SoulstoneCoop.Lobby;
using UnityEngine;

namespace SoulstoneCoop.Life;

/// <summary>
/// #11: a downed partner kept standing on everyone else's screen (only the floating "DOWN" text told). The ghost
/// can't ragdoll like the real player (it has no physics body), so its model lies on its back and its Animator is
/// frozen while that player is down, and both come back when it gets up. The ghost root keeps its own position and
/// yaw (PlayerSync); only the model child under it is turned.
/// </summary>
public static class GhostDowned
{
    private sealed class State
    {
        public Transform? Model;
        public Quaternion Rotation;
        public Vector3 Position;
        public float AnimatorSpeed;
    }

    private static readonly Dictionary<byte, State> _down = new();
    private static int _laid, _raised;

    public static void Tick()
    {
        try
        {
            foreach (var r in CoopSession.Remotes)
            {
                bool down = r.Ghost != null && CoopLife.IsRemoteDown(r.Slot);
                bool shown = _down.TryGetValue(r.Slot, out var st);
                if (down && !shown) LayDown(r);
                else if (!down && shown) Raise(r.Slot, st!);
                else if (down && shown && (st!.Model == null || ModelRoot(r) is { } m && m.Pointer != st.Model.Pointer))
                {
                    // The ghost was rebuilt while down: lay the new model down too.
                    _down.Remove(r.Slot);
                    LayDown(r);
                }
            }
            if (_down.Count > 0)
            {
                List<byte>? gone = null;
                foreach (var slot in _down.Keys) if (CoopSession.Get(slot) == null) (gone ??= new List<byte>()).Add(slot);
                if (gone != null) foreach (var s in gone) _down.Remove(s);
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] GhostDowned: {ex.Message}");
        }
    }

    private static Transform? ModelRoot(RemotePlayer r)
    {
        var ghost = r.Ghost;
        var a = r.ModelAnimator;
        if (ghost == null || a == null) return null;
        var t = a.transform;
        while (t.parent != null && t.parent.Pointer != ghost.transform.Pointer) t = t.parent;
        return t.parent != null ? t : null;
    }

    private static void LayDown(RemotePlayer r)
    {
        var model = ModelRoot(r);
        if (model == null) return;
        var st = new State { Model = model, Rotation = model.localRotation, Position = model.localPosition };
        model.localRotation = Quaternion.Euler(-90f, 0f, 0f) * st.Rotation;
        model.localPosition = st.Position + new Vector3(0f, 0.25f, 0f);
        if (r.ModelAnimator != null)
        {
            st.AnimatorSpeed = r.ModelAnimator.speed;
            r.ModelAnimator.speed = 0f;
        }
        _down[r.Slot] = st;
        _laid++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Ghost of slot {r.Slot} ({r.Name}) lies down.");
    }

    private static void Raise(byte slot, State st)
    {
        _down.Remove(slot);
        if (st.Model != null)
        {
            st.Model.localRotation = st.Rotation;
            st.Model.localPosition = st.Position;
        }
        var r = CoopSession.Get(slot);
        if (r?.ModelAnimator != null) r.ModelAnimator.speed = st.AnimatorSpeed > 0f ? st.AnimatorSpeed : 1f;
        _raised++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Ghost of slot {slot} gets up.");
    }

    /// <summary>Test bridge: which partner ghosts lie down here.</summary>
    internal static string TestInfo(string[] args) => $"lying=[{string.Join(",", _down.Keys)}] laid={_laid} raised={_raised}";
}
