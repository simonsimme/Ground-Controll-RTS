using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The selection bar's FIRE button: one weapon type on the selected unit fires now, at the target
/// the unit is already on, or else the nearest known enemy that weapon can reach.
/// </summary>
/// <remarks>
/// It goes through the same calls the base game's own AI makes: an aircraft selects the station and
/// fires through its <c>WeaponManager</c> (the pilot state's own <c>pilot.Fire()</c>), a ground
/// vehicle or ship fires the station the way <c>Turret</c> does. Guns are left out: a gun only hits
/// what the turret or the nose is already pointing at, which the AI already does on its own.
/// Server only, so a guest's click is forwarded.
/// </remarks>
internal static class CommanderFireNow
{
    // A turret that cannot get onto the target in this long (traverse limits, a target behind
    // terrain) fires anyway: the round still carries the ordered target.
    private const float AimTimeoutSeconds = 3f;

    private static readonly FieldInfo? DeployablePartsField = AccessTools.Field(typeof(GroundVehicle), "deployableParts");
    private static readonly FieldInfo? PartTransformField = AccessTools.Field(AccessTools.Inner(typeof(GroundVehicle), "DeployablePart"), "transform");
    private static readonly FieldInfo? PartDeployedAmountField = AccessTools.Field(AccessTools.Inner(typeof(GroundVehicle), "DeployablePart"), "deployedAmount");
    private static readonly FieldInfo? PartDeployRateField = AccessTools.Field(AccessTools.Inner(typeof(GroundVehicle), "DeployablePart"), "deployRate");
    private static readonly FieldInfo? TurretStowedField = AccessTools.Field(typeof(Turret), "stowed");

    private static readonly Dictionary<WeaponInfo, bool> SensorStores = new();
    private static readonly Dictionary<UnitDefinition, bool> StopToFireTypes = new();
    private static readonly List<Unit> OrderedTargets = new();
    private static readonly List<Unit> FireTargets = new();
    private static readonly HashSet<Unit> Salvoing = new();

    static CommanderFireNow()
    {
        // A safety check that silently stops existing is worse than no safety check, and these are
        // string-matched private fields: a game update that renames one takes the gate with it.
        if (DeployablePartsField == null || PartTransformField == null || PartDeployedAmountField == null || TurretStowedField == null)
        {
            CommanderPlugin.Log.LogWarning(
                "FIRE cannot see a vehicle's deploy state any more, so it will not hold fire while a launcher is stowed.");
        }
    }

    /// <summary>True only inside FIRE's own call to <c>WeaponManager.Fire</c>, which HOLD FIRE lets through.</summary>
    internal static bool ManualFire { get; private set; }

    /// <summary>
    /// A scouting store rather than a weapon: its munition carries an optical <c>TargetDetector</c>
    /// where a warhead would be (the Eyeball Mk.II, "a panoramic optical sensor suite").
    /// </summary>
    /// <remarks>
    /// There is no flag on <c>WeaponInfo</c> for one and its numbers read like an ordinary missile's
    /// — the Eyeball is authored with the AGM-48's own effectiveness and target requirements, 10 km
    /// of reach included — so the munition prefab is the only thing that can answer it, the same way
    /// the loadout code reads pilot types and hardpoints off prefabs rather than off a name table. A
    /// seeker does not count: that is a <c>Radar</c>, which derives from <c>TargetDetector</c>, and a
    /// radar-guided missile fired at nothing is a wasted round.
    /// </remarks>
    internal static bool IsSensorStore(WeaponInfo info)
    {
        // Cached: the anti-radar test and the AI loadout scoring ask it of every store they see.
        if (SensorStores.TryGetValue(info, out bool known))
        {
            return known;
        }

        if (info.weaponPrefab == null)
        {
            SensorStores[info] = false;
            return false;
        }

        bool sensor = false;
        TargetDetector[] detectors = info.weaponPrefab.GetComponentsInChildren<TargetDetector>(true);
        for (int i = 0; i < detectors.Length; i++)
        {
            if (detectors[i] != null && detectors[i] is not Radar)
            {
                sensor = true;
                break;
            }
        }

        // What a munition prefab carries is asset data, so a log line is the only way to confirm the
        // Eyeball reads as a scouting store rather than a weapon that has to have a target.
        SensorStores[info] = sensor;
        CommanderPlugin.Log.LogInfo(
            $"FIRE: {info.weaponName} reads as a {(sensor ? "scouting store, so it fires with or without a target" : "weapon, so it needs a target in reach")}.");
        return sensor;
    }

    /// <summary>
    /// Asks the selected unit to fire every weapon of one type, host or guest. A repeat from holding
    /// the button down is <paramref name="quiet"/>: its refusals (reloading, still firing, none
    /// left) are the expected gaps between rounds, not news.
    /// </summary>
    internal static void Request(Unit? unit, string weaponKey, bool quiet = false)
    {
        if (unit == null || string.IsNullOrEmpty(weaponKey))
        {
            return;
        }

        if (CommanderNetwork.Forward("fire", CommanderNetwork.Id(unit), weaponKey, quiet ? "hold" : string.Empty))
        {
            return;
        }

        if (!TryFire(unit, weaponKey, out string failure) && !quiet)
        {
            CommanderNotice.Fail(failure);
        }
    }

    /// <summary>A guest's FIRE click, ownership-checked.</summary>
    internal static void HostFire(FactionHQ hq, string[] args)
    {
        if (CommanderNetwork.TryUnit(args, 1, out Unit unit)
            && ReferenceEquals(unit.NetworkHQ, hq)
            && !TryFire(unit, CommanderNetwork.Arg(args, 2), out string failure)
            && CommanderNetwork.Arg(args, 3) != "hold")
        {
            CommanderNotice.Fail(failure);
        }
    }

    /// <summary>The weapon key the FIRE button sends: the <c>WeaponInfo</c> asset name, the same on
    /// every machine. Empty for a gun, which gets no button.</summary>
    internal static string GetFireKey(WeaponInfo? info)
    {
        return info == null || info.gun ? string.Empty : info.name;
    }

    /// <summary>
    /// True while the unit is still folding out or folding away. A launcher whose container is not
    /// all the way up puts the missile through its own bodywork: a Stratolauncher ordered to move
    /// lowers its container (the game runs that animation off the vehicle's stationary flag), and a
    /// FIRE click in that window blew the missile up on the vehicle. The base game never hits this
    /// because a stowed turret simply does not shoot; FIRE drives the weapon station directly, so
    /// it has to ask for itself.
    /// </summary>
    internal static bool IsSettingUp(Unit unit, WeaponStation station)
    {
        if (TurretStowedField != null && station.HasTurret())
        {
            for (int i = 0; i < station.Turrets.Count; i++)
            {
                Turret turret = station.Turrets[i];
                if (turret != null && TurretStowedField.GetValue(turret) is true)
                {
                    return true;
                }
            }
        }

        return unit is GroundVehicle vehicle && ArePartsDeploying(vehicle);
    }

    /// <summary>
    /// A vehicle that folds its weapon away whenever it moves (the MSV LADS and its kin): the game
    /// stows it on every move order and only shoots once the parts are out again, so on the move it
    /// is a target and nothing else. Cached per type: the AI's reviews ask it of every vehicle.
    /// </summary>
    internal static bool MustStopToFire(Unit unit)
    {
        if (unit is not GroundVehicle vehicle || unit.definition == null)
        {
            return false;
        }

        if (StopToFireTypes.TryGetValue(unit.definition, out bool known))
        {
            return known;
        }

        bool stops = false;
        if (DeployablePartsField?.GetValue(vehicle) is System.Array parts && PartTransformField != null)
        {
            for (int i = 0; i < parts.Length && !stops; i++)
            {
                object? part = parts.GetValue(i);
                stops = part != null
                    && PartTransformField.GetValue(part) != null
                    && (PartDeployRateField == null || PartDeployRateField.GetValue(part) is not float rate || rate > 0f);
            }
        }

        StopToFireTypes[unit.definition] = stops;
        return stops;
    }

    /// <summary>
    /// True until every animated part of the vehicle is fully out, and while it is not yet parked
    /// (the game only starts folding out once the stationary flag is set). Mid-animation counts: the
    /// turret is only marked stowed once the container has finished coming down. A part with no
    /// transform or no deploy rate never animates, so it is not waited on.
    /// </summary>
    internal static bool ArePartsDeploying(GroundVehicle vehicle)
    {
        if (DeployablePartsField == null
            || PartTransformField == null
            || PartDeployedAmountField == null
            || DeployablePartsField.GetValue(vehicle) is not System.Array parts
            || parts.Length == 0)
        {
            return false;
        }

        if (!vehicle.NetworknetworkStationary)
        {
            return true;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            object? part = parts.GetValue(i);
            if (part != null
                && PartTransformField.GetValue(part) != null
                && (PartDeployRateField == null || PartDeployRateField.GetValue(part) is not float rate || rate > 0f)
                && PartDeployedAmountField.GetValue(part) is float deployed
                && deployed < 1f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFire(Unit unit, string weaponKey, out string failure)
    {
        failure = string.Empty;
        if (unit.disabled)
        {
            failure = "CANNOT FIRE: UNIT IS DESTROYED";
            return false;
        }

        if (Salvoing.Contains(unit))
        {
            failure = "CANNOT FIRE: STILL FIRING";
            return false;
        }

        WeaponStation? station = FindStation(unit, weaponKey);
        if (station == null)
        {
            failure = "CANNOT FIRE: NONE LEFT";
            return false;
        }

        if (!station.Ready())
        {
            failure = "CANNOT FIRE: RELOADING";
            return false;
        }

        if (IsSettingUp(unit, station))
        {
            failure = "CANNOT FIRE: NOT SET UP YET";
            return false;
        }

        // A bomb falls on whatever is under it, and a glide bomb from height goes far past the range
        // its data sheet gives, so a bomb goes at the ordered target wherever it is. A scouting
        // store is the same case for a different reason: it is fired to look, so an ordered target
        // is a direction to look in and no target at all is still a valid sortie. Everything else
        // needs something inside its reach.
        WeaponInfo info = station.WeaponInfo;
        bool dropsAnyway = info.bomb || info.glideBomb || IsSensorStore(info);

        // Every target the unit has been ordered at, not just the first: the base game's own
        // multi-lock salvo puts one round on each of them, which is what a queued attack order is
        // for. More rounds than the rack holds would only be launches the station refuses.
        FireTargets.Clear();
        OrderedTargets.Clear();
        CommanderMoveService.Instance?.CollectAttackTargets(unit, OrderedTargets);
        for (int i = 0; i < OrderedTargets.Count && FireTargets.Count < station.Ammo; i++)
        {
            Unit ordered = OrderedTargets[i];
            if (dropsAnyway || CanReach(unit, station, ordered))
            {
                FireTargets.Add(ordered);
            }
        }

        // Targets ordered but none in this store's reach: refuse, rather than spend the round on
        // whatever happens to be nearest (a destroyer put its missiles into hangars that way).
        if (FireTargets.Count == 0 && OrderedTargets.Count > 0 && !dropsAnyway)
        {
            // Says which test failed: "out of range" alone also covered a store that cannot hurt
            // that class of target, and read as a bug with the target plainly inside reach.
            failure = "CANNOT FIRE: " + DescribeUnreachable(unit, station, OrderedTargets[0]);
            CommanderPlugin.Log.LogInfo(
                $"FIRE refused: {CommanderGameAccess.GetUnitLabel(unit)} {info.weaponName} at "
                    + $"{CommanderGameAccess.GetUnitLabel(OrderedTargets[0])}: {failure}");
            return false;
        }

        // Nothing ordered: the target the unit's own AI is already on, else the nearest enemy the
        // store is made for.
        if (FireTargets.Count == 0)
        {
            Unit? fallback = GetOrderedTarget(unit);
            if (fallback == null || fallback.disabled || (!dropsAnyway && !CanReach(unit, station, fallback)))
            {
                fallback = FindNearestTarget(unit, station);
            }

            if (fallback != null)
            {
                FireTargets.Add(fallback);
            }
        }

        Unit? target = FireTargets.Count > 0 ? FireTargets[0] : null;
        if (target == null && !dropsAnyway)
        {
            failure = "CANNOT FIRE: NO TARGET IN RANGE";
            return false;
        }

        if (unit is Aircraft aircraft)
        {
            if (station.SafetyIsOn(aircraft))
            {
                failure = "CANNOT FIRE: STILL ON THE GROUND";
                return false;
            }

            // WeaponManager.Fire salvos one mount per entry once the list holds more than one, at
            // the store's own fire interval, which is the base game's multi-lock launch.
            WeaponManager manager = aircraft.weaponManager;
            List<Unit> targets = manager.GetTargetList();
            targets.Clear();
            targets.AddRange(FireTargets);
            manager.TargetListChanged();
            manager.SetActiveStation(station.Number);
            ManualFire = true;
            try
            {
                manager.Fire();
            }
            finally
            {
                ManualFire = false;
            }
        }
        else if (FireTargets.Count > 1 || (target != null && station.HasTurret()))
        {
            // A ground station has no per-target salvo of its own (Fire puts every weapon on it at
            // one target), so the aircraft's multi-lock launch is done here: one round per target.
            // A turret goes this way even for one target: it has to be swung round first (see
            // AimedAt), which takes frames.
            Salvoing.Add(unit);
            CommanderPlugin.Instance?.StartCoroutine(GroundSalvo(unit, station, new List<Unit>(FireTargets)));
        }
        else
        {
            station.Fire(unit, target!);
        }

        CommanderPlugin.Log.LogInfo(
            $"FIRE order: {CommanderGameAccess.GetUnitLabel(unit)} fired {info.weaponName}"
                + (target != null ? $" at {CommanderGameAccess.GetUnitLabel(target)}" : string.Empty)
                + (FireTargets.Count > 1 ? $" and {FireTargets.Count - 1} more." : "."));
        return true;
    }

    /// <summary>
    /// One round at each target in turn, each launched as soon as a launcher on the station is free
    /// (a single-rail launcher waits out its fire interval between rounds). Gives up on a target that
    /// died, and on the rest once the station is empty or the salvo has taken far too long.
    /// </summary>
    private static IEnumerator GroundSalvo(Unit unit, WeaponStation station, List<Unit> targets)
    {
        float giveUp = Time.time + 5f + (targets.Count * (Mathf.Max(station.WeaponInfo.fireInterval, 1f) + AimTimeoutSeconds));
        int next = 0;
        float aimStartedAt = -1f;
        int ammoAtAim = 0;
        while (next < targets.Count && unit != null && !unit.disabled && station.Ammo > 0 && Time.time < giveUp)
        {
            Unit target = targets[next];
            if (target == null || target.disabled)
            {
                next++;
                aimStartedAt = -1f;
                continue;
            }

            if (aimStartedAt < 0f)
            {
                aimStartedAt = Time.time;
                ammoAtAim = station.Ammo;
            }

            // The turret, now on this target, may well fire at it by itself once aimed: that round
            // counts, or the target gets two.
            bool launched = station.Ammo < ammoAtAim
                || ((AimedAt(station, target) || Time.time - aimStartedAt > AimTimeoutSeconds)
                    && LaunchOne(unit, station, target));
            if (launched)
            {
                next++;
                aimStartedAt = -1f;
            }
            else
            {
                yield return null;
            }
        }

        Salvoing.Remove(unit!);
    }

    /// <summary>
    /// Points the station's turrets at <paramref name="target"/> and says whether they are on it.
    /// FIRE drives the station directly, and a launcher fires along wherever its turret points: one
    /// still on the AI's own pick sent the round that way, and a seeker that looks for its target
    /// after launch (IR) locked the thing in front of it instead. The AI never picks some targets,
    /// incoming missiles above all, so it never swung round on its own. Set on the host only, where the
    /// round is fired (the networked setter takes a span this build cannot reference): a guest sees
    /// the turret swing late, not a different shot.
    /// </summary>
    private static bool AimedAt(WeaponStation station, Unit target)
    {
        bool aimed = true;
        for (int i = 0; i < station.Turrets.Count; i++)
        {
            Turret turret = station.Turrets[i];
            if (turret == null)
            {
                continue;
            }

            if (!ReferenceEquals(turret.GetTarget(), target))
            {
                turret.SetTarget(target.persistentID, station.Number);
                aimed = false;
                continue;
            }

            aimed &= turret.IsOnTarget();
        }

        return aimed;
    }

    /// <summary>
    /// Fires the first launcher on the station that will go. A launcher still inside its own fire
    /// interval quietly refuses, so the station's ammo count is what says a round left.
    /// </summary>
    private static bool LaunchOne(Unit unit, WeaponStation station, Unit target)
    {
        Vector3 inherited = unit.rb != null ? unit.rb.velocity : default;
        for (int i = 0; i < station.Weapons.Count; i++)
        {
            Weapon weapon = station.Weapons[i];
            if (weapon == null || !weapon.IsAttached())
            {
                continue;
            }

            int before = station.Ammo;
            weapon.Fire(unit, target, inherited, station, default);
            if (station.Ammo < before)
            {
                if (weapon.Rearmable)
                {
                    unit.RequestRearm();
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>A station carrying that weapon with rounds left, a ready one first.</summary>
    private static WeaponStation? FindStation(Unit unit, string weaponKey)
    {
        WeaponStation? loaded = null;
        for (int i = 0; i < unit.weaponStations.Count; i++)
        {
            WeaponStation station = unit.weaponStations[i];
            if (station?.WeaponInfo == null || station.WeaponInfo.gun || station.WeaponInfo.name != weaponKey || station.Ammo <= 0)
            {
                continue;
            }

            if (station.Ready())
            {
                return station;
            }

            loaded ??= station;
        }

        return loaded;
    }

    /// <summary>What the unit is already fighting: the commanded or current target.</summary>
    private static Unit? GetOrderedTarget(Unit unit)
    {
        if (unit is Aircraft aircraft)
        {
            return CommanderAirCommandService.Instance?.GetAttackTarget(aircraft);
        }

        return CommanderMoveService.Instance != null
            && CommanderMoveService.Instance.TryGetOrder(unit, out _, out _, out Unit? attackTarget)
                ? attackTarget
                : null;
    }

    private static Unit? FindNearestTarget(Unit unit, WeaponStation station)
    {
        FactionHQ? hq = unit.NetworkHQ;
        if (hq == null)
        {
            return null;
        }

        Unit? best = null;
        float bestDistance = float.MaxValue;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            if (!entry.Key.TryGetUnit(out Unit candidate)
                || candidate == null
                || candidate.disabled
                || candidate.NetworkHQ == null
                || CommanderGameAccess.IsFriendlyUnit(candidate, hq)
                || !CanReach(unit, station, candidate))
            {
                continue;
            }

            float distance = Vector3.Distance(unit.transform.position, candidate.transform.position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>In the weapon's reach and a kind of target it is made for (the game's own test).</summary>
    /// <summary>The first <see cref="CanReach"/> test the target fails, for the refusal toast.</summary>
    private static string DescribeUnreachable(Unit unit, WeaponStation station, Unit target)
    {
        TargetRequirements requirements = station.WeaponInfo.targetRequirements;
        float range = requirements.maxRange > 0f ? requirements.maxRange : unit.GetMaxRange();
        float distance = Vector3.Distance(unit.transform.position, target.transform.position);
        if (!CanHurt(station, target))
        {
            return "WRONG WEAPON FOR THAT TARGET";
        }

        if (distance > range)
        {
            return $"TARGET {distance / 1000f:0.0} KM AWAY, REACH {range / 1000f:0.0} KM";
        }

        return target.radarAlt > requirements.maxAltitude ? "TARGET TOO HIGH" : "TARGET OUT OF REACH";
    }

    private static bool CanReach(Unit unit, WeaponStation station, Unit target)
    {
        WeaponInfo info = station.WeaponInfo;
        TargetRequirements requirements = info.targetRequirements;
        float range = requirements.maxRange > 0f ? requirements.maxRange : unit.GetMaxRange();
        return target.definition != null
            && Vector3.Distance(unit.transform.position, target.transform.position) <= range
            && target.radarAlt <= requirements.maxAltitude
            && CanHurt(station, target);
    }

    /// <summary>
    /// The store against the target's type, and nothing else. <c>CalcOpportunityThreat</c> also
    /// multiplies in the shooter's own role rating, so an airframe rated as a fighter scored zero
    /// against anything on the ground with an AGM aboard, and FIRE refused a target 15 km out with a
    /// 20 km missile as "out of range". That role factor is the AI choosing what to hunt, not
    /// whether the weapon works.
    /// </summary>
    private static bool CanHurt(WeaponStation station, Unit target)
    {
        return target.definition != null && target.definition.GetOpportunity(station.WeaponInfo.effectiveness) > 0f;
    }
}
