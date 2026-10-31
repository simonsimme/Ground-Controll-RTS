namespace GroundControlRts;

internal sealed class CommanderCameraController
{
    private CameraBaseState? previousState;
    private CameraStateManager? activeManager;
    private bool active;

    internal static bool CustomInputActive { get; private set; }

    internal bool TryActivate()
    {
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (cameraManager == null || cameraManager.freeState == null)
        {
            return false;
        }

        activeManager = cameraManager;
        previousState = cameraManager.currentState;
        if (cameraManager.currentState != cameraManager.freeState)
        {
            cameraManager.SwitchState(cameraManager.freeState);
        }

        active = true;
        CommanderCameraPatches.CommanderFreeCameraInputPatch.ResetCustomMotion();
        CustomInputActive = true;
        return true;
    }

    internal void Deactivate(bool restorePreviousState = true)
    {
        CustomInputActive = false;
        CommanderCameraPatches.CommanderFreeCameraInputPatch.ResetCustomMotion();
        if (activeManager != null)
        {
            activeManager.cameraVelocity = UnityEngine.Vector3.zero;
        }

        if (!active)
        {
            return;
        }

        // The orbit/cockpit view follows a rigidbody. If that unit died while RTS mode was open,
        // restoring the view throws every frame and leaves the camera stuck, so stay free instead.
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (restorePreviousState
            && ReferenceEquals(cameraManager, activeManager)
            && cameraManager != null
            && previousState != null
            && cameraManager.currentState != previousState
            && cameraManager.followingRB != null)
        {
            cameraManager.SwitchState(previousState);
        }

        activeManager = null;
        previousState = null;
        active = false;
    }
}
