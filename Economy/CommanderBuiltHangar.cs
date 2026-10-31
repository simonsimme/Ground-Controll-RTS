using System.Collections.Generic;
using NuclearOption.Effects;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// A built hangar stands on a level concrete floor. A building is dropped onto the terrain under its
/// middle only, so on any slope part of the hangar floor was underground and the grass grew through
/// it. A hangar is raised to the highest ground under its footprint instead, and a slab fills the
/// space below, registered with the game's own grass blocker the way an airfield apron is.
/// </summary>
internal static class CommanderBuiltHangar
{
    /// <summary>Steepest drop across the footprint a hangar may be levelled over. Past this the slab
    /// is a wall on the downhill side and the aircraft roll off a ledge.</summary>
    internal const float MaxStepMeters = 5f;

    private const string FloorName = "GCRTS Floor";

    private const string ApronName = "GCRTS Apron";

    /// <summary>Past half a wingspan, to the taxiway centreline: the taxi's look-ahead cuts the corner.</summary>
    private const float TurnRoomMeters = 8f;

    /// <summary>Where the apron stops, short of the taxiway centreline, inside its pavement.</summary>
    private const float ApronShortOfCentreMeters = 4f;

    /// <summary>Length of each section of a ramp, so its sides follow the ground.</summary>
    private const float RampStepMeters = 4f;

    /// <summary>Metres out per metre down on a ramp's sides: a gentle embankment.</summary>
    private const float RampSideSlope = 2f;

    /// <summary>Farthest a ramp's side runs out past its edge, wherever the ground under it is.</summary>
    private const float RampMaxSpreadMeters = 6f;

    /// <summary>How far a ramp's sides run on below the grass, so no gap shows at the toe.</summary>
    private const float RampSinkMeters = 0.3f;

    /// <summary>How far onto a map road's pavement the ramp from a hangar's doors runs.</summary>
    private const float ApronOntoPavementMeters = 3f;

    /// <summary>How far out of its doors a hangar looks for a taxiway to join.</summary>
    private const float TaxiSpurMeters = 120f;

    /// <summary>Above the highest ground under the footprint, so the terrain never shows through.</summary>
    private const float LiftMeters = 0.1f;

    /// <summary>Dark enough to sit beside the airfield's own concrete in full sun (0.46 read white).</summary>
    private static readonly Color Concrete = new(0.27f, 0.27f, 0.26f, 1f);
    private static readonly Dictionary<BuildingDefinition, bool> Hangars = new();
    private static readonly Dictionary<BuildingDefinition, bool> Floors = new();
    private static readonly Dictionary<BuildingDefinition, float> Setbacks = new();

    private static readonly System.Reflection.FieldInfo? AvailableAircraftField =
        HarmonyLib.AccessTools.Field(typeof(Hangar), "availableAircraft");

    private static Material? floorMaterial;

    /// <summary>True for a hangar or landing pad prefab. Cached because the ghost asks per frame.</summary>
    internal static bool IsHangar(BuildingDefinition candidate)
    {
        if (!Hangars.TryGetValue(candidate, out bool hangar))
        {
            hangar = candidate.unitPrefab != null && candidate.unitPrefab.GetComponentInChildren<Hangar>(true) != null;
            Hangars[candidate] = hangar;
        }

        return hangar;
    }

    /// <summary>True for a hangar that is levelled onto a slab. A landing pad sits on the terrain as the
    /// game drops it, with no ground of the mod's under it.</summary>
    internal static bool HasFloor(BuildingDefinition candidate)
    {
        if (!Floors.TryGetValue(candidate, out bool floor))
        {
            floor = IsHangar(candidate)
                && !CommanderGameAccess.IsRotaryHangar(candidate.unitPrefab.GetComponentInChildren<Hangar>(true));
            Floors[candidate] = floor;
        }

        return floor;
    }

    /// <summary>
    /// The ground a hangar at <paramref name="target"/> is put down on: the highest terrain under its
    /// footprint, plus a little. <paramref name="step"/> is how far the ground falls away beneath it.
    /// </summary>
    internal static GlobalPosition GetFloorGround(
        CommanderBuildPreview preview,
        BuildingDefinition candidate,
        GlobalPosition target,
        Quaternion rotation,
        out float step)
    {
        Vector3 half = preview.GetFootprint(candidate);
        Vector3 middle = preview.GetFootprintCentre(candidate) + candidate.spawnOffset;
        float highest = float.MinValue;
        float lowest = float.MaxValue;
        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                Vector3 offset = rotation * new Vector3(middle.x + (x * half.x), 0f, middle.z + (z * half.z));
                float y = CommanderGameAccess.SnapToTerrain(
                    new GlobalPosition(target.x + offset.x, target.y, target.z + offset.z)).y;
                highest = Mathf.Max(highest, y);
                lowest = Mathf.Min(lowest, y);
            }
        }

        step = highest - lowest;
        return new GlobalPosition(target.x, highest + LiftMeters, target.z);
    }

    /// <summary>
    /// Puts the concrete slab under a built hangar, once. Runs wherever the building is known as
    /// built (the host at spawn, a guest from the levels sync), because the slab is not networked.
    /// </summary>
    internal static void EnsureFloor(Unit? unit, CommanderBuildPreview preview)
    {
        if (unit == null
            || unit.definition is not BuildingDefinition definition
            || !HasFloor(definition)
            || unit.transform.Find(FloorName) != null)
        {
            return;
        }

        Transform root = unit.transform;
        Vector3 half = preview.GetFootprint(definition);
        Vector3 middle = preview.GetFootprintCentre(definition);

        // SpawnBuilding puts the pivot at ground + spawnOffset, so the ground is -spawnOffset.y in the
        // building's own frame. A hair below it, so a floor the prefab has of its own wins.
        float top = -definition.spawnOffset.y - 0.02f;
        float bottom = top;
        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                Vector3 corner = root.TransformPoint(new Vector3(middle.x + (x * half.x), 0f, middle.z + (z * half.z)));
                Vector3 ground = CommanderGameAccess.SnapToTerrain(corner.ToGlobalPosition()).ToLocalPosition();
                bottom = Mathf.Min(bottom, root.InverseTransformPoint(ground).y);
            }
        }

        bottom -= 1f;
        Transform slab = MakeSlab(FloorName, root);
        slab.localPosition = new Vector3(middle.x, (top + bottom) * 0.5f, middle.z);
        slab.localScale = new Vector3(half.x * 2f, top - bottom, half.z * 2f);
        AddApron(unit, root, half, middle, top);
    }

    /// <summary>
    /// The ramp from a hangar's doors to the taxiway they face, if it has none yet. A forward base's
    /// taxiways are laid after the floor (the runway comes from the hangar itself), so the floor's own
    /// ramp found nothing to reach.
    /// </summary>
    internal static void EnsureApron(Unit? unit, CommanderBuildPreview preview)
    {
        if (unit == null
            || unit.definition is not BuildingDefinition definition
            || !IsHangar(definition)
            || unit.transform.Find(ApronName) != null)
        {
            return;
        }

        AddApron(unit, unit.transform, preview.GetFootprint(definition), preview.GetFootprintCentre(definition), -definition.spawnOffset.y - 0.02f);
    }

    /// <summary>
    /// Half the widest wingspan the hangar takes plus room to turn: how far its doors stand off a
    /// taxiway's centreline. Turning onto the taxiway swings the inside wing back towards the doors,
    /// and 15 m out an Alkyon's took the door frame. A hangar_med also takes the Darkreach.
    /// </summary>
    internal static float GetTaxiwaySetback(BuildingDefinition candidate)
    {
        if (!Setbacks.TryGetValue(candidate, out float setback))
        {
            float span = 0f;
            Hangar? hangar = candidate.unitPrefab != null ? candidate.unitPrefab.GetComponentInChildren<Hangar>(true) : null;
            if (hangar != null && AvailableAircraftField?.GetValue(hangar) is AircraftDefinition[] aircraft)
            {
                for (int i = 0; i < aircraft.Length; i++)
                {
                    span = aircraft[i] != null ? Mathf.Max(span, aircraft[i].width) : span;
                }
            }

            setback = (span * 0.5f) + TurnRoomMeters;
            Setbacks[candidate] = setback;
            CommanderPlugin.Log.LogInfo($"{candidate.unitName}: widest airframe {span:0} m, stands {setback:0} m off a taxiway.");
        }

        return setback;
    }

    /// <summary>
    /// Lays a taxiway from the hangar's parking spot straight out of its doors to the airfield taxiway
    /// they face, joined to the field's network. The taxi AI's first waypoint is the nearest point on
    /// the network, which beside a built hangar is off at an angle and through a door frame: an
    /// Alkyon drove into its own hangar walls. Host only, since only the host flies the AI.
    /// </summary>
    internal static bool ConnectToTaxiway(Unit? unit)
    {
        Hangar? hangar = unit != null ? unit.GetComponentInChildren<Hangar>(true) : null;
        Transform? spawn = hangar != null ? hangar.GetSpawnTransform() : null;
        RoadNetwork? taxi = GetTaxiNetwork(unit, hangar);

        // A landing pad's helicopters lift off from the pad, they never taxi to a road.
        if (spawn == null || taxi == null || !taxi.Exists() || CommanderGameAccess.IsRotaryHangar(hangar))
        {
            return false;
        }

        Vector3 forward = spawn.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        forward.Normalize();
        GlobalPosition start = spawn.GlobalPosition();
        if (!TryCastToRoad(taxi, start, forward, TaxiSpurMeters, out Road? road, out int index, out GlobalPosition junction))
        {
            CommanderPlugin.Log.LogInfo($"{unit!.name}: no taxiway ahead of its doors, so no taxiway was laid to it.");
            return false;
        }

        JoinAt(taxi, road!, index, junction);

        // Starts a few metres behind the parking spot, so the aircraft is on the spur, not past its end.
        Road spur = new();
        spur.AddPoint(start - (forward * 5f));
        spur.AddPoint(junction);
        spur.CalcLength();
        taxi.roads.Add(spur);
        taxi.RegenerateNetwork();
        CommanderPlugin.Log.LogInfo(
            $"{unit!.name}: laid a {spur.length:0} m taxiway from its doors to the taxi network.");
        return true;
    }

    /// <summary>Makes <paramref name="junction"/>, on segment <paramref name="index"/> of
    /// <paramref name="road"/>, a node another road can end at.</summary>
    internal static void JoinAt(RoadNetwork taxi, Road road, int index, GlobalPosition junction)
    {
        // A junction is a node, and nodes only sit at road ends: the game's editor splits the road it
        // lands on (Road.TrySplit, a point within 10 m) and merges ends within 10 m into one node.
        List<GlobalPosition> points = road.points;
        if (FastMath.Distance(junction, points[0]) > 10f && FastMath.Distance(junction, points[points.Count - 1]) > 10f)
        {
            points.Insert(index, junction);
            road.UpdateBB();
            road.TrySplit(taxi, junction);
        }
    }

    /// <summary>
    /// Paves the ground from a hangar's doors to the taxiway they face, sloping from the floor to the
    /// taxiway. Without it the jet saw grass ahead, which is what lets the mod's straight-out hold go
    /// (a revetment that opens onto grass short of the taxiway), and turned with a wing still in the
    /// doorway.
    /// </summary>
    private static void AddApron(Unit unit, Transform root, Vector3 half, Vector3 middle, float top)
    {
        Hangar? hangar = unit.GetComponentInChildren<Hangar>(true);
        Transform? spawn = hangar != null ? hangar.GetSpawnTransform() : null;
        RoadNetwork? taxi = GetTaxiNetwork(unit, hangar);
        if (spawn == null || taxi == null || !taxi.Exists() || CommanderGameAccess.IsRotaryHangar(hangar))
        {
            return;
        }

        Vector3 forward = spawn.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            return;
        }

        // No taxiway crossing ahead still leaves map pavement: a revetment facing the start of its own
        // runway line looks straight down the line's end, which the cast does not count.
        bool crossing = TryCastToRoad(taxi, spawn.GlobalPosition(), forward.normalized, TaxiSpurMeters, out _, out _, out GlobalPosition junction);
        if (!crossing)
        {
            junction = spawn.GlobalPosition() + (forward.normalized * TaxiSpurMeters);
        }

        // The front wall along the door's axis, in the building's frame.
        forward.Normalize();
        Vector3 door = root.InverseTransformDirection(forward);
        float front = Vector3.Dot(middle, door) + (Mathf.Abs(door.x) * half.x) + (Mathf.Abs(door.z) * half.z);
        float width = (Mathf.Abs(door.z) * half.x * 2f) + (Mathf.Abs(door.x) * half.z * 2f);
        Vector3 spawnLocal = root.InverseTransformPoint(spawn.position);
        Vector3 start = spawn.position + (forward * (front - Vector3.Dot(spawnLocal, door)));
        start.y = root.TransformPoint(new Vector3(0f, top, 0f)).y;

        // Into the taxiway's own pavement, short of its centreline, at the taxiway's height.
        Vector3 end = junction.ToLocalPosition() - (forward * ApronShortOfCentreMeters);
        end.y = Mathf.Max(CommanderGameAccess.SnapToTerrain(end.ToGlobalPosition()).ToLocalPosition().y, junction.ToLocalPosition().y) + 0.03f;

        // A forward base's runway runs down a map road, whose mesh can stand well above the grass: the
        // ramp climbs onto it and stops just past its edge rather than paving half the road.
        float reach = Vector3.Dot(end - start, forward);
        for (float d = 0f; d < reach; d += 0.5f)
        {
            GlobalPosition probe = (start + (forward * d)).ToGlobalPosition();
            if (CommanderBuildPreview.IsPavement(probe))
            {
                GlobalPosition onRoad = probe + (forward * ApronOntoPavementMeters);
                end = new GlobalPosition(onRoad.x, CommanderBuildPreview.GetSurfaceY(onRoad) + 0.03f, onRoad.z).ToLocalPosition();
                crossing = true;
                break;
            }
        }

        if (!crossing || Vector3.Dot(end - start, forward) < 1f)
        {
            return;
        }

        // The taxiway's raised edge stood above the slope and showed through it as a notch. Lift the
        // low end until the slab's top clears the first static surface under each edge and the middle.
        Vector3 side = Vector3.Cross(Vector3.up, forward) * (width * 0.5f);
        for (int i = 1; i <= 4; i++)
        {
            float along = i * 0.25f;
            Vector3 point = Vector3.Lerp(start, end, along);
            for (int s = -1; s <= 1; s++)
            {
                if (Physics.Raycast(point + (side * s) + (Vector3.up * 5f), Vector3.down, out RaycastHit hit, 30f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)
                    && hit.collider.GetComponentInParent<Unit>() == null)
                {
                    end.y = Mathf.Max(end.y, start.y + ((hit.point.y + 0.03f - start.y) / along));
                }
            }
        }

        Vector3 run = end - start;
        MakeRamp(root, start, end, width);
        CommanderPlugin.Log.LogInfo(
            $"{unit.name}: laid a {run.magnitude:0} m ramp, {width:0} m wide, from its doors, rising {run.y:0.0} m, to pos:{end.ToGlobalPosition().x:0},{end.ToGlobalPosition().z:0}.");
    }

    /// <summary>
    /// The ramp as an embankment: a paved top from <paramref name="start"/> to <paramref name="end"/>,
    /// <paramref name="width"/> across, and each side sloping out and down into the ground at
    /// <see cref="RampSideSlope"/>, so it reads as earthworks under concrete rather than a box on the
    /// grass with walls for sides. Built in <paramref name="root"/>'s frame, so it moves with it.
    /// </summary>
    private static void MakeRamp(Transform root, Vector3 start, Vector3 end, float width)
    {
        Vector3 along = end - start;
        Vector3 flat = new(along.x, 0f, along.z);
        Vector3 side = Vector3.Cross(Vector3.up, flat.normalized) * (width * 0.5f);
        int steps = Mathf.Clamp(Mathf.CeilToInt(flat.magnitude / RampStepMeters), 1, 32);
        List<Vector3> vertices = new();
        List<int> triangles = new();
        Vector3[] top = new Vector3[(steps + 1) * 2];
        Vector3[] foot = new Vector3[(steps + 1) * 2];
        for (int i = 0; i <= steps; i++)
        {
            Vector3 centre = Vector3.Lerp(start, end, i / (float)steps);
            for (int s = 0; s < 2; s++)
            {
                Vector3 edge = centre + (s == 0 ? -side : side);
                Vector3 outward = (s == 0 ? -side : side).normalized;

                // Out until the slope meets the ground, sunk a little so no seam shows.
                float drop = Mathf.Max(0f, edge.y - GroundUnder(edge));
                Vector3 toe = edge + (outward * Mathf.Min(drop * RampSideSlope, RampMaxSpreadMeters));
                toe.y = GroundUnder(toe) - RampSinkMeters;
                top[(i * 2) + s] = root.InverseTransformPoint(edge);
                foot[(i * 2) + s] = root.InverseTransformPoint(toe);
            }
        }

        Vector3 up = root.InverseTransformDirection(Vector3.up);
        Vector3 left = root.InverseTransformDirection(-side);
        for (int i = 0; i < steps; i++)
        {
            int a = i * 2;
            int b = (i + 1) * 2;
            AddQuad(vertices, triangles, top[a], top[b], top[b + 1], top[a + 1], up);
            AddQuad(vertices, triangles, top[a], foot[a], foot[b], top[b], up + left);
            AddQuad(vertices, triangles, top[a + 1], top[b + 1], foot[b + 1], foot[a + 1], up - left);
        }

        Mesh mesh = new() { name = ApronName };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        GameObject ramp = new(ApronName);
        ramp.AddComponent<MeshFilter>().sharedMesh = mesh;
        ramp.AddComponent<MeshRenderer>();
        ramp.AddComponent<MeshCollider>().sharedMesh = mesh;
        Dress(ramp, root);
    }

    /// <summary>A quad as two triangles with vertices of its own, wound to face
    /// <paramref name="outward"/>, so recalculated normals stay flat per face.</summary>
    private static void AddQuad(List<Vector3> vertices, List<int> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
    {
        int first = vertices.Count;
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        vertices.Add(d);
        bool facing = Vector3.Dot(Vector3.Cross(b - a, c - a), outward) >= 0f;
        if (facing)
        {
            triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
        }
        else
        {
            triangles.AddRange(new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
        }
    }

    /// <summary>
    /// The terrain or map pavement under a point. Terrain alone sits 16-20 m under Heartland's road
    /// meshes, so a ramp edge over such a road would slope out tens of metres sideways to reach it.
    /// </summary>
    private static float GroundUnder(Vector3 local)
    {
        GlobalPosition point = local.ToGlobalPosition();
        return new GlobalPosition(point.x, CommanderBuildPreview.GetSurfaceY(point), point.z).ToLocalPosition().y;
    }

    /// <summary>A grey box under <paramref name="parent"/>, solid, and kept clear of grass.</summary>
    private static Transform MakeSlab(string name, Transform parent)
    {
        GameObject slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slab.name = name;
        Dress(slab, parent);
        return slab.transform;
    }

    /// <summary>Statics layer, under <paramref name="parent"/>, concrete, and kept clear of grass.</summary>
    private static void Dress(GameObject slab, Transform parent)
    {
        slab.layer = PhysicsLayers.Statics;
        slab.transform.SetParent(parent, false);

        MeshRenderer renderer = slab.GetComponent<MeshRenderer>();
        Material? material = GetFloorMaterial(parent);
        if (material != null)
        {
            renderer.sharedMaterial = material;
        }

        // The game keeps grass off its aprons by drawing them into a blocker map; this is that
        // component, which also takes the slab back out when the hangar is destroyed. It registers
        // in Start, next frame, so the caller's placement is in by then.
        if (SceneSingleton<TerrainHeightMap>.i != null)
        {
            TerrainHeightMapBlocker blocker = slab.AddComponent<TerrainHeightMapBlocker>();
            blocker.Renderers = new List<TerrainHeightMap.RenderFilter> { new(renderer) };
        }
    }

    private static RoadNetwork? GetTaxiNetwork(Unit? unit, Hangar? hangar)
    {
        Airbase? airbase = hangar != null && hangar.parentAirbase != null
            ? hangar.parentAirbase
            : unit is Building building ? building.Networkairbase : null;
        return airbase != null ? airbase.GetTaxiNetwork() : null;
    }

    /// <summary>The first taxiway segment a ray from <paramref name="from"/> crosses within
    /// <paramref name="range"/>, with the index to insert the crossing at.</summary>
    private static bool TryCastToRoad(
        RoadNetwork network,
        GlobalPosition from,
        Vector3 direction,
        float range,
        out Road? hit,
        out int index,
        out GlobalPosition junction)
    {
        hit = null;
        index = 0;
        junction = from;
        float best = range;
        for (int r = 0; r < network.roads.Count; r++)
        {
            Road road = network.roads[r];
            for (int i = 1; road?.points != null && i < road.points.Count; i++)
            {
                GlobalPosition a = road.points[i - 1];
                GlobalPosition b = road.points[i];

                // Ray against segment in the ground plane: from + direction * t = a + (b - a) * s.
                float ex = b.x - a.x;
                float ez = b.z - a.z;
                float denominator = (direction.x * ez) - (direction.z * ex);
                if (Mathf.Abs(denominator) < 0.0001f)
                {
                    continue;
                }

                float ax = a.x - from.x;
                float az = a.z - from.z;
                float t = ((ax * ez) - (az * ex)) / denominator;
                float s = ((ax * direction.z) - (az * direction.x)) / denominator;
                if (t > 0f && t < best && s >= 0f && s <= 1f)
                {
                    best = t;
                    hit = road;
                    index = i;
                    junction = new GlobalPosition(a.x + (ex * s), a.y + ((b.y - a.y) * s), a.z + (ez * s));
                }
            }
        }

        return hit != null;
    }

    /// <summary>
    /// Plain grey on the game's own lit shader. A runtime primitive comes with the built-in pipeline's
    /// default material, which this game's render pipeline draws magenta.
    /// </summary>
    private static Material? GetFloorMaterial(Transform building)
    {
        if (floorMaterial != null)
        {
            return floorMaterial;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader != null)
        {
            floorMaterial = new Material(shader);
        }
        else
        {
            MeshRenderer? own = building.GetComponentInChildren<MeshRenderer>(true);
            if (own == null || own.sharedMaterial == null)
            {
                return null;
            }

            floorMaterial = new Material(own.sharedMaterial);
        }

        floorMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
        floorMaterial.SetColor("_BaseColor", Concrete);
        floorMaterial.SetColor("_Color", Concrete);
        floorMaterial.SetFloat("_Smoothness", 0.1f);
        return floorMaterial;
    }
}
