using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// Drops a rearm RPC whose station list does not fit the unit on this machine.
/// </summary>
/// <remarks>
/// <c>Unit.RpcRearm</c> carries one entry per weapon station as the host counts them, and the
/// client indexes <c>args.Stations[i]</c> and <c>weaponStations[i].Weapons[0]</c> with no bounds
/// check, throwing <c>IndexOutOfRangeException</c> from Mirage's RPC handler when the two disagree.
/// Skipping it is what the exception already did, minus the log spam; the shelf's own capacity RPC
/// still keeps the stores right.
/// </remarks>
[HarmonyPatch]
internal static class CommanderRearmRpcPatches
{
    // The body's name carries a Mirage weaver hash that a game update can change.
    [HarmonyTargetMethod]
    internal static MethodBase TargetMethod()
    {
        return AccessTools.GetDeclaredMethods(typeof(Unit))
            .First(method => method.Name.StartsWith("UserCode_RpcRearm", System.StringComparison.Ordinal));
    }

    [HarmonyPrefix]
    internal static bool Prefix(Unit __instance, RearmEventArgs args)
    {
        int count = __instance.weaponStations.Count;
        if (args.Stations == null || args.Stations.Length < count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            WeaponStation station = __instance.weaponStations[i];
            if (station == null || station.Weapons == null || station.Weapons.Count == 0)
            {
                return false;
            }
        }

        return true;
    }
}
