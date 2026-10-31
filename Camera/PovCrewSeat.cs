using UnityEngine;

namespace GroundControlRts;

internal sealed class PovCrewSeat
{
    internal PovCrewSeat(
        string label,
        Transform anchor,
        Pilot? pilot,
        Turret? turret = null,
        Transform? viewDirection = null)
    {
        Label = label;
        Anchor = anchor;
        Pilot = pilot;
        Turret = turret;
        ViewDirection = viewDirection;
    }

    internal string Label { get; }

    internal Transform Anchor { get; }

    internal Pilot? Pilot { get; }

    internal Turret? Turret { get; }

    internal Transform? ViewDirection { get; }

    internal bool IsAvailable => Anchor != null
        && (Pilot == null || (!Pilot.dead && !Pilot.ejected));
}
