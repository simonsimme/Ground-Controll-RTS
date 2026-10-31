using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Radar warning for the player's AI aircraft, drawn as a ring round each one's icon on the
/// tactical map, switched by the map header's RADAR button: no ring means
/// no enemy radar is pinging it, a grey ring means something is painting it but the return is
/// too weak to see, and a yellow arc on the ring points at every radar that can see it. A lock
/// (a ground or ship radar that has picked it as its target) is orange, a missile seeker is red.
/// While the aircraft is being jammed the whole ring is yellow and shows no bearings at all.
///
/// It reads the game's own RWR event, <c>Aircraft.onRadarWarning</c>: the server sends
/// <c>RpcGetRadarWarning</c> to every observer on each radar sweep that touches the aircraft and
/// every client works out "detected" itself with <c>EstimateDetection</c>, so a guest sees the
/// same rings the host does and nothing here runs a radar check of its own. The bearing to the
/// emitter is what the cockpit RWR already shows a pilot, so drawing it is not a fog leak.
/// </summary>
internal static class CommanderRadarExposure
{
    /// <summary>A contact stays on the ring this long after its last ping. A search radar sweeps
    /// every few seconds and the game treats a contact as live for 4 s, so shorter flickers.</summary>
    private const float HoldSeconds = 5f;

    /// <summary>How long a fresh ping flashes brighter, so a radar that just swept reads as one.</summary>
    private const float FlashSeconds = 0.4f;

    private const int MaxContactsPerAircraft = 6;
    private const int RingSegments = 40;
    private const float ArcStepDegrees = 4f;
    private const float PruneIntervalSeconds = 10f;

    /// <summary>Just outside a map aircraft icon, in GUI pixels.</summary>
    private const float MapRingRadius = 14f;

    private static readonly Color HiddenRingColor = new(0.8f, 0.85f, 0.9f, 0.35f);
    private static readonly Color SeenColor = new(1f, 0.88f, 0.2f, 1f);
    private static readonly Color LockColor = new(1f, 0.5f, 0.15f, 1f);
    private static readonly Color SeekerColor = new(1f, 0.22f, 0.2f, 1f);

    private static readonly Dictionary<Aircraft, Action<Aircraft.OnRadarWarning>> Handlers = new();
    private static readonly Dictionary<Aircraft, List<Contact>> Contacts = new();
    private static readonly List<Aircraft> FriendlyAircraft = new();
    private static readonly List<Aircraft> DeadAircraft = new();
    private static readonly List<Arc> Arcs = new();
    private static readonly Dictionary<Aircraft, UnitMapIcon> MapIcons = new();
    private static readonly Dictionary<Aircraft, Action<Unit.JamEventArgs>> JamHandlers = new();
    private static readonly Dictionary<Aircraft, JamState> JamLevels = new();
    private static float nextPruneTime;

    internal static bool Enabled => CommanderSettings.RadarExposureRings;

    /// <summary>Drawn on the tactical map only, centred on the aircraft's map icon.</summary>
    internal static void Draw()
    {
        if (!Enabled)
        {
            return;
        }

        CommanderMarkerService.Instance?.CopyFriendlyAircraft(FriendlyAircraft);
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        float now = Time.time;
        for (int i = 0; i < FriendlyAircraft.Count; i++)
        {
            Aircraft aircraft = FriendlyAircraft[i];

            // A human pilot has the cockpit RWR; this is for the aircraft nobody is watching.
            if (aircraft.Player != null || aircraft.disabled)
            {
                continue;
            }

            // Hooked whether or not the map is open, so the ring is current the moment it opens.
            Hook(aircraft);
            if (map == null)
            {
                continue;
            }

            float jam = GetJamLevel(aircraft, now);
            if (jam > 0f)
            {
                if (TryGetIconScreen(map, aircraft, out Vector2 jammedScreen))
                {
                    DrawJammedRing(CommanderUiScale.ScreenToGui(jammedScreen), MapRingRadius, jam);
                }
            }
            else if (Contacts.TryGetValue(aircraft, out List<Contact> contacts)
                && TryGetIconScreen(map, aircraft, out Vector2 mapScreen))
            {
                DrawRing(aircraft, CommanderUiScale.ScreenToGui(mapScreen), MapRingRadius, contacts, now);
            }
        }

        if (CommanderScheduler.IsDueRealtime(ref nextPruneTime, PruneIntervalSeconds))
        {
            PruneDeadAircraft();
        }
    }

    /// <summary>Called from every <c>UnitMapIcon.UpdateIcon</c>, so the ring can sit on the icon itself.</summary>
    internal static void NoteMapIcon(Aircraft aircraft, UnitMapIcon icon)
    {
        if (Enabled && (Contacts.ContainsKey(aircraft) || JamLevels.ContainsKey(aircraft)))
        {
            MapIcons[aircraft] = icon;
        }
    }

    /// <summary>Unhooks everything. Called when RTS mode ends, so no event keeps a scene's aircraft alive.</summary>
    internal static void Reset()
    {
        foreach (KeyValuePair<Aircraft, Action<Aircraft.OnRadarWarning>> pair in Handlers)
        {
            pair.Key.onRadarWarning -= pair.Value;
        }

        foreach (KeyValuePair<Aircraft, Action<Unit.JamEventArgs>> pair in JamHandlers)
        {
            pair.Key.onJam -= pair.Value;
        }

        Handlers.Clear();
        JamHandlers.Clear();
        JamLevels.Clear();
        Contacts.Clear();
        MapIcons.Clear();
        FriendlyAircraft.Clear();
    }

    /// <summary>
    /// Where the aircraft's map icon is drawn, not where the aircraft is. <c>DynamicMap.UpdateIcons</c>
    /// moves only a fifth of the icons each frame, so an icon trails its aircraft by up to five
    /// frames, and at 2x or 4x speed that is far enough for a ring on the live position to slide off it.
    /// </summary>
    private static bool TryGetIconScreen(CommanderTacticalMapService map, Aircraft aircraft, out Vector2 screen)
    {
        if (MapIcons.TryGetValue(aircraft, out UnitMapIcon icon) && icon != null && icon.iconImage != null && icon.isActiveAndEnabled)
        {
            screen = icon.iconImage.transform.position;
            return map.TryWorldToMapScreen(aircraft.GlobalPosition(), out _);
        }

        return map.TryWorldToMapScreen(aircraft.GlobalPosition(), out screen);
    }

    private static void Hook(Aircraft aircraft)
    {
        if (Handlers.ContainsKey(aircraft))
        {
            return;
        }

        // The event's payload does not name the aircraft it fired on, so each one needs its own closure.
        void Handler(Aircraft.OnRadarWarning warning)
        {
            OnRadarWarning(aircraft, warning);
        }

        Action<Aircraft.OnRadarWarning> handler = Handler;
        aircraft.onRadarWarning += handler;
        Handlers[aircraft] = handler;

        void JamHandler(Unit.JamEventArgs jam)
        {
            OnJammed(aircraft, jam);
        }

        Action<Unit.JamEventArgs> jamHandler = JamHandler;
        aircraft.onJam += jamHandler;
        JamHandlers[aircraft] = jamHandler;
    }

    /// <summary>
    /// The cockpit's own jam meter, kept per aircraft: <c>CombatHUD_OnJam</c> adds half of every
    /// <c>jamAmount</c> and <c>CombatHUD</c> drains it by <c>max(level, 0.25)</c> per second. A
    /// <c>JammingPod</c> jams any aircraft it targets every 0.2 s through <c>Unit.Jam</c>, a
    /// ClientRpc, radar or no radar, so a Compass gets jammed exactly like a fighter.
    /// </summary>
    private static void OnJammed(Aircraft aircraft, Unit.JamEventArgs jam)
    {
        if (jam.jammingUnit != null && ReferenceEquals(jam.jammingUnit.NetworkHQ, aircraft.NetworkHQ))
        {
            return;
        }

        float now = Time.time;
        JamLevels[aircraft] = new JamState
        {
            Level = Mathf.Clamp01(GetJamLevel(aircraft, now) + (jam.jamAmount * 0.5f)),
            At = now,
        };
    }

    private static float GetJamLevel(Aircraft aircraft, float now)
    {
        return JamLevels.TryGetValue(aircraft, out JamState state) ? DrainJam(state.Level, now - state.At) : 0f;
    }

    /// <summary>
    /// The HUD's per-frame drain worked out in one step: exponential while the level is above 0.25,
    /// then a straight 0.25 per second, so nothing has to tick every aircraft every frame.
    /// </summary>
    private static float DrainJam(float level, float seconds)
    {
        if (level > 0.25f)
        {
            float toQuarter = Mathf.Log(level / 0.25f);
            if (seconds <= toQuarter)
            {
                return level * Mathf.Exp(-seconds);
            }

            seconds -= toQuarter;
            level = 0.25f;
        }

        return Mathf.Max(0f, level - (0.25f * seconds));
    }

    /// <summary>
    /// A jammed aircraft's warning receiver tells it nothing useful, the way the cockpit's markers
    /// shake and fade under jamming: the whole ring goes yellow and every bearing is dropped.
    /// </summary>
    private static void DrawJammedRing(Vector2 centre, float radius, float jam)
    {
        Color color = SeenColor;
        color.a = (0.5f + (0.5f * jam)) * UnityEngine.Random.Range(0.75f, 1f);
        DrawArc(centre, radius, 0f, 360f, RingSegments, color, 3f);
    }

    private static void OnRadarWarning(Aircraft aircraft, Aircraft.OnRadarWarning warning)
    {
        Unit emitter = warning.emitter;
        if (emitter == null || ReferenceEquals(emitter.NetworkHQ, aircraft.NetworkHQ))
        {
            return;
        }

        if (!Contacts.TryGetValue(aircraft, out List<Contact> contacts))
        {
            contacts = new List<Contact>(MaxContactsPerAircraft);
            Contacts[aircraft] = contacts;
        }

        bool seeker = emitter is Missile;
        float maxSignal = warning.radar != null ? warning.radar.RadarParameters.maxSignal : 0f;
        Contact contact = new()
        {
            Emitter = emitter,
            LastPing = Time.time,
            Detected = warning.detected,
            Locked = warning.isTarget,
            Seeker = seeker,
            Strength = maxSignal > 0f ? Mathf.Clamp01(warning.power / maxSignal) : (seeker ? 1f : 0f),
        };

        int oldest = -1;
        for (int i = 0; i < contacts.Count; i++)
        {
            if (ReferenceEquals(contacts[i].Emitter, emitter))
            {
                contacts[i] = contact;
                return;
            }

            if (oldest < 0 || contacts[i].LastPing < contacts[oldest].LastPing)
            {
                oldest = i;
            }
        }

        if (contacts.Count < MaxContactsPerAircraft)
        {
            contacts.Add(contact);
        }
        else
        {
            contacts[oldest] = contact;
        }
    }

    private static void DrawRing(Aircraft aircraft, Vector2 centre, float radius, List<Contact> contacts, float now)
    {
        bool anySeen = false;
        for (int i = contacts.Count - 1; i >= 0; i--)
        {
            Contact contact = contacts[i];
            if (now - contact.LastPing > HoldSeconds || contact.Emitter == null || contact.Emitter.disabled)
            {
                contacts.RemoveAt(i);
                continue;
            }

            anySeen |= contact.Detected || contact.Seeker;
        }

        if (contacts.Count == 0)
        {
            return;
        }

        Color ring = anySeen ? Color.Lerp(HiddenRingColor, SeenColor, 0.35f) : HiddenRingColor;
        DrawArc(centre, radius, 0f, 360f, RingSegments, ring, 1.5f);

        Arcs.Clear();
        Vector3 from = aircraft.transform.position;
        for (int i = 0; i < contacts.Count; i++)
        {
            Contact contact = contacts[i];

            // The map is north up: world x is east, z north, and GUI y runs down.
            Vector3 toEmitter = contact.Emitter.transform.position - from;
            Vector2 onScreen = new(toEmitter.x, -toEmitter.z);
            if (onScreen.sqrMagnitude < 1f)
            {
                continue;
            }

            float age = now - contact.LastPing;
            float fade = 1f - (age / HoldSeconds);
            float flash = age < FlashSeconds ? 1f - (age / FlashSeconds) : 0f;

            Color color = contact.Seeker ? SeekerColor : contact.Locked ? LockColor : SeenColor;
            float halfWidth = contact.Seeker || contact.Locked ? 22f : Mathf.Lerp(16f, 48f, contact.Strength);
            float thickness = contact.Seeker || contact.Locked ? 4f : 3f;
            if (!contact.Detected && !contact.Seeker)
            {
                // Painted but not seen: a faint sliver, so the ring still says which way the radar is.
                color = Color.Lerp(HiddenRingColor, SeenColor, 0.4f);
                halfWidth = 10f;
                thickness = 2f;
                fade *= 0.6f;
            }

            color.a = Mathf.Clamp01((0.35f + (0.65f * fade)) + (0.4f * flash));
            Arcs.Add(new Arc
            {
                Bearing = Mathf.Atan2(onScreen.y, onScreen.x) * Mathf.Rad2Deg,
                HalfWidth = halfWidth,
                Priority = contact.Seeker ? 3 : contact.Locked ? 2 : contact.Detected ? 1 : 0,
                Color = color,
                Thickness = thickness + flash,
                Radius = radius + (flash * 2f),
            });
        }

        for (int i = 0; i < Arcs.Count; i++)
        {
            DrawUncoveredArc(centre, Arcs[i]);
        }
    }

    /// <summary>
    /// Draws the parts of an arc no more urgent arc covers, so a missile's red replaces the yellow of
    /// the radar guiding it instead of the two stacking into one muddy band.
    /// </summary>
    private static void DrawUncoveredArc(Vector2 centre, Arc arc)
    {
        int segments = Mathf.Max(2, Mathf.CeilToInt(arc.HalfWidth * 2f / ArcStepDegrees));
        float step = arc.HalfWidth * 2f / segments;
        float start = arc.Bearing - arc.HalfWidth;
        for (int s = 0; s < segments; s++)
        {
            float a = start + (step * s);
            if (IsCoveredByHigherPriority(a + (step * 0.5f), arc.Priority))
            {
                continue;
            }

            Vector2 from = centre + (Direction(a) * arc.Radius);
            Vector2 to = centre + (Direction(a + step) * arc.Radius);
            CommanderUiTheme.DrawLine(from, to, arc.Color, arc.Thickness);
        }
    }

    private static bool IsCoveredByHigherPriority(float degrees, int priority)
    {
        for (int i = 0; i < Arcs.Count; i++)
        {
            if (Arcs[i].Priority > priority && Mathf.Abs(Mathf.DeltaAngle(degrees, Arcs[i].Bearing)) <= Arcs[i].HalfWidth)
            {
                return true;
            }
        }

        return false;
    }

    private static void DrawArc(Vector2 centre, float radius, float fromDegrees, float toDegrees, int segments, Color color, float thickness)
    {
        float step = (toDegrees - fromDegrees) / segments;
        Vector2 previous = centre + (Direction(fromDegrees) * radius);
        for (int i = 1; i <= segments; i++)
        {
            Vector2 next = centre + (Direction(fromDegrees + (step * i)) * radius);
            CommanderUiTheme.DrawLine(previous, next, color, thickness);
            previous = next;
        }
    }

    private static Vector2 Direction(float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
    }

    private static void PruneDeadAircraft()
    {
        DeadAircraft.Clear();
        foreach (KeyValuePair<Aircraft, Action<Aircraft.OnRadarWarning>> pair in Handlers)
        {
            // Unity's == null: the managed object outlives the destroyed aircraft.
            if (pair.Key == null)
            {
                DeadAircraft.Add(pair.Key!);
            }
        }

        for (int i = 0; i < DeadAircraft.Count; i++)
        {
            Aircraft dead = DeadAircraft[i];
            dead.onRadarWarning -= Handlers[dead];
            if (JamHandlers.TryGetValue(dead, out Action<Unit.JamEventArgs> jamHandler))
            {
                dead.onJam -= jamHandler;
            }

            JamHandlers.Remove(dead);
            JamLevels.Remove(dead);

            Handlers.Remove(dead);
            Contacts.Remove(dead);
            MapIcons.Remove(dead);
        }
    }

    private struct Arc
    {
        internal float Bearing;
        internal float HalfWidth;
        internal int Priority;
        internal Color Color;
        internal float Thickness;
        internal float Radius;
    }

    private struct JamState
    {
        internal float Level;
        internal float At;
    }

    private struct Contact
    {
        internal Unit Emitter;
        internal float LastPing;
        internal bool Detected;
        internal bool Locked;
        internal bool Seeker;
        internal float Strength;
    }
}
