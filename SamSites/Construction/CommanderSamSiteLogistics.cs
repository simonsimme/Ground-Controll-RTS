using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteService
{
    internal static readonly float[] SupplyThresholdOptions =
    {
        2000f,
        5000f,
        10000f,
        20000f,
        30000f,
    };

    private const int CachedSupplyRouteCount = 3;

    private readonly HashSet<GroundVehicle> reservedConstructionJacknifes = new();

    internal static bool TryCopyCachedSupplyRoute(
        int siteId,
        Airbase airbase,
        List<GlobalPosition> destination,
        out bool steepLanding)
    {
        destination.Clear();
        steepLanding = false;
        if (instance == null
            || !instance.constructionSites.TryGetValue(siteId, out ConstructionSite site)
            || !site.UseCustomSupplyRoute
            || !site.SupplyRoutes.TryGetValue(airbase, out CachedSupplyRoute route)
            || !route.Planned)
        {
            return false;
        }

        destination.AddRange(route.Waypoints);
        steepLanding = route.SteepLanding;
        return true;
    }

    internal IReadOnlyList<CommanderSupplyHeliService.SamSiteAirbaseOption>
        GetConstructionSiteAirbases(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site))
        {
            return Array.Empty<CommanderSupplyHeliService.SamSiteAirbaseOption>();
        }

        RefreshSiteAirbases(site, force: false);
        return site.SupplyAirbases;
    }

    internal Airbase? GetConstructionSiteAirbase(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            ? site.SelectedSupplyAirbase
            : null;
    }

    internal void SelectConstructionSiteAirbase(Unit unit, int index)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            || index < 0
            || index >= site.SupplyAirbases.Count)
        {
            return;
        }

        site.SelectedSupplyAirbase = site.SupplyAirbases[index].Airbase;
        site.SupplyAirbaseManuallySelected = true;
        site.SupplyRouteVisible = false;
        site.Status = $"Logistics airbase set to {site.SupplyAirbases[index].Label}.";
    }

    internal bool IsConstructionSupplyRouteVisible(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            && site.SupplyRouteVisible;
    }

    internal bool GetConstructionCustomRouteEnabled(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            && site.UseCustomSupplyRoute;
    }

    internal void ToggleConstructionCustomRoute(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site))
        {
            return;
        }

        site.UseCustomSupplyRoute = !site.UseCustomSupplyRoute;
        if (!site.UseCustomSupplyRoute)
        {
            site.SupplyRouteVisible = false;
        }

        site.Status = site.UseCustomSupplyRoute
            ? "Custom terrain route enabled for future logistics flights."
            : "Future logistics flights will use Basegame navigation.";
    }

    internal bool CanShowConstructionSupplyRoute(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            && site.SelectedSupplyAirbase != null
            && site.SupplyRoutes.TryGetValue(site.SelectedSupplyAirbase, out CachedSupplyRoute route)
            && route.Planned;
    }

    internal void ToggleConstructionSupplyRoute(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite selectedSite)
            || selectedSite.SelectedSupplyAirbase == null
            || !selectedSite.SupplyRoutes.TryGetValue(selectedSite.SelectedSupplyAirbase, out CachedSupplyRoute route)
            || !route.Planned)
        {
            return;
        }

        bool show = !selectedSite.SupplyRouteVisible;
        foreach (ConstructionSite site in constructionSites.Values)
        {
            site.SupplyRouteVisible = false;
        }

        selectedSite.SupplyRouteVisible = show;
    }

    internal void CopyVisibleSupplyRoute(List<GlobalPosition> destination)
    {
        destination.Clear();
        foreach (ConstructionSite site in constructionSites.Values)
        {
            if (!site.SupplyRouteVisible
                || site.SelectedSupplyAirbase == null
                || !site.SupplyRoutes.TryGetValue(site.SelectedSupplyAirbase, out CachedSupplyRoute route)
                || !route.Planned)
            {
                continue;
            }

            Transform origin = site.SelectedSupplyAirbase.center != null
                ? site.SelectedSupplyAirbase.center
                : site.SelectedSupplyAirbase.transform;
            destination.Add(origin.GlobalPosition());
            destination.AddRange(route.Waypoints);
            destination.Add(site.PlatformTarget);
            return;
        }
    }

    internal bool GetAutomaticSupplyEnabled(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            && site.AutomaticSupplyEnabled;
    }

    internal void ToggleAutomaticSupply(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site))
        {
            return;
        }

        site.AutomaticSupplyEnabled = !site.AutomaticSupplyEnabled;
        site.Status = site.AutomaticSupplyEnabled
            ? $"Automatic supply enabled below {site.AutomaticSupplyThreshold:0}."
            : "Automatic supply disabled.";
        if (site.AutomaticSupplyEnabled)
        {
            TryRequestAutomaticSiteSupply(site);
        }
    }

    internal float GetAutomaticSupplyThreshold(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            ? site.AutomaticSupplyThreshold
            : SupplyThresholdOptions[2];
    }

    internal void SetAutomaticSupplyThreshold(Unit unit, float threshold)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site))
        {
            return;
        }

        float ceiling = site.CoreRearmer?.GetMaxCapacity() ?? SupplyThresholdOptions[SupplyThresholdOptions.Length - 1];
        site.AutomaticSupplyThreshold = Mathf.Clamp(threshold, SupplyThresholdOptions[0], ceiling);
        site.Status = $"Automatic supply threshold set to {site.AutomaticSupplyThreshold:0}.";
        TryRequestAutomaticSiteSupply(site);
    }

    internal bool CanRequestConstructionSupply(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            && site.Phase == ConstructionPhase.Ready
            && !site.PlatformLost
            && site.CoreRearmer != null
            && site.CoreRearmer.Capacity + 0.01f < site.CoreRearmer.GetMaxCapacity()
            && !site.SupplyRunOutstanding
            && GetSelectedAirbaseOption(site)?.SupportsSupply == true;
    }

    internal void RequestConstructionSupply(Unit unit)
    {
        if (constructionSitesByCore.TryGetValue(unit, out ConstructionSite site))
        {
            RequestSiteSupply(site, automatic: false);
        }
    }

    internal bool CanRequestConstructionJacknife(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            || site.Phase != ConstructionPhase.Ready
            || site.PlatformLost
            || site.JacknifeRunOutstanding
            || site.JacknifeInventory + site.IncomingJacknifes.Count >= MaxJacknifeInventory)
        {
            return false;
        }

        return GetSelectedAirbaseOption(site)?.SupportsJacknife == true;
    }

    internal int GetIncomingConstructionJacknifes(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site))
        {
            return 0;
        }

        return site.IncomingJacknifes.Count + (site.JacknifeRunOutstanding ? 1 : 0);
    }

    internal void RequestConstructionJacknife(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            || !CanRequestConstructionJacknife(unit)
            || site.SelectedSupplyAirbase == null)
        {
            return;
        }

        if (supplyHeliService.RequestSamSiteJacknife(
            site.Id,
            site.PlatformTarget,
            site.SelectedSupplyAirbase))
        {
            site.JacknifeRunOutstanding = true;
            site.JacknifeRequestExpiresAt = Time.unscaledTime + 180f;
            site.Status = "Jacknife delivery requested.";
        }
    }

    private static void SelectBestAutomaticSupplyAirbase(ConstructionSite site)
    {
        if (site.SupplyAirbaseManuallySelected)
        {
            return;
        }

        CommanderSupplyHeliService.SamSiteAirbaseOption? best = null;
        float bestScore = float.MaxValue;
        bool hasPlannedRoute = site.SupplyAirbases.Any(option =>
            option.SupportsSupply
            && site.SupplyRoutes.TryGetValue(option.Airbase, out CachedSupplyRoute candidateRoute)
            && candidateRoute.Planned);
        bool hasSafeRoute = site.SupplyAirbases.Any(option =>
            option.SupportsSupply
            && site.SupplyRoutes.TryGetValue(option.Airbase, out CachedSupplyRoute candidateRoute)
            && candidateRoute.Planned
            && candidateRoute.Risk <= 0.62f);
        for (int i = 0; i < site.SupplyAirbases.Count; i++)
        {
            CommanderSupplyHeliService.SamSiteAirbaseOption option = site.SupplyAirbases[i];
            if (!option.SupportsSupply
                || !site.SupplyRoutes.TryGetValue(option.Airbase, out CachedSupplyRoute route))
            {
                continue;
            }

            if (hasPlannedRoute && !route.Planned)
            {
                continue;
            }

            if (hasSafeRoute && (!route.Planned || route.Risk > 0.62f))
            {
                continue;
            }

            float routeLength = route.RouteLength > 0f ? route.RouteLength : option.Distance;
            float score = routeLength + (route.Risk * 50000f);
            if (!route.Planned)
            {
                score += 15000f;
            }

            if (score < bestScore)
            {
                best = option;
                bestScore = score;
            }
        }

        if (best != null)
        {
            site.SelectedSupplyAirbase = best.Airbase;
        }
    }

    private static void EnsureSupplyRouteCache(ConstructionSite site)
    {
        List<CommanderSupplyHeliService.SamSiteAirbaseOption> desired = site.SupplyAirbases
            .Where(option => option.SupportsSupply || option.SupportsJacknife)
            .OrderBy(option => option.Distance)
            .Take(CachedSupplyRouteCount)
            .ToList();
        if (site.SelectedSupplyAirbase != null
            && desired.All(option => !ReferenceEquals(option.Airbase, site.SelectedSupplyAirbase)))
        {
            CommanderSupplyHeliService.SamSiteAirbaseOption? selected = site.SupplyAirbases
                .FirstOrDefault(option => ReferenceEquals(option.Airbase, site.SelectedSupplyAirbase));
            if (selected != null)
            {
                desired.Add(selected);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            CommanderSupplyHeliService.SamSiteAirbaseOption option = desired[i];
            if (site.SupplyRoutes.ContainsKey(option.Airbase))
            {
                continue;
            }

            Transform sourceTransform = option.Airbase.center != null
                ? option.Airbase.center
                : option.Airbase.transform;
            GlobalPosition source = sourceTransform.GlobalPosition();
            List<GlobalPosition> route = new();
            bool planned = CommanderTerrainFlightPlanner.TryBuildRoute(
                source,
                site.PlatformTarget,
                100f,
                route,
                out bool steepLanding);
            CommanderSamSiteAnalyzerService.TryEvaluateLogisticsRisk(
                source,
                site.PlatformTarget,
                planned ? route : null,
                out float risk,
                out float routeLength);
            site.SupplyRoutes[option.Airbase] = new CachedSupplyRoute(
                planned,
                route,
                steepLanding,
                risk,
                routeLength);
            CommanderPlugin.Log.LogInfo(
                $"SAM site {site.Id} cached supply route: airbase={option.Label}, "
                + $"planned={planned}, waypoints={route.Count}, risk={risk:P0}, "
                + $"length={routeLength / 1000f:0.0}km.");
            SelectBestAutomaticSupplyAirbase(site);
            return;
        }

        SelectBestAutomaticSupplyAirbase(site);
    }

    private void TickSiteLogistics(ConstructionSite site)
    {
        RefreshSiteAirbases(site, force: false);
        EnsureSupplyRouteCache(site);
        if (site.SupplyRunOutstanding && Time.unscaledTime >= site.SupplyRequestExpiresAt)
        {
            site.SupplyRunOutstanding = false;
        }

        if (site.JacknifeRunOutstanding && Time.unscaledTime >= site.JacknifeRequestExpiresAt)
        {
            site.JacknifeRunOutstanding = false;
        }

        TickIncomingJacknifes(site);
        TryRequestAutomaticSiteSupply(site);
    }

    private void RefreshSiteAirbases(ConstructionSite site, bool force)
    {
        if (!force && Time.unscaledTime < site.NextAirbaseRefreshAt)
        {
            return;
        }

        site.NextAirbaseRefreshAt = Time.unscaledTime + 5f;
        Airbase? previous = site.SelectedSupplyAirbase;
        supplyHeliService.CopySamSiteAirbaseOptions(site.PlatformTarget, site.SupplyAirbases);
        site.SelectedSupplyAirbase = site.SupplyAirbases
            .FirstOrDefault(option => ReferenceEquals(option.Airbase, previous))
            ?.Airbase;
        site.SelectedSupplyAirbase ??= site.SupplyAirbases
            .FirstOrDefault(option => option.SupportsSupply)
            ?.Airbase;
    }

    private CommanderSupplyHeliService.SamSiteAirbaseOption? GetSelectedAirbaseOption(
        ConstructionSite site)
    {
        RefreshSiteAirbases(site, force: false);
        return site.SupplyAirbases.FirstOrDefault(
            option => ReferenceEquals(option.Airbase, site.SelectedSupplyAirbase));
    }

    private void TryRequestAutomaticSiteSupply(ConstructionSite site)
    {
        if (!site.AutomaticSupplyEnabled || site.PlatformLost || site.CoreRearmer == null)
        {
            return;
        }

        float targetThreshold = site.AutomaticSupplyThreshold;
        if (site.BuildQueue.Count > 0)
        {
            targetThreshold = Mathf.Max(targetThreshold, site.BuildQueue.Peek().Cost);
        }

        if (site.CoreRearmer.Capacity + 0.01f >= targetThreshold)
        {
            return;
        }

        RequestSiteSupply(site, automatic: true);
    }

    private void RequestSiteSupply(ConstructionSite site, bool automatic)
    {
        if (site.Core == null
            || site.CoreRearmer == null
            || site.PlatformLost
            || site.SupplyRunOutstanding
            || (automatic && Time.unscaledTime < site.NextSupplyRequestAt)
            || site.SelectedSupplyAirbase == null
            || GetSelectedAirbaseOption(site)?.SupportsSupply != true)
        {
            return;
        }

        float freeSupply = Mathf.Max(
            0f,
            site.CoreRearmer.GetMaxCapacity() - site.CoreRearmer.Capacity);
        if (supplyHeliService.RequestAutomaticCargoRun(
            site.Id,
            site.PlatformTarget,
            site.SelectedSupplyAirbase,
            freeSupply))
        {
            site.SupplyRunOutstanding = true;
            site.NextSupplyRequestAt = Time.unscaledTime + 30f;
            site.SupplyRequestExpiresAt = Time.unscaledTime + 180f;
            site.Status = automatic
                ? "Supply below threshold; automatic delivery requested."
                : "Manual ammunition delivery requested.";
        }
    }

    private void ReserveConstructionJacknife(int siteId, GroundVehicle jacknife)
    {
        if (jacknife == null || jacknife.disabled || !constructionSites.ContainsKey(siteId))
        {
            return;
        }

        reservedConstructionJacknifes.Add(jacknife);
        jacknife.SetHoldPosition(true);
        if (constructionSites.TryGetValue(siteId, out ConstructionSite site))
        {
            site.Status = "Jacknife unloaded; holding for 30 seconds before moving.";
        }

        Repairer? repairer = jacknife.GetComponentInChildren<Repairer>(true);
        if (repairer != null)
        {
            CommanderRepairPatches.RequestImmediateSearch(repairer);
        }
    }

    private bool IsConstructionJacknifeReserved(Unit? unit)
    {
        if (unit is not GroundVehicle jacknife || jacknife.disabled)
        {
            return false;
        }

        if (reservedConstructionJacknifes.Contains(jacknife))
        {
            return true;
        }

        foreach (ConstructionSite site in constructionSites.Values)
        {
            if (ReferenceEquals(site.Worker, jacknife)
                || site.FoundationJacknifes.Contains(jacknife)
                || site.IncomingJacknifes.Any(entry => ReferenceEquals(entry.Unit, jacknife)))
            {
                return true;
            }
        }

        return false;
    }

    private void HandleSiteJacknifeActivated(int siteId, Unit cargo)
    {
        if (cargo is not GroundVehicle jacknife)
        {
            DestroyNetworkUnit(cargo);
            return;
        }

        reservedConstructionJacknifes.Remove(jacknife);
        if (!constructionSites.TryGetValue(siteId, out ConstructionSite site)
            || site.Phase != ConstructionPhase.Ready
            || site.JacknifeInventory + site.IncomingJacknifes.Count >= MaxJacknifeInventory)
        {
            DestroyNetworkUnit(jacknife);
            return;
        }

        GlobalPosition target = GetSiteServiceTarget(site);
        site.JacknifeDefinition ??= jacknife.definition as VehicleDefinition;
        site.IncomingJacknifes.Add(new IncomingJacknife(
            jacknife,
            target,
            Time.timeSinceLevelLoad + ConstructionTravelTimeout));
        site.JacknifeRunOutstanding = false;
        AddSiteUnit(site, jacknife);
        IssueConstructionMove(jacknife, target);
        site.Status = "Delivered Jacknife moving to site inventory.";
    }

    private void TickIncomingJacknifes(ConstructionSite site)
    {
        for (int i = site.IncomingJacknifes.Count - 1; i >= 0; i--)
        {
            IncomingJacknife entry = site.IncomingJacknifes[i];
            GroundVehicle jacknife = entry.Unit;
            if (jacknife == null || jacknife.disabled)
            {
                site.IncomingJacknifes.RemoveAt(i);
                site.JacknifeRunOutstanding = false;
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                jacknife.transform.position,
                entry.Target.ToLocalPosition());
            if (distance > 5f && Time.timeSinceLevelLoad < entry.Timeout)
            {
                continue;
            }

            if (distance > 5f)
            {
                CommanderGameAccess.TeleportGroundVehicle(jacknife, entry.Target);
            }

            RemoveAndDestroySiteUnit(site, jacknife);
            site.IncomingJacknifes.RemoveAt(i);
            site.JacknifeInventory = Mathf.Min(
                MaxJacknifeInventory,
                site.JacknifeInventory + 1);
            site.JacknifeRunOutstanding = false;
            site.Status = $"Jacknife added to inventory ({site.JacknifeInventory}/{MaxJacknifeInventory}).";
        }
    }
}
