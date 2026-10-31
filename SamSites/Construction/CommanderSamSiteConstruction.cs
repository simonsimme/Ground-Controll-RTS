using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteService
{
    private const float MainComponentSupplyCost = 10000f;
    private const float PointDefenseSupplyCost = 2000f;
    private const float ConstructionTravelTimeout = 60f;
    private const float ConstructionDuration = 30f;
    private const int MaxJacknifeInventory = 2;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly Dictionary<int, ConstructionSite> constructionSites = new();
    private readonly Dictionary<Unit, ConstructionSite> constructionSitesByCore = new();
    private readonly HashSet<int> cancelledFoundationSites = new();
    private readonly List<ConstructionSite> constructionTickBuffer = new();
    private bool issuingConstructionMove;
    private int nextConstructionSiteId = 1;
    private float nextConstructionTickAt;

    internal enum SiteBuildType
    {
        SamBattery,
        Irm,
        Gun23mm,
    }

    private enum ConstructionPhase
    {
        Foundation,
        Ready,
    }

    private enum WorkerPhase
    {
        None,
        Travelling,
        Constructing,
        Returning,
    }

    internal bool HasActiveConstructionSite
    {
        get
        {
            analyzer.CopyActiveLayout(layout);
            return TryFindSiteForLayout(layout, out _);
        }
    }

    public void TickPersistent()
    {
        if (constructionSites.Count == 0 || Time.unscaledTime < nextConstructionTickAt)
        {
            return;
        }

        nextConstructionTickAt = Time.unscaledTime + 0.25f;
        constructionTickBuffer.Clear();
        constructionTickBuffer.AddRange(constructionSites.Values);
        for (int i = 0; i < constructionTickBuffer.Count; i++)
        {
            TickConstructionSite(constructionTickBuffer[i]);
        }
    }

    internal bool IsConstructionCore(Unit? unit)
    {
        return unit != null && constructionSitesByCore.ContainsKey(unit);
    }

    internal bool TryGetConstructionRadarPosition(Unit unit, out GlobalPosition position)
    {
        position = default;
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site))
        {
            return false;
        }

        for (int i = 0; i < site.Layout.Count; i++)
        {
            if (site.Layout[i].Role == CommanderSamSiteAnalyzerService.SiteUnitRole.Radar)
            {
                position = site.Layout[i].Position;
                return true;
            }
        }

        return false;
    }

    internal string GetConstructionSiteStatus(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            ? site.Status
            : string.Empty;
    }

    internal string GetConstructionJacknifeStatus(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site))
        {
            return string.Empty;
        }

        if (site.Phase == ConstructionPhase.Foundation)
        {
            if (site.FoundationJacknifes.Count == 0)
            {
                return "JACKNIFE  Awaiting foundation delivery";
            }

            if (!site.FoundationJacknifeArrived)
            {
                return $"JACKNIFE  Moving to foundation  |  teleport in {SecondsRemaining(site.FoundationJacknifeDeadline)}s";
            }

            if (site.FoundationBuildAt > 0f)
            {
                return $"JACKNIFE  Building site core  |  {SecondsRemaining(site.FoundationBuildAt)}s";
            }

            return "JACKNIFE  Waiting for foundation ammunition";
        }

        if (site.Worker != null)
        {
            string target = site.ActiveBuild != null
                ? GetRoleLabel(site.ActiveBuild.Marker.Role)
                : "site inventory";
            return site.WorkerPhase switch
            {
                WorkerPhase.Travelling => $"JACKNIFE  Moving to {target}  |  teleport in {SecondsRemaining(site.WorkerDeadline)}s",
                WorkerPhase.Constructing => $"JACKNIFE  Building {target}  |  {SecondsRemaining(site.ConstructionCompletesAt)}s",
                WorkerPhase.Returning => $"JACKNIFE  Returning to inventory  |  teleport in {SecondsRemaining(site.WorkerDeadline)}s",
                _ => "JACKNIFE  Reserved for construction",
            };
        }

        if (site.IncomingJacknifes.Count > 0)
        {
            return $"JACKNIFE  Moving to site inventory  |  teleport in {SecondsRemaining(site.IncomingJacknifes[0].Timeout)}s";
        }

        return $"JACKNIFE  Inventory {site.JacknifeInventory}/{MaxJacknifeInventory}";
    }

    internal string GetConstructionSiteSupply(Unit unit)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            || site.CoreRearmer == null)
        {
            return "SUPPLY  --";
        }

        return $"SUPPLY  {site.CoreRearmer.Capacity:0} / {site.CoreRearmer.GetMaxCapacity():0}";
    }

    internal int GetConstructionSiteJacknifes(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            ? site.JacknifeInventory
            : 0;
    }

    internal int GetConstructionQueueCount(Unit unit)
    {
        return constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            ? site.BuildQueue.Count + (site.ActiveBuild != null ? 1 : 0)
            : 0;
    }

    internal bool CanQueueConstruction(Unit unit, SiteBuildType buildType)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            || site.Phase != ConstructionPhase.Ready)
        {
            return false;
        }

        return buildType switch
        {
            SiteBuildType.SamBattery => !site.SamBatteryQueued,
            SiteBuildType.Irm => FindNextAvailableMarker(site, CommanderSamSiteAnalyzerService.SiteUnitRole.Irm) != null,
            SiteBuildType.Gun23mm => FindNextAvailableMarker(site, CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm) != null,
            _ => false,
        };
    }

    internal void QueueConstruction(Unit unit, SiteBuildType buildType)
    {
        if (!constructionSitesByCore.TryGetValue(unit, out ConstructionSite site)
            || site.Phase != ConstructionPhase.Ready)
        {
            return;
        }

        if (buildType == SiteBuildType.SamBattery)
        {
            if (site.SamBatteryQueued)
            {
                return;
            }

            QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl, 0f);
            QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher, MainComponentSupplyCost);
            QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo, 0f);
            QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.Radar, MainComponentSupplyCost);
            site.SamBatteryQueued = true;
            site.Status = "SAM battery added to the construction queue.";
        }
        else
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole role =
                buildType == SiteBuildType.Irm
                    ? CommanderSamSiteAnalyzerService.SiteUnitRole.Irm
                    : CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm;
            CommanderSamSiteAnalyzerService.SiteLayoutMarker? marker = FindNextAvailableMarker(site, role);
            if (marker == null)
            {
                return;
            }

            QueueMarker(site, marker.Value, PointDefenseSupplyCost);
            site.Status = role == CommanderSamSiteAnalyzerService.SiteUnitRole.Irm
                ? "IRM emplacement added to the construction queue."
                : "23 mm emplacement added to the construction queue.";
        }

        TryRequestConstructionSupply(site);
    }

    private static int SecondsRemaining(float deadline)
    {
        return Mathf.Max(0, Mathf.CeilToInt(deadline - Time.timeSinceLevelLoad));
    }

    private static float GetReplacementCost(
        CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => MainComponentSupplyCost,
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => MainComponentSupplyCost,
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm => PointDefenseSupplyCost,
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm => PointDefenseSupplyCost,
            _ => 0f,
        };
    }

    private static void TriggerStoredAmmoDestruction(Unit core, float storedSupply)
    {
        // Let the Ammo Dump prefab play its own configured damage effects rather
        // than introducing a custom explosion that could diverge from Basegame.
        if (storedSupply > 500f && core.damageables.Count > 0)
        {
            core.Damage(0, new DamageInfo(0f, 10000f, 10000f, 0f));
        }

        if (!core.disabled)
        {
            core.DisableUnit();
        }
    }

    private static float GetCargoSupply(Unit cargo)
    {
        float capacity = 0f;
        Rearmer[] rearmers = cargo.GetComponentsInChildren<Rearmer>(true);
        for (int i = 0; i < rearmers.Length; i++)
        {
            capacity += Mathf.Max(0f, rearmers[i].Capacity);
        }

        return capacity;
    }

    private static void SetCoreSupply(ConstructionSite site, float capacity)
    {
        if (site.Core == null || site.CoreRearmer == null)
        {
            return;
        }

        float clamped = Mathf.Clamp(capacity, 0f, site.CoreRearmer.GetMaxCapacity());
        site.CoreRearmer.SetCapacity(clamped);
        site.Core.RpcUpdateRearmerCapacity(clamped);
    }

    private static GlobalPosition GetWorkerTarget(
        ConstructionSite site,
        CommanderSamSiteAnalyzerService.SiteLayoutMarker marker)
    {
        GlobalPosition core = GetCoreSurfacePosition(site);
        Vector3 direction = marker.Position.ToLocalPosition() - core.ToLocalPosition();
        direction.y = 0f;
        direction = direction.sqrMagnitude > 0.01f ? direction.normalized : Vector3.forward;
        Vector3 target = marker.Position.ToLocalPosition() - (direction * 10f);
        return target.ToGlobalPosition();
    }

    private static GlobalPosition GetCoreSurfacePosition(ConstructionSite site)
    {
        CommanderSamSiteAnalyzerService.SiteLayoutMarker tower = site.Layout.First(
            marker => marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower);
        return new GlobalPosition(tower.Position.x, tower.Position.y + 20f, tower.Position.z);
    }

    private static GlobalPosition GetSiteServiceTarget(ConstructionSite site)
    {
        GlobalPosition tower = GetCoreSurfacePosition(site);
        Vector2 front = site.EnemyDirection.sqrMagnitude > 0.01f
            ? site.EnemyDirection.normalized
            : Vector2.up;
        return SnapConstructionTargetToTerrain(new GlobalPosition(
            tower.x - (front.x * 10f),
            tower.y,
            tower.z - (front.y * 10f)));
    }

    // The shared helper lives in CommanderGameAccess because gold mine placement needs the
    // same drop-to-terrain.
    private static GlobalPosition SnapConstructionTargetToTerrain(GlobalPosition target)
    {
        return CommanderGameAccess.SnapToTerrain(target);
    }

    private static CommanderSamSiteAnalyzerService.SiteLayoutMarker? FindNextAvailableMarker(
        ConstructionSite site,
        CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        for (int i = 0; i < site.Layout.Count; i++)
        {
            CommanderSamSiteAnalyzerService.SiteLayoutMarker marker = site.Layout[i];
            string key = GetMarkerKey(marker);
            if (marker.Role == role
                && !site.BuiltMarkers.Contains(key)
                && !site.QueuedMarkers.Contains(key)
                && site.ActiveBuild?.Key != key)
            {
                return marker;
            }
        }

        return null;
    }

    private static string GetMarkerKey(
        CommanderSamSiteAnalyzerService.SiteLayoutMarker marker)
    {
        return $"{marker.Role}:{marker.Position.x:0.0}:{marker.Position.z:0.0}";
    }

    private static string GetRoleLabel(
        CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm => "23 mm emplacement",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm => "IRM emplacement",
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => "StratoLance launcher",
            CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => "fire-control truck",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => "radar truck",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => "ammunition dump",
            _ => role.ToString(),
        };
    }

    private static void DestroyNetworkUnit(Unit? unit)
    {
        if (unit == null || unit.Identity == null)
        {
            return;
        }

        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (manager?.ServerObjectManager != null)
        {
            manager.ServerObjectManager.Destroy(unit.Identity, !unit.Identity.IsSceneObject);
        }
    }

    private static void ActivateConstructionCore(ConstructionSite site, float initialSupply)
    {
        Unit? core = site.Core;
        Rearmer? rearmer = site.CoreRearmer;
        if (core == null || core.disabled || rearmer == null)
        {
            throw new InvalidOperationException("The SAM-site tracking core is unavailable.");
        }

        if (site.CoreOnline)
        {
            SetCoreSupply(site, initialSupply);
            return;
        }

        float maxCapacity = rearmer.GetMaxCapacity();
        float capacity = Mathf.Clamp(initialSupply, 0f, maxCapacity);
        rearmer.Range = CoreSupplyRangeMeters;
        rearmer.SetCapacity(capacity);
        rearmer.AvailableForMission = false;
        rearmer.enabled = true;
        site.Hq.RearmMissionController.RegisterRearmer(rearmer);
        core.RpcUpdateRearmerCapacity(capacity);
        site.CoreOnline = true;
        CommanderPlugin.Log.LogInfo(
            $"SAM site {site.Id} core online: supply={capacity:0}/{maxCapacity:0}, range={CoreSupplyRangeMeters:0}m.");
    }

    private static void QueueDestroyedAssetRepairs(ConstructionSite site)
    {
        bool found = false;
        Unit? wreck = null;
        CommanderSamSiteAnalyzerService.SiteLayoutMarker marker = default;
        Unit? staleEntry = null;
        foreach (KeyValuePair<Unit, CommanderSamSiteAnalyzerService.SiteLayoutMarker> entry in site.LayoutUnits)
        {
            bool unavailable = entry.Key == null || entry.Key.disabled;
            if (!unavailable
                || entry.Value.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.Platform
                || entry.Value.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower)
            {
                continue;
            }

            string key = GetMarkerKey(entry.Value);
            if (site.RepairQueuedMarkers.Contains(key))
            {
                continue;
            }

            found = true;
            if (entry.Key == null)
            {
                staleEntry = entry.Key;
            }
            else
            {
                wreck = entry.Key;
            }

            marker = entry.Value;
            break;
        }

        if (!found)
        {
            return;
        }

        if (!ReferenceEquals(staleEntry, null))
        {
            site.LayoutUnits.Remove(staleEntry);
        }

        string markerKey = GetMarkerKey(marker);
        site.RepairQueuedMarkers.Add(markerKey);
        site.QueuedMarkers.Add(markerKey);
        site.BuildQueue.Enqueue(new BuildTask(
            marker,
            markerKey,
            GetReplacementCost(marker.Role),
            wreck));
        site.Status = $"Destroyed {GetRoleLabel(marker.Role)} queued for automatic replacement.";
    }

    private static void QueueAllMarkers(
        ConstructionSite site,
        CommanderSamSiteAnalyzerService.SiteUnitRole role,
        float cost)
    {
        foreach (CommanderSamSiteAnalyzerService.SiteLayoutMarker marker in site.Layout)
        {
            if (marker.Role == role)
            {
                QueueMarker(site, marker, cost);
            }
        }
    }

    private static void QueueMarker(
        ConstructionSite site,
        CommanderSamSiteAnalyzerService.SiteLayoutMarker marker,
        float cost)
    {
        string key = GetMarkerKey(marker);
        if (site.BuiltMarkers.Contains(key) || !site.QueuedMarkers.Add(key))
        {
            return;
        }

        site.BuildQueue.Enqueue(new BuildTask(marker, key, cost));
    }

    private void ToggleConstructionSite()
    {
        analyzer.CopyActiveLayout(layout);
        if (layout.Count > 0 && TryFindSiteForLayout(layout, out ConstructionSite? existing))
        {
            RemoveConstructionSite(existing!);
            return;
        }

        TryCreateActiveConstructionSite(automaticBuild: false, instant: false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Base-game spawn code can throw anything; the half-made site is removed and the failure logged.")]
    private bool TryCreateActiveConstructionSite(bool automaticBuild, bool instant)
    {
        if (!MissionManager.IsRunning)
        {
            SetStatus("A running mission is required.", warning: true);
            return false;
        }

        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (spawner == null || !spawner.IsServer || hq == null)
        {
            SetStatus("SAM construction is host/singleplayer only.", warning: true);
            return false;
        }

        analyzer.CopyActiveLayout(layout);
        if (layout.Count == 0 || !analyzer.TryGetActiveEnemyDirection(out Vector2 enemyDirection))
        {
            SetStatus("Select an active SAM site with JUMP first.", warning: true);
            return false;
        }

        if (TryFindSiteForLayout(layout, out ConstructionSite? existing))
        {
            SetStatus($"SAM site {existing!.Id} already occupies this location.", warning: true);
            return false;
        }

        if (!ResolveDefinitions(layout, hq, out string missingRoles))
        {
            SetStatus($"Missing unit definitions: {missingRoles}. See log.", warning: true);
            return false;
        }

        CommanderSamSiteAnalyzerService.SiteLayoutMarker platform = layout.First(
            marker => marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.Platform);
        Vector3 facing = new(enemyDirection.x, 0f, enemyDirection.y);
        Quaternion enemyRotation = facing.sqrMagnitude > 0.01f
            ? Quaternion.LookRotation(facing.normalized, Vector3.up)
            : Quaternion.identity;
        int siteId = nextConstructionSiteId++;
        ConstructionSite site = new(
            siteId,
            hq,
            new List<CommanderSamSiteAnalyzerService.SiteLayoutMarker>(layout),
            new Dictionary<CommanderSamSiteAnalyzerService.SiteUnitRole, UnitDefinition>(definitions),
            enemyDirection,
            enemyRotation,
            ResolvePlatformSurface(platform.Position))
        {
            AutomaticBuild = automaticBuild,
        };
        constructionSites.Add(siteId, site);

        try
        {
            SpawnConstructionCoreShell(
                site,
                site.Definitions[CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo],
                GetCoreSurfacePosition(site));
        }
        catch (Exception exception)
        {
            RemoveConstructionSite(site);
            SetStatus($"SAM-site tracking core could not be created: {exception.Message}", warning: true);
            CommanderPlugin.Log.LogError($"SAM-site tracking core spawn failed: {exception}");
            return false;
        }

        if (instant)
        {
            try
            {
                SpawnCompleteDebugSite(site);
                SetStatus($"Debug SAM site {siteId} spawned complete.");
                return true;
            }
            catch (Exception exception)
            {
                RemoveConstructionSite(site);
                SetStatus($"Debug SAM-site spawn failed: {exception.Message}", warning: true);
                CommanderPlugin.Log.LogError($"Debug SAM-site spawn failed: {exception}");
                return false;
            }
        }

        if (!supplyHeliService.RequestSamSiteFoundationDrop(siteId, platform.Position))
        {
            constructionSites.Remove(siteId);
            SetStatus(supplyHeliService.StatusText, warning: true);
            return false;
        }

        site.Status = "Foundation helicopter inbound with Jacknife and ammunition.";
        SetStatus(automaticBuild
            ? $"AI SAM site {siteId} foundation delivery requested."
            : $"SAM site {siteId} foundation delivery requested.");
        return true;
    }

    private void SpawnCompleteDebugSite(ConstructionSite site)
    {
        site.InitialSupply = 40000f;
        SpawnFoundation(site);
        site.JacknifeDefinition = CommanderGameAccess.FindRepairVehicleDefinition();
        site.JacknifeInventory = site.JacknifeDefinition != null ? MaxJacknifeInventory : 0;
        for (int i = 0; i < site.Layout.Count; i++)
        {
            CommanderSamSiteAnalyzerService.SiteLayoutMarker marker = site.Layout[i];
            if (marker.Role is CommanderSamSiteAnalyzerService.SiteUnitRole.Platform
                or CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower)
            {
                continue;
            }

            SpawnLayoutUnit(site, marker);
            site.BuiltMarkers.Add(GetMarkerKey(marker));
        }

        site.SamBatteryQueued = true;
        site.Phase = ConstructionPhase.Ready;
        site.Status = $"Debug site complete. Jacknife inventory: {site.JacknifeInventory}/{MaxJacknifeInventory}.";
    }

    private void HandleFoundationCargoActivated(int siteId, Unit cargo)
    {
        if (cancelledFoundationSites.Contains(siteId))
        {
            DestroyNetworkUnit(cargo);
            return;
        }

        if (!constructionSites.TryGetValue(siteId, out ConstructionSite site)
            || cargo == null
            || cargo.disabled)
        {
            return;
        }

        string identity = $"{cargo.unitName} {cargo.definition?.unitName} {cargo.definition?.code} {cargo.definition?.jsonKey}";
        bool jacknife = identity.IndexOf("jacknife", StringComparison.OrdinalIgnoreCase) >= 0
            || identity.IndexOf("jackknife", StringComparison.OrdinalIgnoreCase) >= 0
            || cargo.GetComponentInChildren<Repairer>(true) != null;
        if (jacknife && cargo is GroundVehicle groundVehicle)
        {
            reservedConstructionJacknifes.Remove(groundVehicle);
            if (site.FoundationJacknifes.Count >= MaxJacknifeInventory)
            {
                DestroyNetworkUnit(cargo);
                return;
            }

            site.FoundationJacknifes.Add(groundVehicle);
            site.JacknifeDefinition ??= groundVehicle.definition as VehicleDefinition;
            AddSiteUnit(site, groundVehicle);
            CommandFoundationJacknife(site, groundVehicle);
            site.Status = "Jacknife deployed; moving to the control-tower foundation.";
            return;
        }

        Rearmer? rearmer = cargo.GetComponentInChildren<Rearmer>(true);
        if (rearmer != null && rearmer.Capacity > 0f)
        {
            site.FoundationAmmoCargoes.Add(new FoundationAmmoCargo(
                cargo,
                Time.timeSinceLevelLoad + 60f,
                Time.timeSinceLevelLoad + 45f));
            AddSiteUnit(site, cargo);
            site.Status = "Foundation ammunition delivered; waiting for cargo to settle.";
        }
    }

    private void HandleFoundationAmmunitionDelivered(int siteId, float supply)
    {
        if (!constructionSites.TryGetValue(siteId, out ConstructionSite site)
            || site.Phase != ConstructionPhase.Foundation
            || supply <= 0f)
        {
            return;
        }

        site.InitialSupply += supply;
        site.FoundationAmmoArrived = true;
        site.Status = $"Foundation ammunition secured: {site.InitialSupply:0} supply.";
    }

    private void HandleSupplyMissionFailed(
        int foundationSiteId,
        int supplySiteId,
        int jacknifeSiteId)
    {
        if (foundationSiteId >= 0
            && constructionSites.TryGetValue(foundationSiteId, out ConstructionSite foundationSite)
            && foundationSite.Phase == ConstructionPhase.Foundation)
        {
            RemoveConstructionSite(foundationSite);
            SetStatus($"SAM site {foundationSiteId} foundation delivery was lost. Select the site and retry.", warning: true);
        }

        if (supplySiteId >= 0 && constructionSites.TryGetValue(supplySiteId, out ConstructionSite supplySite))
        {
            supplySite.SupplyRunOutstanding = false;
            supplySite.NextSupplyRequestAt = Time.unscaledTime + 10f;
            supplySite.Status = "Supply aircraft lost; another delivery can be requested.";
        }

        if (jacknifeSiteId >= 0 && constructionSites.TryGetValue(jacknifeSiteId, out ConstructionSite jackSite))
        {
            jackSite.JacknifeRunOutstanding = false;
            jackSite.Status = "Jacknife delivery aircraft lost; another delivery can be requested.";
        }
    }

    private void TickConstructionSite(ConstructionSite site)
    {
        if (!HandleDestroyedSiteAssets(site))
        {
            return;
        }

        if (site.Phase == ConstructionPhase.Foundation)
        {
            TickFoundation(site);
            return;
        }

        if (site.Phase != ConstructionPhase.Ready || site.Core == null || site.Core.disabled)
        {
            return;
        }

        EnsureAutomaticSiteBuild(site);
        TickSiteLogistics(site);

        if (site.Worker != null)
        {
            TickConstructionWorker(site);
        }
        else if (site.BuildQueue.Count > 0)
        {
            TryStartNextBuild(site, null);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Runs every frame; a throw from base-game spawn code fails this one site, not the frame loop.")]
    private void TickFoundation(ConstructionSite site)
    {
        TickFoundationAmmo(site);
        GroundVehicle? jacknife = site.FoundationJacknifes.FirstOrDefault(
            unit => unit != null && !unit.disabled);
        if (jacknife == null)
        {
            return;
        }

        if (!site.FoundationJacknifeArrived)
        {
            float distance = CommanderGameAccess.HorizontalDistance(
                jacknife.transform.position,
                site.FoundationJacknifeTarget.ToLocalPosition());
            if (distance <= 5f || Time.timeSinceLevelLoad >= site.FoundationJacknifeDeadline)
            {
                if (distance > 5f)
                {
                    CommanderGameAccess.TeleportGroundVehicle(jacknife, site.FoundationJacknifeTarget);
                }

                jacknife.StopImmediately();
                jacknife.SetHoldPosition(true);
                site.FoundationJacknifeArrived = true;
                site.Status = "Jacknife is waiting for foundation ammunition.";
            }
        }

        if (!site.FoundationJacknifeArrived || !site.FoundationAmmoArrived)
        {
            return;
        }

        if (site.FoundationBuildAt <= 0f)
        {
            site.FoundationBuildAt = Time.timeSinceLevelLoad + ConstructionDuration;
            site.Status = "Constructing control tower and landing platform (30 seconds).";
            return;
        }

        if (Time.timeSinceLevelLoad < site.FoundationBuildAt)
        {
            return;
        }

        try
        {
            SpawnFoundation(site);
            int recovered = Mathf.Min(
                MaxJacknifeInventory,
                site.FoundationJacknifes.Count(unit => unit != null && !unit.disabled));
            site.JacknifeInventory = recovered;
            foreach (GroundVehicle unit in site.FoundationJacknifes.ToArray())
            {
                RemoveAndDestroySiteUnit(site, unit);
            }

            site.FoundationJacknifes.Clear();
            site.Phase = ConstructionPhase.Ready;
            site.Status = $"Site core online. Jacknife inventory: {site.JacknifeInventory}/{MaxJacknifeInventory}.";
            if (site.AutomaticBuild)
            {
                QueueAutomaticSiteBuild(site);
            }

            SetStatus($"SAM site {site.Id} foundation completed.");
        }
        catch (Exception exception)
        {
            site.Status = "Foundation construction failed. See log.";
            CommanderPlugin.Log.LogError($"SAM site {site.Id} foundation failed: {exception}");
        }
    }

    private void TickFoundationAmmo(ConstructionSite site)
    {
        if (site.FoundationAmmoCargoes.Count == 0)
        {
            return;
        }

        for (int i = site.FoundationAmmoCargoes.Count - 1; i >= 0; i--)
        {
            FoundationAmmoCargo entry = site.FoundationAmmoCargoes[i];
            Unit? cargo = entry.Unit;
            if (cargo == null || cargo.disabled)
            {
                site.FoundationAmmoCargoes.RemoveAt(i);
                continue;
            }

            bool stationary = cargo.rb == null
                || (cargo.rb.velocity.sqrMagnitude < 0.25f && cargo.rb.angularVelocity.sqrMagnitude < 0.25f);
            entry.StableSince = stationary
                ? entry.StableSince < 0f ? Time.timeSinceLevelLoad : entry.StableSince
                : -1f;
            if ((entry.StableSince < 0f || Time.timeSinceLevelLoad - entry.StableSince < 2f)
                && Time.timeSinceLevelLoad < entry.Timeout)
            {
                continue;
            }

            if (!entry.Deposited)
            {
                site.InitialSupply += GetCargoSupply(cargo);
                site.FoundationAmmoArrived = true;
                entry.Deposited = true;
                site.Status = $"Foundation ammunition secured: {site.InitialSupply:0} supply.";
            }

            if (Time.timeSinceLevelLoad < entry.RemoveAt)
            {
                continue;
            }

            site.FoundationAmmoCargoes.RemoveAt(i);
            RemoveAndDestroySiteUnit(site, cargo);
        }
    }

    private void SpawnFoundation(ConstructionSite site)
    {
        CommanderSamSiteAnalyzerService.SiteLayoutMarker platform = site.Layout.First(
            marker => marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.Platform);
        CommanderSamSiteAnalyzerService.SiteLayoutMarker tower = site.Layout.First(
            marker => marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower);
        site.Platform = SpawnLayoutUnit(site, platform);
        site.PlatformTarget = ResolvePlatformDeckSurface(site.Platform, platform.Position);
        site.ControlTower = SpawnLayoutUnit(site, tower);

        ActivateConstructionCore(site, site.InitialSupply);
    }

    private void SpawnConstructionCoreShell(
        ConstructionSite site,
        UnitDefinition ammoDefinition,
        GlobalPosition position)
    {
        Spawner spawner = NetworkSceneSingleton<Spawner>.i;
        Vector3 localPosition = position.ToLocalPosition() + ammoDefinition.spawnOffset;
        Unit core = spawner.SpawnFromUnitDefinitionInEditor(
            ammoDefinition,
            localPosition.ToGlobalPosition(),
            Quaternion.identity,
            site.Hq,
            $"NOC_SAM_CORE_{site.Id}");
        Rearmer? rearmer = core?.GetComponentInChildren<Rearmer>(true);
        if (core == null || rearmer == null)
        {
            throw new InvalidOperationException("The SAM-site core could not be spawned with a Rearmer.");
        }

        float truckCapacity = FindFactionMunitionsTruckCapacity(site.Hq);
        float maxCapacity = truckCapacity > 0f
            ? truckCapacity * 4f
            : FallbackCoreCapacity;
        site.Hq.RearmMissionController.DeregisterRearmer(rearmer);
        rearmer.Range = 0f;
        rearmer.SetCapacity(0f);
        RearmerMaxCapacityField?.SetValue(rearmer, maxCapacity);
        RearmerSingleUseField?.SetValue(rearmer, false);
        rearmer.AvailableForMission = false;
        rearmer.enabled = false;
        core.RpcUpdateRearmerCapacity(0f);
        core.NetworkunitName = "SAM Site Supplies";

        HideCoreVisuals(core);
        AddSiteUnit(site, core);
        site.Core = core;
        site.CoreRearmer = rearmer;
        constructionSitesByCore[core] = site;
        CommanderSamSiteCoreRegistry.Register(core, null);
        CommanderSelectionService.PinSamSiteUnit(core, $"SAM SITE {site.Id}");
        CommanderPlugin.Log.LogInfo(
            $"SAM site {site.Id} tracking core created: supply=0/{maxCapacity:0}, logistics=offline.");
    }

    private void TryStartNextBuild(ConstructionSite site, GroundVehicle? existingWorker)
    {
        if (site.BuildQueue.Count == 0)
        {
            if (existingWorker != null)
            {
                BeginWorkerReturn(site, existingWorker);
            }

            return;
        }

        BuildTask task = site.BuildQueue.Peek();
        if (site.CoreRearmer == null || site.Core == null)
        {
            return;
        }

        if (site.CoreRearmer.Capacity + 0.01f < task.Cost)
        {
            site.Status = $"Waiting for supply: {task.Cost:0} required, {site.CoreRearmer.Capacity:0} available.";
            TryRequestConstructionSupply(site);
            if (existingWorker != null)
            {
                BeginWorkerReturn(site, existingWorker);
            }

            return;
        }

        GroundVehicle? worker = existingWorker ?? SpawnInventoryJacknife(site);
        if (worker == null)
        {
            site.Status = "Construction waiting for an available Jacknife.";
            if (site.Core != null && CanRequestConstructionJacknife(site.Core))
            {
                RequestConstructionJacknife(site.Core);
            }

            return;
        }

        site.BuildQueue.Dequeue();
        site.QueuedMarkers.Remove(task.Key);
        SetCoreSupply(site, site.CoreRearmer.Capacity - task.Cost);
        site.ActiveBuild = task;
        site.Worker = worker;
        site.WorkerPhase = WorkerPhase.Travelling;
        site.WorkerTarget = SnapConstructionTargetToTerrain(
            GetWorkerTarget(site, task.Marker));
        site.WorkerDeadline = Time.timeSinceLevelLoad + ConstructionTravelTimeout;
        IssueConstructionMove(worker, site.WorkerTarget);
        site.Status = $"Jacknife moving to {GetRoleLabel(task.Marker.Role)}.";
    }

    private void TickConstructionWorker(ConstructionSite site)
    {
        GroundVehicle? worker = site.Worker;
        if (worker == null || worker.disabled)
        {
            site.Worker = null;
            site.ActiveBuild = null;
            site.WorkerPhase = WorkerPhase.None;
            site.Status = "Construction Jacknife was lost.";
            return;
        }

        float distance = CommanderGameAccess.HorizontalDistance(
            worker.transform.position,
            site.WorkerTarget.ToLocalPosition());
        if (site.WorkerPhase == WorkerPhase.Travelling)
        {
            if (distance > 5f && Time.timeSinceLevelLoad < site.WorkerDeadline)
            {
                return;
            }

            if (distance > 5f)
            {
                CommanderGameAccess.TeleportGroundVehicle(worker, site.WorkerTarget);
            }

            worker.StopImmediately();
            worker.SetHoldPosition(true);
            if (site.ActiveBuild?.Wreck != null)
            {
                RemoveAndDestroySiteUnit(site, site.ActiveBuild.Wreck);
                site.Status = $"Removed destroyed {GetRoleLabel(site.ActiveBuild.Marker.Role)}; preparing replacement.";
            }

            site.WorkerPhase = WorkerPhase.Constructing;
            site.ConstructionCompletesAt = Time.timeSinceLevelLoad + ConstructionDuration;
            if (site.ActiveBuild?.Wreck == null)
            {
                site.Status = $"Constructing {GetRoleLabel(site.ActiveBuild!.Marker.Role)} (30 seconds).";
            }

            return;
        }

        if (site.WorkerPhase == WorkerPhase.Constructing)
        {
            if (Time.timeSinceLevelLoad < site.ConstructionCompletesAt)
            {
                return;
            }

            BuildTask completed = site.ActiveBuild!;
            SpawnLayoutUnit(site, completed.Marker);
            site.BuiltMarkers.Add(completed.Key);
            site.RepairQueuedMarkers.Remove(completed.Key);
            site.ActiveBuild = null;
            site.WorkerPhase = WorkerPhase.None;
            site.Status = $"{GetRoleLabel(completed.Marker.Role)} construction completed.";
            TryStartNextBuild(site, worker);
            return;
        }

        if (site.WorkerPhase == WorkerPhase.Returning
            && (distance <= 5f || Time.timeSinceLevelLoad >= site.WorkerDeadline))
        {
            if (distance > 5f)
            {
                CommanderGameAccess.TeleportGroundVehicle(worker, site.WorkerTarget);
            }

            RemoveAndDestroySiteUnit(site, worker);
            site.Worker = null;
            site.WorkerPhase = WorkerPhase.None;
            site.JacknifeInventory = Mathf.Min(MaxJacknifeInventory, site.JacknifeInventory + 1);
            site.Status = $"Jacknife returned to inventory ({site.JacknifeInventory}/{MaxJacknifeInventory}).";
        }
    }

    private GroundVehicle? SpawnInventoryJacknife(ConstructionSite site)
    {
        if (site.JacknifeInventory <= 0 || site.JacknifeDefinition == null)
        {
            return null;
        }

        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (spawner == null)
        {
            return null;
        }

        GlobalPosition spawnPosition = GetSiteServiceTarget(site);
        Quaternion spawnRotation = site.EnemyRotation;
        Vector3 localSpawnPosition = spawnPosition.ToLocalPosition()
            + (spawnRotation * site.JacknifeDefinition.spawnOffset);
        Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
            site.JacknifeDefinition,
            localSpawnPosition.ToGlobalPosition(),
            spawnRotation,
            site.Hq,
            $"NOC_SAM_{site.Id}_JACK_{Time.frameCount}");
        if (unit is not GroundVehicle worker)
        {
            DestroyNetworkUnit(unit);
            return null;
        }

        site.JacknifeInventory--;
        AddSiteUnit(site, worker);
        worker.SetHoldPosition(true);
        return worker;
    }

    private void BeginWorkerReturn(ConstructionSite site, GroundVehicle worker)
    {
        site.Worker = worker;
        site.ActiveBuild = null;
        site.WorkerPhase = WorkerPhase.Returning;
        site.WorkerTarget = GetSiteServiceTarget(site);
        site.WorkerDeadline = Time.timeSinceLevelLoad + ConstructionTravelTimeout;
        IssueConstructionMove(worker, site.WorkerTarget);
        site.Status = "Jacknife returning to the site inventory.";
    }

    private Unit SpawnLayoutUnit(
        ConstructionSite site,
        CommanderSamSiteAnalyzerService.SiteLayoutMarker marker)
    {
        Spawner spawner = NetworkSceneSingleton<Spawner>.i;
        UnitDefinition definition = site.Definitions[marker.Role];
        Quaternion rotation = marker.Role is CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm
            or CommanderSamSiteAnalyzerService.SiteUnitRole.Irm
            or CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher
            ? site.EnemyRotation
            : Quaternion.identity;
        GlobalPosition surfacePosition = marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower
            ? marker.Position
            : SnapConstructionTargetToTerrain(marker.Position);
        Vector3 localPosition = surfacePosition.ToLocalPosition() + (rotation * definition.spawnOffset);
        Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
            definition,
            localPosition.ToGlobalPosition(),
            rotation,
            site.Hq,
            $"NOC_SAM_{site.Id}_{marker.Role}_{Time.frameCount}");
        if (unit == null)
        {
            throw new InvalidOperationException($"Spawner returned null for {definition.unitName}.");
        }

        if (unit is GroundVehicle groundVehicle)
        {
            groundVehicle.SetHoldPosition(true);
        }
        else if (unit is Ship ship)
        {
            ship.SetHoldPosition(true);
        }

        AddSiteUnit(site, unit);
        site.LayoutUnits[unit] = marker;
        if (ShouldExposeSiteMarker(marker.Role))
        {
            CommanderSamSiteCoreRegistry.RegisterTracked(unit);
        }

        if (marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo)
        {
            DisableDecorativeRearmer(unit, site.Hq);
            CommanderSamSiteCoreRegistry.MapVisualToCore(unit, site.Core);
        }

        return unit;
    }

    private bool HandleDestroyedSiteAssets(ConstructionSite site)
    {
        if (site.Phase == ConstructionPhase.Foundation)
        {
            return true;
        }

        if (!site.TowerLost && (site.ControlTower == null || site.ControlTower.disabled))
        {
            site.TowerLost = true;
            site.AutomaticSupplyEnabled = false;
            site.BuildQueue.Clear();
            site.QueuedMarkers.Clear();
            site.ActiveBuild = null;
            supplyHeliService.CancelSamSiteMissions(site.Id);

            float storedSupply = site.CoreRearmer?.Capacity ?? 0f;
            Unit? core = site.Core;
            if (core != null && !core.disabled)
            {
                if (site.CoreRearmer != null)
                {
                    site.Hq.RearmMissionController.DeregisterRearmer(site.CoreRearmer);
                }

                TriggerStoredAmmoDestruction(core, storedSupply);
            }

            site.Status = storedSupply > 500f
                ? $"Control tower destroyed; ammunition core disabled with {storedSupply:0} supply remaining."
                : "Control tower destroyed; SAM-site logistics are offline.";
            CommanderPlugin.Log.LogInfo(
                $"SAM site {site.Id} lost its control tower: core disabled, storedSupply={storedSupply:0}.");
            return false;
        }

        if (!site.PlatformLost && (site.Platform == null || site.Platform.disabled))
        {
            site.PlatformLost = true;
            site.AutomaticSupplyEnabled = false;
            site.SupplyRunOutstanding = false;
            site.JacknifeRunOutstanding = false;
            supplyHeliService.CancelSamSiteMissions(site.Id);
            site.Status = "Landing platform destroyed; air logistics are unavailable.";
        }

        QueueDestroyedAssetRepairs(site);

        return !site.TowerLost;
    }

    private void TryRequestConstructionSupply(ConstructionSite site)
    {
        TryRequestAutomaticSiteSupply(site);
    }

    private bool DepositConstructionAmmunition(
        int siteId,
        Unit cargo,
        out float transferred)
    {
        transferred = 0f;
        ConstructionSite? site = constructionSites.TryGetValue(siteId, out ConstructionSite exact)
            ? exact
            : null;
        float nearest = float.MaxValue;
        if (site == null)
        {
            foreach (ConstructionSite candidate in constructionSites.Values)
            {
                if (candidate.Core == null || candidate.CoreRearmer == null || candidate.Core.disabled)
                {
                    continue;
                }

                float distance = FastMath.SquareDistance(
                    cargo.GlobalPosition(),
                    candidate.Core.GlobalPosition());
                if (distance < nearest)
                {
                    nearest = distance;
                    site = candidate;
                }
            }
        }

        if (site?.Core == null || site.CoreRearmer == null)
        {
            return false;
        }

        float cargoSupply = GetCargoSupply(cargo);
        if (cargoSupply <= 0.01f)
        {
            return false;
        }

        float free = Mathf.Max(0f, site.CoreRearmer.GetMaxCapacity() - site.CoreRearmer.Capacity);
        transferred = Mathf.Min(cargoSupply, free);
        SetCoreSupply(site, site.CoreRearmer.Capacity + transferred);
        site.SupplyRunOutstanding = false;
        site.Status = $"Received {transferred:0} supply.";
        return true;
    }

    private bool DepositConstructionAmmunitionAmount(
        int siteId,
        float supply,
        out float transferred)
    {
        transferred = 0f;
        if (!constructionSites.TryGetValue(siteId, out ConstructionSite site)
            || site.Core == null
            || site.Core.disabled
            || site.CoreRearmer == null
            || supply <= 0f)
        {
            return false;
        }

        float free = Mathf.Max(0f, site.CoreRearmer.GetMaxCapacity() - site.CoreRearmer.Capacity);
        transferred = Mathf.Min(supply, free);
        SetCoreSupply(site, site.CoreRearmer.Capacity + transferred);
        site.SupplyRunOutstanding = false;
        site.Status = $"Received {transferred:0} supply.";
        return true;
    }

    private void CommandFoundationJacknife(ConstructionSite site, GroundVehicle jacknife)
    {
        CommanderSamSiteAnalyzerService.SiteLayoutMarker tower = site.Layout.First(
            marker => marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower);
        Vector2 front = site.EnemyDirection.sqrMagnitude > 0.01f
            ? site.EnemyDirection.normalized
            : Vector2.up;
        GlobalPosition target = SnapConstructionTargetToTerrain(new GlobalPosition(
            tower.Position.x - (front.x * 10f),
            tower.Position.y + 20f,
            tower.Position.z - (front.y * 10f)));
        site.FoundationJacknifeTarget = target;
        site.FoundationJacknifeDeadline = Time.timeSinceLevelLoad + ConstructionTravelTimeout;
        IssueConstructionMove(jacknife, target);
    }

    private void IssueConstructionMove(GroundVehicle vehicle, GlobalPosition target)
    {
        GlobalPosition terrainTarget = SnapConstructionTargetToTerrain(target);
        CommanderDirectPathService.ForceEnabled(vehicle);
        vehicle.StopImmediately();
        vehicle.SetHoldPosition(false);
        issuingConstructionMove = true;
        try
        {
            vehicle.UnitCommand?.SetDestination(terrainTarget, playerCommand: true);
        }
        finally
        {
            issuingConstructionMove = false;
        }
    }

    private void QueueAutomaticSiteBuild(ConstructionSite site)
    {
        QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl, 0f);
        QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher, MainComponentSupplyCost);
        QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo, 0f);
        QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.Irm, PointDefenseSupplyCost);
        QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm, PointDefenseSupplyCost);

        // Radar is deliberately last so the site is not emitting before its defenses exist.
        QueueAllMarkers(site, CommanderSamSiteAnalyzerService.SiteUnitRole.Radar, MainComponentSupplyCost);
        site.SamBatteryQueued = true;
        site.Status = $"AI queued complete site construction ({site.BuildQueue.Count} assets; radar last).";
        TryRequestConstructionSupply(site);
    }

    private void EnsureAutomaticSiteBuild(ConstructionSite site)
    {
        if (!site.AutomaticBuild || site.AutomaticBuildCompleted)
        {
            return;
        }

        bool complete = true;
        for (int i = 0; i < site.Layout.Count; i++)
        {
            CommanderSamSiteAnalyzerService.SiteLayoutMarker marker = site.Layout[i];
            if (marker.Role is CommanderSamSiteAnalyzerService.SiteUnitRole.Platform
                or CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower)
            {
                continue;
            }

            if (!site.BuiltMarkers.Contains(GetMarkerKey(marker)))
            {
                complete = false;
                break;
            }
        }

        if (complete)
        {
            site.AutomaticBuildCompleted = true;
            site.Status = "AI site construction completed.";
            return;
        }

        if (site.ActiveBuild == null && site.BuildQueue.Count == 0)
        {
            QueueAutomaticSiteBuild(site);
        }
    }

    private bool TryFindSiteForLayout(
        IReadOnlyList<CommanderSamSiteAnalyzerService.SiteLayoutMarker> source,
        out ConstructionSite? site)
    {
        site = null;

        // Not FirstOrDefault: on a struct that returns a default marker, never null, and the
        // default marker is a radar at the origin.
        CommanderSamSiteAnalyzerService.SiteLayoutMarker? radar = null;
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Role == CommanderSamSiteAnalyzerService.SiteUnitRole.Radar)
            {
                radar = source[i];
                break;
            }
        }

        if (radar == null)
        {
            return false;
        }

        foreach (ConstructionSite candidate in constructionSites.Values)
        {
            CommanderSamSiteAnalyzerService.SiteLayoutMarker candidateRadar = candidate.Layout.First(
                marker => marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.Radar);
            float dx = candidateRadar.Position.x - radar.Value.Position.x;
            float dz = candidateRadar.Position.z - radar.Value.Position.z;
            if ((dx * dx) + (dz * dz) < 100f * 100f)
            {
                site = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryGetConstructionPlatformTarget(out GlobalPosition target)
    {
        ConstructionSite? ready = constructionSites.Values
            .Where(site => site.Phase == ConstructionPhase.Ready)
            .OrderByDescending(site => site.Id)
            .FirstOrDefault();
        if (ready != null)
        {
            target = ready.PlatformTarget;
            return true;
        }

        target = default;
        return false;
    }

    private void RemoveConstructionSite(ConstructionSite site)
    {
        cancelledFoundationSites.Add(site.Id);
        supplyHeliService.CancelSamSiteMissions(site.Id);
        foreach (Unit unit in site.SpawnedUnits.ToArray())
        {
            RemoveAndDestroySiteUnit(site, unit);
        }

        if (site.Core != null)
        {
            constructionSitesByCore.Remove(site.Core);
        }

        constructionSites.Remove(site.Id);
        SetStatus($"Removed SAM site {site.Id}.");
    }

    private void ResetConstructionSession()
    {
        constructionSites.Clear();
        constructionSitesByCore.Clear();
        cancelledFoundationSites.Clear();
        reservedConstructionJacknifes.Clear();
        spawnedUnits.Clear();
        definitions.Clear();
        layout.Clear();
        nextConstructionSiteId = 1;
        nextConstructionTickAt = 0f;
        CommanderSamSiteCoreRegistry.Clear();
        StatusText = "Select and jump to a SAM site before spawning.";
    }

    private void PruneConstructionSites()
    {
        foreach (ConstructionSite site in constructionSites.Values)
        {
            site.SpawnedUnits.RemoveAll(unit => unit == null);
        }
    }

    private void AddSiteUnit(ConstructionSite site, Unit unit)
    {
        if (!site.SpawnedUnits.Contains(unit))
        {
            site.SpawnedUnits.Add(unit);
        }

        if (!spawnedUnits.Contains(unit))
        {
            spawnedUnits.Add(unit);
        }
    }

    private void RemoveAndDestroySiteUnit(ConstructionSite site, Unit? unit)
    {
        if (unit == null)
        {
            return;
        }

        site.SpawnedUnits.Remove(unit);
        spawnedUnits.Remove(unit);
        site.LayoutUnits.Remove(unit);
        if (unit is GroundVehicle jacknife)
        {
            reservedConstructionJacknifes.Remove(jacknife);
            CommanderDirectPathService.Forget(jacknife);
        }

        CommanderSamSiteCoreRegistry.Unregister(unit);
        if (ReferenceEquals(site.Core, unit))
        {
            CommanderSelectionService.RemoveSamSiteUnit(unit);
            constructionSitesByCore.Remove(unit);
            site.Core = null;
            site.CoreRearmer = null;
        }

        DestroyNetworkUnit(unit);
    }

    private sealed class ConstructionSite
    {
        internal ConstructionSite(
            int id,
            FactionHQ hq,
            List<CommanderSamSiteAnalyzerService.SiteLayoutMarker> layout,
            Dictionary<CommanderSamSiteAnalyzerService.SiteUnitRole, UnitDefinition> definitions,
            Vector2 enemyDirection,
            Quaternion enemyRotation,
            GlobalPosition platformTarget)
        {
            Id = id;
            Hq = hq;
            Layout = layout;
            Definitions = definitions;
            EnemyDirection = enemyDirection;
            EnemyRotation = enemyRotation;
            PlatformTarget = platformTarget;
        }

        internal List<Unit> SpawnedUnits { get; } = new();

        internal List<GroundVehicle> FoundationJacknifes { get; } = new();

        internal List<FoundationAmmoCargo> FoundationAmmoCargoes { get; } = new();

        internal Queue<BuildTask> BuildQueue { get; } = new();

        internal HashSet<string> QueuedMarkers { get; } = new();

        internal HashSet<string> BuiltMarkers { get; } = new();

        internal HashSet<string> RepairQueuedMarkers { get; } = new();

        internal Dictionary<Unit, CommanderSamSiteAnalyzerService.SiteLayoutMarker> LayoutUnits { get; } = new();

        internal List<CommanderSupplyHeliService.SamSiteAirbaseOption> SupplyAirbases { get; } = new();

        internal Dictionary<Airbase, CachedSupplyRoute> SupplyRoutes { get; } = new();

        internal List<IncomingJacknife> IncomingJacknifes { get; } = new();

        internal int Id { get; }

        internal FactionHQ Hq { get; }

        internal List<CommanderSamSiteAnalyzerService.SiteLayoutMarker> Layout { get; }

        internal Dictionary<CommanderSamSiteAnalyzerService.SiteUnitRole, UnitDefinition> Definitions { get; }

        internal Vector2 EnemyDirection { get; }

        internal Quaternion EnemyRotation { get; }

        internal GlobalPosition PlatformTarget { get; set; }

        internal ConstructionPhase Phase { get; set; }

        internal bool AutomaticBuild { get; set; }

        internal bool AutomaticBuildCompleted { get; set; }

        internal string Status { get; set; } = "Foundation delivery requested.";

        internal bool FoundationAmmoArrived { get; set; }

        internal bool FoundationJacknifeArrived { get; set; }

        internal GlobalPosition FoundationJacknifeTarget { get; set; }

        internal float FoundationJacknifeDeadline { get; set; }

        internal float FoundationBuildAt { get; set; }

        internal float InitialSupply { get; set; }

        internal Unit? Core { get; set; }

        internal Rearmer? CoreRearmer { get; set; }

        internal bool CoreOnline { get; set; }

        internal Unit? ControlTower { get; set; }

        internal Unit? Platform { get; set; }

        internal bool TowerLost { get; set; }

        internal bool PlatformLost { get; set; }

        internal VehicleDefinition? JacknifeDefinition { get; set; }

        internal int JacknifeInventory { get; set; }

        internal bool SamBatteryQueued { get; set; }

        internal BuildTask? ActiveBuild { get; set; }

        internal GroundVehicle? Worker { get; set; }

        internal WorkerPhase WorkerPhase { get; set; }

        internal GlobalPosition WorkerTarget { get; set; }

        internal float WorkerDeadline { get; set; }

        internal float ConstructionCompletesAt { get; set; }

        internal bool SupplyRunOutstanding { get; set; }

        internal float NextSupplyRequestAt { get; set; }

        internal float SupplyRequestExpiresAt { get; set; }

        internal bool AutomaticSupplyEnabled { get; set; } = true;

        internal float AutomaticSupplyThreshold { get; set; } = 10000f;

        internal Airbase? SelectedSupplyAirbase { get; set; }

        internal bool SupplyAirbaseManuallySelected { get; set; }

        internal bool UseCustomSupplyRoute { get; set; } = true;

        internal bool SupplyRouteVisible { get; set; }

        internal float NextAirbaseRefreshAt { get; set; }

        internal bool JacknifeRunOutstanding { get; set; }

        internal float JacknifeRequestExpiresAt { get; set; }
    }

    private sealed class CachedSupplyRoute
    {
        internal CachedSupplyRoute(
            bool planned,
            List<GlobalPosition> waypoints,
            bool steepLanding,
            float risk,
            float routeLength)
        {
            Planned = planned;
            Waypoints = waypoints;
            SteepLanding = steepLanding;
            Risk = risk;
            RouteLength = routeLength;
        }

        internal bool Planned { get; }

        internal List<GlobalPosition> Waypoints { get; }

        internal bool SteepLanding { get; }

        internal float Risk { get; }

        internal float RouteLength { get; }
    }

    private sealed class BuildTask
    {
        internal BuildTask(
            CommanderSamSiteAnalyzerService.SiteLayoutMarker marker,
            string key,
            float cost,
            Unit? wreck = null)
        {
            Marker = marker;
            Key = key;
            Cost = cost;
            Wreck = wreck;
        }

        internal CommanderSamSiteAnalyzerService.SiteLayoutMarker Marker { get; }

        internal string Key { get; }

        internal float Cost { get; }

        internal Unit? Wreck { get; }
    }

    private sealed class FoundationAmmoCargo
    {
        internal FoundationAmmoCargo(Unit unit, float timeout, float removeAt)
        {
            Unit = unit;
            Timeout = timeout;
            RemoveAt = removeAt;
        }

        internal Unit Unit { get; }

        internal float Timeout { get; }

        internal float RemoveAt { get; }

        internal float StableSince { get; set; } = -1f;

        internal bool Deposited { get; set; }
    }

    private sealed class IncomingJacknife
    {
        internal IncomingJacknife(GroundVehicle unit, GlobalPosition target, float timeout)
        {
            Unit = unit;
            Target = target;
            Timeout = timeout;
        }

        internal GroundVehicle Unit { get; }

        internal GlobalPosition Target { get; }

        internal float Timeout { get; }
    }
}
