using Praedra.Core.Clock;

namespace Praedra.Core.Research;

/// <summary>
/// One side's research: points, level and the exhausted flag per track (lists of three, fixed order Ship, Troop, Facility), and the day of
/// the next facility tick (0 = not armed: a game made without setup never ticks). rule: m5-research §State per side.
/// </summary>
public sealed class SideResearch
{
    public List<int> Points { get; set; } = new() { 0, 0, 0 };
    public List<int> Level { get; set; } = new() { 0, 0, 0 };
    public List<bool> Exhausted { get; set; } = new() { false, false, false };
    /// <summary>Hook time T of the next facility tick; 0 = not armed. rule: m5-research §Facility income (Arming).</summary>
    public int TickDue { get; set; }

    internal void CopyFrom(SideResearch o)
    {
        Points = new List<int>(o.Points); Level = new List<int>(o.Level); Exhausted = new List<bool>(o.Exhausted); TickDue = o.TickDue;
    }
}

/// <summary>All research state (saved and hashed). The two percents are fixed at setup. rule: m5-research §State per side, §Scaling with galaxy size.</summary>
public sealed class ResearchState
{
    public SideResearch Hand { get; init; } = new();
    public SideResearch Collective { get; init; } = new();
    /// <summary>ResearchCostPercent, used in the level check step 5. 100 until setup sets it.</summary>
    public int CostPercent { get; set; } = 100;
    /// <summary>MissionPointsPercent, used in RollMissionPoints. 100 until setup sets it.</summary>
    public int MissionPercent { get; set; } = 100;

    public SideResearch Of(Side s) => s == Side.Hand ? Hand : Collective;

    internal void CopyFrom(ResearchState o)
    {
        Hand.CopyFrom(o.Hand); Collective.CopyFrom(o.Collective); CostPercent = o.CostPercent; MissionPercent = o.MissionPercent;
    }
}
