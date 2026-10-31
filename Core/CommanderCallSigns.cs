using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// NATO call signs for aircraft and ships, so two A-19 Brawlers read as, say, Kilo and Tango in the
/// alerts, the kill log and every list. Each (faction, type) list deals the names in its own random
/// order, slot i holding the i-th name dealt, and a slot whose holder is gone goes to the next
/// airframe or hull of that type, so a lost Kilo's name comes back. The host deals every name and
/// sends the list to the guests, so a commander and their pilots mean the same aircraft by Tango. A
/// guest shows the plain type name until the list reaches it; with an unmodded host it deals its own.
/// </summary>
internal static class CommanderCallSigns
{
    /// <summary>A late joiner gets the list this long after joining at the latest. Changes go at once.</summary>
    private const float FullResendSeconds = 10f;

    private static readonly string[] Names =
    {
        "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliet",
        "Kilo", "Lima", "Mike", "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango",
        "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu",
    };

    private static readonly Dictionary<string, List<Unit?>> Slots = new();
    private static readonly Dictionary<string, string[]> Orders = new();

    /// <summary>Guest only: the host's names by persistent id. Kept after a unit dies, for the kill feed.</summary>
    private static readonly Dictionary<uint, string> HostNames = new();

    // Not UnityEngine.Random: labels are drawn per client, and that stream is shared with the game.
    private static readonly System.Random Rng = new();

    private static string lastSent = string.Empty;
    private static float nextFullSendAt;
    private static int builtFrame = -1;
    private static bool sendThisFrame;

    internal static void Initialize()
    {
        CommanderNetwork.OnGuest("calls", OnHostNames);
        CommanderNetwork.OnSync(SyncNames);
    }

    internal static void Reset()
    {
        Slots.Clear();
        Orders.Clear();
        HostNames.Clear();
        lastSent = string.Empty;
        nextFullSendAt = 0f;
        builtFrame = -1;
    }

    /// <summary>The unit's call sign, empty on a guest the host has not named it to yet.</summary>
    internal static string Get(Unit unit)
    {
        if (CommanderNetwork.HostHasMod)
        {
            return HostNames.TryGetValue(unit.persistentID.Id, out string hostName) ? hostName : string.Empty;
        }

        string key = $"{unit.NetworkHQ?.faction?.name}|{unit.unitName}";
        if (!Slots.TryGetValue(key, out List<Unit?> slots))
        {
            slots = new List<Unit?>();
            Slots[key] = slots;
            Orders[key] = ShuffledNames();
        }

        int index = slots.IndexOf(unit);
        if (index < 0)
        {
            index = slots.FindIndex(holder => holder == null || holder.disabled);
            if (index < 0)
            {
                slots.Add(unit);
                index = slots.Count - 1;
            }
            else
            {
                slots[index] = unit;
            }
        }

        string name = Orders[key][index % Names.Length];
        return index < Names.Length ? name : $"{name} {(index / Names.Length) + 1}";
    }

    /// <summary>Called once per guest faction in one frame; the list is the same for all of them.</summary>
    private static void SyncNames(FactionHQ hq, List<string[]> messages)
    {
        if (builtFrame != Time.frameCount)
        {
            builtFrame = Time.frameCount;
            string text = BuildNames();
            sendThisFrame = text != lastSent || Time.unscaledTime >= nextFullSendAt;
            if (sendThisFrame)
            {
                lastSent = text;
                nextFullSendAt = Time.unscaledTime + FullResendSeconds;
            }
        }

        if (sendThisFrame)
        {
            messages.Add(new[] { "calls", lastSent });
        }
    }

    private static string BuildNames()
    {
        StringBuilder text = new();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.factionUnits == null)
            {
                continue;
            }

            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit)
                    && !unit.disabled
                    && (unit is Ship || (unit is Aircraft aircraft && aircraft.Player == null)))
                {
                    text.Append(id.Id).Append(':').Append(Get(unit)).Append(';');
                }
            }
        }

        return text.ToString();
    }

    private static void OnHostNames(string[] args)
    {
        foreach (string entry in CommanderNetwork.Arg(args, 1).Split(';'))
        {
            int colon = entry.IndexOf(':');
            if (colon > 0 && uint.TryParse(entry.Substring(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            {
                HostNames[id] = entry.Substring(colon + 1);
            }
        }
    }

    private static string[] ShuffledNames()
    {
        string[] order = (string[])Names.Clone();
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }
}
