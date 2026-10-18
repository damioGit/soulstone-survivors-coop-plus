using System.Runtime.InteropServices;

namespace SoulstoneCoop.Lobby;

/// <summary>
/// The player's Steam name. Steamworks.SteamFriends.GetPersonaName was stripped from this build's interop (only
/// two SteamFriends methods survived), so call the flat API in the game's own steam_api64.dll directly. The game
/// has already loaded and initialized it (SteamManager.Init); LoadLibrary resolves to that same module.
/// </summary>
internal static class SteamName
{
    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamFriends_v017();

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);

    private static string? _cached;
    private static bool _failed;

    public static string? TryGet()
    {
        if (_cached != null) return _cached;
        if (_failed) return null;
        try
        {
            if (!SteamManager.Initialized) return null; // maybe later
            var friends = SteamAPI_SteamFriends_v017();
            if (friends == IntPtr.Zero) return null;
            var p = SteamAPI_ISteamFriends_GetPersonaName(friends);
            var name = p != IntPtr.Zero ? Marshal.PtrToStringUTF8(p) : null;
            if (string.IsNullOrWhiteSpace(name)) return null;
            _cached = name.Trim();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Steam name: {_cached}");
            return _cached;
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][session] Steam name unavailable: {ex.Message}");
            return null;
        }
    }
}
