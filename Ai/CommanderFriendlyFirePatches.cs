using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's units do not damage the enemy commander's own buildings.
/// </summary>
/// <remarks>
/// The base game never aims at a friendly: <c>Turret</c> only fires when
/// <c>target.NetworkHQ != attachedUnit.NetworkHQ</c>, and a unit that changes owner is dropped from
/// the new owner's <c>trackingDatabase</c>. What wrecks the AI's own base is the round itself:
/// artillery and rockets (<c>firesWithoutAiming</c>) skip the barrel obstruction test, the direct
/// fire test only looks 200 m ahead, and splash and misses land wherever they land. The guns
/// firing from inside the base at the player's attack are exactly where that bites. So the fix is
/// at the one place every hit goes through, <c>UnitPart.TakeDamage</c> (server only), and only for
/// an AI faction: the player's own friendly fire is left as the game has it.
/// </remarks>
[HarmonyPatch(typeof(UnitPart), nameof(UnitPart.TakeDamage))]
internal static class CommanderFriendlyFirePatches
{
    internal static bool Prefix(UnitPart __instance, PersistentID dealerID)
    {
        Unit? victim = __instance.parentUnit;
        if (victim is not Building
            || !dealerID.IsValid
            || !dealerID.TryGetUnit(out Unit dealer)
            || dealer == null
            || dealer == victim
            || victim.NetworkHQ == null
            || !ReferenceEquals(dealer.NetworkHQ, victim.NetworkHQ))
        {
            return true;
        }

        return !CommanderEnemyCommanderService.IsAiFaction(victim.NetworkHQ, CommanderGameAccess.GetLocalHq());
    }
}
