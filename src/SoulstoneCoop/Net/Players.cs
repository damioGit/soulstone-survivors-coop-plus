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

    /// <summary>How many players the session holds (host: [Coop] MaxPlayers; client: what the Welcome said).</summary>
    public static int SessionMax => NetworkManager.Role == NetRole.Client && _welcomeMax > 0 ? _welcomeMax : NetworkManager.MaxPlayers;
    private static int _welcomeMax;
    internal static void SetSessionMax(int max) => _welcomeMax = max;

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
        Fire(Appeared, slot, "Appeared");
        Fire(Joined, slot, "Joined");
    }

    /// <summary>Before every Joined handler: CoopSession creates the slot's RemotePlayer.</summary>
    internal static event Action<byte>? Appeared;

    internal static void MarkLeft(byte slot)
    {
        if (slot >= MaxSlots || !Present[slot]) return;
        Present[slot] = false;
        OthersList.Remove(slot);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][players] Slot {slot} left ({Count} in the session).");
        Fire(Left, slot, "Left");
        Fire(Forgotten, slot, "Forgotten");
    }

    /// <summary>After every Left handler ran: CoopSession drops the slot's RemotePlayer (the modules may still read
    /// its name and ghost while they clean up).</summary>
    internal static event Action<byte>? Forgotten;

    private static void Fire(Action<byte>? ev, byte slot, string what)
    {
        foreach (var h in ev?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            // One failing module must not keep the others from cleaning up that slot.
            try { ((Action<byte>)h)(slot); }
            catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][players] {what}({slot}) handler: {ex}"); }
        }
    }

    /// <summary>Out of co-op (or the host is gone, for a client): everybody leaves, and our slot goes back to 0.</summary>
    internal static void ClearAll()
    {
        for (int i = OthersList.Count - 1; i >= 0; i--) MarkLeft(OthersList[i]);
        MySlot = HostSlot;
    }
}
