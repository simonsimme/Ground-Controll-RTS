using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteAnalyzerService
{
    private static Vector2 FindEnemyFrontDirection(GlobalPosition position)
    {
        List<(float Distance, GlobalPosition Position)> enemyAirbases = new();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || DynamicMap.GetFactionMode(hq) != FactionMode.Enemy)
            {
                continue;
            }

            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (airbase?.center == null)
                {
                    continue;
                }

                GlobalPosition airbasePosition = airbase.center.GlobalPosition();
                enemyAirbases.Add((
                    HorizontalSquareDistance(position, airbasePosition),
                    airbasePosition));
            }
        }

        enemyAirbases.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int count = Mathf.Min(3, enemyAirbases.Count);
        if (count == 0)
        {
            return Vector2.up;
        }

        Vector2 averageDirection = Vector2.zero;
        for (int i = 0; i < count; i++)
        {
            GlobalPosition airbasePosition = enemyAirbases[i].Position;
            averageDirection += new Vector2(
                airbasePosition.x - position.x,
                airbasePosition.z - position.z).normalized;
        }

        return averageDirection.sqrMagnitude > 0.01f
            ? averageDirection.normalized
            : Vector2.up;
    }

    private static Vector2 Rotate(Vector2 value, float angle)
    {
        float sin = Mathf.Sin(angle);
        float cos = Mathf.Cos(angle);
        return new Vector2(
            (value.x * cos) - (value.y * sin),
            (value.x * sin) + (value.y * cos));
    }

    private static string BuildMapKey(MapSettings settings)
    {
        int prefix = settings.NetworkMap != null ? settings.NetworkMap.MapPrefix : 0;
        return string.Join(
            "|",
            Application.version,
            settings.name,
            prefix,
            settings.MapSize.x.ToString("R", CultureInfo.CurrentCulture),
            settings.MapSize.y.ToString("R", CultureInfo.CurrentCulture),
            settings.GridSizeX,
            settings.GridSizeY,
            settings.OffsetX,
            settings.OffsetY,
            RegionalSampleSpacing.ToString("R", CultureInfo.CurrentCulture),
            CandidateRegionSize.ToString("R", CultureInfo.CurrentCulture));
    }

    private void BuildSiteLayouts()
    {
        siteLayout.Clear();
        if (!HasActiveSite)
        {
            return;
        }

        int siteIndex = activeCandidateId;
        SiteCandidate site = activeSite;
        List<GlobalPosition> occupied = new() { site.Position };
        siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Radar, site.Position));

        Vector2 enemyDirection = FindEnemyFrontDirection(site.Position);
        Vector2 rearDirection = -enemyDirection;
        int seed = Mathf.Abs(
                Mathf.RoundToInt((site.Position.x * 0.17f) + (site.Position.z * 0.31f)));
        GlobalPosition platform = FindLayoutPosition(
                site,
                Rotate(rearDirection, -55f * Mathf.Deg2Rad),
                240f,
                35f,
                minimumNormalY: 0.78f,
                forwardVisibilityPreference: -1,
                enemyDirection,
                occupied);
        occupied.Add(platform);
        siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Platform, platform));
        float towerAngle = ((((seed * 37) % 6001) / 100f) - 30f) * Mathf.Deg2Rad;
        Vector2 towerOffset = Rotate(enemyDirection, towerAngle) * 40f;
        GlobalPosition controlTowerGround = SnapLayoutPointToTerrain(
                platform.x + towerOffset.x,
                platform.z + towerOffset.y,
                platform);
        occupied.Add(controlTowerGround);
        siteLayout.Add(new SiteLayoutMarker(
                siteIndex,
                SiteUnitRole.ControlTower,
                new GlobalPosition(
                    controlTowerGround.x,
                    controlTowerGround.y - 20f,
                    controlTowerGround.z)));

        int gunCount = 2 + (seed % 2);
        int irmCount = 2 + ((seed / 2) % 3);
        for (int gunIndex = 0; gunIndex < gunCount; gunIndex++)
        {
            float arc = gunIndex == 0
                ? 0f
                : gunCount == 2
                    ? 0f
                    : gunIndex == 1 ? -55f : 55f;
            float radius = gunIndex == 0 ? 55f : 145f;
            GlobalPosition gun = FindLayoutPosition(
                site,
                Rotate(enemyDirection, arc * Mathf.Deg2Rad),
                radius,
                gunIndex == 0 ? 45f : 20f,
                minimumNormalY: 0.88f,
                forwardVisibilityPreference: 1,
                enemyDirection,
                occupied);
            occupied.Add(gun);
            siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Gun23mm, gun));
        }

        for (int irmIndex = 0; irmIndex < irmCount; irmIndex++)
        {
            float spread = irmCount <= 2
                ? (irmIndex == 0 ? -32f : 32f)
                : Mathf.Lerp(-65f, 65f, irmIndex / (float)(irmCount - 1));
            GlobalPosition irm = FindLayoutPosition(
                site,
                Rotate(enemyDirection, spread * Mathf.Deg2Rad),
                105f + ((irmIndex % 2) * 35f),
                24f,
                minimumNormalY: 0.88f,
                forwardVisibilityPreference: 1,
                enemyDirection,
                occupied);
            occupied.Add(irm);
            siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Irm, irm));
        }

        GlobalPosition batteryAnchor = FindBatteryAnchor(
                site,
                rearDirection,
                enemyDirection,
                platform,
                occupied);

        const int launcherCount = 3;
        for (int launcherIndex = 0; launcherIndex < launcherCount; launcherIndex++)
        {
            float angle = launcherIndex * (Mathf.PI * 2f / launcherCount);
            GlobalPosition launcher = FindLayoutPositionAroundAnchor(
                site,
                batteryAnchor,
                new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)),
                58f,
                18f,
                minimumNormalY: 0.9f,
                forwardVisibilityPreference: -1,
                enemyDirection,
                occupied,
                keepAwayFrom: platform,
                keepAwayDistance: 100f);
            occupied.Add(launcher);
            siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.StratoLauncher, launcher));
        }

        Vector2 batteryCross = new(-rearDirection.y, rearDirection.x);
        GlobalPosition ammo = FindLayoutPositionAroundAnchor(
                site,
                batteryAnchor,
                batteryCross,
                28f,
                20f,
                minimumNormalY: 0.94f,
                forwardVisibilityPreference: -1,
                enemyDirection,
                occupied);
        occupied.Add(ammo);
        siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Ammo, ammo));

        GlobalPosition fireControl = FindLayoutPositionAroundAnchor(
                site,
                batteryAnchor,
                -batteryCross,
                28f,
                20f,
                minimumNormalY: 0.94f,
                forwardVisibilityPreference: -1,
                enemyDirection,
                occupied);
        siteLayout.Add(new SiteLayoutMarker(
                siteIndex,
                SiteUnitRole.FireControl,
                fireControl));
    }

    private GlobalPosition FindLayoutPosition(
        SiteCandidate site,
        Vector2 preferredDirection,
        float preferredRadius,
        float angleSpreadDegrees,
        float minimumNormalY,
        int forwardVisibilityPreference,
        Vector2 enemyDirection,
        List<GlobalPosition> occupied)
    {
        GlobalPosition best = default;
        float bestScore = float.MinValue;
        float[] radiusOffsets = { -35f, 0f, 35f };
        float[] angleOffsets = { -angleSpreadDegrees, 0f, angleSpreadDegrees };
        for (int radiusIndex = 0; radiusIndex < radiusOffsets.Length; radiusIndex++)
        {
            float radius = Mathf.Clamp(
                preferredRadius + radiusOffsets[radiusIndex],
                40f,
                SiteControlRadius - 15f);
            for (int angleIndex = 0; angleIndex < angleOffsets.Length; angleIndex++)
            {
                Vector2 direction = Rotate(
                    preferredDirection,
                    angleOffsets[angleIndex] * Mathf.Deg2Rad);
                if (!TrySampleLayoutGround(
                    site.Position,
                    direction * radius,
                    out GlobalPosition position,
                    out float normalY))
                {
                    continue;
                }

                bool blockedByLayout = false;
                for (int occupiedIndex = 0; occupiedIndex < occupied.Count; occupiedIndex++)
                {
                    if (HorizontalSquareDistance(position, occupied[occupiedIndex]) < 35f * 35f)
                    {
                        blockedByLayout = true;
                        break;
                    }
                }

                if (blockedByLayout)
                {
                    continue;
                }

                float visibleDistance = forwardVisibilityPreference == 0
                    ? 0f
                    : GetForwardVisibility(position, enemyDirection, 12000f);
                float score =
                    (normalY * 600f)
                    + (visibleDistance * 0.03f * forwardVisibilityPreference)
                    - (Mathf.Abs(radius - preferredRadius) * 0.5f);
                if (normalY < minimumNormalY)
                {
                    score -= (minimumNormalY - normalY) * 2000f;
                }

                if (score > bestScore)
                {
                    best = position;
                    bestScore = score;
                }
            }
        }

        if (bestScore > float.MinValue)
        {
            return best;
        }

        Vector2 fallback = preferredDirection.normalized * preferredRadius;
        return SnapLayoutPointToTerrain(
            site.Position.x + fallback.x,
            site.Position.z + fallback.y,
            site.Position);
    }

    private GlobalPosition FindBatteryAnchor(
        SiteCandidate site,
        Vector2 rearDirection,
        Vector2 enemyDirection,
        GlobalPosition platform,
        List<GlobalPosition> occupied)
    {
        GlobalPosition best = default;
        float bestScore = float.MinValue;
        float bestHeightRange = float.MaxValue;
        float bestMinimumNormalY = 0f;
        float[] radii = { 105f, 125f, 145f, 165f, 185f, 205f, 225f };
        for (int radiusIndex = 0; radiusIndex < radii.Length; radiusIndex++)
        {
            for (float angle = -110f; angle <= 110f; angle += 10f)
            {
                Vector2 direction = Rotate(rearDirection, angle * Mathf.Deg2Rad);
                if (!TrySampleLayoutGround(
                    site.Position,
                    direction * radii[radiusIndex],
                    out GlobalPosition position,
                    out float normalY)
                    || normalY < 0.75f
                    || HorizontalSquareDistance(position, platform) < 110f * 110f)
                {
                    continue;
                }

                bool blockedByLayout = false;
                for (int occupiedIndex = 0; occupiedIndex < occupied.Count; occupiedIndex++)
                {
                    if (HorizontalSquareDistance(position, occupied[occupiedIndex]) < 55f * 55f)
                    {
                        blockedByLayout = true;
                        break;
                    }
                }

                if (blockedByLayout)
                {
                    continue;
                }

                if (!TryEvaluateBatteryFootprint(
                    position,
                    72f,
                    out float heightRange,
                    out float minimumNormalY,
                    out float averageNormalY))
                {
                    continue;
                }

                float forwardVisibility = GetForwardVisibility(position, enemyDirection, 12000f);
                float rearPreference = Mathf.Max(0f, Vector2.Dot(direction.normalized, rearDirection));
                float score =
                    (-heightRange * 1800f)
                    + (minimumNormalY * 1200f)
                    + (averageNormalY * 800f)
                    + (rearPreference * 350f)
                    - (forwardVisibility * 0.015f)
                    - (Mathf.Abs(radii[radiusIndex] - 175f) * 0.5f);
                if (score > bestScore)
                {
                    best = position;
                    bestScore = score;
                    bestHeightRange = heightRange;
                    bestMinimumNormalY = minimumNormalY;
                }
            }
        }

        if (bestScore > float.MinValue)
        {
            CommanderPlugin.Log.LogInfo(
                $"SAM battery terrain selected: heightRange={bestHeightRange:0.00}m, "
                + $"minimumNormalY={bestMinimumNormalY:0.000}, distance={CommanderGameAccess.HorizontalDistance(site.Position.ToLocalPosition(), best.ToLocalPosition()):0}m.");
            return best;
        }

        return FindLayoutPosition(
            site,
            rearDirection,
            190f,
            70f,
            minimumNormalY: 0.9f,
            forwardVisibilityPreference: -1,
            enemyDirection,
            occupied);
    }

    private bool TryEvaluateBatteryFootprint(
        GlobalPosition center,
        float radius,
        out float heightRange,
        out float minimumNormalY,
        out float averageNormalY)
    {
        const float spacing = 12f;
        float minimumHeight = float.MaxValue;
        float maximumHeight = float.MinValue;
        float normalTotal = 0f;
        int heightSamples = 0;
        int normalSamples = 0;
        minimumNormalY = 1f;

        for (float z = -radius; z <= radius; z += spacing)
        {
            for (float x = -radius; x <= radius; x += spacing)
            {
                if ((x * x) + (z * z) > radius * radius
                    || !TryGetDetailedHeight(center.x + x, center.z + z, out float height)
                    || height <= 1f)
                {
                    continue;
                }

                minimumHeight = Mathf.Min(minimumHeight, height);
                maximumHeight = Mathf.Max(maximumHeight, height);
                heightSamples++;

                if (((Mathf.RoundToInt(x / spacing) + Mathf.RoundToInt(z / spacing)) & 1) != 0)
                {
                    continue;
                }

                float normalY = EstimateDetailedNormalY(center.x + x, center.z + z);
                minimumNormalY = Mathf.Min(minimumNormalY, normalY);
                normalTotal += normalY;
                normalSamples++;
            }
        }

        if (heightSamples < 80 || normalSamples == 0)
        {
            heightRange = float.MaxValue;
            minimumNormalY = 0f;
            averageNormalY = 0f;
            return false;
        }

        heightRange = maximumHeight - minimumHeight;
        averageNormalY = normalTotal / normalSamples;
        return true;
    }

    private GlobalPosition FindLayoutPositionAroundAnchor(
        SiteCandidate site,
        GlobalPosition anchor,
        Vector2 preferredDirection,
        float preferredRadius,
        float angleSpreadDegrees,
        float minimumNormalY,
        int forwardVisibilityPreference,
        Vector2 enemyDirection,
        List<GlobalPosition> occupied,
        GlobalPosition? keepAwayFrom = null,
        float keepAwayDistance = 0f)
    {
        GlobalPosition best = default;
        float bestScore = float.MinValue;
        float[] radiusOffsets = { -12f, 0f, 12f };
        float[] angleOffsets = { -angleSpreadDegrees, 0f, angleSpreadDegrees };
        for (int radiusIndex = 0; radiusIndex < radiusOffsets.Length; radiusIndex++)
        {
            float radius = Mathf.Max(12f, preferredRadius + radiusOffsets[radiusIndex]);
            for (int angleIndex = 0; angleIndex < angleOffsets.Length; angleIndex++)
            {
                Vector2 direction = Rotate(
                    preferredDirection,
                    angleOffsets[angleIndex] * Mathf.Deg2Rad);
                if (!TrySampleLayoutGround(
                    anchor,
                    direction * radius,
                    out GlobalPosition position,
                    out float normalY)
                    || HorizontalSquareDistance(position, site.Position)
                    > (SiteControlRadius - 10f) * (SiteControlRadius - 10f))
                {
                    continue;
                }

                if (keepAwayFrom.HasValue
                    && HorizontalSquareDistance(position, keepAwayFrom.Value)
                        < keepAwayDistance * keepAwayDistance)
                {
                    continue;
                }

                bool blockedByLayout = false;
                for (int occupiedIndex = 0; occupiedIndex < occupied.Count; occupiedIndex++)
                {
                    if (HorizontalSquareDistance(position, occupied[occupiedIndex]) < 24f * 24f)
                    {
                        blockedByLayout = true;
                        break;
                    }
                }

                if (blockedByLayout)
                {
                    continue;
                }

                float visibleDistance = forwardVisibilityPreference == 0
                    ? 0f
                    : GetForwardVisibility(position, enemyDirection, 12000f);
                float score =
                    (normalY * 700f)
                    + (visibleDistance * 0.03f * forwardVisibilityPreference);
                if (normalY < minimumNormalY)
                {
                    score -= (minimumNormalY - normalY) * 2500f;
                }

                if (score > bestScore)
                {
                    best = position;
                    bestScore = score;
                }
            }
        }

        if (bestScore > float.MinValue)
        {
            return best;
        }

        Vector2 fallback = preferredDirection.normalized * preferredRadius;
        return SnapLayoutPointToTerrain(
            anchor.x + fallback.x,
            anchor.z + fallback.y,
            anchor);
    }

    private bool TrySampleLayoutGround(
        GlobalPosition center,
        Vector2 offset,
        out GlobalPosition position,
        out float normalY)
    {
        GlobalPosition requestedPosition = new(
            center.x + offset.x,
            center.y,
            center.z + offset.y);
        if (TryGetDetailedHeight(requestedPosition.x, requestedPosition.z, out float height)
            && height > 1f)
        {
            position = new GlobalPosition(requestedPosition.x, height, requestedPosition.z);
            normalY = EstimateDetailedNormalY(requestedPosition.x, requestedPosition.z);
            return true;
        }

        position = default;
        normalY = 0f;
        return false;
    }

    private float GetForwardVisibility(
        GlobalPosition position,
        Vector2 direction,
        float maximumDistance)
    {
        float sourceHeight = position.y + 3f;
        for (float distance = 20f; distance <= maximumDistance; distance += 20f)
        {
            float x = position.x + (direction.x * distance);
            float z = position.z + (direction.y * distance);
            if (!TryGetDetailedHeight(x, z, out float height))
            {
                return distance;
            }

            if (height > sourceHeight)
            {
                return distance;
            }
        }

        return maximumDistance;
    }

    private GlobalPosition SnapLayoutPointToTerrain(
        float globalX,
        float globalZ,
        GlobalPosition fallback)
    {
        if (TryGetDetailedHeight(globalX, globalZ, out float height))
        {
            return new GlobalPosition(globalX, height, globalZ);
        }

        return fallback;
    }

    private bool TryGetDetailedHeight(float x, float z, out float height)
    {
        for (int i = 0; i < localSiteMaps.Count; i++)
        {
            if (localSiteMaps[i].TryGetHeight(x, z, out height))
            {
                return true;
            }
        }

        return strategicHeightMap.TryGetHeight(x, z, out height);
    }

    private float EstimateDetailedNormalY(float x, float z)
    {
        for (int i = 0; i < localSiteMaps.Count; i++)
        {
            if (localSiteMaps[i].Contains(x, z))
            {
                return localSiteMaps[i].EstimateNormalY(x, z, 2f);
            }
        }

        return strategicHeightMap.EstimateNormalY(x, z, 20f);
    }

    private void ResetAnalysisData()
    {
        State = AnalyzerState.Waiting;
        candidates.Clear();
        suggestedSites.Clear();
        siteLayout.Clear();
        strategicWeights = null;
        strategicRisks = null;
        regionColumns = 0;
        regionRows = 0;
        regionIndex = 0;
        coverageCandidateIndex = 0;
        ResetCoverageAccumulator();
        activeCandidateId = -1;
        activeRefinementGeneration++;
        nextNearbyRefreshAt = 0f;
        nextInfluenceRefreshAt = 0f;
        localHeightMapBaker.Reset();
        localSiteMaps.Clear();
        localRefinementActive = false;
        ClearCoverageOverlay();
    }
}
