using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Mobile air defence that rides with the convoys. Every other air defence vehicle the commander owns
/// is recruited onto a base ring first, so in practice no capture push ever drove out with anything
/// that could shoot back at an attack aircraft. These hulls go to the convoys first and the ring takes
/// the rest (<see cref="RecruitAirDefence"/>).
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>Air defence escorts a capture push takes with it, on top of its squad.</summary>
    internal const int ConvoyAirDefencePerDrive = 2;

    /// <summary>Spaags a push takes with it when the commander has them.</summary>
    internal const int ConvoySpaagPerDrive = 1;

    /// <summary>The depot 3 gun a rich commander adds to each push. It has its own slot on top of
    /// <see cref="ConvoyAirDefencePerDrive"/>, because those two fill up long before depot 3 and an
    /// escort buy would then never want one.</summary>
    private const string SpaagName = "AeroSentry SPAAG";

    /// <summary>Income a minute that counts as a good economy. Below it the money goes on the army: a
    /// Spaag costs thirteen AFV6 AA.</summary>
    private const float SpaagEscortIncomePerMinute = 200f;

    /// <summary>The two hulls built to keep up with a column. Matched by name because the choice is a
    /// design call, not a stat: each faction's catalogue holds at most one of them, so whichever it
    /// has is what it buys. The plan rolls buy them for the rings beside the AFV6 AA; a capture push
    /// only ever takes one as an escort.</summary>
    private static readonly string[] ConvoyAirDefenceNames = { "AFV8 Mobile Air Defense", "Linebreaker SAM" };

    /// <summary>These hulls outside the rings, counted by <see cref="ReadOwnedVehicles"/>, so the
    /// ring's air-defence quota is not met by vehicles out with the convoys.</summary>
    private int ownedEscorts;

    internal static bool IsConvoyAirDefence(VehicleDefinition definition)
    {
        return System.Array.IndexOf(ConvoyAirDefenceNames, definition.unitName) >= 0;
    }

    internal static bool IsConvoyAirDefence(Unit unit)
    {
        return unit.definition is VehicleDefinition definition && IsConvoyAirDefence(definition);
    }

    /// <summary>Never one a mission stood at a station: no order moves it, so it can escort nothing.</summary>
    internal static bool IsConvoySpaag(Unit unit)
    {
        return unit.definition.unitName == SpaagName && !IsPlacedGuard(unit);
    }

    /// <summary>
    /// One Spaag a review while the pushes on the road are short of
    /// <see cref="ConvoySpaagPerDrive"/> each. Being in the catalogue is the depot 3 gate. The
    /// uncapped air-defence share is ignored: the rings fill it, and the count here is bounded by the
    /// pushes. A unit cap still stops it.
    /// </summary>
    private float TryBuyConvoySpaag(FactionHQ hq, float spendable)
    {
        int wanted = (CommanderCaptureService.Instance?.CountConvoys(hq) ?? 0) * ConvoySpaagPerDrive;
        if (wanted == 0
            || CommanderEconomyService.GetEnemyIncomePerMinute(hq) < SpaagEscortIncomePerMinute
            || (CommanderUnitCap.Enabled && IsAirDefenceShareFull()))
        {
            return 0f;
        }

        VehicleDefinition? spaag = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            if (catalog[i].unitName == SpaagName)
            {
                spaag = catalog[i];
                break;
            }
        }

        if (spaag == null || spaag.value > spendable || CommanderUnitCap.IsAiTypeFull(hq, spaag))
        {
            return 0f;
        }

        // The ones in a ring are not coming: a ring never releases a vehicle.
        int owned = hq.GetUnitSupply(spaag);
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && IsConvoySpaag(unit)
                && !IsDefendingUnit(unit))
            {
                owned++;
            }
        }

        float cost = Mathf.Max(0f, spaag.value);
        if (owned >= wanted || !CommanderGameAccess.TrySpend(hq, cost))
        {
            return 0f;
        }

        hq.ModifyUnitSupply(spaag, 1);
        NoteOwned(spaag);
        TotalPurchases++;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(spaag)} "
                + $"for {cost:0} to escort its convoys ({owned + 1} of {wanted}).");
        return cost;
    }

    /// <summary>
    /// One escort a review while the pushes on the road are short of
    /// <see cref="ConvoyAirDefencePerDrive"/> each, and only with money left over: an escort is never
    /// worth a review's whole pot, so it waits until it fits in what is spendable.
    /// </summary>
    private float TryBuyConvoyAirDefence(FactionHQ hq, float spendable)
    {
        int wanted = (CommanderCaptureService.Instance?.CountConvoys(hq) ?? 0) * ConvoyAirDefencePerDrive;
        if (wanted == 0 || spendable <= 0f || IsAirDefenceShareFull())
        {
            return 0f;
        }

        // The reserve counts too: a purchase sits there for a depot cooldown before it rolls out,
        // and counting only the field bought another escort every review until the first arrived.
        int owned = ownedEscorts;
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (!IsConvoyAirDefence(definition))
            {
                continue;
            }

            owned += hq.GetUnitSupply(definition);
            if (definition.value <= spendable && (best == null || definition.value > best.value))
            {
                best = definition;
            }
        }

        if (owned >= wanted || best == null)
        {
            return 0f;
        }

        float cost = Mathf.Max(0f, best.value);
        if (!CommanderGameAccess.TrySpend(hq, cost))
        {
            return 0f;
        }

        hq.ModifyUnitSupply(best, 1);
        NoteOwned(best);
        TotalPurchases++;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(best)} "
                + $"for {cost:0} to escort its convoys ({owned + 1} of {wanted}).");
        return cost;
    }
}
