using System.Diagnostics;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// Frame time and what our ticks cost in it. CoopBehaviour.Update calls <see cref="Begin"/> first and
/// <see cref="Lap"/> after each tick, so each lap is that tick's time. Every 10 s in a run it logs the frame rate, the
/// worst frame, our share of the frame and the costliest ticks ([perf]); `cmd perf` gives the same as text.
/// </summary>
public static class Perf
{
    private const float LogInterval = 10f;

    private static readonly Dictionary<string, double> _ms = new();
    private static readonly Dictionary<string, double> _peak = new();
    private static long _last;
    private static double _modMs, _frameMsSum, _worstFrame;
    private static int _frames;
    private static float _timer;
    internal static string LastReport = "no data yet";

    public static void Begin()
    {
        float dt = Time.unscaledDeltaTime * 1000f;
        _frameMsSum += dt;
        if (dt > _worstFrame) _worstFrame = dt;
        _frames++;
        _last = Stopwatch.GetTimestamp();
    }

    public static void Lap(string name)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - _last) * 1000.0 / Stopwatch.Frequency;
        _last = now;
        _modMs += ms;
        _ms[name] = (_ms.TryGetValue(name, out var s) ? s : 0.0) + ms;
        if (!_peak.TryGetValue(name, out var p) || ms > p) _peak[name] = ms;
    }

    /// <summary>After the last lap of the frame.</summary>
    public static void End(float dt)
    {
        _timer += dt;
        if (_timer < LogInterval) return;
        _timer = 0f;
        if (_frames == 0) return;
        double avg = _frameMsSum / _frames;
        var top = _ms.OrderByDescending(kv => kv.Value).Take(6)
            .Select(kv => $"{kv.Key}={kv.Value / _frames:F2}ms(peak {_peak[kv.Key]:F1})");
        LastReport = $"fps={1000.0 / avg:F0} frame={avg:F1}ms worst={_worstFrame:F0}ms mod={_modMs / _frames:F2}ms/frame " +
            $"({_modMs / _frameMsSum * 100.0:F0}%) entities={EntityRegistry.AllEntities?.Count ?? 0} top: {string.Join(", ", top)}";
        if (GameManagerUtil.HasGameManager) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][perf] {LastReport}");
        _ms.Clear(); _peak.Clear();
        _modMs = _frameMsSum = _worstFrame = 0.0;
        _frames = 0;
    }
}
