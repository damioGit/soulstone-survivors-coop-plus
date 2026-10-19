using UnityEngine;

namespace SoulstoneCoop;

/// <summary>Holds references captured from Harmony patches, read by our own Update loop.</summary>
public static class GameState
{
    public static Entity? LocalPlayerEntity;

}
