namespace Rebellion.Core.Economy;

/// <summary>Autoscrap pick. rule: m2-maintenance §Rule (New) AutoscrapMode.</summary>
public enum AutoscrapMode
{
    /// <summary>DEFAULT. 1998 behaviour: one uniform draw over the eligible objects.</summary>
    Classic = 0,
    /// <summary>Proposal (economy/09 P4): junk first, yards last, no random draw.</summary>
    Smart = 1,
}

/// <summary>How the autoscrap timer runs. rule: m2-maintenance §Overdraft and autoscrap (B5b §6).</summary>
public enum AutoscrapTimer
{
    /// <summary>DEFAULT, 1998 ([EXE-own] 0x5329f0): armed when an overdraft starts, first firing 10 days later, then every 10 days while over; dropped when it ends.</summary>
    Armed = 0,
    /// <summary>The rule before B5b: fires every 10 days from day 10 whatever happens.</summary>
    FreeRunning = 1,
}

/// <summary>
/// What a blockade or an uprising does to a running mine, refinery or yard cycle. rule: m2-mines-refineries-pools §Blockade and uprising;
/// m2-construction §Suspension (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, 1998 is the switch).
/// </summary>
public enum StopMode
{
    /// <summary>DEFAULT (Paul, economy/02 "Blockade and uprising": shipyards pause, a blockade costs exactly that planet's output): the running cycle
    /// is frozen while suspended (its due day moves on one day per day) and resumes where it left off when the suspension ends.</summary>
    Pause = 0,
    /// <summary>1998 (B5b §3, [EXE-own] 0x50bbb0): the running cycle is abandoned with no point (the raw or refined it took is lost).</summary>
    Abandon = 1,
}

/// <summary>Where a new game's slot values come from. rule: m2-slots-and-resources §Rule (New) SlotSource.</summary>
public enum SlotSource
{
    /// <summary>DEFAULT (Decided): read slot-values-draft.csv.</summary>
    Pinned = 0,
    /// <summary>The 1998 start rolls, kept for comparison with the real game.</summary>
    Rolled = 1,
}

/// <summary>
/// Every number of the economy sheets, with its tag. These are data values, not code: tests and later setups may
/// override them. Tags: Traced = read in the 1998 binary or data; Lead = unverified vs original; DEFAULT = the sheet's choice.
/// rule: m2-slots-and-resources §Numbers; m2-mines-refineries-pools §Numbers; m2-maintenance §Numbers.
/// </summary>
public sealed record EconomyParams
{
    // ---- pools sheet
    /// <summary>Capacity (maintenance room) held by each completed mine or refinery. Traced (MANFACSD / PROFACSD).</summary>
    public int CapacityPerFacility { get; init; } = 50;
    /// <summary>GNPRTB 4096, load brake divisor: k = max(1, capacity * this / 100) = 10. Traced.</summary>
    public int LoadBrakeDivisor { get; init; } = 20;
    /// <summary>Processing rate of mines and refineries (PROFACSD), days. Traced.</summary>
    public int ProcessingRate { get; init; } = 5;
    /// <summary>GNPRTB 7763, slowdown numerator: eff = this * 100 / support. Traced.</summary>
    public int SlowdownNumerator { get; init; } = 100;
    /// <summary>Support is clamped to this range when a cycle starts. DEFAULT (pools sheet: "clamped to 1..100").</summary>
    public int SupportFloor { get; init; } = 1;
    public int SupportCeiling { get; init; } = 100;
    /// <summary>Support of a system nobody has set: rim populated systems start at exactly 50 (m2-popular-support, starting values reference). DEFAULT.</summary>
    public int DefaultAllianceSupport { get; init; } = 50;
    /// <summary>Smuggling tiers: (highest support in the tier, percent). Support above the last bound gives 0. GNPRTB 7732 to 7737. Traced.</summary>
    public IReadOnlyList<(int MaxSupport, int Percent)> SmugglingTiers { get; init; } =
        new[] { (20, 75), (30, 50), (40, 25) };
    /// <summary>Reduction per capital ship / fighter squadron / regiment at the system (GNPRTB 7686 to 7688). Traced.</summary>
    public int ShipPresence { get; init; } = 10;
    public int FighterPresence { get; init; } = 5;
    public int RegimentPresence { get; init; } = 2;
    /// <summary>Empire regiments count this many times in a Death Star sector (GNPRTB 7680). Traced.</summary>
    public int DeathStarRegimentMultiplier { get; init; } = 2;
    /// <summary>Starting stockpile of raw and refined. DEFAULT (Lead): 0 and 0.</summary>
    public int StartRaw { get; init; } = 0;
    public int StartRefined { get; init; } = 0;

    // ---- slots sheet
    /// <summary>Slot cap for incidents and start rolls. Traced.</summary>
    public int SlotCap { get; init; } = 15;
    /// <summary>Resource incident interval = Min + rand(0..Spread). GNPRTB 7719 / 7720. Traced.</summary>
    public int ResourceIntervalMin { get; init; } = 1;
    public int ResourceIntervalSpread { get; init; } = 499;
    /// <summary>Disaster interval = Min + rand(0..Spread). GNPRTB 7717 / 7718. Traced.</summary>
    public int DisasterIntervalMin { get; init; } = 1;
    public int DisasterIntervalSpread { get; init; } = 399;
    /// <summary>Percent chance a facility on the disaster system is destroyed (GNPRTB 7716). Lead.</summary>
    public int DisasterDestroyPercent { get; init; } = 10;

    // ---- start rolls (SlotSource = Rolled only). Traced, economy/01.
    public int CorePopulatedPercent { get; init; } = 100;   // GNPRTB 7730
    public int RimPopulatedPercent { get; init; } = 31;     // GNPRTB 7731
    public int CoreEnergyBase { get; init; } = 10;          // 7721
    public int CoreEnergySpread { get; init; } = 4;         // 7722
    public int CoreRawBase { get; init; } = 5;              // 7723
    public int CoreRawSpread { get; init; } = 9;            // 7724
    public int RimEnergyBase { get; init; } = 1;            // 7725
    public int RimEnergySpreadA { get; init; } = 4;         // 7726
    public int RimEnergySpreadB { get; init; } = 9;         // 7727
    public int RimRawBase { get; init; } = 1;               // 7728
    public int RimRawSpread { get; init; } = 14;            // 7729

    // ---- maintenance sheet
    /// <summary>Autoscrap period in days (GNPRTB 7168). Lead.</summary>
    public int ScrapPeriod { get; init; } = 10;
    /// <summary>Refund on scrap = floor(refined cost / this). Lead (rounding DEFAULT).</summary>
    public int RefundDivisor { get; init; } = 2;
}
