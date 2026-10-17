namespace Rebellion.Core.Characters;

/// <summary>Force discovery mode. rule: m5-characters-and-force §Force (Force discovery).</summary>
public enum ForceDiscoveryMode
{
    /// <summary>DEFAULT: only known Jedi at creation and story calls to <see cref="CharacterModule.DiscoverForce"/>.</summary>
    Story,
    /// <summary>The original detection routine 0x58a3f0 (not decoded: Missing). Refused.</summary>
    Detection,
}

/// <summary>Which Imperial officers the start cast keeps. rule: m5-setup-and-victory §P9 item 2 (StartCastOfficers).</summary>
public enum StartCastOfficers
{
    /// <summary>DEFAULT: Vader and the five officers (Jerjerrod, Ozzel, Piett, Veers, Needa).</summary>
    Original,
    /// <summary>Vader only; the five officers stay in the Empire pool.</summary>
    None,
}

/// <summary>
/// A switch set to a value whose rule is Missing in the sheet: the game refuses to start. rule: m5-characters-and-force §Defaults for undecided
/// items ("on refused"), the sheet's <c>RuleMissing</c> refusal.
/// </summary>
public sealed class RuleMissingException : InvalidOperationException
{
    public string Rule { get; }
    public RuleMissingException(string rule) : this(rule, "m5-characters-and-force") { }
    /// <summary>The same refusal for another sheet's switch (m5-missions uses it too).</summary>
    public RuleMissingException(string rule, string sheet) : base($"RuleMissing {{{rule}}}: the rule book has no rule for this switch value ({sheet}).") { Rule = rule; }
}

/// <summary>
/// The switches and numbers of m5-characters-and-force, each with the sheet's DEFAULT (and its tag). Settings, not saved. Values whose rule is
/// Missing are refused at attach with <see cref="RuleMissingException"/>. rule: m5-characters-and-force §Defaults for undecided items, §Numbers;
/// CLAUDE.md hard rule 7.
/// </summary>
public sealed record CharacterParams
{
    // ---- numbers (§Numbers)
    /// <summary>Speed rating of a lone character (GNPRTB 1). Traced, m1-travel.</summary>
    public int CharacterSpeed { get; init; } = Travel.TravelFormula.DefaultSpeed;
    /// <summary>Death Star hit chance on a minor, percent (GNPRTB 0x606 = 50). Traced, Decided (Paul).</summary>
    public int DeathStarHitPercent { get; init; } = 50;
    /// <summary>Jedi Training gain cap, percent of the gap (GNPRTB 6173 = 50). Traced.</summary>
    public int TrainingGainPercent { get; init; } = 50;
    /// <summary>Training chance cap, percent (min(gap, 100)). Traced code, reading [inferred].</summary>
    public int TrainingChanceCap { get; init; } = 100;
    /// <summary>Lowest effective Force of a Jedi Student. DEFAULT 1 (no number known; Open question 15).</summary>
    public int ForceStudentMin { get; init; } = 1;
    /// <summary>Lowest effective Force of a Jedi Knight (GNPRTB 2560/2580 = 80). Traced number, mapping DEFAULT.</summary>
    public int ForceKnightMin { get; init; } = 80;
    /// <summary>Lowest effective Force of a Jedi Master (GNPRTB 2581 = 100). Traced number, mapping DEFAULT.</summary>
    public int ForceMasterMin { get; init; } = 100;
    /// <summary>Navigator floor on a fleet's speed when a navigator (best below 100) rides. Proposal (heroes/mission-speed item 7).</summary>
    public int NavigatorFloor { get; init; } = Travel.TravelFormula.NavigatorFloor;
    /// <summary>Days an injury lasts: 0 = healed at slot 6.10 of the same Step. DEFAULT 0 (no number known; Open question 3).</summary>
    public int InjuryDays { get; init; }

    // ---- switches (§Defaults for undecided items)
    /// <summary>Attached characters ride their fleet; false = detached at departure when a MoveFleet is accepted. DEFAULT true.</summary>
    public bool OfficersRideFleet { get; init; } = true;
    /// <summary>Characters die with a destroyed fleet. DEFAULT false; on is undefined (Missing): refused.</summary>
    public bool CharactersLostWithFleet { get; init; }
    /// <summary>An enemy fleet stops or captures a lone character in transit. DEFAULT false; on has no rule: refused.</summary>
    public bool CharactersStoppedInTransit { get; init; }
    /// <summary>Personnel leaving a blockaded system risk capture (Original text, no rule). DEFAULT false; on refused.</summary>
    public bool BlockadeEscapeRisk { get; init; }
    /// <summary>Assaults capture or kill officers (Missing). DEFAULT false; on refused.</summary>
    public bool AssaultHarmsCharacters { get; init; }
    /// <summary>Major characters can die (else Kill becomes Injure). DEFAULT false (Traced for the Death Star; Open question 2).</summary>
    public bool MajorsCanDie { get; init; }
    /// <summary>Minor characters can die. DEFAULT true (Traced per-character bit, source unknown).</summary>
    public bool MinorsCanDie { get; init; } = true;
    /// <summary>Force users heal faster (Missing). DEFAULT false; on refused.</summary>
    public bool ForceHealBonus { get; init; }
    /// <summary>The Death Star's roll also hits the attacker's own characters. DEFAULT true.</summary>
    public bool DeathStarHitsOwnCharacters { get; init; } = true;
    /// <summary>Captives roll to escape at slot 6.10. DEFAULT false; refused while <see cref="EscapeInput"/> or <see cref="EscapeTable"/> is missing.</summary>
    public bool CaptiveEscape { get; init; }
    /// <summary>The escape input of a captive (whose skills: not known). Null until Paul or the Missions thread names it. rule: §Escape.</summary>
    public Func<CharacterRecord, int>? EscapeInput { get; init; }
    /// <summary>ESCAPETB points (input, percent), ascending input; loaded by the importer (not yet). rule: §Escape, missions/01.</summary>
    public IReadOnlyList<(int Input, int Percent)>? EscapeTable { get; init; }
    /// <summary>
    /// The four loyalty triggers (a system changes hands, the Alliance HQ destroyed, a battle won, a blockade ended). DEFAULT true (Traced, B2c);
    /// false turns all four off together (comparison runs). Was <c>LoyaltyEnabled</c> (refused) before B2c. rule: §Loyalty and betrayal.
    /// </summary>
    public bool LoyaltyTriggers { get; init; } = true;
    /// <summary>Divisor of the loss shift <c>(enemy - own) / d + enemy / d</c>. SDPRTB 2560 = 50 (both side columns). Traced B2c, 0x53ec10.</summary>
    public int LossShiftDivisor { get; init; } = 50;
    /// <summary>Capture shift <c>rand(0..spread) + base</c> for the new owner. SDPRTB 2561 = 5. Traced B2c, 0x53ec90.</summary>
    public int CaptureShiftSpread { get; init; } = 5;
    /// <summary>SDPRTB 2562 = 0. Traced B2c, 0x53ec90.</summary>
    public int CaptureShiftBase { get; init; }
    /// <summary>Alliance HQ destroyed: <c>k = rand(bound) + base</c>; a negative bound draws 0..|bound| and negates (0x53e290). GNPRTB 2592 = -5. Traced B2c, 0x561780.</summary>
    public int HqShiftBound { get; init; } = -5;
    /// <summary>GNPRTB 2593 = 0. Traced B2c, 0x561780.</summary>
    public int HqShiftBase { get; init; }
    /// <summary>The Imperial Seat of Power leadership bonus (no number). DEFAULT false; on refused.</summary>
    public bool SeatOfPower { get; init; }
    /// <summary>DEFAULT Story; Detection refused (routine not decoded).</summary>
    public ForceDiscoveryMode ForceDiscovery { get; init; } = ForceDiscoveryMode.Story;
    /// <summary>Force growth outside training (Missing). DEFAULT false; on refused.</summary>
    public bool ForceGrowth { get; init; }
    /// <summary>Skill growth (Missing). DEFAULT false; on refused.</summary>
    public bool SkillGrowth { get; init; }
    /// <summary>A trainer needs Jedi Knight rank. DEFAULT false (Open question 6).</summary>
    public bool TrainerNeedsKnight { get; init; }
    /// <summary>A navigator aboard speeds a fleet. Classic false (DEFAULT here), New true (Decided in principle; numbers Proposal).</summary>
    public bool NavigatorBonus { get; init; }

    // ---- setup switches owned by m5-setup-and-victory P9 (kept here because they only steer the character placement)
    /// <summary>Extra random characters per side at the start. DEFAULT 4 = SDPRTB 0x1402, size tier 3 (the biggest 1998 galaxy; SizeTier DEFAULT 3, B4 §14.1). rule: m5-setup-and-victory §P9.</summary>
    public int StartExtrasPerSide { get; init; } = 4;
    /// <summary>DEFAULT Original. rule: m5-setup-and-victory §P9 item 2.</summary>
    public StartCastOfficers StartCastOfficers { get; init; } = StartCastOfficers.Original;

    /// <summary>Throw <see cref="RuleMissingException"/> for every switch value the sheet refuses. rule: §Defaults for undecided items, CH24, CH31.</summary>
    public void Validate()
    {
        if (SeatOfPower) throw new RuleMissingException("SeatOfPower");
        if (ForceDiscovery == ForceDiscoveryMode.Detection) throw new RuleMissingException("ForceDiscovery=Detection");
        if (ForceGrowth) throw new RuleMissingException("ForceGrowth");
        if (SkillGrowth) throw new RuleMissingException("SkillGrowth");
        if (ForceHealBonus) throw new RuleMissingException("ForceHealBonus");
        if (BlockadeEscapeRisk) throw new RuleMissingException("BlockadeEscapeRisk");
        if (AssaultHarmsCharacters) throw new RuleMissingException("AssaultHarmsCharacters");
        if (CharactersLostWithFleet) throw new RuleMissingException("CharactersLostWithFleet");
        if (CharactersStoppedInTransit) throw new RuleMissingException("CharactersStoppedInTransit");
        if (CaptiveEscape && (EscapeInput == null || EscapeTable == null || EscapeTable.Count == 0)) throw new RuleMissingException("EscapeInput");
    }
}
