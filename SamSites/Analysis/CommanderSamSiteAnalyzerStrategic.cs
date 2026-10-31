using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteAnalyzerService
{
    private static bool IsStrategicInfluenceUnit(Unit unit)
    {
        return unit != null
            && !unit.disabled
            && unit.unitState != Unit.UnitState.Destroyed
            && unit.unitState != Unit.UnitState.Returned
            && unit is not Aircraft
            && unit is not Missile
            && unit is not PilotDismounted;
    }

    private static void GetUnitInfluence(Unit unit, out float weight, out float radius)
    {
        if (unit is Ship)
        {
            weight = 1.8f;
            radius = 10000f;
            return;
        }

        if (unit is GroundVehicle)
        {
            weight = 1f;
            radius = 7000f;
            return;
        }

        bool armed = unit.weaponStations != null && unit.weaponStations.Count > 0;
        float captureValue = Mathf.Max(unit.CaptureStrength, unit.CaptureDefense);
        weight = armed ? 0.9f : captureValue > 0f ? 1.2f : 0.35f;
        radius = captureValue > 0f ? 9000f : 5000f;
    }

    private static float NearestDistance(GlobalPosition position, List<GlobalPosition> points)
    {
        float nearestSquared = float.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            nearestSquared = Mathf.Min(nearestSquared, HorizontalSquareDistance(position, points[i]));
        }

        return nearestSquared == float.MaxValue ? float.MaxValue : Mathf.Sqrt(nearestSquared);
    }

    private float CalculateStrategicWeight(GlobalPosition position)
    {
        if (strategicWeights != null && mapSettings != null)
        {
            return SampleStrategicField(strategicWeights, position);
        }

        return CalculateStrategicWeightExact(position.x, position.z);
    }

    private float CalculateStrategicWeightExact(float x, float z)
    {
        if (friendlyAirbases.Count == 0 || enemyAirbases.Count == 0)
        {
            return 1f;
        }

        GlobalPosition position = new(x, 0f, z);
        float friendlyDistance = NearestDistance(position, friendlyAirbases);
        float enemyDistance = NearestDistance(position, enemyAirbases);
        const float frontWidth = 10000f;
        float depth = Mathf.Abs(enemyDistance - friendlyDistance);
        float front = Mathf.Exp(-(depth * depth) / (2f * frontWidth * frontWidth));
        return 0.02f + (front * 0.98f);
    }

    private float CalculateRisk(GlobalPosition position)
    {
        if (strategicRisks != null && mapSettings != null)
        {
            return SampleStrategicField(strategicRisks, position);
        }

        float friendlyDistance = NearestDistance(position, friendlyAirbases);
        float enemyDistance = NearestDistance(position, enemyAirbases);
        if (friendlyDistance == float.MaxValue || enemyDistance == float.MaxValue)
        {
            return 0.5f;
        }

        float friendlyDepth = enemyDistance - friendlyDistance;
        return 1f - Mathf.InverseLerp(-15000f, 30000f, friendlyDepth);
    }

    private bool EnsureStrategicInfluenceReady()
    {
        if (strategicWeights != null && strategicRisks != null)
        {
            return true;
        }

        if (Time.unscaledTime < nextInfluenceRefreshAt)
        {
            return false;
        }

        // A failed build walks every unit into the grid (16 ms on a 200-unit map) and only fails
        // while no enemy is known, which can last a whole session: once a second was a steady hitch.
        nextInfluenceRefreshAt = Time.unscaledTime + 10f;
        return RefreshStrategicAnchors();
    }

    private bool RefreshStrategicAnchors()
    {
        friendlyAirbases.Clear();
        enemyAirbases.Clear();
        strategicWeights = null;
        strategicRisks = null;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return false;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }

            FactionMode mode = ReferenceEquals(hq, localHq)
                ? FactionMode.Friendly
                : DynamicMap.GetFactionMode(hq);
            if (mode == FactionMode.NoFaction && !ReferenceEquals(hq, localHq))
            {
                mode = FactionMode.Enemy;
            }

            List<GlobalPosition>? destination = mode == FactionMode.Friendly
                ? friendlyAirbases
                : mode == FactionMode.Enemy ? enemyAirbases : null;
            if (destination == null)
            {
                continue;
            }

            foreach (Airbase airbase in hq!.GetAirbases())
            {
                if (airbase?.center != null)
                {
                    destination.Add(airbase.center.GlobalPosition());
                }
            }
        }

        return BuildStrategicWeightMap(localHq);
    }

    private bool BuildStrategicWeightMap(FactionHQ localHq)
    {
        if (mapSettings == null)
        {
            return false;
        }

        int cellCount = StrategicWeightResolution * StrategicWeightResolution;
        float[] friendlyInfluence = new float[cellCount];
        float[] enemyInfluence = new float[cellCount];
        int friendlyUnits = 0;
        int enemyUnits = 0;

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }

            FactionMode mode = ReferenceEquals(hq, localHq)
                ? FactionMode.Friendly
                : DynamicMap.GetFactionMode(hq);
            if (mode == FactionMode.NoFaction && !ReferenceEquals(hq, localHq))
            {
                mode = FactionMode.Enemy;
            }

            float[]? field = mode == FactionMode.Friendly
                ? friendlyInfluence
                : mode == FactionMode.Enemy ? enemyInfluence : null;
            if (field == null)
            {
                continue;
            }

            foreach (PersistentID unitId in hq.factionUnits)
            {
                if (!unitId.TryGetUnit(out Unit unit) || !IsStrategicInfluenceUnit(unit))
                {
                    continue;
                }

                GetUnitInfluence(unit, out float weight, out float radius);
                AddInfluence(field, unit.GlobalPosition(), weight, radius);
                if (mode == FactionMode.Friendly)
                {
                    friendlyUnits++;
                }
                else
                {
                    enemyUnits++;
                }
            }
        }

        for (int i = 0; i < friendlyAirbases.Count; i++)
        {
            AddInfluence(friendlyInfluence, friendlyAirbases[i], 7f, 18000f);
        }

        for (int i = 0; i < enemyAirbases.Count; i++)
        {
            AddInfluence(enemyInfluence, enemyAirbases[i], 7f, 18000f);
        }

        bool hasFriendlySource = friendlyUnits > 0 || friendlyAirbases.Count > 0;
        bool hasEnemySource = enemyUnits > 0 || enemyAirbases.Count > 0;
        if (!hasFriendlySource || !hasEnemySource)
        {
            CommanderPlugin.Log.LogInfo(
                $"SAM influence field waiting: friendlyUnits={friendlyUnits}, enemyUnits={enemyUnits}, "
                + $"friendlyAirbases={friendlyAirbases.Count}, enemyAirbases={enemyAirbases.Count}.");
            return false;
        }

        strategicWeights = new float[cellCount];
        strategicRisks = new float[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            float friendly = friendlyInfluence[i];
            float enemy = enemyInfluence[i];
            float total = friendly + enemy;
            if (total <= 0.001f)
            {
                strategicWeights[i] = 0f;
                strategicRisks[i] = 0.5f;
                continue;
            }

            float balance = 1f - (Mathf.Abs(friendly - enemy) / total);
            float presence = 1f - Mathf.Exp(-total * 0.35f);
            float enemyShare = enemy / total;
            float contestedRelevance = balance * presence;
            float enemyTerritoryRelevance = enemyShare * presence * 0.75f;
            strategicWeights[i] = Mathf.Max(contestedRelevance, enemyTerritoryRelevance);
            strategicRisks[i] = enemyShare;
        }

        CommanderPlugin.Log.LogInfo(
            $"SAM influence field ready: resolution={StrategicWeightResolution}x{StrategicWeightResolution}, "
            + $"friendlyUnits={friendlyUnits}, enemyUnits={enemyUnits}, "
            + $"friendlyAirbases={friendlyAirbases.Count}, enemyAirbases={enemyAirbases.Count}.");
        return true;
    }

    private void AddInfluence(float[] field, GlobalPosition position, float weight, float radius)
    {
        if (mapSettings == null || radius <= 0f || weight <= 0f)
        {
            return;
        }

        float cellWidth = mapSettings.MapSize.x / (StrategicWeightResolution - 1);
        float cellHeight = mapSettings.MapSize.y / (StrategicWeightResolution - 1);
        float centerX = ((position.x / mapSettings.MapSize.x) + 0.5f) * (StrategicWeightResolution - 1);
        float centerY = ((position.z / mapSettings.MapSize.y) + 0.5f) * (StrategicWeightResolution - 1);
        int radiusX = Mathf.CeilToInt(radius / cellWidth);
        int radiusY = Mathf.CeilToInt(radius / cellHeight);
        int minX = Mathf.Max(0, Mathf.FloorToInt(centerX) - radiusX);
        int maxX = Mathf.Min(StrategicWeightResolution - 1, Mathf.CeilToInt(centerX) + radiusX);
        int minY = Mathf.Max(0, Mathf.FloorToInt(centerY) - radiusY);
        int maxY = Mathf.Min(StrategicWeightResolution - 1, Mathf.CeilToInt(centerY) + radiusY);
        float sigma = radius / 3f;
        float inverseTwoSigmaSquared = 1f / (2f * sigma * sigma);
        for (int y = minY; y <= maxY; y++)
        {
            float worldZ = ((y / (float)(StrategicWeightResolution - 1)) - 0.5f) * mapSettings.MapSize.y;
            float dz = worldZ - position.z;
            for (int x = minX; x <= maxX; x++)
            {
                float worldX = ((x / (float)(StrategicWeightResolution - 1)) - 0.5f) * mapSettings.MapSize.x;
                float dx = worldX - position.x;
                float distanceSquared = (dx * dx) + (dz * dz);
                if (distanceSquared > radius * radius)
                {
                    continue;
                }

                field[(y * StrategicWeightResolution) + x] +=
                    weight * Mathf.Exp(-distanceSquared * inverseTwoSigmaSquared);
            }
        }
    }

    private float SampleStrategicField(float[] field, GlobalPosition position)
    {
        if (mapSettings == null)
        {
            return 0f;
        }

        float u = Mathf.Clamp01((position.x / mapSettings.MapSize.x) + 0.5f) * (StrategicWeightResolution - 1);
        float v = Mathf.Clamp01((position.z / mapSettings.MapSize.y) + 0.5f) * (StrategicWeightResolution - 1);
        int x0 = Mathf.FloorToInt(u);
        int y0 = Mathf.FloorToInt(v);
        int x1 = Mathf.Min(x0 + 1, StrategicWeightResolution - 1);
        int y1 = Mathf.Min(y0 + 1, StrategicWeightResolution - 1);
        float bottom = Mathf.Lerp(field[(y0 * StrategicWeightResolution) + x0], field[(y0 * StrategicWeightResolution) + x1], u - x0);
        float top = Mathf.Lerp(field[(y1 * StrategicWeightResolution) + x0], field[(y1 * StrategicWeightResolution) + x1], u - x0);
        return Mathf.Lerp(bottom, top, v - y0);
    }

    private Vector2 FindEnemyDirectionFromAnchors(GlobalPosition position)
    {
        if (enemyAirbases.Count == 0)
        {
            return Vector2.up;
        }

        List<(float Distance, GlobalPosition Position)> nearest = new(enemyAirbases.Count);
        for (int i = 0; i < enemyAirbases.Count; i++)
        {
            nearest.Add((HorizontalSquareDistance(position, enemyAirbases[i]), enemyAirbases[i]));
        }

        nearest.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));

        Vector2 average = Vector2.zero;
        int count = Mathf.Min(3, nearest.Count);
        for (int i = 0; i < count; i++)
        {
            average += new Vector2(
                nearest[i].Position.x - position.x,
                nearest[i].Position.z - position.z).normalized;
        }

        return average.sqrMagnitude > 0.01f ? average.normalized : Vector2.up;
    }
}
