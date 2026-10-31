using System;
using System.Collections.Generic;
using NuclearOption.Networking;

namespace GroundControlRts;

/// <summary>
/// Income buildings and naval docks a map maker put down in the mission editor's GC RTS tab
/// (<c>CommanderMissionEditorPatches</c>). To the game each is the plain prefab; what makes it a
/// gold mine is its name, "GC Gold Mine", "GC Gold Mine_1" and so on, because the unit's unique name
/// is the one thing of ours a mission file keeps through a save. The host adopts them into the same
/// tables the BUILD window fills, so they pay, level, sync to guests and can be bombed like built ones.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    internal const string MissionNamePrefix = "GC ";

    /// <summary>What an enrichment plant placed from the GC RTS tab is set to make.</summary>
    internal const string MissionWarheadProduction = NuclearWarheadProduction;

    /// <summary>The GC RTS tab's list, in the order a map maker reads it.</summary>
    internal static readonly CommanderBuildKind[] MissionKinds =
    {
        CommanderBuildKind.Mine,
        CommanderBuildKind.GasTerminal,
        CommanderBuildKind.FuelExchange,
        CommanderBuildKind.NavalDock,
    };

    /// <summary>Set only while <c>SpawnBuilding</c> is inside the game's spawn call.</summary>
    private static string? spawningBuildingName;

    internal static string GetMissionLabel(CommanderBuildKind kind)
    {
        return kind == CommanderBuildKind.NavalDock ? NavalDockDisplayName : GetIncomeName(kind);
    }

    /// <summary>The prefab the editor places for <paramref name="kind"/>, the same one a build lands as.</summary>
    internal static BuildingDefinition? GetMissionDefinition(CommanderBuildKind kind)
    {
        return Instance?.ResolveDefinition(kind);
    }

    /// <summary>
    /// Every ground unit any faction in the editor fields, A to Z, then every aircraft and ship the
    /// BUILD window's factory list offers: the GC RTS tab offers a factory for each. Those factories
    /// need no GC name, the game's own <c>factoryOptions</c> run them.
    /// </summary>
    internal static void CollectMissionFactoryProducts(List<UnitDefinition> into)
    {
        into.Clear();
        List<VehicleDefinition> faction = new();
        FactionHQ? anyHq = null;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            anyHq ??= hq;
            CommanderGameAccess.CollectFactionVehicleDefinitions(faction, hq);
            for (int i = 0; i < faction.Count; i++)
            {
                if (!into.Contains(faction[i]))
                {
                    into.Add(faction[i]);
                }
            }
        }

        into.Sort(static (a, b) => string.Compare(a.unitName, b.unitName, StringComparison.OrdinalIgnoreCase));

        // Aircraft and ships are not per faction, so any HQ gives the whole list, already in order.
        List<UnitDefinition> all = new();
        CollectFactoryProducts(all, anyHq);
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] is not VehicleDefinition)
            {
                into.Add(all[i]);
            }
        }
    }

    /// <summary>The hall a built factory for <paramref name="product"/> wears.</summary>
    internal static BuildingDefinition? GetMissionFactoryDefinition(UnitDefinition product)
    {
        return Instance?.ResolveFactoryDefinition(product);
    }

    /// <summary>The nuclear enrichment plant: the building whose own factory makes warheads.</summary>
    internal static BuildingDefinition? GetMissionEnrichmentDefinition()
    {
        List<BuildingDefinition>? buildings = Encyclopedia.i?.buildings;
        for (int i = 0; buildings != null && i < buildings.Count; i++)
        {
            Factory? factory = buildings[i]?.unitPrefab == null ? null : buildings[i].unitPrefab.GetComponentInChildren<Factory>(true);
            if (factory != null && factory.factoryType == Factory.FactoryType.Nukes)
            {
                return buildings[i];
            }
        }

        return null;
    }

    internal static CommanderBuildKind GetMissionKind(string? uniqueName)
    {
        if (uniqueName == null || !uniqueName.StartsWith(MissionNamePrefix, StringComparison.Ordinal))
        {
            return CommanderBuildKind.None;
        }

        for (int i = 0; i < MissionKinds.Length; i++)
        {
            string label = GetMissionLabel(MissionKinds[i]);
            if (string.CompareOrdinal(uniqueName, MissionNamePrefix.Length, label, 0, label.Length) == 0)
            {
                return MissionKinds[i];
            }
        }

        return CommanderBuildKind.None;
    }

    /// <summary>The name <paramref name="building"/> goes out to guests under, or null for its own.</summary>
    internal static string? GetSpawnName(Building building)
    {
        if (spawningBuildingName != null)
        {
            return spawningBuildingName;
        }

        // The tag is read off the mission: IsTaggedMission is refreshed per frame, and a mission's
        // buildings spawn while it loads.
        CommanderBuildKind kind = GetMissionKind(building.UniqueName);
        return kind != CommanderBuildKind.None && CommanderFeatureGate.HasTag(MissionManager.CurrentMission)
            ? GetMissionLabel(kind)
            : null;
    }

    /// <summary>
    /// The dock's depot may already have registered itself while it spawned, before dockLevels
    /// knew it was a dock; CommanderEconomyPatches.AddDepotPrefix keeps it off the list after this.
    /// </summary>
    private static void ForgetDockDepot(FactionHQ hq, Unit dock)
    {
        List<(VehicleDepot, float)> depots = CommanderCaptureSpoils.DepotsRef(hq);
        for (int i = depots.Count - 1; i >= 0; i--)
        {
            if (depots[i].Item1 != null && ReferenceEquals(CommanderGameAccess.GetDepotOwner(depots[i].Item1), dock))
            {
                depots.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Host, on the income tick, so a building a <c>SpawnUnit</c> outcome brings in later is found
    /// too. Only on a GC RTS map, so a stock mission never pays for the walk over every unit.
    /// </summary>
    private void AdoptMissionBuildings()
    {
        if (!CommanderFeatureGate.IsTaggedMission || NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            return;
        }

        List<Unit> all = UnitRegistry.allUnits;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] is not Building building || building == null || building.disabled || builtUnits.Contains(building))
            {
                continue;
            }

            CommanderBuildKind kind = GetMissionKind(building.UniqueName);
            if (kind == CommanderBuildKind.None)
            {
                continue;
            }

            building.NetworkunitName = GetMissionLabel(kind);
            CommanderPlugin.Log.LogInfo($"Mission building '{building.UniqueName}' is a {GetMissionLabel(kind)}.");
            builtUnits.Add(building);
            if (kind == CommanderBuildKind.NavalDock)
            {
                dockLevels[building] = 1;
                if (building.NetworkHQ != null)
                {
                    ForgetDockDepot(building.NetworkHQ, building);
                }
            }
            else
            {
                incomeLevels[building] = 1;
                incomeKinds[building] = kind;
            }
        }
    }
}
