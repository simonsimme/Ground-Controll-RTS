using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Ground Control Tutorial: the duel's map against a passive enemy, walked through one step at a
/// time. Each step watches for the thing it asks for (a supply purchase, a blueprint, a captured
/// base) and moves on by itself once it sees it, so the player learns by doing it rather than by
/// reading about it. It only reads state the other services already keep.
/// </summary>
/// <remarks>
/// A separate mission rather than hints in every match. Local to each player, nothing is networked.
/// </remarks>
internal sealed class CommanderTutorialService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float CheckSeconds = 0.5f;
    private const float DoneHoldSeconds = 2f;
    private const float CameraTravelMeters = 400f;
    private const float SupplyBoughtKg = 50f;
    private const float NearObjectiveMeters = 1000f;

    private readonly List<CommanderSupplyStoreService.BaseSupply> supplyScratch = new();
    private readonly Step[] steps;
    private float nextCheckAt;
    private GlobalPosition cameraStart;
    private float supplyStart;
    private int vehiclesStart;
    private int pingsStart;
    private bool started;

    internal CommanderTutorialService()
    {
        Instance = this;
        steps = new[]
        {
            new Step(
                "OPEN COMMAND MODE",
                () => $"Press the CMD button on the left edge of the screen, or {CommanderSettings.ToggleCommander}, to open command mode. Everything in this tutorial happens from there.",
                CommandModeOpen),
            new Step(
                "MOVE THE CAMERA",
                () => $"This is command mode. Move the camera with {CommanderSettings.CameraForward} {CommanderSettings.CameraLeft} {CommanderSettings.CameraBackward} {CommanderSettings.CameraRight}, climb and dive with {CommanderSettings.CameraUp} and {CommanderSettings.CameraDown}, zoom with the mouse wheel and hold the middle mouse button to look around. Hold {CommanderSettings.CameraBoost} to go faster.",
                CameraMoved),
            new Step(
                "BUY FUEL AND AMMUNITION",
                () => "Your depots start almost empty, and every unit you put on the field draws fuel and ammunition from them. Open SUPPLY on the command bar at the top and press BUY for fuel and for ammunition at K92.",
                SupplyBought),
            new Step(
                "BUILD AN INCOME",
                () => $"Money comes from buildings. Press {CommanderSettings.OpenBuild} for BUILD, open ECONOMY, buy a Gold Mine and click a green spot near your strip. {CommanderSettings.RotateBuilding} turns it. A cheaper Fuel Exchange counts too.",
                IncomePlaced),
            new Step(
                "LET THE BUILDERS WORK",
                () => "The click paid for a blueprint. Your two Jacknife repair vehicles drive out and build it on their own: the more of them on site, the faster it rises. It pays you every minute once it stands.",
                IncomeBuilt,
                BlueprintSite),
            new Step(
                "BUY A VEHICLE",
                () => $"Press {CommanderSettings.OpenGroundUnits} to open your nearest vehicle depot and click a vehicle to buy it. Pick one that carries troops, an APC or an IFV, because only those can take a base.",
                VehicleBought),
            new Step(
                "GIVE AN ORDER",
                () => $"Click your new vehicle to select it, then right-click the ground to send it there. Hold {CommanderSettings.QueueWaypoint} while right-clicking to queue a route of several points. Right-click an enemy to attack it.",
                VehicleOrdered),
            new Step(
                "MAKE A CONTROL GROUP",
                () => $"With the vehicle selected press {CommanderSettings.AssignGroupModifier} + 1 to store it as group 1. Press {CommanderSettings.ClearSelection} to deselect, then 1 to get it back.",
                GroupStored),
            new Step(
                "CAPTURE MARIS AIRPORT",
                () => "Maris Airport, marked in yellow, belongs to nobody. Drive your vehicle toward it: within 4 km the base is marked CAPTURABLE. Right-click that marker, or the ground beside the base, to send your vehicle there.",
                CaptureOrderedOnMaris,
                () => BaseObjective("Maris")),
            new Step(
                "SPEED UP TIME",
                () => "The drive takes a while. Open SETTINGS on the command bar and press 2x or 4x at the top of the window to run the game faster.",
                SpedUp,
                () => BaseObjective("Maris")),
            new Step(
                "FOLLOW YOUR VEHICLE",
                () => $"While you wait, watch it drive. Select your vehicle and tap {CommanderSettings.CameraCenterFollow} to center the camera on it, or hold it to center and follow. The map window's CAMERA row has the same FOLLOW and CENTER buttons. Keep the vehicle inside the ring until the bar fills and Maris is yours.",
                HoldsMaris,
                () => BaseObjective("Maris")),
            new Step(
                "BACK TO 1X",
                () => "Maris is yours. Open SETTINGS again and press 1x to put the game back to normal speed.",
                NormalSpeed),
            new Step(
                "LAUNCH AN AIRCRAFT",
                () => $"Press {CommanderSettings.OpenAirCommand} for AIR COMMAND. Pick an aircraft, keep the mission on HYBRID, press LAUNCH and click the map where it should patrol. It taxis out and takes off by itself. Air Command charges for its munitions too.",
                AircraftLaunched),
            new Step(
                "PING FOR YOUR TEAM",
                () => $"Point at a spot or an enemy and press {CommanderSettings.PingKey}. Everyone on your team sees the ping: here, on the map, and on the HUD while they fly.",
                Pinged),
            new Step(
                "COMMAND FROM THE COCKPIT",
                () => $"Press {CommanderSettings.ToggleCommander} to leave command mode, then press M to bring up the game's regular map. Pick an aircraft at K92 there and take off. In the air, press {CommanderSettings.ToggleCommander} again: the autopilot holds your course while you command. Your pings, attack targets and contested bases show on your HUD.",
                CommandingWhileFlying),
            new Step(
                "DESTROY THE AIR DEFENCE",
                () => $"Two anti-air guns guard Dustbowl Highway Strip, the enemy's only base, and they are now on your map. Launch an aircraft from AIR COMMAND with an air-to-ground loadout (a CAS or STRIKE preset). Select it, right-click the first gun, then hold {CommanderSettings.QueueWaypoint} and right-click the second so it attacks both.",
                EnemyAirDefenceGone,
                EnemyAirDefenceObjective),
            new Step(
                "WIN",
                () => "The guns are down. Send your troop carriers to Dustbowl Highway Strip and capture it to win. Everything you just did is the whole game: earn, supply, build, capture.",
                HoldsDustbowl,
                () => BaseObjective("Dustbowl")),
        };
    }

    internal static CommanderTutorialService? Instance { get; private set; }

    /// <summary>True on the shipped tutorial, which is also a duel mission (see <c>IsDuelMission</c>).</summary>
    internal static bool IsTutorialMission =>
        CommanderFeatureGate.MissionName.IndexOf("Ground Control Tutorial", StringComparison.OrdinalIgnoreCase) >= 0;

    internal int StepIndex { get; private set; }

    internal int StepCount => steps.Length;

    internal bool Finished => StepIndex >= steps.Length;

    /// <summary>The current step reads done and the next one starts after a short pause.</summary>
    internal bool ShowingDone { get; private set; }

    internal bool Dismissed { get; set; }

    internal bool Active => IsTutorialMission && started && !Dismissed;

    internal string Title => Finished ? "TUTORIAL COMPLETE" : steps[StepIndex].Title;

    internal string Body => Finished
        ? "That is the whole loop. Upgrade your mine, build a factory, add radar and air defence, and keep your shelves stocked. The other Ground Control missions put a real commander against you."
        : steps[StepIndex].Body();

    private float DoneAt { get; set; }

    public void TickPersistent()
    {
        if (!IsTutorialMission || !CommanderScheduler.IsDueRealtime(ref nextCheckAt, CheckSeconds))
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetPlayerHq();
        if (hq == null)
        {
            return;
        }

        if (!started)
        {
            started = true;
            Enter(0);
        }

        if (Finished)
        {
            return;
        }

        if (ShowingDone)
        {
            if (Time.realtimeSinceStartup - DoneAt >= DoneHoldSeconds)
            {
                Enter(StepIndex + 1);
            }

            return;
        }

        RefreshObjective();
        if (steps[StepIndex].IsDone(hq))
        {
            ShowingDone = true;
            DoneAt = Time.realtimeSinceStartup;
            CommanderPlugin.Log.LogInfo($"Tutorial: step {StepIndex + 1} '{steps[StepIndex].Title}' done.");
        }
    }

    public void ResetSession()
    {
        started = false;
        Dismissed = false;
        ShowingDone = false;
        StepIndex = 0;
        CommanderPingService.ClearObjective();
    }

    internal void Skip()
    {
        if (!Finished)
        {
            Enter(StepIndex + 1);
        }
    }

    private static GlobalPosition CameraPosition()
    {
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        return camera == null ? default : camera.transform.position.ToGlobalPosition();
    }

    private static bool TryFindBase(string name, out Airbase airbase)
    {
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            if (entry.Value != null
                && CommanderCaptureService.GetAirbaseLabel(entry.Value).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                airbase = entry.Value;
                return true;
            }
        }

        airbase = null!;
        return false;
    }

    private static bool Holds(FactionHQ hq, string name)
    {
        return TryFindBase(name, out Airbase airbase) && ReferenceEquals(airbase.CurrentHQ, hq);
    }

    private static bool HoldsMaris(FactionHQ hq)
    {
        return Holds(hq, "Maris");
    }

    private static bool HoldsDustbowl(FactionHQ hq)
    {
        return Holds(hq, "Dustbowl");
    }

    private static (GlobalPosition Position, string Label)? BaseObjective(string name)
    {
        return TryFindBase(name, out Airbase airbase) && airbase.center != null
            ? (CommanderCaptureService.GetHoldPoint(airbase), CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant())
            : null;
    }

    private static bool CommandModeOpen(FactionHQ hq)
    {
        return CommanderPlugin.Instance?.IsCommanderModeActive == true;
    }

    private static bool CaptureOrderedOnMaris(FactionHQ hq)
    {
        return TryFindBase("Maris", out Airbase airbase)
            && (ReferenceEquals(airbase.CurrentHQ, hq)
                || CommanderCaptureService.IsBeingCaptured(airbase)
                || CommanderCaptureService.Instance?.HasCaptureOrder(airbase) == true
                || HasTravelPointNear(hq, airbase));
    }

    /// <summary>
    /// A plain right-click beside the base counts as much as the CAPTURABLE marker: a troop carrier
    /// that stops inside the ring takes it either way.
    /// </summary>
    private static bool HasTravelPointNear(FactionHQ hq, Airbase airbase)
    {
        CommanderMoveService? move = CommanderMoveService.Instance;
        if (move == null)
        {
            return false;
        }

        GlobalPosition center = CommanderCaptureService.GetHoldPoint(airbase);
        float reach = Mathf.Max(airbase.SavedAirbase?.CaptureRange ?? 0f, 500f) + NearObjectiveMeters;
        return CountOwned(hq, unit => IsFightingVehicle(unit)
            && move.TryGetOrder(unit, out IReadOnlyList<GlobalPosition> waypoints, out _, out _)
            && waypoints.Count > 0
            && FastMath.Distance(waypoints[waypoints.Count - 1], center) <= reach) > 0;
    }

    private static bool SpedUp(FactionHQ hq)
    {
        return Time.timeScale > 1.01f || HoldsMaris(hq);
    }

    private static bool NormalSpeed(FactionHQ hq)
    {
        return Mathf.Approximately(Time.timeScale, 1f);
    }

    private static bool IsAirDefenceBuilding(Unit unit)
    {
        return unit is Building
            && unit.weaponStations.Count > 0
            && unit.definition != null
            && unit.definition.roleIdentity.antiAir > 0f;
    }

    /// <summary>The first enemy anti-air building still standing, null once they are all down.</summary>
    private static Unit? NextEnemyAirDefence(FactionHQ hq)
    {
        foreach (FactionHQ other in FactionRegistry.GetAllHQs())
        {
            if (ReferenceEquals(other, hq) || other.factionUnits == null)
            {
                continue;
            }

            foreach (PersistentID id in other.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit) && unit != null && !unit.disabled && IsAirDefenceBuilding(unit))
                {
                    return unit;
                }
            }
        }

        return null;
    }

    private static bool EnemyAirDefenceGone(FactionHQ hq)
    {
        return NextEnemyAirDefence(hq) == null;
    }

    /// <summary>
    /// Marks the next gun, and puts every enemy anti-air building on the player's radar so they can
    /// be clicked as targets from across the map. The one place the tutorial changes the game: a
    /// building does not move, so one tracking entry each stays true for the rest of the match.
    /// </summary>
    private static (GlobalPosition Position, string Label)? EnemyAirDefenceObjective()
    {
        FactionHQ? hq = CommanderGameAccess.GetPlayerHq();
        if (hq == null)
        {
            return null;
        }

        if (hq.IsServer)
        {
            foreach (FactionHQ other in FactionRegistry.GetAllHQs())
            {
                if (ReferenceEquals(other, hq) || other.factionUnits == null)
                {
                    continue;
                }

                foreach (PersistentID id in other.factionUnits)
                {
                    if (id.TryGetUnit(out Unit unit)
                        && unit != null
                        && !unit.disabled
                        && IsAirDefenceBuilding(unit)
                        && hq.GetTrackingData(id) == null)
                    {
                        hq.RpcUpdateTrackingInfo(id);
                    }
                }
            }
        }

        Unit? next = NextEnemyAirDefence(hq);
        return next == null ? null : (next.GlobalPosition(), "AIR DEFENCE");
    }

    private static (GlobalPosition Position, string Label)? BlueprintSite()
    {
        IReadOnlyList<CommanderEconomyService.ConstructionSite>? sites = CommanderEconomyService.Blueprints;
        FactionHQ? hq = CommanderGameAccess.GetPlayerHq();
        for (int i = 0; sites != null && i < sites.Count; i++)
        {
            if (ReferenceEquals(sites[i].Hq, hq) && CommanderEconomyService.IsIncomeKind(sites[i].Kind))
            {
                return (sites[i].Site, "BLUEPRINT");
            }
        }

        return null;
    }

    private static int CountOwned(FactionHQ hq, Func<Unit, bool> match)
    {
        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit != null && !unit.disabled && match(unit))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsFightingVehicle(Unit unit)
    {
        return unit is GroundVehicle && !CommanderRepairService.IsRepairUnit(unit);
    }

    private static bool IncomePlaced(FactionHQ hq)
    {
        return IncomeBuilt(hq)
            || CommanderEconomyService.CountBlueprints(hq, CommanderBuildKind.Mine) > 0
            || CommanderEconomyService.CountBlueprints(hq, CommanderBuildKind.GasTerminal) > 0
            || CommanderEconomyService.CountBlueprints(hq, CommanderBuildKind.FuelExchange) > 0;
    }

    private static bool IncomeBuilt(FactionHQ hq)
    {
        return CountOwned(hq, CommanderEconomyService.IsIncomeBuildingUnit) > 0;
    }

    private static bool VehicleOrdered(FactionHQ hq)
    {
        CommanderMoveService? move = CommanderMoveService.Instance;
        return move != null && CountOwned(hq, unit => IsFightingVehicle(unit) && move.HasOrder(unit)) > 0;
    }

    private static bool GroupStored(FactionHQ hq)
    {
        CommanderGroupService? groups = CommanderGroupService.Instance;
        for (int id = 1; groups != null && id <= CommanderGroupService.GroupCount; id++)
        {
            if (groups.GetGroupCount(id) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AircraftLaunched(FactionHQ hq)
    {
        return CommanderAirCommandService.Instance?.ActiveMissionCount > 0;
    }

    private static bool CommandingWhileFlying(FactionHQ hq)
    {
        return CommanderPlugin.Instance?.IsCommanderModeActive == true
            && GameManager.GetLocalAircraft(out Aircraft aircraft)
            && aircraft != null
            && !aircraft.disabled;
    }

    private bool CameraMoved(FactionHQ hq)
    {
        // Only the RTS camera counts: opening command mode is itself a jump of several kilometres.
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
        {
            cameraStart = CameraPosition();
            return false;
        }

        return FastMath.Distance(CameraPosition(), cameraStart) > CameraTravelMeters;
    }

    private bool SupplyBought(FactionHQ hq)
    {
        return SupplyTotal() > supplyStart + SupplyBoughtKg;
    }

    private bool VehicleBought(FactionHQ hq)
    {
        return CountOwned(hq, IsFightingVehicle) > vehiclesStart;
    }

    private bool Pinged(FactionHQ hq)
    {
        return CommanderPingService.LocalPingCount > pingsStart;
    }

    private float SupplyTotal()
    {
        CommanderSupplyStoreService.Instance?.CollectBaseSupply(supplyScratch);
        float total = 0f;
        for (int i = 0; i < supplyScratch.Count; i++)
        {
            total += supplyScratch[i].FuelKg + supplyScratch[i].AmmoKg;
        }

        return total;
    }

    /// <summary>Takes each step's "before" reading, so a step only passes on something done during it.</summary>
    private void Enter(int index)
    {
        StepIndex = index;
        ShowingDone = false;
        cameraStart = CameraPosition();
        supplyStart = SupplyTotal();
        FactionHQ? hq = CommanderGameAccess.GetPlayerHq();
        vehiclesStart = hq == null ? 0 : CountOwned(hq, IsFightingVehicle);
        pingsStart = CommanderPingService.LocalPingCount;
        RefreshObjective();
    }

    private void RefreshObjective()
    {
        if (!Finished && steps[StepIndex].Objective?.Invoke() is (GlobalPosition, string) objective)
        {
            CommanderPingService.SetObjective(objective.Position, objective.Label);
            return;
        }

        CommanderPingService.ClearObjective();
    }

    private sealed class Step
    {
        internal Step(
            string title,
            Func<string> body,
            Func<FactionHQ, bool> isDone,
            Func<(GlobalPosition Position, string Label)?>? objective = null)
        {
            Title = title;
            Body = body;
            IsDone = isDone;
            Objective = objective;
        }

        internal string Title { get; }

        internal Func<string> Body { get; }

        internal Func<FactionHQ, bool> IsDone { get; }

        internal Func<(GlobalPosition Position, string Label)?>? Objective { get; }
    }
}
