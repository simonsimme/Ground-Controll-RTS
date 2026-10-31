using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Depots on an island. <c>DeployVehicles</c> rolls each reserve vehicle out of the first depot that
/// takes it, so an AI side holding an island base fed tanks and IFVs onto it: they could reach
/// nothing, parked in the depot's exit lane and jammed it.
/// There an AI depot fields only what defends where it stands.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>Land reached this far from the door is the mainland. The flood is a coarse grid,
    /// so a causeway narrower than a cell, or a bridge, reads as water: the base counts as an island.</summary>
    private const float IslandProbeMeters = 12000f;

    private const float IslandStepMeters = 200f;

    private static readonly Dictionary<VehicleDepot, bool> IslandDepots = new();

    /// <summary>Base centres already flooded, by position to the nearest 100 m.</summary>
    private static readonly Dictionary<(int X, int Z), bool> IslandBases = new();

    /// <summary>
    /// Off the <c>VehicleDepot.TrySpawnVehicle</c> prefix: an AI side's island depot refuses anything
    /// but air defence, rearm trucks and builders, which then wait for a mainland depot.
    /// </summary>
    internal static bool HoldsAssaultAtIsland(VehicleDepot depot, VehicleDefinition definition)
    {
        if (depot == null
            || IsAirDefence(definition)
            || IsSupplyVehicle(definition)
            || ReferenceEquals(definition, CommanderGameAccess.FindRepairVehicleDefinition(depot.NetworkHQ))
            || !CommanderUnitCap.IsAiSide(depot.NetworkHQ))
        {
            return false;
        }

        if (!IslandDepots.TryGetValue(depot, out bool island))
        {
            // Until the height map is baked nothing is known, and nothing is cached.
            if (!TryIsOnIsland(depot.GlobalPosition(), out island))
            {
                return false;
            }

            IslandDepots[depot] = island;
            if (island)
            {
                CommanderPlugin.Log.LogInfo($"{depot.NetworkHQ?.faction.factionName}'s {depot.name} is on an island: it fields air defence and support only.");
            }
        }

        return island;
    }

    /// <summary>
    /// Floods over land from <paramref name="start"/> and says whether it runs out before
    /// <see cref="IslandProbeMeters"/>, on the SAM analyzer's baked height map (land is 1 m above the
    /// sea, as there). A physics raycast found no island in one run and two in the next: terrain far
    /// from the camera is not always there to hit. False until the map is baked.
    /// </summary>
    internal static bool TryIsOnIsland(GlobalPosition start, out bool island)
    {
        island = false;
        if (!CommanderSamSiteAnalyzerService.TryGetStrategicHeightMapSize(out _))
        {
            return false;
        }

        int radius = Mathf.CeilToInt(IslandProbeMeters / IslandStepMeters);
        HashSet<(int X, int Z)> seen = new() { (0, 0) };
        Queue<(int X, int Z)> open = new();
        open.Enqueue((0, 0));
        while (open.Count > 0)
        {
            (int x, int z) = open.Dequeue();
            if ((x * x) + (z * z) >= radius * radius)
            {
                return true;
            }

            for (int n = 0; n < 4; n++)
            {
                (int X, int Z) next = n switch
                {
                    0 => (x + 1, z),
                    1 => (x - 1, z),
                    2 => (x, z + 1),
                    _ => (x, z - 1),
                };
                if (seen.Add(next)
                    && CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(
                        start.x + (next.X * IslandStepMeters), start.z + (next.Z * IslandStepMeters), out float height)
                    && height > Datum.SeaLevel.y + 1f)
                {
                    open.Enqueue(next);
                }
            }
        }

        island = true;
        return true;
    }

    /// <summary>
    /// A vehicle at the base <paramref name="from"/> can drive to a post of the base <paramref name="to"/>:
    /// the same base, or two mainland ones.
    /// </summary>
    internal static bool CanDriveBetween(GlobalPosition from, GlobalPosition to)
    {
        return CommanderGameAccess.ApproximatelyEqual(from, to, 1f) || (!IsIslandAt(from) && !IsIslandAt(to));
    }

    /// <summary>A base centre on an island, cached: the guard asks for every post it weighs.</summary>
    private static bool IsIslandAt(GlobalPosition baseCentre)
    {
        (int, int) key = (Mathf.RoundToInt(baseCentre.x / 100f), Mathf.RoundToInt(baseCentre.z / 100f));
        if (!IslandBases.TryGetValue(key, out bool island) && TryIsOnIsland(baseCentre, out island))
        {
            IslandBases[key] = island;
        }

        return island;
    }

    /// <summary>
    /// A post the defender cannot drive to. Islands are only known once the height map is baked, a few
    /// minutes in, and the opening air defence out of an island depot was given a mainland post before
    /// that and parked at the depot door for the rest of the match. A
    /// ring rebuilt for a new set of bases does the same. <c>PruneDefenders</c> drops it, and it is
    /// re-recruited the same review.
    /// </summary>
    private static bool IsPostAcrossWater(CommanderState state, Unit unit, int post)
    {
        int posts = state.DefencePostBases.Count;
        return posts > 0
            && !IsSamSiteLauncher(unit)
            && !CanDriveBetween(GetHomeBase(state, unit), state.DefencePostBases[post % posts]);
    }
}
