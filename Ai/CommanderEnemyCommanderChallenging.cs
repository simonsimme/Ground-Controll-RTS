using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// CHALLENGING difficulty: the same money and the same roster as NORMAL, spent on what beats the
/// force actually standing opposite. NORMAL reads the opposition into one of four plans and rolls
/// a convoy from that plan's odds; this scores every vehicle the depot sells against every unit
/// the opponent fields, with the game's own numbers, and buys the ones that come out on top.
/// The other half of the difficulty is the massed push in <c>CommanderCaptureService.ReviewMainEffort</c>.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    internal const int DifficultyNormal = 0;
    internal const int DifficultyChallenging = 1;

    /// <summary>Vehicle depots a CHALLENGING commander builds at each base (NORMAL: one), since the
    /// depots are what limits its army.</summary>
    internal const int ChallengingDepotsPerBase = 4;

    /// <summary>The massed push's smallest wave, and its share of the opponent's whole army, with the
    /// income-first tactics. Sized against the whole army, not just the defenders on the objective.</summary>
    internal const int ChallengingMinWave = 20;
    internal const float ChallengingArmyShare = 0.6f;

    /// <summary>Ground vehicles a CHALLENGING review may buy, and the most it lets wait in the reserve.
    /// Depots field about three vehicles a minute, so buying past that only queues.</summary>
    private const int ChallengingPurchasesPerReview = 8;
    private const int ChallengingReserveDepth = 6;

    /// <summary>How much a vehicle the opposition can hurt is marked down, at most.</summary>
    private const float ExposurePenalty = 0.5f;

    /// <summary>How hard the pick leans on price. Firmly: a depot fields one vehicle at a time
    /// whatever it costs, so the tank is worth more of those turns than the carrier.</summary>
    private const float CounterValuePower = 0.6f;

    /// <summary>How fast the odds of a type fall as the commander owns more of it.</summary>
    private const float CounterOwnedDamping = 0.2f;

    /// <summary>Share of a review's budget a CHALLENGING commander sets aside for airframes, against
    /// NORMAL's 0.6. Not much lower: cut to 0.3 it had no aircraft up for half a match, and the
    /// other side's attack aircraft then killed 30 of the 74 vehicles its push lost.</summary>
    private const float ChallengingAirframeBudgetShare = 0.6f;

    /// <summary>Radar pickets a CHALLENGING commander keeps up, against NORMAL's three. The first
    /// match lost seven of them, a fifth of everything that side spent on aircraft.</summary>
    private const int ChallengingRadarPickets = 0;

    /// <summary>Airframes a CHALLENGING commander may buy a review. No price cap: the fund saves for
    /// the expensive ones.</summary>
    private const int ChallengingAirframesPerReview = 3;
    private const float ChallengingAirFundFloor = 250f;

    /// <summary>Artillery a CHALLENGING commander keeps, as a share of its fighting vehicles, the fewest
    /// it wants, and the share of a review's budget saved toward the next gun.</summary>
    private const float ChallengingArtilleryShare = 0.25f;
    private const int ChallengingMinArtillery = 4;
    private const float ChallengingArtilleryBudgetShare = 0.35f;

    /// <summary>Assault vehicles per air defence vehicle that travels with the massed push.</summary>
    private const int AssaultVehiclesPerEscort = 5;

    /// <summary>Seconds between neutral capture landings for a CHALLENGING commander (NORMAL: 240).</summary>
    private const float ChallengingNeutralAirCaptureSeconds = 90f;

    /// <summary>Seconds between two fixes on the same gun. See <see cref="LocateBattery"/>.</summary>
    private const float BatteryFixSeconds = 5f;

    /// <summary>How long after the last hit on an income building its fighter CAP stays. See
    /// <c>RaiseEcoAlarm</c>.</summary>
    private const float EcoAlarmSeconds = 120f;

    /// <summary>Strike aircraft a CHALLENGING wave waits for (NORMAL: three), so they do not go in
    /// piecemeal.</summary>
    private const int ChallengingStrikeWaveSize = 5;

    /// <summary>Gunship helicopters a CHALLENGING commander keeps (NORMAL: two rotary airframes, the
    /// second a transport).</summary>
    private const int ChallengingGunships = 6;

    /// <summary>Air defence vehicles a CHALLENGING commander wants at its first base and at every
    /// other one. What it owns past that travels with the push. NORMAL keeps three in ten of its
    /// army on it and all of it at home.</summary>
    private const int ChallengingAirDefencePerBase = 9;
    private const int ChallengingAirDefencePerOutpost = 3;

    /// <summary>What a unit with no weapons on its prefab (an aircraft, armed from a loadout) is
    /// taken to pierce: everything.</summary>
    private const float UnknownPunch = 99f;

    /// <summary>Each definition's weapons as (armour it pierces, what it is good against), read off
    /// the prefab once. Asset data, so <see cref="LogCounterRosterOnce"/> prints it.</summary>
    private static readonly Dictionary<UnitDefinition, List<(float Tier, RoleIdentity Role)>> Arsenals = new();

    /// <summary>The opposing force by type, read once per CHALLENGING review.</summary>
    private readonly Dictionary<UnitDefinition, int> opposition = new();
    private readonly Dictionary<FactionHQ, int> loggedCounterRoster = new();

    /// <summary>Guns a CHALLENGING commander has located by being shelled, and when.</summary>
    private readonly Dictionary<uint, float> batteryFixes = new();
    private float oppositionWeight;
    private int oppositionAircraft;

    /// <summary>This review is a CHALLENGING commander's, so <see cref="Pick"/> counter-buys.</summary>
    private bool counterBuying;

    internal static string GetDifficultyLabel(int difficulty)
    {
        return difficulty == DifficultyChallenging ? "CHALLENGING" : "NORMAL";
    }

    /// <summary>
    /// Whether this commander plays CHALLENGING. The saved setting, for every AI faction alike.
    /// </summary>
    internal static bool IsChallenging(FactionHQ? hq)
    {
        if (hq == null || hq.faction == null)
        {
            return false;
        }

        return CommanderSettings.EnemyDifficulty == DifficultyChallenging;
    }

    /// <summary>CHALLENGING with its income-first tactics: the eco alarm, the wave discipline and
    /// the larger massed push.</summary>
    internal static bool HasChallengingTactics(FactionHQ? hq)
    {
        return IsChallenging(hq);
    }

    /// <summary>A ground vehicle that fights and can be ordered, artillery aside (the batteries own
    /// that). What the massed push is made of and what it counts on the objective.</summary>
    internal static bool IsFightingVehicle(Unit? unit)
    {
        return unit is GroundVehicle
            && !unit.disabled
            && unit.definition is VehicleDefinition definition
            && definition.vehicleType != VehicleType.ART
            && IsCombatVehicle(definition)
            && !IsRadarGuidedAirDefence(definition)
            && CommanderGameAccess.GetUnitCommand(unit) != null;
    }

    /// <summary>The home ring is short and this vehicle is where the ring recruits, so the massed
    /// push leaves it alone.</summary>
    internal static bool IsWantedByGuard(FactionHQ hq, Unit unit)
    {
        return Instance != null
            && Instance.states.TryGetValue(hq, out CommanderState state)
            && (DefencePriority(unit) > 0 ? state.AirGuardShort : state.GuardShort)
            && IsNearOwnBase(hq, unit.transform.GlobalPosition(), RecruitRadiusMeters);
    }

    /// <summary>Air defence vehicles a CHALLENGING commander's rings hold between them.</summary>
    private static int ChallengingAirDefenceTarget(int bases)
    {
        return bases <= 0 ? 0 : ChallengingAirDefencePerBase + (ChallengingAirDefencePerOutpost * (bases - 1));
    }

    /// <summary>
    /// A rotary airframe that can be landed on an empty base to take it: no cargo aboard (the game
    /// would fly it off on a delivery) and no hook (that one is the builder airlift).
    /// </summary>
    private static bool HasCaptureLander(FactionHQ hq)
    {
        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            if (CommanderAirCommandService.HasRotaryPilot(aircraft)
                && !IsCargoConfigured(aircraft)
                && CommanderSlingLiftService.GetHook(aircraft) == null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyNeutralBase()
    {
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            if (IsNeutralTarget(entry.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How well <paramref name="attacker"/> does against <paramref name="target"/>, 0 to 1: the best
    /// of its weapons that pierce the target's armour, rated by the game's own
    /// <c>RoleIdentity.OpportunityAgainst</c>. The armour gate is the base game's
    /// (<c>CombatAI.AnalyzeTarget</c> drops a weapon below the target's tier), which is why a
    /// carrier's autocannon counts for nothing against a tank and its missile for everything.
    /// </summary>
    private static float Matchup(UnitDefinition attacker, UnitDefinition target)
    {
        List<(float Tier, RoleIdentity Role)> arsenal = GetArsenal(attacker);
        float best = 0f;
        for (int i = 0; i < arsenal.Count; i++)
        {
            if (arsenal[i].Tier >= target.armorTier)
            {
                best = Mathf.Max(best, arsenal[i].Role.OpportunityAgainst(target.typeIdentity));
            }
        }

        return best;
    }

    private static List<(float Tier, RoleIdentity Role)> GetArsenal(UnitDefinition definition)
    {
        if (Arsenals.TryGetValue(definition, out List<(float Tier, RoleIdentity Role)> arsenal))
        {
            return arsenal;
        }

        arsenal = new List<(float Tier, RoleIdentity Role)>();
        Weapon[]? weapons = definition.unitPrefab != null
            ? definition.unitPrefab.GetComponentsInChildren<Weapon>(true)
            : null;
        for (int i = 0; weapons != null && i < weapons.Length; i++)
        {
            WeaponInfo? info = weapons[i] != null ? weapons[i].info : null;
            if (info != null && !info.jammer && !info.cargo && !info.troops)
            {
                arsenal.Add((info.armorTierEffectiveness, info.effectiveness));
            }
        }

        if (arsenal.Count == 0)
        {
            arsenal.Add((UnknownPunch, definition.roleIdentity));
        }

        Arsenals[definition] = arsenal;
        return arsenal;
    }

    /// <summary>
    /// Self-check for the counter pick's arithmetic: the score must rise with how much of the
    /// opposition a vehicle can hurt and fall with how much of it can hurt the vehicle back.
    /// </summary>
    private static void CheckCounterWeights()
    {
        if (CounterWeight(0.8f, 0f, 30f, 0) <= CounterWeight(0.4f, 0f, 30f, 0)
            || CounterWeight(0.8f, 1f, 30f, 0) >= CounterWeight(0.8f, 0f, 30f, 0)
            || CounterWeight(0.8f, 0f, 30f, 10) >= CounterWeight(0.8f, 0f, 30f, 0)
            || CounterWeight(0f, 0f, 30f, 0) > 0f)
        {
            CommanderPlugin.Log.LogError(
                "Enemy counter self-check FAILED: the CHALLENGING pick no longer prefers the vehicle that "
                    + "hurts more of the opposition and is hurt by less of it.");
        }
    }

    private static float CounterWeight(float offence, float exposure, float value, int ownedOfType)
    {
        return offence * offence
            * (1f - (ExposurePenalty * Mathf.Clamp01(exposure)))
            * Mathf.Pow(Mathf.Max(1f, value), CounterValuePower)
            / (1f + (CounterOwnedDamping * ownedOfType));
    }

    /// <summary>One opposing unit's share of the read. Mostly a head count: prices on this roster
    /// run from 0 to 1000 and say little about how dangerous a vehicle is.</summary>
    private static float OppositionWeight(UnitDefinition definition)
    {
        return 1f + (Mathf.Sqrt(Mathf.Max(0f, definition.value)) / 10f);
    }

    /// <summary>
    /// Counter-battery: a gun that lands a round on a CHALLENGING commander's unit is put on that
    /// commander's contact list, where its own guns and attack aircraft can find it.
    /// </summary>
    private void LocateBattery(FactionHQ hq, PersistentID by, bool anyAttacker)
    {
        if (!by.IsValid
            || !hq.IsServer
            || (batteryFixes.TryGetValue(by.Id, out float at) && Time.time - at < BatteryFixSeconds)
            || !by.TryGetUnit(out Unit gun)
            || gun == null
            || gun.disabled
            || ReferenceEquals(gun.NetworkHQ, hq)
            || (!anyAttacker && gun.definition is not VehicleDefinition { vehicleType: VehicleType.ART }))
        {
            return;
        }

        batteryFixes[by.Id] = Time.time;
        hq.RpcUpdateTrackingInfo(by);
    }

    /// <summary>
    /// A CHALLENGING commander's income building is being hit: the attacker goes on its contact list
    /// and half its fighters fly a CAP over the building until the raid has been quiet for
    /// <see cref="EcoAlarmSeconds"/>. The review is pulled forward, since that is what tasks the wing.
    /// </summary>
    private void RaiseEcoAlarm(FactionHQ hq, CommanderState state, Unit building, PersistentID by)
    {
        LocateBattery(hq, by, anyAttacker: true);
        bool fresh = Time.time >= state.EcoAlarmUntil;
        state.EcoAlarmUntil = Time.time + EcoAlarmSeconds;
        state.EcoAlarmAt = building.transform.GlobalPosition();
        if (fresh)
        {
            nextReviewAt = 0f;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}, CHALLENGING) scrambles its fighters over {CommanderGameAccess.GetUnitLabel(building)}: it is under attack.");
        }
    }

    /// <summary>
    /// Saves toward, and buys, the CHALLENGING commander's guns: the cheapest artillery the depot
    /// sells, until a quarter of its fighting vehicles are guns. A fund for the reason the air wing
    /// has one: a gun costs a dozen carriers, and a review's slice never reaches its price.
    /// Returns what it took out of this review's pot.
    /// </summary>
    private float TryBuyArtillery(FactionHQ hq, CommanderState state, float spendable)
    {
        VehicleDefinition? gun = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.vehicleType == VehicleType.ART
                && IsCombatVehicle(definition)
                && (gun == null || definition.value < gun.value))
            {
                gun = definition;
            }
        }

        int owned = CountOwnedType(VehicleType.ART);
        if (gun == null || IsArtilleryCapped(gun) || owned >= Mathf.Max(ChallengingMinArtillery, Mathf.CeilToInt(ownedCombat * ChallengingArtilleryShare)))
        {
            return 0f;
        }

        float cost = Mathf.Max(1f, gun.value);
        float fund = state.ArtilleryFund;
        float taken = AccrueFund(ref fund, spendable * ChallengingArtilleryBudgetShare, cost * 2f);
        while (fund >= cost && CommanderGameAccess.TrySpend(hq, cost))
        {
            fund -= cost;
            hq.ModifyUnitSupply(gun, 1);
            NoteOwned(gun);
            TotalPurchases++;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}, CHALLENGING) bought {CommanderGameAccess.GetVehicleLabel(gun)} for {cost:0} "
                    + $"({owned + 1} gun(s) to {ownedCombat} fighting vehicles).");
            if (IsArtilleryCapped(gun) || ++owned >= Mathf.Max(ChallengingMinArtillery, Mathf.CeilToInt(ownedCombat * ChallengingArtilleryShare)))
            {
                break;
            }
        }

        state.ArtilleryFund = fund;
        return taken;
    }

    /// <summary>
    /// Adds what is bought and still waiting in the reserve to this review's owned counts, and
    /// returns how many fighting vehicles that is. NORMAL counts only what is on the field.
    /// </summary>
    private int NoteReserve(FactionHQ hq)
    {
        int waiting = 0;
        foreach (KeyValuePair<VehicleDefinition, FactionHQ.RuntimeSupply> entry in hq.VehicleSupply)
        {
            for (int i = 0; entry.Key != null && i < entry.Value.Count; i++)
            {
                NoteOwned(entry.Key);
                waiting += IsCombatVehicle(entry.Key) ? 1 : 0;
            }
        }

        return waiting;
    }

    /// <summary>What the other side has on the field, by type. Every vehicle, aircraft and ship:
    /// the same whole-faction read NORMAL's <see cref="ReadForce"/> makes, kept per definition.</summary>
    private void ReadOpposition(FactionHQ opponent)
    {
        opposition.Clear();
        oppositionWeight = 0f;
        oppositionAircraft = 0;
        if (opponent.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in opponent.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit.definition == null
                || !(unit is GroundVehicle || unit is Aircraft || unit is Ship))
            {
                continue;
            }

            opposition.TryGetValue(unit.definition, out int count);
            opposition[unit.definition] = count + 1;
            oppositionWeight += OppositionWeight(unit.definition);
            oppositionAircraft += unit is Aircraft ? 1 : 0;
        }
    }

    /// <summary>How much of the opposition this vehicle hurts, and how much of it hurts back.</summary>
    private void ScoreAgainstOpposition(UnitDefinition definition, out float offence, out float exposure)
    {
        offence = 0f;
        exposure = 0f;
        if (oppositionWeight <= 0f)
        {
            return;
        }

        foreach (KeyValuePair<UnitDefinition, int> entry in opposition)
        {
            float weight = entry.Value * OppositionWeight(entry.Key);
            offence += weight * Matchup(definition, entry.Key);
            exposure += weight * Matchup(entry.Key, definition);
        }

        offence /= oppositionWeight;
        exposure /= oppositionWeight;
    }

    /// <summary>
    /// One convoy vehicle. NORMAL rolls on its plan; CHALLENGING rolls on how each vehicle scores
    /// against the opposition, and falls back to the plan while there is nothing to read.
    /// </summary>
    private VehicleDefinition? Pick(float budget, EnemyPlan plan, bool airDefenceOnly)
    {
        return counterBuying && oppositionWeight > 0f
            ? ChooseCounter(budget, airDefenceOnly) ?? Choose(budget, plan, airDefenceOnly)
            : Choose(budget, plan, airDefenceOnly);
    }

    private VehicleDefinition? ChooseCounter(float budget, bool airDefenceOnly)
    {
        VehicleDefinition? pick = null;
        float total = 0f;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value > budget
                || !IsCombatVehicle(definition)
                || IsRadarGuidedAirDefence(definition)
                || IsArtilleryCapped(definition)
                || IsOutclassedAirDefence(definition, budget)
                || IsAirDefenceFull(definition)
                || (airDefenceOnly && !IsAirDefence(definition)))
            {
                continue;
            }

            // Air defence is bought to a count (see the review), never rolled: it rates a little
            // against everything, costs next to nothing and never leaves the base, and the first
            // match's counter roll bought 117 of them and called it an army.
            if (!airDefenceOnly && IsAirDefence(definition))
            {
                continue;
            }

            float weight;
            if (airDefenceOnly)
            {
                // A forced launcher answers aircraft whatever is flying this minute.
                weight = CounterWeight(definition.roleIdentity.antiAir, 0f, definition.value, 0);
            }
            else
            {
                ScoreAgainstOpposition(definition, out float offence, out float exposure);
                weight = CounterWeight(offence, exposure, definition.value, CountOwnedType(definition.vehicleType));
            }

            if (weight <= 0f)
            {
                continue;
            }

            total += weight;
            if (Random.value * total <= weight)
            {
                pick = definition;
            }
        }

        return pick;
    }

    /// <summary>
    /// The numbers the counter pick runs on, once per commander: every vehicle it can buy with its
    /// armour tier, what it pierces and what the game rates it against. All of it is asset data a
    /// decompile cannot read, so this line is the only place to check the pick against.
    /// </summary>
    private void LogCounterRosterOnce(FactionHQ hq)
    {
        // Again whenever a depot upgrade opens more of the roster.
        if (loggedCounterRoster.TryGetValue(hq, out int logged) && logged == catalog.Count)
        {
            return;
        }

        loggedCounterRoster[hq] = catalog.Count;

        StringBuilder text = new();
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            List<(float Tier, RoleIdentity Role)> arsenal = GetArsenal(definition);
            float punch = 0f;
            for (int w = 0; w < arsenal.Count; w++)
            {
                punch = Mathf.Max(punch, arsenal[w].Tier);
            }

            text.Append(i > 0 ? "; " : string.Empty).AppendFormat(
                CultureInfo.InvariantCulture,
                "{0} [{1}, {2:0}, armour {3:0.#}, pierces {4:0.#}, surface {5:0.##}, air {6:0.##}, missile {7:0.##}, capture {8:0.#}, as target s{9:0.#}/a{10:0.#}]",
                CommanderGameAccess.GetVehicleLabel(definition),
                definition.vehicleType,
                definition.value,
                definition.armorTier,
                punch,
                definition.roleIdentity.antiSurface,
                definition.roleIdentity.antiAir,
                definition.roleIdentity.antiMissile,
                definition.captureStrength,
                definition.typeIdentity.surface,
                definition.typeIdentity.air);
        }

        CommanderPlugin.Log.LogInfo($"Counter roster ({hq.faction.name}): {text}");
    }

    /// <summary>What the CHALLENGING commander read and what it rates best against it, for the log.</summary>
    private void LogCounterRead(FactionHQ hq)
    {
        StringBuilder text = new();
        VehicleDefinition? first = null;
        VehicleDefinition? second = null;
        float firstScore = 0f;
        float secondScore = 0f;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (!IsCombatVehicle(definition) || IsRadarGuidedAirDefence(definition))
            {
                continue;
            }

            ScoreAgainstOpposition(definition, out float offence, out float exposure);
            float score = CounterWeight(offence, exposure, definition.value, 0);
            if (score > firstScore)
            {
                second = first;
                secondScore = firstScore;
                first = definition;
                firstScore = score;
            }
            else if (score > secondScore)
            {
                second = definition;
                secondScore = score;
            }
        }

        int vehicles = 0;
        foreach (KeyValuePair<UnitDefinition, int> entry in opposition)
        {
            vehicles += entry.Key is VehicleDefinition ? entry.Value : 0;
        }

        text.Append($"Enemy commander ({hq.faction.name}, CHALLENGING) reads {vehicles} vehicle(s) and {oppositionAircraft} aircraft opposite");
        if (first != null)
        {
            text.Append($"; rates {CommanderGameAccess.GetVehicleLabel(first)} best against them");
            if (second != null)
            {
                text.Append($", then {CommanderGameAccess.GetVehicleLabel(second)}");
            }
        }

        CommanderPlugin.Log.LogInfo(text.Append('.').ToString());
    }
}
