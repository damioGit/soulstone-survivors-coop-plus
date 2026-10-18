using System.Runtime.InteropServices;
using System.Text;

namespace SoulstoneCoop.Steam;

/// <summary>
/// Phase 7: the Steamworks flat API (SDK 1.53a) called straight into the game's own steam_api64.dll, which the
/// game has already loaded and initialized (SteamManager.Init). The game's Steamworks.NET wrappers for
/// matchmaking/networking were stripped from this build (docs/fase7-investigacao.md), so this is the only way in.
/// Struct offsets are for Windows x64 (callback pack 8).
/// </summary>
internal static unsafe class SteamNative
{
    private const string Dll = "steam_api64";
    private const CallingConvention Cc = CallingConvention.Cdecl;

    // ---------------- callback ids (k_iCallback) ----------------
    public const int GameLobbyJoinRequested = 333;        // CSteamID lobby @0, CSteamID friend @8
    public const int GameRichPresenceJoinRequested = 337; // CSteamID friend @0, char connect[256] @8
    public const int LobbyEnter = 504;                    // u64 lobby @0, u32 perms @8, bool locked @12, u32 response @16
    public const int LobbyDataUpdate = 505;               // u64 lobby @0, u64 member @8, u8 success @16
    public const int LobbyChatUpdate = 506;               // u64 lobby @0, u64 changed @8, u64 makingChange @16, u32 state @24
    public const int LobbyKicked = 512;                   // u64 lobby @0, u64 admin @8, u8 disconnected @16
    public const int LobbyCreated = 513;                  // EResult @0, u64 lobby @8
    public const int SteamAPICallCompleted = 703;         // u64 call @0, i32 callback @8, u32 size @12
    public const int SessionRequest = 1251;               // SteamNetworkingIdentity @0
    public const int SessionFailed = 1252;                // SteamNetConnectionInfo_t @0 (identity first)

    // EChatMemberStateChange
    public const uint ChatEntered = 0x1, ChatLeft = 0x2, ChatDisconnected = 0x4, ChatKicked = 0x8, ChatBanned = 0x10;

    // ELobbyType
    public const int LobbyFriendsOnly = 1;

    // Send flags
    public const int SendUnreliableNoNagle = 1;
    public const int SendReliable = 8;

    // ESteamNetworkingConnectionState
    public const int ConnNone = 0, ConnConnecting = 1, ConnFindingRoute = 2, ConnConnected = 3,
        ConnClosedByPeer = 4, ConnProblemDetectedLocally = 5;

    // ---------------- dispatch ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct CallbackMsg
    {
        public int SteamUser;
        public int Callback;
        public IntPtr Param;
        public int ParamSize;
    }

    [DllImport(Dll, CallingConvention = Cc)] public static extern int SteamAPI_GetHSteamPipe();
    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_ManualDispatch_RunFrame(int pipe);
    [DllImport(Dll, CallingConvention = Cc)] public static extern byte SteamAPI_ManualDispatch_GetNextCallback(int pipe, CallbackMsg* msg);
    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_ManualDispatch_FreeLastCallback(int pipe);
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern byte SteamAPI_ManualDispatch_GetAPICallResult(int pipe, ulong call, IntPtr buffer, int size, int expectedCallback, byte* failed);

    // ---------------- interfaces ----------------
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_SteamUser_v021();
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_SteamFriends_v017();
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_SteamUtils_v010();
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_SteamNetworkingSockets_SteamAPI_v012();

    // ---------------- user / friends / utils ----------------
    [DllImport(Dll, CallingConvention = Cc)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong friend);
    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong lobby);
    [DllImport(Dll, CallingConvention = Cc)] public static extern byte SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, byte* key, byte* value);
    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_ISteamFriends_ClearRichPresence(IntPtr self);
    [DllImport(Dll, CallingConvention = Cc)] public static extern byte SteamAPI_ISteamUtils_IsOverlayEnabled(IntPtr self);

    // ---------------- matchmaking ----------------
    [DllImport(Dll, CallingConvention = Cc)] public static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int type, int maxMembers);
    [DllImport(Dll, CallingConvention = Cc)] public static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
    [DllImport(Dll, CallingConvention = Cc)] public static extern byte SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, byte* key, byte* value);
    [DllImport(Dll, CallingConvention = Cc)] public static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, byte* key);
    [DllImport(Dll, CallingConvention = Cc)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);
    [DllImport(Dll, CallingConvention = Cc)] public static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
    [DllImport(Dll, CallingConvention = Cc)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int index);
    [DllImport(Dll, CallingConvention = Cc)] public static extern byte SteamAPI_ISteamMatchmaking_SetLobbyJoinable(IntPtr self, ulong lobby, byte joinable);
    [DllImport(Dll, CallingConvention = Cc)] public static extern byte SteamAPI_ISteamMatchmaking_SetLobbyMemberLimit(IntPtr self, ulong lobby, int max);

    // ---------------- networking ----------------
    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
    [DllImport(Dll, CallingConvention = Cc)] public static extern int SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(IntPtr self, IntPtr details);

    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_SteamNetworkingIdentity_Clear(byte* identity);
    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(byte* identity, ulong steamId);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, byte* identity, byte* data, uint size, int flags, int channel);
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* messages, int max);
    [DllImport(Dll, CallingConvention = Cc)] public static extern byte SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, byte* identity);
    [DllImport(Dll, CallingConvention = Cc)] public static extern byte SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, byte* identity);
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(IntPtr self, byte* identity, IntPtr info, IntPtr quickStatus);
    [DllImport(Dll, CallingConvention = Cc)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);

    // Loopback self-test only (SteamTransport.SelfTest).
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern byte SteamAPI_ISteamNetworkingSockets_CreateSocketPair(IntPtr self, uint* conn1, uint* conn2, byte useNetworkLoopback, byte* identity1, byte* identity2);
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(IntPtr self, uint conn, byte* data, uint size, int flags, long* messageNumber);
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(IntPtr self, uint conn, IntPtr* messages, int max);
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern byte SteamAPI_ISteamNetworkingSockets_CloseConnection(IntPtr self, uint conn, int reason, byte* debug, byte linger);

    // ---------------- SteamNetworkingMessage_t (Windows x64) ----------------
    public const int MsgData = 0;       // void*
    public const int MsgSize = 8;       // int
    public const int MsgPeer = 16;      // SteamNetworkingIdentity (type i32 @16, size i32 @20, steamID64 @24)
    public const int MsgChannel = 192;  // int

    public const int IdentitySize = 136;
    public const int IdentityTypeSteamId = 16;

    /// <summary>The SteamID64 inside a SteamNetworkingIdentity, 0 if it is not a SteamID.</summary>
    public static ulong IdentitySteamId(byte* identity) =>
        *(int*)identity == IdentityTypeSteamId ? *(ulong*)(identity + 8) : 0UL;

    public static void MakeIdentity(byte* buffer, ulong steamId)
    {
        SteamAPI_SteamNetworkingIdentity_Clear(buffer);
        SteamAPI_SteamNetworkingIdentity_SetSteamID64(buffer, steamId);
    }

    // ---------------- strings ----------------
    public static byte[] Utf8(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    public static string? FromUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
