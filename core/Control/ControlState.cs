using Rebellion.Core.Clock;

namespace Rebellion.Core.Control;

/// <summary>
/// Per-game control settings that are saved and hashed. Today only the scorched-earth switch per side. Nothing in M2 reads it; the M6 AI
/// does. rule: m2-control-and-capture §Scorched earth ("a stored setting per game, per side: off, on"). Value: on, both sides
/// (Decided, Paul 2026-10-12, economy-ai/08 D3), so a new game starts with both on.
/// </summary>
public sealed class ControlState
{
    public bool ScorchedEarthEmpire { get; set; } = true;
    public bool ScorchedEarthAlliance { get; set; } = true;

    public bool ScorchedEarth(Side s) => s == Side.Empire ? ScorchedEarthEmpire : ScorchedEarthAlliance;

    public void SetScorchedEarth(Side s, bool on)
    {
        if (s == Side.Empire) ScorchedEarthEmpire = on; else ScorchedEarthAlliance = on;
    }

    internal void CopyFrom(ControlState o)
    {
        ScorchedEarthEmpire = o.ScorchedEarthEmpire;
        ScorchedEarthAlliance = o.ScorchedEarthAlliance;
    }
}
