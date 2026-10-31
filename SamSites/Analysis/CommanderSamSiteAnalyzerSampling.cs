using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteAnalyzerService
{
    private static void InsertSeed(List<TerrainSeed> seeds, TerrainSeed seed, int limit)
    {
        int index = 0;
        while (index < seeds.Count && seeds[index].Score >= seed.Score)
        {
            index++;
        }

        seeds.Insert(index, seed);
        if (seeds.Count > limit)
        {
            seeds.RemoveAt(seeds.Count - 1);
        }
    }

    private void TryStart()
    {
        if (!MissionManager.IsRunning
            || Time.timeSinceLevelLoad < StartupDelaySeconds
            || NetworkSceneSingleton<LevelInfo>.i?.LoadedMapSettings == null)
        {
            return;
        }

        mapSettings = NetworkSceneSingleton<LevelInfo>.i.LoadedMapSettings;
        mapKey = BuildMapKey(mapSettings);
        analysisStartedAt = Time.realtimeSinceStartup;
        strategicHeightMap.TryStart(mapSettings);
        State = AnalyzerState.Sampling;
        StatusText = "Baking strategic terrain heightmap.";
    }

    private void BeginSampling(MapSettings settings)
    {
        candidates.Clear();
        suggestedSites.Clear();
        regionColumns = Mathf.Max(1, Mathf.CeilToInt(settings.MapSize.x / CandidateRegionSize));
        regionRows = Mathf.Max(1, Mathf.CeilToInt(settings.MapSize.y / CandidateRegionSize));
        regionIndex = 0;
        coverageCandidateIndex = 0;
        analysisStartedAt = Time.realtimeSinceStartup;
        samplingStartedAt = analysisStartedAt;
        State = AnalyzerState.Sampling;
        StatusText = $"Extracting terrain features from {regionColumns * regionRows} regions.";
        CommanderPlugin.Log.LogInfo(
            $"SAM regional analysis started: map={mapKey}, regions={regionColumns}x{regionRows}, "
            + $"regionSize={CandidateRegionSize:0}m, sampleSpacing={RegionalSampleSpacing:0}m.");
    }

    private void SampleTerrainBatch()
    {
        if (!strategicHeightMap.IsReady)
        {
            if (mapSettings != null)
            {
                strategicHeightMap.TryStart(mapSettings);
            }

            StatusText = "Baking strategic terrain heightmap.";
            return;
        }

        if (regionColumns == 0 && mapSettings != null)
        {
            BeginSampling(mapSettings);
        }

        int regionsPerFrame = Mathf.Clamp(ScanQueriesPerFrame / 16, 1, 16);
        int end = Mathf.Min(regionIndex + regionsPerFrame, regionColumns * regionRows);
        for (; regionIndex < end; regionIndex++)
        {
            ExtractRegionCandidates(regionIndex);
        }

        StatusText = $"Extracting terrain regions: {regionIndex}/{regionColumns * regionRows}";
        if (regionIndex < regionColumns * regionRows)
        {
            return;
        }

        if (candidates.Count > CandidateLimit)
        {
            List<SiteCandidate> reduced = new(CandidateLimit);
            float stride = candidates.Count / (float)CandidateLimit;
            for (int i = 0; i < CandidateLimit; i++)
            {
                reduced.Add(candidates[Mathf.Min(
                    Mathf.FloorToInt(i * stride),
                    candidates.Count - 1)]);
            }

            candidates.Clear();
            candidates.AddRange(reduced);
        }

        if (candidates.Count == 0)
        {
            State = AnalyzerState.Failed;
            StatusText = "Regional terrain analysis found no viable SAM-site seeds.";
            return;
        }

        coverageCandidateIndex = 0;
        ResetCoverageAccumulator();
        RefreshStrategicAnchors();
        State = AnalyzerState.Coverage;
        StatusText = $"Checking strategic LOS for {candidates.Count} terrain candidates.";
        coverageStartedAt = Time.realtimeSinceStartup;
        CommanderPlugin.Log.LogInfo(
            $"SAM regional extraction complete: duration={coverageStartedAt - samplingStartedAt:0.000}s, "
            + $"candidates={candidates.Count}.");
    }

    private void ExtractRegionCandidates(int index)
    {
        if (mapSettings == null)
        {
            return;
        }

        int regionX = index % regionColumns;
        int regionY = index / regionColumns;
        float mapMinX = -mapSettings.MapSize.x * 0.5f;
        float mapMinZ = -mapSettings.MapSize.y * 0.5f;
        float minX = mapMinX + (regionX * CandidateRegionSize);
        float minZ = mapMinZ + (regionY * CandidateRegionSize);
        float maxX = Mathf.Min(minX + CandidateRegionSize, mapSettings.MapSize.x * 0.5f);
        float maxZ = Mathf.Min(minZ + CandidateRegionSize, mapSettings.MapSize.y * 0.5f);
        List<TerrainSeed> heightSeeds = new(24);

        for (float z = minZ + (RegionalSampleSpacing * 0.5f); z < maxZ; z += RegionalSampleSpacing)
        {
            for (float x = minX + (RegionalSampleSpacing * 0.5f); x < maxX; x += RegionalSampleSpacing)
            {
                if (!strategicHeightMap.TryGetHeightNearest(x, z, out float height) || height <= 1f)
                {
                    continue;
                }

                float farAverage = SampleAverageHeight(x, z, 1500f);
                float farProminence = height - farAverage;
                float normalY = strategicHeightMap.EstimateNormalY(x, z, 40f);
                float slopeDegrees = Mathf.Acos(Mathf.Clamp(normalY, -1f, 1f)) * Mathf.Rad2Deg;
                GlobalPosition position = new(x, height, z);
                InsertSeed(
                    heightSeeds,
                    new TerrainSeed(position, height, slopeDegrees, farProminence, height),
                    24);
            }
        }

        List<GlobalPosition> selected = new(3);
        AddFirstSeparatedSeed(heightSeeds, selected);
        AddFirstSeparatedSeed(heightSeeds, selected);
        AddFirstSeparatedSeed(heightSeeds, selected);
    }

    private float SampleAverageHeight(float x, float z, float radius)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            float angle = i * Mathf.PI * 0.5f;
            if (strategicHeightMap.TryGetHeightNearest(
                x + (Mathf.Cos(angle) * radius),
                z + (Mathf.Sin(angle) * radius),
                out float height))
            {
                total += height;
                count++;
            }
        }

        return count == 0 ? 0f : total / count;
    }

    private void AddFirstSeparatedSeed(List<TerrainSeed> seeds, List<GlobalPosition> regionSelected)
    {
        for (int i = 0; i < seeds.Count; i++)
        {
            TerrainSeed seed = seeds[i];
            bool tooClose = false;
            for (int selectedIndex = 0; selectedIndex < regionSelected.Count; selectedIndex++)
            {
                if (HorizontalSquareDistance(seed.Position, regionSelected[selectedIndex]) < CandidateSeparation * CandidateSeparation)
                {
                    tooClose = true;
                    break;
                }
            }

            if (tooClose)
            {
                continue;
            }

            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                if (HorizontalSquareDistance(seed.Position, candidates[candidateIndex].Position) < CandidateSeparation * CandidateSeparation)
                {
                    tooClose = true;
                    break;
                }
            }

            if (tooClose)
            {
                continue;
            }

            regionSelected.Add(seed.Position);
            candidates.Add(new SiteCandidate(
                seed.Position,
                seed.Height,
                seed.SlopeDegrees,
                seed.Prominence,
                0f,
                0f,
                seed.Score));
            return;
        }
    }
}
