using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Every AI commander's target priority, on every difficulty: the opponent's income buildings
/// first, then whatever is worth the most, without giving up on what is shooting at it.
/// </summary>
/// <remarks>
/// <c>CombatAI.AnalyzeTarget</c> is the one score every AI weapon ranks its candidates by: turrets
/// (<c>Turret.AssessTargetPriority</c>, opportunity x (1 + threat)), aircraft
/// (<c>ChooseHQTarget</c>, the same over range), fire control and ships. Scaling its opportunity is
/// therefore a priority table for all of them at once, and it keeps the game's own gates: a weapon
/// that cannot reach or cannot pierce a target still scores 0 against it, so nothing is fired at a
/// mine it cannot hurt. An income building outbids every unit in reach, the value term only leans
/// the rest, and the threat and range terms stay the game's, so it still kills what is in front of it.
/// A higher opportunity also makes an AI pilot braver against that target (<c>ChooseHQTarget</c>'s
/// bravery test reads it), which is wanted for a mine and mild for the rest.
/// Host only (the AI is the host's), and never a human team's weapons.
/// </remarks>
[HarmonyPatch(typeof(CombatAI), nameof(CombatAI.AnalyzeTarget))]
internal static class CommanderTargetPriorityPatches
{
    /// <summary>How much an income building outbids a unit the same weapon could hit.</summary>
    internal const float IncomePriority = 8f;

    /// <summary>The value lean: x1 at price 0, about x2.3 at 1000, the dearest on the roster.</summary>
    private const float ValueDivisor = 25f;
    private const float ValueCap = 1000f;

    /// <summary><c>IsAiFaction</c> asks the HQ for its players, too dear for a call this hot.</summary>
    private const float AiCacheSeconds = 2f;

    private static readonly Dictionary<FactionHQ, (bool Ai, float Until)> AiFactions = new();

    internal static void Postfix(Unit analyzer, TrackingInfo trackingInfo, ref OpportunityThreat __result)
    {
        if (__result.opportunity <= 0f
            || analyzer == null
            || analyzer.NetworkHQ is not FactionHQ hq
            || !IsPrioritising(hq)
            || !trackingInfo.TryGetUnit(out Unit target)
            || target == null
            || target.definition == null)
        {
            return;
        }

        float priority = GetPriority(CommanderEconomyService.IsIncomeBuildingUnit(target), target.definition.value);
        __result = new OpportunityThreat(__result.opportunity * priority, __result.threat);
    }

    internal static float GetPriority(bool income, float value)
    {
        return income ? IncomePriority : 1f + (Mathf.Sqrt(Mathf.Clamp(value, 0f, ValueCap)) / ValueDivisor);
    }

    internal static void Reset()
    {
        AiFactions.Clear();
    }

    /// <summary>The income building must beat the dearest unit, and a dearer unit a cheaper one.</summary>
    internal static void SelfCheck()
    {
        if (GetPriority(true, 0f) <= GetPriority(false, ValueCap * 10f)
            || GetPriority(false, 500f) <= GetPriority(false, 50f)
            || GetPriority(false, 0f) < 1f)
        {
            CommanderPlugin.Log.LogError(
                "Target priority self-check FAILED: an AI weapon no longer prefers an income building over "
                    + "any unit, or a dear unit over a cheap one.");
        }
    }

    private static bool IsPrioritising(FactionHQ hq)
    {
        float now = Time.realtimeSinceStartup;
        if (AiFactions.TryGetValue(hq, out (bool Ai, float Until) cached) && now < cached.Until)
        {
            return cached.Ai;
        }

        // A both-sides match hands "the local team" to each side in turn; both are the AI.
        bool ai = hq.IsServer
            && (CommanderEnemyCommanderService.LocalSideRotates || CommanderEnemyCommanderService.IsAiFaction(hq, CommanderGameAccess.GetLocalHq()));
        AiFactions[hq] = (ai, now + AiCacheSeconds);
        return ai;
    }
}
