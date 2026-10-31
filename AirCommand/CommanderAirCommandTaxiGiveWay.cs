using System.Collections.Generic;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// One direction at a time on a taxiway: an aeroplane holds where it is while another is taxiing
/// towards it along the route it is about to take, and goes once that one has passed or turned off.
/// </summary>
/// <remarks>
/// Taxiways are one lane and aeroplanes cannot reverse. The base taxi and
/// <see cref="GetFollowingSpeed"/> only look ahead of the nose, so two aircraft entering a stretch
/// from either end deadlock. The route ahead is read from the taxi's own pathfinder, so this sees
/// the conflict before the aeroplane reaches the shared stretch.
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>How far along its route an aeroplane looks for oncoming taxi traffic.</summary>
    private const float GiveWayLookAheadMeters = 400f;

    /// <summary>Oncoming: the other's nose points back along our route within ~45 degrees.</summary>
    private const float GiveWayHeadingCos = -0.7f;

    /// <summary>Added to both half spans: how far off our route the other may be and still meet us.</summary>
    private const float GiveWayLateralMarginMeters = 4f;

    /// <summary>A give-way entry not refreshed for this long belongs to an aeroplane no longer taxiing.</summary>
    private const float GiveWayStaleSeconds = 1f;

    /// <summary>Who each aeroplane is holding for, and when that was last confirmed.</summary>
    private static readonly Dictionary<Aircraft, (Aircraft Other, float At)> GivingWay = new();

    private static readonly List<Vector3> GiveWayRoute = new();

    private static bool MustGiveWay(Aircraft aircraft, AIPilotTaxiState taxi, Airbase field)
    {
        Aircraft? oncoming = FindOncomingTaxi(aircraft, taxi, field);
        if (oncoming == null || IsGivingWayTo(oncoming, aircraft))
        {
            GivingWay.Remove(aircraft);
            return false;
        }

        if (!GivingWay.TryGetValue(aircraft, out (Aircraft Other, float At) held) || !ReferenceEquals(held.Other, oncoming))
        {
            if (GivingWay.Count > 512)
            {
                GivingWay.Clear();
            }

            CommanderPlugin.Log.LogInfo(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} holds at {CommanderCaptureService.GetAirbaseLabel(field)} "
                    + $"for {CommanderGameAccess.GetUnitLabel(oncoming)} taxiing towards it.");
        }

        GivingWay[aircraft] = (oncoming, Time.time);
        return true;
    }

    /// <summary>Whether <paramref name="from"/> is holding for <paramref name="to"/>, directly or down a chain of holds.</summary>
    private static bool IsGivingWayTo(Aircraft from, Aircraft to)
    {
        Aircraft current = from;
        for (int i = 0; i < 8; i++)
        {
            if (!GivingWay.TryGetValue(current, out (Aircraft Other, float At) held) || Time.time - held.At > GiveWayStaleSeconds)
            {
                return false;
            }

            if (ReferenceEquals(held.Other, to))
            {
                return true;
            }

            current = held.Other;
        }

        return false;
    }

    /// <summary>The nearest AI aeroplane taxiing towards this one along this one's route ahead, if any.</summary>
    private static Aircraft? FindOncomingTaxi(Aircraft aircraft, AIPilotTaxiState taxi, Airbase field)
    {
        PathfindingAgent? agent = TaxiPathfinderRef(taxi);
        if (agent == null)
        {
            return null;
        }

        // The route is the queued road points, then the junctions after them. Between junctions a
        // curved taxiway is cut straight, which can only miss a conflict, never invent one.
        GiveWayRoute.Clear();
        GiveWayRoute.Add(aircraft.transform.position);
        List<GlobalPosition> waypoints = PathWaypointsRef(agent);
        for (int i = 0; i < waypoints.Count; i++)
        {
            GiveWayRoute.Add(waypoints[i].ToLocalPosition());
        }

        List<Node> nodes = PathNodesRef(agent);
        for (int i = 0; i < nodes.Count; i++)
        {
            GiveWayRoute.Add(nodes[i].position.ToLocalPosition());
        }

        if (GiveWayRoute.Count < 2)
        {
            return null;
        }

        Aircraft? nearest = null;
        float nearestAlong = float.MaxValue;
        List<Aircraft> others = field.ControlledAircraft;
        for (int o = 0; o < others.Count; o++)
        {
            Aircraft other = others[o];
            if (other == null
                || ReferenceEquals(other, aircraft)
                || other.disabled
                || other.radarAlt > 5f
                || other.pilots == null
                || other.pilots.Length == 0
                || other.pilots[0] == null
                || other.pilots[0].currentState is not AIPilotTaxiState)
            {
                continue;
            }

            Vector3 position = other.transform.position;
            Vector3 heading = other.transform.forward;
            heading.y = 0f;
            heading.Normalize();
            float reach = aircraft.maxRadius + other.maxRadius + GiveWayLateralMarginMeters;
            float along = 0f;
            for (int i = 1; i < GiveWayRoute.Count && along < GiveWayLookAheadMeters && along < nearestAlong; i++)
            {
                Vector3 a = GiveWayRoute[i - 1];
                Vector3 segment = GiveWayRoute[i] - a;
                segment.y = 0f;
                float length = segment.magnitude;
                if (length < 0.5f)
                {
                    continue;
                }

                Vector3 direction = segment / length;
                Vector3 offset = position - a;
                offset.y = 0f;
                float t = Vector3.Dot(offset, direction);
                if (t >= 0f
                    && t <= length
                    && (offset - (direction * t)).magnitude < reach
                    && Vector3.Dot(heading, direction) < GiveWayHeadingCos)
                {
                    nearest = other;
                    nearestAlong = along + t;
                    break;
                }

                along += length;
            }
        }

        return nearestAlong <= GiveWayLookAheadMeters ? nearest : null;
    }
}
