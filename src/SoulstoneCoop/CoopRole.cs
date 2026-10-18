using SoulstoneCoop.Net;

namespace SoulstoneCoop;

public static class CoopRole
{
    /// <summary>True on the joining side. Also true before the connection is up (launch arg), since
    /// the match - and its spawners - can start before the client has finished connecting.</summary>
    public static bool IsClientSide => NetworkManager.Role == NetRole.Client || Plugin.ConnectAddressArg != null;

    public static bool IsHostSide => NetworkManager.Role == NetRole.Host;
}
