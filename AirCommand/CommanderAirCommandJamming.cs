using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    private static readonly List<Unit> JammingCandidates = new();

    private static readonly System.Comparison<Unit> ById = (left, right) => left.persistentID.Id.CompareTo(right.persistentID.Id);

    /// <summary>
    /// Postfix hook for <c>CombatAI.LookForJammingTargets</c>, which <c>AIPilotCombatModes.UseJammer</c>
    /// calls once a second to hand each pod one emitter. The base game sorts by friendly missiles in
    /// flight and otherwise keeps the tracking database's order, so a RADAR Medusa with two pods
    /// jammed the same two Vagrants all sortie and never touched the radars beside them. A radar's
    /// jam decays within a second or two of the pod leaving it, so the pods step through every
    /// emitter in reach, one set per call, in a fixed order so each gets its turn.
    /// </summary>
    internal static void RotateJammingTargets(Aircraft aircraft, WeaponStation station, List<Unit> outTargets, ref int count)
    {
        if (Instance == null
            || aircraft == null
            || station?.WeaponInfo == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Mode != AirCommandMode.AwacsJammer)
        {
            return;
        }

        FactionHQ? hq = aircraft.NetworkHQ;
        if (hq == null)
        {
            return;
        }

        // The base game's own filter, so nothing is jammed that it would not have jammed.
        JammingCandidates.Clear();
        float range = station.WeaponInfo.targetRequirements.maxRange;
        GlobalPosition from = aircraft.GlobalPosition();
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo tracking = entry.Value;
            if (tracking.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit.NetworkHQ != null
                && unit.NetworkHQ != hq
                && unit.HasRadarEmission()
                && FastMath.InRange(tracking.GetPosition(), from, range)
                && unit.LineOfSight(aircraft.transform.position, 1000f))
            {
                JammingCandidates.Add(unit);
            }
        }

        int pods = station.Weapons.Count;
        if (JammingCandidates.Count <= pods)
        {
            return;
        }

        JammingCandidates.Sort(ById);
        int start = Mathf.FloorToInt(Time.time) * pods;
        outTargets.Clear();
        for (int i = 0; i < pods; i++)
        {
            outTargets.Add(JammingCandidates[(start + i) % JammingCandidates.Count]);
        }

        count = outTargets.Count;
    }
}
