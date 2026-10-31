using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Copies the missions shipped next to the DLL into the game's own user mission folder, so a map
/// that comes with the mod shows up in the mission list without the player moving files by hand.
/// </summary>
/// <remarks>
/// A shipped mission is mod content, not player content: if the file on disk differs from the one
/// in the plugin folder it is overwritten, so a fix to the map arrives with the next mod update.
/// Anyone who wants to edit it should save a copy under a different name.
/// </remarks>
internal static class CommanderMissionInstaller
{
    /// <summary>Mission file names shipped beside the plugin DLL, without the extension.</summary>
    private static readonly string[] ShippedMissions =
    {
        "Ground Control Duel",
        "Ground Control Champions",
        "Ground Control Draft",
        "Ground Control Team Duel",

        // Team Duel against the AI, which plays a side picked at random (CommanderTeamDuelPvePatches).
        "Ground Control Team Duel PvE",

        // The duel's map against a passive enemy, with the step card of CommanderTutorialService.
        "Ground Control Tutorial",

        // Champions with a 10 B enemy under a thunderstorm; the name contains "Ground Control
        // Champions", so every Champions rule matches it, and the budget turns GOD DIFFICULTY on.
        "Ground Control Champions GOD",
    };

    internal static void InstallShippedMissions()
    {
        string? pluginFolder = Path.GetDirectoryName(typeof(CommanderMissionInstaller).Assembly.Location);
        if (string.IsNullOrEmpty(pluginFolder))
        {
            return;
        }

        for (int i = 0; i < ShippedMissions.Length; i++)
        {
            Install(pluginFolder!, ShippedMissions[i]);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Installing a shipped mission is best effort; a failure is logged and must not stop the plugin loading.")]
    private static void Install(string pluginFolder, string missionName)
    {
        string source = Path.Combine(pluginFolder, missionName + ".json");
        if (!File.Exists(source))
        {
            CommanderPlugin.Log.LogWarning(
                $"Mission '{missionName}' was not found next to the plugin DLL, so it was not installed. "
                    + "Copy the whole plugin folder from the release, not just the DLL.");
            return;
        }

        // The game reads user missions from <persistentDataPath>/Missions/<name>/<name>.json, with a
        // meta.json alongside naming the file.
        string folder = Path.Combine(Path.Combine(Application.persistentDataPath, "Missions"), missionName);
        string destination = Path.Combine(folder, missionName + ".json");
        try
        {
            string json = File.ReadAllText(source);

            // Without an objective by this exact name the game throws out of StartMission and the
            // mission's own units never spawn, which reads in-game as an empty map plus one load
            // warning. Cheap to check here, expensive to diagnose in the log.
            if (json.IndexOf("\"Mission Start\"", StringComparison.Ordinal) < 0)
            {
                CommanderPlugin.Log.LogWarning(
                    $"Mission '{missionName}' has no 'Mission Start' objective; the game will not start it.");
            }

            if (File.Exists(destination)
                && string.Equals(File.ReadAllText(destination), json, StringComparison.Ordinal))
            {
                return;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(destination, json);
            File.WriteAllText(Path.Combine(folder, "meta.json"), "{\"FileName\":\"" + missionName + "\"}");
            CommanderPlugin.Log.LogInfo($"Installed mission '{missionName}' to {folder}.");
        }
        catch (Exception e)
        {
            // A locked or read-only profile folder is a bad install, not a reason to take the mod down.
            CommanderPlugin.Log.LogWarning($"Could not install mission '{missionName}': {e.Message}");
        }
    }
}
