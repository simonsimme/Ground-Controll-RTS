using UnityEngine;

namespace GroundControlRts;

/// <summary>Formation shape used to spread a group around its ordered waypoint.</summary>
internal enum CommanderFormationShape
{
    Ring,
    Line,
    Column,
    Wedge,
}

internal static class CommanderDestinationFormation
{
    private const int SlotsPerRing = 8;

    /// <summary>Concentric rings, used where no travel direction is known (depot rally points).</summary>
    internal static GlobalPosition ApplyOffset(GlobalPosition center, int slotIndex, float spacing)
    {
        return ApplyOffset(center, slotIndex, spacing, CommanderFormationShape.Ring, 0f);
    }

    /// <summary>
    /// Slot 0 always sits on the waypoint itself. Line, column and wedge are built in the
    /// travel frame (+Z is the heading) so the group arrives facing the way it drove.
    /// </summary>
    internal static GlobalPosition ApplyOffset(
        GlobalPosition center,
        int slotIndex,
        float spacing,
        CommanderFormationShape shape,
        float headingDegrees)
    {
        if (slotIndex <= 0 || spacing <= 0f)
        {
            return center;
        }

        if (shape == CommanderFormationShape.Ring)
        {
            return center + RingOffset(slotIndex, spacing);
        }

        int rank = (slotIndex + 1) / 2;
        float side = (slotIndex & 1) == 1 ? -1f : 1f;
        Vector3 local = shape switch
        {
            CommanderFormationShape.Line => new Vector3(side * rank * spacing, 0f, 0f),
            CommanderFormationShape.Wedge => new Vector3(side * rank * spacing, 0f, -rank * spacing),
            _ => new Vector3(0f, 0f, -slotIndex * spacing),
        };
        return center + (Quaternion.Euler(0f, headingDegrees, 0f) * local);
    }

    private static Vector3 RingOffset(int slotIndex, float spacing)
    {
        int zeroBasedSlot = slotIndex - 1;
        int ring = (zeroBasedSlot / SlotsPerRing) + 1;
        int slotInRing = zeroBasedSlot % SlotsPerRing;
        float angle = slotInRing * (360f / SlotsPerRing);
        if ((ring & 1) == 0)
        {
            angle += 360f / (SlotsPerRing * 2f);
        }

        return Quaternion.Euler(0f, angle, 0f) * Vector3.forward * (spacing * ring);
    }
}
