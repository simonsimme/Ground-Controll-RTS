using System;
using NuclearOption.SavedMission;

namespace GroundControlRts;

internal static class CommanderFeatureGate
{
    /// <summary>The mission tag a map maker sets with the editor's "Ground Control RTS" toggle. A
    /// tag is the one place a mission file keeps a flag of ours through a save, and the mission
    /// picker already lists every tag as a filter.</summary>
    internal const string MissionTag = "GC RTS";

    private static readonly string[] SupportedMissionNames =
    {
        "Altercation",
        "Carrier Duel",
        "Confrontation",
        "Domination",
        "Escalation",
        "Ground Control",
        "Terminal Control",
    };

    private static bool initialized;
    private static bool manuallyUnlocked;

    internal static string MissionName { get; private set; } = string.Empty;

    internal static bool IsSupportedMission { get; private set; }

    /// <summary>A custom map made a Ground Control map in the editor. Off inside the editor itself,
    /// where nothing should be paying income or buying armies.</summary>
    internal static bool IsTaggedMission { get; private set; }

    /// <summary>On for the listed missions, by hand, or on every mission once the host's AI
    /// COMMANDERS setting reaches past the listed ones: the commander is an advanced service.</summary>
    internal static bool AdvancedFeaturesEnabled =>
        IsSupportedMission || IsTaggedMission || manuallyUnlocked || CommanderEnemyCommanderService.CommandersEverywhere;

    internal static void RefreshMission()
    {
        // Read every frame rather than cached with the name: the editor's toggle changes it under the
        // same name, and a scan of half a dozen tags is nothing.
        IsTaggedMission = GameManager.gameState != GameState.Editor && HasTag(MissionManager.CurrentMission);
        string currentName = MissionManager.CurrentMission?.Name?.Trim() ?? string.Empty;
        if (initialized && string.Equals(MissionName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        MissionName = currentName;
        IsSupportedMission = IsSupportedName(currentName);
        manuallyUnlocked = false;
        initialized = true;
    }

    internal static void UnlockAdvancedFeatures()
    {
        manuallyUnlocked = true;
    }

    internal static void ResetSession()
    {
        MissionName = string.Empty;
        IsSupportedMission = false;
        IsTaggedMission = false;
        manuallyUnlocked = false;
        initialized = false;
    }

    internal static bool HasTag(Mission? mission)
    {
        return CommanderMapSettings.HasTag(mission, MissionTag);
    }

    internal static void SetTag(Mission mission, bool on)
    {
        CommanderMapSettings.SetTag(mission, MissionTag, on);
    }

    private static bool IsSupportedName(string name)
    {
        for (int i = 0; i < SupportedMissionNames.Length; i++)
        {
            if (name.IndexOf(SupportedMissionNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
