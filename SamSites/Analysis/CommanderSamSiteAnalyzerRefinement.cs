using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteAnalyzerService
{
    private static bool TryGetTerrainSurface(float globalX, float globalZ, out GlobalPosition position)
    {
        position = default;
        if (GameAssets.i == null)
        {
            return false;
        }

        Vector3 local = new GlobalPosition(globalX, 0f, globalZ).ToLocalPosition();
        Vector3 origin = new(local.x, Datum.LocalSeaY + 10000f, local.z);
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            Vector3.down,
            20000f,
            PhysicsLayers.StaticsMask,
            QueryTriggerInteraction.Ignore);
        float highestTerrainY = float.MinValue;
        Vector3 terrainPoint = default;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider != null
                && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial
                && hit.point.y > highestTerrainY)
            {
                highestTerrainY = hit.point.y;
                terrainPoint = hit.point;
            }
        }

        if (highestTerrainY <= float.MinValue)
        {
            return false;
        }

        position = terrainPoint.ToGlobalPosition();
        return true;
    }

    private static void InsertLocalRadarSeed(
        List<LocalRadarSeed> seeds,
        LocalRadarSeed seed,
        int limit)
    {
        int index = 0;
        while (index < seeds.Count && seeds[index].TerrainScore >= seed.TerrainScore)
        {
            index++;
        }

        seeds.Insert(index, seed);
        if (seeds.Count > limit)
        {
            seeds.RemoveAt(seeds.Count - 1);
        }
    }

    private static float CalculateLocalRadarOpenness(
        CommanderLocalHeightMapBaker.LocalHeightMap map,
        LocalRadarSeed seed)
    {
        const int directionCount = 12;
        const float step = 25f;
        const float range = 250f;
        int visible = 0;
        int samples = 0;
        float sourceHeight = seed.Height + RadarHeight;
        for (int directionIndex = 0; directionIndex < directionCount; directionIndex++)
        {
            float angle = directionIndex * (Mathf.PI * 2f / directionCount);
            float highestTerrainSlope = float.MinValue;
            for (float distance = step; distance <= range; distance += step)
            {
                float x = seed.Position.x + (Mathf.Cos(angle) * distance);
                float z = seed.Position.z + (Mathf.Sin(angle) * distance);
                if (!map.TryGetHeight(x, z, out float terrainHeight))
                {
                    continue;
                }

                float targetSlope = (terrainHeight + RadarHeight - sourceHeight) / distance;
                if (targetSlope >= highestTerrainSlope)
                {
                    visible++;
                }

                highestTerrainSlope = Mathf.Max(
                    highestTerrainSlope,
                    (terrainHeight - sourceHeight) / distance);
                samples++;
            }
        }

        return samples == 0 ? 0f : visible / (float)samples;
    }

    private void ClearActiveSite()
    {
        activeRefinementGeneration++;
        activeCandidateId = -1;
        siteLayout.Clear();
        localSiteMaps.Clear();
        localRefinementActive = false;
        localHeightMapBaker.Reset();
        if (State == AnalyzerState.Refining)
        {
            State = AnalyzerState.Ready;
        }

        ClearCoverageOverlay();
        CompleteAutomaticSelection(false);
    }

    private void BeginActiveSiteRefinement(SiteCandidate candidate)
    {
        siteLayout.Clear();
        localSiteMaps.Clear();
        localHeightMapBaker.Reset();
        ClearCoverageOverlay();
        activeCandidateId = candidate.CandidateId;
        activeSite = candidate;
        refinementStartedAt = Time.realtimeSinceStartup;
        State = AnalyzerState.Refining;
        localRefinementActive = true;
        int requestedCandidateId = candidate.CandidateId;
        int requestedGeneration = ++activeRefinementGeneration;
        GlobalPosition strategicPeak = FindStrategicPeak(candidate.Position, LocalSnapRadius);
        StatusText = "Baking detailed 600 m terrain for the selected site.";
        if (!localHeightMapBaker.TryBake(
            strategicPeak,
            600f,
            localMap => CompleteActiveSiteRefinement(
                requestedCandidateId,
                requestedGeneration,
                localMap)))
        {
            State = AnalyzerState.Ready;
            localRefinementActive = false;
            StatusText = "Could not bake detailed terrain for the selected SAM site.";
            CompleteAutomaticSelection(false);
        }
    }

    private GlobalPosition FindStrategicPeak(GlobalPosition center, float radius)
    {
        float spacingX = strategicHeightMap.ResolutionX > 0
            ? strategicHeightMap.MapSize.x / strategicHeightMap.ResolutionX
            : 20f;
        float spacingZ = strategicHeightMap.ResolutionY > 0
            ? strategicHeightMap.MapSize.y / strategicHeightMap.ResolutionY
            : 20f;
        float spacing = Mathf.Max(5f, Mathf.Min(spacingX, spacingZ));
        GlobalPosition best = center;
        float bestHeight = center.y;
        for (float z = -radius; z <= radius; z += spacing)
        {
            for (float x = -radius; x <= radius; x += spacing)
            {
                if ((x * x) + (z * z) > radius * radius
                    || !strategicHeightMap.TryGetHeightNearest(
                        center.x + x,
                        center.z + z,
                        out float height)
                    || height <= bestHeight)
                {
                    continue;
                }

                bestHeight = height;
                best = new GlobalPosition(center.x + x, height, center.z + z);
            }
        }

        return best;
    }

    private void CompleteActiveSiteRefinement(
        int requestedCandidateId,
        int requestedGeneration,
        CommanderLocalHeightMapBaker.LocalHeightMap? localMap)
    {
        if (!localRefinementActive
            || requestedCandidateId != activeCandidateId
            || requestedGeneration != activeRefinementGeneration)
        {
            return;
        }

        if (localMap == null)
        {
            State = AnalyzerState.Ready;
            localRefinementActive = false;
            StatusText = "Detailed terrain readback for the selected SAM site failed.";
            CompleteAutomaticSelection(false);
            return;
        }

        SiteCandidate site = activeSite;
        List<LocalRadarSeed> radarSeeds = new(24);
        const float spacing = 4f;
        for (float z = -LocalSnapRadius; z <= LocalSnapRadius; z += spacing)
        {
            for (float x = -LocalSnapRadius; x <= LocalSnapRadius; x += spacing)
            {
                float distance = Mathf.Sqrt((x * x) + (z * z));
                if (distance > LocalSnapRadius)
                {
                    continue;
                }

                float sampleX = localMap.Center.x + x;
                float sampleZ = localMap.Center.z + z;
                if (!localMap.TryGetHeight(sampleX, sampleZ, out float height))
                {
                    continue;
                }

                float normalY = localMap.EstimateNormalY(sampleX, sampleZ, 2f);
                if (normalY < 0.65f)
                {
                    continue;
                }

                float terrainScore = ((height - site.Height) * 10f)
                    + (normalY * 30f)
                    - (distance * 0.03f);
                InsertLocalRadarSeed(
                    radarSeeds,
                    new LocalRadarSeed(
                        new GlobalPosition(sampleX, height, sampleZ),
                        height,
                        normalY,
                        terrainScore),
                    24);
            }
        }

        LocalRadarSeed best = new(
            site.Position,
            site.Height,
            Mathf.Cos(site.SlopeDegrees * Mathf.Deg2Rad),
            float.MinValue);
        float bestScore = float.MinValue;
        for (int i = 0; i < radarSeeds.Count; i++)
        {
            LocalRadarSeed seed = radarSeeds[i];
            float score = seed.TerrainScore + (CalculateLocalRadarOpenness(localMap, seed) * 80f);
            if (score > bestScore)
            {
                best = seed;
                bestScore = score;
            }
        }

        site.Position = best.Position;
        site.Height = best.Height;
        site.SlopeDegrees = Mathf.Acos(Mathf.Clamp(best.NormalY, -1f, 1f)) * Mathf.Rad2Deg;
        site.Risk = CalculateRisk(site.Position);
        activeSite = site;
        localSiteMaps.Add(localMap);
        StatusText = "Baking precise 50 m radar terrain around the local peak.";
        if (localHeightMapBaker.TryBake(
            site.Position,
            50f,
            peakMap => CompleteRadarPeakRefinement(
                requestedCandidateId,
                requestedGeneration,
                peakMap)))
        {
            return;
        }

        FinalizeActiveSiteRefinement(site);
    }

    private void CompleteRadarPeakRefinement(
        int requestedCandidateId,
        int requestedGeneration,
        CommanderLocalHeightMapBaker.LocalHeightMap? peakMap)
    {
        if (!localRefinementActive
            || requestedCandidateId != activeCandidateId
            || requestedGeneration != activeRefinementGeneration)
        {
            return;
        }

        SiteCandidate site = activeSite;
        if (peakMap != null)
        {
            float spacing = Mathf.Max(0.25f, peakMap.MetersPerPixel * 2f);
            float half = (peakMap.Size * 0.5f) - spacing;
            float bestHeight = float.MinValue;
            GlobalPosition bestPosition = site.Position;
            float bestNormalY = Mathf.Cos(site.SlopeDegrees * Mathf.Deg2Rad);
            for (float z = -half; z <= half; z += spacing)
            {
                for (float x = -half; x <= half; x += spacing)
                {
                    float sampleX = peakMap.Center.x + x;
                    float sampleZ = peakMap.Center.z + z;
                    if (!peakMap.TryGetHeight(sampleX, sampleZ, out float height)
                        || height <= bestHeight)
                    {
                        continue;
                    }

                    float normalY = peakMap.EstimateNormalY(sampleX, sampleZ, spacing);
                    if (normalY < 0.55f)
                    {
                        continue;
                    }

                    bestHeight = height;
                    bestPosition = new GlobalPosition(sampleX, height, sampleZ);
                    bestNormalY = normalY;
                }
            }

            if (TryGetTerrainSurface(bestPosition.x, bestPosition.z, out GlobalPosition terrainPosition))
            {
                bestPosition = terrainPosition;
                bestHeight = terrainPosition.y;
            }

            site.Position = bestPosition;
            site.Height = bestHeight;
            site.SlopeDegrees = Mathf.Acos(Mathf.Clamp(bestNormalY, -1f, 1f)) * Mathf.Rad2Deg;
            site.Risk = CalculateRisk(site.Position);
        }

        FinalizeActiveSiteRefinement(site);
    }

    private void FinalizeActiveSiteRefinement(SiteCandidate site)
    {
        activeSite = site;
        BuildSiteLayouts();
        localSiteMaps.Clear();
        localRefinementActive = false;
        State = AnalyzerState.Ready;
        if (CoverageOverlayEnabled)
        {
            BeginCoverageOverlay(activeSite);
        }

        CommanderTacticalMapService.Instance?.JumpCameraToPosition(activeSite.Position);
        StatusText = "Selected SAM site refined and ready.";
        CommanderPlugin.Log.LogInfo(
            $"SAM selected-site refinement complete: duration={Time.realtimeSinceStartup - refinementStartedAt:0.000}s, "
            + $"candidate={activeCandidateId}, layoutWindow=600m, radarWindow=50m, resolution=1024.");
        CompleteAutomaticSelection(true);
    }

    private void CompleteAutomaticSelection(bool success)
    {
        Action<bool>? completed = automaticSelectionCompleted;
        automaticSelectionCompleted = null;
        completed?.Invoke(success);
    }
}
