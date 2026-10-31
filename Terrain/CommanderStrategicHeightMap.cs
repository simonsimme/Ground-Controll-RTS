using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NuclearOption.Effects;
using UnityEngine;
using UnityEngine.Rendering;

namespace GroundControlRts;

internal sealed class CommanderStrategicHeightMap
{
    private const float TargetMetersPerPixel = 20f;
    private const int MaximumResolution = 4096;

    private ushort[]? heights;
    private RenderTexture? target;
    private Vector2 mapSize;
    private float minimumHeight;
    private float heightRange;
    private int generation;
    private float startedAt;

    internal bool IsReady => heights != null;

    internal bool IsBaking => target != null;

    internal Vector2 MapSize => mapSize;

    internal int ResolutionX { get; private set; }

    internal int ResolutionY { get; private set; }

    internal void TryStart(MapSettings map)
    {
        if (IsReady || IsBaking)
        {
            return;
        }

        TerrainHeightMap? source = SceneSingleton<TerrainHeightMap>.i;
        if (source == null || !source.IsActive)
        {
            return;
        }

        Material? material = typeof(TerrainHeightMap).GetField(
            "bakeMaterial",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source) as Material;
        if (material == null || typeof(TerrainHeightMap).GetField(
            "bakeMaterialProps",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source) is not MaterialPropertyBlock properties)
        {
            return;
        }

        mapSize = map.MapSize;
        minimumHeight = source.height.Min;
        heightRange = Mathf.Max(1f, source.height.GetRange());
        int limit = Mathf.Min(MaximumResolution, Mathf.Max(1, SystemInfo.maxTextureSize));
        ResolutionX = Mathf.Min(
            Mathf.NextPowerOfTwo(Mathf.CeilToInt(mapSize.x / TargetMetersPerPixel)),
            limit);
        ResolutionY = Mathf.Min(
            Mathf.NextPowerOfTwo(Mathf.CeilToInt(mapSize.y / TargetMetersPerPixel)),
            limit);

        List<TerrainHeightMap.Entry> providers = new();
        List<TerrainHeightMap.Entry> blockers = new();
        source.GetObjectsInBounds(
            new GlobalPosition(0f, 0f, 0f),
            Mathf.Max(mapSize.x, mapSize.y),
            providers,
            blockers);
        if (providers.Count == 0)
        {
            return;
        }

        target = new RenderTexture(ResolutionX, ResolutionY, 16, RenderTextureFormat.R16)
        {
            name = "GroundControl_StrategicHeightMap",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        target.Create();
        using CommandBuffer command = new() { name = "GroundControl strategic heightmap" };
        Matrix4x4 view = Matrix4x4.TRS(
            new GlobalPosition(0f, 0f, 0f).ToLocalPosition(),
            Quaternion.Euler(90f, 0f, 0f),
            Vector3.one).inverse;
        Matrix4x4 projection = Matrix4x4.Ortho(
            -mapSize.x * 0.5f,
            mapSize.x * 0.5f,
            -mapSize.y * 0.5f,
            mapSize.y * 0.5f,
            0f,
            heightRange);
        command.SetViewProjectionMatrices(view, projection);
        command.SetRenderTarget(target);
        command.ClearRenderTarget(true, true, Color.black, 0f);
        foreach (TerrainHeightMap.Entry entry in providers)
        {
            for (int subMesh = 0; subMesh < entry.mesh.subMeshCount; subMesh++)
            {
                command.DrawMesh(
                    entry.mesh,
                    entry.renderer.transform.localToWorldMatrix,
                    material,
                    subMesh,
                    -1,
                    properties);
            }
        }

        Graphics.ExecuteCommandBuffer(command);
        startedAt = Time.realtimeSinceStartup;
        int requestGeneration = ++generation;
        AsyncGPUReadback.Request(target, 0, request => CompleteReadback(request, requestGeneration));
        CommanderPlugin.Log.LogInfo(
            $"Strategic heightmap bake started: {ResolutionX}x{ResolutionY}, "
            + $"{mapSize.x / ResolutionX:0.0}x{mapSize.y / ResolutionY:0.0}m/px, "
            + $"providers={providers.Count}.");
    }

    internal bool TryGetHeight(float globalX, float globalZ, out float height)
    {
        if (heights == null
            || globalX < -mapSize.x * 0.5f
            || globalX > mapSize.x * 0.5f
            || globalZ < -mapSize.y * 0.5f
            || globalZ > mapSize.y * 0.5f)
        {
            height = 0f;
            return false;
        }

        float pixelX = Mathf.Clamp01((globalX / mapSize.x) + 0.5f) * (ResolutionX - 1);
        float pixelY = Mathf.Clamp01((globalZ / mapSize.y) + 0.5f) * (ResolutionY - 1);
        int x0 = Mathf.FloorToInt(pixelX);
        int y0 = Mathf.FloorToInt(pixelY);
        int x1 = Mathf.Min(x0 + 1, ResolutionX - 1);
        int y1 = Mathf.Min(y0 + 1, ResolutionY - 1);
        float tx = pixelX - x0;
        float ty = pixelY - y0;
        float bottom = Mathf.Lerp(Decode(heights[(y0 * ResolutionX) + x0]), Decode(heights[(y0 * ResolutionX) + x1]), tx);
        float top = Mathf.Lerp(Decode(heights[(y1 * ResolutionX) + x0]), Decode(heights[(y1 * ResolutionX) + x1]), tx);
        height = Mathf.Lerp(bottom, top, ty);
        return true;
    }

    internal bool TryGetHeightNearest(float globalX, float globalZ, out float height)
    {
        if (heights == null
            || globalX < -mapSize.x * 0.5f
            || globalX > mapSize.x * 0.5f
            || globalZ < -mapSize.y * 0.5f
            || globalZ > mapSize.y * 0.5f)
        {
            height = 0f;
            return false;
        }

        int x = Mathf.Clamp(
            Mathf.RoundToInt(((globalX / mapSize.x) + 0.5f) * (ResolutionX - 1)),
            0,
            ResolutionX - 1);
        int y = Mathf.Clamp(
            Mathf.RoundToInt(((globalZ / mapSize.y) + 0.5f) * (ResolutionY - 1)),
            0,
            ResolutionY - 1);
        height = Decode(heights[(y * ResolutionX) + x]);
        return true;
    }

    internal float EstimateNormalY(float globalX, float globalZ, float spacing = 25f)
    {
        if (!TryGetHeight(globalX - spacing, globalZ, out float left)
            || !TryGetHeight(globalX + spacing, globalZ, out float right)
            || !TryGetHeight(globalX, globalZ - spacing, out float down)
            || !TryGetHeight(globalX, globalZ + spacing, out float up))
        {
            return 0f;
        }

        Vector3 normal = new Vector3(left - right, spacing * 2f, down - up).normalized;
        return normal.y;
    }

    internal void Reset()
    {
        generation++;
        heights = null;
        ReleaseTarget();
        mapSize = Vector2.zero;
        ResolutionX = 0;
        ResolutionY = 0;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed GPU readback leaves the height map unbaked, which the flight planner already handles.")]
    private void CompleteReadback(AsyncGPUReadbackRequest request, int requestGeneration)
    {
        try
        {
            if (requestGeneration != generation || request.hasError)
            {
                return;
            }

            Unity.Collections.NativeArray<ushort> data = request.GetData<ushort>();
            ushort[] copy = new ushort[data.Length];
            data.CopyTo(copy);
            heights = copy;
            CommanderPlugin.Log.LogInfo(
                $"Strategic heightmap ready: duration={Time.realtimeSinceStartup - startedAt:0.000}s, "
                + $"memory={(long)copy.Length * 2 / 1048576f:0.0} MiB.");
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogWarning($"Strategic heightmap readback failed: {exception.Message}");
        }
        finally
        {
            ReleaseTarget();
        }
    }

    private float Decode(ushort encoded)
    {
        return minimumHeight + (encoded / 65535f * heightRange);
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
}
