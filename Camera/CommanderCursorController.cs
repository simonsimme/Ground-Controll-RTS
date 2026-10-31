namespace GroundControlRts;

internal sealed class CommanderCursorController
{
    private bool active;
    private bool previousMapFlag;
    private bool previousForceHidden;

    internal static void TickActive()
    {
        if (!CursorManager.GetFlag(CursorFlags.Map))
        {
            CursorManager.SetFlag(CursorFlags.Map, true);
        }

        if (!CursorManager.Visible)
        {
            CursorManager.ForceHidden(false);
            CursorManager.Refresh();
        }
    }

    internal void Activate()
    {
        // A second Activate would record the flag this class set itself as "the player's", and
        // Deactivate would then leave the cursor free over the cockpit.
        if (active)
        {
            return;
        }

        previousMapFlag = CursorManager.GetFlag(CursorFlags.Map);
        previousForceHidden = CursorManager.GetFlags() != CursorFlags.None && !CursorManager.Visible;
        active = true;
        CursorManager.ForceHidden(false);
        CursorManager.SetFlag(CursorFlags.Map, true);
    }

    internal void Deactivate()
    {
        if (!active)
        {
            return;
        }

        // The map flag goes back to whether the game's map is really open, not to what it was when
        // RTS mode started. Entering from the spawn map and leaving in a cockpit restored "map open",
        // which keeps the cursor free and unlocked, so the mouse did nothing for flying.
        CursorManager.SetFlag(CursorFlags.Map, previousMapFlag && DynamicMap.mapMaximized);
        CursorManager.ForceHidden(previousForceHidden);
        active = false;
    }
}
