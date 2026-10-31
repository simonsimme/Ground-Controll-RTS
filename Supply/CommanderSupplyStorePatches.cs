using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// The Harmony adapter for supply stores: one postfix that turns the base game's silent rearm
/// shortfall into something the player is told about.
/// </summary>
/// <remarks>
/// A ground vehicle that drives to an empty ammo depot already does the right thing in the base
/// game — it parks there and gets nothing — but nothing on screen says why, so it reads as a stuck
/// unit. <c>Rearmer.ProcessRearmRequest</c> is the one place that shortfall is known, for the
/// player's vehicles and the enemy's alike, so the alert hangs off it rather than off a poll.
/// </remarks>
[HarmonyPatch]
internal static class CommanderSupplyStorePatches
{
    /// <summary>
    /// A munitions truck holds <see cref="CommanderSupplyStoreService.MunitionsTruckAmmoKg"/>. The
    /// shelf size is the private <c>maxCapacity</c>, copied from <c>Capacity</c> in this same
    /// <c>Awake</c>, so both are written here, on every client alike.
    /// </summary>
    [HarmonyPatch(typeof(Rearmer), "Awake")]
    [HarmonyPostfix]
    private static void AwakePostfix(Rearmer __instance, ref float ___maxCapacity, bool ___singleUse)
    {
        if (___singleUse && __instance.Unit is Building)
        {
            CommanderSupplyStoreService.DumpRearmers.Add(__instance);
        }

        if (__instance.Unit is GroundVehicle)
        {
            ___maxCapacity = CommanderSupplyStoreService.MunitionsTruckAmmoKg;
            __instance.Capacity = CommanderSupplyStoreService.MunitionsTruckAmmoKg;
        }
    }

    [HarmonyPatch(typeof(Rearmer), nameof(Rearmer.ProcessRearmRequest))]
    [HarmonyPrefix]
    private static void ProcessRearmRequestPrefix(Rearmer __instance, out float __state, ref bool ___singleUse)
    {
        __state = __instance.Capacity;
        KeepDump(__instance, ref ___singleUse);
    }

    /// <summary>
    /// An ammo dump is a <c>singleUse</c> rearmer: the base game disables it the moment a rearm
    /// leaves it empty, which is how a truck driving up to an empty dump deleted it. Under the
    /// mod's store economy a dump is refilled by trucks, so it must survive being empty. The flag
    /// is only read synchronously inside the two draw methods, so it is lowered for the call and
    /// put back in the postfix, which keeps stock missions stock.
    /// </summary>
    private static void KeepDump(Rearmer rearmer, ref bool singleUse)
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled && CommanderSupplyStoreService.DumpRearmers.Contains(rearmer))
        {
            singleUse = false;
        }
    }

    private static void RestoreDump(Rearmer rearmer, ref bool singleUse)
    {
        if (CommanderSupplyStoreService.DumpRearmers.Contains(rearmer))
        {
            singleUse = true;
        }
    }

    [HarmonyPatch(typeof(Rearmer), nameof(Rearmer.RefillOtherRearmer))]
    [HarmonyPrefix]
    private static void RefillOtherRearmerPrefix(Rearmer __instance, ref bool ___singleUse)
    {
        KeepDump(__instance, ref ___singleUse);
    }

    [HarmonyPatch(typeof(Rearmer), nameof(Rearmer.RefillOtherRearmer))]
    [HarmonyPostfix]
    private static void RefillOtherRearmerPostfix(Rearmer __instance, ref bool ___singleUse)
    {
        RestoreDump(__instance, ref ___singleUse);
    }

    [HarmonyPatch(typeof(Rearmer), nameof(Rearmer.ProcessRearmRequest))]
    [HarmonyPostfix]
    private static void ProcessRearmRequestPostfix(Rearmer __instance, Unit unitToRearm, int shortfall, float __state, ref bool ___singleUse)
    {
        RestoreDump(__instance, ref ___singleUse);
        if (unitToRearm != null)
        {
            CommanderSupplyStoreService.ChargeArtilleryRefill(__instance, unitToRearm, __state);
            if (CommanderFeatureGate.AdvancedFeaturesEnabled)
            {
                CommanderSupplyStoreService.TopUpWeapons(__instance, unitToRearm);
            }
        }

        if (shortfall <= 0 || unitToRearm == null || __instance.Unit == null)
        {
            return;
        }

        CommanderAlertService.Instance?.NotifySupply(
            $"{CommanderGameAccess.GetUnitLabel(__instance.Unit).ToUpperInvariant()} OUT OF AMMUNITION - "
                + $"{CommanderGameAccess.GetUnitLabel(unitToRearm).ToUpperInvariant()} HELD AT BASE",
            unitToRearm);
    }
}
