using Rebellion.Core.Setup;

namespace Rebellion.Core.Tests.Setup;

/// <summary>
/// Settings for tests whose subject is not the day-one deal and whose expectations were tuned on the fixed day-one files:
/// DealMode.File reproduces that deal exactly (m5-setup-and-victory §Mode Anchored, last paragraph). The DEFAULT deal is Anchored.
/// </summary>
internal static class DealSettings
{
    public static readonly GameSettings File = new() { DealMode = DealMode.File };
    /// <summary>The fixed deal plus the victory and research rules of before 2026-10-16 (<see cref="OldVictory"/>): AI tests tuned on both.</summary>
    public static GameSettings FileOldVictory => new() { DealMode = DealMode.File, Victory = OldVictory.Rules, Research = OldResearch };
    /// <summary>Research as before 2026-10-16: no galaxy-size scaling, blockade does not stop yards (m5-research §Defaults).</summary>
    public static Rebellion.Core.Research.ResearchParams OldResearch => new() { ResearchCostScale = Rebellion.Core.Research.ResearchScale.None, MissionPointsScale = Rebellion.Core.Research.ResearchScale.None, BlockadeStopsResearch = false };
}
