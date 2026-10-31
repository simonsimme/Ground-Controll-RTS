using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Per-window zoom: every Commander window gets a corner grip that scales the whole window,
/// text and all, the way the tactical map's grip scales the map. Right-click the grip for 100 %.
/// </summary>
/// <remarks>
/// The scale is a GUI matrix pivoted on the window's top-left corner, so a window's own layout
/// code keeps working in its unscaled units and only the hit tests need <see cref="Visual"/>.
/// Unity records the matrix per window at the <c>GUI.Window</c> call, which is also how the
/// global <see cref="CommanderUiScale"/> reaches windows drawn after it is popped.
/// </remarks>
internal static class CommanderWindowScale
{
    private const float MinScale = 0.6f;
    private const float MaxScale = 2.5f;
    private const float GripSize = 14f;

    private static readonly Dictionary<int, float> Scales = new();
    private static readonly Dictionary<int, Vector2> Sizes = new();
    private static readonly Dictionary<int, (GUI.WindowFunction Inner, GUI.WindowFunction Outer)> Wrapped = new();
    private static bool loaded;

    private static int resizingId;
    private static float resizeStartScale;
    private static Vector2 resizeStartMouse;

    private static int draggingId;
    private static Vector2 dragOffset;

    internal static float Get(int id)
    {
        Load();
        return Scales.TryGetValue(id, out float scale) ? scale : 1f;
    }

    /// <summary>Where the window really is on screen, in GUI units: its rect with the scale applied.</summary>
    internal static Rect Visual(int id, Rect rect)
    {
        float scale = Get(id);
        return new Rect(rect.x, rect.y, rect.width * scale, rect.height * scale);
    }

    /// <summary><see cref="CommanderUiTheme.ClampWindow"/> for a scaled window: keeps what is on screen on screen.</summary>
    internal static Rect Clamp(int id, Rect rect)
    {
        float scale = Get(id);
        Rect visual = CommanderUiTheme.ClampWindow(Visual(id, rect));
        return new Rect(visual.x, visual.y, visual.width / scale, visual.height / scale);
    }

    /// <summary>Drop-in for <c>GUI.Window(id, rect, draw, title, CommanderUiTheme.Window)</c>.</summary>
    internal static Rect Window(int id, Rect rect, GUI.WindowFunction draw, string title)
    {
        TickPointer(id, rect);
        float scale = Get(id);
        Sizes[id] = rect.size;

        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = previous
            * Matrix4x4.TRS(rect.position, Quaternion.identity, new Vector3(scale, scale, 1f))
            * Matrix4x4.TRS(-(Vector3)rect.position, Quaternion.identity, Vector3.one);
        Rect result = GUI.Window(id, rect, Wrap(id, draw), title, CommanderUiTheme.Window);
        GUI.matrix = previous;

        // Unity's own title drag works in the window's scaled space, so a scaled window would
        // drift off the cursor. Once it starts moving, pin its corner to the cursor instead.
        if (result.position != rect.position || draggingId == id)
        {
            Vector2 mouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
            if (draggingId != id)
            {
                draggingId = id;
                dragOffset = mouse - rect.position;
            }

            result.position = mouse - dragOffset;
        }

        return result;
    }

    /// <summary>
    /// The same grip for top-level GUI that is not a <c>GUI.Window</c> (the selection bar): the
    /// caller applies <see cref="Get"/> as its own matrix and hit-tests through it.
    /// <paramref name="grip"/> is in the caller's unscaled space, <paramref name="size"/> its
    /// unscaled size, which is what the drag distance is measured against.
    /// </summary>
    internal static void Grip(int id, Rect grip, Vector2 size)
    {
        TickPointer(id, new Rect(Vector2.zero, size));
        DrawGrip(id, grip);
    }

    private static void TickPointer(int id, Rect rect)
    {
        if (Input.GetMouseButton(0))
        {
            if (resizingId == id)
            {
                // Grows by whichever axis the cursor pulled further, like the map grip.
                Vector2 start = rect.size * resizeStartScale;
                Vector2 delta = CommanderUiScale.ScreenToGui(Input.mousePosition) - resizeStartMouse;
                float factor = Mathf.Max((start.x + delta.x) / start.x, (start.y + delta.y) / start.y);
                Scales[id] = Mathf.Clamp(resizeStartScale * factor, MinScale, MaxScale);
            }

            return;
        }

        if (resizingId == id)
        {
            Save();
        }

        resizingId = resizingId == id ? 0 : resizingId;
        draggingId = draggingId == id ? 0 : draggingId;
    }

    private static GUI.WindowFunction Wrap(int id, GUI.WindowFunction draw)
    {
        if (Wrapped.TryGetValue(id, out (GUI.WindowFunction Inner, GUI.WindowFunction Outer) cached) && cached.Inner.Equals(draw))
        {
            return cached.Outer;
        }

        void Outer(int windowId)
        {
            draw(windowId);
            Vector2 size = Sizes.TryGetValue(windowId, out Vector2 known) ? known : Vector2.zero;
            DrawGrip(windowId, new Rect(size.x - GripSize - 2f, size.y - GripSize - 2f, GripSize, GripSize));
        }

        Wrapped[id] = (draw, Outer);
        return Wrapped[id].Outer;
    }

    /// <summary>Drawn last, so a control under the corner still gets its click first.</summary>
    private static void DrawGrip(int id, Rect grip)
    {
        GUI.Box(grip, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawFrame(grip, 1f);

        Event current = Event.current;
        if (current.type != EventType.MouseDown || !grip.Contains(current.mousePosition))
        {
            return;
        }

        if (current.button == 1)
        {
            Scales.Remove(id);
            Save();
        }
        else if (current.button == 0)
        {
            resizingId = id;
            resizeStartScale = Get(id);
            resizeStartMouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
        }

        current.Use();
    }

    /// <summary>Saved as "id=scale;id=scale" under UI/WindowScales.</summary>
    private static void Load()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        foreach (string pair in CommanderSettings.WindowScales.Split(';'))
        {
            string[] parts = pair.Split('=');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float scale))
            {
                Scales[id] = Mathf.Clamp(scale, MinScale, MaxScale);
            }
        }
    }

    private static void Save()
    {
        List<string> pairs = new();
        foreach (KeyValuePair<int, float> entry in Scales)
        {
            pairs.Add(entry.Key.ToString(CultureInfo.InvariantCulture) + "=" + entry.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        CommanderSettings.WindowScales = string.Join(";", pairs);
    }
}
