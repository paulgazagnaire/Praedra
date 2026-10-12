namespace Rebellion.Core.Battle;

/// <summary>What a side does when a battle starts. rule: m4-battle-classic §1.3 (OnContact). No Play mode before M8.</summary>
public enum OnContactMode { Simulate = 0, Retreat = 1 }

/// <summary>One task force per ship or squadron (DEFAULT), or one per side. rule: m4-battle-classic §2 (ClassicTaskForces).</summary>
public enum ClassicTaskForces { PerUnit = 0, PerSide = 1 }

/// <summary>How task forces pick targets. rule: m4-battle-classic §6 (Tactic; Stand Off is the traced default, no order sets it in M4).</summary>
public enum BattleTactic { StandOff = 0, Surround = 1 }

/// <summary>Where a retreating fleet goes. rule: m4-retreat-damage-repair §3 (RetreatMode).</summary>
public enum RetreatMode { LaneAdjacent = 0, Nearest = 1, NoRetreat = 2 }

/// <summary>Why a retreat was refused. rule: m4-retreat-damage-repair §1.</summary>
public enum RetreatRefusal { GravityWell, NoHyperdrive, NoDestination }

/// <summary>Why a unit left the game in a battle. rule: m4-battle-classic §10, m4-retreat-damage-repair §Where it runs.</summary>
public enum UnitLossReason { Combat, NoHyperdrive, NoBerth, Stalemate, ShipLost }

/// <summary>Result of a battle for one side. rule: m4-battle-classic §8 (Outcome record).</summary>
public enum BattleOutcome { Victorious, Annihilated, Retreated, Indecisive }

/// <summary>
/// Switches and numbers of the Classic battle (the sheet's table <c>ClassicCombat</c> plus the retreat settings), each with the sheet's DEFAULT
/// and its tag. rule: m4-battle-classic §Numbers, §Defaults for undecided items; m4-retreat-damage-repair §Numbers.
/// </summary>
public sealed record BattleParams
{
    // ---- ruleset. rule: m4-battle-new §Rule (New) (second ruleset, selected by a setting)
    /// <summary>Which engine resolves a battle. DEFAULT Classic. New = the per-gun engine (<see cref="NewBattle"/>).</summary>
    public BattleRuleset Ruleset { get; init; } = BattleRuleset.Classic;
    /// <summary>Switches and numbers of the New engine (used only when <see cref="Ruleset"/> is New).</summary>
    public NewBattleParams New { get; init; } = new();

    // ---- switches. rule: m4-battle-classic §Defaults; m4-retreat-damage-repair §3
    /// <summary>Shield-generator and hull rolls on (DEFAULT on). Off skips both rolls and all draws (model-parity tests). rule: m4-battle-classic §7.</summary>
    public bool SubsystemRolls { get; init; } = true;
    /// <summary>DEFAULT PerUnit. rule: m4-battle-classic §2.</summary>
    public ClassicTaskForces TaskForces { get; init; } = ClassicTaskForces.PerUnit;
    /// <summary>DEFAULT Stand Off, for both sides. rule: m4-battle-classic §6.</summary>
    public BattleTactic Tactic { get; init; } = BattleTactic.StandOff;
    /// <summary>DEFAULT LaneAdjacent. rule: m4-retreat-damage-repair §3.</summary>
    public RetreatMode RetreatMode { get; init; } = RetreatMode.LaneAdjacent;
    /// <summary>A side facing an operational Death Star loses every stalemate. DEFAULT off (only a note supports it). rule: m4-retreat-damage-repair §2.</summary>
    public bool DeathStarStalemate { get; init; }
    /// <summary>The Empire side is the AI: its OnContact is fixed to Simulate (Traced, ai/09 §1). DEFAULT false (M5 sets the sides). rule: m4-battle-classic §1.3.</summary>
    public bool EmpireIsAi { get; init; }
    /// <summary>Same for the Alliance. DEFAULT false.</summary>
    public bool AllianceIsAi { get; init; }

    // ---- rates. rule: m4-battle-classic §4 (Traced, combat-constants.csv)
    /// <summary>C, the family coefficient. Traced.</summary>
    public double FamilyCoefficient { get; init; } = 0.26666668;
    /// <summary>The weapon-rate multiplier. 3.75 x C = 1.0 (the sheet's statement), so the capital-ship rate is wrr x f / N x ratios. Traced.</summary>
    public double WeaponMultiplier { get; init; } = 3.75;
    /// <summary>Laser against a capital ship (float 0x3e2b020c). Traced.</summary>
    public double LaserVsCapital { get; init; } = 0.167;
    public double IonVsCapital { get; init; } = 1.0;
    public double TurbolaserVsCapital { get; init; } = 1.0;
    /// <summary>Laser, ion and turbolaser against a squadron. Traced.</summary>
    public double LaserVsSquadron { get; init; } = 1.0;
    public double IonVsSquadron { get; init; }
    public double TurbolaserVsSquadron { get; init; } = 0.5;
    /// <summary>Floor of the maneuverability ratio of a capital ship against a squadron. Traced constant 0.1; read as a floor (Traced-medium).</summary>
    public double ManeuverFloor { get; init; } = 0.1;
    /// <summary>Each weapons hit takes this share of the base rate; each shield-generator hit this share of the recharge. Traced.</summary>
    public double HitPenalty { get; init; } = 0.25;
    /// <summary>Squadron torpedo term per torpedo; only against a capital ship at shield 0. Traced-medium.</summary>
    public double TorpedoTerm { get; init; } = 0.53333336;
    /// <summary>Squadron hull (12 craft x 8). Lead.</summary>
    public double SquadronHull { get; init; } = 96;
    /// <summary>R, the squadron rate at full hull. Lead.</summary>
    public double SquadronRate { get; init; } = 45.012;
    /// <summary>Squadron shield recharge at full hull (96 x 0.025 / 8). Lead.</summary>
    public double SquadronShieldRecharge { get; init; } = 0.3;

    // ---- targeting. rule: m4-battle-classic §5, §6
    public double PriorityFirepowerWeight { get; init; } = 15;
    public double PriorityShieldWeight { get; init; } = 0.01;
    public double PriorityHullWeight { get; init; } = 0.01;
    /// <summary>Effective-hull factor of the kill-time test. Lead.</summary>
    public double EffectiveHullFactor { get; init; } = 0.65;
    /// <summary>Stand Off kill-time window in seconds. Traced-medium.</summary>
    public double KillTimeWindow { get; init; } = 180;

    // ---- hits and subsystems. rule: m4-battle-classic §7
    /// <summary>Minimum percent chance of a shield-generator hit. Traced-medium.</summary>
    public double ShieldGeneratorMinPercent { get; init; } = 1;
    /// <summary>Both rolls are NextInt(0..this - 1). DEFAULT 100 (0 to 99). Open question 6.</summary>
    public int RollSides { get; init; } = 100;
    /// <summary>A hull-roll score above this hits a subsystem; then the cut-offs below pick which. Traced.</summary>
    public int SubsystemScoreAbove { get; init; } = 50;
    public int ShieldGeneratorUpTo { get; init; } = 65;
    public int WeaponsUpTo { get; init; } = 80;
    public int TractorUpTo { get; init; } = 90;
    public int SubLightUpTo { get; init; } = 95;
    /// <summary>Cap of each of the first four counters in a battle (hyperdrive: its installed components). Lead.</summary>
    public int SubsystemCap { get; init; } = 4;

    // ---- end conditions. rule: m4-battle-classic §8
    /// <summary>A side is flagged when own / enemy strength is below this (float 0.3300000131). Traced.</summary>
    public double RetreatThreshold { get; init; } = 0.3300000131;
    /// <summary>Seconds without a new low of strength, for both sides, that make a stalemate. Traced.</summary>
    public int StalemateSeconds { get; init; } = 1200;
    /// <summary>Engineering guard (not in the original): a battle that reaches this second is treated as a stalemate. DEFAULT.</summary>
    public int SafetyGuardSeconds { get; init; } = 20000;
    /// <summary>Ship class id of the Death Star (the retreat-ability exception and the stalemate switch). Traced (data id).</summary>
    public int DeathStarClassId { get; init; } = 136;
}
