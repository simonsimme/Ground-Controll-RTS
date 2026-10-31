using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Transient "order received" flash at the point that was just ordered. Without it there is
/// no way to tell a swallowed click from a unit that is simply slow to start moving.
/// </summary>
internal static class CommanderOrderPing
{
    private const float LifetimeSeconds = 0.55f;
    private const float StartSizePixels = 120f;
    private const float EndSizePixels = 26f;
    private const int MaxPings = 6;

    private static readonly List<Ping> Pings = new();

    internal static void Show(GlobalPosition position, bool attack, bool rescue = false)
    {
        if (!CommanderSettings.OrderFeedback)
        {
            return;
        }

        if (Pings.Count >= MaxPings)
        {
            Pings.RemoveAt(0);
        }

        Pings.Add(new Ping(position, attack, rescue, Time.unscaledTime));
    }

    internal static void Clear()
    {
        Pings.Clear();
    }

    internal static void Draw(Camera camera)
    {
        for (int i = Pings.Count - 1; i >= 0; i--)
        {
            float age = Time.unscaledTime - Pings[i].StartedAt;
            if (age >= LifetimeSeconds)
            {
                Pings.RemoveAt(i);
                continue;
            }

            float progress = age / LifetimeSeconds;
            float size = Mathf.Lerp(StartSizePixels, EndSizePixels, progress);
            Color color = Pings[i].Rescue
                ? new Color(1f, 0.85f, 0.2f, 1f - progress)
                : Pings[i].Attack
                    ? new Color(1f, 0.42f, 0.24f, 1f - progress)
                    : new Color(0.2f, 0.85f, 0.82f, 1f - progress);

            Vector3 screen = camera.WorldToScreenPoint(Pings[i].Position.ToLocalPosition());
            if (screen.z > 0f)
            {
                DrawRing(CommanderUiScale.ScreenToGui(screen), size, color);
            }

            CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
            if (map != null && map.TryWorldToMapScreen(Pings[i].Position, out Vector2 mapScreen))
            {
                DrawRing(CommanderUiScale.ScreenToGui(mapScreen), size * 0.5f, color);
            }
        }
    }

    private static void DrawRing(Vector2 center, float size, Color color)
    {
        // Same bracket the standing markers use, so the flash reads as the order landing on
        // that point. DrawFrame would tint the neon border slice and wash the orange out.
        CommanderUiTheme.DrawWorldMarker(center, string.Empty, color, size);
    }

    private readonly struct Ping
    {
        internal Ping(GlobalPosition position, bool attack, bool rescue, float startedAt)
        {
            Position = position;
            Attack = attack;
            Rescue = rescue;
            StartedAt = startedAt;
        }

        internal GlobalPosition Position { get; }

        internal bool Attack { get; }

        internal bool Rescue { get; }

        internal float StartedAt { get; }
    }
}
