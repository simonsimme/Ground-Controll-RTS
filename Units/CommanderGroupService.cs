using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// RTS-style control groups. Groups 1-9 are stored per session and can be recalled,
/// ordered and framed as a single formation.
/// </summary>
internal sealed class CommanderGroupService : ICommanderTickActive, ICommanderResetSession
{
    internal const int GroupCount = 9;

    private readonly CommanderSelectionService selectionService;
    private readonly List<Unit>[] groups = new List<Unit>[GroupCount + 1];

    internal CommanderGroupService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        for (int i = 0; i <= GroupCount; i++)
        {
            groups[i] = new List<Unit>();
        }

        Instance = this;
    }

    internal static CommanderGroupService? Instance { get; private set; }

    public void ResetSession()
    {
        for (int i = 0; i <= GroupCount; i++)
        {
            groups[i].Clear();
        }
    }

    public void TickActive()
    {
        Prune();
        if (CommanderSettings.GroupHotkeys && !CommanderShortcutInput.IsTyping)
        {
            HandleHotkeys();
        }
    }

    internal IReadOnlyList<Unit> GetGroup(int id)
    {
        return IsValidId(id) ? groups[id] : System.Array.Empty<Unit>();
    }

    internal int GetGroupCount(int id)
    {
        return IsValidId(id) ? groups[id].Count : 0;
    }

    /// <summary>Group number a unit belongs to, or 0 when it is ungrouped.</summary>
    internal int GetGroupOf(Unit? unit)
    {
        if (unit == null)
        {
            return 0;
        }

        for (int id = 1; id <= GroupCount; id++)
        {
            if (groups[id].Contains(unit))
            {
                return id;
            }
        }

        return 0;
    }

    internal void AssignSelection(int id)
    {
        if (!IsValidId(id))
        {
            return;
        }

        List<Unit> group = groups[id];
        group.Clear();
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (unit != null && !unit.disabled && !group.Contains(unit))
            {
                // A unit belongs to exactly one group, so recalling a group is unambiguous.
                RemoveFromAllGroups(unit);
                group.Add(unit);
            }
        }
    }

    internal void AddSelection(int id)
    {
        if (!IsValidId(id))
        {
            return;
        }

        List<Unit> group = groups[id];
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (unit != null && !unit.disabled && !group.Contains(unit))
            {
                RemoveFromAllGroups(unit);
                group.Add(unit);
            }
        }
    }

    /// <summary>Puts a single unit into a group, used when a depot reinforces one.</summary>
    internal void AddUnitToGroup(Unit? unit, int id)
    {
        if (unit == null || unit.disabled || !IsValidId(id) || groups[id].Contains(unit))
        {
            return;
        }

        RemoveFromAllGroups(unit);
        groups[id].Add(unit);
    }

    internal void SelectGroup(int id, bool additive)
    {
        if (!IsValidId(id))
        {
            return;
        }

        Prune();
        selectionService.SelectUnits(groups[id], additive);
    }

    internal void ClearGroup(int id)
    {
        if (IsValidId(id))
        {
            groups[id].Clear();
        }
    }

    private static bool IsValidId(int id)
    {
        return id is >= 1 and <= GroupCount;
    }

    private void HandleHotkeys()
    {
        for (int id = 1; id <= GroupCount; id++)
        {
            if (!Input.GetKeyDown(KeyCode.Alpha0 + id))
            {
                continue;
            }

            if (CommanderShortcutInput.IsPressed(CommanderSettings.AssignGroupModifier))
            {
                AssignSelection(id);
            }
            else
            {
                SelectGroup(id, CommanderShortcutInput.IsPressed(CommanderSettings.AddToSelection));
            }

            return;
        }
    }

    private void Prune()
    {
        for (int id = 1; id <= GroupCount; id++)
        {
            List<Unit> group = groups[id];
            for (int i = group.Count - 1; i >= 0; i--)
            {
                if (group[i] == null || group[i].disabled)
                {
                    group.RemoveAt(i);
                }
            }
        }
    }

    private void RemoveFromAllGroups(Unit unit)
    {
        for (int id = 1; id <= GroupCount; id++)
        {
            groups[id].Remove(unit);
        }
    }
}
