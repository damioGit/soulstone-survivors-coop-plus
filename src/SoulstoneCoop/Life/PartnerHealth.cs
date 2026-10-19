using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Life;

/// <summary>
/// Phase 5: each player's own health for the overhead bar the others draw above its ghost (GhostOverhead). The host
/// never sent its health (ClientState goes client -> host only, and the PlayerState/snapshot layouts are left alone
/// for phase 8), so every side sends this small packet with the same layout: type u8 | hp f32 | maxHp f32 | flags u8
/// (1 = down, 2 = dead). It goes out at most 5 times a second, only when something changed, plus once a second as a
/// refresher. Phase 9: one entry per other player (PacketRouter.From); the host relays each client's to the others.
/// </summary>
public static class PartnerHealth
{
    private const float MinInterval = 0.2f;   // ~5 Hz while it changes
    private const float RefreshInterval = 1f;
    private const byte FlagDown = 1, FlagDead = 2;

    /// <summary>What we know of one other player's health.</summary>
    public sealed class View
    {
        public bool Known;
        public float Health, MaxHealth;
        public bool Down, Dead;
        public float Fraction => MaxHealth > 0f ? Mathf.Clamp01(Health / MaxHealth) : 0f;
    }

    private static readonly View Unknown = new();
    private static readonly Dictionary<byte, View> Views = new();

    /// <summary>That player's last health (an empty, unknown view if nothing came yet).</summary>
    public static View Of(byte slot) => Views.TryGetValue(slot, out var v) ? v : Unknown;

    private static float _sinceSend;
    private static float _lastHp = -1f, _lastMax = -1f;
    private static byte _lastFlags = 255;
    private static bool _hooked;
    private static int _sent, _recv;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerConnected += _ => ResendOwn(); // a newcomer gets ours right away
        Players.Left += slot => Views.Remove(slot);
    }

    /// <summary>A new ghost of that player for a new run: unknown until its next packet (its 1 s refresh), and ours goes out now.</summary>
    public static void Reset(byte slot)
    {
        Views.Remove(slot);
        ResendOwn();
    }

    private static void ResendOwn()
    {
        _lastHp = _lastMax = -1f;
        _lastFlags = 255;
        _sinceSend = RefreshInterval;
    }

    public static void Tick(float unscaledDeltaTime)
    {
        try
        {
            if (!NetworkManager.HasPeer || !Lobby.CoopSession.LocalInRun) return;
            var local = GameState.LocalPlayerEntity;
            var h = local != null ? local._health : null;
            var st = local != null ? local._stats : null;
            if (h == null || st == null) return;

            _sinceSend += unscaledDeltaTime;
            if (_sinceSend < MinInterval) return;
            float hp = Mathf.Max(0f, h.CurrentHealth), max = st.GetMaxHealth();
            byte flags = (byte)((CoopLife.LocalDowned ? FlagDown : 0) | (h.HasBeenKilled ? FlagDead : 0));
            bool changed = flags != _lastFlags || Mathf.Abs(hp - _lastHp) >= 0.5f || Mathf.Abs(max - _lastMax) >= 0.5f;
            if (!changed && _sinceSend < RefreshInterval) return;

            var w = new NetDataWriter();
            w.Put((byte)PacketType.PartnerHealth);
            w.Put(hp);
            w.Put(max);
            w.Put(flags);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            _sinceSend = 0f;
            _lastHp = hp;
            _lastMax = max;
            _lastFlags = flags;
            _sent++;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][hpbar] PartnerHealth.Tick: {ex.Message}");
        }
    }

    public static void OnPacket(NetDataReader r)
    {
        float hp = r.GetFloat(), max = r.GetFloat();
        byte flags = r.GetByte();
        byte slot = PacketRouter.From;
        if (!Players.IsPresent(slot)) return;
        if (!Views.TryGetValue(slot, out var v)) Views[slot] = v = new View();
        bool first = !v.Known;
        v.Known = true;
        v.Health = hp;
        v.MaxHealth = max;
        v.Down = (flags & FlagDown) != 0;
        v.Dead = (flags & FlagDead) != 0;
        _recv++;
        if (first) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] First health of slot {slot}: {hp:F0}/{max:F0} down={v.Down} dead={v.Dead}.");
    }

    /// <summary>For the 5 s telemetry line of GhostOverhead.</summary>
    internal static string TakeStats()
    {
        string s = $"sent={_sent} recv={_recv}";
        _sent = _recv = 0;
        return s;
    }
}
