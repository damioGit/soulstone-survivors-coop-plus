using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Life;

/// <summary>
/// Phase 5: each side's own health for the partner's overhead bar (GhostOverhead). The host never sent its health
/// (ClientState goes client -> host only, and the PlayerState/snapshot layouts are left alone for phase 8), so both
/// sides send this small packet with the same layout: type u8 | hp f32 | maxHp f32 | flags u8 (1 = down, 2 = dead).
/// It goes out at most 5 times a second, only when something changed, plus once a second as a refresher.
/// </summary>
public static class PartnerHealth
{
    private const float MinInterval = 0.2f;   // ~5 Hz while it changes
    private const float RefreshInterval = 1f;
    private const byte FlagDown = 1, FlagDead = 2;

    public static bool Known { get; private set; }
    public static float Health { get; private set; }
    public static float MaxHealth { get; private set; }
    public static bool Down { get; private set; }
    public static bool Dead { get; private set; }
    public static float Fraction => MaxHealth > 0f ? Mathf.Clamp01(Health / MaxHealth) : 0f;

    private static float _sinceSend;
    private static float _lastHp = -1f, _lastMax = -1f;
    private static byte _lastFlags = 255;
    private static bool _hooked;
    private static int _sent, _recv;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerConnected += _ => Reset();
        NetworkManager.PeerDisconnected += _ => Reset();
    }

    /// <summary>New partner or new run: nothing known until their first packet, and ours goes out right away.</summary>
    public static void Reset()
    {
        Known = false;
        Health = MaxHealth = 0f;
        Down = Dead = false;
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
        bool first = !Known;
        Known = true;
        Health = hp;
        MaxHealth = max;
        Down = (flags & FlagDown) != 0;
        Dead = (flags & FlagDead) != 0;
        _recv++;
        if (first) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] First partner health: {hp:F0}/{max:F0} down={Down} dead={Dead}.");
    }

    /// <summary>For the 5 s telemetry line of GhostOverhead.</summary>
    internal static string TakeStats()
    {
        string s = $"sent={_sent} recv={_recv}";
        _sent = _recv = 0;
        return s;
    }
}
