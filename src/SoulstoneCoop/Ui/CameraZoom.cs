using BepInEx.Configuration;
using HarmonyLib;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Ui;

/// <summary>
/// Zooms the run camera out in co-op (with a partner connected), so the partners stay on screen longer: after the
/// game places its camera each frame (PlayerCameraController.UpdateAll), the camera is pulled back along its view by
/// [Coop] CameraZoomOut percent of its distance to the target, and put back before the next placement, so the game's
/// own follow, lerps and shake never see (or compound) the offset. The far clip plane grows with it so the ground at
/// the screen's top edge isn't cut. Local only.
/// </summary>
internal static class CameraZoom
{
    internal static ConfigEntry<float> ZoomOut = null!;

    internal static readonly float[] Steps = { 0f, 25f, 50f };

    internal static void Bind(ConfigFile cfg)
    {
        ZoomOut = cfg.Bind("Coop", "CameraZoomOut", 25f, new ConfigDescription(
            "In co-op runs, zoom the camera out by this many percent of its normal distance (25 = 1.25x as far). Your screen only. 0 = the game's own camera.",
            new AcceptableValueRange<float>(0f, 100f)));
    }

    private static Transform? _moved;      // the camera transform we pulled back last frame
    private static Vector3 _movedFrom;     // its local position before that
    private static Camera? _clipCam;
    private static float _baseFar;
    private static bool _logged;

    internal static void Restore()
    {
        if (_moved != null) _moved.localPosition = _movedFrom;
        _moved = null;
    }

    internal static void Apply()
    {
        float extra = NetworkManager.HasPeer ? ZoomOut.Value / 100f : 0f;
        var ctrl = GameManagerUtil.PlayerCamera;
        var cam = ctrl != null ? ctrl.Camera : null;
        if (cam == null) return;

        if (_clipCam == null || _clipCam.Pointer != cam.Pointer) { _clipCam = cam; _baseFar = cam.farClipPlane; }
        cam.farClipPlane = _baseFar * (1f + extra);
        if (extra <= 0f) return;

        var t = cam.transform;
        var target = ctrl!.Target != null ? ctrl.Target.position : t.position + t.forward * 20f;
        float dist = Vector3.Distance(t.position, target);
        if (dist < 0.01f) return;
        _moved = t;
        _movedFrom = t.localPosition;
        t.position -= t.forward * (dist * extra);
        if (!_logged)
        {
            _logged = true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Camera zoomed out {ZoomOut.Value:0}% (distance {dist:F1} -> {dist * (1f + extra):F1}, far {_baseFar:F0} -> {cam.farClipPlane:F0}).");
        }
    }
}

[HarmonyPatch(typeof(PlayerCameraController), nameof(PlayerCameraController.UpdateAll))]
internal static class CameraZoomPatch
{
    private static void Prefix()
    {
        try { CameraZoom.Restore(); } catch { }
    }

    private static void Postfix()
    {
        try { CameraZoom.Apply(); }
        catch (System.Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] CameraZoom: {ex.Message}"); }
    }
}
