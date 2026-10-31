using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Logs every building a repair brings back from a wreck. The game never targets a destroyed
/// building, so "units keep shooting buildings destroyed long ago" is most likely a wreck that a
/// repair crew rebuilt, which is a live target again. This line is how the next log tells.
/// </summary>
[HarmonyPatch(typeof(Building), nameof(Building.OnRepairComplete))]
internal static class CommanderWreckRebuiltPatches
{
    [HarmonyPostfix]
    internal static void Postfix(Building __instance)
    {
        CommanderPlugin.Log.LogInfo(
            $"{CommanderGameAccess.GetUnitLabel(__instance)} ({__instance.NetworkHQ?.faction?.name ?? "no faction"}) "
                + $"was rebuilt from a wreck at {Time.timeSinceLevelLoad:0} s.");
    }
}
