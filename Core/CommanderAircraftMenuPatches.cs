using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// Remembers the aircraft selection menu when the game enables it. The launcher button asked
/// <c>FindObjectOfType</c> for it every 0.75 s whenever RTS mode was closed, on every client, and
/// that search walks every behaviour in the scene: its cost grows with the size of the battle.
/// </summary>
[HarmonyPatch(typeof(AircraftSelectionMenu), "OnEnable")]
internal static class CommanderAircraftMenuPatches
{
    internal static AircraftSelectionMenu? Current { get; private set; }

    /// <summary>The same question <c>FindObjectOfType</c> answered: one exists and is active.</summary>
    internal static bool IsOpen => Current != null && Current.isActiveAndEnabled;

    internal static void Postfix(AircraftSelectionMenu __instance)
    {
        Current = __instance;
    }
}
