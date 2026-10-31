using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderForwardBasePatches
{
    [HarmonyPatch(typeof(Airbase), "OnStartClientOnly")]
    [HarmonyPrefix]
    internal static void OnStartClientOnlyPrefix(Airbase __instance)
    {
        CommanderForwardBase.RestoreOnGuest(__instance);
    }
}
