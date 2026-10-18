using SoulstoneCoop.Net;

namespace SoulstoneCoop;

public static class CoopRole
{
    /// <summary>True on the joining side: while connecting or connected as the client (the connection is made in
    /// the menu, before any run - phase 6), and for the rest of a run started as the client even if the
    /// connection drops, since that run's spawners were stopped.</summary>
    public static bool IsClientSide => NetworkManager.Role == NetRole.Client || Lobby.CoopSession.ClientRunLatched;

    public static bool IsHostSide => NetworkManager.Role == NetRole.Host;
}
