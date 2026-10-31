using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderMobileEmplacementService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    private const float TractorRange = 300f;
    private const float ArrivalDistance = 12f;
    private const float StagingDistance = 18f;
    private const float HandlingSeconds = 10f;
    private const float UpdateIntervalSeconds = 0.5f;
    private const float StatusDurationSeconds = 5f;

    private readonly CommanderSelectionService selectionService;
    private readonly List<RelocationJob> jobs = new();
    private readonly HashSet<GroundVehicle> reservedHaulers = new();

    private PendingRelocation? pendingRelocation;
    private float nextUpdateAt;
    private float statusUntil;
    private string statusText = string.Empty;
    private GroundVehicle? availabilityTrailer;
    private bool cachedHaulerAvailable;
    private float nextAvailabilityCheckAt;

    private bool issuingInternalHaulerCommand;

    internal CommanderMobileEmplacementService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
        CommanderNetwork.OnHost("emplace", HostRelocate);
    }

    private enum RelocationStage
    {
        ApproachingPickup,
        Loading,
        Transporting,
        Unloading,
    }

    internal static CommanderMobileEmplacementService? Instance { get; private set; }

    internal bool AwaitingDestination => pendingRelocation != null;

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    public void Activate()
    {
        nextUpdateAt = CommanderScheduler.Stagger("mobile-emplacements", UpdateIntervalSeconds, 0.2f);
    }

    public void Deactivate()
    {
        pendingRelocation = null;
        statusText = string.Empty;
    }

    public void ResetSession()
    {
        pendingRelocation = null;
        jobs.Clear();
        reservedHaulers.Clear();
        statusText = string.Empty;
        availabilityTrailer = null;
        cachedHaulerAvailable = false;
        nextAvailabilityCheckAt = 0f;
    }

    public void TickActive()
    {
        if (CommanderGameInput.CancelDown)
        {
            CancelDestinationSelection();
        }
    }

    public void TickPersistent()
    {
        if (!CommanderScheduler.IsDue(ref nextUpdateAt, UpdateIntervalSeconds))
        {
            return;
        }

        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            RelocationJob job = jobs[i];
            if (!UpdateJob(job))
            {
                continue;
            }

            if (job.Tractor != null && !job.Tractor.disabled)
            {
                CommanderGameAccess.SetUnitHoldPosition(job.Tractor, hold: false);
            }

            reservedHaulers.Remove(job.Tractor!);
            jobs.RemoveAt(i);
        }
    }

    internal static bool IsReservedHauler(Unit? unit)
    {
        return Instance != null
            && unit is GroundVehicle vehicle
            && Instance.reservedHaulers.Contains(vehicle);
    }

    internal static bool ShouldBlockDestination(UnitCommand command)
    {
        CommanderMobileEmplacementService? service = Instance;
        if (service == null || service.issuingInternalHaulerCommand)
        {
            return false;
        }

        return IsReservedHauler(command.GetComponent<Unit>());
    }

    internal static bool IsMoveableTrailer(Unit? unit)
    {
        return unit is GroundVehicle vehicle
            && CommanderGameAccess.IsFriendlyUnit(vehicle, CommanderGameAccess.GetLocalHq())
            && CommanderGameAccess.IsTrailerVehicleDefinition(vehicle.definition as VehicleDefinition);
    }

    internal void CancelDestinationSelection()
    {
        if (!AwaitingDestination)
        {
            return;
        }

        pendingRelocation = null;
        SetStatus("Trailer relocation cancelled.");
    }

    internal bool IsRelocating(Unit? trailer)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            if (ReferenceEquals(jobs[i].Trailer, trailer))
            {
                return true;
            }
        }

        return pendingRelocation != null && ReferenceEquals(pendingRelocation.Trailer, trailer);
    }

    internal bool HasAvailableHauler(Unit? unit)
    {
        if (unit is not GroundVehicle trailer || !IsMoveableTrailer(trailer))
        {
            return false;
        }

        if (ReferenceEquals(availabilityTrailer, trailer) && Time.unscaledTime < nextAvailabilityCheckAt)
        {
            return cachedHaulerAvailable;
        }

        availabilityTrailer = trailer;
        cachedHaulerAvailable = TryFindTractor(trailer, out _);
        nextAvailabilityCheckAt = Time.unscaledTime + UpdateIntervalSeconds;
        return cachedHaulerAvailable;
    }

    internal void BeginRelocation()
    {
        // The tractor is driven by the host; a guest's relocation is asked for when it clicks the spot.
        if (NetworkManagerNuclearOption.i == null
            || (!NetworkManagerNuclearOption.i.Server.Active && !CommanderNetwork.HostHasMod))
        {
            SetStatus(CommanderNetwork.HostMissingReason);
            return;
        }

        Unit? selected = selectionService.FocusedSelection;
        if (!IsMoveableTrailer(selected) || selected is not GroundVehicle trailer)
        {
            SetStatus("Select a friendly static trailer first.");
            return;
        }

        if (IsRelocating(trailer))
        {
            SetStatus("This trailer already has a relocation order.");
            return;
        }

        if (!TryFindTractor(trailer, out GroundVehicle tractor))
        {
            SetStatus("Requires an idle HLT/MSV Tractor or Flatbed within 300 m.");
            return;
        }

        pendingRelocation = new PendingRelocation(trailer, tractor);
        availabilityTrailer = null;
        SetStatus("Click the new emplacement position in the 3D world. The game's Cancel binding cancels.");
    }

    internal bool TrySetDestinationFromWorld(Vector2 screenPosition)
    {
        PendingRelocation? pending = pendingRelocation;
        if (pending == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition destination))
        {
            SetStatus("No valid terrain position was found.");
            return true;
        }

        pendingRelocation = null;
        if (pending.Trailer == null || pending.Trailer.disabled || pending.Tractor == null || pending.Tractor.disabled)
        {
            SetStatus("The trailer or tractor is no longer available.");
            return true;
        }

        if (CommanderNetwork.Forward("emplace", CommanderNetwork.Id(pending.Trailer), CommanderNetwork.Position(destination)))
        {
            SetStatus("Relocating.");
            return true;
        }

        StartRelocation(pending.Trailer, pending.Tractor, destination);
        return true;
    }

    private static bool IsHauler(GroundVehicle vehicle)
    {
        string name = vehicle.definition?.unitName ?? vehicle.unitName ?? string.Empty;
        string key = vehicle.definition?.jsonKey ?? string.Empty;
        return name.EndsWith("Tractor", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Flatbed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "HLT-T", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Truck2-T", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "HLT-L", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Truck2-L", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdle(GroundVehicle vehicle)
    {
        if (Mathf.Abs(vehicle.speed) > 1.5f)
        {
            return false;
        }

        return !CommanderGameAccess.TryGetCurrentCommandPosition(vehicle, out GlobalPosition command)
            || CommanderGameAccess.HorizontalDistance(vehicle.transform.position, command.ToLocalPosition()) <= 20f;
    }

    private static bool HasArrived(GroundVehicle vehicle, GlobalPosition destination)
    {
        return CommanderGameAccess.HorizontalDistance(vehicle.transform.position, destination.ToLocalPosition()) <= ArrivalDistance
            && Mathf.Abs(vehicle.speed) <= 2f;
    }

    private static void RemoveTransportedTrailer(RelocationJob job)
    {
        GroundVehicle? trailer = job.Trailer;
        if (trailer == null)
        {
            return;
        }

        job.LastTrailerPosition = trailer.GlobalPosition();
        UnityEngine.Object.Destroy(trailer.gameObject);
        job.Trailer = null;
    }

    private static bool RestoreTrailer(RelocationJob job, GlobalPosition position)
    {
        if (job.Trailer != null && !job.Trailer.disabled)
        {
            return true;
        }

        if (!job.TrailerRemoved || job.Definition?.unitPrefab == null || job.Hq == null || NetworkSceneSingleton<Spawner>.i == null)
        {
            return !job.TrailerRemoved;
        }

        Quaternion rotation = Quaternion.Euler(0f, job.Tractor != null ? job.Tractor.transform.eulerAngles.y : job.OriginalRotation.eulerAngles.y, 0f);
        Vector3 requestedLocal = position.ToLocalPosition();
        Vector3 groundLocal = requestedLocal - (Vector3.up * job.Definition.spawnOffset.y);
        if (Physics.Raycast(
            requestedLocal + (Vector3.up * 250f),
            Vector3.down,
            out RaycastHit groundHit,
            1000f,
            8256,
            QueryTriggerInteraction.Ignore))
        {
            groundLocal = groundHit.point;
        }

        GroundVehicle restored = NetworkSceneSingleton<Spawner>.i.SpawnVehicle(
            job.Definition.unitPrefab,
            (groundLocal + (Vector3.up * job.Definition.spawnOffset.y)).ToGlobalPosition(),
            rotation,
            Vector3.zero,
            job.Hq,
            job.UniqueName,
            job.Skill,
            holdPosition: true,
            null);
        if (restored == null)
        {
            return false;
        }

        int stationCount = Mathf.Min(restored.weaponStations.Count, job.WeaponAmmo.Length);
        for (int i = 0; i < stationCount; i++)
        {
            WeaponStation station = restored.weaponStations[i];
            int[] savedAmmo = job.WeaponAmmo[i];
            int weaponCount = Mathf.Min(station.Weapons.Count, savedAmmo.Length);
            for (int weaponIndex = 0; weaponIndex < weaponCount; weaponIndex++)
            {
                Weapon weapon = station.Weapons[weaponIndex];
                if (weapon != null)
                {
                    weapon.ammo = Mathf.Clamp(savedAmmo[weaponIndex], 0, weapon.GetFullAmmo());
                }
            }

            station.AccountAmmo();
            station.Updated();
        }

        Rearmer[] restoredRearmers = restored.GetComponentsInChildren<Rearmer>(true);
        int rearmerCount = Mathf.Min(restoredRearmers.Length, job.RearmerCapacities.Length);
        for (int i = 0; i < rearmerCount; i++)
        {
            Rearmer rearmer = restoredRearmers[i];
            if (rearmer != null)
            {
                rearmer.SetCapacity(Mathf.Clamp(job.RearmerCapacities[i], 0f, rearmer.GetMaxCapacity()));
            }
        }

        Radar[] radars = restored.GetComponentsInChildren<Radar>();
        for (int i = 0; i < radars.Length; i++)
        {
            radars[i].activated = job.RadarOnline;
            radars[i].enabled = job.RadarOnline;
        }

        job.Trailer = restored;
        job.TrailerRemoved = false;
        job.LastTrailerPosition = restored.GlobalPosition();
        return true;
    }

    private bool UpdateJob(RelocationJob job)
    {
        if (job.Tractor == null || job.Tractor.disabled)
        {
            RestoreTrailer(job, job.GetSafeRestorePosition());
            SetStatus("Relocation failed: tractor lost. The trailer was restored.");
            return true;
        }

        switch (job.Stage)
        {
            case RelocationStage.ApproachingPickup:
                if (job.Trailer == null || job.Trailer.disabled)
                {
                    SetStatus("Relocation cancelled: trailer unavailable.");
                    return true;
                }

                if (HasArrived(job.Tractor, job.PickupStagingPoint))
                {
                    job.Stage = RelocationStage.Loading;
                    job.StageCompleteAt = Time.unscaledTime + HandlingSeconds;
                    IssueHaulerDestination(job.Tractor, job.Tractor.GlobalPosition());
                    SetStatus("Loading trailer (10 seconds).");
                }

                break;

            case RelocationStage.Loading:
                if (job.Trailer == null || job.Trailer.disabled)
                {
                    SetStatus("Relocation cancelled: trailer unavailable.");
                    return true;
                }

                if (Time.unscaledTime < job.StageCompleteAt)
                {
                    break;
                }

                job.CaptureTrailerState();
                RemoveTransportedTrailer(job);
                job.Stage = RelocationStage.Transporting;
                IssueHaulerDestination(job.Tractor, job.DeliveryStagingPoint);
                SetStatus("Trailer loaded. Tractor moving to destination.");
                break;

            case RelocationStage.Transporting:
                if (HasArrived(job.Tractor, job.DeliveryStagingPoint))
                {
                    job.Stage = RelocationStage.Unloading;
                    job.StageCompleteAt = Time.unscaledTime + HandlingSeconds;
                    IssueHaulerDestination(job.Tractor, job.Tractor.GlobalPosition());
                    SetStatus("Deploying trailer (10 seconds).");
                }

                break;

            case RelocationStage.Unloading:
                if (Time.unscaledTime < job.StageCompleteAt)
                {
                    break;
                }

                if (!RestoreTrailer(job, job.Destination))
                {
                    SetStatus("Trailer deployment failed; it will be restored when the order is cancelled.");
                    return false;
                }

                Vector3 clearDirection = job.Tractor.transform.forward;
                clearDirection.y = 0f;
                if (clearDirection.sqrMagnitude < 0.1f)
                {
                    clearDirection = Vector3.forward;
                }

                CommanderGameAccess.SetUnitHoldPosition(job.Tractor, hold: false);
                IssueHaulerDestination(
                    job.Tractor,
                    (job.Tractor.transform.position + (clearDirection.normalized * 25f)).ToGlobalPosition());
                SetStatus("Trailer deployed. Tractor released to Basegame AI.");
                return true;
        }

        return false;
    }

    private void StartRelocation(GroundVehicle trailer, GroundVehicle tractor, GlobalPosition destination)
    {
        RelocationJob job = new(trailer, tractor, destination);
        jobs.Add(job);
        reservedHaulers.Add(tractor);
        CommanderGameAccess.SetUnitHoldPosition(tractor, hold: true);
        IssueHaulerDestination(tractor, job.PickupStagingPoint);
        SetStatus($"{CommanderGameAccess.GetUnitLabel(tractor)} dispatched to load the trailer.");
    }

    /// <summary>A guest's relocation click: the host picks the tractor, as the guest's own window did.</summary>
    private void HostRelocate(FactionHQ hq, string[] args)
    {
        string hostStatus = statusText;
        float hostUntil = statusUntil;
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit)
            || unit is not GroundVehicle trailer
            || !ReferenceEquals(trailer.NetworkHQ, hq)
            || !IsMoveableTrailer(trailer)
            || !CommanderNetwork.TryPosition(args, 2, out GlobalPosition destination))
        {
            return;
        }

        if (IsRelocating(trailer))
        {
            CommanderNotice.Fail("This trailer already has a relocation order.");
        }
        else if (!TryFindTractor(trailer, out GroundVehicle tractor))
        {
            CommanderNotice.Fail("Requires an idle HLT/MSV Tractor or Flatbed within 300 m.");
        }
        else
        {
            StartRelocation(trailer, tractor, destination);
        }

        statusText = hostStatus;
        statusUntil = hostUntil;
    }

    private bool TryFindTractor(GroundVehicle trailer, out GroundVehicle tractor)
    {
        tractor = null!;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return false;
        }

        float tractorDistance = float.MaxValue;
        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit)
                || unit is not GroundVehicle vehicle
                || vehicle.disabled
                || reservedHaulers.Contains(vehicle)
                || !IsHauler(vehicle)
                || !IsIdle(vehicle))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(vehicle.transform.position, trailer.transform.position);
            if (distance <= TractorRange && distance < tractorDistance)
            {
                tractorDistance = distance;
                tractor = vehicle;
            }
        }

        return tractor != null;
    }

    private void IssueHaulerDestination(GroundVehicle hauler, GlobalPosition destination)
    {
        issuingInternalHaulerCommand = true;
        try
        {
            hauler.UnitCommand?.SetDestination(destination, playerCommand: false);
        }
        finally
        {
            issuingInternalHaulerCommand = false;
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    private sealed class PendingRelocation
    {
        internal PendingRelocation(GroundVehicle trailer, GroundVehicle tractor)
        {
            Trailer = trailer;
            Tractor = tractor;
        }

        internal GroundVehicle Trailer { get; }

        internal GroundVehicle Tractor { get; }
    }

    private sealed class RelocationJob
    {
        internal RelocationJob(GroundVehicle trailer, GroundVehicle tractor, GlobalPosition destination)
        {
            Trailer = trailer;
            Tractor = tractor;
            Destination = destination;
            LastTrailerPosition = trailer.GlobalPosition();
            OriginalRotation = trailer.transform.rotation;
            Hq = trailer.NetworkHQ;
            Definition = trailer.definition as VehicleDefinition;
            UniqueName = trailer.UniqueName;
            Skill = trailer.skill;

            Vector3 pickupDirection = tractor.transform.position - trailer.transform.position;
            pickupDirection.y = 0f;
            if (pickupDirection.sqrMagnitude < 0.1f)
            {
                pickupDirection = trailer.transform.forward;
            }

            PickupStagingPoint = (trailer.transform.position + (pickupDirection.normalized * StagingDistance)).ToGlobalPosition();

            Vector3 destinationLocal = destination.ToLocalPosition();
            Vector3 deliveryDirection = tractor.transform.position - destinationLocal;
            deliveryDirection.y = 0f;
            if (deliveryDirection.sqrMagnitude < 0.1f)
            {
                deliveryDirection = -tractor.transform.forward;
            }

            DeliveryStagingPoint = (destinationLocal + (deliveryDirection.normalized * StagingDistance)).ToGlobalPosition();
        }

        internal GroundVehicle? Trailer { get; set; }

        internal GroundVehicle Tractor { get; }

        internal GlobalPosition Destination { get; }

        internal GlobalPosition PickupStagingPoint { get; }

        internal GlobalPosition DeliveryStagingPoint { get; }

        internal GlobalPosition LastTrailerPosition { get; set; }

        internal Quaternion OriginalRotation { get; }

        internal FactionHQ? Hq { get; }

        internal VehicleDefinition? Definition { get; }

        internal string UniqueName { get; }

        internal float Skill { get; }

        internal RelocationStage Stage { get; set; }

        internal float StageCompleteAt { get; set; }

        internal int[][] WeaponAmmo { get; private set; } = Array.Empty<int[]>();

        internal float[] RearmerCapacities { get; private set; } = Array.Empty<float>();

        internal bool RadarOnline { get; private set; } = true;

        internal bool TrailerRemoved { get; set; }

        internal void CaptureTrailerState()
        {
            if (Trailer == null)
            {
                return;
            }

            WeaponAmmo = new int[Trailer.weaponStations.Count][];
            for (int i = 0; i < WeaponAmmo.Length; i++)
            {
                WeaponStation station = Trailer.weaponStations[i];
                WeaponAmmo[i] = new int[station.Weapons.Count];
                for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
                {
                    Weapon weapon = station.Weapons[weaponIndex];
                    WeaponAmmo[i][weaponIndex] = weapon?.ammo ?? 0;
                }
            }

            Rearmer[] rearmers = Trailer.GetComponentsInChildren<Rearmer>(true);
            RearmerCapacities = new float[rearmers.Length];
            for (int i = 0; i < rearmers.Length; i++)
            {
                RearmerCapacities[i] = rearmers[i]?.Capacity ?? 0f;
            }

            Radar[] radars = Trailer.GetComponentsInChildren<Radar>();
            RadarOnline = radars.Length == 0;
            for (int i = 0; i < radars.Length; i++)
            {
                RadarOnline |= radars[i] != null && radars[i].activated;
            }

            TrailerRemoved = true;
        }

        internal GlobalPosition GetSafeRestorePosition()
        {
            if (Tractor != null && !Tractor.disabled && TrailerRemoved)
            {
                return Tractor.GlobalPosition();
            }

            return LastTrailerPosition;
        }
    }
}
