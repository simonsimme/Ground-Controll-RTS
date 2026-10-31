using HarmonyLib;
using NuclearOption.SavedMission;

namespace GroundControlRts;

/// <summary>
/// Ground Control Team Duel PvE: the host picks the AI's side at random when the mission loads, by
/// closing that faction to players (<c>preventJoin</c>). A mission file has no dice, so the JSON leaves
/// both sides joinable and this decides. <c>FactionHQ.OnMissionLoad</c> runs on the server only and
/// copies the mission's <c>preventJoin</c> into a SyncVar, so writing that SyncVar after it reaches
/// every guest's faction picker, and <c>Player.CanJoinFaction</c> refuses the side on the server too.
/// </summary>
[HarmonyPatch(typeof(FactionHQ), "OnMissionLoad")]
internal static class CommanderTeamDuelPvePatches
{
    internal const string MissionName = "Ground Control Team Duel PvE";

    /// <summary>Not <c>UnityEngine.Random</c>: the map seeds that with 42 while it loads.</summary>
    private static readonly System.Random Dice = new();

    /// <summary>The HQ that rolled. Destroyed with its scene, so the next match rolls again even on the
    /// same <c>Mission</c> object: the host screen hands its selection back unchanged on a re-host.</summary>
    private static FactionHQ? roller;
    private static string aiFaction = string.Empty;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void Postfix(FactionHQ __instance, Mission mission)
    {
        if (mission == null
            || __instance.faction == null
            || mission.Name.IndexOf(MissionName, System.StringComparison.OrdinalIgnoreCase) < 0
            || mission.factions.Count < 2)
        {
            return;
        }

        // Each HQ runs this once per load, so the first one rolls and the other reads its roll.
        if (roller == null || roller == __instance)
        {
            roller = __instance;
            aiFaction = mission.factions[Dice.Next(mission.factions.Count)].factionName;
            CommanderPlugin.Log.LogInfo($"Team Duel PvE: the AI commander plays {aiFaction} this match.");
        }

        __instance.NetworkpreventJoin = string.Equals(__instance.faction.factionName, aiFaction, System.StringComparison.Ordinal);
    }
}
