using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderRecoveryPatches
{
    /// <summary>
    /// The hook's pilot rescue ends here (<c>SlingloadHook.RescuePilot</c>), and so does the pilot:
    /// this is the one moment to write down that a recovery helicopter has him aboard.
    /// </summary>
    [HarmonyPatch(typeof(PilotDismounted), nameof(PilotDismounted.Capture))]
    [HarmonyPrefix]
    private static void CapturePrefix(PilotDismounted __instance, Unit capturingUnit)
    {
        CommanderRecoveryService.NotePilotCaptured(__instance, capturingUnit);
    }
}
