using System.Collections.Generic;
using HarmonyLib;

namespace GroundControlRts;

/// <summary>Hands <see cref="CommanderTerrainRouteService"/> the off-road legs of a path: its first
/// one when it is made, and its last one when the road part runs out, since the game only generates
/// a long path's waypoints as the vehicle gets to them.</summary>
internal static class CommanderTerrainRoutePatches
{
    private static readonly AccessTools.FieldRef<PathfindingAgent, List<GlobalPosition>> Waypoints =
        AccessTools.FieldRefAccess<PathfindingAgent, List<GlobalPosition>>("waypoints");

    private static readonly AccessTools.FieldRef<PathfindingAgent, List<RoadPathfinding.Node>> Nodes =
        AccessTools.FieldRefAccess<PathfindingAgent, List<RoadPathfinding.Node>>("nodes");

    // A postfix runs even when CommanderDirectPathPatch swapped the path for a straight Shortcut.
    [HarmonyPatch(typeof(PathfindingAgent), nameof(PathfindingAgent.Pathfind))]
    internal static class NewPathPatch
    {
        private static void Postfix(PathfindingAgent __instance)
        {
            CommanderTerrainRouteService? service = CommanderTerrainRouteService.Instance;
            List<GlobalPosition> waypoints = Waypoints(__instance);
            if (service == null || waypoints.Count == 0)
            {
                return;
            }

            service.Enqueue(__instance, waypoints[0]);
            if (waypoints.Count > 1 && Nodes(__instance).Count == 0)
            {
                service.Enqueue(__instance, waypoints[waypoints.Count - 1]);
            }
        }
    }

    [HarmonyPatch(typeof(PathfindingAgent), "GetNextWaypoints")]
    internal static class LastLegPatch
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
        private static void Postfix(PathfindingAgent __instance, List<GlobalPosition> __result)
        {
            if (Nodes(__instance).Count == 0 && __result.Count > 0)
            {
                CommanderTerrainRouteService.Instance?.Enqueue(__instance, __result[__result.Count - 1]);
            }
        }
    }
}
