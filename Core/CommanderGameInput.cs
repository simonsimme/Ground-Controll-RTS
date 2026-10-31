using Rewired;

namespace GroundControlRts;

internal static class CommanderGameInput
{
    internal static bool MapDown => GetButtonDown("Map");

    internal static bool CancelDown => GetButtonDown("Cancel");

    internal static bool SelectDown => GetButtonDown("Select");

    internal static bool JumpMapDown => GetButtonDown("Jump Map");

    internal static float GetAxis(string action)
    {
        Player? player = GetPlayer();
        return player != null ? player.GetAxis(action) : 0f;
    }

    private static bool GetButtonDown(string action)
    {
        Player? player = GetPlayer();
        return player != null && player.GetButtonDown(action);
    }

    private static Player? GetPlayer()
    {
        return ReInput.isReady ? ReInput.players.GetPlayer(0) : null;
    }
}
