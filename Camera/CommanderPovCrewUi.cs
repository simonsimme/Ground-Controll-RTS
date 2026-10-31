using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderPovCrewUi
{
    private const float Height = 34f;
    private const float LabelWidth = 76f;
    private const float ButtonWidth = 86f;
    private const float Padding = 4f;
    private const float BottomMargin = 74f;

    private readonly CommanderCameraFollowService cameraFollowService;
    private Rect menuRect;

    internal CommanderPovCrewUi(CommanderCameraFollowService cameraFollowService)
    {
        this.cameraFollowService = cameraFollowService;
    }

    private bool IsVisible => cameraFollowService.PovMode
                && cameraFollowService.PovCrewSeats.Count > 0;

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        return IsVisible && menuRect.Contains(CommanderUiScale.ScreenToGui(screenPoint));
    }

    internal void Draw()
    {
        IReadOnlyList<PovCrewSeat> seats = cameraFollowService.PovCrewSeats;
        if (!cameraFollowService.PovMode || seats.Count == 0)
        {
            menuRect = Rect.zero;
            return;
        }

        CommanderUiTheme.Ensure();
        float width = LabelWidth + (seats.Count * ButtonWidth) + (Padding * 2f);
        menuRect = new Rect(
            (CommanderUiScale.Width - width) * 0.5f,
            CommanderUiScale.Height - BottomMargin - Height,
            width,
            Height);

        GUI.Box(menuRect, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(
            new Rect(menuRect.x + Padding + 4f, menuRect.y + 3f, LabelWidth - 8f, Height - 6f),
            "POV SEAT",
            CommanderUiTheme.MutedLabel);

        for (int i = 0; i < seats.Count; i++)
        {
            PovCrewSeat seat = seats[i];
            bool oldEnabled = GUI.enabled;
            GUI.enabled = seat.IsAvailable;
            Rect buttonRect = new(
                menuRect.x + Padding + LabelWidth + (i * ButtonWidth),
                menuRect.y + 4f,
                ButtonWidth - 4f,
                Height - 8f);
            GUIStyle style = cameraFollowService.PovCrewIndex == i
                ? CommanderUiTheme.SelectedButton
                : CommanderUiTheme.Button;
            if (GUI.Button(buttonRect, seat.Label, style))
            {
                cameraFollowService.TryMoveToCrewPosition(i);
            }

            GUI.enabled = oldEnabled;
        }
    }
}
