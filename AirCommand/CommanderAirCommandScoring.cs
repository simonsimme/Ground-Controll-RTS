using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    /// <summary>Whether anything the aircraft is carrying right now can be fired under this mode.</summary>
    internal static bool CanUseMode(Aircraft aircraft, AirCommandMode mode)
    {
        List<WeaponStation>? stations = aircraft.weaponStations;
        if (stations == null)
        {
            return false;
        }

        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation? station = stations[i];
            if (station != null && !station.Cargo && station.Ammo > 0 && IsStationEligible(station, mode))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a mission of this mode will ever fire this weapon. The same answer for a station in
    /// flight and a store still on the loadout screen, which is what lets Air Command warn before
    /// launch that a sortie carries nothing it can use.
    /// </summary>
    /// <summary>Something aboard that only a STRATEGIC STRIKE mission fires (a Piledriver, a Tusko-B):
    /// on CAS it would come home unused, so a bomber carrying one flies strike.</summary>
    internal static bool CarriesStrategicStore(Aircraft aircraft, bool includeNuclear = false)
    {
        List<WeaponStation>? stations = aircraft.weaponStations;
        for (int i = 0; stations != null && i < stations.Count; i++)
        {
            WeaponInfo? info = stations[i]?.WeaponInfo;
            if (info != null && stations[i].Ammo > 0 && (includeNuclear || !info.nuclear) && IsStrategicStrikeWeapon(info))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsWeaponEligible(WeaponInfo info, AirCommandMode mode)
    {
        return mode switch
        {
            // RADAR is AWACS and ARAD in one: the jammer works any emitter, the anti-radiation
            // missiles any emitter on the surface (ChooseMissionTarget keeps them off aircraft).
            AirCommandMode.AwacsJammer => info.jammer || IsAradWeapon(info),
            AirCommandMode.Cas => !info.nuclear
                && !info.jammer
                && !IsStrategicStrikeWeapon(info)
                && info.effectiveness.antiSurface > 0.05f,

            // Dual-role weapons remain manually usable, but ScoreLoadout keeps
            // them out of the recommended Air Superiority section.
            AirCommandMode.AirGuard => !info.jammer && info.effectiveness.antiAir > 0.05f,
            AirCommandMode.Arad => IsAradWeapon(info),

            // STRIKE is a target class now (buildings and ships), so anything that hurts the
            // surface counts, strategic stores included.
            AirCommandMode.StrategicStrike => !info.nuclear
                && !info.jammer
                && (IsStrategicStrikeWeapon(info) || info.effectiveness.antiSurface > 0.05f),
            AirCommandMode.Hybrid => !info.nuclear
                && !info.jammer
                && Mathf.Max(info.effectiveness.antiAir, info.effectiveness.antiSurface) > 0.05f,
            _ => false,
        };
    }

    internal static bool IsAradWeapon(WeaponInfo info)
    {
        // The Eyeball Mk.II passes the rating test but does no damage. A scouting store is never an ARAD.
        return (info.targetRequirements.minRadar > 0f
                || info.effectiveness.antiRadar > Mathf.Max(info.effectiveness.antiAir, 0.05f)
                || info.weaponPrefab?.GetComponentInChildren<ARMSeeker>(true) != null
                || info.weaponName?.IndexOf("ARAD", StringComparison.OrdinalIgnoreCase) >= 0)
            && !CommanderFireNow.IsSensorStore(info);
    }

    /// <summary>
    /// True when this airframe is flown by a fixed-wing AI pilot, which is the only kind an Air
    /// Command mission can steer. Everything the mod does to a commanded aircraft — the idle-timer
    /// prefix, the route destination, the altitude hold — is a patch on
    /// <c>AIPilotCombatModes</c>, and only <c>PilotType.Plane</c> uses that state machine:
    /// <c>Pilot.SetStartingAiState</c> puts helicopters and tiltwings on <c>AIHeloCombatState</c>
    /// instead. Handing one of those a mission applies the target half of it and none of the flying
    /// half, which is how the enemy's VTOLs ended up nosing into the ground shortly after takeoff.
    /// </summary>
    internal static bool HasPlanePilot(AircraftDefinition definition)
    {
        return definition.unitPrefab != null
            && HasPlanePilot(definition.unitPrefab.GetComponent<Aircraft>());
    }

    /// <summary>
    /// True when the base game gave this airframe an AI flight state at all — fixed wing or rotary.
    /// This, not <see cref="HasPlanePilot(AircraftDefinition)"/>, is what decides whether the player may command an
    /// aircraft: rotary steering exists (the <c>AIHeloCombatState.NoTarget</c> postfix writes the
    /// mission hold point exactly as the fixed-wing one does), so gating on plane pilots alone was
    /// what made a helicopter bought from a pad impossible to give an order to.
    /// </summary>
    internal static bool HasAiPilot(Aircraft? aircraft)
    {
        Pilot.PilotType? type = GetFlyingPilotType(aircraft);
        return type is Pilot.PilotType.Plane or Pilot.PilotType.Helo or Pilot.PilotType.Tiltwing;
    }

    /// <summary>True for a helicopter or tiltwing, which flies <c>AIHeloCombatState</c>.</summary>
    internal static bool HasRotaryPilot(Aircraft? aircraft)
    {
        Pilot.PilotType? type = GetFlyingPilotType(aircraft);
        return type is Pilot.PilotType.Helo or Pilot.PilotType.Tiltwing;
    }

    /// <summary>True when the airframe's prefab is flown by a rotary AI pilot.</summary>
    internal static bool IsRotaryAirframe(AircraftDefinition definition)
    {
        return definition.unitPrefab != null
            && HasRotaryPilot(definition.unitPrefab.GetComponent<Aircraft>());
    }

    internal static bool HasPlanePilot(Aircraft? aircraft)
    {
        return GetFlyingPilotType(aircraft) == Pilot.PilotType.Plane;
    }

    /// <summary>
    /// The type of seat 0, the only seat the base game ever flies: <c>Pilot.OnStartServer</c> calls
    /// <c>SetStartingAiState</c> only when <c>aircraft.pilots[0] == this</c>. The SAH-46 Chicane
    /// carries a Helo pilot and a Plane-typed second seat, so asking "is any seat a plane" filed an
    /// attack helicopter as an aeroplane and gave it fixed-wing handling.
    /// </summary>
    internal static Pilot.PilotType? GetFlyingPilotType(Aircraft? aircraft)
    {
        return aircraft?.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0] != null
            ? aircraft.pilots[0].pilotType
            : null;
    }

    /// <summary>
    /// True when the Basegame AI has a flight model for this airframe at all.
    /// <c>Pilot.SetStartingAiState</c> hands <c>PilotType.Plane</c> to <c>AIPilotCombatModes</c> and
    /// <c>Helo</c>/<c>Tiltwing</c> to <c>AIHeloCombatState</c>, and gives <c>PilotType.VTOL</c>
    /// **nothing at all** — a VTOL with an AI pilot has no state, no autopilot input and falls out
    /// of the sky. Nothing the mod can do fixes that, so nothing may buy one.
    /// </summary>
    internal static bool CanAiFly(AircraftDefinition definition)
    {
        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        return HasAiPilot(prefab);
    }

    /// <summary>The AIR window's own "can this field launch that airframe" rule. The enemy commander
    /// buys through it too, so both sides see the same list of what each field can put up.</summary>
    internal static bool IsCompatibleAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        if (airbase == null || airbase.disabled || !airbase.GetAvailableAircraft().Contains(definition))
        {
            return false;
        }

        foreach (Airbase ownedAirbase in hq.GetAirbases())
        {
            if (ReferenceEquals(ownedAirbase, airbase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The field has a takeoff runway at least as long as the airframe needs, by the same test the
    /// base game applies for a human pilot (<c>Airbase.GetTakeoffRunway(aircraft, takeoffDistance)</c>).
    /// Anything with no takeoff distance, rotary or vertical, needs no runway.
    /// </summary>
    internal static bool HasTakeoffRunway(Airbase airbase, AircraftDefinition definition, bool countOverruns = true)
    {
        AircraftParameters? parameters = definition.aircraftParameters;
        if (parameters == null || parameters.verticalLanding || parameters.takeoffDistance <= 0f)
        {
            return true;
        }

        // The authored distance is a human's roll; what the AI actually managed here outranks it.
        if (countOverruns && GetRunwayOverruns(airbase, definition) >= OverrunsBeforeRefusal)
        {
            return false;
        }

        Airbase.Runway[]? runways = airbase.runways;
        for (int i = 0; runways != null && i < runways.Length; i++)
        {
            // A ski-jump is a carrier's launch ramp, sized for what its hangars hold, not a strip
            // to roll the authored distance on: AIPilotTakeoffState flies off it, and Carrier Duel
            // gives Primeva 20 Ifrits (1200 m) for a deck shorter than that, which kept its wing to
            // Crickets and Medusas.
            if (runways[i] != null
                && runways[i].Takeoff
                && (runways[i].SkiJump || runways[i].Length >= parameters.takeoffDistance))
            {
                return true;
            }
        }

        return false;
    }

    private static float ScoreLoadout(
        Loadout loadout,
        AirCommandMode mode,
        AircraftDefinition? aircraftDefinition = null)
    {
        float score = 0f;
        bool hasRadar = false;
        bool hasJammer = false;
        float radarRange = 0f;
        float jammerRange = 0f;
        int remainingLaserTargets = GetLaserTargetCapacity(loadout, aircraftDefinition);
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            WeaponInfo? info = mount.info;
            switch (mode)
            {
                case AirCommandMode.AwacsJammer:
                    SpecialAirSystem specialSystem = GetSpecialAirSystem(mount);
                    if (specialSystem == SpecialAirSystem.Radar)
                    {
                        hasRadar = true;
                        radarRange = Mathf.Max(radarRange, GetSpecialSystemRange(mount));
                    }

                    if (specialSystem == SpecialAirSystem.RadarJammer)
                    {
                        hasJammer = true;
                        jammerRange = Mathf.Max(jammerRange, GetSpecialSystemRange(mount));
                    }

                    if (info != null && IsAradWeapon(info))
                    {
                        score += ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiRadar, 0.5f), mount, info, ref remainingLaserTargets);
                    }

                    break;

                case AirCommandMode.Cas:
                    if (info != null
                        && !info.nuclear
                        && !info.jammer
                        && !IsStrategicStrikeWeapon(info, mount)
                        && info.effectiveness.antiSurface > 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiSurface, mount, info, ref remainingLaserTargets);
                    }

                    break;

                case AirCommandMode.AirGuard:
                    if (info != null
                        && !info.jammer
                        && info.effectiveness.antiAir > 0.05f
                        && info.effectiveness.antiSurface <= 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiAir, mount, info, ref remainingLaserTargets);
                    }

                    break;

                case AirCommandMode.Arad:
                    if (info != null && IsAradWeapon(info))
                    {
                        score += ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiRadar, 0.5f), mount, info, ref remainingLaserTargets);
                    }

                    break;

                case AirCommandMode.StrategicStrike:
                    if (info != null && IsStrategicStrikeWeapon(info, mount))
                    {
                        score += 20f + ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiSurface, info.effectiveness.antiRadar, 0.5f),
                            mount,
                            info,
                            ref remainingLaserTargets);
                    }
                    else if (info != null && !info.nuclear && !info.jammer && info.effectiveness.antiSurface > 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiSurface, mount, info, ref remainingLaserTargets);
                    }

                    break;

                // Hybrid has no weapon-class filter on purpose: it is the "just fly and shoot what
                // you can" mode, so every store that is worth anything against anything counts.
                case AirCommandMode.Hybrid:
                    if (info != null && !info.nuclear && !info.jammer)
                    {
                        float effectiveness = Mathf.Max(
                            info.effectiveness.antiAir, info.effectiveness.antiSurface, info.effectiveness.antiRadar);
                        if (effectiveness > 0.05f)
                        {
                            score += ScoreConventionalWeapon(effectiveness, mount, info, ref remainingLaserTargets);
                        }
                    }

                    break;
            }
        }

        if (mode == AirCommandMode.AwacsJammer)
        {
            if (hasRadar)
            {
                score += 16f + (radarRange / 10000f);
            }

            if (hasJammer)
            {
                score += 8f + (jammerRange / 10000f);
            }
        }

        return score;
    }

    private static float ScoreConventionalWeapon(
        float effectiveness,
        WeaponMount mount,
        WeaponInfo info,
        ref int remainingLaserTargets)
    {
        int usefulStores = Mathf.Max(mount.ammo, 1);
        if (info.laserGuided)
        {
            usefulStores = Mathf.Min(usefulStores, Mathf.Max(remainingLaserTargets, 0));
            remainingLaserTargets = Mathf.Max(remainingLaserTargets - usefulStores, 0);
            if (usefulStores == 0)
            {
                return 0f;
            }
        }

        // Guns use round counts several orders of magnitude above discrete stores.
        float quantity = info.gun ? 1f : Mathf.Sqrt(usefulStores);
        float delivery = 1f;
        if (info.gun)
        {
            delivery *= 0.5f;
        }

        if (info.missile)
        {
            delivery *= 1.12f;
        }

        if (info.bomb)
        {
            delivery *= 0.88f;
        }

        if (info.glideBomb)
        {
            delivery *= 1.28f;
        }

        if (info.overHorizon)
        {
            delivery *= 1.2f;
        }

        if (info.laserGuided)
        {
            delivery *= 0.78f;
        }

        if (info.missile)
        {
            float speed = info.GetMaxSpeed();
            if (speed > 0f)
            {
                delivery *= speed switch
                {
                    < 250f => Mathf.Lerp(0.4f, 0.58f, speed / 250f),
                    < 400f => Mathf.Lerp(0.58f, 0.82f, (speed - 250f) / 150f),
                    < 700f => Mathf.Lerp(0.82f, 1.08f, (speed - 400f) / 300f),
                    _ => Mathf.Clamp(1.08f + ((speed - 700f) / 3000f), 1.08f, 1.25f),
                };
            }
        }

        float maxRange = Mathf.Max(info.targetRequirements.maxRange, 1000f);
        float rangeFactor = Mathf.Clamp(Mathf.Sqrt(maxRange / 10000f), 0.72f, 1.45f);
        string identity = GetWeaponIdentity(mount, info);
        if (ContainsWeaponToken(identity, "AGM-48", "AGM48", "AGM-68", "AGM68"))
        {
            delivery *= 1.42f;
        }

        if (ContainsWeaponToken(identity, "AGM-99", "AGM99"))
        {
            delivery *= 0.4f;
        }

        if (ContainsWeaponToken(identity, "KINGPIN"))
        {
            delivery *= 1.35f;
        }

        if (info.glideBomb && ContainsWeaponToken(identity, "CLUSTER"))
        {
            delivery *= 1.22f;
        }

        return effectiveness * quantity * delivery * rangeFactor;
    }

    private static int GetLaserTargetCapacity(Loadout loadout, AircraftDefinition? definition)
    {
        int capacity = 0;
        LaserDesignator? builtIn = definition?.unitPrefab?.GetComponentInChildren<LaserDesignator>(true);
        if (builtIn != null)
        {
            capacity = Mathf.Max(capacity, builtIn.GetMaxTargets());
        }

        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            LaserDesignator? mounted = loadout.weapons[i]?.prefab?.GetComponentInChildren<LaserDesignator>(true);
            if (mounted != null)
            {
                capacity = Mathf.Max(capacity, mounted.GetMaxTargets());
            }
        }

        return Mathf.Max(capacity, 1);
    }

    private static bool IsStrategicStrikeWeapon(WeaponInfo info, WeaponMount? mount = null)
    {
        if (info.strategic)
        {
            return true;
        }

        GameObject? prefab = info.weaponPrefab;
        if (prefab != null
            && (prefab.GetComponent<OpticalSeekerCruiseMissile>() != null
                || prefab.GetComponent<BallisticMissileGuidance>() != null))
        {
            return true;
        }

        return ContainsWeaponToken(
            GetWeaponIdentity(mount, info),
            "CRUISE",
            "TBM",
            "BALLISTIC",
            "TUSKO-B",
            "TUSKO B",
            "TUSKOB");
    }

    private static string GetWeaponIdentity(WeaponMount? mount, WeaponInfo info)
    {
        return string.Join("|", new[]
        {
            info.weaponName,
            info.shortName,
            info.name,
            mount?.mountName,
            mount?.jsonKey,
            mount?.name,
        });
    }

    private static bool ContainsWeaponToken(string identity, params string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (identity.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStationEligible(WeaponStation station, AirCommandMode mode)
    {
        WeaponInfo? info = station.WeaponInfo;
        return info != null && IsWeaponEligible(info, mode);
    }

    private static bool IsTargetEligible(Unit target, AirCommandMode mode, bool targetOrdnance)
    {
        return mode switch
        {
            AirCommandMode.AwacsJammer => target.HasRadarEmission(),
            AirCommandMode.Cas => target is GroundVehicle or Ship or Building,
            AirCommandMode.AirGuard => target is Aircraft || (targetOrdnance && target is Missile),
            AirCommandMode.Arad => target is not Aircraft && target.HasRadarEmission(),
            AirCommandMode.StrategicStrike => target is Ship or Building,
            AirCommandMode.Hybrid => target is Aircraft
                || target is GroundVehicle
                || target is Ship
                || target is Building
                || (targetOrdnance && target is Missile),
            _ => false,
        };
    }

    private static void NormalizeLoadoutLength(Loadout loadout, AircraftDefinition definition)
    {
        Aircraft? aircraft = definition.unitPrefab != null
            ? definition.unitPrefab.GetComponent<Aircraft>()
            : null;
        int hardpointCount = aircraft?.weaponManager?.hardpointSets?.Length ?? loadout.weapons.Count;
        while (loadout.weapons.Count < hardpointCount)
        {
            loadout.weapons.Add(null!);
        }

        if (loadout.weapons.Count > hardpointCount)
        {
            loadout.weapons.RemoveRange(hardpointCount, loadout.weapons.Count - hardpointCount);
        }
    }

    private static float GetMissionRadius(AirCommandMode mode)
    {
        return mode switch
        {
            AirCommandMode.Cas => CommanderSettings.CasRadiusKm * 1000f,
            AirCommandMode.AirGuard => CommanderSettings.AirGuardRadiusKm * 1000f,
            AirCommandMode.Arad => CommanderSettings.AradRadiusKm * 1000f,
            AirCommandMode.StrategicStrike => CommanderSettings.StrikeRadiusKm * 1000f,
            AirCommandMode.Hybrid => CommanderSettings.HybridRadiusKm * 1000f,
            _ => CommanderSettings.AwacsRadiusKm * 1000f,
        };
    }

    private static void SetMissionRadius(AirCommandMode mode, float radiusKm)
    {
        switch (mode)
        {
            case AirCommandMode.Cas: CommanderSettings.CasRadiusKm = radiusKm; break;
            case AirCommandMode.AirGuard: CommanderSettings.AirGuardRadiusKm = radiusKm; break;
            case AirCommandMode.Arad: CommanderSettings.AradRadiusKm = radiusKm; break;
            case AirCommandMode.StrategicStrike: CommanderSettings.StrikeRadiusKm = radiusKm; break;
            case AirCommandMode.Hybrid: CommanderSettings.HybridRadiusKm = radiusKm; break;
            default: CommanderSettings.AwacsRadiusKm = radiusKm; break;
        }
    }
}
