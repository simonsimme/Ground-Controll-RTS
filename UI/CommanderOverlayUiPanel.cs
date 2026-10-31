using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    private static readonly float[] TimeScaleSteps = { 1f, 2f, 4f };

    /// <summary>
    /// The window keys. Called from the input controller's keyboard pass, so they work wherever the
    /// cursor is, and skipped while Settings is waiting for a key to bind.
    /// </summary>
    internal void HandleWindowHotkeys()
    {
        if (bindingCapture != null || ScreenshotUiHidden)
        {
            return;
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.OpenOrderOfBattle))
        {
            ToggleOrderOfBattle();
        }

        if (!CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            return;
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.OpenBuild))
        {
            ToggleBuild();
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.OpenGroundUnits))
        {
            spawnService.SelectNearestDepot();
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.OpenAirCommand))
        {
            ToggleAirCommand();
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.OpenNaval) && !CommanderDraftService.IsDraftMission)
        {
            navalPurchaseUi.Toggle();
        }
    }

    private static float DrawGuideSection(float y, float width, string title, string text)
    {
        y = CommanderUiTheme.DrawSection(new Rect(12f, y, width, 18f), title);
        float height = CommanderUiTheme.Label.CalcHeight(new GUIContent(text), width);
        GUI.Label(new Rect(12f, y, width, height), text, CommanderUiTheme.Label);
        return y + height + 12f;
    }

    /// <summary>
    /// The guide behind the command bar's "?": how the mouse and keys command, and in core mode the
    /// switch that unlocks every feature. The windows themselves are all on the bar now, so this
    /// panel no longer repeats them as a column of buttons.
    /// </summary>
    private void DrawPanelWindow(int windowId)
    {
        if (GUI.Button(new Rect(panelRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            panelVisible = false;
        }

        float width = panelRect.width - 24f;
        float y = 38f;
        if (!CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            string mission = string.IsNullOrWhiteSpace(CommanderFeatureGate.MissionName)
                ? "UNKNOWN MISSION"
                : CommanderFeatureGate.MissionName.ToUpperInvariant();
            y = CommanderUiTheme.DrawSection(new Rect(12f, y, width, 18f), $"CORE MODE  {mission}");
            const string core = "Building, buying and Air Command are for the mod's own missions and the large strategic ones. The greyed-out tabs on the bar need ALL FEATURES.";
            float coreHeight = CommanderUiTheme.MutedLabel.CalcHeight(new GUIContent(core), width);
            GUI.Label(new Rect(12f, y, width, coreHeight), core, CommanderUiTheme.MutedLabel);
            y += coreHeight + 8f;
            if (CommanderNetwork.IsGuest)
            {
                GUI.Label(new Rect(12f, y, width, 34f), "Only the host can unlock all features. They then unlock for everyone.", CommanderUiTheme.MutedLabel);
            }
            else if (GUI.Button(
                new Rect(12f, y, width, 34f),
                new GUIContent(
                    advancedUnlockConfirmation ? "ARE YOU SURE? UNLOCK ALL FEATURES" : "UNLOCK ALL FEATURES",
                    "Features behind this switch are designed for large strategic missions such as Escalation and Terminal Control. Enabling them in other missions may break the mission."),
                CommanderUiTheme.DangerButton))
            {
                if (advancedUnlockConfirmation)
                {
                    unlockAdvancedFeatures();
                    advancedUnlockConfirmation = false;
                }
                else
                {
                    advancedUnlockConfirmation = true;
                }
            }

            y += 46f;
        }

        y = DrawGuideSection(y, width, "MOUSE", "Left-click selects; drag for a box; Shift adds, or removes a unit already selected. Double-click takes every unit of that type on screen, Ctrl+click every one you own. Right-click sets a travel point, Shift+right-click adds another. Right-click a hostile to attack it, a base you have found to capture it.");
        y = DrawGuideSection(y, width, "KEYS", $"{CommanderSettings.StopOrder} stops the selection; the period key finds the next idle unit. 1-9 recall control groups, Ctrl+1-9 store them. {CommanderSettings.ToggleCommander} leaves command mode. Every key is in Settings > Shortcuts.");
        y = DrawGuideSection(y, width, "WINDOWS", "Every window is a tab on the bar at the top; the small letter on a tab is its key. Hover any button to see what it does, and the ? on each window explains it.");
        panelContentHeight = y + 4f;

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, panelRect.width, panelRect.height));
        GUI.DragWindow(new Rect(0f, 0f, panelRect.width - 40f, 28f));
    }

    private void ToggleBuild()
    {
        if (CommanderDraftService.IsDraftMission)
        {
            CommanderNotice.Fail("DRAFT: NO MONEY AND NO CONSTRUCTION");
            return;
        }

        showBuildUi = true;
        CommanderSettings.ShowBuildUi = true;
        economyUi.Toggle();
    }

    private void ToggleOrderOfBattle()
    {
        showUnitListUi = true;
        CommanderSettings.ShowUnitListUi = true;
        unitListUi.Toggle();
    }

    private void ToggleAirCommand()
    {
        if (airCommandUi.Visible)
        {
            airCommandUi.Hide();
            return;
        }

        panelVisible = false;
        supplyHeliUi.Hide();
        economyUi.Hide();
        depotUi.Reset();
        airCommandUi.Show();
    }
}
