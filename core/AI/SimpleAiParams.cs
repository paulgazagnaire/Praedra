using Rebellion.Core.Clock;

namespace Rebellion.Core.AI;

/// <summary>
/// Every number of the simple computer opponent. These are tuning values of the AI, not rules of the game: no rule sheet fixes them
/// (the Classic AI notes in specs/ai/ describe the 1998 behaviour only in outline), so each is a DEFAULT chosen here and is
/// overridable per side. Strength numbers are percent of the reference force REF (3 Imperial Star Destroyers, m4-battle-new §12),
/// the scale of <see cref="Rebellion.Core.Fleets.FleetModule.StrengthOf"/>: 100 = as strong as REF.
/// </summary>
public sealed record SimpleAiParams
{
    // ---- how often each area is reviewed (a side's reviews are offset by its side number so the two sides do not all act on one day)
    /// <summary>Days between economy reviews (mines, refineries, yards). DEFAULT.</summary>
    public int EconomyEvery { get; init; } = 7;
    /// <summary>Days between production reviews (ships, fighters, regiments). DEFAULT.</summary>
    public int ProductionEvery { get; init; } = 5;
    /// <summary>Days between military reviews (merge, load, move, bombard, assault). DEFAULT.</summary>
    public int MilitaryEvery { get; init; } = 3;

    // ---- economy and budget
    /// <summary>Maintenance room kept free after every purchase (m2-maintenance: load above capacity makes the autoscrap fire). DEFAULT.</summary>
    public int MaintenanceMargin { get; init; } = 100;
    /// <summary>Shipyards and training facilities the AI wants to own (queued ones count). DEFAULT.</summary>
    public int DesiredShipyards { get; init; } = 9;
    public int DesiredTrainingFacilities { get; init; } = 4;
    public int DesiredConstructionYards { get; init; } = 3;
    /// <summary>Construction yards the AI builds before any other yard (they are the engine of every other build). DEFAULT.</summary>
    public int EarlyConstructionYards { get; init; } = 2;
    /// <summary>Yards of one family stack on one system up to this many (they share one project and finish it faster; m2-construction §Build time). DEFAULT.</summary>
    public int YardsPerSystem { get; init; } = 3;
    /// <summary>The second and later yards are only ordered when the refined pool holds at least this much. DEFAULT.</summary>
    public int ExtraYardRefinedReserve { get; init; } = 40;

    // ---- production
    /// <summary>Regiments kept on the system whose loss ends the game (Coruscant for the Empire, the Rebel HQ for the Alliance), at least. DEFAULT.</summary>
    public int CriticalGarrison { get; init; } = 8;
    /// <summary>Most regiments in one garrison order. DEFAULT.</summary>
    public int GarrisonBatch { get; init; } = 4;
    /// <summary>Regiments (aboard or waiting at the rally system, or queued for it) the AI keeps ready to invade. DEFAULT.</summary>
    public int InvasionRegimentTarget { get; init; } = 10;
    /// <summary>Troop slots the AI wants on its ships. DEFAULT.</summary>
    public int TroopCapacityTarget { get; init; } = 12;
    /// <summary>Most fighters in one order. DEFAULT.</summary>
    public int FightersPerOrder { get; init; } = 4;
    /// <summary>Most capital ships in one order. DEFAULT.</summary>
    public int MaxShipsPerOrder { get; init; } = 4;
    /// <summary>A warship is only ordered when the yards of the shipyard system finish it within this many days (m2-construction §Build time: ceil(cost / yards) x 4). DEFAULT.</summary>
    public int MaxBuildDays { get; init; } = 230;
    /// <summary>One ship order keeps its yard busy at most this many days (count x build days), but at least one ship. DEFAULT.</summary>
    public int MaxOrderDays { get; init; } = 160;
    /// <summary>The enemy force a purchase is measured against (strength per price, FleetModule's CombatStrength). DEFAULT: the other side's typical line.</summary>
    public IReadOnlyList<string> ReferenceEnemy(Side side) => side == Side.Empire ? new[] { "Mon Calamari Cruiser", "Mon Calamari Cruiser", "Nebulon-B Frigate" } : new[] { "Imperial Star Destroyer", "Imperial Star Destroyer", "Victory Destroyer" };
    /// <summary>A warship above this refined cost is only ordered when the refined pool holds at least twice its cost. DEFAULT.</summary>
    public int HeavyShipCost { get; init; } = 80;

    // ---- military
    /// <summary>The fleets at the capital keep adding up (lowest id first) until they reach this strength; they are the home defence and never leave. DEFAULT.</summary>
    public int HomeDefenceStrength { get; init; } = 60;
    /// <summary>A strike fleet leaves its base only when at least this strong. DEFAULT.</summary>
    public int LaunchStrength { get; init; } = 40;
    /// <summary>Own strength must be this percent of the enemy fleets standing at a target (or a relief target). DEFAULT.</summary>
    public int AttackOddsPercent { get; init; } = 150;
    /// <summary>A battle lost (annihilated) at a system is remembered this many days; the odds asked there are multiplied by <see cref="LossOddsFactor"/>. DEFAULT.</summary>
    public int LossMemoryDays { get; init; } = 150;
    public int LossOddsFactor { get; init; } = 3;
    /// <summary>How much of a squadron's strength counts when buying ships (percent). Fights are weighed on capital ships only. DEFAULT: the measure overrates squadrons.</summary>
    public int FighterTrustPercent { get; init; } = 10;
    /// <summary>The AI keeps squadrons at no more than this percent of its fighter berths (their upkeep is real, their worth uncertain). DEFAULT.</summary>
    public int FighterFillPercent { get; init; } = 60;
    /// <summary>A pile (with the fleets already there) goes to relieve a held system when its power is at least this percent of the enemy fleets on or heading to it. DEFAULT.</summary>
    public int DefendOddsPercent { get; init; } = 110;
    /// <summary>A fleet standing with enemy fleets leaves when its power is below this percent of theirs. DEFAULT.</summary>
    public int RetreatOddsPercent { get; init; } = 80;
    /// <summary>Enemy fleets are hunted only when at most this many ticks away. DEFAULT.</summary>
    public int HuntTicks { get; init; } = 60;
    /// <summary>The system that wins the game (the Rebel HQ for the Empire, Coruscant for the Alliance) is attacked from any distance once a pile is this strong (percent of REF). DEFAULT.</summary>
    public int WinStrikeStrength { get; init; } = 40;
    /// <summary>A system lost to the enemy this many days ago or less is worth double as a target. DEFAULT.</summary>
    public int RetakeDays { get; init; } = 200;
    /// <summary>Attacking regiments wanted per defending regiment, in percent (the duels favour the defender; m3-assault §Resolution). DEFAULT.</summary>
    public int RegimentsPerDefenderPercent { get; init; } = 200;
    /// <summary>Regiments aboard before a fleet leaves for an assault. DEFAULT.</summary>
    public int MinLaunchRegiments { get; init; } = 2;
    /// <summary>Targets are systems of the other side or neutral ones at most this many lanes from a system the side holds. DEFAULT.</summary>
    public int TargetHops { get; init; } = 2;
    /// <summary>The AI stops shelling a system after this many bombardments in 30 days. DEFAULT.</summary>
    public int MaxBombardmentsPer30Days { get; init; } = 4;
    /// <summary>A fleet whose ships average below this hull percent stays (or goes) to a shipyard instead of launching. DEFAULT.</summary>
    public int RepairBelowPercent { get; init; } = 70;

    // ---- what to build, by side (first class that is buildable, researched and affordable wins). Class names of unit-and-facility-costs.csv. DEFAULT.
    public IReadOnlyList<string> Warships(Side side) => side == Side.Empire ? EmpireWarships : AllianceWarships;
    public IReadOnlyList<string> HeavyWarships(Side side) => side == Side.Empire ? EmpireHeavy : AllianceHeavy;
    public string Transport(Side side) => side == Side.Empire ? "Star Galleon" : "Bulk Transport";
    public string Fighter(Side side) => side == Side.Empire ? "TIE Fighter" : "X-wing";
    public string GarrisonRegiment(Side side) => side == Side.Empire ? "Imperial Army Regiment" : "Alliance Army Regiment";
    public string InvasionRegiment(Side side) => side == Side.Empire ? "Stormtrooper Regiment" : "Alliance Fleet Regiment";

    public IReadOnlyList<string> EmpireWarships { get; init; } = new[] { "Lancer Frigate", "Carrack Light Cruiser", "Victory Destroyer" };
    public IReadOnlyList<string> AllianceWarships { get; init; } = new[] { "Corellian Corvette", "Corellian Gunship", "Nebulon-B Frigate" };
    public IReadOnlyList<string> EmpireHeavy { get; init; } = new[] { "Imperial Star Destroyer" };
    public IReadOnlyList<string> AllianceHeavy { get; init; } = new[] { "Mon Calamari Cruiser" };
}
