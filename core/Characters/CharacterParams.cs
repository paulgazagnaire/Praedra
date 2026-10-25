namespace Praedra.Core.Characters;

/// <summary>
/// Who can train and awaken the Force. rule: m5-characters-and-force §Rule (New) 4 (Paul 2026-10-18).
/// </summary>
public enum TrainerPick
{
    /// <summary>The Classic data flag <c>jedi_trainer</c> (Luke, Vader). Classic.</summary>
    Fixed,
    /// <summary>DEFAULT (New): <see cref="CharacterParams.TalentedPerSide"/> characters per side drawn at every game start.</summary>
    Random,
}

/// <summary>Force discovery mode. rule: m5-characters-and-force §Force (Force discovery).</summary>
public enum ForceDiscoveryMode
{
    /// <summary>
    /// DEFAULT, the original (Traced B2e §5): a Jedi trainer with Force 80+ awakens, every day, the unaware Force users of his side on his place
    /// (Leia only by Luke once he knows his heritage). Story calls to <see cref="CharacterModule.DiscoverForce"/> still work.
    /// </summary>
    Trainer,
    /// <summary>Only known Jedi at creation and story calls to <see cref="CharacterModule.DiscoverForce"/> (the sheet's earlier DEFAULT).</summary>
    Story,
    /// <summary>The original detection routine 0x58a3f0 (not decoded: Missing). Refused.</summary>
    Detection,
}

/// <summary>Which Hand officers the start cast keeps. rule: m5-setup-and-victory §P9 item 2 (StartCastOfficers).</summary>
public enum StartCastOfficers
{
    /// <summary>DEFAULT: Vader and the five officers (Project Director Cyprian Galena, Admiral Lucan Mordant, Admiral Decimus Varo, Eighty-Eight, Captain Ilian Venia).</summary>
    Classic,
    /// <summary>Vader only; the five officers stay in the Hand pool.</summary>
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
    /// <summary>harvester hit chance on a minor, percent (GNPRTB 0x606 = 50). Traced, Decided (Paul).</summary>
    public int HarvesterHitPercent { get; init; } = 50;
    /// <summary>
    /// Train Talent gain cap, percent of the gap: <c>g = rand(0..gap * this / 100)</c>. DEFAULT 25 (New, Paul 2026-10-18: five or six trainers, so
    /// training is slower); <see cref="ClassicTrainingGainPercent"/> 50 = Classic (GNPRTB 6173). rule: m5-characters-and-force §Force (Train Talent: the roll).
    /// </summary>
    public int ForceTrainingGainPercent { get; init; } = 25;
    /// <summary>
    /// Prisoners land (New, Paul 2026-10-18 decision card "Land them"): at day end a fleet standing at a system its own side holds, not blockaded,
    /// puts the prisoners it holds aboard ashore there, where an ordinary Rescue can reach them. DEFAULT true; false = Classic (a prisoner aboard stays
    /// aboard until he escapes; B2e §3 "A prisoner held aboard stays aboard"). rule: m5-characters-and-force §Capture, injury, death, freeing.
    /// </summary>
    public bool LandPrisoners { get; init; } = true;
    /// <summary>The Classic training gain percent (GNPRTB 6173 = 50). Traced, 0x55ca20. rule: m5-characters-and-force §Numbers.</summary>
    public const int ClassicTrainingGainPercent = 50;
    /// <summary>Training chance cap, percent (min(gap, 100)). Traced code, reading [inferred].</summary>
    public int TrainingChanceCap { get; init; } = 100;
    /// <summary>
    /// Train Talent chance scale: <c>chance = min(gap, 100) * this / 100</c>. DEFAULT 50 (New, Paul 2026-10-18); 100 = Classic (Traced, reading [inferred]).
    /// rule: m5-characters-and-force §Force (Train Talent: the roll; New, Paul 2026-10-18; Classic = 100).
    /// </summary>
    public int ForceTrainingChancePercent { get; init; } = 50;
    /// <summary>
    /// Share of the Force that multiplies diplomacy, espionage and combat: <c>B + B * F * this / 10000</c>. DEFAULT 40 (New, Paul 2026-10-18: a master
    /// at 120 gives +48% instead of +120%); 100 = Classic (Traced B2e §1). rule: m5-characters-and-force §Force (New, Paul 2026-10-18; Classic = 100).
    /// </summary>
    public int ForceSkillPercent { get; init; } = 40;
    /// <summary>The Classic values of the three Force tone-down switches (Classic). rule: m5-characters-and-force §Force (Classic = 100 / 100 / 50).</summary>
    public const int ClassicForceSkillPercent = 100, ClassicTrainingChancePercent = 100;
    /// <summary>Lowest effective Force of a Jedi Student. DEFAULT 1 (no number known; Open question 15).</summary>
    public int ForceStudentMin { get; init; } = 1;
    /// <summary>Lowest effective Force of a Jedi Knight (GNPRTB 2560/2580 = 80). Traced number, mapping DEFAULT.</summary>
    public int ForceKnightMin { get; init; } = 80;
    /// <summary>Lowest effective Force of a Jedi Master (GNPRTB 2581 = 100). Traced number, mapping DEFAULT.</summary>
    public int ForceMasterMin { get; init; } = 100;
    /// <summary>Navigator floor on a fleet's speed when a navigator (best below 100) rides. Proposal (heroes/mission-speed item 7).</summary>
    public int NavigatorFloor { get; init; } = Travel.TravelFormula.NavigatorFloor;

    // ---- injury (Traced B2e §2). rule: m5-characters-and-force §Capture, injury, death, freeing
    /// <summary>Lowest chance of the Hurt roll <c>max(this, 100 - v)</c>. GNPRTB 2565 = 1 (0x53e990).</summary>
    public int HurtMinChance { get; init; } = 1;
    /// <summary>Hurt size <c>rand(0..p) + rand(0..spread) + base</c>: base GNPRTB 2566 = 1.</summary>
    public int HurtSizeBase { get; init; } = 1;
    /// <summary>Hurt size spread, GNPRTB 2567 = 29.</summary>
    public int HurtSizeSpread { get; init; } = 29;
    /// <summary>Hurt2 (vtable +0x2e8, caller not read): GNPRTB 2568 = 1 (lowest chance), 2569 = 1 (size base), 2570 = 29 (size spread). 0x53ea00.</summary>
    public int Hurt2MinChance { get; init; } = 1;
    public int Hurt2SizeBase { get; init; } = 1;
    public int Hurt2SizeSpread { get; init; } = 29;
    /// <summary>HurtBy(a, b) of the Force-user encounter, chance <c>max(this, b - a)</c>: GNPRTB 2571 = 1, size base 2572 = 1, spread 2573 = 29. 0x53ea70.</summary>
    public int HurtByMinChance { get; init; } = 1;
    public int HurtBySizeBase { get; init; } = 1;
    public int HurtBySizeSpread { get; init; } = 29;
    /// <summary>HurtFixed (vtable +0x2ec, caller not read): chance GNPRTB 2574 = 100, size base 2575 = 1, spread 2576 = 29. 0x53eae0.</summary>
    public int HurtFixedChance { get; init; } = 100;
    public int HurtFixedSizeBase { get; init; } = 1;
    public int HurtFixedSizeSpread { get; init; } = 29;
    /// <summary>An injured minor dies if <c>rand(0..99) &lt; this</c>. GNPRTB 2577 = 25 (0x56f160 -> 0x53eb30).</summary>
    public int MinorDeathPercent { get; init; } = 25;
    /// <summary>Force at or above which a character heals fast (FastHeal). GNPRTB 2560 = 80 (0x4eeff0).</summary>
    public int FastHealMinForce { get; init; } = 80;
    /// <summary>Healing timer period with FastHeal, days: GNPRTB 2561 = 1; without: 2562 = 1 (spread 0, no draw).</summary>
    public int HealPeriodFast { get; init; } = 1;
    public int HealPeriod { get; init; } = 1;
    /// <summary>Points healed per firing: GNPRTB 2563 = 2 with FastHeal, 2564 = 1 without (0x4ef100).</summary>
    public int HealFast { get; init; } = 2;
    public int HealSlow { get; init; } = 1;
    /// <summary>Base combat gained by whoever hurts the other in a Force-user encounter. GNPRTB 2578 = 1 (0x560200).</summary>
    public int EncounterCombatGain { get; init; } = 1;

    // ---- escape (Traced B2e §3). rule: §Escape
    /// <summary>Escape delay <c>base + rand(0..spread)</c> days: GNPRTB 2584 = 100, 2585 = 1000 (0x4ef754).</summary>
    public int EscapeDelayBase { get; init; } = 100;
    public int EscapeDelaySpread { get; init; } = 1000;

    // ---- Force (Traced B2e §4, §5). rule: §Force
    /// <summary>Who trains and awakens: DEFAULT Random (New, Paul 2026-10-18); Fixed = the Classic data flag. rule: §Rule (New) 4.</summary>
    public TrainerPick TrainerPick { get; init; } = TrainerPick.Random;
    /// <summary>Talented characters drawn per side at setup (Art Director proposal, 2026-10-18: 3 per side, 6 in all). rule: §Rule (New) 4.</summary>
    public int TalentedPerSide { get; init; } = 3;
    /// <summary>
    /// The Force a drawn talented character gets: rand(min..max), kept if his own is higher (Q16 DEFAULT). PROPOSAL (Dev lead 2026-10-18): 80 =
    /// <see cref="TrainerMinForce"/> so he can awaken; 120 = Vader's Classic value. rule: §Rule (New) 4.
    /// </summary>
    public int TalentedForceMin { get; init; } = 80;
    /// <summary>See <see cref="TalentedForceMin"/>.</summary>
    public int TalentedForceMax { get; init; } = 120;
    /// <summary>
    /// Leave out of the talented draw anyone whose own Force is already at <see cref="TalentedForceMin"/> or above: they keep their Force but are
    /// not talented, so they never train or awaken anyone; the drawn are always new faces. Paul 2026-10-18 (card "Skip the strong"). rule: §Rule (New) 4, Q17.
    /// </summary>
    public bool TalentedSkipStrong { get; init; } = true;
    /// <summary>Leave out of the talented draw the characters flagged never_talented in roster.csv (the Repossessors). Paul 2026-10-18 (card "Leave out hunters"). rule: §Rule (New) 4, Q17.</summary>
    public bool TalentedSkipFlagged { get; init; } = true;
    /// <summary>Lowest Force of an awakening trainer (DiscoveringForceUser). GNPRTB 2580 = 80 (0x4ef070).</summary>
    public int TrainerMinForce { get; init; } = 80;
    /// <summary>Awakening timer: GNPRTB 2582 = 1 day, spread 2583 = 0 (no draw).</summary>
    public int AwakenPeriod { get; init; } = 1;
    public int AwakenSpread { get; init; }
    /// <summary>Luke's Force gain after meeting Vader, percent of the gap: GNPRTB 3072 = 25; at least 3084 = 1.</summary>
    public int LukeVaderGainPercent { get; init; } = 25;
    public int LukeVaderMinGain { get; init; } = 1;
    /// <summary>Luke after the Emperor: GNPRTB 3073 = 20, at least 3085 = 1.</summary>
    public int LukeEmperorGainPercent { get; init; } = 20;
    public int LukeEmperorMinGain { get; init; } = 1;
    /// <summary>Leia after Vader: GNPRTB 3074 = 20, at least 3086 = 1.</summary>
    public int LeiaVaderGainPercent { get; init; } = 20;
    public int LeiaVaderMinGain { get; init; } = 1;
    /// <summary>Leia after the Emperor: GNPRTB 3075 = 20, at least 3087 = 1.</summary>
    public int LeiaEmperorGainPercent { get; init; } = 20;
    public int LeiaEmperorMinGain { get; init; } = 1;
    /// <summary>Luke loses his hand (an injury) only below this Force. GNPRTB 3077 = 80 (0x561b60).</summary>
    public int LukeHandMaxForce { get; init; } = 80;
    /// <summary>Luke's hand injury <c>rand(0..spread) + base</c>: GNPRTB 3080 = 99, 3079 = 1.</summary>
    public int LukeHandSpread { get; init; } = 99;
    public int LukeHandBase { get; init; } = 1;
    /// <summary>The four Jedi of the encounter effects, by record id (keys 0x32000242, 0x31000241, 0x35000281, 0x34000280). Traced B2e §4.</summary>
    public int Luke { get; init; } = 0x242;
    public int Leia { get; init; } = 0x241;
    public int Vader { get; init; } = 0x281;
    public int Emperor { get; init; } = 0x280;

    // ---- Seat of Power (Traced B2e §6). rule: §Seat of Power
    /// <summary>LeadershipAdjustment of Hand-only characters while the Seat of Power holds. GNPRTB 3076 = 50 (0x5618d0).</summary>
    public int SeatOfPowerAdjustment { get; init; } = 50;
    /// <summary>The Seat of Power system, key 0x90000009 = SYSTEMSD record 265 (Charter: inferred from the family and id, not by name).</summary>
    public string SeatOfPowerSystem { get; init; } = "Charter";

    // ---- switches (§Defaults for undecided items)
    /// <summary>Attached characters ride their fleet; false = detached at departure when a MoveFleet is accepted. DEFAULT true.</summary>
    public bool OfficersRideFleet { get; init; } = true;
    /// <summary>Characters die with a destroyed fleet. DEFAULT false; on is undefined (Missing): refused.</summary>
    public bool CharactersLostWithFleet { get; init; }
    /// <summary>An enemy fleet stops or captures a lone character in transit. DEFAULT false; on has no rule: refused.</summary>
    public bool CharactersStoppedInTransit { get; init; }
    /// <summary>Personnel leaving a blockaded system risk capture (Classic text, no rule). DEFAULT false; on refused.</summary>
    public bool BlockadeEscapeRisk { get; init; }
    /// <summary>Assaults capture or kill officers (Missing). DEFAULT false; on refused.</summary>
    public bool AssaultHarmsCharacters { get; init; }
    /// <summary>Major characters can die (else Kill becomes Injure). DEFAULT false (Traced for the harvester; Open question 2).</summary>
    public bool MajorsCanDie { get; init; }
    /// <summary>Minor characters can die. DEFAULT true (Traced per-character bit, source unknown).</summary>
    public bool MinorsCanDie { get; init; } = true;
    /// <summary>The harvester's roll also hits the attacker's own characters. DEFAULT true.</summary>
    public bool HarvesterHitsOwnCharacters { get; init; } = true;
    /// <summary>
    /// Prisoners try to escape on their own: an attempt every <c>100 + rand(0..1000)</c> days, ESCAPETB on <c>combat + espionage - captors' average
    /// combat - captors' special forces</c>. DEFAULT true (Traced B2e §3); false: no timers and no draws (comparison runs). rule: §Escape.
    /// </summary>
    public bool CaptiveEscape { get; init; } = true;
    /// <summary>ESCAPETB points (input, percent), ascending input. Null: the game data's ESCAPETB (mission-tables.csv); tests may pass their own. rule: §Escape.</summary>
    public IReadOnlyList<(int Input, int Percent)>? EscapeTable { get; init; }
    /// <summary>
    /// The four loyalty triggers (a system changes hands, the Collective HQ destroyed, a battle won, a blockade ended). DEFAULT true (Traced, B2c);
    /// false turns all four off together (comparison runs). Was <c>LoyaltyEnabled</c> (refused) before B2c. rule: §Loyalty and betrayal.
    /// </summary>
    public bool LoyaltyTriggers { get; init; } = true;
    /// <summary>Divisor of the loss shift <c>(enemy - own) / d + enemy / d</c>. SDPRTB 2560 = 50 (both side columns). Traced B2c, 0x53ec10.</summary>
    public int LossShiftDivisor { get; init; } = 50;
    /// <summary>Capture shift <c>rand(0..spread) + base</c> for the new owner. SDPRTB 2561 = 5. Traced B2c, 0x53ec90.</summary>
    public int CaptureShiftSpread { get; init; } = 5;
    /// <summary>SDPRTB 2562 = 0. Traced B2c, 0x53ec90.</summary>
    public int CaptureShiftBase { get; init; }
    /// <summary>Collective HQ destroyed: <c>k = rand(bound) + base</c>; a negative bound draws 0..|bound| and negates (0x53e290). GNPRTB 2592 = -5. Traced B2c, 0x561780.</summary>
    public int HqShiftBound { get; init; } = -5;
    /// <summary>GNPRTB 2593 = 0. Traced B2c, 0x561780.</summary>
    public int HqShiftBase { get; init; }
    /// <summary>
    /// The Hand Seat of Power: while the Emperor is free on Charter and the Hand holds it, Hand-only characters lead x1.5. DEFAULT true
    /// (Traced B2e §6); false: never. rule: §Seat of Power.
    /// </summary>
    public bool SeatOfPower { get; init; } = true;
    /// <summary>DEFAULT Trainer (Traced B2e §5); Story keeps only the story calls; Detection refused.</summary>
    public ForceDiscoveryMode ForceDiscovery { get; init; } = ForceDiscoveryMode.Trainer;
    /// <summary>Force growth outside training (Missing). DEFAULT false; on refused.</summary>
    public bool ForceGrowth { get; init; }
    /// <summary>Skill growth (Missing). DEFAULT false; on refused.</summary>
    public bool SkillGrowth { get; init; }
    /// <summary>A trainer needs Jedi Knight rank. DEFAULT false (Open question 6).</summary>
    public bool TrainerNeedsKnight { get; init; }
    /// <summary>A navigator aboard speeds a fleet. Classic false (DEFAULT here), New true (Decided in principle; numbers Proposal).</summary>
    public bool NavigatorBonus { get; init; }

    // ---- setup switches owned by m5-setup-and-victory P9 (kept here because they only steer the character placement)
    /// <summary>Extra random characters per side at the start. DEFAULT 4 = SDPRTB 0x1402, size tier 3 (the biggest Classic galaxy; SizeTier DEFAULT 3, B4 §14.1). rule: m5-setup-and-victory §P9.</summary>
    public int StartExtrasPerSide { get; init; } = 4;
    /// <summary>DEFAULT Classic. rule: m5-setup-and-victory §P9 item 2.</summary>
    public StartCastOfficers StartCastOfficers { get; init; } = StartCastOfficers.Classic;

    /// <summary>Throw <see cref="RuleMissingException"/> for every switch value the sheet refuses. rule: §Defaults for undecided items, CH31.</summary>
    public void Validate()
    {
        if (ForceDiscovery == ForceDiscoveryMode.Detection) throw new RuleMissingException("ForceDiscovery=Detection");
        if (ForceGrowth) throw new RuleMissingException("ForceGrowth");
        if (SkillGrowth) throw new RuleMissingException("SkillGrowth");
        if (BlockadeEscapeRisk) throw new RuleMissingException("BlockadeEscapeRisk");
        if (AssaultHarmsCharacters) throw new RuleMissingException("AssaultHarmsCharacters");
        if (CharactersLostWithFleet) throw new RuleMissingException("CharactersLostWithFleet");
        if (CharactersStoppedInTransit) throw new RuleMissingException("CharactersStoppedInTransit");
    }
}
