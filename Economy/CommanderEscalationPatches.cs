using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NuclearOption.SavedMission;

namespace GroundControlRts;

/// <summary>
/// Per-team nuclear escalation on the duel and Champions. The base game keeps one
/// <c>MissionManager.currentEscalation</c>, ratcheted to the leading faction's score, and asks it
/// through the static <c>AllowTactical</c> / <c>AllowStrategic</c>, so the leader's kills unlocked
/// nukes for the losing side too. Here each faction is cleared by its own <c>factionScore</c> (a
/// SyncVar, so right on a guest). The static calls carry no faction, so the three callers that have
/// one set <see cref="askingHq"/> around themselves; anything else is the local UI and falls back to
/// the local HQ. <c>currentEscalation</c> itself is still written (the leader's score) so nothing
/// unpatched breaks; the readouts that show it are transpiled to the player's own score.
/// </summary>
[HarmonyPatch]
internal static class CommanderEscalationPatches
{
    private const string TacticalMessage = "Warning: Tactical nuclear weapons are cleared for deployment";
    private const string StrategicMessage = "Warning: Strategic nuclear weapons are cleared for deployment";

    private static readonly AccessTools.FieldRef<FireControl, Unit> FireControlUnit =
        AccessTools.FieldRefAccess<FireControl, Unit>("attachedUnit");

    private static readonly AccessTools.FieldRef<WeaponManager, Aircraft> WeaponManagerAircraft =
        AccessTools.FieldRefAccess<WeaponManager, Aircraft>("aircraft");

    private static readonly FieldInfo EscalationField = AccessTools.Field(typeof(MissionManager), nameof(MissionManager.currentEscalation));

    private static FactionHQ? askingHq;

    private static bool PerTeam => CommanderEnemyCommanderService.IsDuelMission;

    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.AllowTactical))]
    [HarmonyPrefix]
    private static bool AllowTacticalPrefix(ref bool __result)
    {
        return Answer(strategic: false, ref __result);
    }

    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.AllowStrategic))]
    [HarmonyPrefix]
    private static bool AllowStrategicPrefix(ref bool __result)
    {
        return Answer(strategic: true, ref __result);
    }

    // The three callers that know whose loadout they are judging.
    [HarmonyPatch(typeof(Loadout), nameof(Loadout.AllowedByHQ))]
    [HarmonyPrefix]
    private static void AllowedByHqPrefix(FactionHQ hq, out FactionHQ? __state)
    {
        Enter(hq, out __state);
    }

    [HarmonyPatch(typeof(Loadout), nameof(Loadout.AllowedByHQ))]
    [HarmonyFinalizer]
    private static void AllowedByHqFinalizer(FactionHQ? __state)
    {
        askingHq = __state;
    }

    [HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.MountAllowedNuclear))]
    [HarmonyPrefix]
    private static void MountAllowedNuclearPrefix(FactionHQ hq, out FactionHQ? __state)
    {
        Enter(hq, out __state);
    }

    [HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.MountAllowedNuclear))]
    [HarmonyFinalizer]
    private static void MountAllowedNuclearFinalizer(FactionHQ? __state)
    {
        askingHq = __state;
    }

    [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SelectAIAircraftWeapons))]
    [HarmonyPrefix]
    private static void SelectAiWeaponsPrefix(WeaponManager __instance, out FactionHQ? __state)
    {
        Aircraft aircraft = WeaponManagerAircraft(__instance);
        Enter(aircraft != null ? aircraft.NetworkHQ : null, out __state);
    }

    [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SelectAIAircraftWeapons))]
    [HarmonyFinalizer]
    private static void SelectAiWeaponsFinalizer(FactionHQ? __state)
    {
        askingHq = __state;
    }

    // Clearance messages go to the team that earned it, and a warning to everyone else.
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.SetScore))]
    [HarmonyPrefix]
    private static void SetScorePrefix(FactionHQ __instance, out float __state)
    {
        __state = __instance.factionScore;
    }

    [HarmonyPatch(typeof(FactionHQ), "CheckEscalation")]
    [HarmonyPrefix]
    private static bool CheckEscalationPrefix()
    {
        return !PerTeam;
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.SetScore))]
    [HarmonyPostfix]
    private static void SetScorePostfix(FactionHQ __instance, float __state)
    {
        MissionManager mm = NetworkSceneSingleton<MissionManager>.i;
        if (!PerTeam || mm == null || !__instance.IsServer)
        {
            return;
        }

        float score = __instance.factionScore;
        if (score > mm.currentEscalation)
        {
            mm.NetworkcurrentEscalation = score;
        }

        Announce(__instance, __state, score, mm.tacticalThreshold, TacticalMessage, "tactical");
        Announce(__instance, __state, score, mm.strategicThreshold, StrategicMessage, "strategic");
    }

    private static bool Answer(bool strategic, ref bool __result)
    {
        MissionManager mm = NetworkSceneSingleton<MissionManager>.i;
        if (!PerTeam || mm == null)
        {
            return true;
        }

        float score = ScoreOf(askingHq ?? CommanderGameAccess.GetLocalHq(), mm);
        __result = score >= (strategic ? mm.strategicThreshold : mm.tacticalThreshold);
        return false;
    }

    private static void Enter(FactionHQ? hq, out FactionHQ? previous)
    {
        previous = askingHq;
        if (hq != null)
        {
            askingHq = hq;
        }
    }

    private static void Announce(FactionHQ hq, float before, float after, float threshold, string own, string level)
    {
        MessageManager messages = NetworkSceneSingleton<MessageManager>.i;
        if (messages == null || before >= threshold || after < threshold)
        {
            return;
        }

        foreach (FactionHQ other in FactionRegistry.GetAllHQs())
        {
            messages.RpcHQMessage(other, other == hq ? own : $"Warning: the enemy is cleared for {level} nuclear weapons");
        }
    }

    private static float ScoreOf(FactionHQ? hq, MissionManager mm)
    {
        return hq != null ? hq.factionScore : mm.currentEscalation;
    }

    private static float ForPlayer(MissionManager mm)
    {
        return PerTeam ? ScoreOf(CommanderGameAccess.GetPlayerHq(), mm) : mm.currentEscalation;
    }

    private static float ForLauncher(FireControl fireControl)
    {
        MissionManager mm = NetworkSceneSingleton<MissionManager>.i;
        Unit unit = FireControlUnit(fireControl);
        return PerTeam && unit != null ? ScoreOf(unit.NetworkHQ, mm) : mm.currentEscalation;
    }

    /// <summary>The DEFCON readouts show the player's own faction's escalation.</summary>
    [HarmonyPatch]
    private static class PlayerReadouts
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AircraftSelectionMenu), "DisplayEscalationPointer");
            yield return AccessTools.Method(typeof(AircraftSelectionMenu), "Update");
            yield return AccessTools.Method(typeof(InfoPanel_DEFCON), "Update");
            yield return AccessTools.Method(typeof(ObjectiveInfoList), nameof(ObjectiveInfoList.UpdateMissionInfo));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo forPlayer = AccessTools.Method(typeof(CommanderEscalationPatches), nameof(ForPlayer));
            foreach (CodeInstruction i in instructions)
            {
                yield return i.LoadsField(EscalationField) ? new CodeInstruction(OpCodes.Call, forPlayer).MoveLabelsFrom(i) : i;
            }
        }
    }

    /// <summary>
    /// A strategic launcher's own AI fires once escalation passes the strategic line
    /// (<c>FireControl.StrategicTargetCheck</c>, an async loop), so it has to read its own side's score.
    /// </summary>
    [HarmonyPatch]
    private static class StrategicLauncher
    {
        private static MethodInfo TargetMethod()
        {
            System.Type stateMachine = AccessTools.Method(typeof(FireControl), "StrategicTargetCheck")
                .GetCustomAttribute<AsyncStateMachineAttribute>().StateMachineType;
            return AccessTools.Method(stateMachine, "MoveNext");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            FieldInfo self = AccessTools.GetDeclaredFields(original.DeclaringType).First(f => f.FieldType == typeof(FireControl));
            MethodInfo forLauncher = AccessTools.Method(typeof(CommanderEscalationPatches), nameof(ForLauncher));
            foreach (CodeInstruction i in instructions)
            {
                if (!i.LoadsField(EscalationField))
                {
                    yield return i;
                    continue;
                }

                // Swap the MissionManager on the stack for this launcher's own side's score.
                yield return new CodeInstruction(OpCodes.Pop).MoveLabelsFrom(i);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, self);
                yield return new CodeInstruction(OpCodes.Call, forLauncher);
            }
        }
    }
}
