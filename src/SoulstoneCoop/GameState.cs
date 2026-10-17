using UnityEngine;

namespace SoulstoneCoop;

/// <summary>Holds references captured from Harmony patches, read by our own Update loop.</summary>
public static class GameState
{
    public static Entity? LocalPlayerEntity;

    /// <summary>The visual stand-in for the other side's player (ghost clone), if any. Plain
    /// GameObject, not an Entity - see VisualGhostFactory for why.</summary>
    public static GameObject? RemoteGhostObject;
}
