using HarmonyLib;

namespace GroundControlRts;

internal static class CommanderMobileEmplacementPatches
{
    [HarmonyPatch(typeof(UnitCommand), nameof(UnitCommand.SetDestination))]
    internal static class CommanderMobileEmplacementDestinationPatch
    {
        private static bool Prefix(UnitCommand __instance)
        {
            return !CommanderMobileEmplacementService.ShouldBlockDestination(__instance)
                && !CommanderSamSiteService.ShouldBlockConstructionDestination(__instance);
        }
    }
}
