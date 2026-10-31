using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace GroundControlRts;

/// <summary>Where a Draft match is. Off on every other mission.</summary>
internal enum DraftPhase
{
    Off,
    Drafting,
    Live,
}

/// <summary>The draft screen's tabs, and the colour bands of its composition bar.</summary>
internal enum DraftGroup
{
    Armor,
    Light,
    Artillery,
    AirDefence,
    Support,
    Aircraft,
}

/// <summary>
/// Ground Control Draft: no money and no production during the match. Before it starts each side
/// spends a points budget on a fixed army, air wing and supply stock, and that is everything it
/// gets. Repair, resupply and pilot recovery are the only ways to keep a force alive, and the match
/// ends when one side runs out of fighting units (<see cref="CommanderVictoryService"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A drafted unit is the game's own reserve.</b> Vehicles and airframes go into
/// <c>FactionHQ.VehicleSupply</c> / <c>AircraftSupply</c>, which is what the depot queue and the AIR
/// window already spend first and for free (<c>CommanderFactionVehicleService.CanAcquire</c>,
/// <c>SpawnMission</c>), and what the enemy commander's launches take out of stock. So the draft
/// adds a front door and nothing downstream: the player's reserve is queued at their depots, the
/// AI's is rolled out by <c>FactionHQ.DeployVehicles</c>, and the air wing waits in stock.
/// </para>
/// <para>
/// <b>No money is a clamp, not a hundred guards.</b> Every server HQ's <c>factionFunds</c> is held at
/// zero for the whole mission, so every purchase path in the mod (BUILD, BUY, repair crews,
/// upgrades) refuses for the reason it already knows how to give. The mission JSON authors no
/// income, no kill reward and no join allowance, so pilots have no personal money either.
/// </para>
/// <para>
/// Host-authoritative. A guest edits a local copy and sends the whole pick list (<c>draft.set</c>);
/// the host fits it to the budget and syncs it back with the phase, budget and clock (<c>draft</c>).
/// </para>
/// </remarks>
internal sealed class CommanderDraftService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Picks per entry. A cap on a typo, not on a strategy.</summary>
    internal const int MaxPerEntry = 60;

    /// <summary>Used until the mission's authored starting balance can be read.</summary>
    private const float DefaultBudget = 3000f;

    /// <summary>A hosted draft locks in on its own after this, so one idle team cannot hold the match.</summary>
    private const float MultiplayerDraftSeconds = 240f;

    /// <summary>A team that turns up late gets at least this long to pick.</summary>
    private const float LateJoinerSeconds = 60f;

    private const float LocalEditQuietSeconds = 3f;

    private const float RosterRefreshSeconds = 10f;

    private static readonly float[] BudgetPresets = { 1500f, 3000f, 5000f, 8000f };

    private static readonly List<VehicleDefinition> VehicleScratch = new();

    private readonly Dictionary<FactionHQ, FactionDraft> drafts = new();
    private readonly Dictionary<FactionHQ, List<DraftEntry>> rosters = new();
    private readonly HashSet<FactionHQ> kitPlaced = new();
    private readonly List<FactionHQ> hqScratch = new();
    private readonly FactionDraft guestDraft = new();

    /// <summary>
    /// Host: launches each side's drafted airframes still have paid for. A drafted aeroplane's first
    /// sortie leaves with the fuel and ordnance its points bought; one that came back into stock (a
    /// recovery, or a pilot a helicopter brought home) takes its load off the shelves like any other
    /// resupply. Ground units never return to stock, so every ground roll-out is prepaid.
    /// </summary>
    private readonly Dictionary<FactionHQ, Dictionary<AircraftDefinition, int>> prepaidLaunches = new();

    private FactionHQ? rosterHq;
    private float nextRosterRefreshAt;
    private float liveSeenAt = -1f;
    private float localEditAt = -100f;
    private float deadline = -1f;
    private int othersReady;
    private int othersTotal;
    private bool budgetFromMission;
    private bool announcedLive;

    internal CommanderDraftService()
    {
        Instance = this;
        CommanderNetwork.OnHost("draft.set", HostSetPicks);
        CommanderNetwork.OnHost("draft.ready", HostSetReady);
        CommanderNetwork.OnSync(SyncDraft);
        CommanderNetwork.OnGuest("draft", ApplySync);
    }

    internal static CommanderDraftService? Instance { get; private set; }

    /// <summary>The mission is a draft, which switches off income, factories and every purchase.</summary>
    internal static bool IsDraftMission =>
        CommanderFeatureGate.MissionName.IndexOf("Ground Control Draft", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>True between mission load and the battle starting.</summary>
    internal static bool IsDrafting => Instance?.Phase == DraftPhase.Drafting;

    /// <summary>True once the drafted armies are on the field.</summary>
    internal static bool IsLive => Instance?.Phase == DraftPhase.Live;

    /// <summary>Singleplayer: READY starts the battle at once, there is nobody to wait for.</summary>
    internal static bool IsSolo => !CommanderEnemyCommanderService.IsMultiplayerMatch;

    internal static bool HasLastDraft => CommanderSettings.DraftLastPicks.Length > 0;

    internal DraftPhase Phase { get; private set; }

    internal float Budget { get; private set; } = DefaultBudget;

    /// <summary>Seconds until a hosted draft locks in, negative when there is no clock.</summary>
    internal float SecondsLeft => deadline < 0f ? -1f : Mathf.Max(0f, deadline - Time.unscaledTime);

    /// <summary>Whether this machine may change the budget: the host, before the battle.</summary>
    internal bool CanChangeBudget => Phase == DraftPhase.Drafting && !CommanderNetwork.IsGuest;

    internal string OpponentStatus => othersTotal == 0
        ? "OPPONENT: DRAFTING"
        : othersReady >= othersTotal
            ? "OPPONENT: READY"
            : othersTotal == 1 ? "OPPONENT: DRAFTING" : $"TEAMS READY {othersReady}/{othersTotal}";

    internal bool OpponentReady => othersTotal > 0 && othersReady >= othersTotal;

    /// <summary>This machine's side: the host's own draft, or a guest's copy.</summary>
    internal FactionDraft Mine
    {
        get
        {
            if (CommanderNetwork.IsGuest)
            {
                return guestDraft;
            }

            FactionHQ? hq = GetRealLocalHq();
            return hq != null ? GetOrCreate(hq) : guestDraft;
        }
    }

    public void ResetSession()
    {
        drafts.Clear();
        rosters.Clear();
        kitPlaced.Clear();
        prepaidLaunches.Clear();
        nextRosterRefreshAt = 0f;
        guestDraft.Picks.Clear();
        guestDraft.Ready = false;
        guestDraft.AutoPicked = false;
        rosterHq = null;
        Phase = DraftPhase.Off;
        Budget = DefaultBudget;
        budgetFromMission = false;
        deadline = -1f;
        liveSeenAt = -1f;
        localEditAt = -100f;
        othersReady = 0;
        othersTotal = 0;
        announcedLive = false;
    }

    public void TickPersistent()
    {
        if (!IsDraftMission)
        {
            return;
        }

        if (!CommanderNetwork.IsGuest)
        {
            TickHost();
        }

        // What a side may fly depends on the fields it holds, and a guest's copy of those arrives
        // over the network: the roster is re-read now and then until the battle starts.
        if (Phase == DraftPhase.Drafting && CommanderScheduler.IsDueRealtime(ref nextRosterRefreshAt, RosterRefreshSeconds))
        {
            rosters.Clear();
        }

        TickLocalLive();
    }

    /// <summary>Whether this launch is one a drafted airframe's points already paid the load for.</summary>
    internal static bool HasPrepaidLaunch(FactionHQ? hq, UnitDefinition? definition)
    {
        return Instance != null
            && hq != null
            && definition is AircraftDefinition aircraft
            && Instance.prepaidLaunches.TryGetValue(hq, out Dictionary<AircraftDefinition, int> left)
            && left.TryGetValue(aircraft, out int count)
            && count > 0;
    }

    /// <summary>Spends one of <see cref="HasPrepaidLaunch"/>'s launches. Host only, at the launch itself.</summary>
    internal static void UsePrepaidLaunch(FactionHQ hq, AircraftDefinition definition)
    {
        if (HasPrepaidLaunch(hq, definition))
        {
            Dictionary<AircraftDefinition, int> left = Instance!.prepaidLaunches[hq];
            left[definition]--;
        }
    }

    internal static float CostOf(IReadOnlyList<DraftEntry> roster, Dictionary<string, int> picks)
    {
        float cost = 0f;
        for (int i = 0; i < roster.Count; i++)
        {
            if (picks.TryGetValue(roster[i].Key, out int count) && count > 0)
            {
                cost += roster[i].Cost * count;
            }
        }

        return cost;
    }

    /// <summary>
    /// Fits a pick list to a budget: entries are taken in the order given and each is cut to what
    /// is left. Deterministic, so the host and a guest agree on what survives, and pure, so
    /// <see cref="SelfCheck"/> can run it at load.
    /// </summary>
    internal static void FitToBudget(List<(string Key, int Count, float Cost)> picks, float budget, Dictionary<string, int> into)
    {
        into.Clear();
        float left = budget + 0.001f;
        for (int i = 0; i < picks.Count; i++)
        {
            (string key, int count, float cost) = picks[i];
            if (count <= 0 || cost <= 0f)
            {
                continue;
            }

            int take = Mathf.Min(Mathf.Min(count, MaxPerEntry), Mathf.FloorToInt(left / cost));
            if (take <= 0)
            {
                continue;
            }

            into[key] = take;
            left -= take * cost;
        }
    }

    internal static string Serialize(Dictionary<string, int> picks)
    {
        StringBuilder builder = new();
        foreach (KeyValuePair<string, int> pick in picks)
        {
            if (pick.Value <= 0 || pick.Key.IndexOf(',') >= 0 || pick.Key.IndexOf('=') >= 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(pick.Key).Append('=').Append(pick.Value.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    internal static Dictionary<string, int> Parse(string text)
    {
        Dictionary<string, int> picks = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return picks;
        }

        string[] parts = text.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            int split = parts[i].LastIndexOf('=');
            if (split <= 0
                || !int.TryParse(parts[i].Substring(split + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                || count <= 0)
            {
                continue;
            }

            string key = parts[i].Substring(0, split);
            picks[key] = (picks.TryGetValue(key, out int have) ? have : 0) + count;
        }

        return picks;
    }

    /// <summary>
    /// The one runnable check of the draft's rules: a pick list survives the wire, a list over the
    /// budget is cut in order and never ends up over it, and nothing is ever handed out free.
    /// </summary>
    internal static void SelfCheck()
    {
        Dictionary<string, int> picks = new(StringComparer.Ordinal) { ["MBT1"] = 3, ["SAM1"] = 2, ["Truck"] = 1 };
        Dictionary<string, int> back = Parse(Serialize(picks));
        if (back.Count != 3 || back["MBT1"] != 3 || back["SAM1"] != 2 || back["Truck"] != 1)
        {
            CommanderPlugin.Log.LogError("Draft self-check FAILED: a pick list does not survive the wire.");
            return;
        }

        List<(string Key, int Count, float Cost)> wanted = new()
        {
            ("MBT1", 5, 100f),
            ("Truck", 4, 30f),
            ("Free", 3, 0f),
        };
        Dictionary<string, int> fitted = new(StringComparer.Ordinal);
        FitToBudget(wanted, 400f, fitted);
        float spent = (fitted.TryGetValue("MBT1", out int tanks) ? tanks * 100f : 0f)
            + (fitted.TryGetValue("Truck", out int trucks) ? trucks * 30f : 0f);
        if (tanks != 4 || trucks != 0 || spent > 400f || fitted.ContainsKey("Free"))
        {
            CommanderPlugin.Log.LogError(
                $"Draft self-check FAILED: fitting to a budget of 400 gave {tanks} tanks and {trucks} trucks ({spent:0}).");
            return;
        }

        FitToBudget(wanted, 0f, fitted);
        if (fitted.Count != 0)
        {
            CommanderPlugin.Log.LogError("Draft self-check FAILED: a budget of nothing still drafts something.");
        }
    }

    /// <summary>The draft screen's roster for this machine's side.</summary>
    internal IReadOnlyList<DraftEntry> GetLocalRoster()
    {
        FactionHQ? hq = GetRealLocalHq();
        if (hq == null)
        {
            return Array.Empty<DraftEntry>();
        }

        // Rebuilt when the side changes (a player switching teams in the lobby).
        if (!ReferenceEquals(rosterHq, hq))
        {
            rosters.Remove(hq);
            rosterHq = hq;
        }

        return GetRoster(hq);
    }

    internal int GetCount(string key)
    {
        return Mine.Picks.TryGetValue(key, out int count) ? count : 0;
    }

    internal float GetSpent()
    {
        return CostOf(GetLocalRoster(), Mine.Picks);
    }

    /// <summary>How many more of <paramref name="entry"/> fit, by budget, cap and shelf space.</summary>
    internal int GetRoomFor(DraftEntry entry)
    {
        if (Phase != DraftPhase.Drafting || entry.Cost <= 0f)
        {
            return 0;
        }

        int have = GetCount(entry.Key);
        int byBudget = Mathf.FloorToInt((Budget - GetSpent() + 0.001f) / entry.Cost);
        int byCap = MaxPerEntry - have;
        return Mathf.Max(0, Mathf.Min(byBudget, byCap));
    }

    internal void Add(DraftEntry entry, int count)
    {
        count = Mathf.Min(count, GetRoomFor(entry));
        if (count <= 0)
        {
            return;
        }

        Mine.Picks[entry.Key] = GetCount(entry.Key) + count;
        OnLocalEdit();
    }

    internal void Remove(DraftEntry entry, int count)
    {
        int have = GetCount(entry.Key);
        if (Phase != DraftPhase.Drafting || have <= 0)
        {
            return;
        }

        int left = Mathf.Max(0, have - count);
        if (left == 0)
        {
            Mine.Picks.Remove(entry.Key);
        }
        else
        {
            Mine.Picks[entry.Key] = left;
        }

        OnLocalEdit();
    }

    internal void Clear()
    {
        if (Phase != DraftPhase.Drafting || Mine.Picks.Count == 0)
        {
            return;
        }

        Mine.Picks.Clear();
        OnLocalEdit();
    }

    /// <summary>Spends whatever is left the way the enemy commander drafts, around what is picked.</summary>
    internal void AutoFill()
    {
        FactionHQ? hq = GetRealLocalHq();
        if (Phase != DraftPhase.Drafting || hq == null)
        {
            return;
        }

        CommanderEnemyCommanderService.DraftArmy(hq, Budget, GetRoster(hq), Mine.Picks);
        OnLocalEdit();
    }

    /// <summary>The draft this player last took into battle, fitted to this match's roster and budget.</summary>
    internal void LoadLast()
    {
        FactionHQ? hq = GetRealLocalHq();
        if (Phase != DraftPhase.Drafting || hq == null || !HasLastDraft)
        {
            return;
        }

        Dictionary<string, int> loaded = Parse(CommanderSettings.DraftLastPicks);
        Mine.Picks.Clear();
        foreach (KeyValuePair<string, int> pick in Sanitize(hq, loaded))
        {
            Mine.Picks[pick.Key] = pick.Value;
        }

        OnLocalEdit();
    }

    internal void ToggleReady()
    {
        if (Phase != DraftPhase.Drafting)
        {
            return;
        }

        FactionDraft mine = Mine;
        if (!mine.Ready && mine.Picks.Count == 0)
        {
            CommanderNotice.Fail("DRAFT SOMETHING FIRST, OR PRESS AUTO-FILL");
            return;
        }

        mine.Ready = !mine.Ready;
        if (CommanderNetwork.Forward("draft.ready", CommanderNetwork.Flag(mine.Ready)))
        {
            localEditAt = Time.unscaledTime;
        }
    }

    /// <summary>Host only: the next budget preset. Everybody's picks are fitted to it and unreadied.</summary>
    internal void CycleBudget()
    {
        if (!CanChangeBudget)
        {
            return;
        }

        int index = Array.IndexOf(BudgetPresets, Budget);
        Budget = BudgetPresets[(index + 1) % BudgetPresets.Length];
        foreach (KeyValuePair<FactionHQ, FactionDraft> entry in drafts)
        {
            Dictionary<string, int> fitted = Sanitize(entry.Key, entry.Value.Picks);
            entry.Value.Picks.Clear();
            foreach (KeyValuePair<string, int> pick in fitted)
            {
                entry.Value.Picks[pick.Key] = pick.Value;
            }

            // A side the quartermaster picked for is simply picked again at the new size.
            if (entry.Value.AutoPicked)
            {
                CommanderEnemyCommanderService.DraftArmy(entry.Key, Budget, GetRoster(entry.Key), entry.Value.Picks);
            }
            else
            {
                entry.Value.Ready = false;
            }
        }

        CommanderPlugin.Log.LogInfo($"Draft budget set to {Budget:0}.");
    }

    /// <summary>The host's side, not a guest being acted for: the one whose screen this is.</summary>
    private static FactionHQ? GetRealLocalHq()
    {
        return GameManager.GetLocalHQ(out FactionHQ hq) ? hq : null;
    }

    private static DraftGroup GetGroup(VehicleDefinition definition)
    {
        return definition.vehicleType switch
        {
            VehicleType.MBT or VehicleType.AFV => DraftGroup.Armor,
            VehicleType.LCV or VehicleType.UGV => DraftGroup.Light,
            VehicleType.ART => DraftGroup.Artillery,
            VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM => DraftGroup.AirDefence,
            _ => DraftGroup.Support,
        };
    }

    private static string DescribeVehicle(VehicleDefinition definition)
    {
        string detail = CommanderGameAccess.GetVehicleCategoryLabel(definition).ToUpperInvariant();
        GameObject? prefab = definition.unitPrefab;
        if (prefab != null && prefab.GetComponentInChildren<Repairer>(true) != null)
        {
            detail += "  |  REPAIRS";
        }

        if (prefab != null && prefab.GetComponentInChildren<Rearmer>(true) != null)
        {
            detail += "  |  CARRIES AMMO";
        }
        else if (CommanderSupplyStoreService.IsFuelTruckName(definition.unitName))
        {
            detail += "  |  CARRIES FUEL";
        }

        if (definition.captureStrength > 0f)
        {
            detail += "  |  CAPTURES";
        }

        if (CommanderGameAccess.IsTrailerVehicleDefinition(definition))
        {
            detail += "  |  NEEDS A TOW";
        }

        return detail;
    }

    private static string DescribeAircraft(AircraftDefinition definition)
    {
        bool rotary = !CommanderAirCommandService.HasPlanePilot(definition);
        string role = definition.captureCapacity > 0 && rotary
            ? "TRANSPORT"
            : definition.roleIdentity.antiAir > definition.roleIdentity.antiSurface ? "AIR-TO-AIR" : "STRIKE";
        float takeoff = definition.aircraftParameters != null ? definition.aircraftParameters.takeoffDistance : 0f;
        return (rotary ? "ROTARY" : "FIXED WING") + "  |  " + role
            + (!rotary && takeoff > 0f ? $"  |  {takeoff:0} M ROLL" : string.Empty);
    }

    private static bool IsLaunchableFrom(FactionHQ hq, AircraftDefinition definition)
    {
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (CommanderAirCommandService.IsCompatibleAirbase(airbase, hq, definition)
                && CommanderAirCommandService.HasTakeoffRunway(airbase, definition))
            {
                return true;
            }
        }

        return false;
    }

    private static void BuildRoster(FactionHQ hq, List<DraftEntry> into)
    {
        into.Clear();
        CommanderGameAccess.TryGetLocalVehicleDefinitions(VehicleScratch);
        for (int i = 0; i < VehicleScratch.Count; i++)
        {
            VehicleDefinition definition = VehicleScratch[i];

            // No uranium in a draft: a nuclear launcher could never be fielded.
            if (definition.value <= 0f
                || string.IsNullOrEmpty(definition.jsonKey)
                || CommanderFactionVehicleService.GetWarheadCost(definition) > 0)
            {
                continue;
            }

            into.Add(new DraftEntry(
                definition.jsonKey,
                CommanderGameAccess.GetVehicleLabel(definition),
                DescribeVehicle(definition),
                GetGroup(definition),
                definition.value,
                definition));
        }

        VehicleScratch.Clear();

        // The air wing is what the mission lets this side fly (its supplies list) and what one of
        // its own fields can put up: an airframe no strip here takes would sit in stock all match.
        foreach (KeyValuePair<AircraftDefinition, FactionHQ.RuntimeSupply> supply in hq.AircraftSupply)
        {
            AircraftDefinition definition = supply.Key;
            if (definition == null
                || definition.value <= 0f
                || string.IsNullOrEmpty(definition.jsonKey)
                || !CommanderAirCommandService.CanAiFly(definition)
                || !IsLaunchableFrom(hq, definition))
            {
                continue;
            }

            into.Add(new DraftEntry(
                definition.jsonKey,
                !string.IsNullOrWhiteSpace(definition.unitName) ? definition.unitName : definition.jsonKey,
                DescribeAircraft(definition),
                DraftGroup.Aircraft,
                CommanderEconomyService.PriceOf(definition),
                definition));
        }

        into.Sort(static (a, b) =>
        {
            int group = a.Group.CompareTo(b.Group);
            return group != 0 ? group : a.Cost.CompareTo(b.Cost);
        });
    }

    private static bool IsHumanSide(FactionHQ hq, FactionHQ? localHq)
    {
        return ReferenceEquals(hq, localHq) || CommanderEnemyCommanderService.HasHumanPlayers(hq);
    }

    private List<DraftEntry> GetRoster(FactionHQ hq)
    {
        if (!rosters.TryGetValue(hq, out List<DraftEntry> roster))
        {
            roster = new List<DraftEntry>();
            BuildRoster(hq, roster);
            rosters[hq] = roster;
        }

        return roster;
    }

    private FactionDraft GetOrCreate(FactionHQ hq)
    {
        if (!drafts.TryGetValue(hq, out FactionDraft draft))
        {
            draft = new FactionDraft();
            drafts[hq] = draft;
        }

        return draft;
    }

    /// <summary>A pick list with every unknown key dropped and the rest fitted to the budget and the shelves.</summary>
    private Dictionary<string, int> Sanitize(FactionHQ hq, Dictionary<string, int> picks)
    {
        List<DraftEntry> roster = GetRoster(hq);
        List<(string Key, int Count, float Cost)> ordered = new();
        foreach (KeyValuePair<string, int> pick in picks)
        {
            DraftEntry? entry = roster.Find(candidate => candidate.Key == pick.Key);
            if (entry != null)
            {
                ordered.Add((entry.Key, Mathf.Min(pick.Value, MaxPerEntry), entry.Cost));
            }
        }

        Dictionary<string, int> fitted = new(StringComparer.Ordinal);
        FitToBudget(ordered, Budget, fitted);
        return fitted;
    }

    private void OnLocalEdit()
    {
        FactionDraft mine = Mine;
        mine.Ready = false;
        mine.AutoPicked = false;
        localEditAt = Time.unscaledTime;
        CommanderNetwork.Forward("draft.set", Serialize(mine.Picks));
    }

    private void TickHost()
    {
        FactionHQ? localHq = GetRealLocalHq();
        hqScratch.Clear();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq.faction != null && hq.IsServer)
            {
                hqScratch.Add(hq);
            }
        }

        if (hqScratch.Count == 0)
        {
            return;
        }

        // No money, from mission load to the end: nothing may be bought before the draft, and
        // nothing earned during the battle. See the class remarks.
        for (int i = 0; i < hqScratch.Count; i++)
        {
            if (hqScratch[i].factionFunds != 0f)
            {
                hqScratch[i].SetFunds(0f);
            }
        }

        // Opened only once every side holds its base and its depots: the air wing on offer is what
        // those fields can launch, and a roster read a frame too early would offer none.
        if (Phase == DraftPhase.Off)
        {
            if (!IsMissionReady())
            {
                return;
            }

            BeginDrafting();
        }

        PlaceKits();
        if (Phase != DraftPhase.Drafting)
        {
            return;
        }

        int humans = 0;
        int ready = 0;
        for (int i = 0; i < hqScratch.Count; i++)
        {
            FactionHQ hq = hqScratch[i];
            FactionDraft draft = GetOrCreate(hq);
            if (!IsHumanSide(hq, localHq))
            {
                // Nobody commands this side (the enemy AI, or an empty team): the quartermaster
                // drafts for it, once, and it is ready.
                if (!draft.AutoPicked)
                {
                    draft.Picks.Clear();
                    CommanderEnemyCommanderService.DraftArmy(hq, Budget, GetRoster(hq), draft.Picks);
                    draft.AutoPicked = true;
                    draft.Ready = true;
                    CommanderPlugin.Log.LogInfo(
                        $"Draft: {hq.faction.factionName} drafted by the quartermaster: {Serialize(draft.Picks)}.");
                }

                continue;
            }

            // A player took over a side the quartermaster had picked for: its picks stay as a
            // starting point, and the player has to lock them in.
            if (draft.AutoPicked)
            {
                draft.AutoPicked = false;
                draft.Ready = false;
                if (deadline >= 0f)
                {
                    deadline = Mathf.Max(deadline, Time.unscaledTime + LateJoinerSeconds);
                }
            }

            humans++;
            ready += draft.Ready ? 1 : 0;
        }

        FactionDraft? mine = localHq != null ? GetOrCreate(localHq) : null;
        othersTotal = 0;
        othersReady = 0;
        for (int i = 0; i < hqScratch.Count; i++)
        {
            if (!ReferenceEquals(hqScratch[i], localHq))
            {
                othersTotal++;
                othersReady += GetOrCreate(hqScratch[i]).Ready ? 1 : 0;
            }
        }

        bool everyoneReady = humans > 0 && ready >= humans && (mine == null || mine.Ready);
        bool outOfTime = deadline >= 0f && Time.unscaledTime >= deadline && humans > 0;
        if (everyoneReady || outOfTime)
        {
            Deploy(outOfTime && !everyoneReady);
        }
    }

    private bool IsMissionReady()
    {
        if (hqScratch.Count < 2)
        {
            return false;
        }

        for (int i = 0; i < hqScratch.Count; i++)
        {
            if (CommanderVictoryService.CountBases(hqScratch[i]) == 0
                || !hqScratch[i].TryGetNearestDepot(Vector3.zero, float.MaxValue, out _))
            {
                return false;
            }
        }

        return true;
    }

    private void BeginDrafting()
    {
        FactionHQ? any = GetRealLocalHq() ?? hqScratch[0];
        budgetFromMission = CommanderEconomyService.TryGetAuthoredBalance(any, out float authored) && authored > 0f;
        Budget = budgetFromMission ? authored : DefaultBudget;
        deadline = CommanderEnemyCommanderService.IsMultiplayerMatch
            ? Time.unscaledTime + MultiplayerDraftSeconds
            : -1f;
        Phase = DraftPhase.Drafting;
        CommanderPlugin.Log.LogInfo(
            $"Draft: drafting opens, budget {Budget:0} ({(budgetFromMission ? "authored" : "default")})"
                + (deadline >= 0f ? $", locks in after {MultiplayerDraftSeconds:0} s." : "."));
    }

    /// <summary>
    /// A munitions bunker beside each side's first depot, once. The duel's buildings are two depots
    /// and three AA posts a side, so without it a drafted ammunition block would have no shelf to
    /// sit on. Put down during the draft so the picks can read the real shelf size.
    /// </summary>
    private void PlaceKits()
    {
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        if (economy == null)
        {
            return;
        }

        for (int i = 0; i < hqScratch.Count; i++)
        {
            FactionHQ hq = hqScratch[i];
            if (kitPlaced.Contains(hq) || !hq.TryGetNearestDepot(Vector3.zero, float.MaxValue, out VehicleDepot depot))
            {
                continue;
            }

            kitPlaced.Add(hq);
            bool placed = economy.PlaceDraftKit(hq, depot.GlobalPosition());
            rosters.Remove(hq);
            CommanderPlugin.Log.LogInfo(placed
                ? $"Draft: {hq.faction.factionName} given a munitions bunker beside {depot.name}."
                : $"Draft: no munitions bunker could be sited for {hq.faction.factionName}; its resupplies run free.");
        }
    }

    private void Deploy(bool timedOut)
    {
        FactionHQ? localHq = GetRealLocalHq();
        for (int i = 0; i < hqScratch.Count; i++)
        {
            FactionHQ hq = hqScratch[i];
            FactionDraft draft = GetOrCreate(hq);
            List<DraftEntry> roster = GetRoster(hq);

            // A side that let the clock run out with nothing picked is not sent to war empty.
            if (draft.Picks.Count == 0)
            {
                CommanderEnemyCommanderService.DraftArmy(hq, Budget, roster, draft.Picks);
                CommanderPlugin.Log.LogInfo($"Draft: {hq.faction.factionName} picked nothing; the quartermaster drafted for it.");
            }

            Dictionary<string, int> fitted = Sanitize(hq, draft.Picks);
            int ground = 0;
            int air = 0;
            foreach (KeyValuePair<string, int> pick in fitted)
            {
                DraftEntry? entry = roster.Find(candidate => candidate.Key == pick.Key);
                if (entry == null)
                {
                    continue;
                }

                if (entry.Definition != null)
                {
                    hq.ModifyUnitSupply(entry.Definition, pick.Value);
                    if (entry.Definition is AircraftDefinition aircraft)
                    {
                        if (!prepaidLaunches.TryGetValue(hq, out Dictionary<AircraftDefinition, int> prepaid))
                        {
                            prepaid = new Dictionary<AircraftDefinition, int>();
                            prepaidLaunches[hq] = prepaid;
                        }

                        prepaid[aircraft] = (prepaid.TryGetValue(aircraft, out int already) ? already : 0) + pick.Value;
                        air += pick.Value;
                    }
                    else
                    {
                        ground += pick.Value;
                    }
                }
            }

            CommanderPlugin.Log.LogInfo(
                $"Draft: {hq.faction.factionName} deploys {ground} ground units, {air} aircraft "
                    + $"({CostOf(roster, fitted):0}/{Budget:0}): {Serialize(fitted)}.");
        }

        if (localHq != null && drafts.TryGetValue(localHq, out FactionDraft mine))
        {
            CommanderSettings.DraftLastPicks = Serialize(mine.Picks);
        }

        Phase = DraftPhase.Live;
        deadline = -1f;
        CommanderPlugin.Log.LogInfo(timedOut ? "Draft: the clock ran out; the battle starts." : "Draft: every side is ready; the battle starts.");
    }

    /// <summary>
    /// Every client, for its own side: the player is told the battle is on. The reserve stays in
    /// stock until the player rolls it out from a depot (a click, or SPAWN RESERVE).
    /// </summary>
    private void TickLocalLive()
    {
        if (Phase != DraftPhase.Live)
        {
            return;
        }

        if (liveSeenAt < 0f)
        {
            liveSeenAt = Time.unscaledTime;
            if (CommanderNetwork.IsGuest)
            {
                CommanderSettings.DraftLastPicks = Serialize(guestDraft.Picks);
            }
        }

        if (!announcedLive && GetRealLocalHq() != null)
        {
            announcedLive = true;
            CommanderAlertService.Instance?.NotifyCapture(
                "BATTLE STARTS: ROLL YOUR ARMY OUT FROM A DEPOT, THE AIR WING WAITS AT AIR COMMAND");
        }
    }

    private void HostSetPicks(FactionHQ hq, string[] args)
    {
        if (Phase != DraftPhase.Drafting)
        {
            return;
        }

        FactionDraft draft = GetOrCreate(hq);
        Dictionary<string, int> fitted = Sanitize(hq, Parse(CommanderNetwork.Arg(args, 1)));
        draft.Picks.Clear();
        foreach (KeyValuePair<string, int> pick in fitted)
        {
            draft.Picks[pick.Key] = pick.Value;
        }

        draft.Ready = false;
        draft.AutoPicked = false;
    }

    private void HostSetReady(FactionHQ hq, string[] args)
    {
        if (Phase == DraftPhase.Drafting)
        {
            FactionDraft draft = GetOrCreate(hq);
            draft.Ready = CommanderNetwork.Bool(args, 1) && draft.Picks.Count > 0;
        }
    }

    private void SyncDraft(FactionHQ hq, List<string[]> messages)
    {
        if (!IsDraftMission || Phase == DraftPhase.Off)
        {
            return;
        }

        FactionDraft draft = GetOrCreate(hq);
        int total = 0;
        int ready = 0;
        foreach (KeyValuePair<FactionHQ, FactionDraft> entry in drafts)
        {
            if (!ReferenceEquals(entry.Key, hq))
            {
                total++;
                ready += entry.Value.Ready ? 1 : 0;
            }
        }

        messages.Add(new[]
        {
            "draft",
            CommanderNetwork.Num((int)Phase),
            CommanderNetwork.Num(Budget),
            CommanderNetwork.Num(SecondsLeft),
            CommanderNetwork.Flag(draft.Ready),
            CommanderNetwork.Num(ready),
            CommanderNetwork.Num(total),
            Serialize(draft.Picks),
        });
    }

    private void ApplySync(string[] args)
    {
        int phase = CommanderNetwork.Int(args, 1);
        Phase = Enum.IsDefined(typeof(DraftPhase), phase) ? (DraftPhase)phase : DraftPhase.Off;
        Budget = Mathf.Max(0f, CommanderNetwork.Float(args, 2));
        float left = CommanderNetwork.Float(args, 3);
        deadline = left < 0f || Phase != DraftPhase.Drafting ? -1f : Time.unscaledTime + left;
        othersReady = CommanderNetwork.Int(args, 5);
        othersTotal = CommanderNetwork.Int(args, 6);

        // The host's copy is the truth, but not while the player is clicking: a sync that left the
        // host before the last click would take that click back for a moment.
        if (Time.unscaledTime - localEditAt < LocalEditQuietSeconds)
        {
            return;
        }

        guestDraft.Ready = CommanderNetwork.Bool(args, 4);
        guestDraft.Picks.Clear();
        foreach (KeyValuePair<string, int> pick in Parse(CommanderNetwork.Arg(args, 7)))
        {
            guestDraft.Picks[pick.Key] = pick.Value;
        }
    }
}
