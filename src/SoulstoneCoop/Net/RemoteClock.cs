namespace SoulstoneCoop.Net;

/// <summary>
/// Phase 8: maps the sender's clock (ms since its game started, carried by PlayerState and EnemySnapshot) onto
/// ours, so a sample is placed by when it was SENT instead of when it arrived. With jitter, arrival times bunch
/// up and spread out, which made the ghost speed up and slow down and gave puppets wrong velocities. The offset
/// follows the fastest transit seen: it drops at once to a faster one and creeps up slowly, which absorbs a
/// latency increase and the drift between the two clocks. A mapped time is never later than the arrival.
/// Only used with [Coop] SenderClock = true (see docs/fase8-notas.md). ToLocal is pure math (no Unity).
/// </summary>
public sealed class RemoteClock
{
    /// <summary>Fraction of the gap closed per sample when transit got slower (20 Hz: ~2.5 s time constant).</summary>
    private const double RisePerSample = 0.02;

    private double _offset; // our seconds - sender seconds, at the fastest transit seen
    private double _lastRemote;
    private bool _has;

    public void Reset() => _has = false;

    /// <param name="remoteMs">The sender's clock in ms (u32, wraps after 49 days).</param>
    /// <param name="localNow">Our clock, in seconds.</param>
    /// <returns>When the sample was sent, on our clock (seconds).</returns>
    public double ToLocal(uint remoteMs, double localNow)
    {
        double remote = remoteMs / 1000.0;
        // The sender restarted (new game) or a wrap: start over instead of trusting a wild offset.
        if (_has && (remote < _lastRemote - 5.0 || remote > _lastRemote + 600.0)) _has = false;
        _lastRemote = remote;
        double o = localNow - remote;
        if (!_has || o < _offset)
        {
            _offset = o;
            _has = true;
        }
        else _offset += (o - _offset) * RisePerSample;
        return remote + _offset;
    }

    /// <summary>Our clock as u32 ms, to send.</summary>
    public static uint NowMs() => (uint)(UnityEngine.Time.realtimeSinceStartupAsDouble * 1000.0);
}
