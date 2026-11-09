using System;
using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>The faction policy for buying ground vehicles at player depots.</summary>
internal static class CommanderDepotVehicleRoster
{
    private const string Bdf = "BDF";
    private const string Pala = "PALA";
    private const string Shared = "SHARED";
    private const string HltPrefix = "HLT-";
    private const string MsvPrefix = "Truck2-";

    // Keys are the game's VehicleDefinition.jsonKey values. Use Bdf, Pala or Shared to override
    // the automatic pairing below. The stock faction assets and missions identify these variants.
    private static readonly Dictionary<string, string> Assignments = new(StringComparer.Ordinal)
    {
        ["AFV8_APC"] = Bdf,
        ["AFV8_IFV"] = Bdf,
        ["AFV8_SAM"] = Bdf,
        ["Linebreaker_APC"] = Pala,
        ["Linebreaker_IFV"] = Pala,
        ["Linebreaker_SAM"] = Pala,
        ["MBT"] = Bdf, // Type-12
        ["MBT1"] = Pala, // Spearhead
        ["CRAMTrailer1"] = Bdf,
        ["Truck2-CRAM"] = Pala,
        ["LaserTrailer1"] = Bdf, // HLT-HEL
        ["Truck2-LADS"] = Pala,
        ["HLT-MART"] = Bdf,
        ["Truck2-MLRS"] = Pala,
        ["SAMTrailer1"] = Bdf, // Towed StratoLance R9
        ["Truck2-RSAM"] = Pala, // MSV R9 Stratolance
        ["SPAAG1"] = Shared, // AeroSentry
        ["SPAAG2"] = Shared, // Anvil
        ["Truck2-TBM"] = Shared,
        ["Truck2-TBM-N"] = Shared,
        ["UGVDozer1"] = Shared, // M12 Jackknife
    };

    internal static bool? HostFactionOnly { get; set; }

    internal static bool FactionOnly => (CommanderNetwork.HostHasMod ? HostFactionOnly : null)
        ?? CommanderSettings.LimitToFactoryVehicles;

    internal static bool AllowsPurchase(FactionHQ? hq, VehicleDefinition definition)
    {
        if (!FactionOnly)
        {
            return true;
        }

        string key = definition.jsonKey;
        if (string.IsNullOrEmpty(key))
        {
            return true;
        }

        if (Assignments.TryGetValue(key, out string? assignment))
        {
            return assignment == Shared || string.Equals(assignment, hq?.faction?.factionTag, StringComparison.Ordinal);
        }

        // HLT and MSV variants with the same game key suffix are counterparts (FC, FT, L, M, R, T).
        // Convoy groups omit whole roles, so a lone variant or an unclassified vehicle stays shared.
        string? counterpartKey = null;
        string? faction = null;
        if (key.StartsWith(HltPrefix, StringComparison.Ordinal))
        {
            counterpartKey = MsvPrefix + key.Substring(HltPrefix.Length);
            faction = Bdf;
        }
        else if (key.StartsWith(MsvPrefix, StringComparison.Ordinal))
        {
            counterpartKey = HltPrefix + key.Substring(MsvPrefix.Length);
            faction = Pala;
        }

        return counterpartKey == null
            || !CommanderGameAccess.IsSpawnableVehicleDefinition(CommanderNetwork.Definition<VehicleDefinition>(counterpartKey))
            || string.Equals(faction, hq?.faction?.factionTag, StringComparison.Ordinal);
    }
}
