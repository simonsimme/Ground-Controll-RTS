using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// A human side's munitions truck goes where it is ordered and stays there
/// (<see cref="CommanderRepairService.TamedTrucks"/>): no rearm mission, no drive back to restock.
/// Units still rearm from it by the base game's own request polling.
/// </summary>
[HarmonyPatch]
internal static class CommanderRearmTruckPatches
{
    [HarmonyPatch(typeof(RearmVehicleAI), nameof(RearmVehicleAI.AssignMission))]
    [HarmonyPatch(typeof(RearmVehicleAI), "RearmVehicleAI_OnRearmProcessed")]
    [HarmonyPrefix]
    internal static bool HoldTamedTruckPrefix(RearmVehicleAI __instance)
    {
        return CommanderRepairService.TamedTrucks.Count == 0 || !CommanderRepairService.TamedTrucks.Contains(__instance);
    }
}
