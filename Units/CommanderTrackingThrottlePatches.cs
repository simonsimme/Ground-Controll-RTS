using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// At most one tracking refresh per contact per faction per second, sent to every client.
/// </summary>
/// <remarks>
/// <c>TargetDetector.DetectTarget</c> calls <c>FactionHQ.RpcUpdateTrackingInfo</c> for every enemy
/// every sensor sees on every scan, a reliable RPC to every client with no dedup, so a contact seen by
/// thirty units is thirty messages a scan and the traffic grows with the square of the armies. All the
/// RPC does is re-stamp <c>lastSpottedTime</c> (and the position, which <c>TrackingInfo.GetPosition</c>
/// reads live anyway while that stamp is under 4 s old), so a refresh within a second of the last one
/// changes nothing a player can see. The first sighting always goes through: nothing is in the table.
/// </remarks>
[HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RpcUpdateTrackingInfo))]
internal static class CommanderTrackingThrottlePatches
{
    private const float RefreshSeconds = 1f;

    private static readonly Dictionary<(FactionHQ Hq, PersistentID Id), float> LastSent = new();
    private static float lastNow;

    private static bool Prefix(FactionHQ __instance, PersistentID id)
    {
        float now = Time.timeSinceLevelLoad;

        // A new level restarts the clock: everything in the table belongs to the old one.
        if (now < lastNow)
        {
            LastSent.Clear();
        }

        lastNow = now;
        (FactionHQ, PersistentID) key = (__instance, id);
        if (LastSent.TryGetValue(key, out float sent) && now - sent < RefreshSeconds)
        {
            return false;
        }

        LastSent[key] = now;
        return true;
    }
}
