using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderMapClickTracker
{
    private const float MaxClickMovement = 8f;

    private bool pressed;
    private Vector2 pressPosition;

    internal bool Tick(DynamicMap dynamicMap, out GlobalPosition position)
    {
        position = default;

        // A press on a window drawn over the map (the selection bar, AIR COMMAND, the map's own
        // resize grip) is that window's, not a click on the map underneath it. Every press
        // re-decides, so one this tracker never saw released cannot fire later.
        Vector2 mouse = Input.mousePosition;
        if (CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction))
        {
            pressed = dynamicMap.IsCursorInMapRectangle()
                && CommanderOverlayUi.Instance?.ContainsScreenPoint(mouse) != true
                && CommanderTacticalMapService.Instance?.OwnsPointer(mouse) != true;
            pressPosition = mouse;
        }

        if (!pressed || !CommanderShortcutInput.IsUp(CommanderSettings.PrimaryAction))
        {
            return false;
        }

        pressed = false;
        if (Vector2.Distance(pressPosition, Input.mousePosition) > MaxClickMovement)
        {
            return false;
        }

        return dynamicMap.TryGetCursorCoordinates(out position);
    }

    internal void Reset()
    {
        pressed = false;
    }
}
