using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The AIR MISSIONS window: what is already flying, and what is sitting idle waiting to be given
/// something to do. It is a window of its own on purpose - it outlives the launch panel, so
/// re-tasking a package does not mean reopening the whole thing.
/// </summary>
internal sealed partial class CommanderAirCommandUi
{
    /// <summary>The order TASK AS cycles through: the AIR window's buttons, AREA's focuses in turn.</summary>
    private static readonly CommanderAirCommandService.AirCommandMode[] TaskModes =
    {
        CommanderAirCommandService.AirCommandMode.Hybrid,
        CommanderAirCommandService.AirCommandMode.AirGuard,
        CommanderAirCommandService.AirCommandMode.Cas,
        CommanderAirCommandService.AirCommandMode.StrategicStrike,
        CommanderAirCommandService.AirCommandMode.AwacsJammer,
        CommanderAirCommandService.AirCommandMode.Autopilot,
        CommanderAirCommandService.AirCommandMode.Standby,
    };

    /// <summary>
    /// The altitude slider, in the stepper's own notches (LOW LEVEL, STANDARD, then 250 m each).
    /// Returns the change as a step delta, because that is what the networked order carries.
    /// </summary>
    internal static float AltitudeSlider(Rect rect, float altitude)
    {
        int notch = CommanderAirCommandService.AltitudeNotch(altitude);
        int picked = Mathf.RoundToInt(GUI.HorizontalSlider(rect, notch, -1f, CommanderAirCommandService.MaxAltitudeNotch));
        return (picked - notch) * CommanderAirCommandService.TargetAltitudeStepMeters;
    }

    /// <summary>The mission radius slider, whole kilometres; returns the change in km.</summary>
    private static float RadiusSlider(Rect rect, float radiusKm)
    {
        float current = Mathf.Round(radiusKm);
        return Mathf.Round(GUI.HorizontalSlider(rect, current, 5f, 150f)) - current;
    }

    private void DrawMissionWindow(int windowId)
    {
        if (GUI.Button(new Rect(missionWindowRect.width - 30f, 3f, 26f, 22f), "X", CommanderUiTheme.ChromeButton))
        {
            MissionsVisible = false;
            return;
        }

        float y = 34f;
        float tabWidth = (missionWindowRect.width - 26f) * 0.5f;
        if (GUI.Button(
            new Rect(10f, y, tabWidth, 26f),
            $"ACTIVE ({missionAircraft.Count})",
            showIdleAircraft ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
        {
            showIdleAircraft = false;
            missionScroll = Vector2.zero;
        }

        if (GUI.Button(
            new Rect(16f + tabWidth, y, tabWidth, 26f),
            $"IDLE ({idleAircraft.Count})",
            showIdleAircraft ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            showIdleAircraft = true;
            missionScroll = Vector2.zero;
        }

        y += 32f;

        // The window is as tall as its list, and says so when there is nothing in it.
        int listed = showIdleAircraft ? idleAircraft.Count : missionAircraft.Count;
        missionContentHeight = y + (Mathf.Max(1, listed) * 58f) + 16f + (showIdleAircraft ? 0f : 126f);
        if (listed == 0)
        {
            GUI.Label(
                new Rect(12f, y + 4f, missionWindowRect.width - 24f, 44f),
                showIdleAircraft ? "No aircraft waiting for a task." : "Nothing flying. Launch a sortie from AIR.",
                CommanderUiTheme.MutedLabel);
            missionContentHeight = y + 60f;
            GUI.DragWindow(new Rect(0f, 0f, missionWindowRect.width, 28f));
            return;
        }

        if (showIdleAircraft)
        {
            DrawIdleAircraft(y);
            CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, missionWindowRect.width, missionWindowRect.height));
            GUI.DragWindow(new Rect(0f, 0f, missionWindowRect.width, 28f));
            return;
        }

        Aircraft? selectedAircraft = null;
        for (int i = 0; i < missionAircraft.Count; i++)
        {
            if (service.IsMissionAircraftSelected(missionAircraft[i]))
            {
                selectedAircraft = missionAircraft[i];
                break;
            }
        }

        float footerHeight = selectedAircraft != null ? 126f : 0f;
        Rect view = new(10f, y, missionWindowRect.width - 20f, missionWindowRect.height - y - 12f - footerHeight);
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, (missionAircraft.Count * 58f) + 4f));
        missionScroll = GUI.BeginScrollView(view, missionScroll, inner);
        bool oldEnabled = GUI.enabled;
        for (int i = 0; i < missionAircraft.Count; i++)
        {
            Aircraft aircraft = missionAircraft[i];
            float rowY = 2f + (i * 58f);
            if (GUI.Button(
                new Rect(2f, rowY, inner.width - 134f, 52f),
                service.GetMissionAircraftLabel(aircraft),
                service.IsMissionAircraftSelected(aircraft) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.ToggleMissionAircraft(aircraft);
            }

            GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;
            if (GUI.Button(
                new Rect(inner.width - 128f, rowY, 50f, 52f),
                new GUIContent("AREA", "Click a new centre for this aircraft's mission area."),
                CommanderUiTheme.Button))
            {
                service.BeginMissionAreaEdit(aircraft);
            }

            // Same name as the selection bar's button for the same order: land, refuel, rearm, go back out.
            if (GUI.Button(
                new Rect(inner.width - 74f, rowY, 72f, 52f),
                new GUIContent("RESUPPLY", "Lands at the nearest field you hold, refuels and rearms from its depots, then flies back to this mission."),
                CommanderUiTheme.Button))
            {
                service.RequestReturnToBase(aircraft);
            }

            GUI.enabled = oldEnabled;
        }

        GUI.EndScrollView();

        if (selectedAircraft != null)
        {
            float footerY = view.yMax + 6f;
            GUI.Box(new Rect(10f, footerY, missionWindowRect.width - 20f, 116f), string.Empty, CommanderUiTheme.Panel);
            GUI.Label(new Rect(20f, footerY + 7f, 104f, 24f), "AREA RADIUS", CommanderUiTheme.MutedLabel);
            GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;
            float radiusKm = service.GetMissionRadiusKm(selectedAircraft);
            float radiusDelta = RadiusSlider(new Rect(126f, footerY + 14f, 100f, 12f), radiusKm);
            if (radiusDelta != 0f)
            {
                service.StepMissionRadius(selectedAircraft, radiusDelta);
            }

            CommanderUiTheme.FitLabel(new Rect(232f, footerY + 8f, 74f, 24f), $"{radiusKm:0} km", CommanderUiTheme.Header);

            // Altitude is changed on a package that is already up, not only at launch: "the AI
            // flies too low" is something a player notices while watching it, not while buying it.
            GUI.Label(new Rect(20f, footerY + 41f, 104f, 24f), "ALTITUDE", CommanderUiTheme.MutedLabel);
            float missionAltitude = service.GetMissionAltitude(selectedAircraft);
            float altitudeDelta = AltitudeSlider(new Rect(126f, footerY + 48f, 100f, 12f), missionAltitude);
            if (altitudeDelta != 0f)
            {
                service.StepMissionAltitude(selectedAircraft, altitudeDelta);
            }

            // 74 px is narrower than STANDARD at the header size, which is how the D ended up on a
            // second line with both lines cut in half.
            CommanderUiTheme.FitLabel(
                new Rect(232f, footerY + 42f, 74f, 24f),
                CommanderAirCommandService.FormatTargetAltitude(missionAltitude),
                CommanderUiTheme.Header);

            if (GUI.Button(
                new Rect(20f, footerY + 77f, missionWindowRect.width - 40f, 32f),
                "MOVE AREA CENTER",
                CommanderUiTheme.PrimaryButton))
            {
                service.BeginMissionAreaEdit(selectedAircraft);
            }

            GUI.enabled = oldEnabled;
        }

        CommanderUiTheme.DrawTooltip(new Rect(0f, 0f, missionWindowRect.width, missionWindowRect.height));
        GUI.DragWindow(new Rect(0f, 0f, missionWindowRect.width, 28f));
    }

    /// <summary>
    /// Aircraft that are already in the world and still flying their own orders. TASK applies the
    /// mission type picked right here: an area mode asks for a circle, AUTOPILOT releases it to the
    /// game's AI, and STANDBY sends it to its nearest field to sit down. The picker is the same
    /// choice as AIR COMMAND's mission row, shown here because that window may well be closed.
    /// </summary>
    private void DrawIdleAircraft(float y)
    {
        CommanderAirCommandService.AirCommandMode mode = service.SelectedMode;
        int current = System.Array.IndexOf(TaskModes, mode);
        const string taskTooltip = "Click to change what TASK gives an aircraft. AREA and RADAR then ask you to place a circle; "
            + "AUTOPILOT hands it to the game's AI; STANDBY lands it at its nearest field.";
        if (GUI.Button(
            new Rect(10f, y, missionWindowRect.width - 20f, 32f),
            new GUIContent($"TASK AS:  {CommanderAirCommandService.GetModeShortLabel(mode)}   >", taskTooltip),
            CommanderUiTheme.Button))
        {
            service.SelectMode(TaskModes[(current + 1) % TaskModes.Length]);
        }

        y += 38f;

        Rect view = new(10f, y, missionWindowRect.width - 20f, missionWindowRect.height - y - 12f);
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, (idleAircraft.Count * 42f) + 4f));
        missionScroll = GUI.BeginScrollView(view, missionScroll, inner);
        bool oldEnabled = GUI.enabled;
        for (int i = 0; i < idleAircraft.Count; i++)
        {
            Aircraft aircraft = idleAircraft[i];
            float rowY = 2f + (i * 42f);
            if (GUI.Button(
                new Rect(2f, rowY, inner.width - 70f, 36f),
                CommanderGameAccess.GetUnitLabel(aircraft),
                CommanderUiTheme.Button))
            {
                CommanderSelectionService.Instance?.SelectUnit(aircraft, false);
                CommanderCameraFollowService.Instance?.FocusSelection();
            }

            GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;
            if (GUI.Button(new Rect(inner.width - 66f, rowY, 64f, 36f), "TASK", CommanderUiTheme.PrimaryButton))
            {
                service.BeginAdoption(aircraft);
            }

            GUI.enabled = oldEnabled;
        }

        GUI.EndScrollView();
    }
}
