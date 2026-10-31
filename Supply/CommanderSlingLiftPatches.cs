using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderSlingLiftPatches
{
    /// <summary>
    /// A helicopter on a sling lift is flown by <see cref="CommanderSlingLiftService"/>. The whole
    /// state update is skipped on purpose: its <c>RunCombatModes</c> writes its own destination, and
    /// <c>AssessHQTargets</c> would hand a helicopter with a loaded cargo bay to the transport state.
    /// </summary>
    [HarmonyPatch(typeof(AIHeloCombatState), nameof(AIHeloCombatState.FixedUpdateState))]
    [HarmonyPrefix]
    private static bool FixedUpdateStatePrefix(AIHeloCombatState __instance)
    {
        return !CommanderSlingLiftService.TryFly(__instance);
    }

    /// <summary>
    /// The hook parts its own rope (snag, angle or force, <c>SlingloadHook.ConnectedState</c>) and
    /// says nothing about which, and a lift that ends in "lost its load" reads the same whichever it
    /// was. Logged once per break, which is rare, so the check costs nothing.
    /// </summary>
    [HarmonyPatch(typeof(SlingloadHook), "BreakRope")]
    [HarmonyPrefix]
    private static bool BreakRopePrefix(SlingloadHook __instance, Unit ___suspendedUnit, float ___breakForce, float ___breakAngle)
    {
        return CommanderSlingLiftService.AllowRopeBreak(__instance, ___suspendedUnit, ___breakForce, ___breakAngle);
    }
}
