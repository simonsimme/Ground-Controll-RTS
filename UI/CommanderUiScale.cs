using UnityEngine;

namespace GroundControlRts;

internal static class CommanderUiScale
{
    // One GUI unit is one pixel at 1080p, and taller screens grow in proportion. IMGUI rasterises
    // glyphs at the style's size, so downscaling makes text soft. Below 1080p it does not shrink
    // further, or text stops being readable.
    private const float ReferenceHeight = 1080f;
    private const float ReferenceScale = 1f;

    internal static float ResolutionScale => Mathf.Max(1f, Screen.height / ReferenceHeight);

    internal static float Scale => ReferenceScale * ResolutionScale * Mathf.Clamp(CommanderSettings.UiScale, 0.5f, 2f);

    internal static float Width => Screen.width / Scale;

    internal static float Height => Screen.height / Scale;

    internal static Matrix4x4 Begin()
    {
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = previous * Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
        return previous;
    }

    internal static void End(Matrix4x4 previous)
    {
        GUI.matrix = previous;
    }

    internal static Vector2 ScreenToGui(Vector2 screenPoint)
    {
        return new Vector2(screenPoint.x / Scale, (Screen.height - screenPoint.y) / Scale);
    }

    internal static Vector2 GuiToScreen(Vector2 guiPoint)
    {
        return new Vector2(guiPoint.x * Scale, Screen.height - (guiPoint.y * Scale));
    }
}
