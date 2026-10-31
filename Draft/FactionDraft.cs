using System;
using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>What one faction has drafted. Host-side for every faction; a guest keeps its own copy.</summary>
internal sealed class FactionDraft
{
    internal Dictionary<string, int> Picks { get; } = new(StringComparer.Ordinal);

    internal bool Ready { get; set; }

    /// <summary>Filled by the quartermaster (the AI picker) because nobody plays this side.</summary>
    internal bool AutoPicked { get; set; }
}
