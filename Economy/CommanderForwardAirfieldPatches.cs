using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// A forward base with a landing spot counts as an airbase for the "landed and got out" rule: an
/// ejection on the ground there returns the airframe and pays the sortie bonus, and the crew walk
/// in, instead of the aircraft being written off as a crash.
/// </summary>
/// <remarks>
/// Every one of those outcomes is decided by <c>FactionHQ.AnyNearAirbase</c>, which only knows the
/// faction's real airbases. Only these call sites are rerouted: the taxi AI and the takeoff overlay
/// ask the same question and then drive to the answer's runway, which a forward base does not have.
/// </remarks>
[HarmonyPatch]
internal static class CommanderForwardAirfieldPatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AsyncMoveNext(typeof(Aircraft), "EjectionSequence");
        yield return AccessTools.Method(typeof(Aircraft), "ServerDisableUnit");
        yield return AccessTools.Method(typeof(Aircraft), nameof(Aircraft.UnitDisabled));
        yield return AccessTools.Method(typeof(Aircraft), "UserCode_RpcJettisonCanopy_1196305304");
        yield return AsyncMoveNext(typeof(PilotDismounted), "CheckForCapture");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo original = AccessTools.Method(typeof(FactionHQ), nameof(FactionHQ.AnyNearAirbase));
        MethodInfo replacement = AccessTools.Method(typeof(CommanderForwardAirfieldPatches), nameof(AnyNearAirbaseOrForwardField));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }

            yield return instruction;
        }
    }

    /// <summary>Same stack shape as the instance call it replaces: the HQ, the position, the out.</summary>
    private static bool AnyNearAirbaseOrForwardField(FactionHQ hq, Vector3 fromPosition, out Airbase airbase)
    {
        // The out stays null for a forward base; every rerouted caller discards it.
        return hq.AnyNearAirbase(fromPosition, out airbase)
            || CommanderEconomyService.IsAtForwardAirfield(hq, fromPosition);
    }

    private static MethodInfo AsyncMoveNext(System.Type type, string name)
    {
        System.Type stateMachine = AccessTools.Method(type, name)
            .GetCustomAttribute<AsyncStateMachineAttribute>().StateMachineType;
        return AccessTools.Method(stateMachine, "MoveNext");
    }
}
