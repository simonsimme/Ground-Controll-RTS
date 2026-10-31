using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// Moves a shared radar to its new owner's list when its unit changes hands. A <c>Radar</c> calls
/// <c>FactionHQ.RegisterRadar</c> once, on initialize, and nothing moves it afterwards, so a
/// captured radar station went on serving the faction that lost it: the new owner's StratoLances
/// (whose <c>Turret.SearchForRadar</c> asks its own HQ's list for a radar within 300 m) stood
/// beside it and never fired, while the old owner's launchers could still use it.
/// </summary>
[HarmonyPatch(typeof(Unit), nameof(Unit.HQChanged))]
internal static class CommanderRadarOwnerPatches
{
    private static readonly AccessTools.FieldRef<TargetDetector, bool> Shared = AccessTools.FieldRefAccess<TargetDetector, bool>("shared");
    private static readonly AccessTools.FieldRef<TargetDetector, bool> Disabled = AccessTools.FieldRefAccess<TargetDetector, bool>("disabled");

    [HarmonyPostfix]
    internal static void Postfix(Unit __instance, FactionHQ oldHQ, FactionHQ newHQ)
    {
        if (__instance == null || __instance.disabled || ReferenceEquals(oldHQ, newHQ))
        {
            return;
        }

        Radar[] radars = __instance.GetComponentsInChildren<Radar>(true);
        for (int i = 0; i < radars.Length; i++)
        {
            Radar radar = radars[i];
            if (radar == null || !Shared(radar) || Disabled(radar))
            {
                continue;
            }

            oldHQ?.DeregisterRadar(radar);

            // Deregister first so a second hook call on the same owner never lists it twice.
            newHQ?.DeregisterRadar(radar);
            newHQ?.RegisterRadar(radar);
        }
    }
}
