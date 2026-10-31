using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Drives ground vehicles round what the base game drives them into. <see cref="PathfindingAgent"/>
/// searches roads only: the legs before the first road point and after the last, and a whole order
/// with no road near it, are one straight line over any ground. The Burst job that steers cannot be
/// patched, but it only follows the waypoint list, so a leg that cannot be driven gets a searched
/// detour spliced into that list. Host only: only the host ever paths a vehicle.
/// </summary>
internal sealed class CommanderTerrainRouteService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>The steepest rise a leg may need over one cell. A guess: how hard a vehicle can
    /// climb is its prefab's <c>acceleration</c>, which is asset data.</summary>
    internal const float MaxGrade = 0.3f;

    private const float CellMeters = 30f;

    /// <summary>Shorter legs are parking and depot doors, and the search cell is half of this.</summary>
    private const float MinLegMeters = 60f;

    private const float MaxLegMeters = 10000f;

    /// <summary>Roughly a 2.7 km square of open search. Past it the leg stays straight and the stall
    /// test is the backstop.</summary>
    private const int MaxExpansions = 8000;

    /// <summary>Above 1 the search prefers finishing to perfection: this runs inside a frame.</summary>
    private const float HeuristicWeight = 1.4f;

    /// <summary>Extra cost per unit of grade, so a route takes a gentle slope over a legal steep one.</summary>
    private const float SteepCost = 6f;

    /// <summary>A structure this far over the ground under it is in the way; less is an apron.</summary>
    private const float StructureMeters = 1.5f;

    private const float BudgetMilliseconds = 1.5f;

    private const float StallCheckSeconds = 5f;

    /// <summary>A vehicle stays inside this while it stalls, backs up and stalls again.</summary>
    private const float StallMeters = 20f;

    private const float StallSeconds = 25f;

    /// <summary>How long a cell a vehicle stalled at stays out of routes. Long enough to get round
    /// it; it may have been traffic, which moves.</summary>
    private const float StallCellSeconds = 120f;

    private const float ReportSeconds = 60f;

    /// <summary>Clearance round a building. With its own walls this is wider than a cell, so even a
    /// small one blocks at least one cell centre, and a route does not shave its corner.</summary>
    private const float BuildingClearanceMeters = 8f;

    private static readonly AccessTools.FieldRef<PathfindingAgent, List<GlobalPosition>> Waypoints =
        AccessTools.FieldRefAccess<PathfindingAgent, List<GlobalPosition>>("waypoints");

    private static readonly AccessTools.FieldRef<PathfindingAgent, Unit> Owner =
        AccessTools.FieldRefAccess<PathfindingAgent, Unit>("unit");

    private static readonly AccessTools.FieldRef<GroundVehicle, PathfindingAgent> VehiclePath =
        AccessTools.FieldRefAccess<GroundVehicle, PathfindingAgent>("pathfinder");

    private static readonly int[] StepX = { 1, -1, 0, 0, 1, 1, -1, -1 };

    private static readonly int[] StepZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private static readonly Dictionary<long, float> Heights = new();
    private static readonly Dictionary<long, float> StalledCells = new();

    /// <summary>Cells under a faction's buildings, counted because footprints overlap.</summary>
    private static readonly Dictionary<long, int> BuildingCells = new();
    private static readonly RaycastHit[] Hits = new RaycastHit[16];

    private static readonly List<(float Priority, long Cell)> Open = new();
    private static readonly Dictionary<long, float> Best = new();
    private static readonly Dictionary<long, long> Parent = new();
    private static readonly HashSet<long> Closed = new();

    private static readonly Func<int, int, float> LiveHeight = CellHeight;

    private readonly Queue<(PathfindingAgent Agent, GlobalPosition To)> pending = new();
    private readonly Dictionary<GroundVehicle, (GlobalPosition Position, float Since)> progress = new();
    private readonly List<Vector2Int> cells = new();
    private readonly List<GlobalPosition> route = new();
    private readonly Dictionary<Building, RectInt> footprints = new();
    private readonly List<Building> goneBuildings = new();
    private readonly List<Collider> colliders = new();
    private readonly Stopwatch timer = new();

    private float nextStallCheck;
    private float nextReport;
    private int detours;
    private int noRoute;
    private int stalls;
    private double worstSearchMs;

    internal CommanderTerrainRouteService()
    {
        Instance = this;
    }

    internal static CommanderTerrainRouteService? Instance { get; private set; }

    public void ResetSession()
    {
        pending.Clear();
        progress.Clear();
        Heights.Clear();
        StalledCells.Clear();
        BuildingCells.Clear();
        footprints.Clear();
    }

    public void TickPersistent()
    {
        if (pending.Count > 0)
        {
            timer.Restart();
            while (pending.Count > 0 && timer.Elapsed.TotalMilliseconds < BudgetMilliseconds)
            {
                (PathfindingAgent agent, GlobalPosition to) = pending.Dequeue();
                Reroute(agent, to);
            }
        }

        if (CommanderScheduler.IsDue(ref nextStallCheck, StallCheckSeconds))
        {
            CheckStalls();
        }

        if (detours + noRoute + stalls > 0 && CommanderScheduler.IsDueRealtime(ref nextReport, ReportSeconds))
        {
            CommanderPlugin.Log.LogInfo(
                $"Terrain routes: {detours} detours, {noRoute} legs with no route, {stalls} stalls, worst search {worstSearchMs:0.0} ms.");
            detours = 0;
            noRoute = 0;
            stalls = 0;
            worstSearchMs = 0d;
        }
    }

    /// <summary>
    /// False when a straight drive between two points meets ground over <see cref="MaxGrade"/>, water
    /// or a structure. Sampled along the line, because a ridge between two level points is what stops
    /// a vehicle. Downhill counts: what drove down has to drive back.
    /// </summary>
    internal static bool IsDrivable(GlobalPosition from, GlobalPosition to)
    {
        return IsLineClear(from.x, from.z, to.x, to.z);
    }

    /// <summary>Pure so <see cref="SelfCheck"/> can run it on a made-up map. <paramref name="height"/>
    /// is NaN for a cell nothing can enter. Stops next to the goal, whose own cell may be the steep
    /// spot the order is for.</summary>
    internal static bool TryFindCells(int startX, int startZ, int goalX, int goalZ, Func<int, int, float> height, List<Vector2Int> path)
    {
        path.Clear();
        Open.Clear();
        Best.Clear();
        Parent.Clear();
        Closed.Clear();
        long start = Key(startX, startZ);
        Best[start] = 0f;
        Push(0f, start);
        for (int expansions = 0; Open.Count > 0 && expansions < MaxExpansions; expansions++)
        {
            long cell = Pop();
            if (!Closed.Add(cell))
            {
                continue;
            }

            int x = CellX(cell);
            int z = CellZ(cell);
            if (Mathf.Abs(x - goalX) <= 1 && Mathf.Abs(z - goalZ) <= 1)
            {
                for (long at = cell; ; at = Parent[at])
                {
                    path.Add(new Vector2Int(CellX(at), CellZ(at)));
                    if (at == start)
                    {
                        break;
                    }
                }

                path.Reverse();
                return true;
            }

            float here = height(x, z);
            for (int n = 0; n < 8; n++)
            {
                int nextX = x + StepX[n];
                int nextZ = z + StepZ[n];
                long next = Key(nextX, nextZ);
                float there = height(nextX, nextZ);
                if (float.IsNaN(there) || Closed.Contains(next))
                {
                    continue;
                }

                float step = (n < 4 ? 1f : 1.414f) * CellMeters;

                // A vehicle standing on a bad cell has to be able to leave it.
                float grade = float.IsNaN(here) ? 0f : Mathf.Abs(there - here) / step;
                if (grade > MaxGrade)
                {
                    continue;
                }

                float cost = Best[cell] + (step * (1f + (SteepCost * grade)));
                if (!Best.TryGetValue(next, out float known) || cost < known)
                {
                    Best[next] = cost;
                    Parent[next] = cell;
                    float left = new Vector2(goalX - nextX, goalZ - nextZ).magnitude * CellMeters;
                    Push(cost + (left * HeuristicWeight), next);
                }
            }
        }

        return false;
    }

    internal static void SelfCheck()
    {
        List<Vector2Int> path = new();
        if (!TryFindCells(0, 0, 10, 0, RidgeWithGap, path) || !path.Contains(new Vector2Int(5, 12)))
        {
            CommanderPlugin.Log.LogError("Terrain route self-check FAILED: the route did not go through the gap.");
        }

        if (TryFindCells(0, 0, 10, 0, UnbrokenCliff, path))
        {
            CommanderPlugin.Log.LogError("Terrain route self-check FAILED: a route crossed an unbroken cliff.");
        }

        if (TryFindCells(0, 0, 10, 0, SteepRamp, path))
        {
            CommanderPlugin.Log.LogError("Terrain route self-check FAILED: a route climbed a slope over the grade limit.");
        }
    }

    /// <summary>A path just got a new leg ending at <paramref name="to"/>. Queued rather than checked
    /// here: a capture wave issues twenty orders a frame, and this sits on the game's own path.</summary>
    internal void Enqueue(PathfindingAgent agent, GlobalPosition to)
    {
        if (Owner(agent) is GroundVehicle)
        {
            pending.Enqueue((agent, to));
        }
    }

    /// <summary>A cliff along x = 5 with one gap at z = 12, between start and goal.</summary>
    private static float RidgeWithGap(int x, int z)
    {
        return x == 5 && z != 12 ? float.NaN : 0f;
    }

    private static float UnbrokenCliff(int x, int z)
    {
        return x == 5 ? float.NaN : 0f;
    }

    private static float SteepRamp(int x, int z)
    {
        return x * CellMeters * MaxGrade * 1.5f;
    }

    private static void Push(float priority, long cell)
    {
        Open.Add((priority, cell));
        int child = Open.Count - 1;
        while (child > 0)
        {
            int parent = (child - 1) / 2;
            if (Open[parent].Priority <= Open[child].Priority)
            {
                break;
            }

            (Open[parent], Open[child]) = (Open[child], Open[parent]);
            child = parent;
        }
    }

    private static long Pop()
    {
        long top = Open[0].Cell;
        int last = Open.Count - 1;
        Open[0] = Open[last];
        Open.RemoveAt(last);
        int parent = 0;
        while (true)
        {
            int left = (parent * 2) + 1;
            if (left >= last)
            {
                return top;
            }

            int smaller = left + 1 < last && Open[left + 1].Priority < Open[left].Priority ? left + 1 : left;
            if (Open[parent].Priority <= Open[smaller].Priority)
            {
                return top;
            }

            (Open[parent], Open[smaller]) = (Open[smaller], Open[parent]);
            parent = smaller;
        }
    }

    private static long Key(int x, int z)
    {
        return ((long)(x + 0x8000) << 16) | (uint)(z + 0x8000);
    }

    private static int CellX(long key)
    {
        return (int)(key >> 16) - 0x8000;
    }

    private static int CellZ(long key)
    {
        return (int)(key & 0xFFFF) - 0x8000;
    }

    private static int ToCell(float meters)
    {
        return Mathf.RoundToInt(meters / CellMeters);
    }

    private static float CellHeight(int x, int z)
    {
        long key = Key(x, z);
        if (StalledCells.TryGetValue(key, out float until))
        {
            if (Time.time < until)
            {
                return float.NaN;
            }

            StalledCells.Remove(key);
        }

        if (BuildingCells.ContainsKey(key))
        {
            return float.NaN;
        }

        if (!Heights.TryGetValue(key, out float height))
        {
            height = SampleHeight(x, z);
            Heights[key] = height;
        }

        return height;
    }

    /// <summary>
    /// The drivable surface of a cell in the global frame, or NaN for sea and structures. Not
    /// <c>RaycastTerrain</c>, whose first hit is a tree or roof, and not
    /// <see cref="CommanderGameAccess.SnapToTerrain"/>, which misses buildings and road meshes and
    /// allocates. Cached without expiry: a faction's buildings come from <see cref="BuildingCells"/>.
    /// </summary>
    private static float SampleHeight(int x, int z)
    {
        Vector3 local = new GlobalPosition(x * CellMeters, 0f, z * CellMeters).ToLocalPosition();
        Vector3 origin = new(local.x, Datum.LocalSeaY + 10000f, local.z);
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, Hits, 20000f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore);
        float ground = float.MinValue;
        float structure = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            Collider collider = Hits[i].collider;
            if (collider == null)
            {
                continue;
            }

            if (collider.sharedMaterial == GameAssets.i.terrainMaterial
                || collider.name.StartsWith("terrain", StringComparison.OrdinalIgnoreCase))
            {
                ground = Mathf.Max(ground, Hits[i].point.y);
            }
            else if (CommanderBuildPreview.IsMapStructure(collider))
            {
                // An airfield apron has no terrain under it, and is as drivable as a road.
                if (collider.bounds.size.y < StructureMeters * 2f)
                {
                    ground = Mathf.Max(ground, Hits[i].point.y);
                }
                else
                {
                    structure = Mathf.Max(structure, Hits[i].point.y);
                }
            }
        }

        return ground <= Datum.LocalSeaY || structure > ground + StructureMeters
            ? float.NaN
            : ground - Datum.LocalSeaY;
    }

    private static bool IsLineClear(float fromX, float fromZ, float toX, float toZ)
    {
        float meters = new Vector2(toX - fromX, toZ - fromZ).magnitude;
        int steps = Mathf.Max(1, Mathf.CeilToInt(meters / CellMeters));
        int lastX = ToCell(fromX);
        int lastZ = ToCell(fromZ);
        float lastHeight = CellHeight(lastX, lastZ);
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = ToCell(Mathf.Lerp(fromX, toX, t));
            int z = ToCell(Mathf.Lerp(fromZ, toZ, t));
            if (x == lastX && z == lastZ)
            {
                continue;
            }

            float height = CellHeight(x, z);
            if (float.IsNaN(height))
            {
                // The ends are where the order says, whatever stands there.
                if (i < steps)
                {
                    return false;
                }

                continue;
            }

            float step = new Vector2(x - lastX, z - lastZ).magnitude * CellMeters;
            if (!float.IsNaN(lastHeight) && Mathf.Abs(height - lastHeight) > step * MaxGrade)
            {
                return false;
            }

            lastX = x;
            lastZ = z;
            lastHeight = height;
        }

        return true;
    }

    /// <summary>Splices a detour in ahead of <paramref name="to"/> if the leg to it cannot be driven.
    /// The leg is read now rather than when it was queued, so a new order drops a stale request.</summary>
    private void Reroute(PathfindingAgent agent, GlobalPosition to)
    {
        Unit? unit = Owner(agent);
        List<GlobalPosition> waypoints = Waypoints(agent);
        int index = waypoints.IndexOf(to);
        if (unit == null || unit.disabled || index < 0)
        {
            return;
        }

        GlobalPosition from = index == 0 ? unit.GlobalPosition() : waypoints[index - 1];
        float meters = FastMath.Distance(from, to);
        if (meters < MinLegMeters || meters > MaxLegMeters || IsDrivable(from, to))
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        bool found = TryFindCells(ToCell(from.x), ToCell(from.z), ToCell(to.x), ToCell(to.z), LiveHeight, cells);
        worstSearchMs = Math.Max(worstSearchMs, (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);
        if (!found)
        {
            noRoute++;
            return;
        }

        Smooth(from);
        waypoints.InsertRange(index, route);
        detours++;
    }

    /// <summary><see cref="cells"/> as the fewest waypoints: each is the last cell a straight drive
    /// from the one before reaches. Thirty metre stair steps would have the vehicle weaving.</summary>
    private void Smooth(GlobalPosition from)
    {
        route.Clear();
        float atX = from.x;
        float atZ = from.z;
        int next = 1;
        while (next < cells.Count)
        {
            int reach = next;
            while (reach + 1 < cells.Count
                && IsLineClear(atX, atZ, cells[reach + 1].x * CellMeters, cells[reach + 1].y * CellMeters))
            {
                reach++;
            }

            atX = cells[reach].x * CellMeters;
            atZ = cells[reach].y * CellMeters;
            route.Add(new GlobalPosition(atX, CellHeight(cells[reach].x, cells[reach].y), atZ));
            next = reach + 1;
        }
    }

    /// <summary>
    /// The steering job backs a stalled vehicle up three seconds and sends it down the same line,
    /// for ever. Whatever the heights missed (a built wall, a ditch finer than a cell, a wreck) shows
    /// here: the cell ahead goes out of routes and the leg is searched again.
    /// </summary>
    private void CheckStalls()
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !hq.IsServer || hq.factionUnits == null)
            {
                continue;
            }

            foreach (PersistentID id in hq.factionUnits)
            {
                if (!id.TryGetUnit(out Unit unit) || unit.disabled)
                {
                    continue;
                }

                if (unit is GroundVehicle vehicle)
                {
                    CheckStall(vehicle);
                }
                else if (unit is Building building && !footprints.ContainsKey(building))
                {
                    AddFootprint(building);
                }
            }
        }

        RemoveGoneFootprints();
    }

    private void AddFootprint(Building building)
    {
        building.GetComponentsInChildren(false, colliders);
        Vector3 origin = building.transform.position;
        GlobalPosition at = building.transform.GlobalPosition();
        Bounds? all = null;
        for (int i = 0; i < colliders.Count; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || collider.isTrigger)
            {
                continue;
            }

            Bounds bounds = collider.bounds;
            if (all.HasValue)
            {
                bounds.Encapsulate(all.Value);
            }

            all = bounds;
        }

        // Flat ones are aprons and pads, which vehicles drive over.
        if (!all.HasValue || all.Value.size.y < StructureMeters * 2f)
        {
            footprints[building] = default;
            return;
        }

        Vector3 min = all.Value.min - origin;
        Vector3 max = all.Value.max - origin;
        int minX = Mathf.CeilToInt((at.x + min.x - BuildingClearanceMeters) / CellMeters);
        int minZ = Mathf.CeilToInt((at.z + min.z - BuildingClearanceMeters) / CellMeters);
        int maxX = Mathf.FloorToInt((at.x + max.x + BuildingClearanceMeters) / CellMeters);
        int maxZ = Mathf.FloorToInt((at.z + max.z + BuildingClearanceMeters) / CellMeters);
        RectInt footprint = new(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
        footprints[building] = footprint;
        for (int x = footprint.xMin; x < footprint.xMax; x++)
        {
            for (int z = footprint.yMin; z < footprint.yMax; z++)
            {
                long key = Key(x, z);
                BuildingCells[key] = (BuildingCells.TryGetValue(key, out int count) ? count : 0) + 1;
            }
        }
    }

    private void RemoveGoneFootprints()
    {
        goneBuildings.Clear();
        foreach (KeyValuePair<Building, RectInt> entry in footprints)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                goneBuildings.Add(entry.Key!);
            }
        }

        for (int i = 0; i < goneBuildings.Count; i++)
        {
            RectInt footprint = footprints[goneBuildings[i]];
            footprints.Remove(goneBuildings[i]);
            for (int x = footprint.xMin; x < footprint.xMax; x++)
            {
                for (int z = footprint.yMin; z < footprint.yMax; z++)
                {
                    long key = Key(x, z);
                    Heights.Remove(key);
                    if (BuildingCells.TryGetValue(key, out int count) && count > 1)
                    {
                        BuildingCells[key] = count - 1;
                    }
                    else
                    {
                        BuildingCells.Remove(key);
                    }
                }
            }
        }
    }

    private void CheckStall(GroundVehicle vehicle)
    {
        PathfindingAgent agent = VehiclePath(vehicle);
        List<GlobalPosition> waypoints = Waypoints(agent);
        GlobalPosition at = vehicle.GlobalPosition();
        if (waypoints.Count == 0
            || !progress.TryGetValue(vehicle, out (GlobalPosition Position, float Since) last)
            || FastMath.OutOfRange(at, last.Position, StallMeters))
        {
            progress[vehicle] = (at, Time.time);
            return;
        }

        // Dead still is a vehicle waiting on purpose (a cohesion hold, a depot lane); a stall jitters.
        if (Time.time - last.Since < StallSeconds || agent.IsOnBridge() || Mathf.Abs(vehicle.speed) < 0.1f)
        {
            return;
        }

        progress[vehicle] = (at, Time.time);
        stalls++;
        Vector3 ahead = FastMath.NormalizedDirection(at, waypoints[0]) * CellMeters;
        GlobalPosition blocked = at + ahead;
        StalledCells[Key(ToCell(blocked.x), ToCell(blocked.z))] = Time.time + StallCellSeconds;
        pending.Enqueue((agent, waypoints[0]));
    }
}
