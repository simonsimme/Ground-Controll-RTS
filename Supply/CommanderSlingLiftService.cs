using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// SLING LIFT: a helicopter carrying the cargo hook (the Ibis and Tarantula "Sling load Hook"
/// loadout option) picks up a friendly ground vehicle straight away, holds it, and sets it down at
/// the LZ the player gives it (SET LZ, or a right-click) whenever that comes. It exists so a builder
/// can reach a blueprint it cannot drive to, a naval dock on a cut-off shore above all, but it lifts
/// anything the game allows on a hook.
/// </summary>
/// <remarks>
/// The base game has no AI for the hook: only the player's own HUD ever drives it. So a lifting
/// helicopter is flown here, by skipping <c>AIHeloCombatState.FixedUpdateState</c> and calling the
/// same <c>Autopilot.AutoAim</c> / <c>Hover</c> pair <c>AIHeloTransportState</c> uses to reach and
/// hold a landing point. Attach and release are <c>Aircraft.SetSlingLoadAttachment</c>, the call the
/// hook makes itself. The attach is only made where the hook's own <c>DeployedState</c> would make
/// it (rope in reach, speeds matched), because the rope is a 20 m joint and connecting from further
/// out yanks the load.
/// <para>
/// The lift owns the helicopter until it is done. Clicks given to it meanwhile are plain
/// Air Command travel points (<c>CommanderMoveService.IssueAircraftOrder</c> skips the landing and
/// capture readings for it), flown here through <c>TryGetNextRoutePoint</c>; an attack order is
/// read as a travel point to the target. A base-game switch to another pilot state (low fuel, say)
/// is undone every tick. CANCEL is the way out.
/// </para>
/// </remarks>
internal sealed class CommanderSlingLiftService : ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Inside this the helicopter stops cruising and hovers, as AIHeloTransportState does.</summary>
    private const float HoverRadiusMeters = 300f;

    private const float EmptyCruiseHeight = 40f;

    /// <summary>Clearance under a hanging load, on top of the rope and the vehicle.</summary>
    private const float CarryMarginMeters = 25f;

    /// <summary>Hover height over the vehicle's base for the pickup: low enough for the rope to reach.</summary>
    private const float PickupHoverHeight = 10f;

    /// <summary>Winch to hook point, inside the 20 m rope with a margin. Measured as a straight line,
    /// so a helicopter a little off to one side still hooks.</summary>
    private const float HookReachMeters = 16f;

    /// <summary>How far inside the rope's full length a vehicle is spawned on it (<see cref="SpawnOnHook"/>).
    /// Hung at the rope's length the rope takes the weight at once; a drop onto a slack rope jolts past
    /// the hook's break force.</summary>
    private const float SpawnRopeSlackMeters = 0.5f;

    /// <summary>Least radar altitude to spawn a vehicle under the hook: rope, vehicle and a margin.</summary>
    private const float SpawnUnderHookMinAltitude = 30f;

    /// <summary>Height over the ground or sea a helicopter holds while a load is spawned under it.</summary>
    private const float SpawnHoverHeight = 45f;

    /// <summary>The hook's own attach rule is speeds within 8 m/s; spawned at most this fast, the
    /// load starts out hanging, not swinging.</summary>
    private const float SpawnMaxSpeed = 5f;

    private const float SpawnHoldTimeoutSeconds = 90f;

    /// <summary>How far short of the drop a spawn lift stops to take its load on.</summary>
    private const float SpawnNearDropMeters = 1000f;

    /// <summary>How long a load spawned on the hook is watched for the base game's airdrop
    /// parachute, which opens 0.5 s after the vehicle's client start.</summary>
    private const float AirdropChuteCheckSeconds = 5f;

    /// <summary>The hook's own attach rule is speeds within 8 m/s of each other.</summary>
    private const float MatchedSpeedSquared = 8f * 8f;

    /// <summary>A load below this is on the ground; above <see cref="LoadClearMeters"/> it hangs
    /// clear. Two thresholds, so the helicopter does not flicker between climbing and flying on.</summary>
    private const float LoadGroundedMeters = 2f;

    private const float LoadClearMeters = 5f;

    /// <summary>Within this of the LZ the helicopter stops and lowers the load.</summary>
    private const float LoweringRadiusMeters = 15f;

    /// <summary>A travel point counts as reached this close, horizontally. Air Command's own 1500 m
    /// is an aeroplane's; a helicopter carrying a load is meant to go where it was pointed.</summary>
    private const float RouteArrivalMeters = 60f;

    private const float LoweringSpeed = 3f;

    private const float MinimumHoverHeight = 6f;

    /// <summary>An unhooked vehicle is handed a 100 G ImpactDetector, so it is only let go once it is
    /// on the ground.</summary>
    private const float GroundedLoadAltitude = 1.5f;

    private const float PickupTimeoutSeconds = 180f;

    /// <summary>How long a helicopter may try to get a hooked load off the ground before it puts it
    /// back: it is too heavy for this airframe, and the alternative is dragging it.</summary>
    private const float LiftOffTimeoutSeconds = 30f;

    /// <summary>How long a lift may sit at full collective without climbing before it lets go.</summary>
    private const float StrainReleaseSeconds = 6f;

    private const float BuilderResumeDelaySeconds = 5f;

    /// <summary>A loaded helicopter that neither closes on the LZ nor climbs over this long cannot
    /// carry the load, and sets it down rather than hang in the air.</summary>
    private const float StallSeconds = 20f;

    /// <summary>How far above anything standing on its path an empty helicopter stays on the way in.</summary>
    private const float HelicopterClearanceMeters = 15f;

    /// <summary>The least height an empty helicopter comes in at over the last 300 m to a pickup.</summary>
    private const float ApproachHeight = 30f;

    /// <summary>Only this close to the vehicle does it come straight down to hook it.</summary>
    private const float PickupDescendMeters = 20f;

    /// <summary>Speed a loaded helicopter is held to. The hover autopilot has no speed limit of its own,
    /// and a fast load swings until it is lost.</summary>
    private const float CarrySpeed = 32f;

    /// <summary>How fast the height target drops when the ground falls away, m/s. Climbs happen at once;
    /// sinking slowly glides over dips instead of tracing every fold.</summary>
    private const float SinkRate = 1.5f;

    /// <summary>The obstacle scan covers this many seconds of travel ahead, and never less than
    /// <see cref="MinLookAheadMeters"/>. At a fixed 160 m a hill came into view ten seconds before the
    /// helicopter had to be over it, too late to climb 30 m with 6 t on the hook.</summary>
    private const float LookAheadSeconds = 12f;

    private const float MinLookAheadMeters = 200f;

    /// <summary>More than this far under its height target, a loaded helicopter starts to slow down,
    /// reaching <see cref="MinSpeedScale"/> of <see cref="CarrySpeed"/> this many metres further on:
    /// it is climbing as hard as it can, and flying on at full speed is flying into the hill.</summary>
    private const float ClimbSlowdownStart = 10f;

    private const float ClimbSlowdownRange = 30f;

    private const float MinSpeedScale = 0.35f;

    /// <summary>Seconds of speed error turned into hover setpoint offset: the gain of the speed hold.</summary>
    private const float VelocityGainSeconds = 2f;

    /// <summary>On the way in the wanted speed is at most this fraction of the distance left, per
    /// second, so it slows down instead of arriving at full speed.</summary>
    private const float ApproachSlowdown = 0.25f;

    /// <summary>Inside this it holds position over the point instead of holding a speed.</summary>
    private const float PositionHoldMeters = 40f;

    /// <summary>
    /// How far above or below its current height a loaded helicopter's height target may be. The
    /// hover's height term saturates the collective at a few metres of error; a target a hundred
    /// metres up yanked a 6 t load off the ground hard enough to part the rope the moment it hooked.
    /// </summary>
    private const float MaxAltitudeLeadMeters = 6f;

    private const float ObstacleScanSeconds = 0.2f;

    /// <summary>Radius of the downward probes, so a tower a few metres off the line still counts.</summary>
    private const float ObstacleScanRadius = 8f;

    /// <summary>Where along the look-ahead the probes go, as fractions of it.</summary>
    private static readonly float[] ScanFractions = { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1f };

    private static readonly FieldInfo? StateAircraftField = AccessTools.Field(typeof(PilotBaseState), "aircraft");

    /// <summary>Airframes already reported as keeping a rope the hook would have parted, so it is said once each.</summary>
    private static readonly HashSet<UnitDefinition> RopeKeptLogged = new();

    private readonly RaycastHit[] scanHits = new RaycastHit[64];

    private readonly Dictionary<Aircraft, SlingJob> jobs = new();

    /// <summary>Enemy airlifts waiting for their helicopter to hover before the load is spawned.</summary>
    private readonly Dictionary<Aircraft, SpawnLift> spawnLifts = new();
    private readonly List<Aircraft> endedSpawnLifts = new();

    /// <summary>A guest's view of its own lifts, flown on the host: carrying, the host's row label,
    /// and the LZ if one is set.</summary>
    private readonly Dictionary<Aircraft, RemoteLift> remoteLifts = new();
    private readonly List<SlingJob> endedJobs = new();

    /// <summary>Builders set down on a hook, waiting <see cref="BuilderResumeDelaySeconds"/> before
    /// driving on to their blueprint.</summary>
    private readonly List<(Unit Load, GlobalPosition To, float At)> pendingResumes = new();

    /// <summary>SLING LIFT armed: the next click picks the vehicle.</summary>
    private Aircraft? pendingHelo;

    /// <summary>SET LZ armed: the next click is this lift's LZ.</summary>
    private Aircraft? lzHelo;

    internal CommanderSlingLiftService()
    {
        Instance = this;

        // The hook and the flying are host-side like every spawn and every AI pilot, so a guest's
        // lift is asked for here and flown by the host's copy of this service.
        CommanderNetwork.OnHost("sling", HostStart);
        CommanderNetwork.OnHost("sling.lz", HostLz);
        CommanderNetwork.OnHost("sling.cancel", HostCancel);
        CommanderNetwork.OnSync(SyncLifts);
        CommanderNetwork.OnGuest("sling.jobs", ApplyLifts);
    }

    internal static CommanderSlingLiftService? Instance { get; private set; }

    /// <summary>A sling click is armed, the vehicle or the LZ.</summary>
    internal bool AwaitingSelection => pendingHelo != null || lzHelo != null;

    internal bool AwaitingLzClick => lzHelo != null;

    public void TickActive()
    {
        if (lzHelo != null && !IsLifting(lzHelo))
        {
            lzHelo = null;
        }

        if (pendingHelo is { } armed && (armed == null || armed.disabled))
        {
            pendingHelo = null;
        }

        if (AwaitingSelection && CommanderGameInput.CancelDown)
        {
            CancelSelection();
        }
    }

    public void TickPersistent()
    {
        if (spawnLifts.Count > 0)
        {
            ExpireSpawnLifts();
        }

        if (pendingResumes.Count > 0)
        {
            TickPendingResumes();
        }

        if (jobs.Count == 0)
        {
            return;
        }

        endedJobs.Clear();
        foreach (SlingJob job in jobs.Values)
        {
            string? failure = null;
            if (job.Rescue && job.Helo != null && !job.Helo.disabled
                && (job.Load == null || job.Load.disabled || (job.Carrying && !job.Load.IsSlung())
                    || Time.time - job.StartedAt > PickupTimeoutSeconds))
            {
                // A rescue ends when the winch has the pilot aboard (the game's own Capture destroys
                // the load), or quietly on anything else: CommanderRecoveryService picks the next one.
                endedJobs.Add(job);
                continue;
            }

            if (job.Helo == null || job.Helo.disabled)
            {
                failure = "The lifting helicopter was lost.";
            }
            else if (job.Load == null || job.Load.disabled)
            {
                failure = "The vehicle being lifted was lost.";
            }
            else if (job.Carrying && !job.Load.IsSlung())
            {
                failure = $"{CommanderGameAccess.GetUnitLabel(job.Helo)} lost its load.";
            }
            else if (!job.Carrying && Time.time - job.StartedAt > PickupTimeoutSeconds)
            {
                failure = $"{CommanderGameAccess.GetUnitLabel(job.Helo)} could not hook "
                    + $"{CommanderGameAccess.GetUnitLabel(job.Load)}.";
            }
            else if (job.Carrying
                && !job.Rescue
                && !job.LoadClear
                && !IsLowering(job)
                && (Time.time - job.GroundedSince > LiftOffTimeoutSeconds
                    || IsStrainedTooLong(job)))
            {
                // Still on the ground after all this time: too heavy for this airframe. It is on the
                // ground, so letting go drops it nowhere.
                job.Helo.SetSlingLoadAttachment(job.Load, SlingloadHook.DeployState.Retracting);
                failure = $"{CommanderGameAccess.GetUnitLabel(job.Helo)} cannot lift "
                    + $"{CommanderGameAccess.GetUnitLabel(job.Load)} off the ground.";
            }

            if (failure != null)
            {
                NotifyOwner(job, failure);
                CommanderPlugin.Log.LogWarning($"Sling lift ended: {failure}");
                endedJobs.Add(job);
                continue;
            }

            if (job.Carrying
                && job.HasLz
                && job.LoadClear
                && !job.SettingDown
                && !HasPendingRoute(job.Helo!)
                && HasStalled(job))
            {
                string stalled = $"{CommanderGameAccess.GetUnitLabel(job.Helo)} cannot fly on with "
                    + $"{CommanderGameAccess.GetUnitLabel(job.Load)}. Setting it down.";
                NotifyOwner(job, stalled);
                CommanderPlugin.Log.LogWarning($"Sling lift: {stalled}");
                SetDownHere(job);
            }

            if (job.SpawnedOnHook && !job.ChuteCleared)
            {
                ClearAirdropChute(job);
            }

            EnsureCombatState(job.Helo!);
        }

        for (int i = 0; i < endedJobs.Count; i++)
        {
            EndJob(endedJobs[i]);
        }

        endedJobs.Clear();
    }

    public void ResetSession()
    {
        jobs.Clear();
        spawnLifts.Clear();
        remoteLifts.Clear();
        endedJobs.Clear();
        pendingResumes.Clear();
        CancelSelection();
    }

    /// <summary>The hook, if this aircraft carries one. It is a loadout option, not part of the airframe.</summary>
    /// <summary>
    /// From the BreakRope prefix: false keeps the rope. Every break is logged with snag, angle and
    /// force. A lift this service flies keeps its rope unless a unit is lost or the force passes the
    /// limit: some winches sit inside the cabin so the snag test hits the airframe's own floor, and a
    /// flare to a stop swings heavy loads past the 120 degree angle limit.
    /// </summary>
    internal static bool AllowRopeBreak(SlingloadHook hook, Unit? load, float breakForce, float breakAngle)
    {
        Unit? helo = hook.attachedUnit;
        if (helo == null || load == null)
        {
            return true;
        }

        bool keep = !helo.disabled
            && !load.disabled
            && hook.loadForce <= breakForce
            && helo is Aircraft aircraft
            && Instance?.jobs.ContainsKey(aircraft) == true;

        // The hook asks again every physics tick the fault lasts, so a kept rope is logged and
        // linecast once per airframe type.
        if (keep && !RopeKeptLogged.Add(helo.definition))
        {
            return false;
        }

        Vector3 to = hook.winch.position - hook.hook.position;
        bool snagged = Physics.Linecast(
            hook.winch.position,
            Vector3.Lerp(hook.winch.position, hook.hook.position, 0.8f),
            out RaycastHit hit,
            ~PhysicsLayers.ExclusionZonesMask);
        string reading = $"force {hook.loadForce:0}/{breakForce:0} N, angle {Vector3.Angle(helo.transform.up, to):0}/{breakAngle:0}, "
            + $"rope {to.magnitude:0.0} m, "
            + $"snag {(snagged ? $"{hit.collider.name} {hit.distance:0.0} m down (layer {hit.collider.gameObject.layer})" : "none")}";
        if (keep)
        {
            CommanderPlugin.Log.LogInfo(
                $"Sling lift: {CommanderGameAccess.GetUnitLabel(helo)}'s rope would have parted ({reading}); lifts flown here keep it.");
            return false;
        }

        CommanderPlugin.Log.LogWarning(
            $"Sling lift: {CommanderGameAccess.GetUnitLabel(helo)}'s rope broke under {CommanderGameAccess.GetUnitLabel(load)}: {reading}.");
        return true;
    }

    internal static SlingloadHook? GetHook(Aircraft? aircraft)
    {
        if (aircraft == null)
        {
            return null;
        }

        List<WeaponStation> stations = aircraft.weaponStations;
        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            if (station?.WeaponInfo != null
                && station.WeaponInfo.sling
                && station.Weapons.Count > 0
                && station.Weapons[0] is SlingloadHook hook)
            {
                return hook;
            }
        }

        return null;
    }

    /// <summary>Whether the selection bar offers SLING LIFT for this unit.</summary>
    internal static bool CanLift(Unit? unit)
    {
        return unit is Aircraft aircraft
            && CommanderAirCommandService.IsTaskableAircraft(aircraft)
            && GetHook(aircraft) != null;
    }

    /// <summary>Per physics frame, from the prefix on <c>AIHeloCombatState.FixedUpdateState</c>. True
    /// means this service flew the helicopter and the base state must not.</summary>
    internal static bool TryFly(AIHeloCombatState state)
    {
        CommanderSlingLiftService? service = Instance;
        if (service == null
            || (service.jobs.Count == 0 && service.spawnLifts.Count == 0)
            || StateAircraftField?.GetValue(state) is not Aircraft helo)
        {
            return false;
        }

        if (service.spawnLifts.TryGetValue(helo, out SpawnLift pending) && !helo.disabled)
        {
            service.HoldForSpawn(helo, pending);
            return true;
        }

        if (!service.jobs.TryGetValue(helo, out SlingJob job)
            || helo.disabled
            || job.Load == null
            || job.Load.disabled)
        {
            return false;
        }

        service.Fly(job);
        return true;
    }

    internal bool IsArmedFor(Aircraft helo)
    {
        return ReferenceEquals(pendingHelo, helo);
    }

    internal bool IsLzArmedFor(Aircraft helo)
    {
        return ReferenceEquals(lzHelo, helo);
    }

    internal bool IsLifting(Aircraft helo)
    {
        return jobs.ContainsKey(helo) || spawnLifts.ContainsKey(helo) || remoteLifts.ContainsKey(helo);
    }

    /// <summary>Some lift has this unit as its load, hooked or not yet, so two helicopters never go
    /// for the same one.</summary>
    internal bool IsLoadOfAnyLift(Unit unit)
    {
        foreach (SlingJob job in jobs.Values)
        {
            if (ReferenceEquals(job.Load, unit))
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsCarrying(Aircraft helo)
    {
        return (jobs.TryGetValue(helo, out SlingJob job) && job.Carrying)
            || (remoteLifts.TryGetValue(helo, out RemoteLift remote) && remote.Carrying);
    }

    internal string GetRowLabel(Aircraft helo)
    {
        if (IsArmedFor(helo))
        {
            return "SLING LIFT   click the vehicle to lift";
        }

        if (!jobs.TryGetValue(helo, out SlingJob job))
        {
            if (remoteLifts.TryGetValue(helo, out RemoteLift remote))
            {
                return IsLzArmedFor(helo) ? "SLING LIFT   click the LZ" : remote.Label;
            }

            return "CARGO HOOK   fly a friendly vehicle somewhere it cannot drive";
        }

        string load = CommanderGameAccess.GetUnitLabel(job.Load);
        if (IsLzArmedFor(helo))
        {
            return $"SLING LIFT   click the LZ for {load}";
        }

        if (!job.Carrying)
        {
            return $"SLING LIFT   flying to {load}";
        }

        if (!job.LoadClear && !IsLowering(job))
        {
            return $"SLING LIFT   lifting {load} off the ground";
        }

        if (HasPendingRoute(helo))
        {
            return $"SLING LIFT   carrying {load} along the travel points";
        }

        return job.HasLz
            ? $"SLING LIFT   carrying {load} to the LZ"
            : $"SLING LIFT   holding {load}   right-click to fly it, SET LZ to set it down";
    }

    internal void BeginSelection(Aircraft helo)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();

        // The attach is a host-side call like every spawn in the mod; a guest's goes to the host.
        if (hq == null || (!hq.IsServer && !CommanderNetwork.HostHasMod))
        {
            CommanderNotice.Fail(CommanderNetwork.HostMissingReason);
            return;
        }

        pendingHelo = helo;
        lzHelo = null;
    }

    internal void BeginLzSelection(Aircraft helo)
    {
        if (IsLifting(helo))
        {
            lzHelo = helo;
            pendingHelo = null;
        }
    }

    internal void CancelSelection()
    {
        pendingHelo = null;
        lzHelo = null;
    }

    /// <summary>The armed click from <see cref="CommanderInputController"/>: an LZ for SET LZ, or the
    /// vehicle for SLING LIFT, which starts the lift there and then. A click on nothing stays armed.</summary>
    internal void TryHandleWorldClick(Vector2 screenPosition)
    {
        if (lzHelo != null)
        {
            if (CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition point))
            {
                Aircraft lifting = lzHelo;
                lzHelo = null;
                TryRedirect(lifting, point);
            }

            return;
        }

        Aircraft? helo = pendingHelo;
        if (helo == null || !TryGetUnitAt(screenPosition, out Unit unit))
        {
            return;
        }

        string? refusal = GetLoadRefusal(unit);
        if (refusal != null)
        {
            CommanderNotice.Fail(refusal);
            return;
        }

        CancelSelection();
        if (CommanderNetwork.Forward("sling", CommanderNetwork.Id(helo), CommanderNetwork.Id(unit)))
        {
            CommanderOrderPing.Show(unit.GlobalPosition(), attack: false);
            return;
        }

        StartJob(helo, unit);
    }

    /// <summary>
    /// The CANCEL button. A helicopter with a load on the hook sets it down where it is first:
    /// letting go at carrying height is a fall of tens of metres onto a 100 G impact limit.
    /// </summary>
    internal void CancelJob(Aircraft helo)
    {
        if (CommanderNetwork.Forward("sling.cancel", CommanderNetwork.Id(helo)))
        {
            return;
        }

        if (!jobs.TryGetValue(helo, out SlingJob job))
        {
            return;
        }

        if (job.Rescue)
        {
            if (job.Carrying && job.Load != null)
            {
                job.Helo.SetSlingLoadAttachment(job.Load, SlingloadHook.DeployState.Retracting);
            }

            EndJob(job);
            return;
        }

        if (!job.Carrying)
        {
            EndJob(job);
            return;
        }

        SetDownHere(job);
    }

    /// <summary>
    /// The local faction's lift LZs, for the green LZ markers. The host's job list also holds the
    /// enemy commander's airlifts, which must not be drawn.
    /// </summary>
    internal void CopyLandingZones(List<GlobalPosition> into)
    {
        into.Clear();
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        foreach (SlingJob job in jobs.Values)
        {
            if (job.HasLz && CommanderGameAccess.IsFriendlyUnit(job.Helo, localHq))
            {
                into.Add(job.Drop);
            }
        }

        foreach (RemoteLift remote in remoteLifts.Values)
        {
            if (remote.HasLz)
            {
                into.Add(remote.Drop);
            }
        }
    }

    /// <summary>
    /// The enemy commander's airlift: a builder flown to a base it captured, set down on
    /// <paramref name="drop"/>. Run as the helicopter's faction, so every "is this mine" test the
    /// lift makes answers for it, and quiet, so the player gets no ping of where it is going.
    /// </summary>
    internal bool TryStartAiLift(Aircraft helo, Unit load, GlobalPosition drop)
    {
        if (helo == null
            || helo.disabled
            || jobs.ContainsKey(helo)
            || GetHook(helo) == null
            || load is not GroundVehicle
            || load.disabled
            || load.IsSlung()
            || !load.definition.CanSlingLoad)
        {
            return false;
        }

        foreach (SlingJob other in jobs.Values)
        {
            if (ReferenceEquals(other.Load, load))
            {
                return false;
            }
        }

        CommanderNetwork.RunAs(helo.NetworkHQ, () =>
        {
            if (CommanderAirCommandService.IsTaskableAircraft(helo))
            {
                StartJob(helo, load, quiet: true);
            }
        });
        if (!jobs.TryGetValue(helo, out SlingJob job))
        {
            return false;
        }

        job.Drop = CommanderGameAccess.SnapToTerrain(drop);
        job.HasLz = true;
        return true;
    }

    /// <summary>
    /// The enemy's airlift when it owns no builder on the ground to fly: a carrier-only commander
    /// (Carrier Duel) has nowhere to roll one out, so the builders it bought sat in the reserve and its
    /// captured base never got a depot. The helicopter is brought to a hover first and the vehicle is
    /// spawned on the rope there (<see cref="HoldForSpawn"/>): spawned under an Ibis cruising at
    /// 51 m/s, the load swung out past the rope's 20 m as the lift slowed it and parted in 3 s.
    /// Host only, like every spawn.
    /// </summary>
    internal bool TryBeginAiSpawnLift(Aircraft helo, VehicleDefinition definition, GlobalPosition drop)
    {
        if (GetHook(helo) == null
            || NetworkSceneSingleton<Spawner>.i == null
            || helo.disabled
            || jobs.ContainsKey(helo)
            || spawnLifts.ContainsKey(helo)
            || !helo.NetworkHQ.IsServer
            || definition.unitPrefab == null
            || !definition.CanSlingLoad)
        {
            return false;
        }

        CommanderAirCommandService.Instance?.ClearAircraftRoute(helo, releaseParked: true);
        EnsureCombatState(helo);
        spawnLifts[helo] = new SpawnLift(definition, CommanderGameAccess.SnapToTerrain(drop));
        return true;
    }

    /// <summary>
    /// RECOVERY's pickup: fly to a downed pilot, hook him and let the hook's own winch pull him
    /// aboard (<c>SlingloadHook.DeployState.RescuePilot</c>, which ends in <c>PilotDismounted.Capture</c>).
    /// </summary>
    internal bool TryStartRescue(Aircraft helo, PilotDismounted pilot)
    {
        if (helo == null
            || helo.disabled
            || jobs.ContainsKey(helo)
            || GetHook(helo) == null
            || pilot == null
            || pilot.disabled
            || pilot.IsSlung())
        {
            return false;
        }

        foreach (SlingJob other in jobs.Values)
        {
            if (ReferenceEquals(other.Load, pilot))
            {
                return false;
            }
        }

        CommanderNetwork.RunAs(helo.NetworkHQ, () =>
        {
            if (CommanderAirCommandService.IsTaskableAircraft(helo))
            {
                StartJob(helo, pilot, quiet: true);
            }
        });
        if (!jobs.TryGetValue(helo, out SlingJob job))
        {
            return false;
        }

        job.Rescue = true;
        return true;
    }

    /// <summary>A recovery pickup in progress, for the selection bar.</summary>
    internal bool TryGetRescue(Aircraft helo, out bool winching)
    {
        winching = jobs.TryGetValue(helo, out SlingJob job) && job.Carrying;
        return job != null && job.Rescue;
    }

    /// <summary>The downed pilot a rescue is going for, for the RESCUE marker.</summary>
    internal bool TryGetRescueTarget(Aircraft helo, out Unit pilot)
    {
        pilot = jobs.TryGetValue(helo, out SlingJob job) && job.Rescue ? job.Load : null!;
        return pilot != null && !pilot.disabled;
    }

    /// <summary>Some lift already has this unit, on the hook or on its way to it.</summary>
    internal bool IsLoadTaken(Unit load)
    {
        foreach (SlingJob job in jobs.Values)
        {
            if (ReferenceEquals(job.Load, load))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A lift's failure, on the screen of whoever owns the helicopter.</summary>
    private static void NotifyOwner(SlingJob job, string text)
    {
        if (job.Helo == null || CommanderGameAccess.IsFriendlyUnit(job.Helo, CommanderGameAccess.GetLocalHq()))
        {
            CommanderNotice.Fail(text);
        }
    }

    private static bool TryGetUnitAt(Vector2 screenPosition, out Unit unit)
    {
        if (CommanderMarkerService.Instance?.TryGetMarkerUnitAt(screenPosition, out Unit marker) == true)
        {
            unit = marker;
            return true;
        }

        return CommanderGameAccess.TryRaycastSelectableUnit(screenPosition, out unit);
    }

    /// <summary>
    /// Full collective and still not climbing for <see cref="StrainReleaseSeconds"/>. Letting go this
    /// low drops the load nowhere, and holding on ends in a crash.
    /// </summary>
    private static bool IsStrainedTooLong(SlingJob job)
    {
        Aircraft helo = job.Helo!;
        bool straining = helo.GetInputs().throttle >= 0.98f && helo.rb.velocity.y < 0.5f;
        if (!straining)
        {
            job.StrainSince = -1f;
            return false;
        }

        if (job.StrainSince < 0f)
        {
            job.StrainSince = Time.time;
        }

        return Time.time - job.StrainSince > StrainReleaseSeconds;
    }

    private static bool HasPendingRoute(Aircraft helo)
    {
        return CommanderAirCommandService.Instance?.TryGetAircraftRoute(
                helo, out IReadOnlyList<GlobalPosition> route, out int index, out _) == true
            && index < route.Count;
    }

    /// <summary>Keeps a loaded helicopter's height target within <see cref="MaxAltitudeLeadMeters"/>
    /// of where it is, so it climbs and sinks at a few metres a second instead of snatching.</summary>
    private static float LimitClimb(Aircraft helo, float wantedY)
    {
        float currentY = helo.GlobalPosition().y;
        return Mathf.Clamp(wantedY, currentY - MaxAltitudeLeadMeters, currentY + MaxAltitudeLeadMeters);
    }

    /// <summary>The switches AIHeloTransportState flips on its way into and out of a hover.</summary>
    private static void SetHover(Aircraft helo, bool hover)
    {
        if (helo.IsAutoHoverEnabled() != hover)
        {
            helo.GetControlsFilter().SetAutoHover(hover);
        }

        helo.SetFlightAssist(!hover);
    }

    /// <summary>
    /// The hook's own rule and nothing more: rope in reach, speeds matched. The hover never settles
    /// precisely enough for a stricter "straight overhead" test.
    /// </summary>
    private static void TryHook(SlingJob job)
    {
        Unit load = job.Load;
        if (load.rb == null)
        {
            return;
        }

        // Where the hook itself aims: halfway up the vehicle.
        Vector3 hookPoint = load.transform.position + (0.5f * load.definition.height * load.transform.up);
        if ((!job.SpawnedOnHook && Vector3.Distance(job.Hook.winch.position, hookPoint) > HookReachMeters)
            || (job.Helo.rb.velocity - load.rb.velocity).sqrMagnitude > MatchedSpeedSquared)
        {
            return;
        }

        // The hook transform only moves onto the load in the hook's Update, and an AI lift goes from
        // Retracted straight to Connected, so on the first physics tick the rope has zero length and its
        // angle test (Vector3.Angle vs. the helicopter's up, limit 120) parts it. Place it now.
        job.Hook.hook.position = hookPoint;
        job.Helo.SetSlingLoadAttachment(load, SlingloadHook.DeployState.Connected);
        job.Carrying = true;
        job.HoldPoint = load.GlobalPosition();
        if (job.Rescue)
        {
            // Connected made the joint; the rescue state reels the pilot in along it.
            job.Helo.SetSlingLoadAttachment(load, SlingloadHook.DeployState.RescuePilot);
            CommanderPlugin.Log.LogInfo($"Recovery: {CommanderGameAccess.GetUnitLabel(job.Helo)} is winching up "
                + $"{CommanderGameAccess.GetUnitLabel(load)}.");
            return;
        }

        // Measured off this helicopter's own rope: the Tarantula's need not match the Ibis's.
        job.CarryHeight = job.Hook.GetLineMaxLength() + load.definition.height + CarryMarginMeters;
        job.HoverHeight = job.CarryHeight;
        job.LoadClear = false;
        job.GroundedSince = Time.time;
        job.CruiseY = job.Helo.GlobalPosition().y;
        ResetProgress(job);
        CommanderPlugin.Log.LogInfo($"Sling lift: hooked {CommanderGameAccess.GetUnitLabel(load)} "
            + $"{Time.time - job.StartedAt:0} s after the order.");
    }

    /// <summary>Over the LZ with the route done: passing over the LZ on the way to a travel point is
    /// not the set-down.</summary>
    private static bool IsLowering(SlingJob job)
    {
        return job.Carrying
            && job.HasLz
            && HorizontalDistance(job.Helo.GlobalPosition(), job.Drop) < LoweringRadiusMeters
            && !HasPendingRoute(job.Helo);
    }

    /// <summary>
    /// <c>GroundVehicle.CheckAirdrop</c> opens a cargo parachute half a second after any vehicle spawns
    /// with nothing 10 m under it, which includes every load spawned on a hook; the canopy locks its
    /// wheels. Destroying the system is the game's own landing path (its OnDestroy unlocks them).
    /// </summary>
    private static void ClearAirdropChute(SlingJob job)
    {
        if (job.Load == null)
        {
            job.ChuteCleared = true;
            return;
        }

        CargoDeploymentSystem? chute = job.Load.GetComponentInChildren<CargoDeploymentSystem>(true);
        if (chute != null)
        {
            Object.Destroy(chute.gameObject);
            job.ChuteCleared = true;
        }
        else if (Time.time - job.StartedAt > AirdropChuteCheckSeconds)
        {
            job.ChuteCleared = true;
        }
    }

    private static void SetDownHere(SlingJob job)
    {
        // Over the sea "here" is the seabed: fly on toward the LZ instead; the next stall check asks again.
        GlobalPosition below = CommanderGameAccess.SnapToTerrain(job.Helo.GlobalPosition());
        if (CommanderGameAccess.IsBelowSeaLevel(below))
        {
            ResetProgress(job);
            return;
        }

        job.Drop = below;
        job.HasLz = true;
        job.SettingDown = true;
    }

    private static void ResetProgress(SlingJob job)
    {
        job.ProgressCheckAt = Time.time + StallSeconds;
        job.ProgressDistance = HorizontalDistance(job.Helo.GlobalPosition(), job.HasLz ? job.Drop : job.HoldPoint);
        job.ProgressAltitude = job.Helo.radarAlt;
    }

    /// <summary>
    /// Once every <see cref="StallSeconds"/>: has the loaded helicopter closed on the LZ or climbed?
    /// Close to the LZ it is meant to stand still and come down, so that is exempt.
    /// </summary>
    private static bool HasStalled(SlingJob job)
    {
        if (Time.time < job.ProgressCheckAt)
        {
            return false;
        }

        float distance = HorizontalDistance(job.Helo.GlobalPosition(), job.Drop);
        float altitude = job.Helo.radarAlt;
        bool stalled = distance > 30f
            && job.ProgressDistance - distance < 30f
            && altitude - job.ProgressAltitude < 5f;
        job.ProgressCheckAt = Time.time + StallSeconds;
        job.ProgressDistance = distance;
        job.ProgressAltitude = altitude;
        return stalled;
    }

    /// <summary>
    /// Whether this probe hit the lifting helicopter or its own load. Which of owner, rigidbody or
    /// hierarchy a rotor or hook answers to is prefab data, so all three are checked, and anything right
    /// where the helicopter or load is counts too, except terrain and buildings (Statics layer).
    /// </summary>
    private static bool IsPartOf(RaycastHit hit, Unit unit, float radius, float below)
    {
        Collider? collider = hit.collider;
        if (collider is null)
        {
            return false;
        }

        if ((unit.rb != null && ReferenceEquals(hit.rigidbody, unit.rb))
            || collider.transform.IsChildOf(unit.transform)
            || ReferenceEquals(collider.GetComponentInParent<Unit>(), unit))
        {
            return true;
        }

        if (collider.gameObject.layer == PhysicsLayers.Statics)
        {
            return false;
        }

        Vector3 offset = hit.point - unit.transform.position;
        return offset.y > -below && (offset.x * offset.x) + (offset.z * offset.z) < radius * radius;
    }

    /// <summary>
    /// The radar-altitude hold for an empty leg flown in <c>AutoAim</c>: the plain cruise height, or
    /// enough to pass <see cref="HelicopterClearanceMeters"/> over the tallest thing ahead. AutoAim
    /// only follows the ground directly under the airframe, not obstacles ahead.
    /// </summary>
    private static float EmptyCruiseHold(Aircraft helo, float clearTopY)
    {
        float ground = Mathf.Max(CommanderGameAccess.SnapToTerrain(helo.GlobalPosition()).y, 0f);
        return Mathf.Max(EmptyCruiseHeight, clearTopY + HelicopterClearanceMeters - ground);
    }

    private static float HorizontalDistance(GlobalPosition a, GlobalPosition b)
    {
        Vector3 offset = a - b;
        offset.y = 0f;
        return offset.magnitude;
    }

    /// <summary>The lift is flown from inside AIHeloCombatState, so a pilot anywhere else is put back
    /// there. A take-off is left to finish, because it ends in that state anyway.</summary>
    private static void EnsureCombatState(Aircraft helo)
    {
        Pilot? pilot = helo.pilots != null && helo.pilots.Length > 0 ? helo.pilots[0] : null;
        if (pilot == null
            || pilot.currentState == null
            || pilot.currentState is AIHeloCombatState
            || pilot.currentState is AIHeloTakeoffState)
        {
            return;
        }

        CommanderAirCommandService.ReturnToCombatState(helo);
    }

    /// <summary>
    /// A SET LZ click: that is the LZ now. Only SET LZ sets it; a right-click is a travel point.
    /// </summary>
    private bool TryRedirect(Aircraft helo, GlobalPosition point)
    {
        if (CommanderNetwork.Forward("sling.lz", CommanderNetwork.Id(helo), CommanderNetwork.Position(point)))
        {
            CommanderOrderPing.Show(point, attack: false);
            return true;
        }

        if (!jobs.TryGetValue(helo, out SlingJob job))
        {
            return false;
        }

        GlobalPosition drop = CommanderGameAccess.SnapToTerrain(point);
        if (CommanderGameAccess.IsBelowSeaLevel(drop))
        {
            CommanderNotice.Fail("That is water. Pick dry land to set it down on.");
            return true;
        }

        job.Drop = drop;
        job.HasLz = true;
        job.SettingDown = false;
        if (job.Carrying)
        {
            // A set-down already under way has lowered the hover height; carry the load up again.
            job.HoverHeight = job.CarryHeight;
            ResetProgress(job);
        }

        CommanderOrderPing.Show(drop, attack: false);
        return true;
    }

    private void HostStart(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryUnit(args, 1, out Unit unit)
            || unit is not Aircraft helo
            || !ReferenceEquals(helo.NetworkHQ, hq)
            || !CommanderNetwork.TryUnit(args, 2, out Unit load))
        {
            return;
        }

        string? refusal = GetLoadRefusal(load);
        if (refusal != null)
        {
            CommanderNotice.Fail(refusal);
            return;
        }

        StartJob(helo, load);
    }

    private void HostLz(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && unit is Aircraft helo
            && ReferenceEquals(helo.NetworkHQ, hq)
            && CommanderNetwork.TryPosition(args, 2, out GlobalPosition point))
        {
            TryRedirect(helo, point);
        }
    }

    private void HostCancel(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit) && unit is Aircraft helo && ReferenceEquals(helo.NetworkHQ, hq))
        {
            CancelJob(helo);
        }
    }

    /// <summary>A spawn lift whose helicopter is gone, or never settled, is dropped; nothing was
    /// spawned, and the commander's next review asks again.</summary>
    private void ExpireSpawnLifts()
    {
        endedSpawnLifts.Clear();
        foreach (KeyValuePair<Aircraft, SpawnLift> entry in spawnLifts)
        {
            if (entry.Key == null
                || entry.Key.disabled
                || (entry.Value.HoverSince > 0f && Time.time - entry.Value.HoverSince > SpawnHoldTimeoutSeconds))
            {
                endedSpawnLifts.Add(entry.Key!);
            }
        }

        for (int i = 0; i < endedSpawnLifts.Count; i++)
        {
            spawnLifts.Remove(endedSpawnLifts[i]);
            CommanderPlugin.Log.LogWarning("Sling lift: a helicopter never settled into a hover to take on a new builder.");
        }

        endedSpawnLifts.Clear();
    }

    /// <summary>
    /// Per physics frame while a spawn lift waits: fly out empty at cruise, then stop
    /// <see cref="SpawnNearDropMeters"/> short of the drop and spawn once steady. Empty because loaded
    /// lifts crawl and drops can be far away; near the drop so the player still sees it carried in.
    /// </summary>
    private void HoldForSpawn(Aircraft helo, SpawnLift pending)
    {
        GlobalPosition at = helo.GlobalPosition();
        Vector3 offset = pending.Drop - at;
        offset.y = 0f;
        if (pending.HoverSince <= 0f && offset.magnitude > SpawnNearDropMeters)
        {
            SetHover(helo, false);
            if (Time.time >= pending.NextScanAt)
            {
                pending.NextScanAt = Time.time + ObstacleScanSeconds;
                pending.ClearTopY = ScanTopY(helo, null, pending.Drop);
            }

            helo.autopilot.AutoAim(pending.Drop, EmptyCruiseHold(helo, pending.ClearTopY), Vector3.zero, Vector3.zero, followTerrain: true);
            return;
        }

        if (pending.HoverSince <= 0f)
        {
            float floor = Mathf.Max(CommanderGameAccess.SnapToTerrain(at).y, 0f) + SpawnHoverHeight;
            pending.HoverPoint = new GlobalPosition(at.x, Mathf.Max(at.y, floor), at.z);
            pending.HoverSince = Time.time;
        }

        helo.CheckRadarAlt();
        if (helo.rb.velocity.sqrMagnitude < SpawnMaxSpeed * SpawnMaxSpeed && helo.radarAlt >= SpawnUnderHookMinAltitude)
        {
            spawnLifts.Remove(helo);
            SpawnOnHook(helo, pending);
            return;
        }

        SetHover(helo, true);
        helo.autopilot.Hover(pending.HoverPoint, 0f, helo.cockpit.xform.forward);
    }

    private void SpawnOnHook(Aircraft helo, SpawnLift pending)
    {
        SlingloadHook? hook = GetHook(helo);
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        VehicleDefinition definition = pending.Definition;
        if (hook == null || spawner == null)
        {
            return;
        }

        // Its hook point (half its height up) a rope's length under the winch, so the rope is taut
        // from the first tick. TryHook lets a spawned load past its usual reach for the same reason.
        float rope = hook.GetLineMaxLength() - SpawnRopeSlackMeters;
        Vector3 basePoint = hook.winch.position - (Vector3.up * (rope + (0.5f * definition.height)));
        GroundVehicle load = spawner.SpawnVehicle(
            definition.unitPrefab,
            basePoint.ToGlobalPosition(),
            Quaternion.Euler(0f, helo.transform.eulerAngles.y, 0f),
            helo.rb.velocity,
            helo.NetworkHQ,
            $"gc_airlift_{helo.NetworkHQ.faction.factionName}_{Time.frameCount}",
            1f,
            holdPosition: true,
            null);
        if (load == null)
        {
            return;
        }

        // Not StartJob: it stops the load's current order, and a vehicle spawned this frame has none
        // and has not run Start yet. The next physics tick's TryHook connects it.
        jobs[helo] = new SlingJob(helo, hook, load, null, Time.time)
        {
            HoldPoint = load.GlobalPosition(),
            Drop = pending.Drop,
            HasLz = true,
            SpawnedOnHook = true,
        };
        CommanderPlugin.Log.LogInfo(
            $"Sling lift: {CommanderGameAccess.GetUnitLabel(helo)} carries a new {definition.unitName} on its hook.");
    }

    /// <summary>This guest's lifts: helicopter, carrying, row label, LZ set, LZ.</summary>
    private void SyncLifts(FactionHQ hq, List<string[]> messages)
    {
        List<string> args = new() { "sling.jobs" };
        foreach (SlingJob job in jobs.Values)
        {
            if (job.Helo == null || !ReferenceEquals(job.Helo.NetworkHQ, hq))
            {
                continue;
            }

            args.Add(CommanderNetwork.Id(job.Helo));
            args.Add(CommanderNetwork.Flag(job.Carrying));
            args.Add(GetRowLabel(job.Helo));
            args.Add(CommanderNetwork.Flag(job.HasLz));
            args.Add(CommanderNetwork.Position(job.Drop));
        }

        messages.Add(args.ToArray());
    }

    private void ApplyLifts(string[] args)
    {
        remoteLifts.Clear();
        for (int i = 1; i + 4 < args.Length; i += 5)
        {
            if (CommanderNetwork.TryUnit(args, i, out Unit unit) && unit is Aircraft helo)
            {
                CommanderNetwork.TryPosition(args, i + 4, out GlobalPosition drop);
                remoteLifts[helo] = new RemoteLift(
                    CommanderNetwork.Bool(args, i + 1),
                    CommanderNetwork.Arg(args, i + 2),
                    CommanderNetwork.Bool(args, i + 3),
                    drop);
            }
        }
    }

    private string? GetLoadRefusal(Unit unit)
    {
        if (unit is not GroundVehicle
            || unit.disabled
            || !CommanderGameAccess.IsFriendlyUnit(unit, CommanderGameAccess.GetLocalHq()))
        {
            return "Pick a friendly ground vehicle to lift.";
        }

        string label = CommanderGameAccess.GetUnitLabel(unit);

        // Asset data: the game decides per vehicle what a hook may take, and refuses the rest itself.
        if (!unit.definition.CanSlingLoad)
        {
            return $"The game does not allow {label} on a hook.";
        }

        foreach (SlingJob job in jobs.Values)
        {
            if (ReferenceEquals(job.Load, unit))
            {
                return $"{label} is already being lifted.";
            }
        }

        return unit.IsSlung() ? $"{label} is already on a hook." : null;
    }

    private void StartJob(Aircraft helo, Unit load, bool quiet = false)
    {
        SlingloadHook? hook = GetHook(helo);
        if (hook == null || !CommanderAirCommandService.IsTaskableAircraft(helo) || load == null || load.disabled)
        {
            CommanderNotice.Fail("The sling lift could not start.");
            return;
        }

        // Where the vehicle was going (a builder's blueprint, say) is remembered before it is stopped
        // for the pickup, so it carries on from the LZ. The hook only takes a load moving with it.
        GlobalPosition? resume = null;
        if (CommanderGameAccess.TryGetCurrentCommandPosition(load, out GlobalPosition going))
        {
            resume = going;
        }

        // A live move order would be re-asserted over this stop and drive the load away from the hook.
        CommanderMoveService.Instance?.ClearOrder(load);
        CommanderGameAccess.TrySetDestination(load, load.GlobalPosition());

        // Off any landing order or pad, and into the one pilot state this service flies.
        CommanderAirCommandService.Instance?.ClearAircraftRoute(helo, releaseParked: true);
        EnsureCombatState(helo);

        jobs[helo] = new SlingJob(helo, hook, load, resume, Time.time) { HoldPoint = load.GlobalPosition() };
        if (!quiet && !CommanderNetwork.IsHandlingGuest)
        {
            CommanderOrderPing.Show(load.GlobalPosition(), attack: false);
        }

        CommanderPlugin.Log.LogInfo(
            $"Sling lift: {CommanderGameAccess.GetUnitLabel(helo)} lifting {CommanderGameAccess.GetUnitLabel(load)} "
                + $"({load.definition.mass:0} kg, rope {hook.GetLineMaxLength():0} m).");
    }

    private void Fly(SlingJob job)
    {
        Aircraft helo = job.Helo;
        Unit load = job.Load;
        if (!job.Carrying)
        {
            ApproachPickup(job);
            TryHook(job);
            return;
        }

        if (job.Rescue)
        {
            SetHover(helo, true);
            helo.autopilot.Hover(job.HoldPoint, PickupHoverHeight, helo.cockpit.xform.forward);
            return;
        }

        load.CheckRadarAlt();
        if (load.radarAlt < LoadGroundedMeters)
        {
            if (job.LoadClear)
            {
                job.LoadClear = false;
                job.GroundedSince = Time.time;
            }
        }
        else if (load.radarAlt > LoadClearMeters)
        {
            job.LoadClear = true;
        }

        if (IsLowering(job))
        {
            // Straight down from wherever it arrived, which the obstacle scan may have made high.
            if (!job.Lowering)
            {
                job.Lowering = true;
                job.HoverHeight = helo.GlobalPosition().y - job.Drop.y;
            }

            job.HoverHeight = Mathf.Max(MinimumHoverHeight, job.HoverHeight - (LoweringSpeed * Time.fixedDeltaTime));
            SetHover(helo, true);
            job.TargetY = job.Drop.y + job.HoverHeight;
            helo.autopilot.Hover(job.Drop, job.HoverHeight, helo.cockpit.xform.forward);
            TryRelease(job);
            return;
        }

        job.Lowering = false;
        job.HoverHeight = job.CarryHeight;
        if (!job.LoadClear)
        {
            // Straight up over the load before going anywhere, or it drags the load along the ground.
            SetHover(helo, true);
            GlobalPosition over = load.GlobalPosition();
            float liftY = LimitClimb(helo, Mathf.Max(over.y, ClearTopY(job, over)) + job.CarryHeight);
            job.TargetY = liftY;
            helo.autopilot.Hover(new GlobalPosition(over.x, liftY, over.z), 0f, helo.cockpit.xform.forward);
            return;
        }

        // Travel points first, the same route and line any aircraft gets; with none left it goes to
        // the LZ, or waits over the last point it was sent to.
        if (CommanderAirCommandService.Instance?.TryGetNextRoutePoint(helo, RouteArrivalMeters, out GlobalPosition travel) == true)
        {
            job.HoldPoint = travel;
            CarryTowards(job, travel);
            return;
        }

        CarryTowards(job, job.HasLz ? job.Drop : job.HoldPoint);
    }

    /// <summary>
    /// The empty leg to the pickup: AutoAim cruise from far out, then the last 300 m in a hover that
    /// stays high over obstacles and only descends once right over the vehicle, like the game's own
    /// transport AI.
    /// </summary>
    private void ApproachPickup(SlingJob job)
    {
        Aircraft helo = job.Helo;
        GlobalPosition target = job.Load.GlobalPosition();
        Vector3 offset = target - helo.GlobalPosition();
        offset.y = 0f;
        float distance = offset.magnitude;
        if (distance >= HoverRadiusMeters)
        {
            SetHover(helo, false);
            helo.autopilot.AutoAim(target, EmptyCruiseHold(helo, ClearTopY(job, target)), Vector3.zero, Vector3.zero, followTerrain: true);
            return;
        }

        SetHover(helo, true);
        if (distance < PickupDescendMeters)
        {
            helo.autopilot.Hover(target, PickupHoverHeight, helo.cockpit.xform.forward);
            return;
        }

        float approachY = Mathf.Max(target.y + ApproachHeight, ClearTopY(job, target) + HelicopterClearanceMeters);
        helo.autopilot.Hover(
            new GlobalPosition(target.x, approachY, target.z),
            0f,
            distance > 50f ? offset : helo.cockpit.xform.forward);
    }

    /// <summary>
    /// A loaded leg, flown in the hover the whole way at <see cref="CarrySpeed"/> and at a height that
    /// keeps the load clear of everything along the path. <c>Autopilot.Hover</c> is a position
    /// controller with no speed limit, so the setpoint is the helicopter's position plus its speed error
    /// times <see cref="VelocityGainSeconds"/>, turning it into a speed hold; the hover's rate term
    /// damps it. Near the point it is a plain position hold again.
    /// </summary>
    private void CarryTowards(SlingJob job, GlobalPosition target)
    {
        Aircraft helo = job.Helo;
        GlobalPosition at = helo.GlobalPosition();
        Vector3 offset = target - at;
        offset.y = 0f;
        float distance = offset.magnitude;
        float required = Mathf.Max(target.y, ClearTopY(job, target)) + job.CarryHeight;

        // Climb at once for what is ahead, give height back slowly: a glide over a dip, not a trace
        // of every fold in the ground.
        job.CruiseY = Mathf.Max(required, job.CruiseY - (SinkRate * Time.fixedDeltaTime));
        float carryY = LimitClimb(helo, job.CruiseY);
        job.TargetY = carryY;

        // Well under its height target it is climbing flat out; slow down rather than fly into what
        // it is climbing for.
        float speedScale = Mathf.Clamp(
            1f - ((job.CruiseY - at.y - ClimbSlowdownStart) / ClimbSlowdownRange), MinSpeedScale, 1f);

        GlobalPosition setpoint;
        if (distance <= PositionHoldMeters)
        {
            setpoint = new GlobalPosition(target.x, carryY, target.z);
        }
        else
        {
            Vector3 velocity = helo.rb.velocity;
            velocity.y = 0f;
            Vector3 wanted = offset / distance * Mathf.Min(CarrySpeed * speedScale, distance * ApproachSlowdown);
            Vector3 lead = (wanted - velocity) * VelocityGainSeconds;
            setpoint = new GlobalPosition(at.x + lead.x, carryY, at.z + lead.z);
        }

        SetHover(helo, true);
        helo.autopilot.Hover(setpoint, 0f, distance > 50f ? offset : helo.cockpit.xform.forward);
    }

    /// <summary>
    /// Height, above sea level, of the top of the highest thing (terrain, building, tower, vehicle)
    /// under the helicopter and along the path towards <paramref name="target"/> (12 s of travel ahead,
    /// at least 200 m). Downward sphere
    /// casts, so something a few metres off the line still counts; the helicopter and its own load are
    /// skipped. Refreshed five times a second, a few metres of travel at carrying speed.
    /// </summary>
    private float ClearTopY(SlingJob job, GlobalPosition target)
    {
        if (Time.time < job.NextScanAt)
        {
            return job.ClearTopY;
        }

        job.NextScanAt = Time.time + ObstacleScanSeconds;
        job.ClearTopY = ScanTopY(job.Helo, job.Load, target);
        return job.ClearTopY;
    }

    private float ScanTopY(Aircraft helo, Unit? load, GlobalPosition target)
    {
        Vector3 from = helo.transform.position;
        Vector3 toward = target.ToLocalPosition() - from;
        toward.y = 0f;
        float length = toward.magnitude;
        Vector3 direction = length > 0.01f ? toward / length : Vector3.zero;
        float reach = Mathf.Max(MinLookAheadMeters, helo.speed * LookAheadSeconds);
        float top = float.MinValue;

        // Exclusion zones are invisible volumes, not things to fly over; the hook's own
        // rope-snag test ignores them for the same reason.
        int scanMask = ~(int)PhysicsLayers.ExclusionZonesMask;
        for (int i = 0; i < ScanFractions.Length; i++)
        {
            float along = Mathf.Min(ScanFractions[i] * reach, length);
            Vector3 origin = from + (direction * along);
            origin.y = from.y + 500f;
            int count = Physics.SphereCastNonAlloc(
                origin,
                ObstacleScanRadius,
                Vector3.down,
                scanHits,
                3000f,
                scanMask,
                QueryTriggerInteraction.Ignore);
            for (int h = 0; h < count; h++)
            {
                RaycastHit hit = scanHits[h];

                // Distance 0 is a probe that started inside something, with no usable contact point.
                if (hit.distance <= 0f
                    || IsPartOf(hit, helo, 15f, 10f)
                    || (load != null && IsPartOf(hit, load, 10f, 3f)))
                {
                    continue;
                }

                top = Mathf.Max(top, hit.point.y);
            }

            if (along >= length)
            {
                break;
            }
        }

        return top == float.MinValue ? target.y : new Vector3(0f, top, 0f).ToGlobalPosition().y;
    }

    private void TryRelease(SlingJob job)
    {
        Unit load = job.Load;

        // With the rope paid out, a helicopter down at its floor has its load on the ground whatever
        // the load's own reading says. Fly() refreshed that reading this frame.
        if (load.radarAlt > GroundedLoadAltitude && job.Helo.radarAlt > MinimumHoverHeight + 1f)
        {
            return;
        }

        job.Helo.SetSlingLoadAttachment(load, SlingloadHook.DeployState.Retracting);
        CommanderPlugin.Log.LogInfo($"Sling lift: set {CommanderGameAccess.GetUnitLabel(load)} down "
            + $"{Time.time - job.StartedAt:0} s after the order.");
        CommanderRepairService.Instance?.SetHome(load, load.GlobalPosition());
        EndJob(job);
    }

    private void TickPendingResumes()
    {
        CommanderMoveService? moves = CommanderMoveService.Instance;
        for (int i = pendingResumes.Count - 1; i >= 0; i--)
        {
            (Unit load, GlobalPosition to, float at) = pendingResumes[i];
            if (load != null && !load.disabled && Time.time < at)
            {
                continue;
            }

            pendingResumes.RemoveAt(i);

            // Given a move order meanwhile, or let go of the job: the player has it now.
            if (load == null || load.disabled || load.IsSlung() || !CommanderEconomyService.IsBuilder(load)
                || moves?.TryGetOrder(load, out _, out _, out _) == true)
            {
                continue;
            }

            if (CommanderEnemyCommanderService.IsAiFaction(load.NetworkHQ, CommanderGameAccess.GetLocalHq()))
            {
                CommanderEconomyService.ResumeBuilder(load);
                continue;
            }

            CommanderGameAccess.TrySetDestination(load, to);
        }
    }

    private void EndJob(SlingJob job)
    {
        jobs.Remove(job.Helo);
        if (ReferenceEquals(lzHelo, job.Helo))
        {
            lzHelo = null;
        }

        Aircraft helo = job.Helo;
        if (helo != null && !helo.disabled)
        {
            if (helo.IsAutoHoverEnabled())
            {
                helo.GetControlsFilter().SetAutoHover(false);
            }

            helo.SetFlightAssistToDefault();

            // A player's helicopter holds over the drop for its next order. Left without one, the
            // base game flew it home the moment the load was off, and kept it in that landing.
            FactionHQ? owner = helo.NetworkHQ;
            if (owner != null && !CommanderEnemyCommanderService.IsAiFaction(owner, CommanderGameAccess.GetLocalHq()))
            {
                GlobalPosition hold = helo.GlobalPosition();
                CommanderNetwork.RunAs(owner, () => CommanderAirCommandService.Instance?.SetAircraftOrder(helo, hold, null, false));
            }
        }

        Unit load = job.Load;
        if (load != null && !load.disabled && !load.IsSlung())
        {
            // An AI builder goes back through its blueprint's own send, which only drives off road
            // within 1.5 km, so one set down short of a far blueprint does not drive straight over the
            // mountains.
            bool ai = CommanderEnemyCommanderService.IsAiFaction(load.NetworkHQ, CommanderGameAccess.GetLocalHq());
            if (load is GroundVehicle vehicle)
            {
                CommanderRepairService.KeepAtCommandedDestination(vehicle);

                // A builder flown in drives off road, as one a cargo run brings does.
                if (job.Carrying && !ai && CommanderRepairService.IsRepairUnit(load))
                {
                    CommanderDirectPathService.ForceEnabled(vehicle);
                }
            }

            // Set down, a vehicle stays on the LZ instead of resuming its old order. Only a builder with a
            // blueprint carries on, and one never picked up resumes where it left off.
            bool resume = !job.Carrying || CommanderEconomyService.IsBuilder(load);

            // A builder ordered on the instant it is released stands still: hold the order until it settles.
            bool delayed = job.Carrying && resume && (job.Resume.HasValue || ai);
            if (delayed)
            {
                pendingResumes.Add((load, job.Resume ?? load.GlobalPosition(), Time.time + BuilderResumeDelaySeconds));
            }

            CommanderGameAccess.TrySetDestination(load, resume && !delayed && job.Resume.HasValue ? job.Resume.Value : load.GlobalPosition());
        }
    }

    private sealed class SpawnLift
    {
        internal SpawnLift(VehicleDefinition definition, GlobalPosition drop)
        {
            Definition = definition;
            Drop = drop;
        }

        internal VehicleDefinition Definition { get; }

        internal GlobalPosition Drop { get; }

        /// <summary>Where it holds to take the load on, fixed when it first gets near the drop.</summary>
        internal GlobalPosition HoverPoint { get; set; }

        /// <summary><c>Time.time</c> it started holding there, 0 while still flying out.</summary>
        internal float HoverSince { get; set; }

        /// <summary>The flight out's obstacle scan, as on <see cref="SlingJob"/>.</summary>
        internal float ClearTopY { get; set; }

        internal float NextScanAt { get; set; }
    }

    private sealed class SlingJob
    {
        internal SlingJob(Aircraft helo, SlingloadHook hook, Unit load, GlobalPosition? resume, float startedAt)
        {
            Helo = helo;
            Hook = hook;
            Load = load;
            Resume = resume;
            StartedAt = startedAt;
            HoverHeight = PickupHoverHeight;
        }

        internal Aircraft Helo { get; }

        internal SlingloadHook Hook { get; }

        internal Unit Load { get; }

        /// <summary>A RECOVERY pickup of a downed pilot rather than a vehicle lift.</summary>
        internal bool Rescue { get; set; }

        /// <summary>The load was spawned hanging at the rope's full length, outside the usual reach.</summary>
        internal bool SpawnedOnHook { get; set; }

        /// <summary>The base game's airdrop parachute is off the spawned load, or it never got one.</summary>
        internal bool ChuteCleared { get; set; }

        /// <summary>Where the vehicle was driving before the pickup.</summary>
        internal GlobalPosition? Resume { get; }

        internal float StartedAt { get; }

        /// <summary>Where the load goes, once the player has said: SET LZ, a right-click, or here.</summary>
        internal GlobalPosition Drop { get; set; }

        internal bool HasLz { get; set; }

        /// <summary>Where a loaded helicopter with no LZ yet waits: over the pickup.</summary>
        internal GlobalPosition HoldPoint { get; set; }

        internal bool Carrying { get; set; }

        /// <summary>The load hangs clear of the ground (with hysteresis, see LoadClearMeters).</summary>
        internal bool LoadClear { get; set; }

        internal float GroundedSince { get; set; }

        /// <summary>When full collective without climbing began while lifting off; -1 when not.</summary>
        internal float StrainSince { get; set; } = -1f;

        /// <summary>Rope plus vehicle plus margin: the hover height that keeps the load off the ground.</summary>
        internal float CarryHeight { get; set; }

        /// <summary>Coming down where it is: the player's SET IT DOWN HERE, or a stall.</summary>
        internal bool SettingDown { get; set; }

        internal float HoverHeight { get; set; }

        internal float ProgressCheckAt { get; set; }

        internal float ProgressDistance { get; set; }

        internal float ProgressAltitude { get; set; }

        /// <summary>In the set-down, coming straight down from the height it arrived at.</summary>
        internal bool Lowering { get; set; }

        /// <summary>Last obstacle scan: the top of the highest thing ahead, above sea level.</summary>
        internal float ClearTopY { get; set; }

        internal float NextScanAt { get; set; }

        /// <summary>The smoothed carrying height, above sea level: up at once, down at SinkRate.</summary>
        internal float CruiseY { get; set; }

        /// <summary>The height this frame's hover was asked for, above sea level, for the log.</summary>
        internal float TargetY { get; set; }
    }

    /// <summary>A guest's copy of one of its lifts, as the host last described it.</summary>
    private sealed class RemoteLift
    {
        internal RemoteLift(bool carrying, string label, bool hasLz, GlobalPosition drop)
        {
            Carrying = carrying;
            Label = label;
            HasLz = hasLz;
            Drop = drop;
        }

        internal bool Carrying { get; }

        internal string Label { get; }

        internal bool HasLz { get; }

        internal GlobalPosition Drop { get; }
    }
}
