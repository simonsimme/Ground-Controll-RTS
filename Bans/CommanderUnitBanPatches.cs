using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderUnitBanPatches
{
    /// <summary>
    /// Every aircraft spawn out of a hangar asks this first: a player's spawn menu, the mission's own
    /// AI deployment and both commanders' launches. Ground units are held at the depot door,
    /// <c>VehicleDepot.TrySpawnVehicle</c>, in <see cref="CommanderFactionVehiclePatches"/>.
    /// </summary>
    [HarmonyPatch(typeof(Hangar), nameof(Hangar.CanSpawnAircraft))]
    [HarmonyPostfix]
    private static void CanSpawnAircraftPostfix(AircraftDefinition definition, ref bool __result)
    {
        if (__result && CommanderUnitBanService.Holds(definition))
        {
            __result = false;
        }
    }
}
