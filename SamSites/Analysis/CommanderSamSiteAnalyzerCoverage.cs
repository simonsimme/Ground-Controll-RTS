using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteAnalyzerService
{
    private static int CoverageHorizonDistanceSteps => Mathf.CeilToInt(CoverageRange / CoverageHorizonSampleSpacing);

    private static int CoverageHorizonSampleCount => CoverageHorizonDirectionCount * CoverageHorizonDistanceSteps;

    private static float CalculateEngagementRangeValue(float distance)
    {
        if (distance <= 20000f)
        {
            return 1f;
        }

        return Mathf.Lerp(1f, 0.2f, Mathf.InverseLerp(20000f, CoverageRange, distance));
    }

    private void EvaluateCoverageBatch()
    {
        if (!strategicHeightMap.IsReady)
        {
            if (mapSettings != null)
            {
                strategicHeightMap.TryStart(mapSettings);
            }

            StatusText = "Waiting for strategic terrain before refreshing front influence.";
            return;
        }

        if (!EnsureStrategicInfluenceReady())
        {
            StatusText = "Waiting for friendly and enemy faction units before evaluating front coverage.";
            return;
        }

        int queryBudget = Mathf.Clamp(ScanQueriesPerFrame * 32, 2048, 16384);
        int queries = 0;
        while (queries < queryBudget && coverageCandidateIndex < candidates.Count)
        {
            if (coverageDirectionIndex >= CoverageDirectionCount)
            {
                SiteCandidate completed = candidates[coverageCandidateIndex];
                completed.Coverage = coverageTotalAreaWeight <= 0f
                    ? 0f
                    : coverageVisibleAreaWeight / coverageTotalAreaWeight;
                completed.StrategicCoverage = coverageTotalFrontWeight <= 0f
                    ? 0f
                    : coverageVisibleFrontWeight / coverageTotalFrontWeight;
                completed.ForwardCoverage = coverageForwardTotalWeight <= 0f
                    ? 0f
                    : coverageForwardVisibleWeight / coverageForwardTotalWeight;
                completed.Risk = CalculateRisk(completed.Position);
                float frontUtility = completed.StrategicCoverage
                    * Mathf.Lerp(0.35f, 1f, completed.Coverage);

                // Terrain metrics remain diagnostic inputs for seed generation, not final ranking.
                completed.Score = (
                    (completed.Coverage * 0.35f)
                    + (frontUtility * 0.65f)) * 1000f;
                candidates[coverageCandidateIndex++] = completed;
                ResetCoverageAccumulator();
                continue;
            }

            SiteCandidate candidate = candidates[coverageCandidateIndex];
            if (!coverageEnemyDirectionReady)
            {
                coverageEnemyDirection = FindEnemyDirectionFromAnchors(candidate.Position);
                coverageEnemyDirectionReady = true;
            }

            if (coverageDistance > CoverageRange)
            {
                coverageDirectionIndex++;
                coverageDistance = CoverageSampleSpacing;
                coverageHighestTerrainSlope = float.MinValue;
                continue;
            }

            float angle = coverageDirectionIndex * (Mathf.PI * 2f / CoverageDirectionCount);
            Vector2 sampleDirection = new(Mathf.Cos(angle), Mathf.Sin(angle));
            float x = candidate.Position.x + (Mathf.Cos(angle) * coverageDistance);
            float z = candidate.Position.z + (Mathf.Sin(angle) * coverageDistance);
            if (strategicHeightMap.TryGetHeightNearest(x, z, out float terrainHeight))
            {
                bool landSample = terrainHeight > Datum.SeaLevel.y + 1f;
                float terrainRelevance = landSample ? 1f : 0.2f;
                float areaWeight = coverageDistance / CoverageRange * terrainRelevance;
                float sourceHeight = candidate.Height + RadarHeight;
                float targetSlope = (terrainHeight + LowAltitudeClearance - sourceHeight) / coverageDistance;
                bool forwardSample = coverageDistance <= ForwardCoverageRange
                    && Vector2.Dot(sampleDirection, coverageEnemyDirection)
                        >= Mathf.Cos(ForwardCoverageHalfAngle * Mathf.Deg2Rad);
                float forwardWeight = coverageDistance / ForwardCoverageRange * terrainRelevance;
                if (forwardSample)
                {
                    coverageForwardTotalWeight += forwardWeight;
                }

                float strategicWeight = CalculateStrategicWeight(new GlobalPosition(x, terrainHeight, z))
                    * CalculateEngagementRangeValue(coverageDistance);
                coverageTotalAreaWeight += areaWeight;
                coverageTotalFrontWeight += areaWeight * strategicWeight;
                if (targetSlope >= coverageHighestTerrainSlope)
                {
                    coverageVisibleAreaWeight += areaWeight;
                    coverageVisibleFrontWeight += areaWeight * strategicWeight;
                    if (forwardSample)
                    {
                        coverageForwardVisibleWeight += forwardWeight;
                    }
                }

                float terrainSlope = (terrainHeight - sourceHeight) / coverageDistance;
                coverageHighestTerrainSlope = Mathf.Max(coverageHighestTerrainSlope, terrainSlope);
            }

            coverageDistance += CoverageSampleSpacing;
            queries++;
        }

        StatusText = $"Evaluating 50 km terrain coverage: {Mathf.Min(coverageCandidateIndex + 1, candidates.Count)}/{candidates.Count}";
        if (coverageCandidateIndex >= candidates.Count)
        {
            FinishAnalysis();
        }
    }

    private void ResetCoverageAccumulator()
    {
        coverageDirectionIndex = 0;
        coverageDistance = CoverageSampleSpacing;
        coverageHighestTerrainSlope = float.MinValue;
        coverageVisibleAreaWeight = 0f;
        coverageVisibleFrontWeight = 0f;
        coverageTotalFrontWeight = 0f;
        coverageTotalAreaWeight = 0f;
        coverageForwardVisibleWeight = 0f;
        coverageForwardTotalWeight = 0f;
        coverageEnemyDirection = Vector2.up;
        coverageEnemyDirectionReady = false;
    }

    private void BeginCoverageOverlay(SiteCandidate candidate, float? emitterHeight = null)
    {
        ClearCoverageOverlay();
        if (!strategicHeightMap.IsReady)
        {
            return;
        }

        Texture2D texture = new(CoverageOverlayResolution, CoverageOverlayResolution, TextureFormat.RGBA32, false)
        {
            name = "GroundControl_SamCoverage",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        CoverageOverlayTexture = texture;
        coverageOverlayPixels = new Color32[CoverageOverlayResolution * CoverageOverlayResolution];
        coverageRequiredAltitudes = new float[coverageOverlayPixels.Length];
        coverageOverlayAlpha = new byte[coverageOverlayPixels.Length];
        for (int i = 0; i < coverageRequiredAltitudes.Length; i++)
        {
            coverageRequiredAltitudes[i] = float.PositiveInfinity;
        }

        coverageHorizonSlopes = new float[CoverageHorizonDirectionCount];
        for (int i = 0; i < coverageHorizonSlopes.Length; i++)
        {
            coverageHorizonSlopes[i] = float.NegativeInfinity;
        }

        coverageHorizonProfile = new float[CoverageHorizonDirectionCount * (CoverageHorizonDistanceSteps + 1)];
        texture.SetPixels32(coverageOverlayPixels);
        texture.Apply(false, false);
        coverageOverlayCandidate = candidate;
        coverageEmitterHeight = emitterHeight ?? candidate.Height + RadarHeight;
        coverageOverlayPixelIndex = 0;
        coverageHorizonSampleIndex = 0;
        CoverageOverlayBuilding = true;
    }

    private void UpdateCoverageOverlayBatch()
    {
        if (!CoverageOverlayEnabled
            || !CoverageOverlayBuilding
            || CoverageOverlayTexture == null
            || coverageOverlayPixels == null
            || coverageRequiredAltitudes == null
            || coverageOverlayAlpha == null
            || coverageHorizonSlopes == null
            || coverageHorizonProfile == null)
        {
            return;
        }

        int baseBatchSize = Mathf.Clamp(ScanQueriesPerFrame, 64, 512);
        int horizonEnd = Mathf.Min(
            coverageHorizonSampleIndex + (baseBatchSize * 24),
            CoverageHorizonSampleCount);
        for (; coverageHorizonSampleIndex < horizonEnd; coverageHorizonSampleIndex++)
        {
            int directionIndex = coverageHorizonSampleIndex / CoverageHorizonDistanceSteps;
            int distanceIndex = (coverageHorizonSampleIndex % CoverageHorizonDistanceSteps) + 1;
            float distance = distanceIndex * CoverageHorizonSampleSpacing;
            float angle = directionIndex * Mathf.PI * 2f / CoverageHorizonDirectionCount;
            float x = coverageOverlayCandidate.Position.x + (Mathf.Cos(angle) * distance);
            float z = coverageOverlayCandidate.Position.z + (Mathf.Sin(angle) * distance);
            if (strategicHeightMap.TryGetHeight(x, z, out float terrainHeight))
            {
                float slope = (terrainHeight - coverageEmitterHeight) / distance;
                coverageHorizonSlopes[directionIndex] = Mathf.Max(coverageHorizonSlopes[directionIndex], slope);
            }

            coverageHorizonProfile[
                (directionIndex * (CoverageHorizonDistanceSteps + 1)) + distanceIndex] =
                coverageHorizonSlopes[directionIndex];
        }

        if (coverageHorizonSampleIndex < CoverageHorizonSampleCount)
        {
            return;
        }

        int resolution = CoverageOverlayTexture.width;
        int end = Mathf.Min(
            coverageOverlayPixelIndex + (baseBatchSize * 24),
            coverageOverlayPixels.Length);
        for (; coverageOverlayPixelIndex < end; coverageOverlayPixelIndex++)
        {
            int x = coverageOverlayPixelIndex % resolution;
            int y = coverageOverlayPixelIndex / resolution;
            float z = Mathf.Lerp(
                -strategicHeightMap.MapSize.y * 0.5f,
                strategicHeightMap.MapSize.y * 0.5f,
                y / (float)(resolution - 1));
            float globalX = Mathf.Lerp(
                -strategicHeightMap.MapSize.x * 0.5f,
                strategicHeightMap.MapSize.x * 0.5f,
                x / (float)(resolution - 1));
            if (!strategicHeightMap.TryGetHeight(globalX, z, out float terrainHeight))
            {
                continue;
            }

            GlobalPosition target = new(globalX, terrainHeight + LowAltitudeClearance, z);
            float distance = Mathf.Sqrt(HorizontalSquareDistance(coverageOverlayCandidate.Position, target));
            if (distance > CoverageRange)
            {
                continue;
            }

            float requiredAltitude = 0f;
            if (distance >= CoverageHorizonSampleSpacing)
            {
                float angle = Mathf.Atan2(
                    z - coverageOverlayCandidate.Position.z,
                    globalX - coverageOverlayCandidate.Position.x);
                if (angle < 0f)
                {
                    angle += Mathf.PI * 2f;
                }

                int directionIndex = Mathf.Clamp(
                    Mathf.FloorToInt(angle / (Mathf.PI * 2f) * CoverageHorizonDirectionCount),
                    0,
                    CoverageHorizonDirectionCount - 1);
                int distanceIndex = Mathf.Clamp(
                    Mathf.FloorToInt(distance / CoverageHorizonSampleSpacing),
                    1,
                    CoverageHorizonDistanceSteps);
                float horizonSlope = coverageHorizonProfile[
                    (directionIndex * (CoverageHorizonDistanceSteps + 1)) + distanceIndex];
                if (!float.IsNegativeInfinity(horizonSlope))
                {
                    requiredAltitude = Mathf.Max(
                        0f,
                        coverageEmitterHeight + (horizonSlope * distance) - terrainHeight);
                }
            }

            coverageRequiredAltitudes[coverageOverlayPixelIndex] = requiredAltitude;
            float weight = CalculateStrategicWeight(target) * CalculateEngagementRangeValue(distance);
            coverageOverlayAlpha[coverageOverlayPixelIndex] =
                (byte)Mathf.RoundToInt(Mathf.Lerp(38f, 112f, weight));
            coverageOverlayPixels[coverageOverlayPixelIndex] = requiredAltitude <= CoverageTargetAltitude
                ? new Color32(34, 174, 230, coverageOverlayAlpha[coverageOverlayPixelIndex])
                : default;
        }

        if (coverageOverlayPixelIndex < coverageOverlayPixels.Length)
        {
            return;
        }

        CoverageOverlayTexture.SetPixels32(coverageOverlayPixels);
        CoverageOverlayTexture.Apply(false, false);
        coverageHorizonSlopes = null;
        coverageHorizonProfile = null;
        CoverageOverlayBuilding = false;
    }

    private void RecolorCoverageOverlay()
    {
        if (CoverageOverlayTexture == null
            || coverageOverlayPixels == null
            || coverageRequiredAltitudes == null
            || coverageOverlayAlpha == null)
        {
            return;
        }

        for (int i = 0; i < coverageOverlayPixels.Length; i++)
        {
            coverageOverlayPixels[i] = coverageRequiredAltitudes[i] <= CoverageTargetAltitude
                ? new Color32(34, 174, 230, coverageOverlayAlpha[i])
                : default;
        }

        CoverageOverlayTexture.SetPixels32(coverageOverlayPixels);
        CoverageOverlayTexture.Apply(false, false);
    }

    private void ClearCoverageOverlay()
    {
        if (CoverageOverlayTexture != null)
        {
            // A RawImage whose texture is destroyed draws a plain white quad, and the map's layer
            // spans the whole map. The map service only re-syncs it while the RTS map is open, so
            // leaving RTS mode (Deactivate lands here) left the vanilla map whited out.
            CommanderTacticalMapService.Instance?.HideCoverageLayer();
            Object.Destroy(CoverageOverlayTexture);
            CoverageOverlayTexture = null;
        }

        coverageOverlayPixels = null;
        coverageRequiredAltitudes = null;
        coverageOverlayAlpha = null;
        coverageHorizonSlopes = null;
        coverageHorizonProfile = null;
        coverageOverlayPixelIndex = 0;
        coverageHorizonSampleIndex = 0;
        CoverageOverlayBuilding = false;
        coverageOverlaySource = null;
    }
}
