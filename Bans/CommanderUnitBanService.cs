using System;
using System.Collections.Generic;
using Mirage;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>Where the unit ban vote is. Off when the host did not turn bans on.</summary>
internal enum BanPhase
{
    Off,
    Voting,
    Locked,
}

/// <summary>
/// Unit bans, two optional rules from GC RTS Settings. The host's own list bans any units, of any
/// kind, from the start. The vote, when on: before the match each player votes for up to
/// <see cref="MaxBans"/> units of any kind, and each side's most voted become that side's bans. Every ban applies to
/// both sides for the whole match. Nothing comes out of a depot or a hangar while the vote is open,
/// so the bans are in force before the first unit is bought.
/// </summary>
/// <remarks>
/// <para>
/// Host-authoritative. A guest edits its own ballot and sends all of it (<c>ban.set</c>); the host
/// tallies each side and syncs the side's tally, the ready counts and, once locked, the bans
/// (<c>ban</c>). A side never sees the other side's votes before the lock.
/// </para>
/// <para>
/// The vote locks when every player on a faction is ready and nobody is still picking a faction,
/// or in multiplayer after <see cref="MultiplayerVoteSeconds"/>, so one idle player cannot hold the
/// match. Guests without the mod cannot vote and are not waited for. In singleplayer there is no
/// clock: CONTINUE locks it.
/// </para>
/// </remarks>
internal sealed class CommanderUnitBanService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float MultiplayerVoteSeconds = 90f;
    private const float ViewRefreshSeconds = 0.25f;
    private const float RosterRefreshSeconds = 10f;

    // A guest's ballot is sent again now and then, so a host that lost it (a reconnect) has it back.
    private const float BallotResendSeconds = 5f;

    /// <summary>The UFO ("???"), never offered by the mod on any mission. Missions list it under
    /// <c>restrictions</c>, but the base game only reads that at its own hangar screen, so the AIR
    /// window, factories and enemy commander would all offer it.</summary>
    private const string NeverBuyableKey = "UFO";

    private readonly Dictionary<Player, Ballot> ballots = new();
    private readonly List<Player> voters = new();
    private readonly List<List<string>> tallyScratch = new();
    private readonly List<string> sideBans = new();
    private readonly Ballot localBallot = new();
    private readonly List<UnitDefinition> roster = new();
    private readonly List<VehicleDefinition> vehicleScratch = new();
    private readonly HashSet<string> banned = new(StringComparer.Ordinal);

    private readonly List<string> hostBans = new();

    private bool opened;
    private float deadline = -1f;
    private int hostMaxBans;
    private float nextViewAt;
    private float nextRosterAt;
    private float nextResendAt;
    private float nextRestrictAt;

    internal CommanderUnitBanService()
    {
        Instance = this;
        CommanderNetwork.OnHost("ban.set", HostSetBallot);
        CommanderNetwork.OnSync(SyncBans);
        CommanderNetwork.OnGuest("ban", ApplySync);
    }

    internal static CommanderUnitBanService? Instance { get; private set; }

    internal static bool IsVoting => Instance?.Phase == BanPhase.Voting;

    internal static bool AnyBanned => Instance != null && Instance.banned.Count > 0;

    /// <summary>Singleplayer: CONTINUE locks the bans at once, there is nobody to wait for.</summary>
    internal static bool IsSolo => !CommanderEnemyCommanderService.IsMultiplayerMatch;

    internal BanPhase Phase { get; private set; }

    /// <summary>
    /// Votes per ballot and bans per side, any mix of ground, air and naval: the host's setting, never
    /// below 0 and never more than the units there are to ban. A guest has the host's from the sync.
    /// </summary>
    internal int MaxBans => CommanderNetwork.IsGuest
        ? hostMaxBans
        : Mathf.Clamp(CommanderSettings.UnitBanVoteCount, 0, roster.Count);

    /// <summary>Seconds until a hosted vote locks by itself, negative when there is no clock.</summary>
    internal float SecondsLeft => deadline < 0f ? -1f : Mathf.Max(0f, deadline - Time.unscaledTime);

    /// <summary>This side's votes per unit, the side's ballots summed. Read by the window.</summary>
    internal Dictionary<string, int> TeamTally { get; } = new(StringComparer.Ordinal);

    internal int TeamReady { get; private set; }

    internal int TeamTotal { get; private set; }

    internal int OthersReady { get; private set; }

    internal int OthersTotal { get; private set; }

    /// <summary>Every unit that can be banned: ground vehicles, aircraft, then ships, cheapest first in each.</summary>
    internal IReadOnlyList<UnitDefinition> Roster => roster;

    /// <summary>This machine's ballot: the host's own, or a guest's copy.</summary>
    internal Ballot Mine
    {
        get
        {
            if (!CommanderNetwork.IsGuest && GameManager.GetLocalPlayer(out Player player) && player != null)
            {
                return GetBallot(player);
            }

            return localBallot;
        }
    }

    public void ResetSession()
    {
        ballots.Clear();
        voters.Clear();
        roster.Clear();
        banned.Clear();
        TeamTally.Clear();
        localBallot.Keys.Clear();
        localBallot.Ready = false;
        Phase = BanPhase.Off;
        hostMaxBans = 0;
        opened = false;
        hostBans.Clear();
        deadline = -1f;
        nextViewAt = 0f;
        nextRosterAt = 0f;
        nextResendAt = 0f;
        TeamReady = TeamTotal = OthersReady = OthersTotal = 0;
    }

    public void TickPersistent()
    {
        if (!CommanderNetwork.IsGuest)
        {
            TickHost();
        }
        else if (Phase == BanPhase.Voting && CommanderNetwork.HostHasMod && CommanderScheduler.IsDueRealtime(ref nextResendAt, BallotResendSeconds))
        {
            SendBallot();
        }

        // Hangars and a guest's copy of the map arrive after mission load, so the roster is read again.
        if (Phase == BanPhase.Voting && CommanderScheduler.IsDueRealtime(ref nextRosterAt, RosterRefreshSeconds))
        {
            RefreshRoster();
        }
    }

    /// <summary>The host's list from the settings page, as known definitions in the order picked.</summary>
    internal static List<UnitDefinition> ReadHostBans()
    {
        List<UnitDefinition> found = new();
        string[] keys = CommanderSettings.HostBannedUnits.Split(',');
        for (int i = 0; i < keys.Length; i++)
        {
            UnitDefinition? definition = CommanderNetwork.Definition<UnitDefinition>(keys[i].Trim());
            if (definition != null && !found.Contains(definition))
            {
                found.Add(definition);
            }
        }

        return found;
    }

    /// <summary>The unit is banned for the match.</summary>
    internal static bool IsBanned(UnitDefinition? definition)
    {
        return definition != null
            && (definition.jsonKey == NeverBuyableKey
                || (Instance != null && Instance.banned.Count > 0 && Instance.banned.Contains(definition.jsonKey)));
    }

    /// <summary>A depot or hangar may not put this unit out: it is banned, or the vote is still open.</summary>
    internal static bool Holds(UnitDefinition? definition)
    {
        return IsVoting || IsBanned(definition);
    }

    internal static bool IsAir(UnitDefinition? definition)
    {
        return definition is AircraftDefinition;
    }

    /// <summary>0 ground, 1 air, 2 naval: the window's tabs and colours, and the roster's order.</summary>
    internal static int KindOf(UnitDefinition? definition)
    {
        return definition switch
        {
            AircraftDefinition => 1,
            ShipDefinition => 2,
            _ => 0,
        };
    }

    internal static string GetLabel(UnitDefinition definition)
    {
        return definition is VehicleDefinition vehicle ? CommanderGameAccess.GetVehicleLabel(vehicle) : definition.unitName;
    }

    /// <summary>
    /// A side's bans out of its players' ballots: the <paramref name="limit"/> most voted units of any
    /// kind, a tie going to the unit voted for first. Pure, so <see cref="SelfCheck"/> can run it at load.
    /// </summary>
    internal static void Tally(List<List<string>> ballots, int limit, Dictionary<string, int> counts, List<string> into)
    {
        counts.Clear();
        into.Clear();
        List<string> order = new();
        for (int i = 0; i < ballots.Count; i++)
        {
            for (int k = 0; k < ballots[i].Count; k++)
            {
                string key = ballots[i][k];
                if (counts.TryGetValue(key, out int have))
                {
                    counts[key] = have + 1;
                }
                else
                {
                    counts[key] = 1;
                    order.Add(key);
                }
            }
        }

        for (int taken = 0; taken < limit; taken++)
        {
            string? best = null;
            for (int i = 0; i < order.Count; i++)
            {
                string key = order[i];
                if (!into.Contains(key) && (best == null || counts[key] > counts[best]))
                {
                    best = key;
                }
            }

            if (best == null)
            {
                break;
            }

            into.Add(best);
        }
    }

    /// <summary>The one runnable check of the vote: most votes win whatever their kind, ties go to the first, the limit holds.</summary>
    internal static void SelfCheck()
    {
        List<List<string>> ballots = new()
        {
            new List<string> { "TankA", "TankB", "JetA" },
            new List<string> { "TankC", "TankB", "JetB", "JetA" },
            new List<string> { "TankC", "JetC" },
        };
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        List<string> result = new();
        Tally(ballots, 3, counts, result);
        string joined = string.Join(",", result);
        Tally(ballots, 0, counts, result);
        if (joined != "TankB,JetA,TankC" || result.Count != 0 || counts["TankC"] != 2 || counts["JetC"] != 1)
        {
            CommanderPlugin.Log.LogError($"Unit ban self-check FAILED: the tally came out as {joined}.");
            return;
        }

        CommanderPlugin.Log.LogInfo("Unit ban self-check passed.");
    }

    /// <summary>Votes for <paramref name="definition"/>, or takes the vote back. Unreadies the ballot.</summary>
    internal void Toggle(UnitDefinition definition)
    {
        if (Phase != BanPhase.Voting)
        {
            return;
        }

        Ballot mine = Mine;
        if (!mine.Keys.Remove(definition.jsonKey))
        {
            if (mine.Keys.Count >= MaxBans)
            {
                CommanderNotice.Fail(MaxBans == 0
                    ? "THE HOST ALLOWS NO VOTED BANS"
                    : $"{MaxBans} BAN{(MaxBans == 1 ? string.Empty : "S")} AT MOST: TAKE ONE BACK FIRST");
                return;
            }

            mine.Keys.Add(definition.jsonKey);
        }

        mine.Ready = false;
        OnLocalEdit();
    }

    internal void Clear()
    {
        if (Phase != BanPhase.Voting)
        {
            return;
        }

        Mine.Keys.Clear();
        Mine.Ready = false;
        OnLocalEdit();
    }

    internal void ToggleReady()
    {
        if (Phase != BanPhase.Voting)
        {
            return;
        }

        Mine.Ready = !Mine.Ready;
        OnLocalEdit();
    }

    private static bool IsAirKey(string key)
    {
        return IsAir(CommanderNetwork.Definition<UnitDefinition>(key));
    }

    /// <summary>Nobody connected is still on the faction screen: readying alone must not lock out a guest still choosing a side.</summary>
    private static bool EveryoneOnAFaction()
    {
        NetworkServer? server = NetworkManagerNuclearOption.i?.Server;
        if (server == null || !server.Active)
        {
            return true;
        }

        IReadOnlyList<INetworkPlayer> players = server.AuthenticatedPlayers;
        for (int i = 0; i < players.Count; i++)
        {
            NetworkIdentity? identity = players[i].Identity;
            if (identity == null || !identity.TryGetComponent(out Player player) || player.HQ == null)
            {
                return false;
            }
        }

        return true;
    }

    private bool HasAllRestricted(FactionHQ hq)
    {
        foreach (string key in banned)
        {
            if (IsAirKey(key) && !hq.restrictedAircraft.Contains(key))
            {
                return false;
            }
        }

        return true;
    }

    private Ballot GetBallot(Player player)
    {
        if (!ballots.TryGetValue(player, out Ballot ballot))
        {
            ballot = new Ballot();
            ballots[player] = ballot;
        }

        return ballot;
    }

    private void OnLocalEdit()
    {
        nextViewAt = 0f;
        if (CommanderNetwork.IsGuest)
        {
            SendBallot();
        }
    }

    private void SendBallot()
    {
        CommanderNetwork.Forward("ban.set", CommanderNetwork.Flag(localBallot.Ready), string.Join(",", localBallot.Keys));
    }

    private void TickHost()
    {
        if (Phase == BanPhase.Off)
        {
            // Once per mission, as soon as it has factions. The host's list is in force from here.
            if (opened || FactionRegistry.GetAllHQs().Count == 0)
            {
                return;
            }

            opened = true;
            List<UnitDefinition> fromSettings = ReadHostBans();
            for (int i = 0; i < fromSettings.Count; i++)
            {
                hostBans.Add(fromSettings[i].jsonKey);
                banned.Add(fromSettings[i].jsonKey);
            }

            // A pilots' build has no RTS mode to vote from, and would hold every spawn until the
            // clock ran out.
            if (CommanderSettings.UnitBansEnabled)
            {
                Phase = BanPhase.Voting;
                deadline = IsSolo ? -1f : Time.unscaledTime + MultiplayerVoteSeconds;
                RefreshRoster();
                CommanderPlugin.Log.LogInfo("Unit ban vote opened: nothing spawns from a depot or hangar until it locks.");
            }
            else if (banned.Count > 0)
            {
                Phase = BanPhase.Locked;
                ApplyRestrictedAircraft();
                Announce();
            }

            return;
        }

        if (Phase == BanPhase.Locked)
        {
            // The mission load clears each faction's list, and it can run after the bans were put
            // in it; a check every few seconds puts them back.
            if (CommanderScheduler.IsDueRealtime(ref nextRestrictAt, 5f))
            {
                ApplyRestrictedAircraft();
            }

            return;
        }

        if (Phase != BanPhase.Voting)
        {
            return;
        }

        // A hosted match that started solo and was joined still gets its clock.
        if (deadline < 0f && !IsSolo)
        {
            deadline = Time.unscaledTime + MultiplayerVoteSeconds;
        }

        CollectVoters();
        if (CommanderScheduler.IsDueRealtime(ref nextViewAt, ViewRefreshSeconds))
        {
            FactionHQ? localHq = CommanderGameAccess.GetPlayerHq();
            if (localHq != null)
            {
                ReadSide(localHq, TeamTally, out int teamReady, out int teamTotal, out int othersReady, out int othersTotal);
                TeamReady = teamReady;
                TeamTotal = teamTotal;
                OthersReady = othersReady;
                OthersTotal = othersTotal;
            }
        }

        bool everyoneReady = voters.Count > 0 && EveryoneOnAFaction();
        for (int i = 0; i < voters.Count && everyoneReady; i++)
        {
            everyoneReady = GetBallot(voters[i]).Ready;
        }

        if (everyoneReady || (deadline >= 0f && Time.unscaledTime >= deadline))
        {
            Lock();
        }
    }

    /// <summary>Players who can vote: on a faction, and running the mod (the host's own player, or a modded guest).</summary>
    private void CollectVoters()
    {
        voters.Clear();
        GameManager.GetLocalPlayer(out Player localPlayer);
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }

            List<Player> players = hq.GetPlayers(sortByScore: false);
            for (int i = 0; i < players.Count; i++)
            {
                Player player = players[i];
                if (player != null
                    && (ReferenceEquals(player, localPlayer) || (player.Owner != null && CommanderNetwork.IsModdedGuest(player.Owner))))
                {
                    voters.Add(player);
                }
            }
        }
    }

    /// <summary><paramref name="hq"/>'s tally and ready count, and how ready everyone else is.</summary>
    private void ReadSide(FactionHQ hq, Dictionary<string, int> tally, out int teamReady, out int teamTotal, out int othersReady, out int othersTotal)
    {
        teamReady = teamTotal = othersReady = othersTotal = 0;
        tallyScratch.Clear();
        for (int i = 0; i < voters.Count; i++)
        {
            Ballot ballot = GetBallot(voters[i]);
            if (ReferenceEquals(voters[i].HQ, hq))
            {
                teamTotal++;
                teamReady += ballot.Ready ? 1 : 0;
                tallyScratch.Add(ballot.Keys);
            }
            else
            {
                othersTotal++;
                othersReady += ballot.Ready ? 1 : 0;
            }
        }

        Tally(tallyScratch, MaxBans, tally, sideBans);
    }

    private void Lock()
    {
        CollectVoters();
        banned.Clear();
        for (int i = 0; i < hostBans.Count; i++)
        {
            banned.Add(hostBans[i]);
        }

        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }

            ReadSide(hq, counts, out _, out _, out _, out _);
            for (int i = 0; i < sideBans.Count; i++)
            {
                banned.Add(sideBans[i]);
            }
        }

        Phase = BanPhase.Locked;
        deadline = -1f;
        ApplyRestrictedAircraft();
        Announce();
    }

    private void ApplyRestrictedAircraft()
    {
        // The game's own per-faction list hides a banned aircraft in the spawn menu, guests without
        // the mod included; it is a SyncVar, so it takes a new list rather than an edited one.
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !hq.IsServer || HasAllRestricted(hq))
            {
                continue;
            }

            List<string> restricted = new(hq.restrictedAircraft);
            foreach (string key in banned)
            {
                if (IsAirKey(key) && !restricted.Contains(key))
                {
                    restricted.Add(key);
                }
            }

            hq.NetworkrestrictedAircraft = restricted;
        }
    }

    private void Announce()
    {
        List<string> names = new();
        foreach (string key in banned)
        {
            UnitDefinition? definition = CommanderNetwork.Definition<UnitDefinition>(key);
            names.Add(definition != null ? GetLabel(definition).ToUpperInvariant() : key);
        }

        string text = names.Count == 0 ? "UNIT BANS LOCKED: NOTHING BANNED" : "UNIT BANS LOCKED: " + string.Join(", ", names);
        CommanderPlugin.Log.LogInfo(text);
        CommanderAlertService.Instance?.NotifyCapture(text);
    }

    /// <summary>Ground vehicles a depot can put out and aircraft a hangar on the map can, cheapest first in each.</summary>
    private void RefreshRoster()
    {
        roster.Clear();
        CommanderGameAccess.TryGetLocalVehicleDefinitions(vehicleScratch);
        for (int i = 0; i < vehicleScratch.Count; i++)
        {
            if (!string.Equals(CommanderGameAccess.GetVehicleCategoryLabel(vehicleScratch[i]), "Trailer", StringComparison.Ordinal)
                && !IsBanned(vehicleScratch[i]))
            {
                roster.Add(vehicleScratch[i]);
            }
        }

        foreach (Hangar hangar in UnityEngine.Object.FindObjectsOfType<Hangar>())
        {
            AircraftDefinition[] available = hangar.GetAvailableAircraft();
            for (int i = 0; i < available.Length; i++)
            {
                if (available[i] != null && !IsBanned(available[i]) && !roster.Contains(available[i]))
                {
                    roster.Add(available[i]);
                }
            }
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.ships != null)
        {
            for (int i = 0; i < encyclopedia.ships.Count; i++)
            {
                ShipDefinition ship = encyclopedia.ships[i];
                if (ship != null
                    && ship.unitPrefab != null
                    && ship.IsAllowed(includeEventContent: false)
                    && ship.unitPrefab.GetComponent<Ship>() != null
                    && !IsBanned(ship)
                    && !roster.Contains(ship))
                {
                    roster.Add(ship);
                }
            }
        }

        roster.Sort(static (a, b) =>
        {
            int kind = KindOf(a).CompareTo(KindOf(b));
            return kind != 0 ? kind : a.value.CompareTo(b.value);
        });
    }

    private void HostSetBallot(FactionHQ hq, string[] args)
    {
        Player? sender = CommanderNetwork.SenderPlayer;
        if (Phase != BanPhase.Voting || sender == null)
        {
            return;
        }

        Ballot ballot = GetBallot(sender);
        ballot.Ready = CommanderNetwork.Bool(args, 1);
        ballot.Keys.Clear();
        string[] keys = CommanderNetwork.Arg(args, 2).Split(',');
        for (int i = 0; i < keys.Length; i++)
        {
            UnitDefinition? definition = CommanderNetwork.Definition<UnitDefinition>(keys[i]);
            if (definition is not (VehicleDefinition or AircraftDefinition or ShipDefinition)
                || IsBanned(definition)
                || ballot.Keys.Contains(keys[i]))
            {
                continue;
            }

            if (ballot.Keys.Count < MaxBans)
            {
                ballot.Keys.Add(keys[i]);
            }
        }

        nextViewAt = 0f;
    }

    private void SyncBans(FactionHQ hq, List<string[]> messages)
    {
        if (Phase == BanPhase.Off)
        {
            return;
        }

        Dictionary<string, int> tally = new(StringComparer.Ordinal);
        int teamReady = 0;
        int teamTotal = 0;
        int othersReady = 0;
        int othersTotal = 0;
        if (Phase == BanPhase.Voting)
        {
            ReadSide(hq, tally, out teamReady, out teamTotal, out othersReady, out othersTotal);
        }

        messages.Add(new[]
        {
            "ban",
            CommanderNetwork.Num((int)Phase),
            CommanderNetwork.Num(SecondsLeft),
            CommanderNetwork.Num(teamReady),
            CommanderNetwork.Num(teamTotal),
            CommanderNetwork.Num(othersReady),
            CommanderNetwork.Num(othersTotal),
            CommanderDraftService.Serialize(tally),
            string.Join(",", banned),
            CommanderNetwork.Num(MaxBans),
        });
    }

    private void ApplySync(string[] args)
    {
        int phase = CommanderNetwork.Int(args, 1);
        BanPhase previous = Phase;
        Phase = Enum.IsDefined(typeof(BanPhase), phase) ? (BanPhase)phase : BanPhase.Off;
        float left = CommanderNetwork.Float(args, 2);
        deadline = left < 0f || Phase != BanPhase.Voting ? -1f : Time.unscaledTime + left;
        TeamReady = CommanderNetwork.Int(args, 3);
        TeamTotal = CommanderNetwork.Int(args, 4);
        OthersReady = CommanderNetwork.Int(args, 5);
        OthersTotal = CommanderNetwork.Int(args, 6);
        TeamTally.Clear();
        foreach (KeyValuePair<string, int> vote in CommanderDraftService.Parse(CommanderNetwork.Arg(args, 7)))
        {
            TeamTally[vote.Key] = vote.Value;
        }

        hostMaxBans = Mathf.Max(0, CommanderNetwork.Int(args, 9));
        banned.Clear();
        string[] keys = CommanderNetwork.Arg(args, 8).Split(',');
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].Length > 0)
            {
                banned.Add(keys[i]);
            }
        }

        if (previous == BanPhase.Off && Phase == BanPhase.Voting)
        {
            RefreshRoster();
        }
        else if (previous == BanPhase.Voting && Phase == BanPhase.Locked)
        {
            Announce();
        }
    }

    /// <summary>One player's votes and whether they have locked them in.</summary>
    internal sealed class Ballot
    {
        internal List<string> Keys { get; } = new();

        internal bool Ready { get; set; }
    }
}
