using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// RECOVERY: a hook helicopter on standing duty that flies out to downed pilots, winches them
/// aboard, brings them to the nearest base its faction holds, refuels there and waits for the next
/// one, until it is lost or the player gives it another order. Its own pilot back home pays
/// <see cref="RescueRewardFunds"/>; an enemy pilot is interrogated, which puts every enemy unit
/// within <see cref="IntelRadiusMeters"/> of where he was picked up on the faction's radar, once.
/// </summary>
/// <remarks>
/// The pickup is the sling lift's (<see cref="CommanderSlingLiftService.TryStartRescue"/>) and the
/// winch is the base game's own pilot rescue, which ends in <c>PilotDismounted.Capture</c>: the
/// pilot is gone from the world the moment he is aboard, so "aboard" is this service's bookkeeping,
/// recorded off a prefix on that method. Going home and sitting on the deck is an Air Command
/// <c>Hold</c> landing order; Hold is not serviced, so the refuel is done here once parked.
/// </remarks>
internal sealed class CommanderRecoveryService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Faction money is in millions: this is 20 M.</summary>
    internal const float RescueRewardFunds = 20f;

    internal const float IntelRadiusMeters = 25000f;

    private const float TickSeconds = 1f;

    /// <summary>Below this a helicopter goes home to refuel instead of starting another pickup.</summary>
    private const float MinSortieFuel = 0.4f;

    /// <summary>Parked, it tops up to this before it counts as ready.</summary>
    private const float RefuelBelow = 0.95f;

    private const float RefuelRetrySeconds = 15f;

    /// <summary>A pilot this high is still under his parachute.</summary>
    private const float LandedPilotAltitude = 3f;

    /// <summary>How long a delivery toast stays in the guest sync: several syncs, so one dropped
    /// packet or a slow reconnect does not lose it.</summary>
    private const float ToastKeepSeconds = 30f;

    /// <summary>A travel point this close to a downed pilot is an order to pick him up.</summary>
    private const float PickupClickMeters = 150f;

    private readonly Dictionary<Aircraft, Duty> duties = new();
    private readonly List<Aircraft> lost = new();
    private readonly List<PilotDismounted> pilots = new();

    /// <summary>
    /// The airframe each downed pilot ejected from, noted while the wreck can still be looked up
    /// (<c>PilotDismounted.parentUnit</c>): by the time he is winched aboard it may be gone. Only a
    /// draft reads it, where a pilot brought home returns that airframe to stock.
    /// </summary>
    private readonly Dictionary<PilotDismounted, AircraftDefinition> ejectedFrom = new();

    /// <summary>A guest's view of its duties, as the host last described them: mode and status line.</summary>
    private readonly Dictionary<Aircraft, (RecoveryMode Mode, string Label)> remote = new();

    /// <summary>Host: delivery toasts owed to guest factions, numbered so a guest shows each once.</summary>
    private readonly List<GuestToast> toasts = new();
    private float nextTick;
    private int nextToastId;
    private int lastSeenToast;

    internal CommanderRecoveryService()
    {
        Instance = this;
        CommanderNetwork.OnHost("recovery", HostSetMode);
        CommanderNetwork.OnHost("recovery.pickup", HostPickup);
        CommanderNetwork.OnSync(SyncDuties);
        CommanderNetwork.OnGuest("recovery.state", ApplyDuties);
        CommanderNetwork.OnGuest("recovery.toast", ShowToast);
    }

    internal enum RecoveryMode
    {
        Off,
        Team,
        Enemy,
        Both,
    }

    internal static CommanderRecoveryService? Instance { get; private set; }

    public void TickPersistent()
    {
        // A draft notes every downed pilot's airframe from the moment he is down, duty or no duty:
        // the wreck that says what he flew may be gone before anyone is sent for him.
        bool draft = CommanderDraftService.IsDraftMission;
        if ((duties.Count == 0 && !draft) || !CommanderScheduler.IsDue(ref nextTick, TickSeconds))
        {
            return;
        }

        if (duties.Count == 0)
        {
            CollectPilots();
            return;
        }

        lost.Clear();
        foreach (Aircraft helo in duties.Keys)
        {
            // Not the hook: a fresh airframe's stores spawn a moment after it registers.
            if (helo == null || helo.disabled)
            {
                lost.Add(helo!);
            }
        }

        for (int i = 0; i < lost.Count; i++)
        {
            duties.Remove(lost[i]);
        }

        CollectPilots();
        foreach (KeyValuePair<Aircraft, Duty> entry in duties)
        {
            // Flown on the host, like every AI pilot; a guest's entry only says which mode it asked for.
            if (entry.Key.IsServer)
            {
                CommanderNetwork.RunAs(entry.Key.NetworkHQ, () => Review(entry.Key, entry.Value));
            }
        }
    }

    public void ResetSession()
    {
        duties.Clear();
        pilots.Clear();
        ejectedFrom.Clear();
        remote.Clear();
        toasts.Clear();
        nextToastId = 0;
        lastSeenToast = 0;
    }

    /// <summary>From the prefix on <c>PilotDismounted.Capture</c>: a recovery helicopter just winched him aboard.</summary>
    internal static void NotePilotCaptured(PilotDismounted pilot, Unit capturingUnit)
    {
        if (Instance == null
            || capturingUnit is not Aircraft helo
            || !Instance.duties.TryGetValue(helo, out Duty duty)
            || pilot == null)
        {
            return;
        }

        bool friendly = ReferenceEquals(pilot.NetworkHQ, helo.NetworkHQ);
        AircraftDefinition? airframe = Instance.NoteAirframe(pilot);
        Instance.ejectedFrom.Remove(pilot);
        duty.Aboard.Add(new Passenger(friendly, pilot.GlobalPosition(), airframe));
        CommanderPlugin.Log.LogInfo($"Recovery: {CommanderGameAccess.GetUnitLabel(helo)} has "
            + $"{(friendly ? "a friendly" : "an enemy")} pilot aboard.");
    }

    internal RecoveryMode GetMode(Aircraft helo)
    {
        if (CommanderNetwork.IsGuest)
        {
            return remote.TryGetValue(helo, out (RecoveryMode Mode, string Label) seen) ? seen.Mode : RecoveryMode.Off;
        }

        return duties.TryGetValue(helo, out Duty duty) ? duty.Mode : RecoveryMode.Off;
    }

    /// <summary>The selection bar's buttons and the AIR COMMAND launch. Off ends the duty.</summary>
    internal void SetMode(Aircraft helo, RecoveryMode mode)
    {
        if (helo == null || GetMode(helo) == mode)
        {
            return;
        }

        if (CommanderNetwork.IsGuest)
        {
            // Shown at once; the host's next sync replaces it with the real status.
            if (mode == RecoveryMode.Off)
            {
                remote.Remove(helo);
            }
            else
            {
                remote[helo] = (mode, $"RECOVERY {mode.ToString().ToUpperInvariant()}   starting");
            }

            CommanderNetwork.Forward("recovery", CommanderNetwork.Id(helo), CommanderNetwork.Num((int)mode));
            return;
        }

        if (mode != RecoveryMode.Off && GetRefusal(helo) is string refusal)
        {
            CommanderNotice.Fail(refusal);
            return;
        }

        Apply(helo, mode);
    }

    /// <summary>A RECOVERY launch from AIR COMMAND, at registration: no hook test, the stores are
    /// still spawning.</summary>
    internal void Assign(Aircraft helo, RecoveryMode mode)
    {
        Apply(helo, mode);
    }

    /// <summary>
    /// A right-click with a rescue helicopter selected, on or next to a downed pilot: that pilot
    /// next, whichever side he is on and whatever the mode says. False for anything else.
    /// </summary>
    internal bool TryOrderPickup(Aircraft helo, Unit? clicked, GlobalPosition point)
    {
        if (GetMode(helo) == RecoveryMode.Off)
        {
            return false;
        }

        PilotDismounted? pilot = clicked as PilotDismounted ?? FindPilotNear(point);
        if (pilot == null)
        {
            return false;
        }

        CommanderOrderPing.Show(pilot.GlobalPosition(), attack: false, rescue: true);
        if (!CommanderNetwork.Forward("recovery.pickup", CommanderNetwork.Id(helo), CommanderNetwork.Id(pilot)))
        {
            StartOrderedPickup(helo, pilot);
        }

        return true;
    }

    /// <summary>Any other order the player gives this helicopter ends the duty.</summary>
    internal void Stop(Aircraft helo)
    {
        if (helo != null && GetMode(helo) != RecoveryMode.Off)
        {
            SetMode(helo, RecoveryMode.Off);
        }
    }

    internal string GetStatusLabel(Aircraft helo)
    {
        if (CommanderNetwork.IsGuest)
        {
            return remote.TryGetValue(helo, out (RecoveryMode Mode, string Label) seen) ? seen.Label : "RECOVERY   off";
        }

        if (!duties.TryGetValue(helo, out Duty duty))
        {
            return "RECOVERY   off";
        }

        string mode = duty.Mode.ToString().ToUpperInvariant();
        if (CommanderSlingLiftService.Instance?.TryGetRescue(helo, out bool winching) == true)
        {
            return winching ? $"RECOVERY {mode}   winching the pilot aboard" : $"RECOVERY {mode}   flying to a downed pilot";
        }

        int aboard = duty.Aboard.Count;
        if (aboard > 0)
        {
            return $"RECOVERY {mode}   returning to base with {aboard} pilot{(aboard == 1 ? string.Empty : "s")}";
        }

        return $"RECOVERY {mode}   standing by for downed pilots";
    }

    /// <summary>Rescue or transport, never both: why this helicopter cannot go on rescue duty.</summary>
    private static string? GetRefusal(Aircraft helo)
    {
        if (CommanderSlingLiftService.GetHook(helo) == null)
        {
            return "Rescue needs a helicopter carrying the sling load hook.";
        }

        List<WeaponStation> stations = helo.weaponStations;
        for (int i = 0; i < stations.Count; i++)
        {
            WeaponInfo? info = stations[i]?.WeaponInfo;
            if (info != null && (info.cargo || info.troops))
            {
                return $"{CommanderGameAccess.GetUnitLabel(helo)} is set up as a transport. "
                    + "Rescue helicopters carry the sling hook and no cargo.";
            }
        }

        CommanderSlingLiftService? sling = CommanderSlingLiftService.Instance;
        if (CommanderSupplyHeliService.Instance?.IsCargoRunAircraft(helo) == true
            || (sling != null && sling.IsLifting(helo) && !sling.TryGetRescue(helo, out _)))
        {
            return $"{CommanderGameAccess.GetUnitLabel(helo)} is busy transporting. Finish or cancel that first.";
        }

        return null;
    }

    /// <summary>The nearest downed pilot to a clicked point, within <see cref="PickupClickMeters"/>.</summary>
    private static PilotDismounted? FindPilotNear(GlobalPosition point)
    {
        PilotDismounted? best = null;
        float bestDistance = PickupClickMeters;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.factionUnits == null)
            {
                continue;
            }

            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit) && unit is PilotDismounted pilot && !pilot.disabled)
                {
                    float distance = HorizontalDistance(point, pilot.GlobalPosition());
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = pilot;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Host: drop whatever pickup is under way and go for this pilot.</summary>
    private static void StartOrderedPickup(Aircraft helo, PilotDismounted pilot)
    {
        CommanderSlingLiftService? sling = CommanderSlingLiftService.Instance;
        if (sling == null || pilot == null || pilot.disabled)
        {
            return;
        }

        if (sling.TryGetRescue(helo, out _))
        {
            sling.CancelJob(helo);
        }

        bool started = false;
        CommanderNetwork.RunAs(helo.NetworkHQ, () => started = sling.TryStartRescue(helo, pilot));
        if (!started)
        {
            CommanderNotice.Fail($"{CommanderGameAccess.GetUnitLabel(pilot)} cannot be picked up: "
                + "another helicopter is already going for him.");
        }
    }

    private static bool IsParkedAtHeldBase(Aircraft helo, FactionHQ hq, out Airbase? field)
    {
        field = null;
        return CommanderAirCommandService.Instance?.TryGetParkedField(helo, out field) == true
            && field != null
            && ReferenceEquals(field.CurrentHQ, hq);
    }

    private static void Deliver(Aircraft helo, Duty duty, FactionHQ hq, Airbase field)
    {
        int rescued = 0;
        int interrogated = 0;
        int revealed = 0;
        bool draft = CommanderDraftService.IsDraftMission;
        string airframes = string.Empty;
        for (int i = 0; i < duty.Aboard.Count; i++)
        {
            Passenger passenger = duty.Aboard[i];
            if (passenger.Friendly)
            {
                // A draft has no money, and a trained crew is the scarce thing: the pilot's
                // airframe type goes back into stock, the one way an air wing grows mid-match.
                if (draft && passenger.Airframe != null)
                {
                    hq.ModifyUnitSupply(passenger.Airframe, 1);
                    airframes += (airframes.Length == 0 ? string.Empty : ", ")
                        + (!string.IsNullOrWhiteSpace(passenger.Airframe.unitName) ? passenger.Airframe.unitName : passenger.Airframe.jsonKey);
                }
                else if (!draft)
                {
                    hq.AddFunds(RescueRewardFunds);
                }

                rescued++;
            }
            else
            {
                revealed += RevealAround(hq, passenger.PickedUpAt);
                interrogated++;
            }
        }

        duty.Aboard.Clear();
        string label = CommanderGameAccess.GetUnitLabel(helo);
        string where = CommanderCaptureService.GetAirbaseLabel(field);
        string text = rescued == 0
            ? string.Empty
            : !draft
                ? $"{label} brought {rescued} pilot{(rescued == 1 ? string.Empty : "s")} home to {where}: "
                    + $"+{CommanderEconomyService.FundsLabel(RescueRewardFunds * rescued)}."
                : airframes.Length > 0
                    ? $"{label} brought {rescued} pilot{(rescued == 1 ? string.Empty : "s")} home to {where}: back in stock {airframes}."
                    : $"{label} brought {rescued} pilot{(rescued == 1 ? string.Empty : "s")} home to {where}.";
        if (interrogated > 0)
        {
            text += (text.Length > 0 ? " " : string.Empty)
                + $"Captured pilot interrogated at {where}: {revealed} enemy positions on radar.";
        }

        CommanderPlugin.Log.LogInfo($"Recovery: {text}");

        // The real local faction, not GetLocalHq: this runs under RunAs(helo's faction), where that
        // answers the helicopter's own side and a guest's toast landed on the host's screen.
        if (GameManager.GetLocalHQ(out FactionHQ localHq) && ReferenceEquals(helo.NetworkHQ, localHq))
        {
            CommanderAlertService.Instance?.NotifySupply(text, helo);
        }
        else
        {
            // A guest's helicopter: its toast goes out with the next sync to that faction.
            Instance?.toasts.Add(new GuestToast(hq, ++Instance.nextToastId, text, helo, Time.unscaledTime));
        }
    }

    /// <summary>A snapshot: every enemy unit in the radius goes into the tracking database where it
    /// stands now, and is a lost contact frozen there a few seconds later.</summary>
    private static int RevealAround(FactionHQ hq, GlobalPosition center)
    {
        int count = 0;
        foreach (FactionHQ other in FactionRegistry.GetAllHQs())
        {
            if (other == null || ReferenceEquals(other, hq) || other.factionUnits == null)
            {
                continue;
            }

            foreach (PersistentID id in other.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit)
                    && unit != null
                    && !unit.disabled
                    && unit is not PilotDismounted
                    && unit is not Missile
                    && HorizontalDistance(unit.GlobalPosition(), center) <= IntelRadiusMeters)
                {
                    hq.RpcUpdateTrackingInfo(id);
                    count++;
                }
            }
        }

        return count;
    }

    private static float HorizontalDistance(GlobalPosition a, GlobalPosition b)
    {
        Vector3 offset = a - b;
        offset.y = 0f;
        return offset.magnitude;
    }

    private static void GoHome(Aircraft helo, FactionHQ hq)
    {
        CommanderAirCommandService? air = CommanderAirCommandService.Instance;
        if (air == null
            || (air.TryGetLandingOrder(helo, out Airbase? going, out _) && going != null && ReferenceEquals(going.CurrentHQ, hq)))
        {
            return;
        }

        Airbase? home = CommanderAirCommandService.FindNearestOwnedAirbase(helo);
        if (home != null)
        {
            // Quiet: the duty's own status line says it is heading home, and under RunAs the toast
            // would go to the host's screen for a guest's helicopter.
            air.SetAircraftLandingOrder(helo, home, CommanderAirCommandService.LandingIntent.Hold, append: false, announce: false);
        }
    }

    private void Apply(Aircraft helo, RecoveryMode mode)
    {
        if (mode == RecoveryMode.Off)
        {
            duties.Remove(helo);
            if (CommanderSlingLiftService.Instance?.TryGetRescue(helo, out _) == true)
            {
                CommanderSlingLiftService.Instance.CancelJob(helo);
            }

            return;
        }

        if (duties.TryGetValue(helo, out Duty duty))
        {
            duty.Mode = mode;
        }
        else
        {
            duties[helo] = new Duty(mode);
            CommanderPlugin.Log.LogInfo($"Recovery: {CommanderGameAccess.GetUnitLabel(helo)} on {mode} recovery duty.");
        }
    }

    /// <summary>Host, per guest: that faction's duties and any delivery toasts still fresh.</summary>
    private void SyncDuties(FactionHQ hq, List<string[]> messages)
    {
        List<string> args = new() { "recovery.state" };
        foreach (KeyValuePair<Aircraft, Duty> entry in duties)
        {
            if (entry.Key != null && ReferenceEquals(entry.Key.NetworkHQ, hq))
            {
                args.Add(CommanderNetwork.Id(entry.Key));
                args.Add(CommanderNetwork.Num((int)entry.Value.Mode));
                args.Add(GetStatusLabel(entry.Key));
            }
        }

        messages.Add(args.ToArray());
        toasts.RemoveAll(static toast => Time.unscaledTime - toast.At > ToastKeepSeconds);
        for (int i = 0; i < toasts.Count; i++)
        {
            GuestToast toast = toasts[i];
            if (ReferenceEquals(toast.Hq, hq))
            {
                messages.Add(new[]
                {
                    "recovery.toast",
                    CommanderNetwork.Num(toast.Id),
                    toast.Text,
                    toast.Helo != null ? CommanderNetwork.Id(toast.Helo) : string.Empty,
                });
            }
        }
    }

    private void ApplyDuties(string[] args)
    {
        remote.Clear();
        for (int i = 1; i + 2 < args.Length; i += 3)
        {
            if (CommanderNetwork.TryUnit(args, i, out Unit unit) && unit is Aircraft helo)
            {
                remote[helo] = ((RecoveryMode)CommanderNetwork.Int(args, i + 1), CommanderNetwork.Arg(args, i + 2));
            }
        }
    }

    private void ShowToast(string[] args)
    {
        int id = CommanderNetwork.Int(args, 1);
        if (id <= lastSeenToast)
        {
            return;
        }

        lastSeenToast = id;
        Unit? helo = CommanderNetwork.TryUnit(args, 3, out Unit found) ? found : null;
        CommanderAlertService.Instance?.NotifySupply(CommanderNetwork.Arg(args, 2), helo);
    }

    private void HostPickup(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && unit is Aircraft helo
            && ReferenceEquals(helo.NetworkHQ, hq)
            && GetMode(helo) != RecoveryMode.Off
            && CommanderNetwork.TryUnit(args, 2, out Unit target)
            && target is PilotDismounted pilot)
        {
            StartOrderedPickup(helo, pilot);
        }
    }

    private void HostSetMode(FactionHQ hq, string[] args)
    {
        RecoveryMode mode = (RecoveryMode)CommanderNetwork.Int(args, 2);
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && unit is Aircraft helo
            && ReferenceEquals(helo.NetworkHQ, hq)
            && System.Enum.IsDefined(typeof(RecoveryMode), mode))
        {
            SetMode(helo, mode);
        }
    }

    /// <summary>One sweep of every downed pilot on the ground, shared by all the duties this tick.</summary>
    private void CollectPilots()
    {
        pilots.Clear();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.factionUnits == null)
            {
                continue;
            }

            foreach (PersistentID id in hq.factionUnits)
            {
                // Beside one of his own fields the game walks him home by itself in a few seconds.
                if (id.TryGetUnit(out Unit unit)
                    && unit is PilotDismounted pilot
                    && !pilot.disabled
                    && !pilot.IsSlung()
                    && pilot.radarAlt < LandedPilotAltitude
                    && !hq.AnyNearAirbase(pilot.transform.position, out _))
                {
                    pilots.Add(pilot);
                    NoteAirframe(pilot);
                }
            }
        }
    }

    private AircraftDefinition? NoteAirframe(PilotDismounted pilot)
    {
        if (ejectedFrom.TryGetValue(pilot, out AircraftDefinition known))
        {
            return known;
        }

        if (UnitRegistry.TryGetUnit(pilot.parentUnit, out Aircraft aircraft)
            && aircraft != null
            && aircraft.definition != null)
        {
            ejectedFrom[pilot] = aircraft.definition;
            return aircraft.definition;
        }

        return null;
    }

    private void Review(Aircraft helo, Duty duty)
    {
        CommanderSlingLiftService? sling = CommanderSlingLiftService.Instance;
        FactionHQ? hq = helo.NetworkHQ;
        if (sling == null || hq == null || sling.IsLifting(helo))
        {
            return;
        }

        if (IsParkedAtHeldBase(helo, hq, out Airbase? field))
        {
            if (duty.Aboard.Count > 0)
            {
                Deliver(helo, duty, hq, field!);
            }

            if (helo.GetFuelLevel() < RefuelBelow && Time.time >= duty.NextRefuelAt)
            {
                duty.NextRefuelAt = Time.time + RefuelRetrySeconds;
                if (CommanderSupplyStoreService.Instance?.TryResupplyAircraft(helo, field, null, out string why) == false)
                {
                    CommanderPlugin.Log.LogInfo($"Recovery: {CommanderGameAccess.GetUnitLabel(helo)} cannot refuel: {why}.");
                }
            }
        }

        PilotDismounted? target = duty.Aboard.Count == 0 && helo.GetFuelLevel() >= MinSortieFuel
            ? FindPilot(helo, duty.Mode)
            : null;
        if (target != null && sling.TryStartRescue(helo, target))
        {
            CommanderPlugin.Log.LogInfo($"Recovery: {CommanderGameAccess.GetUnitLabel(helo)} going for "
                + $"{CommanderGameAccess.GetUnitLabel(target)}, {HorizontalDistance(helo.GlobalPosition(), target.GlobalPosition()) / 1000f:0.0} km out.");
            return;
        }

        GoHome(helo, hq);
    }

    private PilotDismounted? FindPilot(Aircraft helo, RecoveryMode mode)
    {
        PilotDismounted? best = null;
        float bestDistance = float.MaxValue;
        GlobalPosition from = helo.GlobalPosition();
        for (int i = 0; i < pilots.Count; i++)
        {
            PilotDismounted pilot = pilots[i];
            bool friendly = ReferenceEquals(pilot.NetworkHQ, helo.NetworkHQ);
            if ((friendly ? mode == RecoveryMode.Enemy : mode == RecoveryMode.Team) || CommanderSlingLiftService.Instance?.IsLoadTaken(pilot) == true)
            {
                continue;
            }

            float distance = HorizontalDistance(from, pilot.GlobalPosition());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = pilot;
            }
        }

        return best;
    }

    private readonly struct Passenger
    {
        internal Passenger(bool friendly, GlobalPosition pickedUpAt, AircraftDefinition? airframe)
        {
            Friendly = friendly;
            PickedUpAt = pickedUpAt;
            Airframe = airframe;
        }

        internal bool Friendly { get; }

        /// <summary>What he flew, when it could be found. See <c>ejectedFrom</c>.</summary>
        internal AircraftDefinition? Airframe { get; }

        internal GlobalPosition PickedUpAt { get; }
    }

    private sealed class GuestToast
    {
        internal GuestToast(FactionHQ hq, int id, string text, Aircraft helo, float at)
        {
            Hq = hq;
            Id = id;
            Text = text;
            Helo = helo;
            At = at;
        }

        internal FactionHQ Hq { get; }

        internal int Id { get; }

        internal string Text { get; }

        internal Aircraft? Helo { get; }

        internal float At { get; }
    }

    private sealed class Duty
    {
        internal Duty(RecoveryMode mode)
        {
            Mode = mode;
        }

        internal RecoveryMode Mode { get; set; }

        /// <summary>Pilots winched aboard and not yet delivered.</summary>
        internal List<Passenger> Aboard { get; } = new();

        internal float NextRefuelAt { get; set; }
    }
}
