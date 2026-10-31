namespace GroundControlRts;

internal sealed partial class CommanderSamSiteAnalyzerService
{
    internal enum AnalyzerState
    {
        Waiting,
        Sampling,
        Coverage,
        Refining,
        Ready,
        Failed,
    }

    internal enum CandidateListMode
    {
        Nearby,
        Ranked,
    }

    internal enum CandidateSortMode
    {
        Rating,
        AreaLos,
        FrontEnemy,
        Risk,
        Forward5Km,
        Height,
    }

    internal enum FilterComparison
    {
        Minimum,
        Maximum,
    }

    internal enum SiteUnitRole
    {
        Radar,
        Platform,
        ControlTower,
        Gun23mm,
        Irm,
        StratoLauncher,
        Ammo,
        FireControl,
    }

    internal readonly struct SiteLayoutMarker
    {
        internal SiteLayoutMarker(int siteIndex, SiteUnitRole role, GlobalPosition position)
        {
            SiteIndex = siteIndex;
            Role = role;
            Position = position;
        }

        internal int SiteIndex { get; }

        internal SiteUnitRole Role { get; }

        internal GlobalPosition Position { get; }
    }

    internal struct SiteCandidate
    {
        internal int CandidateId;
        internal GlobalPosition Position;
        internal float Height;
        internal float SlopeDegrees;
        internal float Prominence;
        internal float Coverage;
        internal float StrategicCoverage;
        internal float ForwardCoverage;
        internal float Risk;
        internal float BaseScore;
        internal float Score;

        internal SiteCandidate(
            GlobalPosition position,
            float height,
            float slopeDegrees,
            float prominence,
            float coverage,
            float risk,
            float score)
        {
            CandidateId = -1;
            Position = position;
            Height = height;
            SlopeDegrees = slopeDegrees;
            Prominence = prominence;
            Coverage = coverage;
            StrategicCoverage = 0f;
            ForwardCoverage = 0f;
            Risk = risk;
            BaseScore = score - (coverage * 1000f);
            Score = score;
        }
    }

    private readonly struct TerrainSeed
    {
        internal TerrainSeed(
            GlobalPosition position,
            float height,
            float slopeDegrees,
            float prominence,
            float score)
        {
            Position = position;
            Height = height;
            SlopeDegrees = slopeDegrees;
            Prominence = prominence;
            Score = score;
        }

        internal GlobalPosition Position { get; }

        internal float Height { get; }

        internal float SlopeDegrees { get; }

        internal float Prominence { get; }

        internal float Score { get; }
    }

    private readonly struct LocalRadarSeed
    {
        internal LocalRadarSeed(
            GlobalPosition position,
            float height,
            float normalY,
            float terrainScore)
        {
            Position = position;
            Height = height;
            NormalY = normalY;
            TerrainScore = terrainScore;
        }

        internal GlobalPosition Position { get; }

        internal float Height { get; }

        internal float NormalY { get; }

        internal float TerrainScore { get; }
    }

    private readonly struct NearbyCandidate
    {
        internal NearbyCandidate(SiteCandidate candidate, float distanceSquared)
        {
            Candidate = candidate;
            DistanceSquared = distanceSquared;
        }

        internal SiteCandidate Candidate { get; }

        internal float DistanceSquared { get; }
    }
}
