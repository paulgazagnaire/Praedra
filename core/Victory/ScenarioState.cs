using Rebellion.Core.Clock;

namespace Rebellion.Core.Victory;

/// <summary>
/// Per-game scenario state, saved and hashed: whether victory is checked, the Rebel HQ system, the explored flags dealt at setup and the
/// result. rule: setup-victory/04-victory-conditions §3 (HQ-Only mode); setup-victory/09-day-one-state-360 §4 (explored), §5 (HQ).
/// DEFAULT: the HQ does not move yet (HQ move is M5 fog-and-hq work); explored flags are stored but nothing reads them in the core yet.
/// </summary>
public sealed class ScenarioState
{
    /// <summary>Victory is only checked in a game made by NewGame (a bare GameHost.New has no owners and would end on day 1).</summary>
    public bool Active { get; set; }
    /// <summary>The Rebel HQ system, null = none.</summary>
    public string? HqSystem { get; set; }
    /// <summary>The winner, null while the game runs. Set once.</summary>
    public Side? Winner { get; set; }
    public string? WinReason { get; set; }
    /// <summary>Per system: bit 1 = known to the Empire, bit 2 = known to the Alliance. A system not listed is known to nobody.</summary>
    public SortedDictionary<string, int> Explored { get; init; } = new(StringComparer.Ordinal);

    public static int Bit(Side s) => s == Side.Empire ? 1 : 2;

    internal void CopyFrom(ScenarioState o)
    {
        Active = o.Active; HqSystem = o.HqSystem; Winner = o.Winner; WinReason = o.WinReason;
        foreach (var kv in o.Explored) Explored[kv.Key] = kv.Value;
    }
}
