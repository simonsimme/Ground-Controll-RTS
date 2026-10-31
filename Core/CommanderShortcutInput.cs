using BepInEx.Configuration;
using NuclearOption.MissionEditorScripts;
using Rewired;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderShortcutInput
{
    /// <summary>
    /// True while the player is typing, so no keyboard hotkey fires into a chat line.
    /// <c>InputFieldChecker</c> only lives in the mission editor, so in a match chat is known by
    /// the cursor flag <c>ChatBox</c> sets while open. The chat opens in <c>MessageUI.LateUpdate</c>,
    /// after every mod hotkey has read the frame, so the frame of the game's own "Open Chat" press
    /// (T by default, which is also AIR COMMAND) counts as typing too.
    /// </summary>
    internal static bool IsTyping =>
        InputFieldChecker.InsideInputField
        || CursorManager.GetFlag(CursorFlags.Chat)
        || (ChatBox.ChatAllowed && ReInput.isReady && ReInput.players.GetPlayer(0).GetButtonDown("Open Chat"));

    internal static bool IsDown(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None
            && !BlockedByTyping(shortcut.MainKey)
            && Input.GetKeyDown(shortcut.MainKey)
            && AreModifiersPressed(shortcut);
    }

    internal static bool IsUp(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None && Input.GetKeyUp(shortcut.MainKey);
    }

    internal static bool IsPressed(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None
            && !BlockedByTyping(shortcut.MainKey)
            && Input.GetKey(shortcut.MainKey)
            && AreModifiersPressed(shortcut);
    }

    // Mouse buttons type nothing, so clicks keep working with the chat open.
    private static bool BlockedByTyping(KeyCode key)
    {
        return (key < KeyCode.Mouse0 || key > KeyCode.Mouse6) && IsTyping;
    }

    private static bool AreModifiersPressed(KeyboardShortcut shortcut)
    {
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!Input.GetKey(modifier))
            {
                return false;
            }
        }

        return true;
    }
}
