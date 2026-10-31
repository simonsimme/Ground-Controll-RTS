using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// A taxi route over the map's pavement, for a forward base whose roads carry no road data. A shelter
/// on a strip 600 m from the base's runway had nothing to join, and the taxi AI drove its jets straight
/// across the grass to the nearest taxi line. This is a grid search over
/// <see cref="CommanderBuildPreview.IsPavement"/>, which keeps to the middle of wide roads and takes the
/// narrow ones when they are the way.
/// </summary>
internal static class CommanderPavementRoute
{
    private const float CellMeters = 4f;

    /// <summary>Farthest a route may wander from where it starts.</summary>
    private const float MaxReachMeters = 2500f;

    /// <summary>Cells looked at before giving up; each is one pavement probe.</summary>
    private const int MaxExpanded = 60000;

    /// <summary>Within this of a target line the route has arrived.</summary>
    private const float ArriveMeters = 6f;

    /// <summary>Extra cost per step for each of the four directions where pavement ends within two
    /// cells: it keeps the route off the edges of a wide road.</summary>
    private const float EdgePenalty = 0.75f;

    private static readonly Dictionary<long, bool> Paved = new();

    /// <summary>
    /// The way from <paramref name="from"/> to the nearest of <paramref name="lines"/> over pavement, as
    /// corner points (the first is <paramref name="from"/>, the last the junction on the line).
    /// <paramref name="line"/> and <paramref name="segment"/> say where the junction is.
    /// </summary>
    internal static List<GlobalPosition>? Find(GlobalPosition from, List<RoadPathfinding.Road> lines, out RoadPathfinding.Road? line, out int segment)
    {
        line = null;
        segment = 0;
        Paved.Clear();
        Dictionary<long, long> cameFrom = new();
        Dictionary<long, float> cost = new();
        SortedSet<(float, long)> open = new();
        long start = Key(0, 0);
        cost[start] = 0f;
        open.Add((Remaining(from, lines), start));
        long goal = 0;
        bool found = false;
        int expanded = 0;
        while (open.Count > 0 && expanded++ < MaxExpanded)
        {
            (float _, long current) = open.Min;
            open.Remove(open.Min);
            GlobalPosition here = Position(from, current);
            if (Remaining(here, lines) <= ArriveMeters)
            {
                goal = current;
                found = true;
                break;
            }

            Unpack(current, out int x, out int z);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if ((dx == 0 && dz == 0) || !IsPaved(from, x + dx, z + dz))
                    {
                        continue;
                    }

                    // A diagonal must not cut a corner of grass.
                    if (dx != 0 && dz != 0 && (!IsPaved(from, x + dx, z) || !IsPaved(from, x, z + dz)))
                    {
                        continue;
                    }

                    long next = Key(x + dx, z + dz);
                    if (new Vector2((x + dx) * CellMeters, (z + dz) * CellMeters).magnitude > MaxReachMeters)
                    {
                        continue;
                    }

                    float step = (dx != 0 && dz != 0 ? 1.414f : 1f) * CellMeters * (1f + (EdgePenalty * Edges(from, x + dx, z + dz)));
                    float total = cost[current] + step;
                    if (cost.TryGetValue(next, out float known) && known <= total)
                    {
                        continue;
                    }

                    if (cost.ContainsKey(next))
                    {
                        open.Remove((known + Remaining(Position(from, next), lines), next));
                    }

                    cost[next] = total;
                    cameFrom[next] = current;
                    open.Add((total + Remaining(Position(from, next), lines), next));
                }
            }
        }

        if (!found)
        {
            CommanderPlugin.Log.LogInfo($"Pavement route from pos:{from.x:0},{from.z:0}: none found after {expanded} cells.");
            return null;
        }

        List<long> cells = new() { goal };
        while (cameFrom.TryGetValue(cells[cells.Count - 1], out long previous))
        {
            cells.Add(previous);
        }

        cells.Reverse();

        // Corners only: from each corner, on to the farthest cell still in a straight line over pavement.
        List<GlobalPosition> route = new() { from };
        int at = 0;
        while (at < cells.Count - 1)
        {
            int reach = at + 1;
            for (int j = cells.Count - 1; j > at + 1; j--)
            {
                if (IsStraightOverPavement(from, cells[at], cells[j]))
                {
                    reach = j;
                    break;
                }
            }

            route.Add(Position(from, cells[reach]));
            at = reach;
        }

        GlobalPosition end = Position(from, goal);
        GlobalPosition junction = Nearest(end, lines, out line, out segment);
        route.Add(junction);
        for (int i = 0; i < route.Count; i++)
        {
            route[i] = new GlobalPosition(route[i].x, CommanderBuildPreview.GetSurfaceY(route[i]), route[i].z);
        }

        CommanderPlugin.Log.LogInfo($"Pavement route from pos:{from.x:0},{from.z:0}: {route.Count - 1} legs over {cells.Count * CellMeters:0} m, {expanded} cells looked at.");
        return route;
    }

    private static bool IsStraightOverPavement(GlobalPosition origin, long a, long b)
    {
        Unpack(a, out int ax, out int az);
        Unpack(b, out int bx, out int bz);
        int steps = Mathf.CeilToInt(new Vector2(bx - ax, bz - az).magnitude * 2f);
        for (int i = 1; i < steps; i++)
        {
            float t = i / (float)steps;
            if (!IsPaved(origin, Mathf.RoundToInt(Mathf.Lerp(ax, bx, t)), Mathf.RoundToInt(Mathf.Lerp(az, bz, t))))
            {
                return false;
            }
        }

        return true;
    }

    private static int Edges(GlobalPosition origin, int x, int z)
    {
        int edges = 0;
        edges += IsPaved(origin, x + 2, z) ? 0 : 1;
        edges += IsPaved(origin, x - 2, z) ? 0 : 1;
        edges += IsPaved(origin, x, z + 2) ? 0 : 1;
        edges += IsPaved(origin, x, z - 2) ? 0 : 1;
        return edges;
    }

    private static bool IsPaved(GlobalPosition origin, int x, int z)
    {
        long key = Key(x, z);
        if (!Paved.TryGetValue(key, out bool paved))
        {
            paved = (x == 0 && z == 0) || CommanderBuildPreview.IsPavement(Position(origin, key));
            Paved[key] = paved;
        }

        return paved;
    }

    private static float Remaining(GlobalPosition point, List<RoadPathfinding.Road> lines)
    {
        GlobalPosition nearest = Nearest(point, lines, out _, out _);
        return new Vector2(nearest.x - point.x, nearest.z - point.z).magnitude;
    }

    private static GlobalPosition Nearest(GlobalPosition point, List<RoadPathfinding.Road> lines, out RoadPathfinding.Road? line, out int segment)
    {
        line = null;
        segment = 0;
        GlobalPosition best = point;
        float bestDistance = float.MaxValue;
        foreach (RoadPathfinding.Road road in lines)
        {
            for (int i = 1; i < road.points.Count; i++)
            {
                GlobalPosition a = road.points[i - 1];
                GlobalPosition b = road.points[i];
                Vector2 ab = new(b.x - a.x, b.z - a.z);
                Vector2 ap = new(point.x - a.x, point.z - a.z);
                float t = ab.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude) : 0f;
                GlobalPosition on = new(a.x + (ab.x * t), a.y + ((b.y - a.y) * t), a.z + (ab.y * t));
                float distance = new Vector2(on.x - point.x, on.z - point.z).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = on;
                    line = road;
                    segment = i;
                }
            }
        }

        return best;
    }

    private static GlobalPosition Position(GlobalPosition origin, long key)
    {
        Unpack(key, out int x, out int z);
        return new GlobalPosition(origin.x + (x * CellMeters), origin.y, origin.z + (z * CellMeters));
    }

    private static long Key(int x, int z)
    {
        return ((long)x << 32) | (uint)z;
    }

    private static void Unpack(long key, out int x, out int z)
    {
        x = (int)(key >> 32);
        z = (int)(key & 0xffffffffL);
    }
}
