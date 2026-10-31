using System;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderSupplyHeliPatches
{
    [HarmonyPatch(typeof(CameraStateManager), "LateUpdate")]
    [HarmonyPostfix]
    private static void CameraLateUpdatePostfix(CameraStateManager __instance)
    {
        CommanderCameraFollowService.ApplyCommanderLatePose(__instance);
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RegisterFactionUnit))]
    [HarmonyPostfix]
    private static void RegisterFactionUnitPostfix(FactionHQ __instance, Unit unit)
    {
        CommanderSupplyHeliService.NotifyFactionUnitRegistered(__instance, unit);
    }

    [HarmonyPatch(typeof(AIHeloTransportState), "SearchForLandingSpot")]
    [HarmonyPrefix]
    private static bool SearchForLandingSpotPrefix(AIHeloTransportState __instance)
    {
        return !CommanderSupplyHeliService.TryOverrideTransportTarget(__instance);
    }

    [HarmonyPatch(typeof(AIHeloTransportState), nameof(AIHeloTransportState.FixedUpdateState))]
    [HarmonyPrefix]
    private static void TransportFixedUpdatePrefix(AIHeloTransportState __instance)
    {
        CommanderSupplyHeliService.TryOverrideTransportTarget(__instance);
        CommanderSupplyHeliService.TickHoverUnload(__instance);
    }

    [HarmonyPatch(typeof(AIHeloTransportState), "EjectionCheck")]
    [HarmonyPrefix]
    private static bool EjectionCheckPrefix(AIHeloTransportState __instance)
    {
        return !CommanderSupplyHeliService.ShouldSuppressAssignedEjection(__instance);
    }

    [HarmonyPatch(typeof(AIHeloTransportState), nameof(AIHeloTransportState.LeaveState))]
    [HarmonyPostfix]
    private static void LeaveStatePostfix(AIHeloTransportState __instance)
    {
        CommanderSupplyHeliService.NotifyTransportStateLeft(__instance);
    }

    [HarmonyPatch(typeof(AIHeloTransportState), "DeployCargo")]
    [HarmonyPrefix]
    private static bool DeployCargoPrefix(AIHeloTransportState __instance)
    {
        return !CommanderSupplyHeliService.TryDeployAssignedCargo(__instance);
    }

    [HarmonyPatch(typeof(Pilot), nameof(Pilot.SwitchState))]
    [HarmonyPrefix]
    private static bool SwitchStatePrefix(Pilot __instance, PilotBaseState state)
    {
        return !CommanderSupplyHeliService.ShouldDelayCargoTakeoff(__instance, state);
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.ReturnToInventory))]
    [HarmonyPostfix]
    private static void ReturnToInventoryPostfix(Aircraft __instance)
    {
        CommanderSupplyHeliService.NotifyAircraftReturned(__instance);
    }

    [HarmonyPatch(typeof(AIHeloLandingState), nameof(AIHeloLandingState.EnterState))]
    [HarmonyPostfix]
    private static void HeloLandingEnterStatePostfix(AIHeloLandingState __instance)
    {
        CommanderSupplyHeliService.TryOverrideAssignedReturnAirbase(__instance);
    }

    [HarmonyPatch(typeof(AIHeloCombatState), nameof(AIHeloCombatState.EnterState))]
    [HarmonyPostfix]
    private static void HeloCombatEnterStatePostfix(AIHeloCombatState __instance)
    {
        CommanderSupplyHeliService.TryOverrideAssignedNearestAirbase(__instance);
    }

    [HarmonyPatch(typeof(PilotBaseState), "FindNearestAirbase")]
    [HarmonyPostfix]
    private static void FindNearestAirbasePostfix(PilotBaseState __instance)
    {
        CommanderSupplyHeliService.TryOverrideAssignedNearestAirbase(__instance);
    }

    [HarmonyPatch(typeof(MountedCargo), nameof(MountedCargo.ActivateCargoVehicle))]
    [HarmonyPostfix]
    private static void ActivateCargoVehiclePostfix(
        MountedCargo __instance,
        Unit cargoUnit)
    {
        if (__instance.attachedUnit is Aircraft aircraft && cargoUnit != null)
        {
            CommanderSupplyHeliService.NotifyCargoActivated(aircraft, cargoUnit);
        }
    }

    [HarmonyPatch(
        typeof(AutopilotHelo),
        nameof(AutopilotHelo.AutoAim),
        new Type[] { typeof(GlobalPosition), typeof(float), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    [HarmonyPrefix]
    private static void HeloAutoAimPrefix(
        AutopilotHelo __instance,
        GlobalPosition destination,
        ref float altitudeHold,
        bool followTerrain)
    {
        CommanderSupplyHeliService.PrepareAssignedTerrainFlight(
            __instance,
            destination,
            ref altitudeHold,
            followTerrain);
    }

    [HarmonyPatch(
        typeof(AutopilotTiltwing),
        nameof(AutopilotTiltwing.AutoAim),
        new Type[] { typeof(GlobalPosition), typeof(float), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    [HarmonyPrefix]
    private static void TiltwingAutoAimPrefix(
        AutopilotTiltwing __instance,
        GlobalPosition destination,
        ref float altitudeHold,
        bool followTerrain)
    {
        CommanderSupplyHeliService.PrepareAssignedTerrainFlight(
            __instance,
            destination,
            ref altitudeHold,
            followTerrain);
    }

    [HarmonyPatch(typeof(Autopilot), nameof(Autopilot.Hover))]
    [HarmonyPrefix]
    private static void HoverPrefix(Autopilot __instance, GlobalPosition destination, ref float altitudeHold)
    {
        CommanderSupplyHeliService.HoldCargoRunHoverClearance(__instance, destination, ref altitudeHold);
    }

    [HarmonyPatch(typeof(SwivelDuctSystem), "FixedUpdate")]
    [HarmonyPrefix]
    private static void SwivelDuctFixedUpdatePrefix(SwivelDuctSystem __instance)
    {
        CommanderSupplyHeliService.ForceAssignedVerticalTakeoff(__instance);
    }
}
