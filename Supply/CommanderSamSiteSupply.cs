using System.Collections.Generic;
using System.Linq;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSupplyHeliService
{
    internal void CopySamSiteAirbaseOptions(
        GlobalPosition target,
        List<SamSiteAirbaseOption> destination)
    {
        destination.Clear();
        if (!CanHostSpawn(out FactionHQ? hq, out _))
        {
            return;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        foreach (Airbase airbase in hq!.GetAirbases())
        {
            if (airbase == null
                || airbase.disabled
                || airbase.CurrentHQ != hq)
            {
                continue;
            }

            Transform sourceTransform = airbase.center != null ? airbase.center : airbase.transform;
            GlobalPosition sourcePosition = sourceTransform.GlobalPosition();
            bool hasInfluenceRisk = CommanderSamSiteAnalyzerService.TryEvaluateLogisticsRisk(
                sourcePosition,
                target,
                null,
                out float risk,
                out _);
            bool safe = hasInfluenceRisk
                ? risk <= 0.62f
                : IsSamSupplyAirbaseSafe(airbase, target);
            if (!hasInfluenceRisk)
            {
                risk = safe ? 0.35f : 0.75f;
            }

            bool supportsSupply = false;
            bool supportsJacknife = false;
            bool ready = false;
            for (int aircraftIndex = 0; aircraftIndex < aircraftOptions.Count; aircraftIndex++)
            {
                CargoAircraftOption aircraft = aircraftOptions[aircraftIndex];
                if (!IsCompatibleAirbase(airbase, hq, aircraft.Definition))
                {
                    continue;
                }

                bool aircraftReady = IsAvailableAirbase(airbase, hq, aircraft.Definition);
                for (int slotIndex = 0; slotIndex < aircraft.CargoSlots.Count; slotIndex++)
                {
                    CargoSlotOption slot = aircraft.CargoSlots[slotIndex];
                    for (int mountIndex = 0; mountIndex < slot.Mounts.Count; mountIndex++)
                    {
                        WeaponMount mount = slot.Mounts[mountIndex];
                        if (!WeaponChecker.MountAllowedHQ(mount, hq)
                            || !WeaponChecker.MountAllowedAirbase(mount, airbase))
                        {
                            continue;
                        }

                        bool mountSupportsSupply = TryGetAmmunitionCargoCapacity(mount, out _);
                        bool mountSupportsJacknife = IsJacknifeCargo(mount);
                        supportsSupply |= mountSupportsSupply;
                        supportsJacknife |= mountSupportsJacknife;
                        ready |= aircraftReady && (mountSupportsSupply || mountSupportsJacknife);
                    }
                }
            }

            if (!supportsSupply && !supportsJacknife)
            {
                continue;
            }

            destination.Add(new SamSiteAirbaseOption(
                airbase,
                GetAirbaseLabel(airbase),
                CommanderGameAccess.HorizontalDistance(
                    sourceTransform.position,
                    target.ToLocalPosition()),
                supportsSupply,
                supportsJacknife,
                ready,
                safe,
                risk));
        }

        destination.Sort((left, right) =>
        {
            int safety = right.Safe.CompareTo(left.Safe);
            if (safety != 0)
            {
                return safety;
            }

            float leftScore = left.Distance + (left.Risk * 50000f);
            float rightScore = right.Distance + (right.Risk * 50000f);
            return leftScore.CompareTo(rightScore);
        });
    }

    internal bool RequestAutomaticCargoRun(
        int siteId,
        GlobalPosition target,
        Airbase? requestedAirbase,
        float requestedSupply)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return false;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        List<SamSupplyLoadoutChoice> choices = new();
        for (int aircraftIndex = 0; aircraftIndex < aircraftOptions.Count; aircraftIndex++)
        {
            CargoAircraftOption aircraft = aircraftOptions[aircraftIndex];
            foreach (Airbase airbase in hq!.GetAirbases())
            {
                if (requestedAirbase != null && !ReferenceEquals(airbase, requestedAirbase))
                {
                    continue;
                }

                if (!IsCompatibleAirbase(airbase, hq, aircraft.Definition)
                    || (requestedAirbase == null && !IsSamSupplyAirbaseSafe(airbase, target)))
                {
                    continue;
                }

                List<(CargoSlotOption Slot, WeaponMount Mount, float Capacity)>? mounts = new();
                HashSet<int>? supportedSlots = new();
                for (int slotIndex = 0; slotIndex < aircraft.CargoSlots.Count; slotIndex++)
                {
                    CargoSlotOption slot = aircraft.CargoSlots[slotIndex];
                    for (int mountIndex = 0; mountIndex < slot.Mounts.Count; mountIndex++)
                    {
                        WeaponMount mount = slot.Mounts[mountIndex];
                        if (WeaponChecker.MountAllowedHQ(mount, hq)
                            && WeaponChecker.MountAllowedAirbase(mount, airbase)
                            && TryGetAmmunitionCargoCapacity(mount, out float capacity))
                        {
                            mounts.Add((slot, mount, capacity));
                            supportedSlots.Add(slot.HardpointIndex);
                        }
                    }
                }

                for (int first = 0; first < mounts.Count; first++)
                {
                    (CargoSlotOption slot, WeaponMount mount, float capacity) = mounts[first];
                    choices.Add(new SamSupplyLoadoutChoice(
                        aircraft,
                        airbase,
                        slot,
                        mount,
                        capacity,
                        supportedSlots.Count));
                    for (int second = first + 1; second < mounts.Count; second++)
                    {
                        (CargoSlotOption secondSlot, WeaponMount secondMount, float secondCapacity) = mounts[second];
                        if (slot.HardpointIndex == secondSlot.HardpointIndex
                            || SetsConflict(
                                aircraft.HardpointSets,
                                slot.HardpointIndex,
                                secondSlot.HardpointIndex))
                        {
                            continue;
                        }

                        choices.Add(new SamSupplyLoadoutChoice(
                            aircraft,
                            airbase,
                            slot,
                            mount,
                            capacity,
                            supportedSlots.Count,
                            secondSlot,
                            secondMount,
                            secondCapacity));
                    }
                }
            }
        }

        if (choices.Count == 0)
        {
            SetStatus(requestedAirbase == null
                ? "No compatible SAM-site supply helicopter is available."
                : "The selected airbase cannot dispatch a SAM-site supply helicopter.");
            return false;
        }

        requestedSupply = Mathf.Max(1f, requestedSupply);
        bool requestTwoContainers = requestedSupply >= 20000f - 0.01f;
        List<SamSupplyLoadoutChoice> suitable = choices
            .Where(choice => choice.TotalCapacity <= requestedSupply + 0.01f)
            .ToList();
        if (suitable.Count == 0)
        {
            suitable.AddRange(choices.Where(choice => choice.CargoCount == 1));
        }

        if (requestTwoContainers && suitable.Any(choice => choice.CargoCount == 2))
        {
            suitable.RemoveAll(choice => choice.CargoCount != 2);
            suitable.Sort(static (left, right) =>
            {
                int tarantula = right.IsTarantula.CompareTo(left.IsTarantula);
                return tarantula != 0
                    ? tarantula
                    : right.TotalCapacity.CompareTo(left.TotalCapacity);
            });
        }
        else
        {
            suitable.RemoveAll(choice => choice.CargoCount != 1);
            bool hasSingleSlotAircraft = suitable.Any(choice => choice.SupportedSlotCount == 1);
            if (hasSingleSlotAircraft)
            {
                suitable.RemoveAll(choice => choice.SupportedSlotCount != 1);
            }

            suitable.Sort(static (left, right) => right.TotalCapacity.CompareTo(left.TotalCapacity));
        }

        SamSupplyLoadoutChoice choice = suitable[0];
        Loadout loadout = CreateEmptyLoadout(choice.Aircraft.HardpointSets.Length);
        PlaceCargoAndClearNonCargo(
            loadout,
            choice.Aircraft.HardpointSets,
            choice.FirstSlot.HardpointIndex,
            choice.FirstMount);
        if (choice.SecondSlot != null && choice.SecondMount != null)
        {
            PlaceCargoAndClearNonCargo(
                loadout,
                choice.Aircraft.HardpointSets,
                choice.SecondSlot.HardpointIndex,
                choice.SecondMount);
        }

        string cargoLabel = GetCargoLabel(choice.FirstMount, string.Empty);
        CommanderPlugin.Log.LogInfo(
            $"SAM supply loadout selected: aircraft={choice.Aircraft.Label}, cargo={choice.CargoCount}, "
            + $"capacity={choice.TotalCapacity:0}, freeAtRequest={requestedSupply:0}, "
            + $"singleSlotAircraft={choice.SupportedSlotCount == 1}.");
        SpawnCargoRun(
            choice.Aircraft,
            loadout,
            choice.CargoCount > 1 ? $"{choice.CargoCount}x {cargoLabel}" : cargoLabel,
            choice.Airbase,
            useHighTerrainClearance: true,
            terrainClearanceMeters: 100f,
            useAirdrop: false,
            supportSummary: SamSiteCargoSupportPrefix + siteId,
            useOtherAirfields: requestedAirbase == null,
            target);
        return true;
    }

    internal bool RequestSamSiteJacknife(
        int siteId,
        GlobalPosition target,
        Airbase? requestedAirbase)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return false;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        List<(CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption Slot, WeaponMount Mount)>? choices = new();
        for (int aircraftIndex = 0; aircraftIndex < aircraftOptions.Count; aircraftIndex++)
        {
            CargoAircraftOption aircraft = aircraftOptions[aircraftIndex];
            foreach (Airbase airbase in hq!.GetAirbases())
            {
                if (requestedAirbase != null && !ReferenceEquals(airbase, requestedAirbase))
                {
                    continue;
                }

                if (!IsCompatibleAirbase(airbase, hq, aircraft.Definition)
                    || (requestedAirbase == null && !IsSamSupplyAirbaseSafe(airbase, target)))
                {
                    continue;
                }

                for (int slotIndex = 0; slotIndex < aircraft.CargoSlots.Count; slotIndex++)
                {
                    CargoSlotOption slot = aircraft.CargoSlots[slotIndex];
                    for (int mountIndex = 0; mountIndex < slot.Mounts.Count; mountIndex++)
                    {
                        WeaponMount mount = slot.Mounts[mountIndex];
                        if (IsJacknifeCargo(mount)
                            && WeaponChecker.MountAllowedHQ(mount, hq)
                            && WeaponChecker.MountAllowedAirbase(mount, airbase))
                        {
                            choices.Add((aircraft, airbase, slot, mount));
                        }
                    }
                }
            }
        }

        if (choices.Count == 0)
        {
            SetStatus(requestedAirbase == null
                ? "No compatible Jacknife transport is available."
                : "The selected airbase cannot dispatch a Jacknife transport.");
            return false;
        }

        (CargoAircraftOption Aircraft, Airbase Airbase, CargoSlotOption Slot, WeaponMount Mount) choice = choices[Random.Range(0, choices.Count)];
        Loadout loadout = CreateEmptyLoadout(choice.Aircraft.HardpointSets.Length);
        PlaceCargoAndClearNonCargo(
            loadout,
            choice.Aircraft.HardpointSets,
            choice.Slot.HardpointIndex,
            choice.Mount);
        SpawnCargoRun(
            choice.Aircraft,
            loadout,
            GetCargoLabel(choice.Mount, string.Empty),
            choice.Airbase,
            useHighTerrainClearance: true,
            terrainClearanceMeters: 100f,
            useAirdrop: false,
            supportSummary: SamSiteJacknifeSupportPrefix + siteId,
            useOtherAirfields: requestedAirbase == null,
            target);
        return true;
    }

    internal sealed class SamSiteAirbaseOption
    {
        internal SamSiteAirbaseOption(
            Airbase airbase,
            string label,
            float distance,
            bool supportsSupply,
            bool supportsJacknife,
            bool ready,
            bool safe,
            float risk)
        {
            Airbase = airbase;
            Label = label;
            Distance = distance;
            SupportsSupply = supportsSupply;
            SupportsJacknife = supportsJacknife;
            Ready = ready;
            Safe = safe;
            Risk = risk;
        }

        internal Airbase Airbase { get; }

        internal string Label { get; }

        internal float Distance { get; }

        internal bool SupportsSupply { get; }

        internal bool SupportsJacknife { get; }

        internal bool Ready { get; }

        internal bool Safe { get; }

        internal float Risk { get; }
    }

    private sealed class SamSupplyLoadoutChoice
    {
        internal SamSupplyLoadoutChoice(
            CargoAircraftOption aircraft,
            Airbase airbase,
            CargoSlotOption firstSlot,
            WeaponMount firstMount,
            float firstCapacity,
            int supportedSlotCount,
            CargoSlotOption? secondSlot = null,
            WeaponMount? secondMount = null,
            float secondCapacity = 0f)
        {
            Aircraft = aircraft;
            Airbase = airbase;
            FirstSlot = firstSlot;
            FirstMount = firstMount;
            FirstCapacity = firstCapacity;
            SupportedSlotCount = supportedSlotCount;
            SecondSlot = secondSlot;
            SecondMount = secondMount;
            SecondCapacity = secondCapacity;
        }

        internal CargoAircraftOption Aircraft { get; }

        internal Airbase Airbase { get; }

        internal CargoSlotOption FirstSlot { get; }

        internal WeaponMount FirstMount { get; }

        internal float FirstCapacity { get; }

        internal int SupportedSlotCount { get; }

        internal CargoSlotOption? SecondSlot { get; }

        internal WeaponMount? SecondMount { get; }

        internal float SecondCapacity { get; }

        internal int CargoCount => SecondSlot != null && SecondMount != null ? 2 : 1;

        internal float TotalCapacity => FirstCapacity + SecondCapacity;

        internal bool IsTarantula => Aircraft.Label.IndexOf("Tarantula", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
