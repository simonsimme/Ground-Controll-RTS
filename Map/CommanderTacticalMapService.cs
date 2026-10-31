using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace GroundControlRts;

internal sealed class CommanderTacticalMapService : ICommanderTickActive, ICommanderResetSession
{
    private const float MinimumMapSize = 260f;
    private const float TacticalMapMargin = 12f;

    /// <summary>The map opens under the command bar, not behind it.</summary>
    private const float TacticalMapTop = 52f;
    private const float ResizeGripSize = 18f;
    private const float HeaderHeight = 30f;

    private static readonly MethodInfo? JumpCameraToMethod = AccessTools.Method(typeof(DynamicMap), "JumpCameraTo");

    private readonly CommanderCameraFollowService cameraFollowService;
    private readonly CommanderMapClickTracker cameraJumpTracker = new();
    private readonly Vector3[] mapCorners = new Vector3[4];
    private DynamicMap? activeMap;
    private bool tacticalOpen;
    private bool positionInitialized;
    private bool dragging;
    private bool resizing;
    private bool helpVisible;
    private float mapSize = Mathf.Clamp(CommanderSettings.TacticalMapSize, MinimumMapSize, 2000f);
    private Vector2 dragOffset;
    private Rect mapWindowRect;
    private Rect gripRect;
    private GameObject? hiddenVirtualMfd;
    private bool virtualMfdWasActive;
    private bool restoreTacticalAfterFullscreen;
    private int suppressExtraUiFrame = -1;
    private float lastAppliedUiScale = -1f;
    private Vector2 lastAppliedMapPosition = new(float.NaN, float.NaN);
    private RawImage? coverageMapLayer;
    private Image? coverageOriginMarker;
    private GameObject? coverageLayerObject;

    internal CommanderTacticalMapService(CommanderCameraFollowService cameraFollowService)
    {
        this.cameraFollowService = cameraFollowService;
        Instance = this;
    }

    internal static CommanderTacticalMapService? Instance { get; private set; }

    internal static bool AllowCommanderMapJump { get; private set; }

    internal bool IsOpen => tacticalOpen && DynamicMap.mapMaximized;

    internal bool IsFullscreenOpen => !tacticalOpen && DynamicMap.mapMaximized;

    internal bool SuppressMapFollow { get; set; }

    internal bool SuppressExtraUiThisFrame => suppressExtraUiFrame == Time.frameCount;

    private static float MaximumMapSize => Mathf.Max(
        MinimumMapSize,
        Mathf.Min(CommanderUiScale.Width - (TacticalMapMargin * 2f), CommanderUiScale.Height - (TacticalMapMargin * 2f) - HeaderHeight));

    public void TickActive()
    {
        if (restoreTacticalAfterFullscreen && IsFullscreenOpen && CommanderGameInput.CancelDown)
        {
            suppressExtraUiFrame = Time.frameCount;
            RestoreTacticalMap();
            return;
        }

        if (restoreTacticalAfterFullscreen && !DynamicMap.mapMaximized)
        {
            restoreTacticalAfterFullscreen = false;
        }

        if (!tacticalOpen)
        {
            return;
        }

        if (!DynamicMap.mapMaximized || activeMap == null)
        {
            tacticalOpen = false;
            dragging = false;
            SuppressMapFollow = false;
            activeMap = null;
            cameraJumpTracker.Reset();
            return;
        }

        mapSize = Mathf.Clamp(mapSize, MinimumMapSize, MaximumMapSize);
        mapWindowRect.width = mapSize;
        mapWindowRect.height = mapSize + HeaderHeight;
        mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
        if (!Mathf.Approximately(lastAppliedUiScale, CommanderUiScale.Scale)
            || lastAppliedMapPosition != mapWindowRect.position)
        {
            ApplyLayout();
        }

        HideFullMapPanels();
        SyncCoverageLayer();

        // A click on a unit icon is a selection, not a request to move the 3D camera there.
        // Nor is a click that placed something: the press disarms the placement, so the release
        // would otherwise read as a plain click on the map.
        bool jump = cameraJumpTracker.Tick(activeMap, out GlobalPosition position);
        if (CommanderInputController.PlacementActive)
        {
            cameraJumpTracker.Reset();
        }
        else if (!SuppressMapFollow
            && jump
            && CommanderTacticalMapPatches.CommanderTacticalMapControlsPatch.FindIconUnderCursor(activeMap) == null)
        {
            JumpCameraTo(position);
        }
    }

    public void ResetSession()
    {
        RestoreScale(activeMap);
        RestoreVirtualMfd();
        tacticalOpen = false;
        dragging = false;
        helpVisible = false;
        SuppressMapFollow = false;
        activeMap = null;
        cameraJumpTracker.Reset();
        restoreTacticalAfterFullscreen = false;
        suppressExtraUiFrame = -1;
        lastAppliedUiScale = -1f;
        lastAppliedMapPosition = new Vector2(float.NaN, float.NaN);
        DestroyCoverageLayer();
    }

    /// <summary>
    /// How far the cursor is from a map icon's centre, squared, in screen pixels, or infinity when
    /// the cursor is not on the icon's own glyph.
    /// </summary>
    /// <remarks>
    /// The mod replaces <c>DynamicMap.MapControls</c>, so it picks icons itself. It measures to the
    /// visible <c>iconImage</c> rather than the icon's root transform, and accounts for glyph size, so
    /// large icons are clickable across their whole glyph. Both callers (the map's own click and the
    /// order path's hover) share this so they agree on what is under the cursor.
    /// <para>
    /// Only a cursor on the glyph counts. Among overlapping glyphs the nearest centre wins.
    /// </para>
    /// </remarks>
    internal static float IconPickDistanceSq(MapIcon icon, Vector2 screenPoint)
    {
        Image? image = icon.iconImage;
        if (image == null
            || !RectTransformUtility.RectangleContainsScreenPoint(image.rectTransform, screenPoint, null))
        {
            return float.PositiveInfinity;
        }

        return ((Vector2)image.transform.position - screenPoint).sqrMagnitude;
    }

    /// <summary>
    /// The map window's own chrome has the cursor: the header is being dragged, the corner grip is
    /// being pulled, or the cursor is on that grip. The grip sits inside the map rectangle, so
    /// without this the map's own drag-to-pan also ran while the player resized it.
    /// </summary>
    internal bool OwnsPointer(Vector2 screenPoint)
    {
        return IsOpen && (dragging || resizing || gripRect.Contains(CommanderUiScale.ScreenToGui(screenPoint)));
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        if (IsOpen)
        {
            return mapWindowRect.Contains(guiPoint);
        }

        return !DynamicMap.mapMaximized
            && new Rect(CommanderUiScale.Width - 86f, TacticalMapMargin, 74f, 38f).Contains(guiPoint);
    }

    internal void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    internal bool HandleMapKey()
    {
        if (IsOpen)
        {
            bool opened = OpenFullscreen();
            restoreTacticalAfterFullscreen = opened;
            return opened;
        }

        if (restoreTacticalAfterFullscreen && IsFullscreenOpen)
        {
            RestoreTacticalMap();
            return true;
        }

        return false;
    }

    internal bool Open()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return false;
        }

        DynamicMap.AllowedToOpen = true;
        if (!DynamicMap.mapMaximized)
        {
            dynamicMap.Maximize();
        }

        activeMap = dynamicMap;
        tacticalOpen = true;
        helpVisible = false;
        cameraJumpTracker.Reset();
        EnsureInitialPosition();
        ApplyLayout();
        HideFullMapPanels();
        SyncCoverageLayer();
        return true;
    }

    /// <summary>
    /// Opens the mod's own map for a placement click — an air mission area, a naval rally point —
    /// and answers whether this call is the one that opened it, so the caller knows whether to put
    /// it away again. The compact RTS map is the only map the mod ever asks the player to work on:
    /// swapping to the fullscreen game map mid-order and back was the whole complaint.
    /// </summary>
    internal bool OpenForPlacement()
    {
        return !IsOpen && Open();
    }

    internal bool OpenFullscreen()
    {
        if (IsOpen)
        {
            Close();
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return false;
        }

        RestoreVirtualMfd();
        RestoreScale(dynamicMap);
        DynamicMap.AllowedToOpen = true;
        if (!DynamicMap.mapMaximized)
        {
            dynamicMap.Maximize();
        }

        activeMap = dynamicMap;
        tacticalOpen = false;
        return DynamicMap.mapMaximized;
    }

    internal bool ShowCoverageFullscreen()
    {
        if (!OpenFullscreen())
        {
            return false;
        }

        SyncCoverageLayer();
        return true;
    }

    internal void HideCoverageLayer()
    {
        if (coverageLayerObject != null)
        {
            coverageLayerObject.SetActive(false);
        }
    }

    internal void Close()
    {
        DynamicMap? dynamicMap = activeMap ?? SceneSingleton<DynamicMap>.i;
        RestoreScale(dynamicMap);
        RestoreVirtualMfd();
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }

        tacticalOpen = false;
        dragging = false;
        resizing = false;
        helpVisible = false;
        SuppressMapFollow = false;
        activeMap = null;
        cameraJumpTracker.Reset();
        restoreTacticalAfterFullscreen = false;
        HideCoverageLayer();
    }

    internal void CloseFullscreen()
    {
        DynamicMap? dynamicMap = activeMap ?? SceneSingleton<DynamicMap>.i;
        RestoreScale(dynamicMap);
        RestoreVirtualMfd();
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }

        tacticalOpen = false;
        SuppressMapFollow = false;
        activeMap = null;
        HideCoverageLayer();
    }

    internal void DrawControls()
    {
        CommanderUiTheme.Ensure();
        if (!IsOpen)
        {
            if (!DynamicMap.mapMaximized)
            {
                Rect launcher = new(CommanderUiScale.Width - 86f, TacticalMapMargin, 74f, 38f);
                if (GUI.Button(launcher, "MAP", CommanderUiTheme.Button))
                {
                    Open();
                }
            }

            return;
        }

        Rect header = new(mapWindowRect.x, mapWindowRect.y, mapWindowRect.width, HeaderHeight);
        GUI.Box(header, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawAccentLine(new Rect(header.x, header.y, header.width, 2f));
        GUI.Label(new Rect(header.x + 12f, header.y + 3f, 60f, 24f), "MAP", CommanderUiTheme.Header);
        bool oldEnabled = GUI.enabled;

        // Radar warning rings round every aircraft you command, on this map (CommanderRadarExposure).
        bool radarRings = CommanderSettings.RadarExposureRings;
        if (GUI.Button(
            new Rect(header.x + 60f, header.y + 4f, 80f, 22f),
            "RADAR",
            radarRings ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            CommanderSettings.RadarExposureRings = !radarRings;
        }

        // Every route you own, not just the selection's (CommanderMapRouteRenderer).
        bool allRoutes = CommanderSettings.MapAllRoutes;
        if (GUI.Button(
            new Rect(header.x + 142f, header.y + 4f, 80f, 22f),
            "ROUTES",
            allRoutes ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            CommanderSettings.MapAllRoutes = !allRoutes;
        }

        Rect cameraGroup = new(header.xMax - 384f, header.y + 2f, 316f, 26f);
        GUI.Label(new Rect(cameraGroup.x, cameraGroup.y + 1f, 50f, 24f), "CAMERA", CommanderUiTheme.Caption);
        GUI.enabled = oldEnabled && cameraFollowService.CanFollow;
        if (GUI.Button(
            new Rect(cameraGroup.x + 52f, cameraGroup.y + 2f, 80f, 22f),
            "FOLLOW",
            cameraFollowService.Enabled ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.Toggle();
        }

        if (GUI.Button(new Rect(cameraGroup.x + 134f, cameraGroup.y + 2f, 80f, 22f), "CENTER", CommanderUiTheme.Button))
        {
            cameraFollowService.CenterOnSelection();
        }

        if (GUI.Button(
            new Rect(cameraGroup.x + 216f, cameraGroup.y + 2f, 80f, 22f),
            "POV",
            cameraFollowService.PovMode ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.TogglePov();
        }

        GUI.enabled = oldEnabled;
        if (GUI.Button(new Rect(header.xMax - 60f, header.y + 3f, 26f, 24f), "?", CommanderUiTheme.HelpButton))
        {
            helpVisible = !helpVisible;
        }

        if (GUI.Button(new Rect(header.xMax - 30f, header.y + 3f, 26f, 24f), "X", CommanderUiTheme.ChromeButton))
        {
            Close();
            return;
        }

        Rect mapRect = GetMapGuiRect();
        CommanderUiTheme.DrawOutline(new Rect(mapRect.x - 1f, mapRect.y - 1f, mapRect.width + 2f, mapRect.height + 2f));
        if (helpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(mapRect.x + 10f, mapRect.y + 10f, mapRect.width - 20f, 82f),
                "LMB clicks icons; drag LMB to pan the map, or hold the map box-select key and drag for a selection box. RMB sets a travel point, and RMB on a hostile icon makes it an attack order. Hold the queue key to chain points into a route the unit drives in order. Drag the bottom-right corner to resize this map. FOLLOW tracks position, CENTER jumps once, POV attaches to the unit. M opens the fullscreen map.");
        }

        HandleDrag(new Rect(header.x, header.y, header.width - 66f, header.height));
        gripRect = new Rect(mapRect.xMax - ResizeGripSize, mapRect.yMax - ResizeGripSize, ResizeGripSize, ResizeGripSize);
        HandleResize(gripRect);
    }

    internal void ResetLayoutPosition()
    {
        positionInitialized = false;
        if (IsOpen)
        {
            EnsureInitialPosition();
            ApplyLayout();
        }
    }

    internal bool JumpCameraToPosition(GlobalPosition position)
    {
        DynamicMap? dynamicMap = activeMap ?? SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null || JumpCameraToMethod == null)
        {
            return false;
        }

        Transform camera = SceneSingleton<CameraStateManager>.i.transform;
        float heightBefore = camera.position.y
            - CommanderCameraPatches.CommanderFreeCameraInputPatch.GroundHeightBelow(camera.position);
        AllowCommanderMapJump = true;
        try
        {
            JumpCameraToMethod.Invoke(dynamicMap, new object[] { position });

            // The base jump leaves the camera at a fixed height looking 30 degrees down. Back it
            // off along that same line until it sits at the player's height, so the clicked
            // point stays in the middle of the screen.
            float height = CommanderSettings.MapJumpKeepHeight ? heightBefore : CommanderSettings.MapJumpHeight;
            Vector3 focus = position.ToLocalPosition();
            focus.y = CommanderCameraPatches.CommanderFreeCameraInputPatch.GroundHeightBelow(focus);
            camera.position = focus - (camera.forward * (Mathf.Max(height, 20f) / Mathf.Max(-camera.forward.y, 0.1f)));
            return true;
        }
        finally
        {
            AllowCommanderMapJump = false;
        }
    }

    /// <summary>Projects a world coordinate onto the map canvas in screen pixels.</summary>
    internal bool TryWorldToMapScreen(GlobalPosition position, out Vector2 screenPosition)
    {
        screenPosition = default;
        DynamicMap? map = activeMap ?? SceneSingleton<DynamicMap>.i;
        if (map == null || !DynamicMap.mapMaximized || map.mapImage == null || map.mapDimension <= 0f)
        {
            return false;
        }

        // Inverse of DynamicMap.GetCursorCoordinates.
        float pixelsPerMeter = 900f * map.mapImage.transform.lossyScale.x / map.mapDimension;
        Vector3 origin = map.mapImage.transform.position;
        Vector3 offset = position.AsVector3();
        screenPosition = new Vector2(
            origin.x + (offset.x * pixelsPerMeter),
            origin.y + (offset.z * pixelsPerMeter));
        return RectTransformUtility.RectangleContainsScreenPoint(map.mapBackground.rectTransform, screenPosition, null);
    }

    private static void RestoreScale(DynamicMap? dynamicMap)
    {
        if (dynamicMap != null)
        {
            dynamicMap.GetComponent<RectTransform>().localScale = Vector3.one;
        }
    }

    private static void HideFullMapPanels()
    {
        GameplayUI? gameplayUi = SceneSingleton<GameplayUI>.i;
        gameplayUi?.HideSelectAirbase();
        gameplayUi?.HideSpectatorPanel();
        Instance?.HideVirtualMfd();
    }

    private void RestoreTacticalMap()
    {
        restoreTacticalAfterFullscreen = false;
        CloseFullscreen();
        Open();
    }

    private void EnsureInitialPosition()
    {
        if (positionInitialized)
        {
            return;
        }

        mapSize = Mathf.Clamp(mapSize, MinimumMapSize, MaximumMapSize);
        mapWindowRect = new Rect(
            CommanderUiScale.Width - TacticalMapMargin - mapSize,
            TacticalMapTop,
            mapSize,
            mapSize + HeaderHeight);
        positionInitialized = true;
    }

    private Rect GetMapGuiRect()
    {
        return new Rect(mapWindowRect.x, mapWindowRect.y + HeaderHeight, mapSize, mapSize);
    }

    private void SyncCoverageLayer()
    {
        CommanderSamSiteAnalyzerService? analyzer = CommanderSamSiteAnalyzerService.Instance;
        Texture2D? texture = analyzer?.CoverageOverlayTexture;
        if (activeMap == null
            || analyzer?.CoverageOverlayEnabled != true
            || texture == null)
        {
            HideCoverageLayer();
            return;
        }

        EnsureCoverageLayer(activeMap);
        if (coverageMapLayer == null || coverageOriginMarker == null || coverageLayerObject == null)
        {
            return;
        }

        bool textureChanged = coverageMapLayer.texture != texture;
        if (textureChanged)
        {
            coverageMapLayer.texture = texture;
        }

        GlobalPosition origin = analyzer.CoverageOverlayOrigin;
        Vector2 mapSize = NetworkSceneSingleton<LevelInfo>.i?.LoadedMapSettings?.MapSize
            ?? new Vector2(81920f, 81920f);
        Vector2 anchor = new(
            Mathf.Clamp01((origin.x / Mathf.Max(mapSize.x, 1f)) + 0.5f),
            Mathf.Clamp01((origin.z / Mathf.Max(mapSize.y, 1f)) + 0.5f));
        RectTransform markerTransform = coverageOriginMarker.rectTransform;
        markerTransform.anchorMin = anchor;
        markerTransform.anchorMax = anchor;
        markerTransform.anchoredPosition = Vector2.zero;
        bool wasHidden = !coverageLayerObject.activeSelf;
        coverageLayerObject.SetActive(true);
        if (textureChanged || wasHidden)
        {
            coverageMapLayer.SetAllDirty();
        }
    }

    private void EnsureCoverageLayer(DynamicMap dynamicMap)
    {
        Transform parent = dynamicMap.mapImage.transform;
        if (coverageLayerObject != null && coverageLayerObject.transform.parent == parent)
        {
            return;
        }

        DestroyCoverageLayer();
        coverageLayerObject = new GameObject(
            "GroundControl SAM Coverage",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(RawImage));
        RectTransform layerTransform = coverageLayerObject.GetComponent<RectTransform>();
        layerTransform.SetParent(parent, false);
        layerTransform.anchorMin = Vector2.zero;
        layerTransform.anchorMax = Vector2.one;
        layerTransform.offsetMin = Vector2.zero;
        layerTransform.offsetMax = Vector2.zero;
        layerTransform.localRotation = Quaternion.identity;
        layerTransform.localScale = Vector3.one;
        coverageMapLayer = coverageLayerObject.GetComponent<RawImage>();
        coverageMapLayer.raycastTarget = false;
        coverageMapLayer.color = Color.white;

        GameObject markerObject = new(
            "GroundControl SAM Coverage Origin",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        RectTransform markerTransform = markerObject.GetComponent<RectTransform>();
        markerTransform.SetParent(layerTransform, false);
        markerTransform.sizeDelta = new Vector2(6f, 6f);
        coverageOriginMarker = markerObject.GetComponent<Image>();
        coverageOriginMarker.raycastTarget = false;
        coverageOriginMarker.color = new Color(0.15f, 0.9f, 1f, 0.95f);
    }

    private void DestroyCoverageLayer()
    {
        if (coverageLayerObject != null)
        {
            Object.Destroy(coverageLayerObject);
        }

        coverageLayerObject = null;
        coverageMapLayer = null;
        coverageOriginMarker = null;
    }

    private void ApplyLayout()
    {
        if (activeMap == null)
        {
            return;
        }

        Rect mapRect = GetMapGuiRect();
        RectTransform rectTransform = activeMap.GetComponent<RectTransform>();
        rectTransform.localScale = Vector3.one;
        RectTransform measuredTransform = activeMap.mapBackground != null
            ? activeMap.mapBackground.rectTransform
            : rectTransform;
        measuredTransform.GetWorldCorners(mapCorners);
        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(null, mapCorners[0]);
        Vector2 bottomRight = RectTransformUtility.WorldToScreenPoint(null, mapCorners[3]);
        float sourcePixelSize = Mathf.Max(Vector2.Distance(bottomLeft, bottomRight), 1f);
        float scale = mapSize * CommanderUiScale.Scale / sourcePixelSize;
        rectTransform.localScale = Vector3.one * scale;
        Vector2 screenCenter = CommanderUiScale.GuiToScreen(mapRect.center);
        rectTransform.position = new Vector3(
            screenCenter.x,
            screenCenter.y,
            rectTransform.position.z);
        lastAppliedUiScale = CommanderUiScale.Scale;
        lastAppliedMapPosition = mapWindowRect.position;
    }

    private void HandleDrag(Rect dragRect)
    {
        Event current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0 && dragRect.Contains(current.mousePosition))
        {
            dragging = true;
            dragOffset = current.mousePosition - mapWindowRect.position;
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && dragging)
        {
            mapWindowRect.position = current.mousePosition - dragOffset;
            mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
            ApplyLayout();
            current.Use();
        }
        else if (dragging && !Input.GetMouseButton(0))
        {
            dragging = false;
        }
    }

    /// <summary>Corner grip that scales the compact tactical map.</summary>
    private void HandleResize(Rect gripRect)
    {
        GUI.Box(gripRect, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawFrame(gripRect, 1f);

        Event current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0 && gripRect.Contains(current.mousePosition))
        {
            resizing = true;
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && resizing)
        {
            float wanted = Mathf.Max(
                current.mousePosition.x - mapWindowRect.x,
                current.mousePosition.y - mapWindowRect.y - HeaderHeight);
            mapSize = Mathf.Clamp(wanted, MinimumMapSize, MaximumMapSize);
            mapWindowRect.width = mapSize;
            mapWindowRect.height = mapSize + HeaderHeight;
            mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
            ApplyLayout();
            current.Use();
        }
        else if (resizing && !Input.GetMouseButton(0))
        {
            // Also when the release went to another control and this never saw a MouseUp.
            resizing = false;
            CommanderSettings.TacticalMapSize = mapSize;
        }
    }

    private void JumpCameraTo(GlobalPosition position)
    {
        JumpCameraToPosition(position);
    }

    private void HideVirtualMfd()
    {
        GameObject? virtualMfd = SceneSingleton<MapOptions>.i?.screen?.virtualMFD?.gameObject;
        if (virtualMfd == null)
        {
            return;
        }

        if (hiddenVirtualMfd != virtualMfd)
        {
            RestoreVirtualMfd();
            hiddenVirtualMfd = virtualMfd;
            virtualMfdWasActive = virtualMfd.activeSelf;
        }

        virtualMfd.SetActive(false);
    }

    private void RestoreVirtualMfd()
    {
        if (hiddenVirtualMfd != null)
        {
            hiddenVirtualMfd.SetActive(virtualMfdWasActive);
        }

        hiddenVirtualMfd = null;
        virtualMfdWasActive = false;
    }
}
