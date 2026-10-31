using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The loadout half of the AIR COMMAND window, in two tabs: PRESETS is one click, PYLONS starts
/// from the chosen preset and sets every set of hardpoints by hand. Plus the popup over them.
/// </summary>
internal sealed partial class CommanderAirCommandUi
{
    /// <summary>Which saved preset's DELETE is armed, and until when. Two clicks, because a saved
    /// preset is gone for good.</summary>
    private int deletePresetArmed = -1;
    private float deletePresetArmedUntil;

    private int openPylonIndex = -1;
    private Vector2 pylonScroll;

    private static Rect GetPopupRect(Rect loadoutPanel)
    {
        return new Rect(loadoutPanel.x + 40f, Mathf.Max(loadoutPanel.y - 120f, 40f), loadoutPanel.width - 80f, 360f);
    }

    private void DrawPresetList(Rect panel)
    {
        if (service.Presets.Count == 0)
        {
            GUI.Label(
                new Rect(panel.x + 12f, panel.y + 12f, panel.width - 24f, 40f),
                "This aircraft has no presets. Use PYLONS to arm it by hand.",
                CommanderUiTheme.MutedLabel);
            return;
        }

        Rect view = new(panel.x + 8f, panel.y + 8f, panel.width - 16f, panel.height - 16f);
        float innerHeight = Mathf.Max(view.height, (service.Presets.Count * 52f) + 4f);
        presetScroll = GUI.BeginScrollView(view, presetScroll, new Rect(0f, 0f, view.width - 18f, innerHeight));
        for (int i = 0; i < service.Presets.Count; i++)
        {
            bool custom = service.Presets[i].Custom;
            float buttonWidth = view.width - 24f - (custom ? 66f : 0f);
            if (GUI.Button(
                new Rect(2f, 2f + (i * 52f), buttonWidth, 46f),
                service.GetPresetLabel(i),
                i == service.SelectedPresetIndex ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.ChoosePreset(i);
            }

            if (!custom)
            {
                continue;
            }

            bool armed = deletePresetArmed == i && Time.unscaledTime <= deletePresetArmedUntil;
            if (GUI.Button(
                new Rect(buttonWidth + 6f, 2f + (i * 52f), 60f, 46f),
                new GUIContent(armed ? "SURE?" : "DELETE", "Deletes this saved preset for good. Click twice."),
                CommanderUiTheme.DangerButton))
            {
                if (armed)
                {
                    deletePresetArmed = -1;
                    service.DeletePreset(i);
                    GUI.EndScrollView();
                    return;
                }

                deletePresetArmed = i;
                deletePresetArmedUntil = Time.unscaledTime + 4f;
            }
        }

        GUI.EndScrollView();
    }

    /// <summary>The expert tab: every set of hardpoints, what is on it and what it rules out.</summary>
    private void DrawPylonLoadout(Rect panel)
    {
        CommanderAirCommandService.AirMissionOption? option = service.SelectedOption;
        if (option == null)
        {
            return;
        }

        System.Collections.Generic.List<CommanderAirCommandService.AirHardpointGroup> groups = option.HardpointGroups;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && openHardpointGroup < 0;
        Rect view = new(panel.x + 8f, panel.y + 8f, panel.width - 16f, panel.height - 104f);
        float innerHeight = Mathf.Max(view.height, (groups.Count * 50f) + 4f);
        pylonScroll = GUI.BeginScrollView(view, pylonScroll, new Rect(0f, 0f, view.width - 18f, innerHeight));
        for (int i = 0; i < groups.Count; i++)
        {
            if (GUI.Button(
                new Rect(2f, 2f + (i * 50f), view.width - 24f, 46f),
                CommanderAirCommandService.GetHardpointButtonLabel(option, groups[i]) + "   v",
                groups[i].SelectedMount != null ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                openHardpointGroup = 3;
                openPylonIndex = i;
                loadoutDropdownScroll = Vector2.zero;
            }
        }

        GUI.EndScrollView();
        GUI.enabled = oldEnabled;

        GUI.Label(
            new Rect(panel.x + 12f, panel.yMax - 94f, panel.width - 24f, 24f),
            "ON THE PYLONS: " + service.DescribeCurrentLoadout(),
            CommanderUiTheme.MutedLabel);

        GUI.enabled = oldEnabled && openHardpointGroup < 0;
        const string cannonsTooltip = "Fits built-in cannons wherever an airframe has them. One setting for every aircraft and preset. "
            + "An aircraft with cannon rounds left may not come home to rearm on its own after its missiles and bombs are gone.";
        service.IncludeInternalCannons = CommanderUiTheme.Check(
            new Rect(panel.x + 12f, panel.yMax - 66f, panel.width - 24f, 28f),
            service.IncludeInternalCannons,
            new GUIContent("INTERNAL CANNONS  (ALL AIRCRAFT)", cannonsTooltip),
            CommanderUiTheme.Toggle);
        GUI.enabled = oldEnabled;
        DrawSaveButton(new Rect(panel.x + 12f, panel.yMax - 34f, panel.width - 24f, 28f));
    }

    private void DrawSaveButton(Rect rect)
    {
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && openHardpointGroup < 0;
        if (GUI.Button(
            rect,
            new GUIContent("SAVE AS PRESET", "Keeps this loadout on the PRESETS tab for good, named after what it carries."),
            CommanderUiTheme.Button))
        {
            // Named after what it carries rather than typed: the RTS camera owns the keyboard while
            // this window is up, so a text field here would eat the movement keys.
            service.TrySaveCurrentAsPreset(service.DescribeCurrentLoadout().ToUpperInvariant(), out string _);
        }

        GUI.enabled = oldEnabled;
    }

    /// <summary>Draws the popup frame and title; false when its X closed it.</summary>
    private bool DrawPopupFrame(Rect popup, string title)
    {
        GUI.Box(popup, string.Empty, CommanderUiTheme.Window);
        GUI.Label(new Rect(popup.x + 12f, popup.y + 8f, popup.width - 52f, 24f), title, CommanderUiTheme.Header);
        if (GUI.Button(new Rect(popup.xMax - 34f, popup.y + 7f, 24f, 24f), "X", CommanderUiTheme.Button))
        {
            openHardpointGroup = -1;
            return false;
        }

        return true;
    }

    private void DrawLoadoutDropdown(Rect loadoutPanel)
    {
        if (openHardpointGroup == 3 && service.AdvancedLoadout)
        {
            DrawPylonDropdown(loadoutPanel);
        }
    }

    /// <summary>Every store one set of hardpoints takes, tagged with what it is for.</summary>
    private void DrawPylonDropdown(Rect loadoutPanel)
    {
        CommanderAirCommandService.AirMissionOption? option = service.SelectedOption;
        if (option == null || openPylonIndex < 0 || openPylonIndex >= option.HardpointGroups.Count)
        {
            openHardpointGroup = -1;
            return;
        }

        CommanderAirCommandService.AirHardpointGroup group = option.HardpointGroups[openPylonIndex];
        Rect popup = GetPopupRect(loadoutPanel);
        if (!DrawPopupFrame(popup, $"PYLON:  {group.Label}"))
        {
            return;
        }

        Rect view = new(popup.x + 10f, popup.y + 38f, popup.width - 20f, popup.height - 48f);
        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, ((group.Mounts.Count + 1) * 38f) + 4f));
        loadoutDropdownScroll = GUI.BeginScrollView(view, loadoutDropdownScroll, inner);
        if (GUI.Button(new Rect(4f, 2f, inner.width - 8f, 34f), "EMPTY", CommanderUiTheme.Button))
        {
            service.SelectHardpointMount(openPylonIndex, -1);
            openHardpointGroup = -1;
        }

        for (int i = 0; i < group.Mounts.Count; i++)
        {
            if (GUI.Button(
                new Rect(4f, 40f + (i * 38f), inner.width - 8f, 34f),
                CommanderAirCommandService.GetHardpointMountLabel(group.Mounts[i]),
                ReferenceEquals(group.SelectedMount, group.Mounts[i]) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SelectHardpointMount(openPylonIndex, i);
                openHardpointGroup = -1;
            }
        }

        GUI.EndScrollView();
    }
}
