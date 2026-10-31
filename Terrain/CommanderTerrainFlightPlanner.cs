using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderTerrainFlightPlanner
{
    private const float CellSize = 500f;
    private const int MaximumExpandedNodes = 50000;
    private const float SteepLandingNormalY = 0.94f;

    internal static bool TryBuildRoute(
        GlobalPosition start,
        GlobalPosition destination,
        float clearance,
        List<GlobalPosition> route,
        out bool steepLanding)
    {
        route.Clear();
        steepLanding = CommanderSamSiteAnalyzerService.EstimateStrategicTerrainNormalY(
            destination.x,
            destination.z,
            40f) < SteepLandingNormalY;
        if (!CommanderSamSiteAnalyzerService.TryGetStrategicHeightMapSize(out Vector2 mapSize))
        {
            return false;
        }

        float directDistance = HorizontalDistance(start.x, start.z, destination.x, destination.z);
        if (directDistance < 2500f)
        {
            return true;
        }

        float margin = Mathf.Clamp(directDistance * 0.15f, 3000f, 10000f);
        float mapMinX = -mapSize.x * 0.5f;
        float mapMaxX = mapSize.x * 0.5f;
        float mapMinZ = -mapSize.y * 0.5f;
        float mapMaxZ = mapSize.y * 0.5f;
        float minX = Mathf.Clamp(Mathf.Min(start.x, destination.x) - margin, mapMinX, mapMaxX);
        float maxX = Mathf.Clamp(Mathf.Max(start.x, destination.x) + margin, mapMinX, mapMaxX);
        float minZ = Mathf.Clamp(Mathf.Min(start.z, destination.z) - margin, mapMinZ, mapMaxZ);
        float maxZ = Mathf.Clamp(Mathf.Max(start.z, destination.z) + margin, mapMinZ, mapMaxZ);
        int columns = Mathf.Max(2, Mathf.CeilToInt((maxX - minX) / CellSize) + 1);
        int rows = Mathf.Max(2, Mathf.CeilToInt((maxZ - minZ) / CellSize) + 1);
        int count = columns * rows;
        if (count is <= 0 or > 100000)
        {
            return false;
        }

        int startX = Mathf.Clamp(Mathf.RoundToInt((start.x - minX) / CellSize), 0, columns - 1);
        int startZ = Mathf.Clamp(Mathf.RoundToInt((start.z - minZ) / CellSize), 0, rows - 1);
        int goalX = Mathf.Clamp(Mathf.RoundToInt((destination.x - minX) / CellSize), 0, columns - 1);
        int goalZ = Mathf.Clamp(Mathf.RoundToInt((destination.z - minZ) / CellSize), 0, rows - 1);
        int startIndex = (startZ * columns) + startX;
        int goalIndex = (goalZ * columns) + goalX;

        float[] costs = new float[count];
        float[] heights = new float[count];
        int[] parents = new int[count];
        byte[] states = new byte[count];
        for (int i = 0; i < count; i++)
        {
            costs[i] = float.PositiveInfinity;
            heights[i] = float.NaN;
            parents[i] = -1;
        }

        NodeHeap open = new(count);
        costs[startIndex] = 0f;
        open.Push(startIndex, Heuristic(startX, startZ, goalX, goalZ));
        states[startIndex] = 1;

        int expanded = 0;
        int[] offsetsX = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] offsetsZ = { -1, -1, -1, 0, 0, 1, 1, 1 };
        while (open.Count > 0 && expanded++ < MaximumExpandedNodes)
        {
            int current = open.Pop();
            if (states[current] == 2)
            {
                continue;
            }

            if (current == goalIndex)
            {
                break;
            }

            states[current] = 2;
            int currentX = current % columns;
            int currentZ = current / columns;
            if (!TryGetNodeHeight(current, currentX, currentZ, minX, minZ, heights, out float currentHeight))
            {
                continue;
            }

            for (int direction = 0; direction < offsetsX.Length; direction++)
            {
                int nextX = currentX + offsetsX[direction];
                int nextZ = currentZ + offsetsZ[direction];
                if (nextX < 0 || nextX >= columns || nextZ < 0 || nextZ >= rows)
                {
                    continue;
                }

                int next = (nextZ * columns) + nextX;
                if (states[next] == 2
                    || !TryGetNodeHeight(next, nextX, nextZ, minX, minZ, heights, out float nextHeight))
                {
                    continue;
                }

                float step = offsetsX[direction] == 0 || offsetsZ[direction] == 0
                    ? CellSize
                    : CellSize * 1.41421356f;
                float rise = Mathf.Max(0f, nextHeight - currentHeight);
                float slope = Mathf.Abs(nextHeight - currentHeight) / step;
                float terrainPenalty = (rise * 8f) + (slope * step * 4f);
                if (slope > 0.12f)
                {
                    terrainPenalty += (slope - 0.12f) * step * 40f;
                }

                float tentative = costs[current] + step + terrainPenalty;
                if (tentative >= costs[next])
                {
                    continue;
                }

                parents[next] = current;
                costs[next] = tentative;
                float priority = tentative + Heuristic(nextX, nextZ, goalX, goalZ);
                open.Push(next, priority);
                states[next] = 1;
            }
        }

        if (goalIndex != startIndex && parents[goalIndex] < 0)
        {
            return false;
        }

        List<int> reversePath = new();
        for (int node = goalIndex; node >= 0; node = parents[node])
        {
            reversePath.Add(node);
            if (node == startIndex)
            {
                break;
            }
        }

        if (reversePath.Count == 0 || reversePath[reversePath.Count - 1] != startIndex)
        {
            return false;
        }

        reversePath.Reverse();
        for (int i = 2; i < reversePath.Count - 2; i += 2)
        {
            int node = reversePath[i];
            int x = node % columns;
            int z = node / columns;
            float globalX = minX + (x * CellSize);
            float globalZ = minZ + (z * CellSize);
            float height = float.IsNaN(heights[node]) ? destination.y : heights[node];
            route.Add(new GlobalPosition(globalX, height + clearance, globalZ));
        }

        CommanderPlugin.Log.LogInfo(
            $"SAM flight route planned: direct={directDistance / 1000f:0.0}km, "
            + $"grid={columns}x{rows}, expanded={expanded}, waypoints={route.Count}, steepLanding={steepLanding}.");
        return true;

        static bool TryGetNodeHeight(
            int index,
            int x,
            int z,
            float originX,
            float originZ,
            float[] cache,
            out float height)
        {
            if (!float.IsNaN(cache[index]))
            {
                height = cache[index];
                return true;
            }

            if (!CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(
                originX + (x * CellSize),
                originZ + (z * CellSize),
                out height))
            {
                return false;
            }

            cache[index] = height;
            return true;
        }
    }

    private static float Heuristic(int x, int z, int goalX, int goalZ)
    {
        float dx = x - goalX;
        float dz = z - goalZ;
        return Mathf.Sqrt((dx * dx) + (dz * dz)) * CellSize;
    }

    private static float HorizontalDistance(float x1, float z1, float x2, float z2)
    {
        float dx = x1 - x2;
        float dz = z1 - z2;
        return Mathf.Sqrt((dx * dx) + (dz * dz));
    }

    private sealed class NodeHeap
    {
        private readonly List<Entry> entries;

        internal NodeHeap(int capacity)
        {
            entries = new List<Entry>(Mathf.Min(capacity, MaximumExpandedNodes));
        }

        internal int Count => entries.Count;

        internal void Push(int node, float priority)
        {
            entries.Add(new Entry(node, priority));
            int index = entries.Count - 1;
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (entries[parent].Priority <= priority)
                {
                    break;
                }

                entries[index] = entries[parent];
                index = parent;
            }

            entries[index] = new Entry(node, priority);
        }

        internal int Pop()
        {
            Entry root = entries[0];
            Entry tail = entries[entries.Count - 1];
            entries.RemoveAt(entries.Count - 1);
            if (entries.Count == 0)
            {
                return root.Node;
            }

            int index = 0;
            while (true)
            {
                int left = (index * 2) + 1;
                if (left >= entries.Count)
                {
                    break;
                }

                int right = left + 1;
                int child = right < entries.Count && entries[right].Priority < entries[left].Priority
                    ? right
                    : left;
                if (entries[child].Priority >= tail.Priority)
                {
                    break;
                }

                entries[index] = entries[child];
                index = child;
            }

            entries[index] = tail;
            return root.Node;
        }

        private readonly struct Entry
        {
            internal Entry(int node, float priority)
            {
                Node = node;
                Priority = priority;
            }

            internal int Node { get; }

            internal float Priority { get; }
        }
    }
}
