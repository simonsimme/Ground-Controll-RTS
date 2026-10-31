namespace GroundControlRts;

/// <summary>One thing a side can draft: a vehicle or an airframe.</summary>
internal sealed class DraftEntry
{
    internal DraftEntry(string key, string label, string detail, DraftGroup group, float cost, UnitDefinition? definition)
    {
        Key = key;
        Label = label;
        Detail = detail;
        Group = group;
        Cost = cost;
        Definition = definition;
    }

    /// <summary>The definition's jsonKey.</summary>
    internal string Key { get; }

    internal string Label { get; }

    internal string Detail { get; }

    internal DraftGroup Group { get; }

    internal float Cost { get; }

    internal UnitDefinition? Definition { get; }
}
