namespace SoulstoneCoop.Net;

/// <summary>
/// Phase 9: who is in the co-op session, by slot. Slot 0 is the host, 1-7 are clients; the host hands a client its slot
/// in the Welcome packet, right after the connection. The host stamps the origin of everything a client sends from the
/// connection it came on (PacketRouter.From), so clients never write their own slot into a packet.
///
/// "Others" are the players this side knows about besides itself: on the host, the connected clients; on a client,
/// the host plus the other clients listed in the Welcome (and later joins/PlayerLeft). Joined/Left fire for both
/// sides the same way, so the per-player modules (ghost, proxy, remote caster, health...) only hook these.
/// </summary>
public static class Players
{
    public const int MaxSlots = 8;
    public const byte HostSlot = 0;
    public const byte NoSlot = 255;

    /// <summary>Our own slot: 0 on the host (and solo), the Welcome's on a client (NoSlot until it arrives).</summary>
    public static byte MySlot { get; private set; } = HostSlot;

    private static readonly bool[] Present = new bool[MaxSlots];
    private static readonly List<byte> OthersList = new();

    /// <summary>Another player appeared (host: a client connected; client: the host, or a client the host told about).</summary>
    public static event Action<byte>? Joined;

    /// <summary>Another player is gone (disconnected, or the host told us). Per-player state for that slot must go.</summary>
    public static event Action<byte>? Left;

    /// <summary>Slots of the other players, in slot order. Don't keep the list: it changes on Joined/Left.</summary>
    public static IReadOnlyList<byte> Others => OthersList;

    public static int OtherCount => OthersList.Count;

    /// <summary>Everyone in the session, us included.</summary>
    public static int Count => OthersList.Count + 1;

    public static bool IsPresent(byte slot) => slot < MaxSlots && Present[slot];

    public static bool IsValid(byte slot) => slot < MaxSlots;

    internal static void SetMySlot(byte slot)
    {
        if (MySlot == slot) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][players] My slot is now {slot}.");
        MySlot = slot;
        if (slot < MaxSlots && Present[slot]) MarkLeft(slot); // never list ourselves among the others
    }

    internal static void MarkJoined(byte slot)
    {
        if (slot >= MaxSlots || slot == MySlot || Present[slot]) return;
        Present[slot] = true;
        OthersList.Add(slot);
        OthersList.Sort();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][players] Slot {slot} joined ({Count} in the session).");
        try { Joined?.Invoke(slot); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][players] Joined({slot}) handler: {ex}"); }
    }

    internal static void MarkLeft(byte slot)
    {
        if (slot >= MaxSlots || !Present[slot]) return;
        Present[slot] = false;
        OthersList.Remove(slot);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][players] Slot {slot} left ({Count} in the session).");
        foreach (var h in Left?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            // One failing module must not keep the others from cleaning up that slot.
            try { ((Action<byte>)h)(slot); }
            catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][players] Left({slot}) handler: {ex}"); }
        }
    }

    /// <summary>Out of co-op (or the host is gone, for a client): everybody leaves, and our slot goes back to 0.</summary>
    internal static void ClearAll()
    {
        for (int i = OthersList.Count - 1; i >= 0; i--) MarkLeft(OthersList[i]);
        MySlot = HostSlot;
    }
}
