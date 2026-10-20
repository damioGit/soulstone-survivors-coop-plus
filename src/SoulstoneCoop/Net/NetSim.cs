using System.Diagnostics;

namespace SoulstoneCoop.Net;

/// <summary>
/// Phase 8: a simulated bad network, for testing on one PC (or on top of a real link). Off by default; turned on
/// by [Debug] SimulateLatencyMs / SimulateJitterMs / SimulatePacketLossPercent in the .cfg or by
/// --coop-sim-latency / --coop-sim-jitter / --coop-sim-loss on the command line. Each game only degrades ONE
/// direction (LiteNetLib: what this game receives; Steam: what this game sends), so turn it on in both games:
/// 75 ms in each is about 150 ms of ping. See docs/fase8-notas.md.
/// </summary>
public static class NetSim
{
    public static int LatencyMs { get; private set; }
    public static int JitterMs { get; private set; }
    public static int LossPercent { get; private set; }

    public static bool Enabled => LatencyMs > 0 || JitterMs > 0 || LossPercent > 0;

    /// <summary>Short description: English for the logs, the player's language for the panel.</summary>
    public static string Describe(bool local = false)
    {
        var args = new (string, object?)[] { ("lat", LatencyMs), ("jit", JitterMs), ("loss", LossPercent) };
        return local ? Ui.Loc.T("netsim.desc", args) : Ui.Loc.En("netsim.desc", args);
    }

    internal static void Configure(int latencyMs, int jitterMs, int lossPercent)
    {
        LatencyMs = Math.Clamp(latencyMs, 0, 2000);
        JitterMs = Math.Clamp(jitterMs, 0, 1000);
        LossPercent = Math.Clamp(lossPercent, 0, 50);
    }

    /// <summary>Loud on purpose: a simulation left on by mistake would look like a real network problem.</summary>
    internal static void LogBanner()
    {
        if (!Enabled) return;
        var log = Plugin.StaticLog;
        log.LogWarning("[SoulstoneCoop][netsim] ##########################################################");
        log.LogWarning($"[SoulstoneCoop][netsim] ### SIMULACAO DE REDE LIGADA: {Describe()}");
        log.LogWarning("[SoulstoneCoop][netsim] ### (each game degrades one direction; set it to 0 in the .cfg to turn it off)");
        log.LogWarning("[SoulstoneCoop][netsim] ##########################################################");
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    internal static double NowSeconds => Clock.Elapsed.TotalSeconds;
}

/// <summary>
/// Phase 8, Steam transport only (LiteNetLib has its own simulation): outgoing messages wait in a queue for the
/// simulated latency +- jitter. Unreliable messages are dropped with the loss chance and may overtake each other
/// (the receiver's sequence checks deal with that, as on a real network). Reliable messages are never dropped and
/// never reordered: a "lost" one costs a retransmission delay instead, and everything reliable behind it waits
/// (head-of-line blocking, like a real ordered channel).
/// </summary>
internal sealed class SimSendQueue
{
    private struct Item
    {
        public double Due;
        public byte[] Data;
        public int Length;
        public int Flags;
        public int Channel;
        public bool Reliable;
        public ulong To;
    }

    public delegate void SendNow(ulong to, byte[] data, int length, int flags, int channel);

    private readonly List<Item> _queue = new(); // sorted by Due
    private readonly System.Random _rng = new();
    private double _lastReliableDue;

    public int Queued => _queue.Count;
    public int Dropped { get; private set; }
    public int Retransmits { get; private set; }

    public void Enqueue(ulong to, byte[] data, int length, int flags, int channel, bool reliable)
    {
        double now = NetSim.NowSeconds;
        if (!reliable && NetSim.LossPercent > 0 && _rng.Next(100) < NetSim.LossPercent)
        {
            Dropped++;
            return;
        }

        double delay = OneWay();
        if (reliable)
        {
            // Each lost transmission costs about a round trip before the resend (never below 200 ms).
            double rto = Math.Max(0.2, 2.0 * (NetSim.LatencyMs + NetSim.JitterMs) / 1000.0);
            for (int tries = 0; tries < 3 && NetSim.LossPercent > 0 && _rng.Next(100) < NetSim.LossPercent; tries++)
            {
                delay += rto;
                Retransmits++;
            }
        }
        double due = now + delay;
        if (reliable)
        {
            if (due < _lastReliableDue) due = _lastReliableDue; // in order, behind the previous reliable one
            _lastReliableDue = due;
        }

        var copy = new byte[length];
        Buffer.BlockCopy(data, 0, copy, 0, length);
        var item = new Item { Due = due, Data = copy, Length = length, Flags = flags, Channel = channel, Reliable = reliable, To = to };
        int i = _queue.Count;
        while (i > 0 && _queue[i - 1].Due > due) i--; // stable: equal Due keeps the send order
        _queue.Insert(i, item);
    }

    /// <summary>Send everything that is due. Call once per frame.</summary>
    public void Pump(SendNow send)
    {
        if (_queue.Count == 0) return;
        double now = NetSim.NowSeconds;
        int n = 0;
        while (n < _queue.Count && _queue[n].Due <= now) n++;
        if (n == 0) return;
        // Take them out first: the send callback may enqueue again.
        var due = _queue.GetRange(0, n);
        _queue.RemoveRange(0, n);
        foreach (var it in due) send(it.To, it.Data, it.Length, it.Flags, it.Channel);
    }

    /// <summary>Leaving: reliable messages still waiting go out now (a RunEnd must not be lost in our own queue);
    /// unreliable ones are dropped.</summary>
    public void FlushReliable(SendNow send)
    {
        var items = _queue.ToArray();
        _queue.Clear();
        foreach (var it in items)
            if (it.Reliable) send(it.To, it.Data, it.Length, it.Flags, it.Channel);
    }

    public void Clear() => _queue.Clear();

    private double OneWay()
    {
        double ms = NetSim.LatencyMs;
        if (NetSim.JitterMs > 0) ms += (_rng.NextDouble() * 2.0 - 1.0) * NetSim.JitterMs;
        return Math.Max(0.0, ms) / 1000.0;
    }
}
