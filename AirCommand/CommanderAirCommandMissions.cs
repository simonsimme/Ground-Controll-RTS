using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    /// <summary>Why the last <see cref="LaunchAiAircraft"/> refused, for the player's red banner.</summary>
    internal static string LastLaunchRefusal { get; private set; } = string.Empty;

    /// <summary>
    /// Rolls one AI airframe out of a hangar at <paramref name="airbase"/> so it taxis and takes off
    /// under its own power, exactly as a mission-spawned aircraft does. Returns false only when the
    /// airframe could not be put on the map at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AIPilotTaxiState</c> and <c>AIPilotTakeoffState</c> answer any trouble (stuck timer, scrape,
    /// wing off level, stall on the roll) with <c>StartEjectionSequence</c>.
    /// <c>CommanderAirCommandPatches.StartEjectionSequencePrefix</c> catches that for a commanded
    /// aircraft still on the deck and hands it to <see cref="TryBlockGroundEjection"/>. A field whose
    /// runway is too short or whose hangars are full refuses the launch before anything is charged,
    /// with the reason in <see cref="LastLaunchRefusal"/>.
    /// </para>
    /// <para>
    /// <c>Hangar.TrySpawnAircraft</c> takes the airframe out of stock. Callers here do their own supply
    /// bookkeeping, so that decrement is put back: LaunchAiAircraft never changes supply.
    /// </para>
    /// </remarks>
    internal static bool LaunchAiAircraft(
        FactionHQ hq,
        Airbase airbase,
        AircraftDefinition definition,
        LiveryKey livery,
        Loadout loadout,
        float fuel,
        Hangar? hangar = null)
    {
        LastLaunchRefusal = string.Empty;
        if (airbase.center == null || definition.unitPrefab == null)
        {
            return false;
        }

        // Every launch takes off under its own power; nothing is spawned already flying. The game's AI
        // takeoff asks for a runway of any length (GetTakeoffRunway(aircraft, 0f)), so the length rule is
        // the one the human flight HUD uses, takeoffDistance against Runway.Length, checked before
        // anything is charged. A player's launch is never refused for past overruns; only the AI
        // commanders learn from them.
        bool aiLaunch = CommanderEnemyCommanderService.IsAiFaction(hq, CommanderGameAccess.GetLocalHq());
        if (!HasTakeoffRunway(airbase, definition, countOverruns: aiLaunch))
        {
            string tooShort = aiLaunch && GetRunwayOverruns(airbase, definition) >= OverrunsBeforeRefusal
                ? "the last ones ran off the end of the runway there"
                : $"no runway there is long enough ({definition.aircraftParameters.takeoffDistance:0} m)";
            return RefuseAiLaunch(hq, airbase, definition, tooShort);
        }

        // Full tanks are the rule (a part-filled Darkreach turned home before reaching anything), but
        // a type that has already run off the end here goes lighter: less weight, shorter roll.
        if (GetRunwayOverruns(airbase, definition) > 0)
        {
            fuel = Mathf.Min(fuel, definition.aircraftParameters.DefaultFuelLevel);
        }

        // A named hangar or landing pad is what "spawn it out of this one" means; otherwise the
        // airbase picks whichever of its hangars will take the airframe.
        bool doorsFree = hangar != null
            ? !hangar.Disabled && hangar.CanSpawnAircraft(definition)
            : airbase.CanSpawnAircraft(definition);
        if (!doorsFree)
        {
            return RefuseAiLaunch(hq, airbase, definition, "no free hangar there will take it");
        }

        if (CommanderUnitCap.IsFull(hq))
        {
            return RefuseAiLaunch(hq, airbase, definition, $"unit cap of {CommanderUnitCap.Cap} reached");
        }

        if (CommanderUnitCap.IsAiTypeFull(hq, definition))
        {
            return RefuseAiLaunch(hq, airbase, definition, $"{CommanderUnitCap.TypeCap} of this airframe already up");
        }

        // An airframe leaves the field with full tanks and a full rack, and that comes off the
        // shelves at the field it left from. Both commanders launch through here, so this is the
        // only place it has to be charged.
        if (CommanderSupplyStoreService.Instance?.TrySpendSpawnSupply(
            hq, airbase.center.GlobalPosition(), definition, out string supplyReason) == false)
        {
            return RefuseAiLaunch(hq, airbase, definition, supplyReason);
        }

        bool allowed = hangar != null
            ? hangar.TrySpawnAircraft(null!, definition, livery, loadout, fuel).Allowed
            : airbase.TrySpawnAircraft(null!, definition, livery, loadout, fuel).Allowed;
        if (!allowed)
        {
            return RefuseAiLaunch(hq, airbase, definition, "the hangar refused the spawn");
        }

        // Undo the hangar's own stock decrement; the caller does that half.
        hq.ModifyUnitSupply(definition, 1);
        if (airbase.TryGetAttachedUnit(out _))
        {
            DeckLaunchedAt[airbase] = Time.time;
        }

        return true;
    }

    /// <summary>
    /// A ship's deck with an aeroplane of its own still on it, taxiing or rolling. Deck hangars sit side
    /// by side and block each other, so the enemy commander launches one at a time from a deck. The
    /// player's launches are not held: the deck turn (HoldForDeckTurn) sequences them instead.
    /// </summary>
    internal static bool IsDeckBusy(Airbase airbase)
    {
        if (!airbase.TryGetAttachedUnit(out _))
        {
            return false;
        }

        // A carrier hangar only spawns once its door has opened (Hangar.DoorSequenceCarrier), so for those
        // seconds the launch is on no deck and in no faction list. Without this the buy loop launches twice.
        if (DeckLaunchedAt.TryGetValue(airbase, out float launchedAt) && Time.time - launchedAt < DeckDoorSeconds)
        {
            return true;
        }

        List<Aircraft> aircraft = airbase.ControlledAircraft;
        for (int i = 0; i < aircraft.Count; i++)
        {
            Aircraft other = aircraft[i];
            if (other != null
                && !other.disabled
                && other.radarAlt < 5f
                && other.pilots?.Length > 0
                && other.pilots[0].currentState is AIPilotTaxiState or AIPilotTakeoffState)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The hook on the first hardpoint that offers one and nothing else, or null when the
    /// airframe has no hook option. Found by <c>WeaponInfo.sling</c>, as the hook preset is. The
    /// TRANSPORT window's rescue launch and the enemy's airlift both fly this.</summary>
    internal static Loadout? BuildSlingLoadout(AircraftDefinition definition)
    {
        HardpointSet[]? sets = definition.unitPrefab?.GetComponent<Aircraft>()?.weaponManager?.hardpointSets;
        if (sets == null)
        {
            return null;
        }

        Loadout loadout = new();
        bool hooked = false;
        for (int i = 0; i < sets.Length; i++)
        {
            WeaponMount? hook = hooked ? null : sets[i].weaponOptions?.Find(static mount => mount != null && mount.info?.sling == true);
            loadout.weapons.Add(hook!);
            hooked |= hook != null;
        }

        return hooked ? loadout : null;
    }

    /// <summary>
    /// The TRANSPORT window's RESCUE launch: a STANDBY sortie carrying the hook alone, straight onto
    /// recovery duty. It goes through the same door as AIR COMMAND's LAUNCH (price, stock, supply
    /// draw, guest forwarding), so nothing about it is a second launch path.
    /// </summary>
    internal void LaunchRescue(AircraftDefinition definition, Airbase airbase, CommanderRecoveryService.RecoveryMode recovery)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        HardpointSet[]? sets = definition.unitPrefab?.GetComponent<Aircraft>()?.weaponManager?.hardpointSets;
        if (hq == null || sets == null || BuildSlingLoadout(definition) is not Loadout loadout)
        {
            RefuseLaunch("This aircraft cannot carry the sling hook.");
            return;
        }

        if (!ValidateMounts(loadout, sets, airbase, hq, out string error))
        {
            RefuseLaunch(error);
            return;
        }

        AirMissionOption option = new(definition, AirCommandMode.Standby, sets, new List<AirHardpointGroup>());
        Transform field = airbase.center != null ? airbase.center : airbase.transform;
        if (CommanderNetwork.IsGuest)
        {
            SendLaunch(option, loadout, airbase, null, field.GlobalPosition(), recovery);
            return;
        }

        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            RefuseLaunch("Air Command is host-only.");
            return;
        }

        LaunchMission(hq, option, loadout, airbase, null, field.GlobalPosition(), 0f, 0f, false, false, recovery);
    }

    private static bool RefuseAiLaunch(FactionHQ hq, Airbase airbase, AircraftDefinition definition, string reason)
    {
        LastLaunchRefusal = reason;
        CommanderPlugin.Log.LogInfo(
            $"{hq.faction?.name} could not launch a {definition.unitName} from {airbase.name}: {reason}.");
        return false;
    }

    /// <summary>Another set of doors at the same field that will take this airframe.</summary>
    private static Hangar? FindFreeSiblingHangar(Airbase airbase, Hangar busy, AircraftDefinition definition)
    {
        List<Hangar> hangars = airbase.hangars;
        for (int i = 0; i < hangars.Count; i++)
        {
            Hangar candidate = hangars[i];
            if (candidate != null
                && !ReferenceEquals(candidate, busy)
                && !candidate.Disabled
                && candidate.CanSpawnAircraft(definition))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>RECOVERY only rides a STANDBY launch that actually carries the hook; a guest's too.</summary>
    private static CommanderRecoveryService.RecoveryMode RecoveryFor(
        AirCommandMode mode, CommanderRecoveryService.RecoveryMode wanted, Loadout loadout)
    {
        if (mode != AirCommandMode.Standby
            || !System.Enum.IsDefined(typeof(CommanderRecoveryService.RecoveryMode), wanted))
        {
            return CommanderRecoveryService.RecoveryMode.Off;
        }

        bool hook = false;
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponInfo? info = loadout.weapons[i]?.info;
            if (info != null && (info.cargo || info.troops))
            {
                // Rescue or transport, never both: a cargo bay aboard is a transport.
                return CommanderRecoveryService.RecoveryMode.Off;
            }

            hook |= info?.sling == true;
        }

        return hook ? wanted : CommanderRecoveryService.RecoveryMode.Off;
    }

    /// <summary>
    /// Rebuilds the aircraft list. It keeps the aeroplane, the weapons and the departure field the
    /// player already chose wherever they still exist, because this now runs on a mission-type
    /// change too and losing three answers to change the fourth is the churn this rework removes.
    /// </summary>
    private void RefreshOptions()
    {
        AircraftDefinition? previousDefinition = SelectedOption?.Definition;
        string? primaryKey = SelectedPrimaryWeaponKey;
        string? secondaryKey = SelectedSecondaryWeaponKey;

        // The options are rebuilt from scratch, so pylons set by hand in the editor would be lost on
        // a mission change. Remember them, and put them back if the aircraft is still the same.
        LoadoutPreset? handMade = AdvancedLoadout && SelectedOption != null
            ? LoadoutPreset.FromLoadout(string.Empty, SelectedOption.BuildLoadout(), SelectedOption.HardpointSets, custom: false)
            : null;
        options.Clear();
        airbases.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (!IsUiVisible || hq == null)
        {
            weaponOptions.Clear();
            cargoOptions.Clear();
            presets.Clear();
            return;
        }

        // A hangar that has been bombed, or whose base has fallen, stops being the filter rather
        // than leaving the window showing an empty list with no way back.
        Hangar? hangar = HangarFilter;
        if (hangar == null || hangar.attachedUnit == null || hangar.Disabled || ResolveHangarField(hangar) == null)
        {
            hangar = null;
            HangarFilter = null;
        }

        List<AircraftDefinition> definitions = new();
        HashSet<AircraftDefinition> seen = new();
        if (hangar != null)
        {
            // The base game files each airframe under the hangars that can hold it, so a landing pad
            // lists helicopters and a hangar lists aeroplanes with no type test of the mod's own.
            AircraftDefinition[] hangarAircraft = hangar.GetAvailableAircraft();
            for (int i = 0; i < hangarAircraft.Length; i++)
            {
                if (hangarAircraft[i] != null && seen.Add(hangarAircraft[i]))
                {
                    definitions.Add(hangarAircraft[i]);
                }
            }
        }
        else
        {
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia?.aircraft != null)
            {
                for (int i = 0; i < encyclopedia.aircraft.Count; i++)
                {
                    AircraftDefinition definition = encyclopedia.aircraft[i];
                    if (definition != null && seen.Add(definition))
                    {
                        definitions.Add(definition);
                    }
                }
            }

            AircraftDefinition[] resourceDefinitions = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
            for (int i = 0; i < resourceDefinitions.Length; i++)
            {
                AircraftDefinition definition = resourceDefinitions[i];
                if (definition != null && seen.Add(definition))
                {
                    definitions.Add(definition);
                }
            }
        }

        for (int i = 0; i < definitions.Count; i++)
        {
            AircraftDefinition definition = definitions[i];
            if (definition == null
                || definition.unitPrefab == null
                || definition.aircraftParameters == null
                || CommanderUnitBanService.IsBanned(definition))
            {
                continue;
            }

            // CanAiFly, not HasPlanePilot: rotary airframes are commandable now, and a landing pad
            // holds nothing else. PilotType.VTOL still gets no AI state at all, so it stays out.
            if (!CanAiFly(definition))
            {
                continue;
            }

            AirMissionOption? option = CreateVariableLoadoutOption(definition, hq, SelectedMode);
            if (option == null)
            {
                continue;
            }

            bool supported = hangar != null;
            if (hangar == null)
            {
                foreach (Airbase airbase in hq.GetAirbases())
                {
                    if (IsCompatibleAirbase(airbase, hq, definition))
                    {
                        supported = true;
                        break;
                    }
                }
            }

            if (supported)
            {
                options.Add(option);
            }
        }

        SortOptions();
        SelectedOptionIndex = 0;
        if (previousDefinition != null)
        {
            int preserved = options.FindIndex(entry => ReferenceEquals(entry.Definition, previousDefinition));
            if (preserved >= 0)
            {
                SelectedOptionIndex = preserved;
            }
        }

        RebuildWeaponSelection(primaryKey, secondaryKey);
        RefreshPresets();
        if (handMade != null && SelectedOption != null && ReferenceEquals(SelectedOption.Definition, previousDefinition))
        {
            WearPylons(SelectedOption, handMade);
        }

        RefreshAirbases();
    }

    private void RefreshAirbases()
    {
        Airbase? previouslySelected = SelectedAirbase?.Airbase;
        airbases.Clear();
        AirMissionOption? option = SelectedOption;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (option == null || hq == null)
        {
            SelectedAirbaseIndex = 0;
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;

        // Armed from a hangar there is exactly one departure point and the player already picked it
        // by clicking the building, so the list is that one row.
        if (HangarFilter != null && ResolveHangarField(HangarFilter) is Airbase field)
        {
            Transform fieldPosition = field.center != null ? field.center : field.transform;
            airbases.Add(new AirbaseOption(
                field,
                GetHangarFilterLabel(),
                Vector3.Distance(cameraPosition, fieldPosition.position),
                HangarFilter.Available));
            SelectedAirbaseIndex = 0;
            return;
        }

        // A field whose doors are all shut stays listed as BUSY. Hangar.Available is false until the
        // last spawn is 30+ m clear, which on a carrier with aircraft parked by the doors can be the
        // rest of the match, and dropping the row made a Hyperion beside a Cursor simply vanish.
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!IsCompatibleAirbase(airbase, hq, option.Definition))
            {
                continue;
            }

            Transform position = airbase.center != null ? airbase.center : airbase.transform;
            airbases.Add(new AirbaseOption(
                airbase,
                GetAirbaseName(airbase),
                Vector3.Distance(cameraPosition, position.position),
                ready: airbase.CanSpawnAircraft(option.Definition)));
        }

        airbases.Sort(static (left, right) => left.Ready != right.Ready
            ? right.Ready.CompareTo(left.Ready)
            : left.Distance.CompareTo(right.Distance));
        int preservedIndex = previouslySelected == null
            ? -1
            : airbases.FindIndex(option => ReferenceEquals(option.Airbase, previouslySelected));

        // A pick survives only while it can launch: ready rows sort first, so a field that goes busy
        // hands over to the nearest free one and spamming LAUNCH walks down the list.
        SelectedAirbaseIndex = preservedIndex >= 0 && (airbases[preservedIndex].Ready || !airbases[0].Ready)
            ? preservedIndex
            : 0;
    }

    private void TryHandleTacticalMapClick()
    {
        if (!DynamicMap.mapMaximized)
        {
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        UpdatePendingAreaPreview();
        Vector2 guiMouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
        if (areaSelectionBlockingRect.Contains(guiMouse) || areaSelectionSecondaryBlockingRect.Contains(guiMouse))
        {
            mapClickTracker.Reset();
            return;
        }

        if (map != null && mapClickTracker.Tick(map, out GlobalPosition target))
        {
            CompleteAreaSelection(target);
        }
    }

    private void CompleteAreaSelection(GlobalPosition target)
    {
        PendingAreaSelection? selection = pendingAreaSelection;
        Aircraft? relocationAircraft = pendingMissionRelocation;
        Aircraft? adoptionAircraft = pendingAdoption;

        // The same repeat key the supply and BUILD placements use keeps the purchase armed, so a
        // row of sorties is one click each instead of a trip back to LAUNCH for every one.
        if (selection != null
            && relocationAircraft == null
            && adoptionAircraft == null
            && CommanderShortcutInput.IsPressed(CommanderSettings.RepeatDeployment))
        {
            SpawnMission(selection.Option, GetCurrentDeparture(selection), target);
            return;
        }

        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = null;
        DestroyPendingAreaPreview();
        CloseOrderMap();
        if (relocationAircraft != null && missions.TryGetValue(relocationAircraft, out AirMission relocationMission))
        {
            if (ForwardAir("air.area", relocationAircraft, CommanderNetwork.Position(target)))
            {
                return;
            }

            relocationMission.AreaCenter = target;
            DestroyMissionMapVisual(relocationMission);
            EnsureMissionMapVisual(relocationMission);
            SetStatus($"{GetModeLabel(relocationMission.Mode)} mission area relocated.");
            return;
        }

        if (adoptionAircraft != null)
        {
            if (ForwardAir(
                "air.adopt",
                adoptionAircraft,
                CommanderNetwork.Num((int)SelectedMode),
                CommanderNetwork.Position(target),
                CommanderNetwork.Num(GetMissionRadius(SelectedMode))))
            {
                return;
            }

            if (TryAdoptAircraft(adoptionAircraft, SelectedMode, target, GetMissionRadius(SelectedMode)))
            {
                SetStatus($"{CommanderGameAccess.GetUnitLabel(adoptionAircraft)} tasked with a {GetModeLabel(SelectedMode)} mission.");
            }
            else
            {
                SetStatus("That aircraft can no longer be tasked.");
            }

            return;
        }

        if (selection == null)
        {
            return;
        }

        SpawnMission(selection.Option, GetCurrentDeparture(selection), target);
    }

    /// <summary>The field armed when the map opened can have gone busy by the time the area is
    /// clicked, the previous click's launch being the usual reason, so ask the list again.</summary>
    private Airbase GetCurrentDeparture(PendingAreaSelection selection)
    {
        RefreshAirbases();
        return ReferenceEquals(selection.Option, SelectedOption) && SelectedAirbase is AirbaseOption current
            ? current.Airbase
            : selection.Airbase;
    }

    private void CancelAreaSelection(bool showStatus)
    {
        if (pendingAreaSelection == null && pendingMissionRelocation == null && pendingAdoption == null)
        {
            return;
        }

        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = null;
        DestroyPendingAreaPreview();
        CloseOrderMap();
        if (showStatus)
        {
            SetStatus("Air mission area selection cancelled.");
        }
    }

    private void SpawnMission(AirMissionOption option, Airbase airbase, GlobalPosition target)
    {
        // A guest's sortie is bought and flown on the host, the only machine whose AI pilots the
        // Air Command patches can steer.
        if (CommanderNetwork.IsGuest)
        {
            ForwardLaunch(option, airbase, target);
            return;
        }

        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            RefuseLaunch("Air Command is host-only.");
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            RefuseLaunch("No faction HQ.");
            return;
        }

        Loadout loadout = option.BuildLoadout();
        NormalizeLoadoutLength(loadout, option.Definition);

        // Checked before a coin leaves the treasury: the old order bought the airframe, found the
        // loadout illegal, and then had to unwind the purchase.
        if (!ValidateSelectedLoadout(option, loadout, airbase, hq, out string loadoutError))
        {
            RefuseLaunch(loadoutError);
            return;
        }

        LaunchMission(
            hq,
            option,
            loadout,
            airbase,
            HangarFilter,
            target,
            GetMissionRadius(option.Mode),
            SupportsTargetAltitude(option.Mode) ? SelectedTargetAltitude : 0f,
            option.Mode == AirCommandMode.AirGuard && TargetOrdnance,
            UsesSaturationAttack(option.Mode) && SaturationAttack);
    }

    /// <summary>
    /// Buys the airframe (or takes it from stock) and launches it with its mission waiting for it.
    /// The host's LAUNCH and a guest's forwarded one both end here, which is why everything the
    /// window would otherwise supply is a parameter.
    /// </summary>
    private void LaunchMission(
        FactionHQ hq,
        AirMissionOption option,
        Loadout loadout,
        Airbase airbase,
        Hangar? hangar,
        GlobalPosition target,
        float radius,
        float targetAltitude,
        bool targetOrdnance,
        bool saturationAttack,
        CommanderRecoveryService.RecoveryMode recovery = CommanderRecoveryService.RecoveryMode.Off)
    {
        if (hangar != null && (hangar.Disabled || !ReferenceEquals(ResolveHangarField(hangar), airbase)))
        {
            hangar = null;
        }

        // Two landing pads at one field are two independent sets of doors, and the base game leaves
        // a pad marked busy until whatever came out of it has moved clear — so a helicopter that
        // flew home and settled back onto its own pad bricked that pad for the rest of the mission.
        // Rolling the sortie out of the field's other doors is what the player expects from having
        // two of them; with none free the original refusal still stands, and now says so loudly.
        if (hangar != null && !hangar.CanSpawnAircraft(option.Definition))
        {
            hangar = FindFreeSiblingHangar(airbase, hangar, option.Definition) ?? hangar;
        }

        if (hq == null || (hangar == null && !IsCompatibleAirbase(airbase, hq, option.Definition)))
        {
            RefuseLaunch("The selected airbase is no longer compatible.");
            return;
        }

        if (hangar != null ? !hangar.CanSpawnAircraft(option.Definition) : !airbase.CanSpawnAircraft(option.Definition))
        {
            RefuseLaunch(hangar != null
                ? "That hangar is busy. Retry once its doors have cycled."
                : "The selected airbase is busy. Retry when a compatible hangar is free.");
            return;
        }

        if (CommanderDraftService.IsDraftMission && hq.GetUnitSupply(option.Definition) <= 0)
        {
            RefuseLaunch($"DRAFT: no {GetAircraftLabel(option.Definition)} left in your air wing.");
            return;
        }

        // The stores are paid for on every launch, stocked airframe or not: munitions cost money.
        float munitions = GetMunitionsCost(option.Definition, loadout);
        bool purchased = false;
        if (hq.GetUnitSupply(option.Definition) <= 0)
        {
            if (hq.factionFunds < CommanderEconomyService.PriceOf(option.Definition) + munitions)
            {
                RefuseLaunch(munitions > 0f
                    ? $"The faction cannot afford this aircraft and its munitions ({CommanderEconomyService.FundsLabel(CommanderEconomyService.PriceOf(option.Definition) + munitions)})."
                    : "The faction cannot afford this aircraft.");
                return;
            }

            hq.AddFunds(-CommanderEconomyService.PriceOf(option.Definition));
            hq.ModifyUnitSupply(option.Definition, 1);
            purchased = true;
        }
        else if (hq.factionFunds < munitions)
        {
            RefuseLaunch($"The faction cannot afford this loadout ({CommanderEconomyService.FundsLabel(munitions)}).");
            return;
        }

        hq.AddFunds(-munitions);

        int liveryIndex = option.Definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);

        // AUTOPILOT takes a pending record too, but only so its loadout is remembered for the rearm:
        // TryAssignPendingAircraft attaches no mission to it and the base-game AI keeps it.
        PendingAircraftSpawn pendingSpawn = new(
            hq,
            option,
            airbase,
            target,
            radius,
            targetAltitude,
            targetOrdnance,
            saturationAttack,
            purchased,
            purchased ? CommanderEconomyService.PriceOf(option.Definition) : 0f,
            Time.unscaledTime + PendingSpawnTimeoutSeconds,
            loadout)
        {
            Recovery = recovery,
        };
        pendingAircraftSpawns.Add(pendingSpawn);

        // Full tanks. A commander-bought airframe is flying a route to somewhere the player picked
        // and then coming back for another, and an airframe whose definition defaults to part fuel
        // turns for home before it has done either.
        if (!LaunchAiAircraft(hq, airbase, option.Definition, new LiveryKey(liveryIndex), loadout, 1f, hangar))
        {
            pendingAircraftSpawns.Remove(pendingSpawn);
            hq.AddFunds(munitions);
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(CommanderEconomyService.PriceOf(option.Definition));
            }

            RefuseLaunch(string.IsNullOrEmpty(LastLaunchRefusal)
                ? "The airbase rejected the spawn."
                : $"Cannot launch here: {LastLaunchRefusal}.");
            return;
        }

        // LaunchAiAircraft leaves supply exactly as it found it, so the stock comes off here: it
        // cancels the purchase's +1 above, or consumes one the faction already had.
        hq.ModifyUnitSupply(option.Definition, -1);

        SetStatus($"{GetModeLabel(option.Mode)}: {GetAircraftLabel(option.Definition)} / {option.LoadoutName}"
            + (purchased ? $" for {CommanderEconomyService.FundsLabel(CommanderEconomyService.PriceOf(option.Definition) + munitions)}." : " from stock.")
            + (!purchased && munitions > 0f ? $" Munitions {CommanderEconomyService.FundsLabel(munitions)}." : string.Empty));
    }

    private void TryAssignPendingAircraft(FactionHQ hq, Unit unit)
    {
        if (pendingAircraftSpawns.Count == 0 || unit is not Aircraft aircraft || aircraft.Player != null)
        {
            return;
        }

        // Two launches of one type can be waiting at once, at different fields: the airframe is
        // the one ordered from the field it appeared at.
        PendingAircraftSpawn? pending = null;
        float nearest = float.MaxValue;
        foreach (PendingAircraftSpawn candidate in pendingAircraftSpawns)
        {
            if (!ReferenceEquals(hq, candidate.Hq) || !ReferenceEquals(aircraft.definition, candidate.Option.Definition))
            {
                continue;
            }

            float distance = candidate.Airbase != null && candidate.Airbase.center != null
                ? (candidate.Airbase.center.position - aircraft.transform.position).sqrMagnitude
                : float.MaxValue - 1f;
            if (distance < nearest)
            {
                nearest = distance;
                pending = candidate;
            }
        }

        if (pending == null)
        {
            return;
        }

        pendingAircraftSpawns.Remove(pending);

        // Kept so a resupply puts back the weapons the player actually chose, rather than whatever
        // the base game's AI loadout picker would have hung on the pylons.
        if (pending.Loadout != null)
        {
            launchLoadouts[aircraft] = pending.Loadout;
        }

        if (pending.Option.Mode == AirCommandMode.Autopilot)
        {
            return;
        }

        AirMission mission = new(
            pending.Hq,
            pending.Option.Mode,
            pending.AreaCenter,
            pending.Radius,
            pending.TargetAltitude,
            pending.TargetOrdnance,
            pending.SaturationAttack,
            pending.PurchasedWithFunds,
            pending.PurchaseCost);

        // STANDBY is a landing order that never took off: the airframe is already on the deck, the
        // sweep parks it there, and the first order the player gives clears the order and lifts it.
        if (pending.Option.Mode == AirCommandMode.Standby && pending.Airbase != null && pending.Airbase.center != null)
        {
            GlobalPosition field = pending.Airbase.center.GlobalPosition();
            mission.Route.Add(field);
            mission.AreaCenter = field;
            mission.LandingBase = pending.Airbase;
            mission.Intent = LandingIntent.Hold;
        }

        missions[aircraft] = mission;
        if (pending.Recovery != CommanderRecoveryService.RecoveryMode.Off)
        {
            CommanderRecoveryService.Instance?.Assign(aircraft, pending.Recovery);
        }

        CommanderSelectionService.PinMissionUnit(
            aircraft,
            "AIR COMMAND",
            GetModeLabel(pending.Option.Mode));
    }

    private void PruneMissions()
    {
        staleAircraft.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                staleAircraft.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleAircraft.Count; i++)
        {
            RemoveMission(staleAircraft[i]);
        }

        staleAircraft.Clear();
        foreach (Aircraft aircraft in launchLoadouts.Keys)
        {
            if (aircraft == null || aircraft.disabled)
            {
                staleAircraft.Add(aircraft!);
            }
        }

        for (int i = 0; i < staleAircraft.Count; i++)
        {
            launchLoadouts.Remove(staleAircraft[i]);
        }

        staleAircraft.Clear();
        foreach (Aircraft aircraft in autopilotAltitudes.Keys)
        {
            if (aircraft == null || aircraft.disabled)
            {
                staleAircraft.Add(aircraft!);
            }
        }

        for (int i = 0; i < staleAircraft.Count; i++)
        {
            autopilotAltitudes.Remove(staleAircraft[i]);
        }
    }
}
