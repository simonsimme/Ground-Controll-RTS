using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Everything about a cargo run that happens <em>after</em> it has launched: moving the landing
/// zone, switching between a touchdown and a fly-by drop, routing the aircraft via a travel point,
/// and flying the fixed-wing half of the feature, which has no <c>AIHeloTransportState</c> to hook.
/// </summary>
internal sealed partial class CommanderSupplyHeliService
{
    /// <summary>
    /// How close a fixed-wing transport has to be to the LZ before the bays open. Scaled up with
    /// airspeed, because the check runs four times a second and a fast transport crosses a fixed
    /// ring between two of them — a run that overflew its zone and did not drop is the failure this
    /// margin exists to stop.
    /// </summary>
    private const float FixedWingDropRadiusMeters = 500f;

    /// <summary>Inside this the aeroplane is over the zone and drops without waiting for the
    /// distance to start growing — a run that is already on top of the point.</summary>
    private const float FixedWingDropOverheadMeters = 120f;

    /// <summary>
    /// How far ahead of the zone, in seconds of flight, a helicopter releases its first load. The
    /// base game used four seconds (about 240 m), which let go well before the aircraft reached
    /// the click. A parachuted load still carries some way forward, so this is a lead, not zero;
    /// tune it from the "came to rest ... from its LZ" log lines.
    /// </summary>
    private const float RotaryAirdropLeadSeconds = 1.5f;

    /// <summary>Spacing between loads, so a stack does not leave the aircraft as one lump.</summary>
    private const float FixedWingDropIntervalSeconds = 1.5f;

    /// <summary>How long a freshly launched run may show empty pylons before it counts as finished.</summary>
    private const float CargoLoadGraceSeconds = 20f;

    /// <summary>
    /// The fixed-wing delivery. There is no engine behaviour to borrow here — <c>AIHeloTransportState</c>
    /// is rotary only — so the whole run is "fly the route, and when the LZ is under you, fire the
    /// cargo stations". <c>Weapon.Fire</c> on a cargo pylon is exactly what the rotary path already
    /// uses to release a load, so the cargo, its parachute and its arrival on the ground are all the
    /// base game's.
    /// </summary>
    private readonly System.Collections.Generic.List<Aircraft> completedFixedWingRuns = new();

    private Aircraft? pendingLzAircraft;
    private float nextFixedWingDropAt;
    private float nextFixedWingTickAt;
    private float nextLaunchLogAt;

    /// <summary>True while the player has SET LZ armed and is about to click a new drop point.</summary>
    internal bool AwaitingLzSelection => pendingLzAircraft != null && !pendingLzAircraft.disabled;

    /// <summary>
    /// True for any AI aircraft of ours with cargo aboard, on a run or not: SET LZ starts a run for
    /// one that has none, such as a transport a capture order took off its run. Not during a sling
    /// lift, which flies the helicopter itself and has its own SET LZ.
    /// </summary>
    internal static bool CanSetLz(Unit? unit)
    {
        return unit is Aircraft aircraft
            && CommanderAirCommandService.IsTaskableAircraft(aircraft)
            && HasDeployableCargo(aircraft)
            && CommanderSlingLiftService.Instance?.IsLifting(aircraft) != true;
    }

    /// <summary>False for an airframe that has no way to land at an LZ, i.e. anything not rotary.</summary>
    internal static bool CargoRunCanLand(Unit? unit)
    {
        return unit is Aircraft aircraft && CommanderAirCommandService.IsRotaryAirframe(aircraft.definition);
    }

    /// <summary>
    /// True when this unit is an aircraft on a commander cargo run that still has something to
    /// deliver. A transport that has emptied its bays is deliberately no longer one: its mission
    /// record outlives the delivery, and while it counted the aircraft could never be given an
    /// ordinary order again.
    /// </summary>
    internal bool IsCargoRunAircraft(Unit? unit)
    {
        return unit is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            && !mission.Cancelled
            && HasDeployableCargo(aircraft);
    }

    internal bool CargoRunIsAirdrop(Unit? unit)
    {
        return unit is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            && mission.Airdrop;
    }

    /// <summary>One line for the selection bar: what is aboard and where it is going.</summary>
    internal string GetCargoRunLabel(Unit? unit)
    {
        if (unit is not Aircraft aircraft)
        {
            return string.Empty;
        }

        int loads = CountDeployableCargo(aircraft);
        if (!IsCargoRunAircraft(aircraft) || !assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return $"CARGO   {loads} load(s) aboard   |   SET LZ to deliver";
        }

        string delivery = mission.Airdrop ? "FLY-BY DROP" : "LANDING";
        float distance = CommanderGameAccess.HorizontalDistance(
            aircraft.transform.position, mission.Target.ToLocalPosition());
        return $"CARGO RUN   {mission.CargoLabel}   |   {loads} load(s)   |   {delivery}   |   LZ {distance / 1000f:0.0} km";
    }

    /// <summary>
    /// Land or fly by. A helicopter can do either; a fixed-wing transport has no way to put itself
    /// down on open ground, so the toggle is refused rather than silently ignored.
    /// </summary>
    internal void ToggleCargoRunDelivery(Unit? unit)
    {
        if (unit is not Aircraft aircraft || !assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return;
        }

        if (mission.FixedWing)
        {
            SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} has no rotary pilot and can only drop from the air.");
            return;
        }

        mission.Airdrop = !mission.Airdrop;
        RestartCargoApproach(mission);
        SetStatus(mission.Airdrop
            ? $"{CommanderGameAccess.GetUnitLabel(aircraft)} will drop its load over the LZ."
            : $"{CommanderGameAccess.GetUnitLabel(aircraft)} will land at the LZ.");
    }

    /// <summary>Arms the next world click as this aircraft's new landing zone.</summary>
    internal void BeginLzRetarget(Unit? unit)
    {
        if (unit is not Aircraft aircraft || !CanSetLz(aircraft))
        {
            return;
        }

        CancelTargetSelection(showStatus: false);
        pendingLzAircraft = aircraft;
        SetStatus($"Click the new landing zone for {CommanderGameAccess.GetUnitLabel(aircraft)}.");
    }

    internal void CancelLzRetarget()
    {
        if (pendingLzAircraft == null)
        {
            return;
        }

        pendingLzAircraft = null;
        SetStatus("Landing zone change cancelled.");
    }

    /// <summary>
    /// A travel point given to a cargo aircraft. It becomes a leg on the way to the LZ rather than a
    /// destination of its own, so the delivery is never forgotten.
    /// </summary>
    internal bool TryAddCargoRunWaypoint(Aircraft aircraft, GlobalPosition point, bool append)
    {
        if (!IsCargoRunAircraft(aircraft) || !assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return false;
        }

        // A fixed-wing run is steered by handing the route to Air Command. If Air Command will not
        // take this airframe there is nothing here that can fly it, so the order must fall through
        // to the ordinary path rather than being filed on a route nothing reads — swallowing it is
        // what made a move order on a stuck transport do nothing at all.
        if (mission.FixedWing && !CommanderAirCommandService.IsTaskableAircraft(aircraft))
        {
            return false;
        }

        if (!append)
        {
            mission.ApproachRoute.Clear();
            mission.ApproachRouteIndex = 0;
        }

        mission.ApproachRoute.Add(point);
        mission.RoutePlanned = true;
        mission.LastTransportOverrideFixedTime = float.NegativeInfinity;
        if (mission.FixedWing)
        {
            SteerFixedWingToLz(aircraft, mission);
        }

        SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} routed via a travel point on the way to its LZ.");
        return true;
    }

    /// <summary>
    /// Takes an aircraft off its cargo run, cargo still aboard, until SET LZ puts it back on, so an
    /// order that is not a leg on the way to the LZ (a capture landing) can have it. Filed as a route leg instead, the
    /// helicopter flew over the base it was sent to take and carried on to its LZ. An emptied run
    /// comes here too: it is already flying home in the landing state, which nothing else leaves.
    /// </summary>
    internal void EndCargoRun(Aircraft aircraft)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return;
        }

        // Not cancelled once empty: a load released a moment ago still has to activate, and
        // HoldDeployedCargo destroys whatever a cancelled run drops.
        if (HasDeployableCargo(aircraft))
        {
            CancelMission(aircraft, mission);
        }

        CloseCargoDoors(mission);

        // Left in AIHeloTransportState with the override off, the base game flies its own delivery
        // to whatever it can see. The combat state is the one Air Command steers a helicopter from.
        if (aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && aircraft.pilots[0] != null
            && aircraft.pilots[0].currentState is AIHeloTransportState or AIHeloLandingState)
        {
            CommanderAirCommandService.ReturnToCombatState(aircraft);
        }

        CommanderPlugin.Log.LogInfo($"{CommanderGameAccess.GetUnitLabel(aircraft)} taken off its cargo run by a new order.");
    }

    private static void RestartCargoApproach(CargoMission mission)
    {
        mission.TargetOverrideActive = true;
        mission.ApproachRoute.Clear();
        mission.ApproachRouteIndex = 0;
        mission.RoutePlanned = false;
        mission.RouteTransitActive = false;
        mission.FoundationLandingSearchInitialized = false;
        mission.PlatformArrivalInitialized = false;
        mission.PlatformArrivalReached = false;
        mission.PlatformApproachComplete = false;
        mission.DeliveryCompleted = false;
        mission.DropRunComplete = false;
        mission.LandingUnloadAt = 0f;
        mission.VerticalDepartureActive = false;
        mission.LastLzDistance = float.PositiveInfinity;

        // Forces the next AIHeloTransportState pass to recompute rather than reuse its 3 s cache.
        mission.LastTransportOverrideFixedTime = float.NegativeInfinity;
    }

    /// <summary>
    /// Hands a fixed-wing transport to Air Command as an ordinary travel order: the approach route
    /// followed by the LZ. That gives it the same steering, yellow route line and map pin every
    /// other commanded aeroplane has, and none of it needs a rotary flight state.
    /// </summary>
    private static void SteerFixedWingToLz(Aircraft aircraft, CargoMission mission)
    {
        // A guest's transport is only taskable while the guest's faction is the one acting.
        if (!ReferenceEquals(mission.Hq, CommanderGameAccess.GetLocalHq()))
        {
            CommanderNetwork.RunAs(mission.Hq, () => SteerFixedWingToLz(aircraft, mission));
            return;
        }

        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null || !CommanderAirCommandService.IsTaskableAircraft(aircraft))
        {
            CommanderPlugin.Log.LogWarning(
                $"Air Command will not take {CommanderGameAccess.GetUnitLabel(aircraft)} for its cargo run, "
                    + "so nothing is steering it. The launch watchdog will lift it clear; it will still "
                    + "drop on the zone if it gets there.");
            return;
        }

        for (int i = mission.ApproachRouteIndex; i < mission.ApproachRoute.Count; i++)
        {
            airCommand.SetAircraftOrder(aircraft, mission.ApproachRoute[i], null, append: i > mission.ApproachRouteIndex);
        }

        airCommand.SetAircraftOrder(
            aircraft, mission.Target, null, append: mission.ApproachRouteIndex < mission.ApproachRoute.Count);
    }

    /// <summary>
    /// Replaces the delivery point and throws away everything the old one had worked out — the
    /// approach route, the touchdown search, the platform arrival — so the next override pass plans
    /// the new leg from scratch instead of finishing the old approach at a new coordinate.
    /// </summary>
    private static void SetCargoRunTarget(Aircraft aircraft, CargoMission mission, GlobalPosition target)
    {
        if (mission.DeliveryTargets.Count == 0)
        {
            mission.DeliveryTargets.Add(target);
        }
        else
        {
            mission.DeliveryTargets[0] = target;
        }

        RestartCargoApproach(mission);
        if (mission.FixedWing)
        {
            SteerFixedWingToLz(aircraft, mission);
        }
    }

    /// <summary>Completes an armed SET LZ. Returns false when nothing was armed.</summary>
    private bool TryCompleteLzRetarget(Vector2 screenPosition)
    {
        Aircraft? aircraft = pendingLzAircraft;
        if (aircraft == null)
        {
            // Unity-null once the airframe is destroyed, so the stale reference is dropped here.
            pendingLzAircraft = null;
            return false;
        }

        if (!CanSetLz(aircraft))
        {
            pendingLzAircraft = null;
            SetStatus("That aircraft has nothing left to deliver.");
            return true;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid terrain point was found. Click visible terrain or a surface.");
            return true;
        }

        pendingLzAircraft = null;
        ApplyLz(aircraft, target);
        return true;
    }

    /// <summary>
    /// The LZ for an aircraft with cargo aboard, whatever it was doing. A run cancelled by a capture
    /// order resumes, and an aircraft that never had a run (launched from AIR COMMAND) gets one.
    /// Cancelled is cleared first: <c>ShouldRefuseTransportState</c> reads it the moment the Air
    /// Command order is dropped and the pilot goes back to the combat state.
    /// </summary>
    private void ApplyLz(Aircraft aircraft, GlobalPosition target)
    {
        if (CommanderNetwork.Forward("cargo.lz", CommanderNetwork.Id(aircraft), CommanderNetwork.Position(target)))
        {
            return;
        }

        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            Airbase? home = CommanderAirCommandService.FindNearestOwnedAirbase(aircraft);
            if (home == null)
            {
                CommanderNotice.Fail($"{CommanderGameAccess.GetUnitLabel(aircraft)} has no friendly field to fly a cargo run from.");
                return;
            }

            bool fixedWing = !CommanderAirCommandService.IsRotaryAirframe(aircraft.definition);
            mission = new CargoMission(
                aircraft.NetworkHQ,
                "Cargo",
                highTerrainClearance: false,
                terrainClearanceMeters: 0f,
                airdrop: fixedWing,
                purchasedWithFunds: false,
                purchaseCost: 0f,
                CountDeployableCargo(aircraft),
                new[] { target },
                depositAtSamCore: false,
                depositSiteId: -1,
                foundationSiteId: -1,
                jacknifeSiteId: -1,
                home)
            {
                AssignedAt = Time.timeSinceLevelLoad,
                FixedWing = fixedWing,
            };
            assignedMissions[aircraft] = mission;
        }
        else if (mission.Cancelled)
        {
            mission.Cancelled = false;
            if (mission.HighTerrainClearance && !TryBindTerrainAutopilot(aircraft, mission))
            {
                pendingTerrainAutopilotBindings.Add(aircraft);
            }
        }

        CommanderAirCommandService.Instance?.ReleaseForCargoRun(aircraft);
        SetCargoRunTarget(aircraft, mission, target);
        CommanderSelectionService.PinMissionUnit(
            aircraft, "SUPPLY", mission.Airdrop ? $"Airdrop: {mission.CargoLabel}" : $"Cargo Delivery: {mission.CargoLabel}");
        if (!CommanderNetwork.IsHandlingGuest)
        {
            SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} landing zone set.");
        }
    }

    private void HostSetLz(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && unit is Aircraft aircraft
            && ReferenceEquals(aircraft.NetworkHQ, hq)
            && CanSetLz(aircraft)
            && CommanderNetwork.TryPosition(args, 2, out GlobalPosition target))
        {
            ApplyLz(aircraft, target);
        }
    }

    private void TickFixedWingDrops()
    {
        if (assignedMissions.Count == 0
            || Time.timeSinceLevelLoad < nextFixedWingDropAt
            || !CommanderScheduler.IsDue(ref nextFixedWingTickAt, 0.25f))
        {
            return;
        }

        foreach (System.Collections.Generic.KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            Aircraft aircraft = entry.Key;
            CargoMission mission = entry.Value;
            if (aircraft == null
                || aircraft.disabled
                || !mission.FixedWing
                || mission.Cancelled
                || mission.DropRunComplete)
            {
                continue;
            }

            if (!TrySelectNextCargoWeapon(aircraft, out WeaponStation station, out Weapon cargoWeapon))
            {
                // An empty rack this early is the loadout still being built, not a finished run:
                // WeaponManager spawns the stores asynchronously, so for the first seconds after a
                // launch every cargo pylon reads empty. Concluding "delivered" here would delete the
                // mission before the aeroplane had ever carried anything.
                if (mission.ReleasedCargoCount == 0
                    && Time.timeSinceLevelLoad - mission.AssignedAt < CargoLoadGraceSeconds)
                {
                    continue;
                }

                // The run is over, so the record goes with it: an aeroplane still listed here is one
                // whose move orders this service keeps stealing.
                mission.DropRunComplete = true;
                mission.DeliveryCompleted = true;
                CloseCargoDoors(mission);
                CommanderSelectionService.PinMissionUnit(aircraft, "SUPPLY", "Cargo delivered");
                completedFixedWingRuns.Add(aircraft);
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderGameAccess.GetUnitLabel(aircraft)} finished its cargo run with "
                        + $"{mission.ReleasedCargoCount} load(s) dropped; it is an ordinary aircraft again.");
                continue;
            }

            if (mission.ExpectedCargoLoads == 0)
            {
                mission.ExpectedCargoLoads = CountDeployableCargo(aircraft);
            }

            float dropRadius = Mathf.Max(FixedWingDropRadiusMeters, aircraft.speed * 3f);
            float distance = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position, mission.Target.ToLocalPosition());
            float previousDistance = mission.LastLzDistance;
            mission.LastLzDistance = distance;
            if (distance > dropRadius)
            {
                continue;
            }

            // Release over the zone, not on the way to it: the outer ring is half a kilometre wide on a fast
            // transport. Release at closest approach (the tick where distance stops falling); the small ring is
            // the backstop for a run already on top of the zone, and a started stick keeps dropping on its own
            // interval so the loads stay together.
            if (distance > FixedWingDropOverheadMeters
                && mission.ReleasedCargoCount == 0
                && distance < previousDistance)
            {
                continue;
            }

            OpenCargoDoors(aircraft, mission);
            aircraft.weaponManager.currentWeaponStation = station;
            cargoWeapon.Fire(aircraft, null!, aircraft.rb.velocity, station, default);
            station.UpdateLastFired(1);
            mission.ReleasedCargoCount++;
            mission.LastCargoReleasedAt = Time.timeSinceLevelLoad;
            nextFixedWingDropAt = Time.timeSinceLevelLoad + FixedWingDropIntervalSeconds;
            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} dropped load {mission.ReleasedCargoCount} "
                    + $"of {mission.ExpectedCargoLoads} on its LZ from {aircraft.radarAlt:0} m "
                    + $"at {aircraft.speed:0} m/s.");
            break;
        }

        for (int i = 0; i < completedFixedWingRuns.Count; i++)
        {
            assignedMissions.Remove(completedFixedWingRuns[i]);
        }

        completedFixedWingRuns.Clear();
    }

    /// <summary>
    /// A rotary run that never leaves its pad logs nothing after "Cargo run assigned", so an
    /// airdrop that sat on a helipad for two minutes left no trace at all. The first minute of
    /// every rotary run is written once a second until it is 60 m up.
    /// </summary>
    private void LogRotaryLaunches()
    {
        if (assignedMissions.Count == 0 || !CommanderScheduler.IsDue(ref nextLaunchLogAt, 1f))
        {
            return;
        }

        foreach (System.Collections.Generic.KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            Aircraft aircraft = entry.Key;
            CargoMission mission = entry.Value;
            if (aircraft == null || aircraft.disabled || mission.FixedWing || mission.LaunchLogDone)
            {
                continue;
            }

            float age = Time.timeSinceLevelLoad - mission.AssignedAt;
            if (aircraft.radarAlt > 60f || age > 60f)
            {
                mission.LaunchLogDone = true;
            }

            Pilot? pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
            ControlInputs inputs = aircraft.GetInputs();
            CommanderPlugin.Log.LogInfo(
                $"Cargo launch: {CommanderGameAccess.GetUnitLabel(aircraft)} #{aircraft.persistentID.Id} +{age:0} s, "
                    + $"{(mission.Airdrop ? "airdrop" : "landing")}, state {pilot?.currentState?.GetType().Name ?? "none"}, "
                    + $"height {aircraft.radarAlt:0.0} m, speed {aircraft.speed:0.0} m/s, climb {aircraft.rb.velocity.y:0.0} m/s, "
                    + $"throttle {inputs.throttle:0.00}, brake {inputs.brake:0.00}, gear {(aircraft.gearDeployed ? "down" : "up")}, "
                    + $"override {(mission.TargetOverrideActive ? "on" : "off")}, cargo {CountDeployableCargo(aircraft)}.");
        }
    }

    private void CancelMission(Aircraft? aircraft, CargoMission mission)
    {
        mission.TargetOverrideActive = false;
        mission.Cancelled = true;
        mission.CargoClearancePending = false;
        if (aircraft == null)
        {
            return;
        }

        if (aircraft.autopilot != null)
        {
            terrainClearanceAutopilots.Remove(aircraft.autopilot);
            assignedAutopilotAircraft.Remove(aircraft.autopilot);
        }

        pendingTerrainAutopilotBindings.Remove(aircraft);
    }
}
