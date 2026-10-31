using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The quartermaster: how the enemy commander drafts an army for Ground Control Draft, and what the
/// player's AUTO-FILL runs. A partial of the enemy service because it reads the same classifiers the
/// buy loop does (combat vehicle, air defence, air role, SAM site parts), so a vehicle that loop
/// would never buy is not drafted either.
/// </summary>
/// <remarks>
/// It drafts blind: at the start there is no enemy force to read, so the plan is rolled rather than
/// chosen off <see cref="ChoosePlan"/>. What stays fixed is the shape every plan needs in a match
/// with no reinforcements, which is exactly where the buy loop's per-review rules do not apply:
/// something that can take a base, an air-defence umbrella that is a share of the force, a repair
/// truck, munitions trucks behind the column, and enough ammunition on the shelf to rearm.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>Share of the budget the air wing may take, when the side has anything to fly.</summary>
    private const float DraftAirShare = 0.35f;

    /// <summary>Share of the budget a radar SAM site (launcher, fire control, radar) may take.</summary>
    private const float DraftSamSiteShare = 0.3f;

    /// <summary>Capture-capable fighting vehicles a draft always carries, or it cannot win a base.</summary>
    private const int DraftCaptureUnits = 3;

    /// <summary>Repair vehicles drafted: nothing else fixes a depot in a match with no money.</summary>
    private const int DraftRepairVehicles = 2;

    /// <summary>Fighting vehicles per munitions truck drafted behind the column.</summary>
    private const int DraftVehiclesPerAmmoTruck = 10;

    /// <summary>Budget at which a draft carries a radar picket, when the roster has one.</summary>
    private const float DraftPicketBudget = 6000f;

    /// <summary>Not <c>UnityEngine.Random</c>: the map reseeds that with 42 on every load, and the
    /// enemy would draft the same army every match.</summary>
    private static readonly System.Random DraftDice = new();

    /// <summary>
    /// Adds to <paramref name="picks"/> until the budget is spent, keeping what is already there.
    /// Static and scene-free apart from the roster it is handed, so AUTO-FILL runs it for the player
    /// with the player's own picks as the starting point.
    /// </summary>
    internal static void DraftArmy(FactionHQ hq, float budget, IReadOnlyList<DraftEntry> roster, Dictionary<string, int> picks)
    {
        CommanderDraftService? draft = CommanderDraftService.Instance;
        float left = budget - CommanderDraftService.CostOf(roster, picks);
        if (left <= 0f || draft == null)
        {
            return;
        }

        EnemyPlan plan = (EnemyPlan)DraftDice.Next(System.Enum.GetValues(typeof(EnemyPlan)).Length);
        DraftTally tally = new(roster, picks);

        // Repair vehicles: the only way a depot or a bunker comes back in a draft.
        DraftEntry? repair = CheapestWhere(roster, left, static entry => entry.Definition is VehicleDefinition vehicle && IsBuilderVehicle(vehicle));
        for (int i = tally.Repair; repair != null && i < DraftRepairVehicles && left >= budget * 0.1f; i++)
        {
            TryDraft(repair, picks, ref left);
        }

        DraftAirWing(hq, budget, roster, picks, ref left);
        DraftSamSite(budget, roster, picks, ref left);
        DraftGround(plan, roster, picks, tally, ref left);
    }

    private static void DraftAirWing(
        FactionHQ hq, float budget, IReadOnlyList<DraftEntry> roster, Dictionary<string, int> picks, ref float left)
    {
        float airBudget = Mathf.Min(left, budget * DraftAirShare) - CostOfGroup(roster, picks, DraftGroup.Aircraft);
        if (airBudget <= 0f)
        {
            return;
        }

        float spent = 0f;
        if (budget >= DraftPicketBudget)
        {
            DraftEntry? picket = CheapestWhere(
                roster,
                Mathf.Min(left, airBudget),
                entry => entry.Definition is AircraftDefinition aircraft && IsRadarPicket(aircraft, hq));
            if (picket != null && Count(picks, picket.Key) == 0 && TryDraft(picket, picks, ref left))
            {
                spent += picket.Cost;
            }
        }

        int fighters = 0;
        int strikers = 0;
        for (int guard = 0; guard < 60; guard++)
        {
            // One fighter for every two strike airframes, fighters first so the strike wing has cover.
            AirRole wanted = fighters <= strikers / 2 ? AirRole.Fighter : AirRole.Strike;
            DraftEntry? pick = PickAirframe(hq, roster, picks, wanted, airBudget - spent)
                ?? PickAirframe(hq, roster, picks, null, airBudget - spent);
            if (pick == null || !TryDraft(pick, picks, ref left))
            {
                return;
            }

            spent += pick.Cost;
            if (pick.Definition is AircraftDefinition aircraft && FitsRole(aircraft, AirRole.Fighter))
            {
                fighters++;
            }
            else
            {
                strikers++;
            }
        }
    }

    /// <summary>A launcher, its fire-control truck and a radar, drafted together or not at all: the
    /// launcher does not fire without the other two within 300 m.</summary>
    private static void DraftSamSite(
        float budget, IReadOnlyList<DraftEntry> roster, Dictionary<string, int> picks, ref float left)
    {
        DraftEntry? launcher = CheapestWhere(roster, left, static entry => entry.Definition is VehicleDefinition vehicle && IsSamSiteLauncher(vehicle));
        DraftEntry? fireControl = CheapestWhere(roster, left, static entry => entry.Definition is VehicleDefinition vehicle && IsFireControlVehicle(vehicle));
        DraftEntry? radar = CheapestWhere(roster, left, static entry => entry.Definition is VehicleDefinition vehicle
            && vehicle.vehicleType == VehicleType.RDR
            && !CommanderGameAccess.IsTrailerVehicleDefinition(vehicle));
        if (launcher == null || fireControl == null || radar == null || Count(picks, launcher.Key) > 0)
        {
            return;
        }

        float site = launcher.Cost + fireControl.Cost + radar.Cost;
        if (site > budget * DraftSamSiteShare || site > left)
        {
            return;
        }

        TryDraft(launcher, picks, ref left);
        TryDraft(fireControl, picks, ref left);
        TryDraft(radar, picks, ref left);
    }

    private static void DraftGround(
        EnemyPlan plan, IReadOnlyList<DraftEntry> roster, Dictionary<string, int> picks, DraftTally tally, ref float left)
    {
        DraftEntry? ammoTruck = CheapestWhere(roster, float.MaxValue, static entry => entry.Definition is VehicleDefinition vehicle
            && IsSupplyVehicle(vehicle)
            && !IsBuilderVehicle(vehicle)
            && !CommanderGameAccess.IsTrailerVehicleDefinition(vehicle));
        for (int guard = 0; guard < 400; guard++)
        {
            if (ammoTruck != null && tally.Combat >= (tally.AmmoTrucks + 1) * DraftVehiclesPerAmmoTruck)
            {
                if (!TryDraft(ammoTruck, picks, ref left))
                {
                    ammoTruck = null;
                }
                else
                {
                    tally.AmmoTrucks++;
                    continue;
                }
            }

            bool wantCapture = tally.Capture < DraftCaptureUnits;
            bool wantAirDefence = tally.AirDefence < Mathf.CeilToInt(tally.Combat * AirDefenceShareOfForce);
            DraftEntry? pick = PickGround(roster, picks, tally, plan, left, wantCapture, wantAirDefence && !wantCapture)
                ?? PickGround(roster, picks, tally, plan, left, false, false);
            if (pick == null || !TryDraft(pick, picks, ref left) || pick.Definition is not VehicleDefinition vehicle)
            {
                return;
            }

            tally.Note(vehicle);
        }
    }

    /// <summary>A weighted roll among the fighting vehicles that fit, damped by what is already drafted:
    /// the same spread the buy loop's <see cref="Choose"/> gets from its owned counts.</summary>
    private static DraftEntry? PickGround(
        IReadOnlyList<DraftEntry> roster,
        Dictionary<string, int> picks,
        DraftTally tally,
        EnemyPlan plan,
        float left,
        bool captureOnly,
        bool airDefenceOnly)
    {
        DraftEntry? pick = null;
        double total = 0d;
        for (int i = 0; i < roster.Count; i++)
        {
            DraftEntry entry = roster[i];
            if (entry.Cost > left
                || entry.Definition is not VehicleDefinition vehicle
                || !IsCombatVehicle(vehicle)
                || IsRadarGuidedAirDefence(vehicle)
                || CommanderGameAccess.IsTrailerVehicleDefinition(vehicle)
                || (captureOnly && vehicle.captureStrength <= 0f)
                || (airDefenceOnly && !IsAirDefence(vehicle)))
            {
                continue;
            }

            float weight = GetPlanWeight(plan, vehicle.vehicleType)
                / (1 + tally.OfType(vehicle.vehicleType) + Count(picks, entry.Key));
            if (weight <= 0f)
            {
                continue;
            }

            total += weight;
            if (DraftDice.NextDouble() * total <= weight)
            {
                pick = entry;
            }
        }

        return pick;
    }

    private static DraftEntry? PickAirframe(
        FactionHQ hq, IReadOnlyList<DraftEntry> roster, Dictionary<string, int> picks, AirRole? role, float airBudget)
    {
        DraftEntry? pick = null;
        double total = 0d;
        for (int i = 0; i < roster.Count; i++)
        {
            DraftEntry entry = roster[i];
            if (entry.Cost > airBudget
                || entry.Definition is not AircraftDefinition aircraft
                || IsRadarPicket(aircraft, hq)
                || GetAirRole(aircraft) == AirRole.Transport
                || (role.HasValue && !FitsRole(aircraft, role.Value)))
            {
                continue;
            }

            // Spread over the roster, leaning slightly toward the dearer airframes: with no
            // replacements, one good jet outlives two cheap ones.
            float weight = Mathf.Sqrt(Mathf.Max(1f, entry.Cost)) / (1 + Count(picks, entry.Key));
            total += weight;
            if (DraftDice.NextDouble() * total <= weight)
            {
                pick = entry;
            }
        }

        return pick;
    }

    private static bool TryDraft(DraftEntry entry, Dictionary<string, int> picks, ref float left)
    {
        int have = Count(picks, entry.Key);
        if (entry.Cost > left || have >= CommanderDraftService.MaxPerEntry)
        {
            return false;
        }

        picks[entry.Key] = have + 1;
        left -= entry.Cost;
        return true;
    }

    private static int Count(Dictionary<string, int> picks, string key)
    {
        return picks.TryGetValue(key, out int count) ? count : 0;
    }

    private static DraftEntry? CheapestWhere(IReadOnlyList<DraftEntry> roster, float left, System.Func<DraftEntry, bool> match)
    {
        DraftEntry? best = null;
        for (int i = 0; i < roster.Count; i++)
        {
            DraftEntry entry = roster[i];
            if (entry.Cost <= left && (best == null || entry.Cost < best.Cost) && match(entry))
            {
                best = entry;
            }
        }

        return best;
    }

    private static float CostOfGroup(IReadOnlyList<DraftEntry> roster, Dictionary<string, int> picks, DraftGroup group)
    {
        float cost = 0f;
        for (int i = 0; i < roster.Count; i++)
        {
            if (roster[i].Group == group)
            {
                cost += roster[i].Cost * Count(picks, roster[i].Key);
            }
        }

        return cost;
    }

    /// <summary>What a pick list already holds, by the classes the draft rules care about.</summary>
    private sealed class DraftTally
    {
        private readonly int[] byType = new int[System.Enum.GetValues(typeof(VehicleType)).Length];

        internal DraftTally(IReadOnlyList<DraftEntry> roster, Dictionary<string, int> picks)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].Definition is not VehicleDefinition vehicle)
                {
                    continue;
                }

                int count = Count(picks, roster[i].Key);
                for (int n = 0; n < count; n++)
                {
                    Note(vehicle);
                }
            }
        }

        internal int Combat { get; private set; }

        internal int Capture { get; private set; }

        internal int AirDefence { get; private set; }

        internal int Repair { get; private set; }

        internal int AmmoTrucks { get; set; }

        internal int OfType(VehicleType type)
        {
            int index = (int)type;
            return index >= 0 && index < byType.Length ? byType[index] : 0;
        }

        internal void Note(VehicleDefinition vehicle)
        {
            if (IsBuilderVehicle(vehicle))
            {
                Repair++;
                return;
            }

            if (IsSupplyVehicle(vehicle))
            {
                AmmoTrucks++;
                return;
            }

            if (!IsCombatVehicle(vehicle))
            {
                return;
            }

            Combat++;
            int index = (int)vehicle.vehicleType;
            if (index >= 0 && index < byType.Length)
            {
                byType[index]++;
            }

            if (vehicle.captureStrength > 0f)
            {
                Capture++;
            }

            if (IsAirDefence(vehicle))
            {
                AirDefence++;
            }
        }
    }
}
