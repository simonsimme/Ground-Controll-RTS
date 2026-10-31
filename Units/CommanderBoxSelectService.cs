using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Drag-a-box selection. Works both in the 3D camera view (against the Commander world
/// markers) and on the tactical / fullscreen map (against the Basegame map icons).
/// </summary>
internal sealed class CommanderBoxSelectService : ICommanderDeactivate
{
    private const float DragThresholdPixels = 10f;

    private readonly CommanderSelectionService selectionService;
    private readonly CommanderMarkerService markerService;
    private readonly List<Unit> hits = new();

    private bool pressed;
    private Vector2 startScreenPosition;
    private Vector2 currentScreenPosition;

    internal CommanderBoxSelectService(
        CommanderSelectionService selectionService,
        CommanderMarkerService markerService)
    {
        this.selectionService = selectionService;
        this.markerService = markerService;
        Instance = this;
    }

    internal static CommanderBoxSelectService? Instance { get; private set; }

    /// <summary>True while the user is actually dragging a box (past the click threshold).</summary>
    internal bool Dragging => pressed && IsPastThreshold;

    /// <summary>True while a box drag is running over the map, so map panning can stand down.</summary>
    internal bool DraggingOnMap { get; private set; }

    private bool IsPastThreshold =>
        Vector2.Distance(startScreenPosition, currentScreenPosition) > DragThresholdPixels;

    // Leaving RTS mode mid-drag is just a cancelled drag.
    public void Deactivate()
    {
        Cancel();
    }

    internal void Cancel()
    {
        pressed = false;
        DraggingOnMap = false;
    }

    /// <summary>
    /// Drives the drag state machine. Returns true when this frame completed a box
    /// selection, in which case the caller must not also treat the release as a click.
    /// </summary>
    internal bool Tick(bool overMap)
    {
        currentScreenPosition = Input.mousePosition;

        if (CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction)
            && (!overMap || CommanderShortcutInput.IsPressed(CommanderSettings.MapBoxSelect)))
        {
            // On the map a plain drag belongs to map panning, so a box there needs the
            // modifier. In the 3D view nothing else uses a left drag, so it stays free.
            pressed = true;
            DraggingOnMap = overMap;
            startScreenPosition = currentScreenPosition;
        }

        if (!pressed)
        {
            return false;
        }

        if (!CommanderShortcutInput.IsUp(CommanderSettings.PrimaryAction))
        {
            return false;
        }

        pressed = false;
        bool wasMap = DraggingOnMap;
        DraggingOnMap = false;
        if (!IsPastThreshold)
        {
            return false;
        }

        Rect rect = GetScreenRect();
        hits.Clear();
        if (wasMap)
        {
            CollectMapIcons(rect, hits);
        }
        else
        {
            markerService.CollectUnitsInScreenRect(rect, hits);
        }

        PreferFriendlyUnits(hits);
        selectionService.SelectUnits(hits, CommanderShortcutInput.IsPressed(CommanderSettings.AddToSelection));
        return true;
    }

    internal void Draw()
    {
        if (!Dragging || Event.current.type != EventType.Repaint)
        {
            return;
        }

        Vector2 a = CommanderUiScale.ScreenToGui(startScreenPosition);
        Vector2 b = CommanderUiScale.ScreenToGui(currentScreenPosition);
        Rect guiRect = Rect.MinMaxRect(
            Mathf.Min(a.x, b.x),
            Mathf.Min(a.y, b.y),
            Mathf.Max(a.x, b.x),
            Mathf.Max(a.y, b.y));

        Color previous = GUI.color;
        GUI.color = new Color(0.34f, 0.78f, 0.75f, 0.18f);
        GUI.DrawTexture(guiRect, CommanderUiTheme.BorderTexture);
        GUI.color = previous;
        CommanderUiTheme.DrawFrame(guiRect, 1f);
    }

    /// <summary>A box that caught anything friendly selects only the friendlies, RTS style.</summary>
    private static void PreferFriendlyUnits(List<Unit> units)
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        bool anyFriendly = false;
        for (int i = 0; i < units.Count; i++)
        {
            if (CommanderGameAccess.IsFriendlyUnit(units[i], localHq))
            {
                anyFriendly = true;
                break;
            }
        }

        if (!anyFriendly)
        {
            return;
        }

        for (int i = units.Count - 1; i >= 0; i--)
        {
            if (!CommanderGameAccess.IsFriendlyUnit(units[i], localHq))
            {
                units.RemoveAt(i);
            }
        }
    }

    private static void CollectMapIcons(Rect screenRect, List<Unit> units)
    {
        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map == null)
        {
            return;
        }

        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        IReadOnlyList<MapIcon> icons = map.mapIcons;
        for (int i = 0; i < icons.Count; i++)
        {
            if (icons[i] is not UnitMapIcon unitIcon
                || unitIcon == null
                || !unitIcon.gameObject.activeInHierarchy
                || unitIcon.unit == null)
            {
                continue;
            }

            if (!screenRect.Contains(unitIcon.transform.position))
            {
                continue;
            }

            Unit unit = CommanderSamSiteCoreRegistry.ResolveSelection(unitIcon.unit) ?? unitIcon.unit;
            if (CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq) && !units.Contains(unit))
            {
                units.Add(unit);
            }
        }
    }

    private Rect GetScreenRect()
    {
        return Rect.MinMaxRect(
            Mathf.Min(startScreenPosition.x, currentScreenPosition.x),
            Mathf.Min(startScreenPosition.y, currentScreenPosition.y),
            Mathf.Max(startScreenPosition.x, currentScreenPosition.x),
            Mathf.Max(startScreenPosition.y, currentScreenPosition.y));
    }
}
