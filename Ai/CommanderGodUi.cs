using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GroundControlRts;

/// <summary>
/// GOD DIFFICULTY's local show: every effect a god line triggers on a client that runs the mod.
/// The host only sends chat lines; the tag in each one (<c>[GOD AWAKENS]</c>, <c>[GOD, ENRAGED]</c>,
/// ...) is what tells this class which effect to play, so no second network message exists.
/// </summary>
/// <remarks>
/// All cosmetic. The screen shake is applied only for the render and undone right after, so no
/// camera code sees it; the colour grade is our own URP volume on the night-vision volume's layer,
/// which never overrides <c>postExposure</c> (night vision's gain); the thunder is the storm's own
/// clips. The display glitch flickers the flight HUD's own canvas through a <c>CanvasGroup</c>
/// and nudges its top-level panels by a few pixels, relative to wherever they are, so both are
/// undone exactly when it ends.
/// </remarks>
internal static class CommanderGodUi
{
    /// <summary>What every god line starts with, and what the patch looks for.</summary>
    internal const string Marker = "<color=#FF1E3C><b>[GOD";

    internal const string TagNone = "";
    internal const string TagAwakens = " AWAKENS";
    internal const string TagEnraged = ", ENRAGED";
    internal const string TagWounded = ", WOUNDED";
    internal const string TagTriumphant = ", TRIUMPHANT";
    internal const string TagFallen = ", FALLEN";
    internal const string TagArmed = ", ARMED";
    internal const string TagLaunching = ", LAUNCHING";

    private const float ShakeSeconds = 1.4f;
    private const float TextSeconds = 7f;
    private const float CinematicSeconds = 9f;
    private const float EndScreenSeconds = 14f;
    private const float RageSeconds = 8f;
    private const float GlitchSeconds = 1.1f;
    private const float BannerSeconds = 9f;

    private static readonly Regex Tags = new("<.*?>");

    private static readonly AccessTools.FieldRef<NightVision, Volume> NightVisionVolume =
        AccessTools.FieldRefAccess<NightVision, Volume>("postProcessing");

    private static readonly AccessTools.FieldRef<Lightning, AudioClip[]> ThunderClips =
        AccessTools.FieldRefAccess<Lightning, AudioClip[]>("thunderClips");

    private static readonly MethodInfo GenerateStrike = AccessTools.Method(typeof(Lightning), "GenerateStrike");

    private static readonly AccessTools.FieldRef<FlightHud, Canvas> HudCanvas =
        AccessTools.FieldRefAccess<FlightHud, Canvas>("canvas");

    private static readonly List<RectTransform> HudPanels = new();
    private static readonly List<Vector2> HudOffsets = new();

    private static float effectStart = -100f;
    private static float effectStrength;
    private static string bigText = string.Empty;
    private static float cinematicStart = -100f;
    private static float rageStart = -100f;
    private static bool bossPhase;
    private static bool awake;
    private static float matchStart;
    private static float endStart = -100f;
    private static bool godWon;
    private static string endStats = string.Empty;
    private static float nextBossStrikeAt;
    private static float glitchStart = -100f;
    private static float bannerStart = -100f;
    private static string bannerText = string.Empty;
    private static bool glitching;
    private static CanvasGroup? hudGroup;
    private static float hudRestoreAlpha = 1f;
    private static Texture2D? noiseTexture;

    private static Lightning? lightning;
    private static float nextLightningSearchAt;
    private static AudioSource? thunderSource;

    private static Volume? volume;
    private static VolumeProfile? profile;
    private static ColorAdjustments? colour;
    private static ChromaticAberration? aberration;
    private static FilmGrain? grain;
    private static LensDistortion? distortion;
    private static Vignette? vignetteFx;

    private static Texture2D? edgeTexture;
    private static GUIStyle? style;
    private static Camera? shakenCamera;
    private static Vector3 savedPosition;
    private static Quaternion savedRotation;

    internal static void Install()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
    }

    internal static void Reset()
    {
        EndHudGlitch();
        effectStart = cinematicStart = rageStart = endStart = glitchStart = bannerStart = -100f;
        bossPhase = awake = godWon = false;
        lightning = null;
        nextLightningSearchAt = 0f;
        if (volume != null)
        {
            Object.Destroy(volume.gameObject);
        }

        if (profile != null)
        {
            Object.Destroy(profile);
        }

        volume = null;
        profile = null;
    }

    internal static void Receive(string message, float firstWordsSeconds)
    {
        int tagEnd = message.IndexOf(']', Marker.Length);
        string tag = tagEnd > 0 ? message.Substring(Marker.Length, tagEnd - Marker.Length) : TagNone;
        int split = message.IndexOf("</color> ", System.StringComparison.Ordinal);
        bigText = Tags.Replace(split >= 0 ? message.Substring(split) : message, string.Empty).Trim();

        float now = Time.unscaledTime;
        effectStart = now;
        glitchStart = now;
        effectStrength = tag.Length == 0 ? 1f : 1.6f;

        if (!awake)
        {
            awake = true;

            // The awakening line is sent a fixed time into the match; a player who joined later
            // counts from their own load.
            matchStart = Time.time - (tag == TagAwakens ? firstWordsSeconds : Time.timeSinceLevelLoad);
        }

        switch (tag)
        {
            case TagAwakens:
                cinematicStart = now;
                break;
            case TagEnraged:
                rageStart = now;
                break;
            case TagWounded:
                rageStart = now;
                bossPhase = true;
                break;
            case TagArmed:
                rageStart = now;
                bannerStart = now;
                bannerText = "NUCLEAR RELEASE AUTHORIZED";
                break;
            case TagLaunching:
                rageStart = now;
                bannerStart = now;
                bannerText = "NUCLEAR LAUNCH DETECTED";
                break;
            case TagTriumphant:
            case TagFallen:
                godWon = tag == TagTriumphant;
                endStart = now;
                endStats = BuildEndStats();
                break;
        }

        Strike(withThunder: true);
    }

    /// <summary>Per frame on every client: the boss-phase storm and the colour grade.</summary>
    internal static void Tick()
    {
        if (bossPhase && endStart < 0f && Time.time >= nextBossStrikeAt)
        {
            nextBossStrikeAt = Time.time + Random.Range(3f, 8f);
            Strike(withThunder: false);
        }

        UpdateGrade();
        TickHudGlitch();
    }

    /// <summary>The IMGUI pass: edge flash, opening cinematic, the line, survival timer, end screen.</summary>
    internal static void Draw()
    {
        if (!awake)
        {
            return;
        }

        float width = CommanderUiScale.Width;
        float height = CommanderUiScale.Height;
        float now = Time.unscaledTime;
        Color previous = GUI.color;
        style ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
        };

        float age = now - effectStart;
        float flash = Mathf.Clamp01(1f - (age / 1.8f)) * (0.55f + (0.2f * Mathf.Sin(age * 25f)));
        if (flash > 0f)
        {
            GUI.color = new Color(1f, 1f, 1f, flash * Mathf.Min(1f, effectStrength));
            GUI.DrawTexture(new Rect(0f, 0f, width, height), GetEdgeTexture());
        }

        DrawStatic(now - glitchStart, width, height);
        DrawCinematic(now - cinematicStart, width, height);

        if (age < TextSeconds)
        {
            float alpha = Mathf.Clamp01(age / 0.25f) * Mathf.Clamp01((TextSeconds - age) / 1.5f);
            float jitter = age < ShakeSeconds ? Mathf.Sin(age * 60f) * 4f * (1f - (age / ShakeSeconds)) * effectStrength : 0f;
            Color burn = Color.Lerp(new Color(1f, 0.12f, 0.24f), new Color(1f, 0.7f, 0.28f), Mathf.PingPong(age * 0.8f, 1f));
            Label(
                new Rect((width * 0.12f) + jitter, height * 0.2f, width * 0.76f, height * 0.16f),
                bigText,
                Mathf.Clamp(height * 0.034f, 18f, 44f),
                burn,
                alpha);
        }

        if (endStart < 0f)
        {
            string timer = "SURVIVED " + FormatTime(Time.time - matchStart) + (bossPhase ? "   ·   GOD IS WOUNDED" : string.Empty);
            Label(new Rect(0f, 2f, width, 28f), timer, Mathf.Clamp(height * 0.016f, 12f, 20f), new Color(1f, 0.2f, 0.25f), 0.9f);
        }

        DrawBanner(now - bannerStart, width, height);
        DrawEndScreen(now - endStart, width, height);
        GUI.color = previous;
    }

    /// <summary>A second of broken signal: moving static and torn scan lines over everything.</summary>
    private static void DrawStatic(float age, float width, float height)
    {
        if (age < 0f || age > GlitchSeconds)
        {
            return;
        }

        float strength = (1f - (age / GlitchSeconds)) * Mathf.Min(1.3f, effectStrength);
        GUI.color = new Color(1f, 1f, 1f, (0.16f + (0.2f * Random.value)) * strength);
        GUI.DrawTextureWithTexCoords(
            new Rect(0f, 0f, width, height),
            GetNoiseTexture(),
            new Rect(Random.value, Random.value, width / 256f, height / 256f));

        for (int i = 0; i < 5; i++)
        {
            if (Random.value > 0.55f)
            {
                continue;
            }

            GUI.color = Random.value < 0.5f
                ? new Color(1f, 1f, 1f, 0.22f * strength)
                : new Color(0.9f, 0f, 0.1f, 0.3f * strength);
            GUI.DrawTexture(new Rect(0f, Random.value * height, width, Random.Range(2f, 18f)), Texture2D.whiteTexture);
        }
    }

    /// <summary>The flashing yellow-and-black nuclear warning under the survival timer.</summary>
    private static void DrawBanner(float age, float width, float height)
    {
        if (age < 0f || age > BannerSeconds)
        {
            return;
        }

        bool lit = Mathf.Repeat(age, 0.6f) < 0.4f;
        float alpha = Mathf.Clamp01((BannerSeconds - age) / 1f);
        Color yellow = new(1f, 0.85f, 0.1f);
        Rect box = new(width * 0.25f, Mathf.Max(34f, height * 0.06f), width * 0.5f, Mathf.Clamp(height * 0.05f, 34f, 60f));
        GUI.color = lit ? new Color(yellow.r, yellow.g, yellow.b, 0.9f * alpha) : new Color(0f, 0f, 0f, 0.85f * alpha);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        Label(box, bannerText, Mathf.Clamp(height * 0.03f, 16f, 38f), lit ? Color.black : yellow, alpha);
    }

    private static void DrawCinematic(float age, float width, float height)
    {
        if (age < 0f || age > CinematicSeconds)
        {
            return;
        }

        float bars = Mathf.Clamp01(age / 0.8f) * Mathf.Clamp01((CinematicSeconds - age) / 1f);
        float barHeight = height * 0.11f * bars;
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0f, 0f, width, barHeight), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(0f, height - barHeight, width, barHeight), Texture2D.whiteTexture);

        float black = 0.9f * Mathf.Clamp01(age / 1f) * Mathf.Clamp01((6f - age) / 1.5f);
        GUI.color = new Color(0f, 0f, 0f, black);
        GUI.DrawTexture(new Rect(0f, 0f, width, height), Texture2D.whiteTexture);

        float title = Mathf.Clamp01((age - 1f) / 0.8f) * Mathf.Clamp01((5.5f - age) / 1f);
        if (title > 0f)
        {
            float wobble = Mathf.Sin(age * 45f) * 3f * Mathf.Clamp01(2f - age);
            Label(
                new Rect(wobble, height * 0.36f, width, height * 0.14f),
                "GOD DIFFICULTY",
                Mathf.Clamp(height * 0.09f, 36f, 120f),
                new Color(1f, 0.1f, 0.18f),
                title);
            Label(
                new Rect(0f, height * 0.5f, width, height * 0.06f),
                "THE GOD HAS AWAKENED",
                Mathf.Clamp(height * 0.028f, 16f, 36f),
                new Color(1f, 0.7f, 0.28f),
                title);
        }
    }

    private static void DrawEndScreen(float age, float width, float height)
    {
        if (age < 0f || age > EndScreenSeconds)
        {
            return;
        }

        float alpha = Mathf.Clamp01(age / 1.5f) * Mathf.Clamp01((EndScreenSeconds - age) / 2f);
        GUI.color = new Color(0f, 0f, 0f, alpha * 0.75f);
        GUI.DrawTexture(new Rect(0f, 0f, width, height), Texture2D.whiteTexture);
        Label(
            new Rect(0f, height * 0.34f, width, height * 0.14f),
            godWon ? "YOU HAVE PERISHED" : "GOD HAS FALLEN",
            Mathf.Clamp(height * 0.085f, 34f, 110f),
            godWon ? new Color(1f, 0.1f, 0.18f) : new Color(1f, 0.78f, 0.3f),
            alpha);
        Label(
            new Rect(0f, height * 0.5f, width, height * 0.1f),
            endStats,
            Mathf.Clamp(height * 0.024f, 14f, 30f),
            Color.white,
            alpha);
    }

    private static void Label(Rect rect, string text, float size, Color colour, float alpha)
    {
        GUIStyle labelStyle = style!;
        labelStyle.fontSize = Mathf.RoundToInt(size);
        GUIContent content = new(text);
        GUI.color = new Color(0f, 0f, 0f, alpha * 0.85f);
        labelStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(rect.x + 3f, rect.y + 3f, rect.width, rect.height), content, labelStyle);
        GUI.color = new Color(1f, 1f, 1f, alpha);
        labelStyle.normal.textColor = colour;
        GUI.Label(rect, content, labelStyle);
    }

    private static string BuildEndStats()
    {
        FactionHQ? local = CommanderGameAccess.GetLocalHq();
        int yours = 0;
        int theirs = 0;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }

            if (hq == local)
            {
                yours += CommanderVictoryService.CountBases(hq);
            }
            else
            {
                theirs += CommanderVictoryService.CountBases(hq);
            }
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1}\nYour bases: {2}     God's bases: {3}",
            godWon ? "You survived" : "Match time",
            FormatTime(Time.time - matchStart),
            yours,
            theirs);
    }

    private static string FormatTime(float seconds)
    {
        int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", total / 60, total % 60);
    }

    /// <summary>A strike from the storm's own lightning, and its thunder played close.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A cosmetic effect; whatever it throws is logged and must not take the mod's tick down with it.")]
    private static void Strike(bool withThunder)
    {
        if (lightning == null && Time.unscaledTime >= nextLightningSearchAt)
        {
            nextLightningSearchAt = Time.unscaledTime + 5f;
            foreach (Lightning candidate in Resources.FindObjectsOfTypeAll<Lightning>())
            {
                if (candidate != null && candidate.gameObject.scene.IsValid())
                {
                    lightning = candidate;
                    break;
                }
            }
        }

        if (lightning == null)
        {
            return;
        }

        try
        {
            // Inactive outside a storm: the flash would not render, but the thunder still plays.
            if (lightning.isActiveAndEnabled)
            {
                GenerateStrike.Invoke(lightning, null);
            }

            AudioClip[] clips = ThunderClips(lightning);
            if (withThunder && clips != null && clips.Length > 0)
            {
                if (thunderSource == null)
                {
                    GameObject holder = new("GodDifficultyThunder");
                    Object.DontDestroyOnLoad(holder);
                    thunderSource = holder.AddComponent<AudioSource>();
                    thunderSource.spatialBlend = 0f;
                }

                thunderSource.PlayOneShot(clips[Random.Range(0, clips.Length)], 1f);
            }
        }
        catch (System.Exception ex)
        {
            CommanderPlugin.Log.LogWarning($"GOD DIFFICULTY lightning failed: {ex.Message}");
            lightning = null;
        }
    }

    private static void UpdateGrade()
    {
        float now = Time.unscaledTime;
        float pulse = Mathf.Clamp01(1f - ((now - effectStart) / 2f)) * effectStrength;
        float rage = Mathf.Clamp01(1f - ((now - rageStart) / RageSeconds));
        float boss = bossPhase ? 1f : 0f;
        if (pulse <= 0f && rage <= 0f && boss <= 0f)
        {
            if (volume != null)
            {
                volume.weight = 0f;
            }

            return;
        }

        if (!EnsureVolume())
        {
            return;
        }

        volume!.weight = 1f;
        colour!.saturation.value = Mathf.Max(-100f, (-45f * rage) - (30f * boss));
        colour.contrast.value = (20f * rage) + (15f * boss);
        colour.colorFilter.value = Color.Lerp(Color.white, new Color(1f, 0.5f, 0.5f), Mathf.Max(rage, 0.55f * boss));
        aberration!.intensity.value = Mathf.Clamp01((0.9f * pulse) + (0.25f * boss));
        grain!.intensity.value = Mathf.Clamp01((0.7f * pulse) + (0.35f * boss));
        distortion!.intensity.value = Mathf.Clamp(-0.3f * pulse * Mathf.Sin(now * 12f), -1f, 1f);
        vignetteFx!.intensity.value = Mathf.Clamp01((0.2f * rage) + (0.3f * boss));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A cosmetic effect; whatever it throws is logged and must not take the mod's tick down with it.")]
    private static bool EnsureVolume()
    {
        if (volume != null)
        {
            return true;
        }

        try
        {
            GameObject holder = new("GodDifficultyVolume");
            if (NightVision.i != null)
            {
                Volume nightVision = NightVisionVolume(NightVision.i);
                if (nightVision != null)
                {
                    holder.layer = nightVision.gameObject.layer;
                }
            }

            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            colour = profile.Add<ColorAdjustments>(false);
            colour.saturation.overrideState = true;
            colour.contrast.overrideState = true;
            colour.colorFilter.overrideState = true;
            aberration = profile.Add<ChromaticAberration>(false);
            aberration.intensity.overrideState = true;
            grain = profile.Add<FilmGrain>(false);
            grain.intensity.overrideState = true;
            grain.type.overrideState = true;
            grain.type.value = FilmGrainLookup.Medium3;
            distortion = profile.Add<LensDistortion>(false);
            distortion.intensity.overrideState = true;
            vignetteFx = profile.Add<Vignette>(false);
            vignetteFx.intensity.overrideState = true;
            vignetteFx.color.overrideState = true;
            vignetteFx.color.value = new Color(0.6f, 0f, 0.05f);

            volume = holder.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 500f;
            volume.profile = profile;
            return true;
        }
        catch (System.Exception ex)
        {
            CommanderPlugin.Log.LogWarning($"GOD DIFFICULTY screen grade unavailable: {ex.Message}");
            return false;
        }
    }

    private static void OnBeginCamera(ScriptableRenderContext context, Camera camera)
    {
        float age = Time.unscaledTime - effectStart;
        if (age > ShakeSeconds || camera == null || camera != SceneSingleton<CameraStateManager>.i?.mainCamera)
        {
            return;
        }

        float amount = effectStrength * (1f - (age / ShakeSeconds));
        amount *= amount;
        float t = Time.unscaledTime * 38f;
        Transform tr = camera.transform;
        shakenCamera = camera;
        savedPosition = tr.position;
        savedRotation = tr.rotation;
        tr.position += (tr.right * ((Mathf.PerlinNoise(t, 0f) - 0.5f) * 1.4f * amount))
            + (tr.up * ((Mathf.PerlinNoise(0f, t) - 0.5f) * 1.4f * amount));
        tr.rotation *= Quaternion.Euler(
            (Mathf.PerlinNoise(t, 7f) - 0.5f) * 3f * amount,
            (Mathf.PerlinNoise(7f, t) - 0.5f) * 3f * amount,
            (Mathf.PerlinNoise(t, t) - 0.5f) * 4f * amount);
    }

    private static void OnEndCamera(ScriptableRenderContext context, Camera camera)
    {
        if (shakenCamera == null || camera != shakenCamera)
        {
            return;
        }

        camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
        shakenCamera = null;
    }

    /// <summary>The HUD half of the display glitch: flicker and jitter for <see cref="GlitchSeconds"/>.</summary>
    private static void TickHudGlitch()
    {
        float age = Time.unscaledTime - glitchStart;
        if (age < 0f || age > GlitchSeconds)
        {
            EndHudGlitch();
            return;
        }

        if (!glitching && !BeginHudGlitch())
        {
            return;
        }

        hudGroup!.alpha = Random.value < 0.25f ? Random.Range(0f, 0.2f) : Random.Range(0.6f, 1f);
        float amount = (1f - (age / GlitchSeconds)) * 10f * effectStrength;
        for (int i = 0; i < HudPanels.Count; i++)
        {
            RectTransform panel = HudPanels[i];
            if (panel == null)
            {
                continue;
            }

            Vector2 fresh = Random.value < 0.3f
                ? new Vector2(Random.Range(-amount, amount), Random.Range(-amount * 0.4f, amount * 0.4f))
                : Vector2.zero;
            panel.anchoredPosition += fresh - HudOffsets[i];
            HudOffsets[i] = fresh;
        }
    }

    private static bool BeginHudGlitch()
    {
        FlightHud? hud = SceneSingleton<FlightHud>.i;
        Canvas? canvas = hud == null ? null : HudCanvas(hud);
        if (canvas == null || !canvas.gameObject.activeInHierarchy)
        {
            return false;
        }

        hudGroup = canvas.GetComponent<CanvasGroup>();
        if (hudGroup == null)
        {
            hudGroup = canvas.gameObject.AddComponent<CanvasGroup>();
        }

        hudRestoreAlpha = hudGroup.alpha;
        HudPanels.Clear();
        HudOffsets.Clear();
        foreach (Transform child in canvas.transform)
        {
            if (child is RectTransform panel)
            {
                HudPanels.Add(panel);
                HudOffsets.Add(Vector2.zero);
            }
        }

        glitching = true;
        return true;
    }

    private static void EndHudGlitch()
    {
        if (!glitching)
        {
            return;
        }

        if (hudGroup != null)
        {
            hudGroup.alpha = hudRestoreAlpha;
        }

        for (int i = 0; i < HudPanels.Count; i++)
        {
            if (HudPanels[i] != null)
            {
                HudPanels[i].anchoredPosition -= HudOffsets[i];
            }
        }

        HudPanels.Clear();
        HudOffsets.Clear();
        hudGroup = null;
        glitching = false;
    }

    private static Texture2D GetNoiseTexture()
    {
        if (noiseTexture != null)
        {
            return noiseTexture;
        }

        const int size = 256;
        noiseTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
        };
        System.Random noise = new(7);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float v = (float)noise.NextDouble();
                noiseTexture.SetPixel(x, y, new Color(v, v, v, 1f));
            }
        }

        noiseTexture.Apply();
        return noiseTexture;
    }

    private static Texture2D GetEdgeTexture()
    {
        if (edgeTexture != null)
        {
            return edgeTexture;
        }

        const int size = 128;
        edgeTexture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = ((x + 0.5f) / size * 2f) - 1f;
                float dy = ((y + 0.5f) / size * 2f) - 1f;
                float edge = Mathf.Clamp01((Mathf.Sqrt((dx * dx) + (dy * dy)) - 0.45f) / 0.75f);
                edgeTexture.SetPixel(x, y, new Color(0.85f, 0f, 0.05f, edge * edge));
            }
        }

        edgeTexture.Apply();
        return edgeTexture;
    }
}
