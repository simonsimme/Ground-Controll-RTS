using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// Puts call signs into the base game's kill feed. <c>MessageManager</c>'s kill RPC names both units
/// from <c>PersistentUnit.unitName</c>, a copy of the bare type name taken at registration, so the
/// record is renamed just before the line is built. The RPC body's name carries a Mirage hash that a
/// game patch can change, hence the prefix match.
/// <para>
/// It also keeps the feed inside the fog: the stock feed announces every enemy loss on the map, so
/// "Cricket Alpha crashed" named an aircraft the player never had on radar. An enemy loss is only
/// shown when the local faction scored it or had the unit in its tracking database at some point.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class CommanderCallSignPatches
{
    /// <summary>
    /// Contacts the local faction has tracked and since dropped. <c>DeregisterTrackedUnit</c> runs
    /// off <c>onDisableUnit</c>, which can land before the kill RPC, so the database alone would
    /// hide every kill the player actually watched.
    /// </summary>
    private static readonly HashSet<PersistentID> ForgottenContacts = new();

    [HarmonyTargetMethod]
    internal static MethodBase TargetMethod()
    {
        return AccessTools.GetDeclaredMethods(typeof(MessageManager))
            .First(method => method.Name.StartsWith("UserCode_RpcKillMessage", System.StringComparison.Ordinal));
    }

    [HarmonyPrefix]
    internal static bool Prefix(PersistentID killerID, PersistentID killedID)
    {
        if (IsHiddenByFog(killerID, killedID))
        {
            return false;
        }

        Rename(killerID);
        Rename(killedID);
        return true;
    }

    internal static void NoteForgotten(FactionHQ hq, Unit unit)
    {
        if (unit != null && ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()) && hq.GetTrackingData(unit.persistentID) != null)
        {
            ForgottenContacts.Add(unit.persistentID);
        }
    }

    internal static void Reset()
    {
        ForgottenContacts.Clear();
        CommanderCallSigns.Reset();
    }

    private static bool IsHiddenByFog(PersistentID killerID, PersistentID killedID)
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null
            || !UnitRegistry.TryGetPersistentUnit(killedID, out PersistentUnit killed)
            || !killed.HasHQ(out FactionHQ killedHq)
            || killedHq == localHq)
        {
            return false;
        }

        if (UnitRegistry.TryGetPersistentUnit(killerID, out PersistentUnit killer)
            && killer.HasHQ(out FactionHQ killerHq)
            && killerHq == localHq)
        {
            return false;
        }

        if (killed.unit is Building)
        {
            return false;
        }

        return localHq.GetTrackingData(killedID) == null && !ForgottenContacts.Contains(killedID);
    }

    private static void Rename(PersistentID id)
    {
        if (UnitRegistry.TryGetPersistentUnit(id, out PersistentUnit record)
            && record.unit != null
            && (record.unit is Ship || record.unit is Aircraft { Player: null }))
        {
            record.unitName = CommanderGameAccess.GetUnitLabel(record.unit);
        }
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.DeregisterTrackedUnit))]
    internal static class ForgetContactPatch
    {
        [HarmonyPrefix]
        internal static void Prefix(FactionHQ __instance, Unit unit)
        {
            NoteForgotten(__instance, unit);
        }
    }
}
