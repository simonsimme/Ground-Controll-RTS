using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderCameraFollowService : ICommanderDeactivate, ICommanderTickActive, ICommanderResetSession
{
    private const float PovMouseLookScale = 2f;
    private const float PovNearClipPlane = 0.05f;
    private const int BookmarkCount = 4;
    private const float GlideSmoothing = 0.12f;
    private const float GlideTimeoutSeconds = 1.5f;
    private static readonly FieldInfo? FreeCameraPanField = AccessTools.Field(typeof(CameraFreeState), "panView");
    private static readonly FieldInfo? FreeCameraTiltField = AccessTools.Field(typeof(CameraFreeState), "tiltView");

    private readonly CommanderSelectionService selectionService;
    private readonly List<PovCrewSeat> povCrewSeats = new();
    private readonly CameraBookmark[] bookmarks = new CameraBookmark[BookmarkCount];
    private Unit? target;
    private Vector3 lastGlobalPosition;
    private Vector3 povLocalPosition;
    private Quaternion povLocalRotation;
    private Aircraft? povEffectsAircraft;
    private Vector3 povInertiaPosition;
    private Vector3 povInertiaVelocity;
    private Vector3 povPreviousVelocity;
    private float povAntiSlump;
    private float povPreviousGForce;
    private float povLowFrequencyShake;
    private float povHighFrequencyShake;
    private Aircraft? povCrewAircraft;
    private int povCrewTurretCount = -1;
    private Camera? povClipCamera;
    private float povPreviousNearClipPlane;
    private float spacePressedAt;
    private bool spaceHeld;
    private bool longSpaceTriggered;
    private int lastSelectionRevision = -1;
    private Vector3 followAnchor;
    private Vector3 followVelocity;
    private Vector3 glideViewDirection = Vector3.forward;
    private float glideElapsed;
    private bool gliding;

    internal CommanderCameraFollowService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal static CommanderCameraFollowService? Instance { get; private set; }

    internal static bool IsPovActive => Instance?.Enabled == true && Instance.PovMode;

    internal bool Enabled { get; private set; }

    internal bool PovMode { get; private set; }

    internal bool CanFollow => selectionService.FocusedSelection is Unit unit && !unit.disabled;

    internal Aircraft? FollowedAircraft => Enabled ? target as Aircraft : null;

    internal int PovCrewIndex { get; private set; } = -1;

    internal IReadOnlyList<PovCrewSeat> PovCrewSeats
    {
        get
        {
            RefreshPovCrewSeats();
            return povCrewSeats;
        }
    }

    public void TickActive()
    {
        HandleSpaceShortcut();
        HandleCameraBookmarks();
        if (lastSelectionRevision != selectionService.SelectionRevision)
        {
            lastSelectionRevision = selectionService.SelectionRevision;
            HandleSelectionChanged();
        }

        if (!Enabled)
        {
            return;
        }

        Unit? selected = selectionService.FocusedSelection;
        if (selected == null || selected.disabled)
        {
            Deactivate();
            return;
        }

        Vector3 currentGlobalPosition = GetFollowAnchor(selected);
        if (!ReferenceEquals(selected, target))
        {
            target = selected;
            PovCrewIndex = -1;
            BindPovEffectsAircraft(PovMode ? selected as Aircraft : null);
            lastGlobalPosition = currentGlobalPosition;
            followAnchor = currentGlobalPosition;
            followVelocity = Vector3.zero;
            CapturePovOffset();
            TryMoveToFirstCrewPosition();
            return;
        }

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (cameraManager != null)
        {
            if (PovMode)
            {
                cameraManager.transform.position = selected.transform.TransformPoint(povLocalPosition);
                cameraManager.transform.rotation = selected.transform.rotation * povLocalRotation;
                SyncFreeCameraAngles(cameraManager);
            }
            else if (gliding)
            {
                TickAutoFrameGlide(cameraManager, currentGlobalPosition);
            }
            else
            {
                // The camera tracks a damped anchor rather than copying the unit's exact movement
                // frame by frame, so an aircraft's jitter no longer arrives as camera shake.
                Vector3 desired = currentGlobalPosition + LeadOffset(currentGlobalPosition);
                Vector3 next = Vector3.Lerp(
                    followAnchor,
                    desired,
                    CommanderCameraTuning.SmoothBlend(CommanderSettings.FollowSmoothing, Time.unscaledDeltaTime));
                cameraManager.transform.position += next - followAnchor;
                followAnchor = next;
            }
        }

        lastGlobalPosition = currentGlobalPosition;
    }

    public void Deactivate()
    {
        ExitPovMode();
        Enabled = false;
        gliding = false;
        followVelocity = Vector3.zero;
        target = null;
    }

    /// <summary>Bookmarks are world positions, so they are meaningless in the next mission.</summary>
    public void ResetSession()
    {
        for (int i = 0; i < bookmarks.Length; i++)
        {
            bookmarks[i] = default;
        }
    }

    internal static void ApplyCommanderLatePose(CameraStateManager cameraManager)
    {
        CommanderCameraFollowService? service = Instance;
        Unit? selected = service?.selectionService.FocusedSelection;
        if (service == null || !service.Enabled || selected == null || selected.disabled)
        {
            return;
        }

        if (!service.PovMode)
        {
            return;
        }

        service.EnsurePovNearClip();

        // Keep FreeCam translation, but rebuild rotation from the complete unit pose.
        // CameraFreeState always produces a zero-roll Euler rotation, which otherwise
        // slowly removes aircraft bank from an attached POV.
        Vector3 movedPosition = cameraManager.transform.position;
        service.povLocalPosition = selected.transform.InverseTransformPoint(movedPosition);
        service.IntegratePovInertiaPosition();
        cameraManager.transform.position = movedPosition
            + (service.povInertiaPosition * PlayerSettings.cockpitCamInertia)
            + service.GetPovCameraShake();
        service.ApplyPovLookInput(cameraManager);
        cameraManager.transform.rotation = selected.transform.rotation * service.povLocalRotation;
        SyncFreeCameraAngles(cameraManager);
    }

    internal void Toggle()
    {
        if (Enabled)
        {
            Deactivate();
            return;
        }

        Unit? selected = selectionService.FocusedSelection;
        if (selected == null || selected.disabled)
        {
            return;
        }

        target = selected;
        followAnchor = GetFollowAnchor(selected);
        lastGlobalPosition = followAnchor;
        followVelocity = Vector3.zero;
        gliding = false;
        Enabled = true;
    }

    internal void TogglePov()
    {
        if (!CanFollow)
        {
            return;
        }

        if (!Enabled)
        {
            Toggle();
        }

        if (PovMode)
        {
            ExitPovMode();
            return;
        }

        PovMode = true;
        PovCrewIndex = -1;
        BindPovEffectsAircraft(target as Aircraft);
        CapturePovOffset();
        TryMoveToFirstCrewPosition();
        EnsurePovNearClip();
    }

    internal bool TryMoveToCrewPosition(int crewIndex)
    {
        Aircraft? aircraft = FollowedAircraft;
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        IReadOnlyList<PovCrewSeat> seats = PovCrewSeats;
        if (!PovMode
            || aircraft == null
            || cameraManager == null
            || crewIndex < 0
            || crewIndex >= seats.Count
            || !seats[crewIndex].IsAvailable)
        {
            return false;
        }

        PovCrewSeat seat = seats[crewIndex];
        Transform crewTransform = seat.Anchor;
        Vector3 viewPosition;
        Quaternion viewRotation;

        Pilot? primaryPilot = aircraft.pilots != null && aircraft.pilots.Length > 0
            ? aircraft.pilots[0]
            : null;
        Transform? cockpitView = aircraft.cockpitViewPoint;
        if (seat.Pilot != null && primaryPilot != null && cockpitView != null)
        {
            Vector3 seatViewOffset = primaryPilot.transform.InverseTransformPoint(cockpitView.position);
            Quaternion seatViewRotation = Quaternion.Inverse(primaryPilot.transform.rotation) * cockpitView.rotation;
            viewPosition = crewTransform.TransformPoint(seatViewOffset);
            viewRotation = crewTransform.rotation * seatViewRotation;
        }
        else if (seat.Turret != null)
        {
            Transform aimTransform = seat.ViewDirection != null ? seat.ViewDirection : crewTransform;
            viewPosition = aimTransform.position
                - (aimTransform.forward * 0.55f)
                + (aircraft.transform.up * 0.35f);
            viewRotation = Quaternion.LookRotation(aimTransform.forward, aircraft.transform.up);
        }
        else
        {
            viewPosition = crewTransform.position + (aircraft.transform.forward * 0.05f);
            viewRotation = aircraft.transform.rotation;
        }

        cameraManager.transform.SetPositionAndRotation(viewPosition, viewRotation);
        cameraManager.cameraVelocity = Vector3.zero;
        povLocalPosition = aircraft.transform.InverseTransformPoint(viewPosition);
        povLocalRotation = Quaternion.Inverse(aircraft.transform.rotation) * viewRotation;
        PovCrewIndex = crewIndex;
        ResetPovMotionEffects();
        SyncFreeCameraAngles(cameraManager);
        return true;
    }

    /// <summary>
    /// Centers on the selection right now. A single unit is framed close up; a group (convoy,
    /// control group, box selection) is framed so that every selected unit fits on screen.
    /// </summary>
    internal void CenterOnSelection()
    {
        Unit? selected = selectionService.FocusedSelection;
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (selected == null || cameraManager == null
            || !TryGetFramingPose(cameraManager.transform.forward, out Vector3 position, out Quaternion rotation))
        {
            return;
        }

        gliding = false;
        cameraManager.transform.SetPositionAndRotation(position, rotation);
        cameraManager.cameraVelocity = Vector3.zero;
        FinishCameraMove(cameraManager, selected);
    }

    /// <summary>
    /// Jump to whatever was just picked and keep following it. Used by every list row that
    /// selects a unit, so "click a unit in a list" always shows you the unit without a
    /// second trip to the CENTER button.
    /// </summary>
    internal void FocusSelection()
    {
        if (!CanFollow)
        {
            return;
        }

        Enabled = true;

        // The selection this row just made is handled here, so the next tick must not read it as a
        // fresh click and undo the follow (a plain click no longer moves the camera at all).
        lastSelectionRevision = selectionService.SelectionRevision;
        CenterOnSelection();
    }

    internal void FixedTick()
    {
        if (!PovMode || povEffectsAircraft == null || Time.deltaTime <= 0f)
        {
            return;
        }

        Rigidbody? cockpitRigidbody = povEffectsAircraft.CockpitRB();
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (cockpitRigidbody == null || cameraManager == null)
        {
            return;
        }

        Vector3 pointVelocity = cockpitRigidbody.GetPointVelocity(cameraManager.transform.position);
        Vector3 acceleration = povPreviousVelocity == Vector3.zero
            ? Vector3.zero
            : (pointVelocity - povPreviousVelocity) / Time.deltaTime;
        Vector3 springForce = -500f * povInertiaPosition;
        float verticalDisplacement = Vector3.Dot(cameraManager.transform.up, -povInertiaPosition);
        povAntiSlump += verticalDisplacement * 1000f * Time.deltaTime;
        springForce += cameraManager.transform.up * povAntiSlump;
        povInertiaVelocity += (-Vector3.ClampMagnitude(acceleration, 500f) + springForce) * Time.deltaTime;
        povInertiaVelocity -= Vector3.ClampMagnitude(
            povInertiaVelocity * 20f * Time.deltaTime,
            povInertiaVelocity.magnitude);
        float gForce = acceleration.magnitude / 9.81f;
        float jerk = povPreviousGForce == 0f ? 0f : (gForce - povPreviousGForce) / Time.deltaTime;
        povPreviousVelocity = pointVelocity;
        povPreviousGForce = gForce;
        povLowFrequencyShake = Mathf.Clamp(jerk * 0.005f, povLowFrequencyShake, 1f);
        povLowFrequencyShake = Mathf.Lerp(povLowFrequencyShake, 0f, 5f * Time.fixedDeltaTime);
        povHighFrequencyShake = Mathf.Lerp(povHighFrequencyShake, 0f, 4f * Time.fixedDeltaTime);
    }

    private static void SyncFreeCameraAngles(CameraStateManager cameraManager)
    {
        if (cameraManager.currentState != cameraManager.freeState)
        {
            return;
        }

        Vector3 euler = cameraManager.transform.eulerAngles;
        FreeCameraPanField?.SetValue(cameraManager.freeState, euler.y);
        FreeCameraTiltField?.SetValue(cameraManager.freeState, euler.x);
    }

    private static int CountAircraftTurrets(Aircraft? aircraft)
    {
        if (aircraft?.weaponStations == null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation? station = aircraft.weaponStations[i];
            if (station?.Turrets != null)
            {
                count += station.Turrets.Count;
            }
        }

        return count;
    }

    private static bool IsHeadBoneName(string name)
    {
        return name.EndsWith("head", StringComparison.OrdinalIgnoreCase)
            || name.IndexOf("headbone", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsNearRegisteredPilot(Aircraft aircraft, Vector3 position)
    {
        if (aircraft.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot? pilot = aircraft.pilots[i];
            if (pilot != null && (pilot.transform.position - position).sqrMagnitude < 2.25f)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMoveToFirstCrewPosition()
    {
        IReadOnlyList<PovCrewSeat> seats = PovCrewSeats;
        for (int i = 0; i < seats.Count; i++)
        {
            if (seats[i].IsAvailable)
            {
                return TryMoveToCrewPosition(i);
            }
        }

        return false;
    }

    /// <summary>
    /// Where the camera has to sit to frame the selection, looking along <paramref name="viewDirection"/>
    /// so a jump reads as travelling rather than as being spun around.
    /// </summary>
    private bool TryGetFramingPose(Vector3 viewDirection, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        Unit? selected = selectionService.FocusedSelection;
        if (selected == null || selected.disabled)
        {
            return false;
        }

        float length = selected.definition != null ? selected.definition.length : selected.maxRadius * 2f;
        float distance = Mathf.Max(20f, selected.maxRadius * 4f, length * 2f);
        Vector3 targetPosition = selected.transform.position + (Vector3.up * Mathf.Max(1f, selected.maxRadius * 0.35f));
        if (selectionService.SelectedUnits.Count > 1
            && TryGetSelectionBounds(out Vector3 center, out float extent))
        {
            targetPosition = center + (Vector3.up * Mathf.Max(1f, selected.maxRadius * 0.35f));
            distance = Mathf.Max(distance, (extent * 2.2f) + 60f);
        }

        if (viewDirection.sqrMagnitude < 0.1f)
        {
            viewDirection = -selected.transform.forward;
        }

        position = targetPosition - (viewDirection.normalized * distance);
        rotation = Quaternion.LookRotation(targetPosition - position, Vector3.up);
        return true;
    }

    /// <summary>
    /// The bookkeeping every camera jump shares. Writing the angles back is the important part:
    /// the free camera lerps its rotation toward its own stored pan/tilt every frame, so a jump
    /// that sets only transform.rotation is unwound within a few frames - which is why the camera
    /// could end up following a unit while pointing somewhere else entirely.
    /// </summary>
    private void FinishCameraMove(CameraStateManager cameraManager, Unit selected)
    {
        SyncFreeCameraAngles(cameraManager);
        target = selected;
        followAnchor = GetFollowAnchor(selected);
        lastGlobalPosition = followAnchor;
        followVelocity = Vector3.zero;
        if (PovMode)
        {
            PovCrewIndex = -1;
            BindPovEffectsAircraft(selected as Aircraft);
            CapturePovOffset();
            TryMoveToFirstCrewPosition();
        }
    }

    /// <summary>
    /// True when the selection already sits comfortably on screen. Selecting something you can
    /// see should never move the camera you just aimed.
    /// </summary>
    private bool IsComfortablyFramed(Unit unit)
    {
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Vector3 point = unit.transform.position;
        float radius = unit.maxRadius;
        if (selectionService.SelectedUnits.Count > 1 && TryGetSelectionBounds(out Vector3 center, out float extent))
        {
            point = center;
            radius = Mathf.Max(radius, extent);
        }

        // Size matters as much as screen position: a fast aircraft two screens' worth of sky away
        // is technically centred and completely invisible, and following it without framing it
        // just slides the world past at its speed.
        return CommanderCameraTuning.IsFramed(
            camera.WorldToViewportPoint(point),
            Vector3.Distance(camera.transform.position, point),
            CommanderCameraTuning.ReadableDistance(radius));
    }

    /// <summary>
    /// Travels to the framing pose over a fraction of a second instead of teleporting, and hands
    /// the camera straight back the moment the player touches it.
    /// </summary>
    private void TickAutoFrameGlide(CameraStateManager cameraManager, Vector3 currentAnchor)
    {
        glideElapsed += Time.unscaledDeltaTime;
        followAnchor = currentAnchor;
        if (CommanderCameraPatches.CommanderFreeCameraInputPatch.PlayerMovedCameraThisFrame
            || glideElapsed > GlideTimeoutSeconds
            || !TryGetFramingPose(glideViewDirection, out Vector3 position, out Quaternion rotation))
        {
            gliding = false;
            return;
        }

        float blend = CommanderCameraTuning.SmoothBlend(GlideSmoothing, Time.unscaledDeltaTime);
        cameraManager.transform.SetPositionAndRotation(
            Vector3.Lerp(cameraManager.transform.position, position, blend),
            Quaternion.Slerp(cameraManager.transform.rotation, rotation, blend));
        cameraManager.cameraVelocity = Vector3.zero;
        SyncFreeCameraAngles(cameraManager);
        if (Vector3.Distance(cameraManager.transform.position, position) < 5f)
        {
            gliding = false;
        }
    }

    /// <summary>
    /// How far ahead of a moving unit the camera sits. Off by default: leading is a taste knob,
    /// and it only earns its keep on something fast enough to outrun the frame.
    /// </summary>
    private Vector3 LeadOffset(Vector3 currentAnchor)
    {
        float lead = CommanderSettings.FollowLeadSeconds;
        float deltaTime = Time.unscaledDeltaTime;
        if (lead <= 0f || deltaTime <= 0f)
        {
            followVelocity = Vector3.zero;
            return Vector3.zero;
        }

        followVelocity = Vector3.Lerp(
            followVelocity,
            (currentAnchor - lastGlobalPosition) / deltaTime,
            CommanderCameraTuning.SmoothBlend(0.25f, deltaTime));
        return followVelocity * lead;
    }

    /// <summary>World-space centre and radius of everything currently selected.</summary>
    private bool TryGetSelectionBounds(out Vector3 center, out float extent)
    {
        center = Vector3.zero;
        extent = 0f;
        int count = 0;
        IReadOnlyList<Unit> units = selectionService.SelectedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i] == null || units[i].disabled)
            {
                continue;
            }

            center += units[i].transform.position;
            count++;
        }

        if (count == 0)
        {
            return false;
        }

        center /= count;
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i] != null && !units[i].disabled)
            {
                extent = Mathf.Max(extent, Vector3.Distance(center, units[i].transform.position));
            }
        }

        return true;
    }

    /// <summary>Global position the camera tracks: one unit, or the centre of a group.</summary>
    private Vector3 GetFollowAnchor(Unit fallback)
    {
        if (selectionService.SelectedUnits.Count <= 1 || PovMode)
        {
            return fallback.GlobalPosition().AsVector3();
        }

        return TryGetSelectionBounds(out Vector3 center, out _)
            ? center.ToGlobalPosition().AsVector3()
            : fallback.GlobalPosition().AsVector3();
    }

    /// <summary>
    /// Starts following whatever was just selected. It deliberately does not centre: yanking the
    /// camera on every click is what made selecting a unit feel violent. It only travels when the
    /// unit is off screen, hugging an edge, or too far away to read - and then it glides.
    /// </summary>
    private void HandleSelectionChanged()
    {
        // Off means the camera does not move at all on a selection — including letting go of
        // whatever it was already following, or turning the setting off mid-follow left the camera
        // riding the last unit forever. The centre-and-follow key still works: that is an order.
        // A pick made on the tactical map is the same: the player is looking at the map, and the
        // 3D view behind it stays where they left it.
        if (!CommanderSettings.AutoFollowSelection
            || CommanderTacticalMapService.Instance?.ContainsScreenPoint(Input.mousePosition) == true
            || (DynamicMap.mapMaximized && SceneSingleton<DynamicMap>.i?.IsCursorInMapRectangle() == true))
        {
            if (Enabled)
            {
                Deactivate();
            }

            return;
        }

        Unit? selected = selectionService.FocusedSelection;
        if (selected == null || selected.disabled)
        {
            if (Enabled)
            {
                Deactivate();
            }

            return;
        }

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        target = selected;
        Enabled = true;
        followAnchor = GetFollowAnchor(selected);
        lastGlobalPosition = followAnchor;
        followVelocity = Vector3.zero;
        glideElapsed = 0f;
        gliding = CommanderSettings.AutoFrameSelection
            && cameraManager != null
            && !PovMode
            && !IsComfortablyFramed(selected);
        if (gliding && cameraManager != null)
        {
            // Locked in at the start: re-reading it every frame would let the target pose chase
            // its own rotation while the camera slerps toward it.
            glideViewDirection = cameraManager.transform.forward;
        }
    }

    /// <summary>
    /// F1-F4 jump the camera to a saved viewpoint; the assign-group modifier (Ctrl by default)
    /// stores the current one, matching how control groups are stored.
    /// </summary>
    private void HandleCameraBookmarks()
    {
        if (!CommanderSettings.CameraBookmarks || CommanderShortcutInput.IsTyping)
        {
            return;
        }

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (cameraManager == null)
        {
            return;
        }

        for (int slot = 0; slot < BookmarkCount; slot++)
        {
            if (!Input.GetKeyDown(KeyCode.F1 + slot))
            {
                continue;
            }

            if (CommanderShortcutInput.IsPressed(CommanderSettings.AssignGroupModifier))
            {
                bookmarks[slot] = new CameraBookmark(
                    cameraManager.transform.position,
                    cameraManager.transform.rotation);
                CommanderPlugin.Log.LogInfo($"Camera bookmark {slot + 1} stored.");
                return;
            }

            if (!bookmarks[slot].HasValue)
            {
                return;
            }

            // Following would drag the camera straight back off the bookmark.
            if (Enabled)
            {
                Deactivate();
            }

            cameraManager.transform.SetPositionAndRotation(
                bookmarks[slot].Position, bookmarks[slot].Rotation);
            cameraManager.cameraVelocity = Vector3.zero;
            SyncFreeCameraAngles(cameraManager);
            return;
        }
    }

    private void HandleSpaceShortcut()
    {
        BepInEx.Configuration.KeyboardShortcut shortcut = CommanderSettings.CameraCenterFollow;
        if (CommanderShortcutInput.IsDown(shortcut))
        {
            spaceHeld = true;
            longSpaceTriggered = false;
            spacePressedAt = Time.unscaledTime;
        }

        if (spaceHeld && !longSpaceTriggered && CommanderShortcutInput.IsPressed(shortcut) && Time.unscaledTime - spacePressedAt >= 0.45f)
        {
            if (!Enabled)
            {
                Toggle();
            }

            CenterOnSelection();
            longSpaceTriggered = true;
        }

        if (spaceHeld && CommanderShortcutInput.IsUp(shortcut))
        {
            if (!longSpaceTriggered)
            {
                CenterOnSelection();
            }

            spaceHeld = false;
        }
    }

    private void CapturePovOffset()
    {
        Unit? selected = selectionService.FocusedSelection;
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (selected == null || cameraManager == null)
        {
            return;
        }

        povLocalPosition = selected.transform.InverseTransformPoint(cameraManager.transform.position);
        Vector3 localForward = Quaternion.Inverse(selected.transform.rotation)
            * cameraManager.transform.forward;
        if (localForward.sqrMagnitude < 0.001f)
        {
            localForward = Vector3.forward;
        }

        // Preserve where the camera is looking, but do not preserve world-level roll as
        // a counter-rotation. POV roll must come entirely from the attached unit.
        Vector3 localUp = Mathf.Abs(Vector3.Dot(localForward.normalized, Vector3.up)) > 0.995f
            ? Vector3.forward
            : Vector3.up;
        povLocalRotation = Quaternion.LookRotation(localForward, localUp);
        cameraManager.transform.rotation = selected.transform.rotation * povLocalRotation;
        SyncFreeCameraAngles(cameraManager);
    }

    private void ApplyPovLookInput(CameraStateManager cameraManager)
    {
        if (CommanderShortcutInput.IsTyping
            || CommanderTacticalMapService.Instance?.IsFullscreenOpen == true
            || !CommanderShortcutInput.IsPressed(CommanderSettings.CameraFreeLook))
        {
            return;
        }

        float fovScale = Mathf.Min(cameraManager.mainCamera.fieldOfView / 20f, 1f);
        float pitch = (PlayerSettings.viewInvertPitch ? 1f : -1f)
            * fovScale
            * Input.GetAxisRaw("Mouse Y")
            * PovMouseLookScale
            * PlayerSettings.viewSensitivity;
        float yaw = fovScale
            * Input.GetAxisRaw("Mouse X")
            * PovMouseLookScale
            * PlayerSettings.viewSensitivity;

        if (Mathf.Approximately(pitch, 0f) && Mathf.Approximately(yaw, 0f))
        {
            return;
        }

        povLocalRotation = Quaternion.AngleAxis(yaw, Vector3.up)
            * povLocalRotation
            * Quaternion.AngleAxis(pitch, Vector3.right);
    }

    private void EnsurePovNearClip()
    {
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return;
        }

        if (!ReferenceEquals(camera, povClipCamera))
        {
            RestorePovNearClip();
            povClipCamera = camera;
            povPreviousNearClipPlane = camera.nearClipPlane;
        }

        camera.nearClipPlane = PovNearClipPlane;
    }

    private void ExitPovMode()
    {
        BindPovEffectsAircraft(null);
        RestorePovNearClip();
        PovMode = false;
        PovCrewIndex = -1;
    }

    private void BindPovEffectsAircraft(Aircraft? aircraft)
    {
        if (ReferenceEquals(povEffectsAircraft, aircraft))
        {
            return;
        }

        if (!ReferenceEquals(povEffectsAircraft, null))
        {
            povEffectsAircraft.onShake -= OnPovAircraftShake;
        }

        povEffectsAircraft = aircraft;
        povCrewAircraft = null;
        povCrewTurretCount = -1;
        povCrewSeats.Clear();
        if (povEffectsAircraft != null)
        {
            povEffectsAircraft.onShake += OnPovAircraftShake;
        }

        ResetPovMotionEffects();
    }

    private void RefreshPovCrewSeats()
    {
        Aircraft? aircraft = FollowedAircraft;
        int turretCount = CountAircraftTurrets(aircraft);
        if (ReferenceEquals(povCrewAircraft, aircraft) && povCrewTurretCount == turretCount)
        {
            return;
        }

        povCrewAircraft = aircraft;
        povCrewTurretCount = turretCount;
        povCrewSeats.Clear();
        if (aircraft == null)
        {
            return;
        }

        if (aircraft.pilots != null)
        {
            for (int i = 0; i < aircraft.pilots.Length; i++)
            {
                Pilot? pilot = aircraft.pilots[i];
                if (pilot != null)
                {
                    povCrewSeats.Add(new PovCrewSeat(
                        i == 0 ? "PILOT" : $"CREW {i + 1}",
                        pilot.transform,
                        pilot));
                }
            }
        }

        HashSet<Transform> knownHeads = new();
        int gunnerCount = 0;
        Animator[] animators = aircraft.GetComponentsInChildren<Animator>(includeInactive: true);
        for (int animatorIndex = 0; animatorIndex < animators.Length; animatorIndex++)
        {
            Animator animator = animators[animatorIndex];
            if (animator == null || !animator.isHuman || animator.GetComponentInParent<Pilot>() != null)
            {
                continue;
            }

            Transform? head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null || !knownHeads.Add(head) || IsNearRegisteredPilot(aircraft, head.position))
            {
                continue;
            }

            gunnerCount++;
            povCrewSeats.Add(new PovCrewSeat($"GUNNER {gunnerCount}", head, null));
        }

        SkinnedMeshRenderer[] renderers = aircraft.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            SkinnedMeshRenderer renderer = renderers[rendererIndex];
            if (renderer == null || renderer.GetComponentInParent<Pilot>() != null)
            {
                continue;
            }

            Transform[] bones = renderer.bones;
            for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
            {
                Transform? bone = bones[boneIndex];
                if (bone == null
                    || !IsHeadBoneName(bone.name)
                    || !knownHeads.Add(bone)
                    || IsNearRegisteredPilot(aircraft, bone.position))
                {
                    continue;
                }

                gunnerCount++;
                povCrewSeats.Add(new PovCrewSeat(
                    $"GUNNER {gunnerCount}",
                    bone,
                    null));
            }
        }

        AddTurretCrewSeats(aircraft, knownHeads, ref gunnerCount);
    }

    private void AddTurretCrewSeats(
        Aircraft aircraft,
        HashSet<Transform> knownAnchors,
        ref int gunnerCount)
    {
        if (aircraft.weaponStations == null)
        {
            return;
        }

        for (int stationIndex = 0; stationIndex < aircraft.weaponStations.Count; stationIndex++)
        {
            WeaponStation? station = aircraft.weaponStations[stationIndex];
            if (station?.Turrets == null)
            {
                continue;
            }

            Transform? viewDirection = station.Weapons != null && station.Weapons.Count > 0
                ? station.Weapons[0]?.transform
                : null;
            for (int turretIndex = 0; turretIndex < station.Turrets.Count; turretIndex++)
            {
                Turret? turret = station.Turrets[turretIndex];
                if (turret == null || !knownAnchors.Add(turret.transform))
                {
                    continue;
                }

                gunnerCount++;
                povCrewSeats.Add(new PovCrewSeat(
                    $"GUNNER {gunnerCount}",
                    turret.transform,
                    null,
                    turret,
                    viewDirection));
            }
        }
    }

    private void IntegratePovInertiaPosition()
    {
        povInertiaPosition += povInertiaVelocity * Mathf.Min(Time.deltaTime, 1f / 60f);
        if (povInertiaPosition.magnitude > 0.15f)
        {
            povInertiaVelocity = Vector3.zero;
            povInertiaPosition = Vector3.ClampMagnitude(povInertiaPosition, 0.15f);
        }
    }

    private void OnPovAircraftShake(Aircraft.OnShake shake)
    {
        povLowFrequencyShake += shake.lowFreqShake;
        povHighFrequencyShake += shake.highFreqShake;
    }

    private Vector3 GetPovCameraShake()
    {
        povLowFrequencyShake = Mathf.Min(povLowFrequencyShake, 1f);
        povHighFrequencyShake = Mathf.Min(povHighFrequencyShake, 1f);
        if (povLowFrequencyShake < 0.01f && povHighFrequencyShake < 0.05f)
        {
            return Vector3.zero;
        }

        float time = Time.timeSinceLevelLoad;
        Vector3 lowFrequencyOffset = 0.03f * new Vector3(
            Mathf.PerlinNoise1D(time * 16f) - 0.5f,
            Mathf.PerlinNoise1D(time * 13.333334f) - 0.5f,
            Mathf.PerlinNoise1D(time * 9.6856f) - 0.5f);
        Vector3 highFrequencyOffset = 0.01f * new Vector3(
            Mathf.PerlinNoise1D(time * 32f) - 0.5f,
            Mathf.PerlinNoise1D(time * 26.666668f) - 0.5f,
            Mathf.PerlinNoise1D(time * 19.3712f) - 0.5f);
        return (lowFrequencyOffset * Mathf.Max(povLowFrequencyShake - 0.01f, 0f))
            + (highFrequencyOffset * Mathf.Max(povHighFrequencyShake - 0.05f, 0f));
    }

    private void ResetPovMotionEffects()
    {
        povInertiaPosition = Vector3.zero;
        povInertiaVelocity = Vector3.zero;
        povPreviousVelocity = Vector3.zero;
        povAntiSlump = 0f;
        povPreviousGForce = 0f;
        povLowFrequencyShake = 0f;
        povHighFrequencyShake = 0f;
    }

    private void RestorePovNearClip()
    {
        if (povClipCamera != null)
        {
            povClipCamera.nearClipPlane = povPreviousNearClipPlane;
        }

        povClipCamera = null;
    }

    private readonly struct CameraBookmark
    {
        internal CameraBookmark(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
            HasValue = true;
        }

        internal Vector3 Position { get; }

        internal Quaternion Rotation { get; }

        internal bool HasValue { get; }
    }
}
