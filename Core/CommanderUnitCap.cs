using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// The optional per-side unit cap (Settings > Gameplay). Counts a faction's live vehicles, ships
/// and aircraft, and is asked at every door a unit comes out of: the depot, the hangar, the naval
/// purchase, and the enemy commander's buy review, so the AI stops spending on units it cannot
/// field. Starting builders and SAM site emplacements are not capped.
/// </summary>
/// <remarks>
/// The cap is the host's setting: every spawn runs on the host, and a guest is sent the host's
/// choice with the sync (<see cref="HostMode"/>), so its buttons refuse what the host would and
/// its settings show the host's cap, locked.
/// </remarks>
internal static class CommanderUnitCap
{
    internal const int ModePerformance = 0;
    internal const int ModeNormal = 1;
    internal const int ModeLarge = 2;

    /// <summary>Highest ship or artillery cap the settings offer.</summary>
    internal const int MaxKindCap = 50;

    private static readonly int[] Caps = { 60, 150, 300, 450 };
    private static readonly string[] Labels = { "PERFORMANCE", "NORMAL", "LARGE", "ULTRA" };

    // Factions refused since they last had room, so the log or the player's notice says "capped"
    // once, not every tick.
    private static readonly HashSet<FactionHQ> Logged = new();

    // Same, per faction and kind (true for the ship cap, false for artillery).
    private static readonly HashSet<(FactionHQ, bool)> LoggedKind = new();

    internal static int ModeCount => Caps.Length;

    /// <summary>
    /// Ships each side may have afloat, 0 for no limit. Set per mission (GC RTS Settings), apart from
    /// the unit cap, and read from the mission's tags, which reach every guest with the mission.
    /// </summary>
    internal static int ShipCap => UnityEngine.Mathf.Clamp(CommanderMapSettings.OverrideInt(CommanderMapSettings.ShipCapPrefix) ?? 0, 0, MaxKindCap);

    /// <summary>Artillery vehicles each side may field, 0 for no limit. Like <see cref="ShipCap"/>.</summary>
    internal static int ArtilleryCap => UnityEngine.Mathf.Clamp(CommanderMapSettings.OverrideInt(CommanderMapSettings.ArtilleryCapPrefix) ?? 0, 0, MaxKindCap);

    /// <summary>On a guest, the host's mode, -1 for off. A host without the mod sends nothing, and has no cap.</summary>
    internal static int HostMode { get; set; } = -1;

    internal static bool Enabled => CommanderNetwork.IsGuest ? HostMode >= 0 : CommanderSettings.UnitCapEnabled;

    internal static int Mode => CommanderNetwork.IsGuest ? HostMode : CommanderSettings.UnitCapMode;

    internal static int Cap => GetCap(Mode);

    /// <summary>
    /// Ground vehicles an AI side may field: two thirds of the cap. The cap counts aircraft too, and
    /// factories keep the vehicle reserve topped up, so on PERFORMANCE (60) both AI commanders filled
    /// every slot with APCs and never launched another aircraft.
    /// </summary>
    internal static int GroundCap => Cap * 2 / 3;

    /// <summary>The most of any one unit an AI side may field: 60% of the cap.</summary>
    internal static int TypeCap => Cap * 3 / 5;

    /// <summary>Air defence vehicles an AI side may own under the cap: 30% of its ground share.</summary>
    internal static int AirDefenceCap => GroundCap * 3 / 10;

    /// <summary>Free builders the Champions deal hands an AI side under the cap: a tenth of it.</summary>
    internal static int BuilderCap => Cap / 10;

    internal static int GetCap(int mode)
    {
        return Caps[UnityEngine.Mathf.Clamp(mode, 0, Caps.Length - 1)];
    }

    internal static string GetLabel(int mode)
    {
        return Labels[UnityEngine.Mathf.Clamp(mode, 0, Labels.Length - 1)];
    }

    /// <summary>Plants of one kind per base worth building under the cap: 1, 2, 5 for 60, 150, 300.
    /// Every plant fills the reserve whether or not there is room to field it, so past this the AI
    /// was saving its money for buildings that only made units to sit in storage.</summary>
    internal static int FactoriesPerBase(int uncapped)
    {
        return Enabled ? UnityEngine.Mathf.Clamp(Cap / 60, 1, uncapped) : uncapped;
    }

    /// <summary>An AI commander plays this side: every side with AI COMMANDERS on both, else
    /// <see cref="CommanderEnemyCommanderService.IsAiFaction"/>.</summary>
    internal static bool IsAiSide(FactionHQ? hq)
    {
        return CommanderEnemyCommanderService.BothSides
            || CommanderEnemyCommanderService.IsAiFaction(hq, CommanderGameAccess.GetLocalHq());
    }

    /// <summary>
    /// True when an AI side already fields 60% of the cap in this one unit. The spawn is refused and
    /// the unit stays in the faction's reserve, which the game deploys again once losses make room.
    /// </summary>
    internal static bool IsAiTypeFull(FactionHQ? hq, UnitDefinition? definition)
    {
        if (!Enabled || hq == null || hq.factionUnits == null || definition == null || !IsAiSide(hq))
        {
            return false;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && !unit.disabled && ReferenceEquals(unit.definition, definition))
            {
                count++;
            }
        }

        return count >= TypeCap;
    }

    /// <summary>True when an AI side's ground vehicles have taken their share of the cap.</summary>
    internal static bool IsGroundFull(FactionHQ? hq)
    {
        if (!Enabled || hq == null || hq.factionUnits == null)
        {
            return false;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && !unit.disabled && unit is GroundVehicle)
            {
                count++;
            }
        }

        return count >= GroundCap;
    }

    internal static int CountUnits(FactionHQ hq)
    {
        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && !unit.disabled && (unit is GroundVehicle || unit is Ship || unit is Aircraft))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// True when <paramref name="hq"/> may not field another unit. Shows the refusal to the local
    /// player when they asked for the unit (<paramref name="tellPlayer"/>; the game's own reserve
    /// deployment would repeat it every tick), and logs it once per capped stretch for anyone else.
    /// </summary>
    internal static bool IsFull(FactionHQ? hq, bool tellPlayer = true)
    {
        if (!Enabled || hq == null || hq.factionUnits == null)
        {
            return false;
        }

        int count = CountUnits(hq);
        if (count < Cap)
        {
            Logged.Remove(hq);
            return false;
        }

        // GetLocalHq is the guest's faction while the host runs a guest's request, and Fail then
        // sends the notice to that guest. Once per capped stretch: queued buys retry every few
        // seconds and kept the banner up for as long as the side stayed full.
        if (ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()))
        {
            if (tellPlayer && Logged.Add(hq))
            {
                CommanderNotice.Fail($"UNIT CAP REACHED: {count} / {Cap} UNITS");
            }
        }
        else if (Logged.Add(hq))
        {
            CommanderPlugin.Log.LogInfo($"{hq.faction?.factionName} is at the unit cap ({count} / {Cap}); its spawns are held.");
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="definition"/> is a ship and the side is at the ship cap, or artillery
    /// and the side is at the artillery cap. Tells and logs like <see cref="IsFull"/>.
    /// </summary>
    internal static bool IsKindFull(FactionHQ? hq, UnitDefinition? definition, bool tellPlayer = true)
    {
        bool ship = definition is ShipDefinition;
        int cap = ship ? ShipCap
            : definition is VehicleDefinition { vehicleType: VehicleType.ART } ? ArtilleryCap
            : 0;
        if (cap <= 0 || hq == null || hq.factionUnits == null)
        {
            return false;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && !unit.disabled
                && (ship ? unit is Ship : unit.definition is VehicleDefinition { vehicleType: VehicleType.ART }))
            {
                count++;
            }
        }

        string kind = ship ? "SHIP" : "ARTILLERY";
        if (count < cap)
        {
            LoggedKind.Remove((hq, ship));
            return false;
        }

        if (ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()))
        {
            if (tellPlayer && LoggedKind.Add((hq, ship)))
            {
                CommanderNotice.Fail($"{kind} CAP REACHED: {count} / {cap}");
            }
        }
        else if (LoggedKind.Add((hq, ship)))
        {
            CommanderPlugin.Log.LogInfo($"{hq.faction?.factionName} is at the {(ship ? "ship" : "artillery")} cap ({count} / {cap}); those spawns are held.");
        }

        return true;
    }
}
