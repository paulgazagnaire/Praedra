namespace Praedra.Core.Support;

/// <summary>Garrison rule of the check "G >= N". rule: m2-uprisings-and-garrison §Garrison G and the check.</summary>
public enum GarrisonRuleset
{
    /// <summary>G = number of the holder's regiments on the system (weight 1.0 each).</summary>
    Classic = 0,
    /// <summary>G = sum of regiment weights from the weights file (Decided, Paul 2026-10-12; placeholder weights).</summary>
    New = 1,
}

/// <summary>Why support changed. rule: m2-popular-support §Events that change support.</summary>
public enum SupportCause
{
    UprisingErosion, Presence,
    // hooks for later milestones (the function exists; nothing calls it yet)
    Bombardment, Diplomacy, IncitedUprising, Battle, Harvester, ControlChange, HqMove, Other,
    /// <summary>A Subdue Uprising success (new cause). rule: m5-missions §6 (Lead notes: add SupportCause.Subdue).</summary>
    Subdue,
}

/// <summary>
/// Every number of the two support sheets, with its tag. Data values, not code: tests and later setups may override them.
/// Tags: Traced = read in the Classic binary or data; DEFAULT = the sheet's choice. GNPRTB ids in the comments.
/// rule: m2-popular-support §Numbers; m2-uprisings-and-garrison §Numbers.
/// </summary>
public sealed record SupportParams
{
    // ---- support sheet
    public int SupportMin { get; init; } = 0;     // Traced
    public int SupportMax { get; init; } = 100;   // Traced
    /// <summary>GNPRTB 7681. Collective gains in a harvester sector are divided by this, truncating toward zero. Traced; rounding DEFAULT.</summary>
    public int HarvesterDampingDivisor { get; init; } = 2;
    /// <summary>GNPRTB 7698 (value from economy/05 text). Traced-medium.</summary>
    public int ErosionPeriod { get; init; } = 30;
    /// <summary>GNPRTB 7697. The holder's change per erosion firing. Traced.</summary>
    public int ErosionAmount { get; init; } = -1;
    /// <summary>GNPRTB 7694 and 7696 (values from economy/05 text). Traced-medium.</summary>
    public int PresencePeriod { get; init; } = 30;
    /// <summary>GNPRTB 7693 (+1) and 7695 (-1). Meaning Lead.</summary>
    public int PresenceGain { get; init; } = 1;
    public int PresenceLoss { get; init; } = -1;
    /// <summary>"Majority" means strictly more than this. DEFAULT.</summary>
    public int PresenceMajorityAbove { get; init; } = 50;
    /// <summary>GNPRTB 7691 = 0: Hand passive gain. Dead feature, NOT run in M2 whatever the value (needs period values and the meaning of flag bit 8).</summary>
    public int HandPassiveGain { get; init; } = 0;

    // ---- garrison sheet
    /// <summary>GNPRTB 7761: no garrison is needed at or above this controller support. Traced.</summary>
    public int GarrisonFreeSupport { get; init; } = 60;
    /// <summary>GNPRTB 7762 (-10): one more regiment per this many points below. Traced.</summary>
    public int GarrisonStep { get; init; } = 10;
    /// <summary>GNPRTB 7682. Requirement multiplier during an uprising. Traced.</summary>
    public int UprisingMultiplier { get; init; } = 2;
    /// <summary>GNPRTB 7680. Hand requirement divisor in a harvester sector (truncating, DEFAULT) and the multiplier on Hand regiments in the incident score. Traced.</summary>
    public int HarvesterGarrisonDivisor { get; init; } = 2;
    public int HarvesterScoreMultiplier { get; init; } = 2;
    /// <summary>A regiment class without a weight counts this many tenths (1.0). DEFAULT.</summary>
    public int DefaultWeight10 { get; init; } = 10;
    /// <summary>Tenths per regiment in the Classic check (weight 1.0).</summary>
    public int ClassicWeight10 { get; init; } = 10;

    // ---- incident timer and score
    /// <summary>GNPRTB 7701 / 7702: next incident in Min + rand(0..Spread) days. Traced.</summary>
    public int IncidentIntervalMin { get; init; } = 30;
    public int IncidentIntervalSpread { get; init; } = 70;
    /// <summary>GNPRTB 7707 / 7708: each die is Base + rand(0..Spread). Traced.</summary>
    public int DieBase { get; init; } = 1;
    public int DieSpread { get; init; } = 9;
    /// <summary>The Pacifiers class (0x10000006, TROOPSD id 6). Traced.</summary>
    public string PacifierClass { get; init; } = "Pacifiers";
    /// <summary>UPRIS1TB: (lowest score, code); "largest threshold at or below the score". Scores below the first threshold give code 0. Traced.</summary>
    public IReadOnlyList<(int MinScore, int Code)> Table1 { get; init; } = new[] { (6, 1), (10, 2) };
    /// <summary>UPRIS2TB. Traced.</summary>
    public IReadOnlyList<(int MinScore, int Code)> Table2 { get; init; } = new[] { (9, 3), (11, 4), (12, 5) };
}
