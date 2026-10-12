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
    /// <summary>A ship's price for the strength-per-price choice is refined cost plus this many times its upkeep. DEFAULT.</summary>
    public int UpkeepWeight { get; init; } = 3;
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
    public int FighterTrustPercent { get; init; } = 0;
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

    // ---- fog of war. rule: m5-fog-and-hq §N8 (the AI reads exactly what its side sees)
    /// <summary>
    /// Read the other side only through the side's KnowledgeView (pictures, own sightings, filtered events, own HQ belief). DEFAULT false for
    /// now so the existing AI behaviour and tests do not change; the owner's decision (economy-ai D4, ai/12 D3) is that the AI plays fair, so
    /// this is to become true once the M6 AI reads the view itself.
    /// </summary>
    public bool UseFog { get; init; }
    /// <summary>With fog, the regiments assumed on an enemy-held system whose ground forces the side has never seen (a system its picture shows neutral: none). DEFAULT (AI tuning, no rule).</summary>
    public int FogUnknownDefenders { get; init; } = 2;

    // ---- fog: scouting (m6 fog AI; ai/12 P7, ai/07 §7 "spend scouts where probability / travel time is highest"). All AI tuning, DEFAULT.
    // A scout is a fleet of exactly one ship of a scout class with nothing aboard; the AI keeps up to ScoutCount of them (fewer when their
    // upkeep would pass ScoutUpkeepBudget), splitting one off a pile or building one, and sends each to the system whose picture is most
    // worth refreshing (unexplored, stale, enemy-held, or a likely Rebel HQ for the Empire) per day of travel.
    /// <summary>Scouts wanted (fog only). DEFAULT.</summary>
    public int ScoutCount { get; init; } = 4;
    /// <summary>Most upkeep (maintenance points) all scouts together may cost. DEFAULT.</summary>
    public int ScoutUpkeepBudget { get; init; } = 60;
    /// <summary>Scout classes by side, first available wins for builds (cheap upkeep first). DEFAULT.</summary>
    public IReadOnlyList<string> ScoutClasses(Side side) => side == Side.Empire ? EmpireScouts : AllianceScouts;
    public IReadOnlyList<string> EmpireScouts { get; init; } = new[] { "Galleon" };
    public IReadOnlyList<string> AllianceScouts { get; init; } = new[] { "Medium Transport" };
    /// <summary>Scouts look at systems at most this many lanes from a held system (Empire: HQ candidates at any distance). DEFAULT.</summary>
    public int ScoutHops { get; init; } = 4;
    /// <summary>Scouting value of a never-explored system. DEFAULT.</summary>
    public int ScoutUnexploredValue { get; init; } = 100;
    /// <summary>A picture this many days old is fully stale (value 100 for scouting; full staleness margin for estimates). DEFAULT.</summary>
    public int StaleDays { get; init; } = 120;
    /// <summary>A stale picture of an enemy-held system is worth this many times a neutral one for scouting. DEFAULT.</summary>
    public int ScoutEnemyFactor { get; init; } = 2;
    /// <summary>Scouts avoid systems where the picture shows armed enemy ships seen within this many days. DEFAULT.</summary>
    public int ScoutAvoidFleetDays { get; init; } = 30;
    /// <summary>Empire: HQ belief adds up to this much scouting value (at the highest belief). DEFAULT.</summary>
    public int ScoutBeliefValue { get; init; } = 150;

    // ---- fog: estimates from the last-seen picture (ai/02 §5 principle 2: age matters). DEFAULT.
    /// <summary>Enemy fleets of a picture older than this are forgotten when choosing targets. DEFAULT.</summary>
    public int FogFleetMemoryDays { get; init; } = 120;
    /// <summary>A fully stale picture (StaleDays old) asks this much more (percent) of the odds and of the regiments. DEFAULT.</summary>
    public int FogStaleMarginPercent { get; init; } = 50;
    /// <summary>Regiments assumed on the enemy's game-winning system (Coruscant for the Alliance, a known HQ) when its ground was never seen. DEFAULT.</summary>
    public int FogUnknownWinDefenders { get; init; } = 8;
    /// <summary>Bombardments of the game-winning system in 30 days at most (with fog the AI cannot see the shelling work). DEFAULT.</summary>
    public int FogWinBombardmentsPer30Days { get; init; } = 8;
    /// <summary>Regiments killed by own bombardments (the side's own reports) are taken off an unseen garrison guess for this many days. DEFAULT.</summary>
    public int FogKillMemoryDays { get; init; } = 60;
    /// <summary>Systems the picture shows enemy-held are targets up to this many lanes from a held system (others: TargetHops). DEFAULT.</summary>
    public int FogEnemyTargetHops { get; init; } = 4;

    // ---- fog: the Empire's belief map of the Rebel HQ (ai/07 §7 "Empire: belief-map search", ai/12 D1 Decided YES; m5-fog-and-hq §N7 clues).
    // Weight per system, from the Empire's picture only. A system the picture shows Alliance-held (the HQ stands only on Alliance systems,
    // §N7 4) or never explored (rim only) can hold the HQ; signs of a capital (fleets, regiments, facilities seen, Alliance sightings) add
    // weight; a fresh group C look that showed no HQ (Yavin's day-0 copy, captures) wipes it out, and the weight returns as that look ages.
    /// <summary>Weight of a never-explored, non-Core system. DEFAULT.</summary>
    public int BeliefUnexplored { get; init; } = 30;
    /// <summary>Weight of a system the picture shows Alliance-held. DEFAULT.</summary>
    public int BeliefAllianceHeld { get; init; } = 100;
    /// <summary>Weight of a system the picture shows neutral or Imperial, once that picture is older than BeliefOwnerTrustDays. DEFAULT.</summary>
    public int BeliefStaleOther { get; init; } = 10;
    public int BeliefOwnerTrustDays { get; init; } = 90;
    /// <summary>A group C look without the HQ clears the system; the weight comes back linearly over this many days. DEFAULT.</summary>
    public int BeliefClearedDays { get; init; } = 300;
    /// <summary>Added per Alliance capital ship last seen at the system (picture group E, up to FogFleetMemoryDays old). DEFAULT.</summary>
    public int BeliefPerShipSeen { get; init; } = 10;
    /// <summary>Added per facility of the picture (group C). DEFAULT.</summary>
    public int BeliefPerFacilitySeen { get; init; } = 5;
    /// <summary>Added per Alliance regiment of the picture (group D). DEFAULT.</summary>
    public int BeliefPerRegimentSeen { get; init; } = 5;
    /// <summary>Added per Alliance fleet sighting at the system in the side's log, at most BeliefSightingCap. DEFAULT.</summary>
    public int BeliefPerSighting { get; init; } = 5;
    public int BeliefSightingCap { get; init; } = 50;
    /// <summary>
    /// Alliance support seen above BeliefSupportFloor adds (support - floor)^2 / BeliefSupportDivisor (setup puts the HQ at support 100,
    /// setup-victory/09 §2, and the Alliance keeps its capital loyal; the picture's group B shows the value). DEFAULT.
    /// </summary>
    public int BeliefSupportFloor { get; init; } = 50;
    public int BeliefSupportDivisor { get; init; } = 25;
    /// <summary>Added per other system of the same sector the picture shows Alliance-held (the HQ sits in the Alliance's heartland). DEFAULT.</summary>
    public int BeliefPerSectorAlly { get; init; } = 5;
    /// <summary>Weight multiplier for systems of a sector named by an HQ sector clue (only when the clue rule is on). DEFAULT.</summary>
    public int BeliefSectorClueFactor { get; init; } = 4;
    /// <summary>Target value added to an Alliance system at the highest belief (strike piles of WinStrikeStrength go at any distance). DEFAULT.</summary>
    public int SearchValue { get; init; } = 300;

    // ---- fog: the Alliance hides its HQ by risk (ai/07 §7 "Alliance: hide, bait and run"; ai/12 D1 Decided YES).
    // Risk of a system = Imperial fleet sightings in the side's log of the last ObservedWindowDays, weighted by lanes from it, plus Imperial
    // systems next to it. The HQ moves (MoveHq) when its risk reaches HqMoveRisk and its last move is HqMoveCooldownDays old, to the held,
    // non-dead-end system with the least risk and the most room from the Empire.
    /// <summary>Risk per Imperial fleet sighting at the system itself, one lane away, two lanes away. DEFAULT.</summary>
    public int HqRiskHere { get; init; } = 100;
    public int HqRiskNear { get; init; } = 40;
    public int HqRiskFar { get; init; } = 15;
    /// <summary>Risk per neighbouring system the picture shows Imperial. DEFAULT.</summary>
    public int HqRiskEnemyNeighbour { get; init; } = 25;
    /// <summary>The HQ moves at this risk. DEFAULT.</summary>
    public int HqMoveRisk { get; init; } = 100;
    /// <summary>Days between HQ moves at least (each arrival costs 10 support in the sector, m5-fog-and-hq §F6). DEFAULT.</summary>
    public int HqMoveCooldownDays { get; init; } = 90;
    /// <summary>A destination needs at least this many lanes (no dead ends: a one-fleet blockade traps the HQ, ai/07 §7). DEFAULT.</summary>
    public int HqMinLanes { get; init; } = 2;

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
