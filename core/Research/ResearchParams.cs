namespace Praedra.Core.Research;

/// <summary>How a research percent is set at setup. rule: m5-research §Scaling with galaxy size.</summary>
public enum ResearchScale
{
    /// <summary>Percent 100 (no scaling). DEFAULT for both settings.</summary>
    None,
    /// <summary>Percent = the number of systems in play at setup, taken once (Classic's reading, Inferred, unverified vs Classic).</summary>
    SystemCount,
    /// <summary>Percent = the matching Fixed value.</summary>
    Fixed,
}

/// <summary>
/// The switches of m5-research, each with the sheet's DEFAULT. Settings, not saved: the percents they produce are fixed at setup and saved in
/// <see cref="ResearchState"/>. rule: m5-research §Defaults for undecided items; CLAUDE.md hard rule 7.
/// </summary>
public sealed record ResearchParams
{
    /// <summary>Yards add points at the research tick. Off: the timer still re-arms and draws, but adds nothing. DEFAULT on. rule: m5-research §Facility income.</summary>
    public bool FacilityResearch { get; init; } = true;
    /// <summary>AddMissionPoints adds points. Off: it adds nothing (draws unchanged). DEFAULT on. rule: m5-research §Defaults.</summary>
    public bool MissionResearch { get; init; } = true;
    /// <summary>DEFAULT SystemCount (Decided by Paul 2026-10-16, economy/14: costs scale with systems / 100). rule: m5-research §Scaling with galaxy size.</summary>
    public ResearchScale ResearchCostScale { get; init; } = ResearchScale.SystemCount;
    /// <summary>The percent used when <see cref="ResearchCostScale"/> is Fixed (the sheet gives no value; 100 = unchanged).</summary>
    public int ResearchCostPercentFixed { get; init; } = 100;
    /// <summary>DEFAULT SystemCount (Decided by Paul 2026-10-16, economy/14: mission points scale like the costs). rule: m5-research §Scaling with galaxy size.</summary>
    public ResearchScale MissionPointsScale { get; init; } = ResearchScale.SystemCount;
    /// <summary>A yard on a system under economic blockade against its holder adds no points at the tick. DEFAULT on (Decided by Paul 2026-10-16, economy/14; uprising does not stop it). rule: m5-research §Facility income (Counting).</summary>
    public bool BlockadeStopsResearch { get; init; } = true;
    /// <summary>The percent used when <see cref="MissionPointsScale"/> is Fixed (the sheet gives no value; 100 = unchanged).</summary>
    public int MissionPointsPercentFixed { get; init; } = 100;
    /// <summary>A startup data check refuses two classes of one side on one rung of one track. DEFAULT on. rule: m5-research §Rule (New) 1.</summary>
    public bool OneClassPerRung { get; init; } = true;
    /// <summary>The Advanced Sensor Station gets a facility rung. DEFAULT off. rule: m5-research §Sensor Station tiers.</summary>
    public bool SensorTier2Research { get; init; }
    /// <summary>Research difficulty of the Advanced Sensor Station: Missing, Paul must supply it. Null with the switch on = the engine refuses to start.</summary>
    public int? SensorTier2Difficulty { get; init; }
}
