using System.Collections.Generic;

namespace GroundControlRts;

internal static class CommanderSamSiteCoreRegistry
{
    private static readonly HashSet<Unit> Cores = new();
    private static readonly HashSet<Unit> TrackedSiteUnits = new();
    private static readonly Dictionary<Unit, Unit> VisualToCore = new();

    internal static bool IsCore(Unit? unit)
    {
        return unit != null && Cores.Contains(unit);
    }

    internal static bool IsTrackedSiteUnit(Unit? unit)
    {
        return unit != null && TrackedSiteUnits.Contains(unit);
    }

    internal static Unit? ResolveSelection(Unit? unit)
    {
        return unit != null && VisualToCore.TryGetValue(unit, out Unit core)
            ? core
            : unit;
    }

    internal static void Register(Unit core, Unit? visual)
    {
        Cores.Add(core);
        TrackedSiteUnits.Add(core);
        if (visual != null)
        {
            TrackedSiteUnits.Add(visual);
            VisualToCore[visual] = core;
        }
    }

    internal static void RegisterTracked(Unit? unit)
    {
        if (unit != null)
        {
            TrackedSiteUnits.Add(unit);
        }
    }

    internal static void MapVisualToCore(Unit? visual, Unit? core)
    {
        if (visual == null || core == null)
        {
            return;
        }

        TrackedSiteUnits.Add(visual);
        VisualToCore[visual] = core;
    }

    internal static void Unregister(Unit? unit)
    {
        if (unit == null)
        {
            return;
        }

        Cores.Remove(unit);
        TrackedSiteUnits.Remove(unit);
        List<Unit> visuals = new();
        foreach (KeyValuePair<Unit, Unit> entry in VisualToCore)
        {
            if (entry.Key == null || entry.Value == null || ReferenceEquals(entry.Key, unit) || ReferenceEquals(entry.Value, unit))
            {
                if (!ReferenceEquals(entry.Key, null))
                {
                    visuals.Add(entry.Key);
                }
            }
        }

        for (int i = 0; i < visuals.Count; i++)
        {
            VisualToCore.Remove(visuals[i]);
        }
    }

    internal static void Clear()
    {
        Cores.Clear();
        TrackedSiteUnits.Clear();
        VisualToCore.Clear();
    }
}
