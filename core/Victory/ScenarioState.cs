using Rebellion.Core.Clock;

namespace Rebellion.Core.Victory;

/// <summary>
/// Per-game scenario state, saved and hashed: whether victory is checked, the Rebel HQ system, the explored flags dealt at setup and the
/// result. rule: setup-victory/04-victory-conditions §3 (HQ-Only mode); setup-victory/09-day-one-state-360 §4 (explored), §5 (HQ).
/// The HQ itself is <see cref="Hq"/> (m5-fog-and-hq §F5: it can move and be destroyed). The explored flags here are the ones DEALT at setup;
/// the live per-side knowledge is <see cref="Fog.FogState"/>, which the fog module fills from them.
/// </summary>
public sealed class ScenarioState
{
    /// <summary>Victory is only checked in a game made by NewGame (a bare GameHost.New has no owners and would end on day 1).</summary>
    public bool Active { get; set; }
    /// <summary>The Rebel HQ (where it stands, its transit, whether it was destroyed). rule: m5-fog-and-hq §F5 HqState.</summary>
    public Fog.HqState Hq { get; init; } = new();

    /// <summary>
    /// The system the HQ stands at, or stood at when destroyed; null while it is in transit or when there is none. Setting it places the HQ
    /// at rest there (setup only: PlaceHq, raises nothing). Not saved itself: <see cref="Hq"/> is.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? HqSystem
    {
        get => Hq.Status is Fog.HqStatus.AtRest or Fog.HqStatus.Destroyed ? Hq.System : null;
        set => Hq.Place(value);
    }
    /// <summary>The winner, null while the game runs. Set once.</summary>
    public Side? Winner { get; set; }
    public string? WinReason { get; set; }
    /// <summary>Per system: bit 1 = known to the Empire, bit 2 = known to the Alliance. A system not listed is known to nobody.</summary>
    public SortedDictionary<string, int> Explored { get; init; } = new(StringComparer.Ordinal);

    public static int Bit(Side s) => s == Side.Empire ? 1 : 2;

    internal void CopyFrom(ScenarioState o)
    {
        Active = o.Active; Hq.CopyFrom(o.Hq); Winner = o.Winner; WinReason = o.WinReason;
        foreach (var kv in o.Explored) Explored[kv.Key] = kv.Value;
    }
}
