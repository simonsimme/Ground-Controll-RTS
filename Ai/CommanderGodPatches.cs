using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// Every chat line lands in <c>GameplayUI.GameMessage(string)</c> on every client, so a god line
/// the host sent plays its screen effects here, on the host and on any guest that runs the mod.
/// </summary>
[HarmonyPatch(typeof(GameplayUI), nameof(GameplayUI.GameMessage), typeof(string))]
internal static class CommanderGodPatches
{
    private static void Postfix(string message)
    {
        if (message != null && message.StartsWith(CommanderGodUi.Marker, System.StringComparison.Ordinal))
        {
            CommanderGodUi.Receive(message, CommanderGodService.FirstWordsSeconds);
        }
    }
}
