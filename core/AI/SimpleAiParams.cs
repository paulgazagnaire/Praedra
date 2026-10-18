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
    /// <summary>
    /// The 1998 AI's garrison floor (B6b §3): on every own world below 70 support it wants min((79 - s) / 10, 6) regiments (doubled in an uprising),
    /// and 9 on its special world (the Rebel HQ for the Alliance, Coruscant for the Empire); it builds toward that and never moves a regiment that
    /// would leave a world below it. Replaces the B5a last-regiment guard (a weaker form: 1 below 60). It belongs to derived control
    /// (GameSettings.ControlRule = Derived), so it is DEFAULT off with it (Dev lead 2026-10-18: Derived is DEFAULT off until the AI copes; with it,
    /// QA's 16-seed Medium mirror gave 0/16 Empire wins); turn both on together. Off = the rule's need, <see cref="CriticalGarrison"/> on the
    /// critical world, and the B5a guard.
    /// </summary>
    public bool OriginalGarrisonFloor { get; init; }
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
    /// <summary>
    /// Fleets that only retreat: when enemy fleets retreated from this side's fleets at least RunnerRetreats times within RunnerMemoryDays at a
    /// system or next to it (the side's own event feed), enemy fleets there are no longer hunted for their own sake (the morale retreat of
    /// m4-battle-classic jumps them to a neighbour, so the hunt turns into a chase with no losses). The system stays a target for an assault. DEFAULT.
    /// </summary>
    public int RunnerRetreats { get; init; } = 1;
    public int RunnerMemoryDays { get; init; } = 30;
    /// <summary>A battle where this side's fleets retreated counts like an annihilation for the odds asked there (LossOddsFactor for LossMemoryDays). DEFAULT.</summary>
    public bool RetreatCountsAsLoss { get; init; } = true;
    /// <summary>
    /// No trip stops (destination included) at a system where the side's fleets retreated within RunnerMemoryDays (it is no target or relief
    /// meanwhile), nor stops on the way where enemy fleets the side sees now outmatch the fleets leaving (RetreatOddsPercent): the forced stop
    /// and the morale retreat would only bounce the fleet back. With fog, scouts also skip trips that stop where armed enemy ships were seen lately. DEFAULT.
    /// </summary>
    public bool AvoidBlockedPaths { get; init; } = true;
    /// <summary>
    /// With fog, a stop on the way at a system the picture shows enemy-held counts as blocked unless its fleets are seen there now: the side
    /// cannot see a gravity well it has not looked at, and the enemy's capital always has one at the start (a fogged strike pile flew
    /// through Coruscant and was pulled out and destroyed on the v5 map). The router then goes around (free travel). DEFAULT (lead 2026-10-16).
    /// </summary>
    public bool FogAvoidEnemyStops { get; init; } = true;

    /// <summary>
    /// A lone home guard stronger than HomeDefenceStrength splits off its newest armed ships as a strike group, so new ships built at a
    /// shipyard world don't sit merged into the guard all game (the Alliance pile parked on its HQ on the v5 map). DEFAULT (lead 2026-10-16).
    /// </summary>
    public bool SplitHomeSurplus { get; init; } = true;

    /// <summary>
    /// A fleet going around a blocked stop sends one order with waypoints for the whole detour (the router's waypoints, m1-travel §Path rule (3);
    /// Paul approved the AI adding its own waypoints, 2026-10-16 19:37). Off = one leg at a time, planning again on arrival with what the side sees then. DEFAULT off (lead 2026-10-16): measured on the v5 map,
    /// on made two play tests miss their win (Alliance vs passive Empire, fogged Empire seed 3), because a committed detour cannot react. The AI trainer may revisit it.
    /// </summary>
    public bool DetourWithWaypoints { get; init; } = false;
    /// <summary>
    /// A detour whose first stop is a system the side does not hold ends instead at the first held stop of the way around (SimpleAi.DetourLeg),
    /// so the fleet does not forget its trip at a stranger's system and turn back (AI trainer 2026-10-17: a strike pile bounced between its
    /// rally system and an Imperial stop for 200 days on the v5 map). DEFAULT off: with this and InvasionBatchesOnly on, Medium against
    /// Medium without them took 54% of the systems over 16 seeds x both sides, inside the 55% a placebo change scored, and the single-seed
    /// play tests took other courses (fogged Empire seed 3 had no winner by day 1900). Off = the AI of before.
    /// </summary>
    public bool DetourToHeldStop { get; init; } = false;
    /// <summary>
    /// The invasion regiments the AI counts as ready (InvasionRegimentTarget) are those aboard and the spares standing at least
    /// MinLaunchRegiments together; a lone spare on a far system is not counted (AI trainer 2026-10-17: thirty lone starting regiments on
    /// Alliance worlds stopped every invasion build on the v5 map). DEFAULT off, measured with DetourToHeldStop (see there); alone it took the
    /// passive Alliance's HQ on day 748 instead of 1282 but ended the AI-vs-AI seed 5 game on day 214 with no battle. Off = every spare counts.
    /// </summary>
    public bool InvasionBatchesOnly { get; init; } = false;
    /// <summary>A strike pile of WinStrikeStrength fetches the regiments for the game-winning system first when at most this many regiments are believed there (-1 = never). DEFAULT.</summary>
    public int WinRushDefenders { get; init; } = 2;
    /// <summary>
    /// A fleet that has shelled a target lands when it carries what PickTarget asked (Need of half the believed defenders), not the full Need again.
    /// Off = the AI of before (it shelled and left forever when the believed garrison stayed at the unseen guess). DEFAULT true (m6 stall fix).
    /// </summary>
    public bool ShelledLandingNeed { get; init; } = true;
    /// <summary>A fleet whose ships average below this hull percent stays (or goes) to a shipyard instead of launching. DEFAULT.</summary>
    public int RepairBelowPercent { get; init; } = 70;

    // ---- difficulty handicaps (ai/09 §6, specs/ai/data/difficulty-table.csv; AiLevels sets them per level). DEFAULT = off (the Medium AI).
    /// <summary>
    /// Pick noise: the chance (percent) that a strike pile takes a random one of its three best targets instead of the best (private generator).
    /// ai/09 §6 row "pick noise" (Easy 30). DEFAULT 0.
    /// </summary>
    public int PickNoisePercent { get; init; }
    /// <summary>
    /// Most offensives at once: armed own fleets flying to, or standing on, a system the side does not hold. A strike pile does not pick a new
    /// target while that many are out (it still relieves held systems). ai/09 §6 row "concurrent offensive jobs" (Easy 1). DEFAULT no limit.
    /// </summary>
    public int MaxOffensives { get; init; } = int.MaxValue;

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
    /// <summary>A scout tries at most this many of its best targets for a trip that stops nowhere near armed enemy ships or a recent own retreat. DEFAULT.</summary>
    public int ScoutPathChecks { get; init; } = 8;
    /// <summary>Empire: HQ belief adds up to this much scouting value (at the highest belief). DEFAULT.</summary>
    public int ScoutBeliefValue { get; init; } = 150;
    /// <summary>
    /// Scouts kept on top of ScoutCount as explorers (the lowest fleet ids): each goes to the nearest system the side has never explored, at any
    /// distance (no ScoutHops limit), over a trip that ScoutPathClear allows; with none left they scout as the others. AI trainer 2026-10-17
    /// (QA-2): the value per day of travel always picked a stale system near home, so in seed 3 the Empire never explored six systems in
    /// 3000 days, the Rebel HQ (Munto Codru) among them, and the game stalled with the Alliance holding 9 systems and no forces. DEFAULT 1 at
    /// every level (lead 2026-10-17: an AI that never finds the HQ is a bug, not a difficulty). 0 = none (the AI of before).
    /// </summary>
    public int ExplorerScouts { get; init; } = 1;

    // ---- fog: estimates from the last-seen picture (ai/02 §5 principle 2: age matters). DEFAULT.
    /// <summary>Enemy fleets of a picture older than this are forgotten when choosing targets. DEFAULT.</summary>
    public int FogFleetMemoryDays { get; init; } = 120;
    /// <summary>A fully stale picture (StaleDays old) asks this much more (percent) of the odds and of the regiments. DEFAULT.</summary>
    public int FogStaleMarginPercent { get; init; } = 50;
    /// <summary>
    /// The ground forces (group D) of an enemy-held system last seen more than this many days ago are forgotten: the AI assumes
    /// FogUnknownDefenders (FogUnknownWinDefenders on the winning system) as if never seen. Only espionage, an assault or holding the system
    /// shows group D (m5-fog-and-hq §F1), so an old count is never refreshed by a fleet passing by, and with the staleness margin it only grew.
    /// An assault on a wrong guess shows the true garrison, so a bad guess corrects itself. AI trainer 2026-10-17 (QA-5): in seed 8 Hard the
    /// Empire, holding 375-449 systems, never took Lannik and Toprawa (0 regiments each from day 2400) because it still believed the 6
    /// regiments it saw there around day 1250 (9 with the margin, so 18 regiments asked aboard). 0 = never forget (the AI of before).
    /// </summary>
    public int FogGroundForgetDays { get; init; } = 365;

    /// <summary>
    /// The 1998 AI's leader marks (B6c §7): the six story characters (Luke, Mon Mothma, the Emperor, Vader, Leia, Han) are never fleet or army
    /// commanders, the Emperor is never a diplomat, and the Empire keeps the Emperor at the Seat of Power while it holds it (Emperor home;
    /// SimpleAiAgents.EmperorHome). The leaders' ban from Incite, Rescue, Sabotage and Abduction is already this AI's rule for every major
    /// character (NoKeyKinds). QA-8 (2026-10-18): without it the Emperor stood on Coruscant 1-7% of days, so the Seat of Power was off ~95%
    /// of the game, and he was captured on missions. DEFAULT true (B6c §7 Work 7; the 1998 new-game choice that turns the marks off is not
    /// identified).
    /// </summary>
    public bool LeaderMarks { get; init; } = true;
    /// <summary>Mon Mothma's character id, one of the B6c §7 leaders: MJCHARSD 576 (data/design/roster.csv).</summary>
    public int MonMothmaId { get; init; } = 576;
    /// <summary>Regiments assumed on the enemy's game-winning system (Coruscant for the Alliance, a known HQ) when its ground was never seen. DEFAULT.</summary>
    public int FogUnknownWinDefenders { get; init; } = 8;
    /// <summary>Bombardments of the game-winning system in 30 days at most (with fog the AI cannot see the shelling work). DEFAULT.</summary>
    public int FogWinBombardmentsPer30Days { get; init; } = 8;
    /// <summary>Regiments killed by own bombardments (the side's own reports) are taken off an unseen garrison guess for this many days. DEFAULT.</summary>
    public int FogKillMemoryDays { get; init; } = 60;
    /// <summary>Systems the picture shows enemy-held are targets up to this many lanes from a held system (others: TargetHops). DEFAULT.</summary>
    public int FogEnemyTargetHops { get; init; } = 4;
    /// <summary>
    /// Finishing the war: when the side holds at least this many times the systems it knows the enemy to hold, an enemy-held target is worth
    /// FinishValuePercent of its value (neutral systems near home otherwise win on value per day of travel, and the beaten side lives on in
    /// its last systems). AI trainer 2026-10-17 (QA-2): in seed 3 the Empire held 355 systems against 20 Alliance systems it knew, with 0 or 1
    /// regiment each, and kept taking neutrals for 800 days. 0 = off (the AI of before).
    /// </summary>
    public int FinishWhenAheadRatio { get; init; }
    public int FinishValuePercent { get; init; } = 400;

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

    // ---- characters and missions (m6 AI; ai/05 §4-5 and §7b, ai/12 P8 and P9). All AI tuning, DEFAULT. The rules they act through are
    // m5-characters-and-force (posts, MoveCharacter) and m5-missions (StartMission, CancelMission); the odds are the core's own tables
    // (MissionModule.Lookup, FoilChance, DetectionX). With UseFog the AI reads only its side's knowledge for every target and defender count.
    /// <summary>
    /// Use characters (fleet and system posts) and missions. DEFAULT true. False = the AI of before characters and missions, byte for byte
    /// (no character or mission order, nothing else changes).
    /// </summary>
    public bool UseCharactersAndMissions { get; init; } = true;
    /// <summary>Days between character and mission reviews (offset by the side number like the other reviews). DEFAULT.</summary>
    public int AgentsEvery { get; init; } = 2;
    /// <summary>Lowest leadership for a fleet or system post (ai/05 §5: the original's commander slots ask 50). DEFAULT.</summary>
    public int OfficerMinLeadership { get; init; } = 50;
    /// <summary>A fleet gets officers when its capital ships are at least this strong (percent of REF, as LaunchStrength). DEFAULT.</summary>
    public int OfficerFleetStrength { get; init; } = 20;
    /// <summary>At most this many fleets (the strongest) get officers; the rest of the leaders go on missions. DEFAULT.</summary>
    public int OfficerFleets { get; init; } = 3;
    /// <summary>Empire with fog: characters kept for the HQ hunt (best espionage first) while the HQ is unknown or due a re-check. DEFAULT.</summary>
    public int HqSpies { get; init; } = 4;
    /// <summary>Lowest espionage of an HQ spy (ai/05 §4: the original's floor for espionage kinds is 51). DEFAULT.</summary>
    public int SpyMinEspionage { get; init; } = 51;
    /// <summary>HQ search targets: this many systems of the highest search score (belief weighted by room and lanes, see SpyRoomPercent). DEFAULT.</summary>
    public int HqSearchTargets { get; init; } = 5;
    /// <summary>
    /// The Alliance hides its HQ far from the Empire and never in a dead end (ai/07 §7; the Alliance AI's own HqMinLanes): a search target's
    /// belief is raised SpyRoomPercent x r x r percent, r = lanes from the nearest Imperial system (at most SpyRoomCap), and cut to
    /// SpyDeadEndPercent with fewer than HqMinLanes lanes; a system where an own look was foiled within SpyFoilMemoryDays is skipped. DEFAULT.
    /// </summary>
    public int SpyRoomPercent { get; init; } = 25;
    public int SpyRoomCap { get; init; } = 8;
    public int SpyDeadEndPercent { get; init; } = 50;
    public int SpyFoilMemoryDays { get; init; } = 120;
    /// <summary>
    /// A known HQ whose look was foiled is looked at again only after this many days (its garrison must be seen before an assault, and a
    /// foiled team suffers nothing, m5-missions OnTeamExposed none; but each detected spy warns the Alliance). 0 = at once. DEFAULT.
    /// </summary>
    public int HqRecheckFoilDays { get; init; } = 30;
    /// <summary>
    /// Empire with fog: probe droids (special forces whose mission bit allows Reconnaissance, m5-missions §Special forces) kept for the HQ hunt
    /// while the HQ is unknown. Each free one goes alone on a Reconnaissance of the best HQ search target not already looked at (the deeper
    /// list of DroidSearchTargets), so the characters keep the best targets and the droids sweep the rest. A look that succeeds clears the
    /// system on the belief map; a cheap droid is what a player sends (cost 1, upkeep 1). 0 = none (DEFAULT, the Medium AI).
    /// </summary>
    public int HuntDroids { get; init; }
    public string HuntDroidClass { get; init; } = "Imperial Probe Droid";
    public int DroidSearchTargets { get; init; } = 24;
    /// <summary>For the spies a candidate whose owner the side has never seen weighs at least this (the belief map gives it BeliefUnexplored). DEFAULT.</summary>
    public int SpyUnexploredBelief { get; init; } = 120;
    /// <summary>Spies farther than SpyBaseSlackTicks from the held system nearest the SpyBaseTargets best search targets move there first. DEFAULT.</summary>
    public int SpyBaseTargets { get; init; } = 3;
    public int SpyBaseSlackTicks { get; init; } = 15;
    /// <summary>A known HQ is looked at again when the Empire's picture of it is this many days old (it may have moved: m5-fog-and-hq §N5). DEFAULT.</summary>
    public int HqRecheckDays { get; init; } = 60;
    /// <summary>Mission values (the planner's units: value x chance / (travel days + MissionTravelBias)). DEFAULT.</summary>
    public int HqSearchValue { get; init; } = 1000;
    public int HqRecheckValue { get; init; } = 1000;
    /// <summary>Espionage counts this percent of Reconnaissance for the same chance (it also shows the ground forces and extra systems). DEFAULT.</summary>
    public int EspionageOverReconPercent { get; init; } = 125;
    public int SubdueValue { get; init; } = 900;
    public int RescueValue { get; init; } = 800;
    public int AbductionValue { get; init; } = 500;
    /// <summary>Diplomacy on an own system: DiplomacyValue x (DiplomacyStopSupport - support) / DiplomacyStopSupport; neutral: NeutralDiplomacyPercent of that. DEFAULT.</summary>
    public int DiplomacyValue { get; init; } = 400;
    public int NeutralDiplomacyPercent { get; init; } = 60;
    public int InciteValue { get; init; } = 300;
    /// <summary>Sabotage: per facility the picture shows at the target. DEFAULT.</summary>
    public int SabotageValuePerFacility { get; init; } = 60;
    public int ResearchValue { get; init; } = 250;
    public int MissionTravelBias { get; init; } = 10;
    /// <summary>A mission is started only when its estimated chance (not detected x success, percent) is at least this. DEFAULT.</summary>
    public int MissionMinOddsPercent { get; init; } = 15;
    /// <summary>Repeating missions (Diplomacy, Incite, Subdue, Research) ask at least this per iteration. DEFAULT.</summary>
    public int RepeatMinOddsPercent { get; init; } = 10;
    /// <summary>Own systems below this own support get Diplomacy; a Diplomacy mission is cancelled once its target reaches DiplomacyStopSupport. DEFAULT.</summary>
    public int DiplomacyOwnBelow { get; init; } = 60;
    public int DiplomacyStopSupport { get; init; } = 75;
    /// <summary>Neutral systems at most this many lanes from a held system get Diplomacy (the border). DEFAULT.</summary>
    public int DiplomacyHops { get; init; } = 1;
    /// <summary>Most diplomats out at once. DEFAULT.</summary>
    public int MaxDiplomats { get; init; } = 3;
    /// <summary>Lowest skill sent per kind (ai/05 §4: the original's floors are diplomacy 69, incite/subdue 49, combat kinds 51, research 10). DEFAULT.</summary>
    public int DiplomacyMinSkill { get; init; } = 60;
    public int LeadershipMinSkill { get; init; } = 49;
    public int CombatMinSkill { get; init; } = 51;
    public int ResearchMinSkill { get; init; } = 30;
    /// <summary>Alliance: most Incite and Sabotage missions out at once (only on enemy systems where no defender is believed). DEFAULT.</summary>
    public int MaxSubversion { get; init; } = 2;
    /// <summary>
    /// Special forces as mission agents (QA-6; B6c §7: the 1998 AI's Sabotage, Recon, espionage-team and Abduction/Assassination filters admit
    /// special forces, inferred from the mask; B6b §9: it keeps at least 8 of them). On: the AI keeps SpecialForcesWanted special forces of its
    /// AgentForceClasses and sends each free one alone on any planned mission whose record's mask holds its class bit (m5-missions §Special
    /// forces), scored like a character with its class columns as skills. A character goes first on a tie. Off = the AI of before. DEFAULT off
    /// (2026-10-18, the Dev lead's call): on, both arms of the stall test land more and its 1.5x floor fails (85 against 58).
    /// </summary>
    public bool SpecialForceAgents { get; init; }
    /// <summary>Special forces of the agent classes kept, built or queued (B6b §9: "at least 8"). DEFAULT 8.</summary>
    public int SpecialForcesWanted { get; init; } = 8;
    /// <summary>
    /// The agent classes built, fewest first (list order on a tie): one per kind of job the AI plans (m5-missions §Special forces masks).
    /// Alliance: Infiltrators (Sabotage, Rescue, Abduction), Guerrillas (Incite, Subdue). Empire: ISB Agents (Espionage on the HQ hunt), Emperor's
    /// Hands (Rescue, Abduction), Imperial Commandos (Subdue). The Bothan Spies are left out: the Alliance plans no Espionage. DEFAULT.
    /// </summary>
    public IReadOnlyList<string> AgentForceClasses(Side side) => side == Side.Empire ? EmpireAgentForces : AllianceAgentForces;
    public IReadOnlyList<string> AllianceAgentForces { get; init; } = new[] { "Infiltrators", "Guerrillas" };
    public IReadOnlyList<string> EmpireAgentForces { get; init; } = new[] { "Imperial Espionage Droid", "Noghri Death Commandos", "Imperial Commandos" };
    /// <summary>A picture's owner is trusted for a mission target (Diplomacy on neutral, Incite) when at most this old. DEFAULT.</summary>
    public int MissionOwnerTrustDays { get; init; } = 60;
    /// <summary>An enemy character is an Abduction target only when the side's own feed placed it at a system it sees within this many days. DEFAULT.</summary>
    public int AbductionInfoDays { get; init; } = 5;
    /// <summary>
    /// Key characters (the majors: Luke, Leia, Han, Mon Mothma, Vader, Palpatine) count this percent of their mission value (others are
    /// preferred) and never go on Rescue, Abduction, Sabotage or Incite (ai/05 §4: the original keeps its leaders off those). With
    /// m5-characters-and-force MajorsCanDie on (DEFAULT off) they also stay off every mission where any defender is believed. DEFAULT.
    /// </summary>
    public int KeyCharacterValuePercent { get; init; } = 50;

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

    // ---- the Death Star. rule: m5-death-star §6 (Computer opponent)
    /// <summary>
    /// The Empire AI builds, keeps, escorts and fires the Death Star at the Rebel HQ it knows. DEFAULT true (m5-death-star §6). False = the AI
    /// of before: it never builds one and treats a Death Star it happens to own as any ship.
    /// </summary>
    public bool UseDeathStar { get; init; } = true;
    /// <summary>A Death Star is queued only when the maintenance headroom left after adding its upkeep is at least this percent of the capacity. DEFAULT 10 (m5-death-star §6.1).</summary>
    public int DeathStarHeadroomMarginPercent { get; init; } = 10;
    /// <summary>
    /// Strength (FleetModule.StrengthOf scale) of the escort that must stand with the Death Star before it leaves. Null = the economic blockade
    /// threshold E from the blockade data (3 Victory Star Destroyers). DEFAULT null (m5-death-star §6.3).
    /// </summary>
    public double? DeathStarEscortStrength { get; init; }
}
