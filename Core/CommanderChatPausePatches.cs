using HarmonyLib;
using Rewired;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Closing the chat box switches the keyboard and mouse maps back on after a 100 ms delay in game time
/// (ChatBox.WaitToReEnableKeyboard), so a chat closed while the server is paused leaves them off until it
/// unpauses: "Open Chat" is a keyboard binding, so the chat can't be opened again. While paused, switch them back on now;
/// the game's own delay still runs once time moves and does the same again.
/// </summary>
[HarmonyPatch(typeof(ChatBox), nameof(ChatBox.OnDisable))]
internal static class CommanderChatPausePatches
{
    [HarmonyPostfix]
    internal static void Postfix()
    {
        if (Time.timeScale > 0f || !ReInput.isReady)
        {
            return;
        }

        Rewired.Player player = ReInput.players.GetPlayer(0);
        foreach (ControllerMap map in player.controllers.maps.GetAllMaps(ControllerType.Keyboard))
        {
            map.enabled = true;
        }

        foreach (ControllerMap map in player.controllers.maps.GetAllMaps(ControllerType.Mouse))
        {
            map.enabled = true;
        }

        GameplayUI.AllowPauseKeybind = true;
    }
}
