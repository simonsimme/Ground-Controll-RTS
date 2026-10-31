using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Tells the player what is happening to the faction while they are not looking: units taking
/// fire, units lost, kills scored and reinforcements arriving. Runs whether or not Commander
/// mode is active, because the whole point is to find out while you are flying.
/// </summary>
internal sealed class CommanderAlertService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float DamageThrottleSeconds = 0.5f;
    private const float AlertCooldownSeconds = 25f;
    private const float AlertLifetimeSeconds = 14f;
    private const float AttackerMemorySeconds = 20f;
    private const float HqRefreshSeconds = 2f;
    private const int MaxVisibleAlerts = 4;
    private const int MaxLogEntries = 120;

    private readonly Dictionary<Unit, DamageState> damageStates = new();
    private readonly List<Alert> alerts = new();
    private readonly List<LogEntry> log = new();
    private readonly List<Unit> staleDamage = new();

    private FactionHQ? boundHq;
    private float nextHqRefreshAt;
    private float nextPruneAt;

    internal CommanderAlertService()
    {
        Instance = this;
        CommanderNetwork.OnGuest("alert", OnHostAlert);
    }

    internal enum LogKind
    {
        Attack,
        Loss,
        Kill,
        Arrival,
        Capture,
        Supply,
    }

    internal static CommanderAlertService? Instance { get; private set; }

    internal IReadOnlyList<Alert> Alerts => alerts;

    internal IReadOnlyList<LogEntry> Log => log;

    /// <summary>Runs every frame from persistent operations, so alerts survive leaving RTS mode.</summary>
    public void TickPersistent()
    {
        if (CommanderScheduler.IsDue(ref nextHqRefreshAt, HqRefreshSeconds))
        {
            RebindHq(CommanderGameAccess.GetLocalHq());
        }

        for (int i = alerts.Count - 1; i >= 0; i--)
        {
            if (Time.unscaledTime - alerts[i].RaisedAt > AlertLifetimeSeconds)
            {
                alerts.RemoveAt(i);
            }
        }

        if (!CommanderScheduler.IsDue(ref nextPruneAt, 10f) || damageStates.Count == 0)
        {
            return;
        }

        staleDamage.Clear();
        foreach (KeyValuePair<Unit, DamageState> entry in damageStates)
        {
            if (entry.Key == null
                || entry.Key.disabled
                || Time.unscaledTime - entry.Value.LastDamageAt > 60f)
            {
                staleDamage.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleDamage.Count; i++)
        {
            damageStates.Remove(staleDamage[i]);
        }
    }

    public void ResetSession()
    {
        RebindHq(null);
        CommanderCallSignPatches.Reset();
        damageStates.Clear();
        alerts.Clear();
        log.Clear();
    }

    /// <summary>The unit that most recently damaged <paramref name="unit"/>, used by guard orders.</summary>
    internal static Unit? GetRecentAttacker(Unit? unit)
    {
        if (unit == null
            || Instance == null
            || !Instance.damageStates.TryGetValue(unit, out DamageState state)
            || state.Attacker == null
            || state.Attacker.disabled
            || Time.unscaledTime - state.LastDamageAt > AttackerMemorySeconds)
        {
            return null;
        }

        return state.Attacker;
    }

    /// <summary>
    /// Called from the <c>Unit.RecordDamage</c> patch for every damage event in the world, so
    /// everything here has to stay cheap and bail out on the first test that fails.
    /// </summary>
    internal void NotifyDamage(Unit unit, PersistentID dealerId)
    {
        if (boundHq == null
            || unit == null
            || unit.disabled
            || !ReferenceEquals(unit.NetworkHQ, boundHq))
        {
            return;
        }

        if (!damageStates.TryGetValue(unit, out DamageState state))
        {
            state = new DamageState();
            damageStates[unit] = state;
        }

        if (Time.unscaledTime - state.LastDamageAt < DamageThrottleSeconds)
        {
            return;
        }

        state.LastDamageAt = Time.unscaledTime;
        if (dealerId.TryGetUnit(out Unit dealer) && dealer != null && !dealer.disabled)
        {
            state.Attacker = dealer;
        }

        if (!CommanderSettings.CombatAlerts
            || Time.unscaledTime - state.LastAlertAt < AlertCooldownSeconds)
        {
            return;
        }

        state.LastAlertAt = Time.unscaledTime;
        int group = CommanderGroupService.Instance?.GetGroupOf(unit) ?? 0;
        string label = group > 0
            ? $"GROUP {group} UNDER ATTACK"
            : $"{CommanderGameAccess.GetUnitLabel(unit).ToUpperInvariant()} UNDER ATTACK";
        Raise(LogKind.Attack, label, unit);
    }

    /// <summary>Called from the <c>Unit.ReportKilled</c> patch; the unit passed is the one that died.</summary>
    internal void NotifyKilled(Unit unit, Dictionary<PersistentID, float>? credit)
    {
        if (boundHq == null || unit == null || ReferenceEquals(unit.NetworkHQ, boundHq))
        {
            // Friendly losses come from the faction's own remove-unit event instead, which also
            // fires for a pure multiplayer client where ReportKilled never runs.
            return;
        }

        if (!CommanderGameAccess.ShouldAllowCommanderSelection(unit, boundHq)
            && unit is not Building)
        {
            return;
        }

        // Name the shooter when it was ours, which is what call signs are for.
        string by = CommanderGameAccess.GetTopDealer(credit).TryGetUnit(out Unit dealer)
            && ReferenceEquals(dealer.NetworkHQ, boundHq)
                ? $" (BY {CommanderGameAccess.GetUnitLabel(dealer).ToUpperInvariant()})"
                : string.Empty;
        AddLog(LogKind.Kill, $"KILLED {CommanderGameAccess.GetUnitLabel(unit).ToUpperInvariant()}{by}", unit);
    }

    /// <summary>
    /// A base changed hands. Raises a real toast regardless of
    /// <see cref="CommanderSettings.CombatAlerts"/>, since it is the biggest event in a match. The toast
    /// carries no unit (an airbase is not a <see cref="Unit"/>), so clicking it just dismisses it.
    /// </summary>
    internal void NotifyCapture(string text)
    {
        Raise(LogKind.Capture, text, null);
    }

    /// <summary>
    /// A capture order the player just gave. This one does raise a toast, and deliberately ignores
    /// <see cref="CommanderSettings.CombatAlerts"/>: it is the confirmation that a button press or
    /// a right-click did something, so it has to be on screen. Falls back to the log when there is
    /// no unit to anchor the toast to, because clicking a toast focuses its unit.
    /// </summary>
    internal void NotifyCaptureOrder(string text, Unit? anchor)
    {
        // A guest's order carried out on the host is not the host's news.
        if (CommanderNetwork.IsHandlingGuest || SendToOwner(capture: true, text, anchor))
        {
            return;
        }

        if (anchor == null)
        {
            AddLog(LogKind.Capture, text, null);
            return;
        }

        Raise(LogKind.Capture, text, anchor);
    }

    /// <summary>
    /// A depot running out, or a unit that could not be resupplied because one had. Like a capture
    /// order this ignores <see cref="CommanderSettings.CombatAlerts"/>: an aircraft parked on a
    /// field with nothing to load looks exactly like a broken order unless the player is told why.
    /// </summary>
    internal void NotifySupply(string text, Unit? anchor)
    {
        if (CommanderNetwork.IsHandlingGuest || SendToOwner(capture: false, text, anchor))
        {
            return;
        }

        Raise(LogKind.Supply, text, anchor);
    }

    /// <summary>A teammate's ping. Ignores <see cref="CommanderSettings.CombatAlerts"/>: a ping is a
    /// person asking for your attention, and it is the only sign of one a pilot gets off the map.</summary>
    internal void NotifyPing(string text, Unit? anchor)
    {
        Raise(LogKind.Arrival, text, anchor);
    }

    internal void DismissAlert(Alert alert)
    {
        alerts.Remove(alert);
    }

    /// <summary>
    /// Air missions and rearming run on the host whoever owns the unit, so without this a guest's
    /// aircraft reported to the host's screen and, on a dedicated server, to nobody. True when the
    /// unit is not the host's own team's and so nothing should show here.
    /// </summary>
    private static bool SendToOwner(bool capture, string text, Unit? anchor)
    {
        FactionHQ? owner = anchor != null ? anchor.NetworkHQ : null;
        if (owner == null || CommanderNetwork.IsGuest)
        {
            return false;
        }

        CommanderNetwork.SendToFaction(owner, "alert", CommanderNetwork.Flag(capture), text, CommanderNetwork.Id(anchor));
        return !ReferenceEquals(owner, GameManager.GetLocalHQ(out FactionHQ localHq) ? localHq : null);
    }

    private void RebindHq(FactionHQ? hq)
    {
        if (ReferenceEquals(hq, boundHq))
        {
            return;
        }

        if (boundHq != null)
        {
            boundHq.onRegisterUnit -= OnUnitRegistered;
            boundHq.onRemoveUnit -= OnUnitRemoved;
        }

        boundHq = hq;
        if (boundHq != null)
        {
            boundHq.onRegisterUnit += OnUnitRegistered;
            boundHq.onRemoveUnit += OnUnitRemoved;
        }
    }

    private void OnUnitRegistered(Unit unit)
    {
        if (unit == null || unit is Missile || Time.timeSinceLevelLoad < 10f)
        {
            return;
        }

        AddLog(LogKind.Arrival, $"{CommanderGameAccess.GetUnitLabel(unit).ToUpperInvariant()} READY", unit);
    }

    private void OnUnitRemoved(Unit unit)
    {
        if (unit == null || unit is Missile)
        {
            return;
        }

        damageStates.Remove(unit);
        int group = CommanderGroupService.Instance?.GetGroupOf(unit) ?? 0;
        string label = group > 0
            ? $"LOST {CommanderGameAccess.GetUnitLabel(unit).ToUpperInvariant()} (GROUP {group})"
            : $"LOST {CommanderGameAccess.GetUnitLabel(unit).ToUpperInvariant()}";
        if (CommanderSettings.CombatAlerts)
        {
            Raise(LogKind.Loss, label, unit);
        }
        else
        {
            AddLog(LogKind.Loss, label, unit);
        }
    }

    private void OnHostAlert(string[] args)
    {
        Unit? anchor = CommanderNetwork.TryUnit(args, 3, out Unit unit) ? unit : null;
        if (CommanderNetwork.Bool(args, 1))
        {
            NotifyCaptureOrder(CommanderNetwork.Arg(args, 2), anchor);
        }
        else
        {
            NotifySupply(CommanderNetwork.Arg(args, 2), anchor);
        }
    }

    private void Raise(LogKind kind, string text, Unit? unit)
    {
        for (int i = 0; i < alerts.Count; i++)
        {
            if (string.Equals(alerts[i].Text, text, System.StringComparison.Ordinal))
            {
                alerts[i].RaisedAt = Time.unscaledTime;
                AddLog(kind, text, unit);
                return;
            }
        }

        alerts.Add(new Alert(kind, text, unit));
        while (alerts.Count > MaxVisibleAlerts)
        {
            alerts.RemoveAt(0);
        }

        AddLog(kind, text, unit);
    }

    private void AddLog(LogKind kind, string text, Unit? unit)
    {
        log.Insert(0, new LogEntry(kind, text, unit));
        if (log.Count > MaxLogEntries)
        {
            log.RemoveAt(log.Count - 1);
        }
    }

    internal sealed class Alert
    {
        internal Alert(LogKind kind, string text, Unit? unit)
        {
            Kind = kind;
            Text = text;
            Unit = unit;
            RaisedAt = Time.unscaledTime;
        }

        internal LogKind Kind { get; }

        internal string Text { get; }

        internal Unit? Unit { get; }

        internal float RaisedAt { get; set; }
    }

    internal sealed class LogEntry
    {
        internal LogEntry(LogKind kind, string text, Unit? unit)
        {
            Kind = kind;
            Text = text;
            Unit = unit;
            MissionTime = Time.timeSinceLevelLoad;
        }

        internal LogKind Kind { get; }

        internal string Text { get; }

        internal Unit? Unit { get; }

        internal float MissionTime { get; }

        internal string Timestamp => $"{Mathf.FloorToInt(MissionTime / 60f):00}:{Mathf.FloorToInt(MissionTime % 60f):00}";
    }

    private sealed class DamageState
    {
        internal float LastDamageAt { get; set; }

        internal float LastAlertAt { get; set; }

        internal Unit? Attacker { get; set; }
    }
}
