using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// In a big battle, or under a missile swarm, a turret that already holds a live target re-scans
/// every 4 s instead of 2.
/// </summary>
/// <remarks>
/// <c>Turret.DatalinkTargetSearch</c> / <c>FireSupportTargetSearch</c> run every
/// <c>targetAssessmentInterval</c> (2 s) on the host for every turret: <c>GetTargetsWithinCone</c>
/// walks the faction's whole <c>trackingDatabase</c> with a <c>Physics.Linecast</c> per contact in
/// reach, then <c>AssessTargetPriority</c> scores each one. That is turrets x contacts, the square of
/// the armies. An idle turret is left alone (nothing in reach is cheap, and it must still pick up a
/// new threat at full speed); only a turret already shooting at something waits the extra tick, and
/// never when the player has just ordered its unit onto a different target.
/// Missiles are contacts too, so a salvo of hundreds of them grows every turret's walk without
/// adding a single vehicle: the slowdown also starts when any faction tracks that many contacts.
/// The interval is baked into a <c>SlowUpdate</c> at <c>Start</c>, so the skip reads the private
/// <c>lastTargetAssessment</c> (written by <c>ChooseTarget</c>) instead of rescheduling anything.
/// </remarks>
[HarmonyPatch]
internal static class CommanderTurretScanPatches
{
    /// <summary>Ground vehicles, ships and aircraft on the map, all sides, before the slowdown starts.</summary>
    private const int BigBattleUnits = 200;

    /// <summary>Entries in any one faction's <c>trackingDatabase</c>, missiles included.</summary>
    private const int BigBattleContacts = 300;

    /// <summary>Just under two ticks of the game's 2 s search, so every other search runs.</summary>
    private const float EngagedRescanSeconds = 3.5f;

    private const float CountIntervalSeconds = 5f;

    private static readonly AccessTools.FieldRef<Turret, Unit> TargetRef =
        AccessTools.FieldRefAccess<Turret, Unit>("target");

    private static readonly AccessTools.FieldRef<Turret, Unit> AttachedUnitRef =
        AccessTools.FieldRefAccess<Turret, Unit>("attachedUnit");

    private static readonly AccessTools.FieldRef<Turret, float> LastAssessmentRef =
        AccessTools.FieldRefAccess<Turret, float>("lastTargetAssessment");

    private static float nextCountAt;
    private static bool bigBattle;

    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Turret), "DatalinkTargetSearch");
        yield return AccessTools.Method(typeof(Turret), "FireSupportTargetSearch");
    }

    internal static bool Prefix(Turret __instance)
    {
        if (!IsBigBattle())
        {
            return true;
        }

        Unit? target = TargetRef(__instance);
        if (target == null
            || target.disabled
            || Time.timeSinceLevelLoad - LastAssessmentRef(__instance) >= EngagedRescanSeconds)
        {
            return true;
        }

        return CommanderMoveService.WantsRetarget(AttachedUnitRef(__instance), target);
    }

    private static bool IsBigBattle()
    {
        if (!CommanderScheduler.IsDueRealtime(ref nextCountAt, CountIntervalSeconds))
        {
            return bigBattle;
        }

        int units = 0;
        int contacts = 0;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }

            contacts = Mathf.Max(contacts, hq.trackingDatabase?.Count ?? 0);
            if (hq.factionUnits == null)
            {
                continue;
            }

            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit) && unit is GroundVehicle or Ship or Aircraft)
                {
                    units++;
                }
            }
        }

        bool big = units >= BigBattleUnits || contacts >= BigBattleContacts;
        if (big != bigBattle)
        {
            CommanderPlugin.Log.LogInfo(
                $"{units} units, {contacts} contacts on the map: engaged turrets re-scan every {(big ? 4 : 2)} s.");
        }

        bigBattle = big;
        return bigBattle;
    }
}
