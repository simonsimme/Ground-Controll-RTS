using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Keeps the enemy's jamming pods switched on while its aircraft fly their ordinary missions.
/// </summary>
/// <remarks>
/// The base game only ever uses a <c>JammingPod</c> from <c>AIPilotCombatModes.UseJammer</c>, a
/// mode of its own that the pilot enters when its target search picks the pod's station, and then
/// it orbits a radar and does nothing else. A strike airframe carrying a pod beside its bombs never
/// picks it, so the Alkyon flew every match with its jammer off. The pod itself is simple: it jams
/// whatever <c>SetTarget</c> gave it for as long as <c>Fire</c> keeps being called (its
/// <c>LateUpdate</c> switches it off the first frame nobody fired it), and it does not care which
/// station the pilot is using. So the commander points it at the nearest player radar in reach once
/// a second and holds the trigger every frame. Radar pickets are left to the base game's own mode.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    private const float JammerPickIntervalSeconds = 1f;

    private readonly List<ActiveJammer> jammers = new();
    private float nextJammerPickAt;

    /// <summary>The nearest radar of another faction's the pod can reach and see, as the base game's
    /// own <c>LookForJammingTargets</c> filters them.</summary>
    private static Unit? FindJammingTarget(FactionHQ hq, Aircraft aircraft, float range)
    {
        GlobalPosition from = aircraft.GlobalPosition();
        Unit? best = null;
        float bestDistance = range;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo tracking = entry.Value;
            if (!tracking.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq)
                || !unit.HasRadarEmission())
            {
                continue;
            }

            float distance = FastMath.Distance(tracking.GetPosition(), from);
            if (distance < bestDistance && unit.LineOfSight(aircraft.transform.position, 1000f))
            {
                bestDistance = distance;
                best = unit;
            }
        }

        return best;
    }

    /// <summary>Every frame: a pod not fired this frame switches itself off.</summary>
    private void DriveJammers(FactionHQ localHq)
    {
        if (CommanderScheduler.IsDue(ref nextJammerPickAt, JammerPickIntervalSeconds))
        {
            PickJammerTargets(localHq);
        }

        for (int i = jammers.Count - 1; i >= 0; i--)
        {
            ActiveJammer jammer = jammers[i];
            if (jammer.Aircraft == null
                || jammer.Aircraft.disabled
                || jammer.Pod == null
                || jammer.Target == null
                || jammer.Target.disabled)
            {
                jammers.RemoveAt(i);
                continue;
            }

            Vector3 velocity = jammer.Aircraft.rb != null ? jammer.Aircraft.rb.velocity : Vector3.zero;
            jammer.Pod.Fire(jammer.Aircraft, jammer.Target, velocity, jammer.Station, default);
        }
    }

    private void PickJammerTargets(FactionHQ localHq)
    {
        jammers.Clear();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || ReferenceEquals(hq, localHq) || !hq.IsServer || hq.faction == null)
            {
                continue;
            }

            foreach (Aircraft aircraft in EnumerateAircraft(hq))
            {
                if (IsRadarPicket(aircraft.definition, hq) || aircraft.weaponStations == null)
                {
                    continue;
                }

                for (int s = 0; s < aircraft.weaponStations.Count; s++)
                {
                    WeaponStation station = aircraft.weaponStations[s];
                    WeaponInfo? info = station?.WeaponInfo;
                    if (station == null || info == null || !info.jammer)
                    {
                        continue;
                    }

                    Unit? target = FindJammingTarget(hq, aircraft, info.targetRequirements.maxRange);
                    if (target == null)
                    {
                        break;
                    }

                    for (int w = 0; w < station.Weapons.Count; w++)
                    {
                        if (station.Weapons[w] is JammingPod pod)
                        {
                            pod.SetTarget(target);
                            jammers.Add(new ActiveJammer(aircraft, station, pod, target));
                        }
                    }
                }
            }
        }
    }

    private readonly struct ActiveJammer
    {
        internal ActiveJammer(Aircraft aircraft, WeaponStation station, JammingPod pod, Unit target)
        {
            Aircraft = aircraft;
            Station = station;
            Pod = pod;
            Target = target;
        }

        internal Aircraft Aircraft { get; }

        internal WeaponStation Station { get; }

        internal JammingPod Pod { get; }

        internal Unit Target { get; }
    }
}
