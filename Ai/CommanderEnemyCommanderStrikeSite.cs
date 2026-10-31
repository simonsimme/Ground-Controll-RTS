using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The ballistic strike site: every ballistic launcher parks round one ammo dump in front of the
/// artillery depot, shells the nearest of the player's bases in reach, and one munitions truck runs
/// ordnance from a bunker to the dump so the launchers can reload where they stand.
/// </summary>
/// <remarks>
/// The launchers do not fire on their own: <c>FireControl.StrategicTargetCheck</c> only assesses a
/// strategic weapon above the mission's strategic escalation, which the duel never reaches. Area
/// fire (the GOD nukes' path) fires the station itself with the round aimed at a point, so it needs
/// no target and no escalation.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>How far in front of the artillery depot, toward the target, the site stands.</summary>
    private const float StrikeSiteForwardMeters = 1000f;

    /// <summary>Launchers park on a ring this share of the dump's rearm reach from its middle.</summary>
    private const float StrikeSlotReachShare = 0.5f;

    private const float StrikeSlotMinMeters = 25f;

    private const float StrikeSlotMaxMeters = 60f;

    /// <summary>Used for the slot ring when the game has no dump to read a reach off.</summary>
    private const float StrikeSlotFallbackMeters = 40f;

    /// <summary>A launcher this close to its slot is there and may open fire.</summary>
    private const float StrikeSlotArrivedMeters = 30f;

    /// <summary>Scatter of the shelling round the target base's centre.</summary>
    private const float StrikeAreaRadiusMeters = 300f;

    /// <summary>A truck this close to the bunker or the dump loads or unloads.</summary>
    private const float StrikeTruckReachMeters = 150f;

    private const float StrikeTruckEmptyBelow = 0.1f;

    private const float StrikeTruckFullAbove = 0.9f;

    /// <summary>Scaled seconds between two munitions trucks bought for the site.</summary>
    private const float StrikeTruckBuySeconds = 180f;

    private readonly List<Unit> strikeStores = new();

    private int ownedBallistic;

    /// <summary>True while a SAM site this review's commander plans still has no launcher: ballistic
    /// launchers wait for every StratoLance first.</summary>
    private bool ballisticWaitsForSam;

    /// <summary>
    /// A ballistic launcher's slot at the strike site, picking the site itself the first time:
    /// <see cref="StrikeSiteForwardMeters"/> out of the artillery depot toward the target, pulled in
    /// to <see cref="BatteryRangeShare"/> of the launcher's reach if the depot is out of range.
    /// </summary>
    private static bool TryPlaceStrikeSlot(CommanderState state, Unit unit, GlobalPosition target, out GlobalPosition post)
    {
        post = default;
        if (state.StrikeSite == null)
        {
            GlobalPosition from = state.ArtilleryDepot != null ? state.ArtilleryDepot.GlobalPosition() : unit.GlobalPosition();
            Vector3 away = from - target;
            away.y = 0f;
            float reach = unit.GetMaxRange() * BatteryRangeShare;
            float radius = away.magnitude - StrikeSiteForwardMeters;
            if (reach > 0f)
            {
                radius = Mathf.Min(radius, reach);
            }

            radius = Mathf.Max(radius, BatteryClearOfBaseMeters);
            if (!TryPlacePost(target, radius, Mathf.Atan2(away.z, away.x), 0, out GlobalPosition site))
            {
                return false;
            }

            state.StrikeSite = site;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({unit.NetworkHQ?.faction.name}) set up its ballistic strike site "
                    + $"{FastMath.Distance(site, target) / 1000f:0.0} km from the player's base.");
        }

        // Round the dump, inside its reach: eight to a ring, the next eight further out.
        float reachOfDump = CommanderEconomyService.Instance?.GetDumpReach() ?? 0f;
        float ring = reachOfDump > 0f
            ? Mathf.Clamp(reachOfDump * StrikeSlotReachShare, StrikeSlotMinMeters, StrikeSlotMaxMeters)
            : StrikeSlotFallbackMeters;
        int slot = state.StrikeSlots++;
        float angle = slot * (Mathf.PI / 4f);
        float distance = ring * (1f + (slot / 8 * 0.6f));
        GlobalPosition centre = state.StrikeSite.Value;
        post = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
            centre.x + (Mathf.Cos(angle) * distance),
            centre.y,
            centre.z + (Mathf.Sin(angle) * distance)));
        return true;
    }

    /// <summary>
    /// Opens fire once the launcher stands on its slot, at the nearest of the player's bases it
    /// reaches. True while it is shelling, so the caller leaves it where it is: area fire holds the
    /// vehicle, and a fresh pin would only be an RPC and a path search to the spot it stands on.
    /// </summary>
    private static bool ReviewStrikeLauncher(FactionHQ localHq, CommanderState state, Unit launcher, GlobalPosition post)
    {
        if (CommanderAreaFire.IsAreaFiring(launcher))
        {
            // A base that changed hands is not the player's any more; the next review re-aims.
            if (state.StrikeAims.TryGetValue(launcher, out Airbase aim)
                && aim != null
                && !aim.disabled
                && localHq.ContainsAirbase(aim))
            {
                return true;
            }

            CommanderAreaFire.Clear(launcher);
            state.StrikeAims.Remove(launcher);
        }

        if (!FastMath.InRange(launcher.GlobalPosition(), post, StrikeSlotArrivedMeters))
        {
            return false;
        }

        float reach = launcher.GetMaxRange();
        Airbase? best = null;
        float bestDistance = float.MaxValue;
        foreach (Airbase airbase in localHq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = FastMath.Distance(launcher.GlobalPosition(), airbase.center.GlobalPosition());
            if (distance <= reach && distance < bestDistance)
            {
                best = airbase;
                bestDistance = distance;
            }
        }

        if (best == null || !CommanderAreaFire.TryBeginForAi(launcher, best.center.GlobalPosition(), StrikeAreaRadiusMeters))
        {
            return false;
        }

        state.StrikeAims[launcher] = best;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({launcher.NetworkHQ?.faction.name}): {CommanderGameAccess.GetUnitLabel(launcher)} "
                + $"fires on {CommanderCaptureService.GetAirbaseLabel(best)} from its strike site.");
        return true;
    }

    /// <summary>Any munitions truck that is not the home guard's.</summary>
    private static Unit? RecruitStrikeTruck(FactionHQ hq, CommanderState state)
    {
        if (hq.factionUnits == null)
        {
            return null;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && IsRearmVehicle(unit)
                && !state.Support.ContainsKey(unit)
                && !state.Defenders.ContainsKey(unit))
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>Both MSV ballistic launchers, high explosive and nuclear, by their part names.</summary>
    private static bool IsBallisticLauncherType(VehicleDefinition definition)
    {
        return definition.vehicleType == VehicleType.ART
            && definition.unitName.EndsWith("Ballistic Missile Launcher", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Keeps the strike site supplied: a dump in the middle of it, and one munitions truck that
    /// fills up at the nearest bunker and empties into the dump. Until the dump stands the truck
    /// parks on the site itself, where the launchers rearm straight from it.
    /// </summary>
    private void ReviewStrikeSupply(FactionHQ hq, CommanderState state, int launchers)
    {
        if (launchers == 0 || state.StrikeSite is not GlobalPosition site)
        {
            return;
        }

        CommanderEconomyService.Instance?.EnsureEnemyStrikeDump(hq, site);
        CommanderSupplyStoreService? stores = CommanderSupplyStoreService.Instance;
        Unit? truck = state.StrikeTruck;
        if (truck == null || truck.disabled || truck.NetworkHQ != hq)
        {
            truck = state.StrikeTruck = RecruitStrikeTruck(hq, state);
            state.StrikeTruckLoading = false;
        }

        if (truck == null)
        {
            TryBuyStrikeTruck(hq, state);
            return;
        }

        Unit? dump = FindStrikeStore(hq, site, dump: true);
        if (stores == null || dump == null)
        {
            HoldAt(truck, site);
            return;
        }

        if (state.StrikeTruckLoading)
        {
            Unit? bunker = FindStrikeStore(hq, truck.GlobalPosition(), dump: false);
            if (bunker == null)
            {
                state.StrikeTruckLoading = false;
            }
            else if (!FastMath.InRange(truck.GlobalPosition(), bunker.GlobalPosition(), StrikeTruckReachMeters))
            {
                HoldAt(truck, bunker.GlobalPosition());
                return;
            }
            else
            {
                // An empty bunker refills nothing: wait beside it for the commander's own top-up
                // rather than driving an empty truck back and forth.
                string moved = stores.RefillTruck(truck, bunker);
                float load = CommanderSupplyStoreService.GetAmmoFraction(truck);
                if (load < StrikeTruckFullAbove && (moved.Length > 0 || load < StrikeTruckEmptyBelow))
                {
                    return;
                }

                state.StrikeTruckLoading = false;
            }
        }

        if (!FastMath.InRange(truck.GlobalPosition(), dump.GlobalPosition(), StrikeTruckReachMeters))
        {
            HoldAt(truck, dump.GlobalPosition());
            return;
        }

        // A full dump leaves the truck parked beside it, a second rearmer for the launchers.
        stores.UnloadTruck(truck, dump);
        if (CommanderSupplyStoreService.GetAmmoFraction(truck) < StrikeTruckEmptyBelow)
        {
            state.StrikeTruckLoading = true;
        }
    }

    /// <summary>One truck into the reserve when none is free and none is waiting there.</summary>
    private void TryBuyStrikeTruck(FactionHQ hq, CommanderState state)
    {
        if (Time.time < state.NextStrikeTruckBuyAt)
        {
            return;
        }

        VehicleDefinition? truck = ChooseSupplyTruck(hq.factionFunds);
        if (truck == null || hq.GetUnitSupply(truck) > 0 || !CommanderGameAccess.TrySpend(hq, Mathf.Max(0f, truck.value)))
        {
            return;
        }

        state.NextStrikeTruckBuyAt = Time.time + StrikeTruckBuySeconds;
        hq.ModifyUnitSupply(truck, 1);
        NoteOwned(truck);
        TotalPurchases++;
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) bought a {CommanderGameAccess.GetVehicleLabel(truck)} to supply its strike site.");
    }

    /// <summary>The ammo dump within reach of the site, or the nearest bunker holding ordnance.</summary>
    private Unit? FindStrikeStore(FactionHQ hq, GlobalPosition near, bool dump)
    {
        CommanderSupplyStoreService.CollectStores(hq, strikeStores);
        Unit? best = null;
        float bestDistance = dump ? 300f : float.MaxValue;
        for (int i = 0; i < strikeStores.Count; i++)
        {
            Unit store = strikeStores[i];
            Rearmer? rearmer = CommanderSupplyStoreService.GetRearmer(store);
            if (store is not Building
                || store.disabled
                || CommanderSupplyStoreService.IsDump(store) != dump
                || rearmer == null
                || (!dump && rearmer.Capacity <= 1f))
            {
                continue;
            }

            float distance = FastMath.Distance(store.GlobalPosition(), near);
            if (distance < bestDistance)
            {
                best = store;
                bestDistance = distance;
            }
        }

        strikeStores.Clear();
        return best;
    }
}
