namespace Rebellion.Core.Battle;

/// <summary>
/// A side's answer to the Battle Alert: Fight (retreat threshold 0.0) or Retreat (threshold 10000.0). Both run the normal battle loop; the threshold
/// decides when the side leaves. The AI side is fixed to Fight and uses the AI threshold of B6a §2 (<see cref="BattleParams.AiRetreat"/>). No Play mode before M8.
/// rule: m4-battle-classic §1.3 (Traced, B3 §10: buttons 101 and 103 of the alert dialog).
/// </summary>
public enum OnContactMode { Fight = 0, Retreat = 1 }

/// <summary>How an AI side's battle threshold is set. rule: B6a §7 (AiRetreatRule DEFAULT Classic).</summary>
public enum AiRetreatRule { Classic = 0, Fixed = 1 }

/// <summary>
/// How task forces form. Original (DEFAULT, Traced B3 §2): fighter groups by class (up to 4), then up to 8 capital task forces of about √N ships from
/// three pools. PerUnit: one per ship or squadron. PerSide: one per side. rule: m4-battle-classic §2 (ClassicTaskForces).
/// </summary>
public enum ClassicTaskForces { PerUnit = 0, PerSide = 1, Original = 2 }

/// <summary>Who fires first in a second. EmpireFirst (DEFAULT, Traced B3 §3): the Empire's whole step, then the Alliance's. Simultaneous: both from the same state. rule: m4-battle-classic §3.</summary>
public enum ClassicFireOrder { EmpireFirst = 0, Simultaneous = 1 }

/// <summary>
/// The battle dice. Original (DEFAULT, Traced B3 §13): a Lehmer generator inside the battle, seed 12345678, made fresh for every battle; the battle
/// makes no draw from the game generator. Game: one game draw seeds a <see cref="Clock.GameRng"/> for the battle. rule: m4-battle-classic §1.4, §11.
/// </summary>
public enum ClassicBattleRng { Original = 0, Game = 1 }

/// <summary>How task forces pick targets. rule: m4-battle-classic §6 (Tactic; Stand Off is the traced default; Surround is never used in Simulate, B3 §16).</summary>
public enum BattleTactic { StandOff = 0, Surround = 1 }

/// <summary>Where a retreating fleet goes. rule: m4-retreat-damage-repair §3 (RetreatMode).</summary>
public enum RetreatMode { LaneAdjacent = 0, Nearest = 1, NoRetreat = 2 }

/// <summary>Why a retreat was refused. rule: m4-retreat-damage-repair §1.</summary>
public enum RetreatRefusal { GravityWell, NoHyperdrive, NoDestination }

/// <summary>Why a unit left the game in a battle. rule: m4-battle-classic §10, m4-retreat-damage-repair §Where it runs.</summary>
/// <remarks>MissionDuel: a special force lost the escape duel of a mission check (B2d §5; the original's DestroyedReason 9).</remarks>
public enum UnitLossReason { Combat, NoHyperdrive, NoBerth, Stalemate, ShipLost, MissionDuel }

/// <summary>Result of a battle for one side. rule: m4-battle-classic §8 (Outcome record).</summary>
public enum BattleOutcome { Victorious, Annihilated, Retreated, Indecisive }

/// <summary>
/// Switches and numbers of the Classic battle (the sheet's table <c>ClassicCombat</c> plus the retreat settings), each with the sheet's DEFAULT
/// and its tag. rule: m4-battle-classic §Numbers, §Defaults for undecided items; m4-retreat-damage-repair §Numbers.
/// </summary>
public sealed record BattleParams
{
    // ---- ruleset. rule: m4-battle-new §Rule (New) (second ruleset, selected by a setting)
    /// <summary>Which engine resolves a battle. New = the per-gun engine (<see cref="NewBattle"/>). DEFAULT New (Paul, 2026-10-18 decision card; under his 07:43 rule Classic 1998 rules are opt-in when they collide with our changes). Classic = the 1998 battle.</summary>
    public BattleRuleset Ruleset { get; init; } = BattleRuleset.New;
    /// <summary>Switches and numbers of the New engine (used only when <see cref="Ruleset"/> is New).</summary>
    public NewBattleParams New { get; init; } = new();

    // ---- switches. rule: m4-battle-classic §Defaults; m4-retreat-damage-repair §3
    /// <summary>Shield-generator and hull rolls on (DEFAULT on, Traced). Off skips both rolls and all draws (model-parity tests). rule: m4-battle-classic §6.</summary>
    public bool SubsystemRolls { get; init; } = true;
    /// <summary>DEFAULT Original (Traced, B3 §2). rule: m4-battle-classic §2.</summary>
    public ClassicTaskForces TaskForces { get; init; } = ClassicTaskForces.Original;
    /// <summary>DEFAULT EmpireFirst (Traced, B3 §3). rule: m4-battle-classic §3.</summary>
    public ClassicFireOrder FireOrder { get; init; } = ClassicFireOrder.EmpireFirst;
    /// <summary>DEFAULT Original (Traced, B3 §13). rule: m4-battle-classic §1.4.</summary>
    public ClassicBattleRng BattleRng { get; init; } = ClassicBattleRng.Original;
    /// <summary>DEFAULT Stand Off, for both sides. rule: m4-battle-classic §6.</summary>
    public BattleTactic Tactic { get; init; } = BattleTactic.StandOff;
    /// <summary>DEFAULT LaneAdjacent. rule: m4-retreat-damage-repair §3.</summary>
    public RetreatMode RetreatMode { get; init; } = RetreatMode.LaneAdjacent;
    /// <summary>
    /// While the Empire has a working Death Star in the battle, the Alliance's stalemate strength counts as -1, so the Alliance is always the flagged side.
    /// DEFAULT on (Traced, B3 §12). rule: m4-retreat-damage-repair §2.
    /// </summary>
    public bool DeathStarStalemate { get; init; } = true;
    /// <summary>
    /// The Empire side is the AI (no human plays it): it always fights (Simulate) and its threshold is the AI's (B6a §2). Filled from the game's side
    /// controllers (AiMatch sets both, the Godot opponent its side; <see cref="BattleModule.SetControllers"/>). DEFAULT false. rule: B6a §1, m4-battle-classic §1.3.
    /// </summary>
    public bool EmpireIsAi { get; init; }
    /// <summary>Same for the Alliance. DEFAULT false.</summary>
    public bool AllianceIsAi { get; init; }

    // ---- the original's battle generator. rule: m4-battle-classic §1.4 (Traced, B3 §13)
    /// <summary>Seed of the Original battle generator (0xbc614e). Traced.</summary>
    public int OriginalRngSeed { get; init; } = 12345678;
    /// <summary>Steps made right after seeding. Traced.</summary>
    public int OriginalRngWarmup { get; init; } = 16;

    // ---- rates. rule: m4-battle-classic §4 (Traced, B3 §4)
    /// <summary>K, the family coefficient (0x3e888889). Traced.</summary>
    public double FamilyCoefficient { get; init; } = 0.26666668;
    /// <summary>W = weapon_recharge_rate x this (0x66c49c). Traced.</summary>
    public double WeaponMultiplier { get; init; } = 3.75;
    /// <summary>Laser against a capital ship (float 0x3e2b020c). Traced.</summary>
    public double LaserVsCapital { get; init; } = 0.167;
    public double IonVsCapital { get; init; } = 1.0;
    public double TurbolaserVsCapital { get; init; } = 1.0;
    /// <summary>Laser, ion and turbolaser against a squadron (the laser and turbolaser terms are times the maneuverability ratio). Traced.</summary>
    public double LaserVsSquadron { get; init; } = 1.0;
    public double IonVsSquadron { get; init; }
    public double TurbolaserVsSquadron { get; init; } = 0.5;
    /// <summary>Floor of the maneuverability ratio against a squadron (0x66c518). Traced.</summary>
    public double ManeuverFloor { get; init; } = 0.1;
    /// <summary>Maneuverability cap. Traced (0x5b0be0, 0x5ba270).</summary>
    public double ManeuverCap { get; init; } = 9.0;
    /// <summary>
    /// Added to the class maneuverability: the squadron constant, and for a capital ship the side's officer bonus clamp(9 - x, 1, 9), which is 1.0 with no
    /// officer record (the attribute x is not found: DEFAULT no officer, B3 §17). Traced.
    /// </summary>
    public double ManeuverBonus { get; init; } = 1.0;
    /// <summary>Each weapons hit takes this share of the full rate from the hull-scaled one (also shield generator, tractor, sub-light). Traced.</summary>
    public double HitPenalty { get; init; } = 0.25;
    /// <summary>Squadron torpedo term per torpedo (0x66c4cc); only against a capital ship at shield ratio 0. Traced.</summary>
    public double TorpedoTerm { get; init; } = 0.53333336;
    /// <summary>Hull per squadron craft (0x66c6b8). Traced.</summary>
    public double SquadronCraftHull { get; init; } = 8.0;
    /// <summary>Squadron base rate = hull x this / craft hull (0x66c6e4). Traced.</summary>
    public double SquadronRateFactor { get; init; } = 3.751;
    /// <summary>Squadron shield recharge = hull x this / craft hull (0x66c6e8). Traced.</summary>
    public double SquadronRechargeFactor { get; init; } = 0.025;
    /// <summary>Craft of a squadron class whose data has no squadron_size (FIGHTSD gives 12 for all eight). Traced (data).</summary>
    public int SquadronCraft { get; init; } = 12;
    /// <summary>Squadron hull of the New battle and of QuickBattle's whole squadrons (12 craft x 8). Traced.</summary>
    public double SquadronHull { get; init; } = 96;
    /// <summary>Battle speed of a capital ship per point of sub_light_engine; only "is it 0" and the pool order use it. [Model] b3_simulate.py.</summary>
    public double CapitalSpeedPerSublight { get; init; } = 0.24;
    /// <summary>Battle speed of a squadron per point of sub_light_engine. [Model] b3_simulate.py.</summary>
    public double SquadronSpeedPerSublight { get; init; } = 0.4;

    // ---- task forces. rule: m4-battle-classic §2 (Traced, B3 §2)
    /// <summary>At most this many fighter groups per side. Traced (0x5ae330).</summary>
    public int MaxFighterGroups { get; init; } = 4;
    /// <summary>A fighter group of at least this many squadrons is split in two. Traced.</summary>
    public int FighterGroupSplitAt { get; init; } = 8;
    /// <summary>Pool sort key: speed + this x longest gun range (constant -10000.0 at 0x66cbb0). Traced.</summary>
    public double RangeSortWeight { get; init; } = 10000.0;

    // ---- targeting. rule: m4-battle-classic §5, §6 (Traced, B3 §8, §9)
    public double PriorityFirepowerWeight { get; init; } = 15;
    /// <summary>Weight of the recharge ratio (the sheet's old "shield ratio"). Traced.</summary>
    public double PriorityShieldWeight { get; init; } = 0.01;
    public double PriorityHullWeight { get; init; } = 0.01;
    /// <summary>Per character and special-forces unit aboard. Traced (inferred "aboard").</summary>
    public double PriorityPassengerWeight { get; init; } = 1.0;
    /// <summary>Taken off the value of a docked squadron. Traced.</summary>
    public double PriorityDockedPenalty { get; init; } = 0.01;
    /// <summary>Effective-hull factor of the kill-time test (0x66da8c). Traced.</summary>
    public double EffectiveHullFactor { get; init; } = 0.65;
    /// <summary>Stand Off kill-time window in seconds (0x66cbac). Traced.</summary>
    public double KillTimeWindow { get; init; } = 180;
    /// <summary>A task force keeps its target while the kill time is below this (0x66da84). Traced.</summary>
    public double StickyKillTime { get; init; } = 300;
    /// <summary>Stand Off sort key of a task force outside the window: this - rate (0x66da58). Traced.</summary>
    public double StandOffFar { get; init; } = 100000;

    // ---- hits and subsystems. rule: m4-battle-classic §6, §7 (Traced, B3 §6)
    /// <summary>Minimum percent chance of a shield-generator hit. Traced.</summary>
    public double ShieldGeneratorMinPercent { get; init; } = 1;
    /// <summary>Both rolls are rand(0..this), inclusive (the original's 0x5f5700). Traced.</summary>
    public int RollSides { get; init; } = 100;
    /// <summary>A hull-roll score at or below this hits nothing; then the cut-offs below pick which. Traced.</summary>
    public int SubsystemScoreAbove { get; init; } = 50;
    public int ShieldGeneratorUpTo { get; init; } = 65;
    public int WeaponsUpTo { get; init; } = 80;
    public int TractorUpTo { get; init; } = 90;
    public int SubLightUpTo { get; init; } = 95;
    /// <summary>Cap of each of the four plain counters, in battle and stored (GNPRTB 2048); hyperdrive: its installed components. Traced.</summary>
    public int SubsystemCap { get; init; } = 4;

    // ---- retreat and end. rule: m4-battle-classic §8 (Traced, B3 §10, §12)
    /// <summary>Threshold of a side with no controller (QuickBattle and tests without an alert): the constructor's 0.33 (0x5c7dd4), which a controller always overwrites. Traced.</summary>
    public double RetreatThreshold { get; init; } = 0.3300000131;
    /// <summary>Threshold of a human side that answered Fight (button 101). Traced.</summary>
    public double FightThreshold { get; init; } = 0.0;
    /// <summary>Threshold of a human side that answered Retreat (button 103, 0x461c4000). Traced.</summary>
    public double RetreatAlertThreshold { get; init; } = 10000.0;
    /// <summary>How an AI side's threshold is set: Classic = B6a §2 (computed at the alert from its view of the system), Fixed = <see cref="AiRetreatCap"/> (tests). DEFAULT Classic.</summary>
    public AiRetreatRule AiRetreat { get; init; } = AiRetreatRule.Classic;
    /// <summary>The AI's threshold cap and default, 0.9 (float 0x3f666666, seven copies 0x65c8e4..0x65ca04). [EXE-own] rule: B6a §2.</summary>
    public double AiRetreatCap { get; init; } = 0.9;
    /// <summary>The system where an AI side holding it with a planet squadron fights to the end (system id 0x109, B5). [EXE-own] rule: B6a §2.</summary>
    public string CoruscantSystem { get; init; } = "Coruscant";
    /// <summary>Seconds without a new low of strength, for both sides, that make a stalemate (0x66c3e0). Traced.</summary>
    public int StalemateSeconds { get; init; } = 1200;
    /// <summary>Engineering guard (not in the original): a battle that reaches this second is treated as a stalemate. DEFAULT.</summary>
    public int SafetyGuardSeconds { get; init; } = 20000;
    /// <summary>Ship class id of the Death Star (the retreat-ability exception and the stalemate switch). Traced (data id).</summary>
    public int DeathStarClassId { get; init; } = 136;
}
