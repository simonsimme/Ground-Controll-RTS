using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderDirectPathPatches
{
    [HarmonyPatch(typeof(PathfindingAgent), nameof(PathfindingAgent.Pathfind))]
    internal static class CommanderDirectPathPatch
    {
        private static bool Prefix(PathfindingAgent __instance, GlobalPosition targetPos)
        {
            return !CommanderDirectPathService.TryApplyShortcut(__instance, targetPos);
        }
    }

    /// <summary>
    /// The base game only counts the last path point as reached within 5 m, or once it is behind the
    /// vehicle, and it never drives slower than 30 km/h until then. A formation slot that ends up off
    /// to one side is inside the turning circle at that speed, so the vehicle orbited it until it fell
    /// behind, then parked and deployed: the spin players saw at the end of every move and rally.
    /// Close and abeam while moving now counts as arrived. From rest it still turns onto the point.
    /// </summary>
    [HarmonyPatch(typeof(PathfindingAgent), nameof(PathfindingAgent.GetSteerpoint))]
    internal static class CommanderFinalPointOrbitPatch
    {
        private const float OrbitMeters = 25f;
        private const float MovingSpeed = 4f;

        // cos 45°: anything further round than that is abeam; behind is the base game's own case.
        private const float AbeamCos = 0.707f;

        private static readonly AccessTools.FieldRef<PathfindingAgent, List<GlobalPosition>> Waypoints =
            AccessTools.FieldRefAccess<PathfindingAgent, List<GlobalPosition>>("waypoints");

        private static readonly AccessTools.FieldRef<PathfindingAgent, List<RoadPathfinding.Node>> Nodes =
            AccessTools.FieldRefAccess<PathfindingAgent, List<RoadPathfinding.Node>>("nodes");

        private static readonly AccessTools.FieldRef<PathfindingAgent, Transform?> MovingTarget =
            AccessTools.FieldRefAccess<PathfindingAgent, Transform?>("movingTarget");

        private static readonly AccessTools.FieldRef<PathfindingAgent, Unit> Owner =
            AccessTools.FieldRefAccess<PathfindingAgent, Unit>("unit");

        private static void Prefix(PathfindingAgent __instance, GlobalPosition position, Vector3 forward, float speed)
        {
            if (speed < MovingSpeed)
            {
                return;
            }

            List<GlobalPosition> waypoints = Waypoints(__instance);
            if (waypoints.Count != 1
                || Nodes(__instance).Count > 0
                || MovingTarget(__instance) != null
                || Owner(__instance) is not GroundVehicle)
            {
                return;
            }

            Vector3 toPoint = FastMath.Direction(position, waypoints[0]);
            toPoint.y = 0f;
            forward.y = 0f;
            float distance = toPoint.magnitude;
            if (distance < OrbitMeters && Vector3.Dot(toPoint, forward.normalized) < distance * AbeamCos)
            {
                // With no waypoint and no nodes left the original returns no steerpoint, which is
                // exactly its own "arrived": throttle off, brakes on.
                waypoints.Clear();
            }
        }
    }
}
