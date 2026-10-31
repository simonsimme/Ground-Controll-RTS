using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// How long a depot or a naval dock takes to roll out one unit, in game seconds (the 2x/4x speed
/// buttons shorten it, pause stops it). Better units take longer, nothing takes over 1 minute.
/// A unit already in the reserve (factory output, a drafted army) was built elsewhere and leaves at once.
/// </summary>
internal static class CommanderBuildTimes
{
    internal const float MaxSeconds = 60f;

    // Keyed on unitName, the same exact names UNITS.md and the price table use.
    private static readonly Dictionary<string, float> Seconds = new()
    {
        // Trucks and tractors.
        ["HLT Tractor"] = 3f,
        ["HLT Flatbed"] = 3f,
        ["HLT Fuel Tanker"] = 5f,
        ["HLT Munitions Truck"] = 5f,
        ["M12 Jackknife"] = 10f,

        // Light armour.
        ["LCV25 AA"] = 5f,
        ["AFV6 APC"] = 7f,
        ["AFV6 IFV"] = 8f,
        ["AFV6 AT"] = 8f,
        ["AFV6 AA"] = 8f,
        ["AFV8 APC"] = 8f,
        ["AFV8 IFV"] = 10f,
        ["AFV8 Mobile Air Defense"] = 12f,
        ["Linebreaker APC"] = 10f,
        ["Linebreaker IFV"] = 12f,
        ["Linebreaker SAM"] = 13f,

        // Support and radar.
        ["HLT Fire Control"] = 15f,
        ["MSV Fire Control"] = 15f,
        ["HLT Radar Truck"] = 20f,
        ["MSV Radar"] = 20f,

        // Tanks, gun air defence and artillery.
        ["FGA-57 Anvil"] = 20f,
        ["Type-12 MBT"] = 25f,
        ["HLT Mobile Artillery"] = 25f,
        ["MSV CRAM"] = 25f,
        ["AeroSentry SPAAG"] = 27f,
        ["Spearhead MBT"] = 30f,
        ["MSV LADS"] = 33f,
        ["MSV MLRS"] = 33f,

        // Missile systems.
        ["T9K41 Boltstrike"] = 37f,
        ["MSV Ballistic Missile Launcher"] = 40f,
        ["MSV Nuclear Ballistic Missile Launcher"] = 50f,
        ["MSV R9 Stratolance Launcher"] = 60f,

        // Towed launchers.
        ["HLT-CRAM"] = 20f,
        ["HLT-HEL"] = 27f,
        ["Boltstrike RAM45 Launcher"] = 33f,
        ["StratoLance R9 Launcher"] = 53f,

        // Ships.
        ["OTB-31 landing craft"] = 15f,
        ["Shard Class Corvette"] = 30f,
        ["Dynamo Class Destroyer"] = 50f,
        ["Hyperion Class Carrier"] = 60f,
        ["Annex Class Carrier"] = 60f,
    };

    internal static float For(UnitDefinition definition)
    {
        if (Seconds.TryGetValue(definition.unitName, out float seconds))
        {
            return seconds;
        }

        // A unit not in the table (a game update, a modded unit) is timed by price: 1 M is about
        // 10 s, 100 M about 40 s, a ship by the dock level its class needs.
        if (definition is ShipDefinition ship)
        {
            return CommanderNavalPurchaseService.GetRequiredDockLevel(ship) switch
            {
                1 => 15f,
                2 => 33f,
                _ => 53f,
            };
        }

        return Mathf.Clamp(5f + (18.3f * Mathf.Log10(1f + Mathf.Max(0f, definition.value))), 3f, MaxSeconds);
    }

    /// <summary>"1:05", for the countdown on a queue picture.</summary>
    internal static string Format(float seconds)
    {
        int whole = Mathf.CeilToInt(Mathf.Max(0f, seconds));
        return $"{whole / 60}:{whole % 60:00}";
    }
}
