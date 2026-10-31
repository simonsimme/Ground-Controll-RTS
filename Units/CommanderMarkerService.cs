using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GroundControlRts;

internal sealed class CommanderMarkerService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive
{
    private const float RefreshIntervalSeconds = 1f;

    private readonly CommanderSelectionService selectionService;
    private readonly Dictionary<Unit, CommanderMarkerView> markerViews = new();
    private readonly HashSet<Unit> currentUnits = new();
    private readonly List<Unit> unitsToRemove = new();
    private FactionHQ? boundHq;
    private Transform? markerRoot;
    private float nextRefreshTime;

    internal CommanderMarkerService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal static CommanderMarkerService? Instance { get; private set; }

    public void Activate()
    {
        nextRefreshTime = CommanderScheduler.StaggerRealtime("markers.bindings", RefreshIntervalSeconds, 0.35f);
        EnsureMarkerRoot();
        RefreshBindings();
        SyncExistingUnits();
    }

    public void Deactivate()
    {
        CommanderRadarExposure.Reset();
        UnbindHq();
        ClearViews();
        DestroyMarkerRoot();
    }

    public void TickActive()
    {
        EnsureMarkerRoot();

        if (!CommanderScheduler.IsDueRealtime(ref nextRefreshTime, RefreshIntervalSeconds))
        {
            UpdateMarkerViews();
            return;
        }

        RefreshBindings();
        SyncExistingUnits();
        UpdateMarkerViews();
    }

    /// <summary>Every unit whose world marker currently sits inside a screen-space rectangle.</summary>
    internal void CollectUnitsInScreenRect(Rect screenRect, List<Unit> units)
    {
        foreach (KeyValuePair<Unit, CommanderMarkerView> pair in markerViews)
        {
            if (!pair.Value.TryGetScreenPosition(out Vector2 screenPosition)
                || !screenRect.Contains(screenPosition))
            {
                continue;
            }

            Unit unit = CommanderSamSiteCoreRegistry.ResolveSelection(pair.Key) ?? pair.Key;
            if (!units.Contains(unit))
            {
                units.Add(unit);
            }
        }
    }

    /// <summary>Every friendly aircraft with a marker, on screen or not.</summary>
    internal void CopyFriendlyAircraft(List<Aircraft> aircraftList)
    {
        aircraftList.Clear();
        foreach (KeyValuePair<Unit, CommanderMarkerView> pair in markerViews)
        {
            if (pair.Value.IsFriendlyMarker() && pair.Key is Aircraft aircraft && aircraft != null)
            {
                aircraftList.Add(aircraft);
            }
        }
    }

    internal bool TryGetMarkerUnitAt(Vector2 screenPosition, out Unit unit)
    {
        unit = null!;
        float nearestDistance = float.MaxValue;

        foreach (KeyValuePair<Unit, CommanderMarkerView> pair in markerViews)
        {
            if (!pair.Value.TryHit(screenPosition, out float hitDistance))
            {
                continue;
            }

            if (hitDistance < nearestDistance)
            {
                nearestDistance = hitDistance;
                unit = CommanderSamSiteCoreRegistry.ResolveSelection(pair.Key) ?? pair.Key;
            }
        }

        return nearestDistance < float.MaxValue;
    }

    private void RefreshBindings()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (ReferenceEquals(localHq, boundHq) && boundHq != null)
        {
            return;
        }

        UnbindHq();
        ClearViews();
        boundHq = localHq;
        if (boundHq == null)
        {
            return;
        }

        boundHq.onRegisterUnit += OnRegisterUnit;
        boundHq.onRemoveUnit += OnRemoveUnit;
        boundHq.onDiscoverUnit += OnDiscoverUnit;
        boundHq.onForgetUnit += OnForgetUnit;
    }

    private void UnbindHq()
    {
        if (boundHq == null)
        {
            return;
        }

        boundHq.onRegisterUnit -= OnRegisterUnit;
        boundHq.onRemoveUnit -= OnRemoveUnit;
        boundHq.onDiscoverUnit -= OnDiscoverUnit;
        boundHq.onForgetUnit -= OnForgetUnit;
        boundHq = null;
    }

    private void SyncExistingUnits()
    {
        if (boundHq?.factionUnits == null)
        {
            return;
        }

        currentUnits.Clear();
        foreach (PersistentID unitId in boundHq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit))
            {
                continue;
            }

            currentUnits.Add(unit);
            TryCreateMarker(unit);
        }

        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in boundHq.trackingDatabase)
        {
            if (!entry.Key.TryGetUnit(out Unit unit))
            {
                continue;
            }

            currentUnits.Add(unit);
            TryCreateMarker(unit);
        }

        unitsToRemove.Clear();
        foreach (KeyValuePair<Unit, CommanderMarkerView> pair in markerViews)
        {
            if (!currentUnits.Contains(pair.Key) || !CommanderGameAccess.ShouldTrackUnit(pair.Key, boundHq))
            {
                unitsToRemove.Add(pair.Key);
            }
        }

        for (int i = 0; i < unitsToRemove.Count; i++)
        {
            RemoveMarker(unitsToRemove[i]);
        }
    }

    private void OnRegisterUnit(Unit unit)
    {
        TryCreateMarker(unit);
    }

    private void OnRemoveUnit(Unit unit)
    {
        RemoveMarker(unit);
    }

    private void OnDiscoverUnit(PersistentID unitId)
    {
        if (unitId.TryGetUnit(out Unit unit))
        {
            TryCreateMarker(unit);
        }
    }

    private void OnForgetUnit(PersistentID unitId)
    {
        if (unitId.TryGetUnit(out Unit unit))
        {
            RemoveMarker(unit);
        }
    }

    private bool TryCreateMarker(Unit? unit)
    {
        if (markerRoot == null || !CommanderGameAccess.ShouldTrackUnit(unit, boundHq) || unit == null)
        {
            return false;
        }

        if (markerViews.ContainsKey(unit))
        {
            return false;
        }

        Image? image = CommanderGameAccess.CreateMarkerImage(markerRoot);
        if (image == null)
        {
            return false;
        }

        markerViews[unit] = new CommanderMarkerView(unit, image, boundHq!);
        return true;
    }

    private void UpdateMarkerViews()
    {
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return;
        }

        unitsToRemove.Clear();
        foreach (KeyValuePair<Unit, CommanderMarkerView> pair in markerViews)
        {
            if (!CommanderGameAccess.ShouldRetainCommanderMarker(pair.Key, boundHq))
            {
                unitsToRemove.Add(pair.Key);
                continue;
            }

            Unit selectedUnit = CommanderSamSiteCoreRegistry.ResolveSelection(pair.Key) ?? pair.Key;
            pair.Value.Update(camera, selectionService.IsSelected(selectedUnit));
        }

        for (int i = 0; i < unitsToRemove.Count; i++)
        {
            RemoveMarker(unitsToRemove[i]);
        }
    }

    private void RemoveMarker(Unit unit)
    {
        if (!markerViews.TryGetValue(unit, out CommanderMarkerView view))
        {
            return;
        }

        view.Dispose();
        markerViews.Remove(unit);
    }

    private void EnsureMarkerRoot()
    {
        if (markerRoot != null)
        {
            return;
        }

        Transform? parent = CommanderGameAccess.GetMarkerParent();
        if (parent == null)
        {
            return;
        }

        GameObject rootObject = new("CommanderMarkerRoot", typeof(RectTransform));
        RectTransform rectTransform = rootObject.GetComponent<RectTransform>();
        rectTransform.SetParent(parent, false);
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        markerRoot = rectTransform;
    }

    private void DestroyMarkerRoot()
    {
        if (markerRoot == null)
        {
            return;
        }

        Object.Destroy(markerRoot.gameObject);
        markerRoot = null;
    }

    private void ClearViews()
    {
        foreach (CommanderMarkerView view in markerViews.Values)
        {
            view.Dispose();
        }

        markerViews.Clear();
    }
}
