using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace GroundControlRts;

internal sealed class CommanderDirectPathService : ICommanderResetSession
{
    private static readonly FieldInfo? PathfindingUnitField = AccessTools.Field(typeof(PathfindingAgent), "unit");
    private readonly CommanderSelectionService selectionService;
    private readonly HashSet<GroundVehicle> directRouteVehicles = new();

    internal CommanderDirectPathService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
        CommanderNetwork.OnHost("road", HostRoad);
    }

    internal static CommanderDirectPathService? Instance { get; private set; }

    // Not cleared when RTS mode closes: on the host this is every player's ROADS switch.
    public void ResetSession()
    {
        directRouteVehicles.Clear();
    }

    internal static bool TryApplyShortcut(PathfindingAgent pathfinder, GlobalPosition target)
    {
        if (Instance == null
            || PathfindingUnitField?.GetValue(pathfinder) is not GroundVehicle vehicle
            || !Instance.directRouteVehicles.Contains(vehicle))
        {
            return false;
        }

        pathfinder.Shortcut(target);
        return true;
    }

    internal static void ForceEnabled(GroundVehicle? vehicle)
    {
        if (Instance != null && vehicle != null && !vehicle.disabled)
        {
            Instance.SetOffRoad(vehicle, true);
        }
    }

    internal static void Forget(GroundVehicle? vehicle)
    {
        if (Instance != null && vehicle != null)
        {
            Instance.SetOffRoad(vehicle, false);
        }
    }

    internal static bool CanConfigure(Unit? unit)
    {
        return unit is GroundVehicle vehicle
            && !vehicle.disabled
            && CommanderGameAccess.IsFriendlyUnit(vehicle, CommanderGameAccess.GetLocalHq())
            && !CommanderSamSiteService.IsReservedConstructionJacknife(vehicle)
            && !CommanderGameAccess.IsTrailerVehicleDefinition(vehicle.definition as VehicleDefinition);
    }

    internal bool HasConfigurableSelection()
    {
        IReadOnlyList<Unit> selected = selectionService.SelectedUnits;
        for (int i = 0; i < selected.Count; i++)
        {
            if (IsToggleable(selected[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when every configurable selected vehicle drives across country.</summary>
    internal bool IsSelectionOffRoad()
    {
        bool any = false;
        IReadOnlyList<Unit> selected = selectionService.SelectedUnits;
        for (int i = 0; i < selected.Count; i++)
        {
            if (!IsToggleable(selected[i]))
            {
                continue;
            }

            if (!directRouteVehicles.Contains((GroundVehicle)selected[i]))
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>A mixed selection all goes off road; one that is already all off road goes back on.</summary>
    internal void ToggleSelection()
    {
        bool offRoad = !IsSelectionOffRoad();
        IReadOnlyList<Unit> selected = selectionService.SelectedUnits;
        for (int i = 0; i < selected.Count; i++)
        {
            if (!IsToggleable(selected[i]))
            {
                continue;
            }

            GroundVehicle vehicle = (GroundVehicle)selected[i];
            if (SetOffRoad(vehicle, offRoad))
            {
                ReapplyCurrentDestination(vehicle);
            }
        }
    }

    /// <summary>The selected units the ROADS button can act on; haulers and site builders are driven by their own services.</summary>
    private static bool IsToggleable(Unit unit)
    {
        return CanConfigure(unit) && !CommanderMobileEmplacementService.IsReservedHauler(unit);
    }

    private static void ReapplyCurrentDestination(GroundVehicle vehicle)
    {
        UnitCommand? command = vehicle.UnitCommand;
        if (command == null)
        {
            return;
        }

        UnitCommand.Command current = command.GetCommandCached();
        if (current.time > 0f || current.player != null || !current.position.Equals(default))
        {
            command.SetDestination(current.position, current.FromPlayer);
        }
    }

    /// <summary>
    /// True when the flag changed here and the caller should re-path. A path is only ever searched on
    /// the host (`UnitCommand.ServerSetDestination`), so a guest's set is just its ROADS button: the
    /// host keeps the real one and re-paths the vehicle itself.
    /// </summary>
    private bool SetOffRoad(GroundVehicle vehicle, bool offRoad)
    {
        bool changed = offRoad ? directRouteVehicles.Add(vehicle) : directRouteVehicles.Remove(vehicle);
        return !CommanderNetwork.Forward("road", CommanderNetwork.Id(vehicle), CommanderNetwork.Flag(offRoad)) && changed;
    }

    /// <summary>A guest's ROADS switch, on the host, where the vehicle's path is searched.</summary>
    private void HostRoad(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && ReferenceEquals(unit.NetworkHQ, hq)
            && unit is GroundVehicle vehicle
            && !vehicle.disabled
            && SetOffRoad(vehicle, CommanderNetwork.Bool(args, 2)))
        {
            ReapplyCurrentDestination(vehicle);
        }
    }
}
