using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GroundControlRts;

/// <summary>
/// Pictures of units for the purchase windows and the selection card: a cached still per type,
/// and a few live, slowly turning models. The prefab is never instantiated (that would register a
/// networked <c>Unit</c> in a live match, the reason <see cref="CommanderBuildPreview"/> draws its
/// ghost the same way); its meshes are drawn with <c>Graphics.DrawMesh</c> for a private camera
/// on the game's own <c>PreviewRender</c> layer, which nothing in a match puts anything on.
/// </summary>
/// <remarks>
/// Windows ask during OnGUI, which is after this frame's cameras have rendered, so a request is
/// served on the next <see cref="Tick"/> and drawn from then on: a picture appears a frame late,
/// and a still is rendered at most one per frame so a long list costs nothing noticeable.
/// </remarks>
internal static class CommanderUnitPreview
{
    private const int PreviewLayer = 16;

    // Twice the size a card draws it at 1080p: at 160x112 a BUILD card stretched it 1.6x and the
    // smaller buildings read as pixelated.
    private const int StillWidth = 320;
    private const int StillHeight = 224;
    private const int LiveSize = 320;
    private const float FieldOfView = 26f;
    private const float DegreesPerSecond = 22f;

    private static readonly Dictionary<UnitDefinition, List<Part>> Parts = new();
    private static readonly Dictionary<UnitDefinition, RenderTexture> Stills = new();
    private static readonly List<UnitDefinition> StillQueue = new();

    /// <summary>One live view per place that shows one, so a portrait and a shop can turn side by side.</summary>
    private static readonly LiveView[] Views = { new(), new(), new() };

    /// <summary>
    /// The one anti-aliased target every still is drawn into before it is copied out to its own
    /// plain texture: a kept MSAA target per still holds four samples a pixel for the whole mission.
    /// </summary>
    private static RenderTexture? stillCanvas;

    private static Camera? stillCamera;
    private static Light? studioLight;
    private static SphericalHarmonicsL2 studioAmbient;

    /// <summary>
    /// Draws the live, turning model of <paramref name="definition"/> into <paramref name="rect"/>.
    /// <paramref name="slot"/> is which view: 0 the selection portrait, 1 a shop, 2 AIR COMMAND.
    /// </summary>
    internal static void DrawLive(Rect rect, UnitDefinition? definition, int slot = 0)
    {
        if (definition == null || Event.current.type != EventType.Repaint)
        {
            return;
        }

        LiveView view = Views[slot];
        if (view.Wanted != definition)
        {
            view.Yaw = 0f;
        }

        view.Wanted = definition;
        view.WantedFrame = Time.frameCount;
        if (view.Target != null && view.Shown == definition)
        {
            GUI.DrawTexture(rect, view.Target, ScaleMode.ScaleAndCrop, alphaBlend: true);
        }
    }

    /// <summary>Draws the still of <paramref name="definition"/>, queueing it if it is not made yet.</summary>
    internal static void DrawStill(Rect rect, UnitDefinition? definition)
    {
        if (definition == null || Event.current.type != EventType.Repaint)
        {
            return;
        }

        if (Stills.TryGetValue(definition, out RenderTexture still) && still != null)
        {
            GUI.DrawTexture(rect, still, ScaleMode.ScaleAndCrop, alphaBlend: true);
        }
        else if (!StillQueue.Contains(definition))
        {
            StillQueue.Add(definition);
        }
    }

    /// <summary>Per frame, from the mode controller's Update: before this frame renders.</summary>
    internal static void Tick()
    {
        // One still a frame, rendered on demand.
        if (StillQueue.Count > 0)
        {
            if (stillCamera == null)
            {
                stillCamera = NewCamera("GroundControlUnitStill", 400f);
            }

            UnitDefinition next = StillQueue[0];
            StillQueue.RemoveAt(0);
            RenderStill(next);
        }

        // Each live view is left to its own camera's render, which only this frame's DrawMesh reaches.
        for (int i = 0; i < Views.Length; i++)
        {
            LiveView view = Views[i];
            bool wanted = view.Wanted != null && Time.frameCount - view.WantedFrame <= 2;
            if (!wanted)
            {
                view.Wanted = null;
                if (view.Camera != null && view.Camera.enabled)
                {
                    view.Camera.enabled = false;
                }

                continue;
            }

            if (view.Camera == null)
            {
                view.Camera = NewCamera("GroundControlUnitPreview" + i, 800f + (i * 400f));
            }

            view.Target ??= NewTarget(LiveSize, LiveSize);
            view.Yaw += Time.unscaledDeltaTime * DegreesPerSecond;
            view.Camera.targetTexture = view.Target;

            // Rendered here, right after the meshes are queued, not in the camera's own pass later in the
            // frame: anything that moves the camera in between (floating origin shifts) would leave the model
            // outside that render.
            if (Submit(view.Camera, view.Wanted!, -145f + view.Yaw))
            {
                RenderLit(view.Camera);
                view.Shown = view.Wanted;
            }
        }
    }

    /// <summary>Scene change: the stills are freed so a long session does not collect them.</summary>
    internal static void Reset()
    {
        foreach (RenderTexture texture in Stills.Values)
        {
            if (texture != null)
            {
                texture.Release();
                Object.Destroy(texture);
            }
        }

        Stills.Clear();
        StillQueue.Clear();
        for (int i = 0; i < Views.Length; i++)
        {
            Views[i].Wanted = null;
            Views[i].Shown = null;
        }
    }

    private static void RenderStill(UnitDefinition definition)
    {
        stillCanvas ??= NewTarget(StillWidth, StillHeight);
        RenderTexture target = new(StillWidth, StillHeight, 0, RenderTextureFormat.ARGB32)
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        target.Create();
        Stills[definition] = target;
        stillCamera!.targetTexture = stillCanvas;
        if (Submit(stillCamera, definition, -145f))
        {
            RenderLit(stillCamera);
            Graphics.Blit(stillCanvas, target);
        }
    }

    /// <summary>
    /// Renders under a fixed studio light instead of the mission's sky. <c>LevelInfo</c> darkens the
    /// scene at night and a still is kept for the whole mission, so it would otherwise stay black.
    /// <c>Camera.Render</c> is synchronous, so the swap is invisible to the scene.
    /// </summary>
    private static void RenderLit(Camera camera)
    {
        if (studioLight == null)
        {
            GameObject holder = new("GroundControlUnitStudioLight") { hideFlags = HideFlags.HideAndDontSave };
            holder.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            studioLight = holder.AddComponent<Light>();
            studioLight.type = LightType.Directional;
            studioLight.color = new Color(1f, 0.96f, 0.9f);
            studioLight.intensity = 1.6f;
            studioLight.shadows = LightShadows.None;
            studioLight.cullingMask = 1 << PreviewLayer;
            studioLight.enabled = false;
            studioAmbient.AddAmbientLight(new Color(0.42f, 0.44f, 0.47f));
        }

        LevelInfo? level = NetworkSceneSingleton<LevelInfo>.i;
        Light? sun = level != null ? level.sun : null;
        Light? moon = level != null ? level.moon : null;
        bool sunOn = sun != null && sun.enabled;
        bool moonOn = moon != null && moon.enabled;
        Light? previousSun = RenderSettings.sun;
        SphericalHarmonicsL2 previousAmbient = RenderSettings.ambientProbe;
        try
        {
            if (sunOn)
            {
                sun!.enabled = false;
            }

            if (moonOn)
            {
                moon!.enabled = false;
            }

            studioLight.enabled = true;
            RenderSettings.sun = studioLight;
            RenderSettings.ambientProbe = studioAmbient;
            camera.Render();
        }
        finally
        {
            studioLight.enabled = false;
            RenderSettings.sun = previousSun;
            RenderSettings.ambientProbe = previousAmbient;
            if (sunOn)
            {
                sun!.enabled = true;
            }

            if (moonOn)
            {
                moon!.enabled = true;
            }
        }
    }

    /// <summary>
    /// Queues the model's meshes for this frame, in front of the camera, turned to
    /// <paramref name="yaw"/>. The camera is re-anchored every call: it is a scene root, so the
    /// floating origin moves it like everything else.
    /// </summary>
    private static bool Submit(Camera camera, UnitDefinition definition, float yaw)
    {
        List<Part> parts = GetParts(definition);
        if (parts.Count == 0)
        {
            return false;
        }

        Bounds bounds = GetBounds(parts);
        float radius = Mathf.Max(0.5f, bounds.extents.magnitude);
        float distance = radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad) * 0.92f;
        Transform view = camera.transform;
        Vector3 centre = view.position + (view.forward * distance);
        camera.nearClipPlane = Mathf.Max(0.05f, distance - (radius * 2f));
        camera.farClipPlane = distance + (radius * 2f);

        Matrix4x4 model = Matrix4x4.TRS(centre, Quaternion.Euler(0f, yaw, 0f), Vector3.one)
            * Matrix4x4.Translate(-bounds.center);
        for (int i = 0; i < parts.Count; i++)
        {
            Part part = parts[i];
            Matrix4x4 matrix = model * part.Local;
            for (int sub = 0; sub < part.Materials.Length && sub < part.Mesh.subMeshCount; sub++)
            {
                Material? material = part.Materials[sub];
                if (material != null)
                {
                    Graphics.DrawMesh(part.Mesh, matrix, material, PreviewLayer, camera, sub, null, ShadowCastingMode.Off, false);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Far above the play space and looking a little down: only the preview layer is drawn, so
    /// nothing else can get in the frame, and the sun still lights the model like the unit outside.
    /// The two cameras stand apart so neither sees the other's model.
    /// </summary>
    private static Camera NewCamera(string name, float offset)
    {
        GameObject holder = new(name) { hideFlags = HideFlags.HideAndDontSave };
        holder.transform.SetPositionAndRotation(new Vector3(offset, 9000f, 0f), Quaternion.Euler(18f, 0f, 0f));
        Camera camera = holder.AddComponent<Camera>();
        camera.cullingMask = 1 << PreviewLayer;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        camera.fieldOfView = FieldOfView;
        camera.allowHDR = false;
        camera.allowMSAA = true;
        camera.depth = -50f;
        camera.enabled = false;
        UniversalAdditionalCameraData data = holder.AddComponent<UniversalAdditionalCameraData>();
        data.renderPostProcessing = false;
        data.renderShadows = false;
        return camera;
    }

    private static RenderTexture NewTarget(int width, int height)
    {
        RenderTexture texture = new(width, height, 24, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 4,
            hideFlags = HideFlags.HideAndDontSave,
        };
        texture.Create();
        return texture;
    }

    private static Bounds GetBounds(List<Part> parts)
    {
        Bounds bounds = default;
        bool any = false;
        for (int i = 0; i < parts.Count; i++)
        {
            Bounds local = parts[i].Mesh.bounds;
            Vector3 extents = local.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = local.center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                point = parts[i].Local.MultiplyPoint3x4(point);
                if (!any)
                {
                    bounds = new Bounds(point, Vector3.zero);
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(point);
                }
            }
        }

        return bounds;
    }

    private static List<Part> GetParts(UnitDefinition definition)
    {
        if (Parts.TryGetValue(definition, out List<Part> cached))
        {
            return cached;
        }

        cached = new List<Part>();
        Parts[definition] = cached;
        GameObject prefab = definition.unitPrefab;
        if (prefab == null)
        {
            return cached;
        }

        // Only the most detailed LOD: the others are the same model again.
        HashSet<Renderer> lowDetail = new();
        LODGroup[] groups = prefab.GetComponentsInChildren<LODGroup>(true);
        for (int i = 0; i < groups.Length; i++)
        {
            LOD[] levels = groups[i].GetLODs();
            for (int level = 1; level < levels.Length; level++)
            {
                foreach (Renderer renderer in levels[level].renderers)
                {
                    if (renderer != null)
                    {
                        lowDetail.Add(renderer);
                    }
                }
            }
        }

        Transform root = prefab.transform;
        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];

            // Mesh renderers only. The skinned ones on a unit are its crew figures, and drawn without
            // their skeleton they come out at bind-pose scale: the Cricket's two pilots filled the
            // frame as giants and the aeroplane could not be seen.
            Mesh? mesh = renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            if (mesh == null)
            {
                continue;
            }

            bool skip = lowDetail.Contains(renderer)
                || renderer.gameObject.layer == 3
                || !CommanderBuildPreview.IsSolidPart(renderer.transform, root, renderer);
            if (skip)
            {
                continue;
            }

            cached.Add(new Part(mesh, renderer.sharedMaterials, root.worldToLocalMatrix * renderer.transform.localToWorldMatrix));
        }

        return cached;
    }

    private readonly struct Part
    {
        internal Part(Mesh mesh, Material?[] materials, Matrix4x4 local)
        {
            Mesh = mesh;
            Materials = materials;
            Local = local;
        }

        internal Mesh Mesh { get; }

        internal Material?[] Materials { get; }

        internal Matrix4x4 Local { get; }
    }

    private sealed class LiveView
    {
        internal Camera? Camera { get; set; }

        internal RenderTexture? Target { get; set; }

        internal UnitDefinition? Wanted { get; set; }

        internal UnitDefinition? Shown { get; set; }

        internal int WantedFrame { get; set; }

        internal float Yaw { get; set; }
    }
}
