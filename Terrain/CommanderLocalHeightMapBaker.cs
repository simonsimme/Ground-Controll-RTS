using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NuclearOption.Effects;
using UnityEngine;
using UnityEngine.Rendering;

namespace GroundControlRts;

internal sealed class CommanderLocalHeightMapBaker
{
    private const int Resolution = 1024;
    private RenderTexture? target;
    private int generation;

    internal bool IsBusy => target != null;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed GPU readback leaves the height map unbaked, which the flight planner already handles.")]
    internal bool TryBake(GlobalPosition center, float size, Action<LocalHeightMap?> completed)
    {
        if (IsBusy)
        {
            return false;
        }

        TerrainHeightMap? source = SceneSingleton<TerrainHeightMap>.i;
        if (source == null)
        {
            return false;
        }

        Material? material = typeof(TerrainHeightMap).GetField(
            "bakeMaterial",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source) as Material;
        if (material == null || typeof(TerrainHeightMap).GetField(
            "bakeMaterialProps",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source) is not MaterialPropertyBlock properties)
        {
            return false;
        }

        List<TerrainHeightMap.Entry> providers = new();
        List<TerrainHeightMap.Entry> blockers = new();
        source.GetObjectsInBounds(center, size, providers, blockers);
        if (providers.Count == 0)
        {
            return false;
        }

        target = new RenderTexture(Resolution, Resolution, 16, RenderTextureFormat.R16)
        {
            name = "GroundControl_LocalTerrain",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        target.Create();
        using CommandBuffer command = new() { name = "GroundControl local terrain" };
        Matrix4x4 view = Matrix4x4.TRS(
            center.ToLocalPosition(),
            Quaternion.Euler(90f, 0f, 0f),
            Vector3.one).inverse;
        float heightRange = Mathf.Max(1f, source.height.GetRange());
        command.SetViewProjectionMatrices(
            view,
            Matrix4x4.Ortho(-size * 0.5f, size * 0.5f, -size * 0.5f, size * 0.5f, 0f, heightRange));
        command.SetRenderTarget(target);
        command.ClearRenderTarget(true, true, Color.black, 0f);
        foreach (TerrainHeightMap.Entry entry in providers)
        {
            for (int subMesh = 0; subMesh < entry.mesh.subMeshCount; subMesh++)
            {
                command.DrawMesh(entry.mesh, entry.renderer.transform.localToWorldMatrix, material, subMesh, -1, properties);
            }
        }

        Graphics.ExecuteCommandBuffer(command);

        int requestGeneration = ++generation;
        RenderTexture requestTarget = target;
        AsyncGPUReadback.Request(requestTarget, 0, request =>
        {
            LocalHeightMap? result = null;
            try
            {
                if (requestGeneration == generation && !request.hasError)
                {
                    Unity.Collections.NativeArray<ushort> data = request.GetData<ushort>();
                    ushort[] copy = new ushort[data.Length];
                    data.CopyTo(copy);
                    result = new LocalHeightMap(
                        center,
                        size,
                        Resolution,
                        source.height.Min,
                        heightRange,
                        copy);
                }
            }
            catch (Exception exception)
            {
                CommanderPlugin.Log.LogWarning($"Local heightmap bake failed: {exception.Message}");
            }
            finally
            {
                if (target == requestTarget)
                {
                    ReleaseTarget();
                }

                completed(result);
            }
        });
        return true;
    }

    internal void Reset()
    {
        generation++;
        ReleaseTarget();
    }

    private void ReleaseTarget()
    {
        if (target == null)
        {
            return;
        }

        target.Release();
        UnityEngine.Object.Destroy(target);
        target = null;
    }

    internal sealed class LocalHeightMap
    {
        private readonly int resolution;
        private readonly float minimumHeight;
        private readonly float heightRange;
        private readonly ushort[] heights;

        internal LocalHeightMap(
            GlobalPosition center,
            float size,
            int resolution,
            float minimumHeight,
            float heightRange,
            ushort[] heights)
        {
            Center = center;
            Size = size;
            this.resolution = resolution;
            this.minimumHeight = minimumHeight;
            this.heightRange = heightRange;
            this.heights = heights;
        }

        internal GlobalPosition Center { get; }

        internal float Size { get; }

        internal float MetersPerPixel => Size / resolution;

        internal bool Contains(float x, float z)
        {
            float half = Size * 0.5f;
            return x >= Center.x - half && x <= Center.x + half
                && z >= Center.z - half && z <= Center.z + half;
        }

        internal bool TryGetHeight(float x, float z, out float height)
        {
            if (!Contains(x, z))
            {
                height = 0f;
                return false;
            }

            float u = Mathf.Clamp01(((x - Center.x) / Size) + 0.5f) * (resolution - 1);
            float v = Mathf.Clamp01(((z - Center.z) / Size) + 0.5f) * (resolution - 1);
            int x0 = Mathf.FloorToInt(u);
            int y0 = Mathf.FloorToInt(v);
            int x1 = Mathf.Min(x0 + 1, resolution - 1);
            int y1 = Mathf.Min(y0 + 1, resolution - 1);
            ushort bottomLeft = heights[(y0 * resolution) + x0];
            ushort bottomRight = heights[(y0 * resolution) + x1];
            ushort topLeft = heights[(y1 * resolution) + x0];
            ushort topRight = heights[(y1 * resolution) + x1];
            if (bottomLeft == 0 || bottomRight == 0 || topLeft == 0 || topRight == 0)
            {
                height = 0f;
                return false;
            }

            float bottom = Mathf.Lerp(Decode(bottomLeft), Decode(bottomRight), u - x0);
            float top = Mathf.Lerp(Decode(topLeft), Decode(topRight), u - x0);
            height = Mathf.Lerp(bottom, top, v - y0);
            return true;
        }

        internal float EstimateNormalY(float x, float z, float spacing = 2f)
        {
            if (!TryGetHeight(x - spacing, z, out float left)
                || !TryGetHeight(x + spacing, z, out float right)
                || !TryGetHeight(x, z - spacing, out float down)
                || !TryGetHeight(x, z + spacing, out float up))
            {
                return 0f;
            }

            return new Vector3(left - right, spacing * 2f, down - up).normalized.y;
        }

        private float Decode(ushort value)
        {
            return minimumHeight + (value / 65535f * heightRange);
        }
    }
}
