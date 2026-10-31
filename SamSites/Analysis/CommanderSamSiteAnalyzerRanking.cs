using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteAnalyzerService
{
    private static bool PassesPercentFilter(
        float value,
        float threshold,
        FilterComparison comparison)
    {
        if (comparison == FilterComparison.Minimum)
        {
            return threshold <= 0f || value >= threshold;
        }

        return threshold >= 0.999f || value <= threshold;
    }

    private static bool IsWithinRoadDistance(GlobalPosition position, float maxDistance)
    {
        LevelInfo? level = NetworkSceneSingleton<LevelInfo>.i;
        return level?.roadNetwork != null
            && level.roadNetwork.TryGetNearestPoint(position, out GlobalPosition nearestRoad, out _)
            && HorizontalSquareDistance(position, nearestRoad) <= maxDistance * maxDistance;
    }

    private static float HorizontalSquareDistance(GlobalPosition left, GlobalPosition right)
    {
        float x = left.x - right.x;
        float z = left.z - right.z;
        return (x * x) + (z * z);
    }

    private void FinishAnalysis()
    {
        candidates.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        AssignCandidateIds();
        State = AnalyzerState.Ready;
        float now = Time.realtimeSinceStartup;
        CommanderPlugin.Log.LogInfo(
            $"SAM LOS evaluation complete: duration={now - coverageStartedAt:0.000}s, "
            + $"candidates={candidates.Count}, directions={CoverageDirectionCount}, "
            + $"range={CoverageRange / 1000f:0}km.");
        RefreshNearbySites(force: true);
    }

    private void RefreshNearbySites(bool force = false)
    {
        if (!force && Time.unscaledTime < nextNearbyRefreshAt)
        {
            return;
        }

        nextNearbyRefreshAt = Time.unscaledTime + 1f;

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (cameraManager == null || candidates.Count == 0)
        {
            return;
        }

        GlobalPosition cameraPosition = cameraManager.transform.position.ToGlobalPosition();
        List<NearbyCandidate> pool = new(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            float distanceSquared = HorizontalSquareDistance(cameraPosition, candidates[i].Position);
            if (!PassesActiveFilters(candidates[i])
                || !PassesRangeFilter(Mathf.Sqrt(distanceSquared)))
            {
                continue;
            }

            pool.Add(new NearbyCandidate(
                candidates[i],
                distanceSquared));
        }

        if (ListMode == CandidateListMode.Nearby)
        {
            pool.Sort(static (left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));
        }
        else
        {
            pool.Sort(CompareRankedCandidates);
        }

        suggestedSites.Clear();
        for (int i = 0; i < pool.Count && i < SuggestedSiteCount; i++)
        {
            SiteCandidate candidate = pool[i].Candidate;
            if (candidate.CandidateId == activeCandidateId && ActiveSiteReady)
            {
                candidate = activeSite;
            }

            suggestedSites.Add(candidate);
        }
    }

    private int CompareRankedCandidates(NearbyCandidate left, NearbyCandidate right)
    {
        float leftValue;
        float rightValue;
        bool ascending = false;
        switch (SortMode)
        {
            case CandidateSortMode.AreaLos:
                leftValue = left.Candidate.Coverage;
                rightValue = right.Candidate.Coverage;
                break;
            case CandidateSortMode.FrontEnemy:
                leftValue = left.Candidate.StrategicCoverage;
                rightValue = right.Candidate.StrategicCoverage;
                break;
            case CandidateSortMode.Risk:
                leftValue = left.Candidate.Risk;
                rightValue = right.Candidate.Risk;
                ascending = true;
                break;
            case CandidateSortMode.Forward5Km:
                leftValue = left.Candidate.ForwardCoverage;
                rightValue = right.Candidate.ForwardCoverage;
                break;
            case CandidateSortMode.Height:
                leftValue = left.Candidate.Height;
                rightValue = right.Candidate.Height;
                break;
            default:
                leftValue = left.Candidate.Score;
                rightValue = right.Candidate.Score;
                break;
        }

        int comparison = ascending
            ? leftValue.CompareTo(rightValue)
            : rightValue.CompareTo(leftValue);
        return comparison != 0
            ? comparison
            : left.DistanceSquared.CompareTo(right.DistanceSquared);
    }

    private void AssignCandidateIds()
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            SiteCandidate candidate = candidates[i];
            candidate.CandidateId = i;
            candidates[i] = candidate;
        }
    }

    private int FindSuggestedSiteIndex(int candidateId)
    {
        if (candidateId < 0)
        {
            return -1;
        }

        for (int i = 0; i < suggestedSites.Count; i++)
        {
            if (suggestedSites[i].CandidateId == candidateId)
            {
                return i;
            }
        }

        return -1;
    }

    private void RefreshFilteredSuggestions()
    {
        if (candidates.Count == 0)
        {
            return;
        }

        RefreshNearbySites(force: true);
    }

    private bool PassesActiveFilters(SiteCandidate candidate)
    {
        if (!PassesPercentFilter(candidate.Coverage, MinimumAreaCoverage, AreaComparison)
            || !PassesPercentFilter(candidate.StrategicCoverage, MinimumFrontShare, FrontComparison)
            || !PassesPercentFilter(candidate.Risk, MaximumRisk, RiskComparison)
            || !PassesPercentFilter(candidate.ForwardCoverage, MinimumForwardCoverage, ForwardComparison))
        {
            return false;
        }

        if (LimitRoadDistance && !IsWithinRoadDistance(candidate.Position, MaxRoadDistance))
        {
            return false;
        }

        return true;
    }

    private bool PassesRangeFilter(float distance)
    {
        if (MaximumCandidateRange <= 0f)
        {
            return true;
        }

        return RangeComparison == FilterComparison.Minimum
            ? distance >= MaximumCandidateRange
            : distance <= MaximumCandidateRange;
    }
}
