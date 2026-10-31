using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    private static readonly Color StationAreaColor = new(0.2f, 0.85f, 1f, 0.34f);
    private static readonly Color TargetAreaColor = new(1f, 0.34f, 0.1f, 0.38f);
    private GameObject? pendingAreaPreview;

    internal static GameObject? EnsureMapRadiusVisual(
        GameObject? visual,
        DynamicMap map,
        GlobalPosition position,
        float radius,
        Color color,
        string name)
    {
        GameObject? prefab = GameAssets.i?.exclusionZoneDisplay;
        if (map.iconLayer == null || prefab == null)
        {
            return visual;
        }

        if (visual == null)
        {
            visual = Object.Instantiate(prefab, map.iconLayer.transform);
            visual.name = name;
            Image[] images = visual.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                images[i].color = color;
                images[i].raycastTarget = false;
            }
        }

        visual.transform.localPosition = new Vector3(position.x, position.z, 0f) * map.mapDisplayFactor;
        visual.transform.localScale = Vector3.one * (radius * map.mapDisplayFactor);
        return visual;
    }

    private static void EnsureMissionMapVisual(AirMission mission)
    {
        // An escort's circle sits on a unit that is moving, so it is re-placed on every sweep
        // instead of being drawn once and left behind.
        if (mission.MapVisual != null && mission.EscortTarget == null)
        {
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map != null)
        {
            mission.MapVisual = EnsureMapRadiusVisual(
                mission.MapVisual,
                map,
                mission.AreaCenter,
                mission.Radius,
                GetMissionAreaColor(mission.Mode),
                "RTS Air Mission Area");
        }
    }

    private static Color GetMissionAreaColor(AirCommandMode mode)
    {
        return KeepsStationInMissionArea(mode) ? StationAreaColor : TargetAreaColor;
    }

    private static void DestroyMissionMapVisual(AirMission mission)
    {
        if (mission.MapVisual != null)
        {
            Object.Destroy(mission.MapVisual);
            mission.MapVisual = null;
        }
    }

    private void UpdatePendingAreaPreview()
    {
        AirCommandMode mode;
        float radius;
        if (pendingAreaSelection != null)
        {
            mode = pendingAreaSelection.Option.Mode;
            radius = GetMissionRadius(mode);
        }
        else if (pendingMissionRelocation != null
            && missions.TryGetValue(pendingMissionRelocation, out AirMission relocationMission))
        {
            mode = relocationMission.Mode;
            radius = relocationMission.Radius;
        }
        else
        {
            DestroyPendingAreaPreview();
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map == null || !map.TryGetCursorCoordinates(out GlobalPosition position))
        {
            if (pendingAreaPreview != null)
            {
                pendingAreaPreview.SetActive(false);
            }

            return;
        }

        pendingAreaPreview = EnsureMapRadiusVisual(
            pendingAreaPreview,
            map,
            position,
            radius,
            GetMissionAreaColor(mode),
            "RTS Air Mission Preview");
        if (pendingAreaPreview != null)
        {
            pendingAreaPreview.SetActive(true);
        }
    }

    private void RefreshMissionMapVisuals()
    {
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (ReferenceEquals(selectedMissionAircraft, entry.Key))
            {
                EnsureMissionMapVisual(entry.Value);
            }
            else
            {
                DestroyMissionMapVisual(entry.Value);
            }
        }
    }

    private void RemoveMission(Aircraft? aircraft)
    {
        if (ReferenceEquals(aircraft, null) || !missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        DestroyMissionMapVisual(mission);
        missions.Remove(aircraft);
        if (ReferenceEquals(selectedMissionAircraft, aircraft))
        {
            selectedMissionAircraft = null;
        }

        if (ReferenceEquals(pendingMissionRelocation, aircraft))
        {
            pendingMissionRelocation = null;
            DestroyPendingAreaPreview();
            tacticalMapService.SuppressMapFollow = false;
        }
    }

    private void ClearMissionMapVisuals()
    {
        foreach (AirMission mission in missions.Values)
        {
            DestroyMissionMapVisual(mission);
        }

        DestroyPendingAreaPreview();
    }

    private void DestroyPendingAreaPreview()
    {
        if (pendingAreaPreview != null)
        {
            Object.Destroy(pendingAreaPreview);
            pendingAreaPreview = null;
        }
    }
}
