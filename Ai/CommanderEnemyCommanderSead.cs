using System.Collections.Generic;
using NuclearOption.SavedMission;

namespace GroundControlRts;

/// <summary>
/// Radar hunting: a strike airframe that can carry anti-radiation missiles is armed with them and
/// flown on the RADAR mission straight at the player's known radars, to take down their coverage.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>Radar hunters kept in the air at once while the player has a radar to hunt.</summary>
    private const int MaxRadarHunters = 2;

    /// <summary>The nearest radar on the surface of another faction's that is emitting, at the
    /// position this faction last knew it at. An ARAD seeker homes on emission, so a radar that is
    /// switched off is not a target.</summary>
    private static Unit? FindRadarToHunt(FactionHQ hq, GlobalPosition from, out GlobalPosition at)
    {
        at = default;
        Unit? best = null;
        float bestDistance = float.MaxValue;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            if (!entry.Value.TryGetUnit(out Unit unit)
                || unit == null
                || unit is Aircraft
                || unit.disabled
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq)
                || !unit.HasRadarEmission()
                || !hq.TryGetKnownPosition(unit, out GlobalPosition known))
            {
                continue;
            }

            float distance = FastMath.Distance(known, from);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = unit;
                at = known;
            }
        }

        return best;
    }

    /// <summary>A plane carrying anti-radiation missiles and nothing else that hits the ground. A
    /// bomber with one ARAD among its bombs stays a bomber.</summary>
    private static bool IsRadarHunter(Aircraft aircraft, FactionHQ hq)
    {
        if (aircraft.weaponStations == null
            || !CommanderAirCommandService.HasPlanePilot(aircraft)
            || IsRadarPicket(aircraft.definition, hq))
        {
            return false;
        }

        bool arad = false;
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            WeaponInfo? info = station?.WeaponInfo;
            if (station == null || info == null || station.Ammo <= 0)
            {
                continue;
            }

            if (CommanderAirCommandService.IsAradWeapon(info))
            {
                arad = true;
            }
            else if (!info.gun && CommanderAirCommandService.IsWeaponEligible(info, CommanderAirCommandService.AirCommandMode.Cas))
            {
                return false;
            }
        }

        return arad;
    }

    /// <summary>Whether the next strike buy should be a radar hunter: fewer than the cap are up
    /// and the faction knows of a player radar to send one at.</summary>
    private static bool WantsRadarHunter(FactionHQ hq)
    {
        int hunters = 0;
        foreach (Aircraft aircraft in EnumerateAircraft(hq))
        {
            if (IsRadarHunter(aircraft, hq))
            {
                hunters++;
            }
        }

        return hunters < MaxRadarHunters
            && FindRadarToHunt(hq, CommanderCaptureService.GetTerritoryCenter(hq), out _) != null;
    }

    /// <summary>A radar hunter's fit: ARADs first, a jammer pod to live through the SAM's reply,
    /// and a little for self-defence missiles.</summary>
    private static float ScoreRadarHunterStore(WeaponMount mount)
    {
        WeaponInfo info = mount.info;
        if (CommanderAirCommandService.IsAradWeapon(info))
        {
            return 1f + info.effectiveness.antiRadar;
        }

        return IsJammerMount(mount) ? 0.5f : IsAirOnlyStore(mount) ? info.effectiveness.antiAir * 0.25f : 0f;
    }

    private static bool CarriesArad(Loadout? loadout)
    {
        for (int i = 0; loadout?.weapons != null && i < loadout.weapons.Count; i++)
        {
            WeaponInfo? info = loadout.weapons[i]?.info;
            if (info != null && CommanderAirCommandService.IsAradWeapon(info))
            {
                return true;
            }
        }

        return false;
    }
}
