using System.Collections.Generic;
using BepInEx.Configuration;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Team markers: the ping key marks the spot or unit under the cursor for everyone on the team,
/// drawn in the RTS view, on the map and over the cockpit (<see cref="CommanderCockpitMarkerUi"/>).
/// It is the one way a commander and the team's pilots can point each other at something, and the
/// base game has no equivalent. A tap pings with the obvious meaning (an enemy is ATTACK, a friendly
/// DEFEND, ground SCOUT); holding the key opens a wheel of six; Shift makes the marker permanent,
/// numbered so it can be talked about ("hit #3"); the key on an existing marker removes it. Outside
/// command mode the key works on the game's own map, so a pilot can answer from the cockpit.
/// Also holds the one standing objective marker the tutorial puts on the map.
/// </summary>
/// <remarks>
/// The host owns every team's list and assigns the ids and numbers. It sends the whole list to that
/// team's modded guests on every change, and again with each sync, so a player who joins late or
/// missed a message still gets the permanent markers. Guests age the temporary ones out themselves.
/// </remarks>
internal sealed class CommanderPingService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Per team, temporary and permanent together; the oldest temporary one makes room first.</summary>
    internal const int MaxPerTeam = 10;

    /// <summary>A release inside the dead zone sooner than this is a quick ping; later, it cancels.
    /// The wheel is drawn from here on, but a flick toward a kind picks it however short the press.</summary>
    internal const float WheelHoldSeconds = 0.25f;

    /// <summary>GUI pixels from the press point before the wheel picks a kind; inside it, release cancels.</summary>
    internal const float WheelDeadZone = 28f;

    internal const float WheelRadius = 92f;

    private const float RemoveReach = 34f;
    private const float AnnounceWithinSeconds = 3f;
    private const int StateFields = 9;

    private static readonly string[] KindNames = { "ATTACK", "DEFEND", "MOVE", "DANGER", "SCOUT", "SUPPORT" };

    private static readonly List<Ping> PingList = new();

    private static int nextId = 1;
    private static bool wheelArmed;
    private static float pressedAt;
    private static Vector2 pressedGui;
    private static GlobalPosition pressedPosition;
    private static Unit? pressedUnit;

    private readonly CommanderMarkerService markerService;

    internal CommanderPingService(CommanderMarkerService markerService)
    {
        this.markerService = markerService;
        CommanderNetwork.OnHost("ping", HostPing);
        CommanderNetwork.OnHost("ping.remove", HostRemove);
        CommanderNetwork.OnGuest("ping.state", GuestState);
        CommanderNetwork.OnSync(SyncPings);
    }

    /// <summary>Clockwise from the top of the wheel.</summary>
    internal enum PingKind
    {
        Attack,
        Defend,
        Move,
        Danger,
        Scout,
        Support,
    }

    internal static IReadOnlyList<Ping> Pings => PingList;

    /// <summary>The tutorial's current objective, if it has one.</summary>
    internal static GlobalPosition? Objective { get; private set; }

    internal static string ObjectiveLabel { get; private set; } = string.Empty;

    /// <summary>Pings this player has placed this match. The tutorial asks.</summary>
    internal static int LocalPingCount { get; private set; }

    /// <summary>The wheel is up: the key has been held past a tap.</summary>
    internal static bool WheelOpen => wheelArmed && Time.unscaledTime - pressedAt >= WheelHoldSeconds;

    internal static Vector2 WheelCenter => pressedGui;

    /// <summary>The kind the wheel would place on release, or null inside the dead zone.</summary>
    internal static PingKind? WheelHover => SectorOf(CursorGui() - pressedGui);

    internal static bool ShiftHeld => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    private static bool CtrlHeld => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

    public void TickPersistent()
    {
        // Command mode pings anywhere; outside it, only the game's own map has a cursor to ping with.
        bool commandView = CommanderPlugin.Instance?.IsCommanderModeActive == true;
        if (!commandView && !DynamicMap.mapMaximized)
        {
            wheelArmed = false;
            return;
        }

        KeyboardShortcut key = CommanderSettings.PingKey;
        if (!CommanderShortcutInput.IsTyping && CommanderShortcutInput.IsDown(key))
        {
            // The key on a marker takes it off; Ctrl still means "remove only", never place.
            if (RemoveUnderCursor() || CtrlHeld)
            {
                return;
            }

            if (TryResolveCursor(commandView, out GlobalPosition position, out Unit? unit))
            {
                wheelArmed = true;
                pressedAt = Time.unscaledTime;
                pressedGui = CursorGui();
                pressedPosition = position;
                pressedUnit = unit;
            }

            return;
        }

        if (!wheelArmed || Input.GetKey(key.MainKey))
        {
            return;
        }

        wheelArmed = false;
        PingKind? hover = WheelHover;
        bool open = WheelOpen;
        PingKind? kind = hover ?? (open ? null : QuickKind(pressedUnit));
        CommanderPlugin.Log.LogInfo(
            $"Ping: held {Time.unscaledTime - pressedAt:0.00} s, wheel {(open ? "open" : "closed")}, "
            + $"cursor moved {(CursorGui() - pressedGui).magnitude:0} px, unit {(pressedUnit != null ? pressedUnit.name : "none")}, "
            + $"{(kind is PingKind chosen ? KindName(chosen) : "cancelled")}{(ShiftHeld ? ", permanent" : string.Empty)}.");
        if (kind is PingKind picked)
        {
            Place(pressedPosition, pressedUnit, picked, ShiftHeld);
        }
    }

    public void ResetSession()
    {
        PingList.Clear();
        ClearObjective();
        LocalPingCount = 0;
        nextId = 1;
        wheelArmed = false;
    }

    internal static string KindName(PingKind kind)
    {
        return KindNames[(int)kind];
    }

    /// <summary>"MOVE B": the kind, and for a move ping its letter, so two of them are not both "MOVE".</summary>
    internal static string NameOf(Ping ping)
    {
        return ping.Letter > 0 ? $"{KindName(ping.Kind)} {(char)('A' + ((ping.Letter - 1) % 26))}" : KindName(ping.Kind);
    }

    /// <summary>The sender's name after <paramref name="lead"/>; nothing for "Player", the game's stand-in when it has no Steam name.</summary>
    internal static string SenderLabel(Ping ping, string lead)
    {
        return ping.From == "Player" ? string.Empty : lead + ping.From.ToUpperInvariant();
    }

    /// <summary>A temporary ping with the obvious meaning, exactly as a tap of the ping key does.</summary>
    internal static void Place(GlobalPosition position, Unit? unit)
    {
        Place(position, unit, QuickKind(unit), permanent: false);
    }

    internal static void Place(GlobalPosition position, Unit? unit, PingKind kind, bool permanent)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        LocalPingCount++;
        string kindArg = CommanderNetwork.Num((int)kind);
        string permanentArg = CommanderNetwork.Flag(permanent);
        if (CommanderNetwork.Forward("ping", CommanderNetwork.Position(position), CommanderNetwork.Id(unit), kindArg, permanentArg))
        {
            return;
        }

        AddForTeam(hq, position, unit, kind, permanent, LocalName());
    }

    /// <summary>Whether this player's team sees the ping. The host keeps every team's list.</summary>
    internal static bool IsVisible(Ping ping)
    {
        return ping.Hq == null || ReferenceEquals(ping.Hq, CommanderGameAccess.GetPlayerHq());
    }

    internal static void SetObjective(GlobalPosition position, string label)
    {
        Objective = position;
        ObjectiveLabel = label;
    }

    internal static void ClearObjective()
    {
        Objective = null;
        ObjectiveLabel = string.Empty;
    }

    /// <summary>Where a ping is now: on its unit while the unit lives and is seen, else where it was put.</summary>
    internal static GlobalPosition CurrentPosition(Ping ping)
    {
        Unit? unit = ping.Unit;
        FactionHQ? hq = CommanderGameAccess.GetPlayerHq();
        if (unit != null && !unit.disabled && hq != null)
        {
            if (CommanderGameAccess.IsFriendlyUnit(unit, hq))
            {
                ping.Position = unit.GlobalPosition();
            }
            else if (hq.TryGetKnownPosition(unit, out GlobalPosition known))
            {
                ping.Position = known;
            }
        }

        return ping.Position;
    }

    internal static void Prune()
    {
        for (int i = PingList.Count - 1; i >= 0; i--)
        {
            if (PingList[i].Expired)
            {
                PingList.RemoveAt(i);
            }
        }
    }

    /// <summary>Asserts the wheel's sectors and the marker numbering. Run at plugin load.</summary>
    internal static void SelfCheck()
    {
        bool sectors = SectorOf(new Vector2(0f, -WheelRadius)) == PingKind.Attack
            && SectorOf(new Vector2(WheelRadius, -WheelRadius * 0.55f)) == PingKind.Defend
            && SectorOf(new Vector2(0f, WheelRadius)) == PingKind.Danger
            && SectorOf(new Vector2(-WheelRadius, -WheelRadius * 0.55f)) == PingKind.Support
            && SectorOf(new Vector2(3f, 3f)) == null;

        List<Ping> list = new()
        {
            new Ping(1, PingKind.Attack, 0f, 1, null, default, null, string.Empty, 0f),
            new Ping(2, PingKind.Attack, 60f, 3, null, default, null, string.Empty, 0f),
            new Ping(3, PingKind.Attack, 60f, 2, null, default, null, string.Empty, 5f),
        };
        bool numbering = LowestFree(list, null, NumberOf) == 4 && Evictable(list, null) == list[1];
        list.RemoveAt(1);
        numbering &= LowestFree(list, null, NumberOf) == 3 && LowestFree(list, null, LetterOf) == 1;

        if (sectors && numbering)
        {
            CommanderPlugin.Log.LogInfo("Ping self-check passed.");
        }
        else
        {
            CommanderPlugin.Log.LogError($"Ping self-check FAILED: sectors {sectors}, numbering {numbering}.");
        }
    }

    private static PingKind QuickKind(Unit? unit)
    {
        if (unit == null)
        {
            return PingKind.Scout;
        }

        return CommanderGameAccess.IsFriendlyUnit(unit, CommanderGameAccess.GetLocalHq()) ? PingKind.Defend : PingKind.Attack;
    }

    /// <summary>GUI-space offset from the wheel's centre to the kind under it, null inside the dead zone.</summary>
    private static PingKind? SectorOf(Vector2 delta)
    {
        if (delta.sqrMagnitude < WheelDeadZone * WheelDeadZone)
        {
            return null;
        }

        // GUI y runs down, so -y is up; kind 0 sits at the top and the rest follow clockwise.
        float degrees = Mathf.Atan2(delta.x, -delta.y) * Mathf.Rad2Deg;
        int sector = Mathf.RoundToInt(Mathf.Repeat(degrees, 360f) / 60f) % KindNames.Length;
        return (PingKind)sector;
    }

    private static Vector2 CursorGui()
    {
        return CommanderUiScale.ScreenToGui(Input.mousePosition);
    }

    private static string LocalName()
    {
        return GameManager.GetLocalPlayer(out Player player) && player != null
            ? player.GetDisplayName(PlayerNameContext.Other)
            : "COMMANDER";
    }

    private static int CountFor(List<Ping> list, FactionHQ? hq)
    {
        int count = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i].Hq, hq))
            {
                count++;
            }
        }

        return count;
    }

    private static int NumberOf(Ping ping)
    {
        return ping.Number;
    }

    private static int LetterOf(Ping ping)
    {
        return ping.Letter;
    }

    /// <summary>The lowest marker number (or move letter) the team is not using, so they stay short and get reused.</summary>
    private static int LowestFree(List<Ping> list, FactionHQ? hq, System.Func<Ping, int> slot)
    {
        for (int number = 1; ; number++)
        {
            bool used = false;
            for (int i = 0; i < list.Count && !used; i++)
            {
                used = ReferenceEquals(list[i].Hq, hq) && slot(list[i]) == number;
            }

            if (!used)
            {
                return number;
            }
        }
    }

    /// <summary>The team's oldest temporary marker, or with none its oldest permanent one.</summary>
    private static Ping? Evictable(List<Ping> list, FactionHQ? hq)
    {
        Ping? oldestTemporary = null;
        Ping? oldestPermanent = null;
        for (int i = 0; i < list.Count; i++)
        {
            Ping ping = list[i];
            if (!ReferenceEquals(ping.Hq, hq))
            {
                continue;
            }

            if (ping.Permanent)
            {
                oldestPermanent ??= ping;
            }
            else
            {
                oldestTemporary ??= ping;
            }
        }

        return oldestTemporary ?? oldestPermanent;
    }

    /// <summary>Host and singleplayer: adds the marker to the team's list and tells the team.</summary>
    private static void AddForTeam(FactionHQ hq, GlobalPosition position, Unit? unit, PingKind kind, bool permanent, string from)
    {
        Prune();
        while (CountFor(PingList, hq) >= MaxPerTeam && Evictable(PingList, hq) is Ping evicted)
        {
            PingList.Remove(evicted);
        }

        float lifetime = permanent ? 0f : Mathf.Max(5f, CommanderSettings.PingSeconds);
        Ping ping = new(nextId++, kind, lifetime, LowestFree(PingList, hq, NumberOf), hq, position, unit, from, Time.unscaledTime)
        {
            Letter = kind == PingKind.Move ? LowestFree(PingList, hq, LetterOf) : 0,
        };

        PingList.Add(ping);
        if (ReferenceEquals(CommanderGameAccess.GetPlayerHq(), hq))
        {
            Announce(ping);
        }

        BroadcastState(hq);
    }

    private static void RemoveForTeam(FactionHQ hq, int id)
    {
        for (int i = 0; i < PingList.Count; i++)
        {
            if (PingList[i].Id == id && ReferenceEquals(PingList[i].Hq, hq))
            {
                PingList.RemoveAt(i);
                BroadcastState(hq);
                return;
            }
        }
    }

    /// <summary>The order flash at the spot, and an alert when it is a teammate's: in the cockpit most of all.</summary>
    private static void Announce(Ping ping)
    {
        CommanderOrderPing.Show(ping.Position, attack: ping.Kind == PingKind.Attack || ping.Kind == PingKind.Danger);
        if (ping.From == LocalName())
        {
            return;
        }

        string marker = ping.Permanent ? $"MARKER #{ping.Number} " : string.Empty;
        string target = ping.Unit != null ? $": {CommanderGameAccess.GetUnitLabel(ping.Unit).ToUpperInvariant()}" : string.Empty;
        CommanderAlertService.Instance?.NotifyPing($"{marker}{NameOf(ping)}{SenderLabel(ping, " FROM ")}{target}", ping.Unit);
    }

    /// <summary>Removes the marker drawn nearest the cursor, the one this frame's draw pass put there.
    /// False when there was none.</summary>
    private static bool RemoveUnderCursor()
    {
        Vector2 cursor = CursorGui();
        Ping? nearest = null;
        float best = RemoveReach * RemoveReach;
        for (int i = 0; i < PingList.Count; i++)
        {
            Ping ping = PingList[i];
            float distance = Mathf.Min(
                DrawnDistance(ping.LastGui, ping.LastDrawnAt, cursor),
                DrawnDistance(ping.LastMapGui, ping.LastMapDrawnAt, cursor));
            if (IsVisible(ping) && distance < best)
            {
                best = distance;
                nearest = ping;
            }
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (nearest == null || hq == null)
        {
            return false;
        }

        if (!CommanderNetwork.Forward("ping.remove", CommanderNetwork.Num(nearest.Id)))
        {
            RemoveForTeam(hq, nearest.Id);
            return true;
        }

        // Gone here at once; the host's list follows.
        PingList.Remove(nearest);
        return true;
    }

    /// <summary>Squared GUI distance from the cursor to where a ping was drawn just now; never drawn is infinitely far.</summary>
    private static float DrawnDistance(Vector2 drawnAt, float drawnTime, Vector2 cursor)
    {
        return Time.unscaledTime - drawnTime < 0.25f ? (drawnAt - cursor).sqrMagnitude : float.MaxValue;
    }

    private static List<string> StateArgs(FactionHQ hq)
    {
        List<string> args = new();
        for (int i = 0; i < PingList.Count; i++)
        {
            Ping ping = PingList[i];
            if (!ReferenceEquals(ping.Hq, hq) || ping.Expired)
            {
                continue;
            }

            args.Add(CommanderNetwork.Num(ping.Id));
            args.Add(CommanderNetwork.Num((int)ping.Kind));
            args.Add(CommanderNetwork.Num(ping.Lifetime));
            args.Add(CommanderNetwork.Num(ping.Number));
            args.Add(CommanderNetwork.Position(ping.Position));
            args.Add(CommanderNetwork.Id(ping.Unit));
            args.Add(ping.From);
            args.Add(CommanderNetwork.Num(ping.Age));
            args.Add(CommanderNetwork.Num(ping.Letter));
        }

        return args;
    }

    private static void BroadcastState(FactionHQ hq)
    {
        CommanderNetwork.SendToFaction(hq, "ping.state", StateArgs(hq).ToArray());
    }

    private static void SyncPings(FactionHQ hq, List<string[]> messages)
    {
        List<string> args = StateArgs(hq);
        args.Insert(0, "ping.state");
        messages.Add(args.ToArray());
    }

    private static void HostPing(FactionHQ hq, string[] args)
    {
        if (!CommanderNetwork.TryPosition(args, 1, out GlobalPosition position))
        {
            return;
        }

        Unit? unit = CommanderNetwork.TryUnit(args, 2, out Unit found) ? found : null;
        int kind = Mathf.Clamp(CommanderNetwork.Int(args, 3), 0, KindNames.Length - 1);
        Player? sender = CommanderNetwork.SenderPlayer;
        string from = sender != null ? sender.GetDisplayName(PlayerNameContext.Other) : "TEAMMATE";
        AddForTeam(hq, position, unit, (PingKind)kind, CommanderNetwork.Bool(args, 4), from);
    }

    private static void HostRemove(FactionHQ hq, string[] args)
    {
        RemoveForTeam(hq, CommanderNetwork.Int(args, 1));
    }

    /// <summary>The team's whole list from the host. Markers already held keep their object, so a
    /// followed unit and the last draw point carry over; a new one under a few seconds old is news.</summary>
    private static void GuestState(string[] args)
    {
        List<Ping> fresh = new();
        for (int i = 1; i + StateFields <= args.Length; i += StateFields)
        {
            int id = CommanderNetwork.Int(args, i);
            Ping? known = null;
            for (int k = 0; k < PingList.Count && known == null; k++)
            {
                known = PingList[k].Id == id ? PingList[k] : null;
            }

            if (known != null)
            {
                fresh.Add(known);
                continue;
            }

            if (!CommanderNetwork.TryPosition(args, i + 4, out GlobalPosition position))
            {
                continue;
            }

            int kind = Mathf.Clamp(CommanderNetwork.Int(args, i + 1), 0, KindNames.Length - 1);
            float age = CommanderNetwork.Float(args, i + 7);
            Ping ping = new(
                id,
                (PingKind)kind,
                CommanderNetwork.Float(args, i + 2),
                CommanderNetwork.Int(args, i + 3),
                null,
                position,
                CommanderNetwork.TryUnit(args, i + 5, out Unit unit) ? unit : null,
                CommanderNetwork.Arg(args, i + 6),
                Time.unscaledTime - age)
            {
                Letter = CommanderNetwork.Int(args, i + 8),
            };
            fresh.Add(ping);
            if (age < AnnounceWithinSeconds)
            {
                Announce(ping);
            }
        }

        PingList.Clear();
        PingList.AddRange(fresh);
    }

    private bool TryResolveCursor(bool commandView, out GlobalPosition position, out Unit? unit)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        unit = null;
        position = default;
        if (hq == null)
        {
            return false;
        }

        Vector2 mouse = Input.mousePosition;
        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map != null && DynamicMap.mapMaximized && map.IsCursorInMapRectangle())
        {
            if (!map.TryGetCursorCoordinates(out position))
            {
                return false;
            }

            if (CommanderInputController.TryGetMapUnitUnderCursor(map, out Unit mapUnit))
            {
                unit = mapUnit;
                position = mapUnit.GlobalPosition();
            }
        }
        else if (!commandView)
        {
            return false;
        }
        else if (markerService.TryGetMarkerUnitAt(mouse, out Unit marked)
            || CommanderGameAccess.TryRaycastHostileUnit(mouse, out marked)
            || CommanderGameAccess.TryRaycastSelectableUnit(mouse, out marked))
        {
            unit = marked;
            position = marked.GlobalPosition();
        }
        else if (!CommanderGameAccess.TryRaycastWorldPosition(mouse, out position))
        {
            return false;
        }

        // A contact is pinned where the team last saw it, never where it really is.
        if (unit != null && !CommanderGameAccess.IsFriendlyUnit(unit, hq) && hq.TryGetKnownPosition(unit, out GlobalPosition known))
        {
            position = known;
        }

        return true;
    }

    internal sealed class Ping
    {
        internal Ping(int id, PingKind kind, float lifetime, int number, FactionHQ? hq, GlobalPosition position, Unit? unit, string from, float placedAt)
        {
            Id = id;
            Kind = kind;
            Lifetime = lifetime;
            Number = number;
            Hq = hq;
            Position = position;
            Unit = unit;
            From = from;
            PlacedAt = placedAt;
        }

        internal int Id { get; }

        /// <summary>A move ping's letter, 1 for A; 0 on every other kind.</summary>
        internal int Letter { get; set; }

        internal PingKind Kind { get; }

        /// <summary>Seconds it lasts; 0 for a permanent marker.</summary>
        internal float Lifetime { get; }

        /// <summary>The number the team calls it by, lowest free in that team.</summary>
        internal int Number { get; }

        /// <summary>The team it belongs to, on the host; null on a guest, whose list is its own team's.</summary>
        internal FactionHQ? Hq { get; }

        internal GlobalPosition Position { get; set; }

        internal Unit? Unit { get; }

        internal string From { get; }

        internal float PlacedAt { get; }

        /// <summary>Where it was last drawn in the 3D view or cockpit and when, so the ping key can pick it off the screen.</summary>
        internal Vector2 LastGui { get; set; }

        internal float LastDrawnAt { get; set; } = -1f;

        /// <summary>The same for the map, which draws a ping a second time while it is open.</summary>
        internal Vector2 LastMapGui { get; set; }

        internal float LastMapDrawnAt { get; set; } = -1f;

        internal bool Permanent => Lifetime <= 0f;

        internal float Age => Time.unscaledTime - PlacedAt;

        internal float Remaining => Permanent ? float.MaxValue : Lifetime - Age;

        internal bool Expired => !Permanent && Age >= Lifetime;
    }
}
