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
/// the screen's top edge isn't cut, and so does the fog: the maps' linear fog (RenderSettings, ~65-75 m) and their
/// volumetric fog's max distance are measured from the camera, so the farther camera drowned the screen in fog.
/// Local only.
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

    // The fog distances the scene set (re-read whenever they aren't the ones we wrote: a new scene, the game itself).
    private static float _fogStart, _fogEnd, _fogWroteStart = float.NaN, _fogWroteEnd = float.NaN, _fogScale = 1f;
    private static readonly Dictionary<IntPtr, (float baseMax, float wrote)> _volumeFog = new();
    private static float _volumeTimer;

    private static void ApplyFog(float scale)
    {
        if (RenderSettings.fog)
        {
            float s = RenderSettings.fogStartDistance, e = RenderSettings.fogEndDistance;
            if (s != _fogWroteStart || e != _fogWroteEnd) { _fogStart = s; _fogEnd = e; }
            _fogWroteStart = _fogStart * scale;
            _fogWroteEnd = _fogEnd * scale;
            if (s != _fogWroteStart) RenderSettings.fogStartDistance = _fogWroteStart;
            if (e != _fogWroteEnd) RenderSettings.fogEndDistance = _fogWroteEnd;
        }
        // The volumetric fog's profile only needs a look now and then (and when the zoom changes).
        _volumeTimer -= Time.unscaledDeltaTime;
        if (_volumeTimer > 0f && scale == _fogScale) return;
        _volumeTimer = 1f;
        _fogScale = scale;
        var fogs = VolumetricFogAndMist2.VolumetricFog.volumetricFogs;
        if (fogs == null) return;
        foreach (var vf in fogs)
        {
            var pr = vf != null ? vf.profile : null;
            if (pr == null) continue;
            float max = pr.maxDistance;
            if (!_volumeFog.TryGetValue(pr.Pointer, out var known) || known.wrote != max) known = (max, max);
            float want = known.baseMax * scale;
            _volumeFog[pr.Pointer] = (known.baseMax, want);
            if (max == want) continue;
            pr.maxDistance = want;
            vf.UpdateMaterialProperties();
        }
    }

    internal static void Apply()
    {
        if (!GameManagerUtil.HasGameManager) return; // menus: no run camera (PlayerCamera throws)
        float extra = NetworkManager.HasPeer ? ZoomOut.Value / 100f : 0f;
        ApplyFog(1f + extra);
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
    private static int _errors;

    private static void Prefix()
    {
        try { CameraZoom.Restore(); } catch { }
    }

    private static void Postfix()
    {
        try { CameraZoom.Apply(); }
        catch (System.Exception ex) { if (_errors++ < 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] CameraZoom: {ex.Message}"); }
    }
}
