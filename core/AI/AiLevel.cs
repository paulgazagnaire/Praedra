namespace Praedra.Core.AI;

/// <summary>
/// How well the computer opponent plays. Every level sees only its side's fog-of-war view and gets no hidden bonus (ai/12 D3, Paul
/// 2026-10-12: Hard is smarter, never a cheat; economy-ai D4). The Classic head start (more systems and forces at setup, setup-victory/02) is a
/// separate setup choice (StartPreset), so a player can mix the two. rule: ai/09 §6 (difficulty is an AI parameter set, not a head start).
/// </summary>
public enum AiLevel
{
    /// <summary>Today's AI, handicapped: slower reviews, one offensive at a time, sometimes a worse target, fewer scouts and spies.</summary>
    Easy,
    /// <summary>The DEFAULT AI (<see cref="SimpleAiParams"/> defaults) playing to win, plus the Collective's guerrilla (SafeHearth, Cells, Guerrilla).</summary>
    Medium,
    /// <summary>Medium plus every upgrade that beat Medium in a seeded tournament (sim tourney). Same view, same rules, same costs.</summary>
    Hard,
}

public static class AiLevels
{
    /// <summary>
    /// The AI parameters of a level, on top of <paramref name="baseParams"/> (DEFAULT: the SimpleAiParams defaults with fog on, the fair
    /// setting of every level). Each value below is AI tuning measured with `sim tourney`; ai/09 §6 and specs/ai/data/difficulty-table.csv
    /// give the axes (review delay, pick noise, concurrent offensives, scouting and search quality).
    /// </summary>
    public static SimpleAiParams For(AiLevel level, SimpleAiParams? baseParams = null)
    {
        var p = GamePlan(baseParams ?? new SimpleAiParams { UseFog = true });
        return level switch
        {
            // ai/09 §6 Easy column: reacts late (reviews about twice as far apart), one offensive at a time, 30% pick noise, few scouts and spies.
            AiLevel.Easy => p with
            {
                MilitaryEvery = 6, ProductionEvery = 8, EconomyEvery = 10, AgentsEvery = 4,
                PickNoisePercent = 30, MaxOffensives = 1, ScoutCount = 2, HqSpies = 2, SafeHearth = true,
                FoundYardsBelow = FoundBelow,
            },
            // AI trainer 2026-10-26 (Paul: Easy about today's AI, Medium balanced, Hard really hard; the Collective plays guerrilla). The guerrilla
            // switches are Collective-only (both sides play to win, PlayToWin below). Medium Hand against the Collective, 96 seeds (1-96), day 3000, fog, Hand / Collective / no winner,
            // on main 9a594a9 (the Hearth flees, #176): the old Medium (all three off) 83 / 5 / 8; Easy here 94 / 0 / 2; Medium here 28 / 36 / 32
            // (without SafeHearth 33 / 27 / 36); Hard here 13 / 58 / 25. Notes: specs/engine/ai-notes/2026-10-26-guerrilla-collective.md.
            // AI trainer 2026-10-27 (QA-58; Paul: the Collective goes after a harvester being built): HarvesterYardValue 2000 at Medium and Hard.
            // 96 seeds against a Hand that builds harvesters (scratch: upkeep once built, 4x build, 8 yards): 18 harvesters spiked in the yard
            // (0 without), sightings 43 vs 35, Hand / Collective / open 17 / 55 / 24 vs 18 / 53 / 25; Medium mirror 27 / 43 / 26 vs 29 / 43 / 24.
            // AI trainer 2026-11-07 (Paul: fix the Collective training too few troops): InvasionReadyAtRally at Medium and Hard (numbers there).
            // AI trainer 2026-11-07 (Paul: send Talented agents on Burn the Books at the right moment): BooksTalentWindow at Medium and Hard. Medium
            // mirror, 192 seeds, day 3000, fog, on main 7765d8a: Hand / Collective / open 45 / 93 / 54 off, 48 / 89 / 55 on (noise). Against the
            // Hand AI the window never opened (0 known-Talent tries, against 127 off, 93 of them foiled by a senser at Charter); it
            // matters only against a Hand whose sensers leave Charter where the Collective sees them. No burns in either run.
            // AI trainer 2026-11-20 (Paul: the Hand starts with a big military, the Collective must stay hard to kill): with BigHandStart and
            // CollectiveBaseDraw on, the Medium Collective reviews its military every 2 days (was 3, Hard 1; the Medium Hand keeps 3). Medium Hand against Medium
            // Collective, MedE, fog, day 3000, Hand / Collective / open: 192 seeds on main 561755b before the big start 15 / 135 / 42; big start
            // (4 Mandate, 12 Gleaner) 61 / 99 / 32; 96 seeds: 4 Mandate fleet, every 3 days 24 / 54 / 18; 3 Mandate fleet, every 2 days
            // 14 / 70 / 12; 192 seeds, 3 Mandate fleet, every 2 days 34 / 132 / 26. Notes: specs/engine/ai-notes/2026-11-20-big-hand-start.md.
            // AI trainer 2026-11-27 (the adaptive commander, Paul 2026-11-26 "push this to the max"; QA-126 neither AI built defences): at Medium
            // and Hard both sides wall their capital (FortifyWorlds 1: 2 shields and a Lien-150 on Charter / the Hearth's world), shell shields
            // they can beat before landing (SiegeShields), and the Collective picks each front's plan by fit (FrontPlans: Campaign, Bleed,
            // Starve) and remembers what failed where (FrontMemory). MedE, fog, day 3000, Hand / Collective / open: Medium mirror, 96 seeds,
            // 6 / 80 / 10 before, 7 / 64 / 25 with walls and the first Siege (Charter taken 80 times, not 124); 32 seeds with the Siege that
            // only shells what it can beat 2 / 22 / 8; Hard Collective 1 / 26 / 5 before, 1 / 24 / 7 after; Hand walling 3 worlds 3 / 17 / 12
            // without Siege, 3 / 26 / 3 with it. FrontPlans and FrontMemory cost nothing (79 vs 80 of 96 at Medium, 74 vs 75 at Hard). Cut: a
            // second front at Medium (69 of 96), Hand fronts (0 of 32), RoomFloor, ShellHoles, a stronger Outreach drive.
            // Notes: specs/engine/ai-notes/2026-11-27-commander-night-log.md.
            // AI trainer "Rooster" 2026-11-27 (the commander's Collective plan 11, Neutralise; Medium and Hard, via Neutralised below): Collective
            // fleets shell the last 1 to 3 Hand regiments off worlds leaning its way (support 41+), which then go neutral or join by support
            // (m2-control-and-capture §Control), anywhere, not only in a Neutralise front. MedE, fog, day 3000, main f5482cc, Hand / Collective / open
            // (Hearth lost): Medium mirror, 96 seeds, 11 / 57 / 28 (19) before, 1 / 77 / 18 (5) with it; Hard Hand walling 3 worlds against the
            // Medium Collective, 96 seeds, 14 / 50 / 32 (18) and 15 / 63 / 18 (21); Medium Hand walling 4 worlds with 2 batteries, 32 seeds,
            // 2 / 17 / 13 and 1 / 23 / 8; Medium Hand against the Hard Collective, 32 seeds, 2 / 19 / 11 and 0 / 23 / 9. Front-only (no
            // NeutraliseElsewherePercent) changed nothing (4 / 20 / 8 against 5 / 18 / 9). Notes: specs/engine/ai-notes/rooster-log.md.
            AiLevel.Medium => Neutralised(BestNavy(PlayToWin(p))) with { FortifyWorlds = 1, SiegeShields = true, FrontPlans = true, FrontMemory = true, CollectiveMilitaryEvery = MediumCollectiveMilitaryEvery, SafeHearth = true, Cells = true, Guerrilla = true, Campaign = true, StrandedAgentsHome = true, HarvesterYardValue = HarvesterYardHunt, InvasionReadyAtRally = true, BooksTalentWindow = true, FoundYardsBelow = FoundBelow },
            // AI trainer 2026-10-17 (sim tourney, 16 seeds x both sides, Medium against Medium plus one change; a placebo change scored 55%, so
            // only a gap well above that counts): a military review every day (Medium every 3) took 63% of the systems, the one clear win of 15
            // tried. Probe speculators on the HQ hunt found the Hearth in 13 of 16 games against 8 for Medium without costing systems (51%); kept
            // because against a player the Hand wins by finding the HQ, not by holding every system.
            // AI trainer 2026-10-17 (QA-2): Hard also went after the enemy's last systems before neutral ones once five times bigger than the
            // enemy it knows; since 2026-10-18 every level does (FinishWhenAheadRatio DEFAULT 5, needed to end games under derived control).
            // AI trainer 2026-10-26: the Hard Collective keeps more agents out and raids more; on main f00fbb8 a Medium Hand won 24 of 96 against
            // Hard without these counts instead of 14 (more still cost wins: 32 forces, 16 missions, 6 looks and 4 raids gave 28 Hand wins).
            // AI trainer 2026-11-20 (the Hand's big start): the Hard Collective reaches one step farther with its agents and fleets (CellsHops,
            // GuerrillaHops 6, Medium 5); a drawn base can start far from the Hand. Medium Hand against the Hard Collective, MedE, fog, day 3000,
            // 192 seeds, Hand / Collective / open: before the big start 4 / 175 / 13; with it (3 Mandate fleet) 28 / 126 / 38 at reach 5,
            // 22 / 139 / 31 at reach 6 (Medium Collective 34 / 132 / 26). Its agent counts below no longer help (96 seeds: 15 / 66 / 15 with them,
            // 17 / 68 / 11 without) but are kept, as nothing better replaced them.
            AiLevel.Hard => Neutralised(BestNavy(PlayToWin(p))) with
            {
                MilitaryEvery = 1, HuntSpeculators = 8, SafeHearth = true, Cells = true, Guerrilla = true, Campaign = true, StrandedAgentsHome = true,
                CellsForcesWanted = 20, CellsSubversion = 10, WatchLooks = 4, RaidStrikes = 3, HarvesterYardValue = HarvesterYardHunt,
                InvasionReadyAtRally = true, BooksTalentWindow = true, CellsHops = HardReach, GuerrillaHops = HardReach,
                FoundYardsBelow = FoundBelow, CampaignsAtOnce = 2,
                FortifyWorlds = 1, SiegeShields = true, FrontPlans = true, FrontMemory = true, // the adaptive commander, as Medium (2026-11-27)
                CollectiveAbductionInfoDays = HardCollectiveAbductionInfoDays,
            },
            _ => p,
        };
    }

    /// <summary>
    /// A game plan at every level (AI trainer 2026-11-19; Paul: "Everytime I start a new game playing the Hand, the Collective ALWAYS capture
    /// Excubia ... I don't want the enemy to be predictable"). The AI's dice were seeded by the day only, and its strike piles took the best
    /// target, where the first ones from the Collective's rally often scored within 10% of each other: the same picture gave the same opening in
    /// every game. Now each game draws its liking for every sector (up to +PlanSectorSpread%) and system (up to +PlanSystemSpread%), redrawn every 180 days
    /// (until 2026-11-20 also dislikes, down to -30% and -15%: they held the first invasion back, median day 204 to 300; likes only, 228).
    /// The plan is the Collective's only (SimpleAi.PlanOn). 192 seeds, day 3000, fog, MedE start, on main 561755b, Hand / Collective / open, and
    /// the first Hand world the Collective invades (different worlds; share of the 3 most common): Medium mirror 24 / 136 / 32 (52; 43%) without,
    /// 15 / 135 / 42 (62; 36%) with; Medium Hand against the Hard Collective 3 / 169 / 20 (49; 46%) without, 4 / 175 / 13 (51; 35%) with.
    /// With the plan on both sides the mirror was 17 / 130 / 45. Notes: specs/engine/ai-notes/2026-11-19-game-plan.md.
    /// </summary>
    private static SimpleAiParams GamePlan(SimpleAiParams p) => p with { PlanSectorSpreadPercent = PlanSectorSpread, PlanSystemSpreadPercent = PlanSystemSpread };

    /// <summary>
    /// The Hard Collective aims Abductions at sightings up to 5 days old; the Hard Hand keeps the DEFAULT 2 (AI trainer 2026-11-28, QA-135
    /// follow-ups; Ponytail's #433 and #434 reviews: only the Collective must be hard to kill). Hard mirrors, 96 games (MedE 1-64, MedA 1-32),
    /// Collective Abductions succeeded / Hand Abductions succeeded / Hand-Collective-open: both 2 days 186 / 60 / 13-53-30; both 5 days (#434)
    /// 230 / 60 / 19-55-22; Collective 5 and Hand 2 (this) 246 / 62 / 15-59-22. Medium mirrors went the other way (#433), so Medium keeps 2.
    /// </summary>
    private const int HardCollectiveAbductionInfoDays = 5;

    /// <summary>The Medium Collective's military review interval in days (AI trainer 2026-11-20, against the big Hand start; the Hand keeps 3).</summary>
    private const int MediumCollectiveMilitaryEvery = 2;

    /// <summary>The Hard Collective's CellsHops and GuerrillaHops (AI trainer 2026-11-20; DEFAULT 5).</summary>
    private const int HardReach = 6;

    /// <summary>The game plan's spreads at every level (AI trainer 2026-11-19).</summary>
    public const int PlanSectorSpread = 30, PlanSystemSpread = 15;

    /// <summary>FoundYardsBelow at every level, both sides (AI trainer 2026-11-20, m2-construction §Founding a construction yard): a side with no
    /// construction yard left founds one on its safest world. 1, not 2 (Dev lead: only once it has lost its yards): over 128 Medium mirrors
    /// (MedA, MedE, default start) "below 2" and "below 1" end alike (Hand 16 / 13 wins, Collective 99 / 95), so the second yard adds nothing.</summary>
    public const int FoundBelow = 1;

    /// <summary>HarvesterYardValue at Medium and Hard (AI trainer 2026-10-27, QA-58): a Spike team at a harvester in the yard, below a finished harvester (SpikeValue 3000), above any other mission.</summary>
    private const int HarvesterYardHunt = 2000;

    /// <summary>
    /// Play to win (AI trainer 2026-10-26, QA-56; Paul 2026-10-26: "long, unending wars are no fun, make the AI play to win"). Both sides, Medium
    /// and Hard. A pile strong enough for the game-winning system fetches the regiments for it while up to WinRushDefenders are believed there
    /// (the Hand used to shell a known Hearth guarded by 3+ regiments and leave), and the AI keeps three times the invasion troops and troop
    /// ships ready: a taken world needs its garrison, so a side that had won the war took four worlds per 250 days. Medium against Medium,
    /// 96 seeds, day 3000, fog, Hand / Collective / no winner, on 30ac893: seeds 1-96 20 / 34 / 42 before, 30 / 33 / 33 with these; seeds
    /// 97-192 19 / 34 / 43 before, 25 / 34 / 37 with these. Medium Hand against the Hard Collective (seeds 1-96): 11 / 60 / 25.
    /// AI trainer 2026-10-27 (slow wars): the Collective holding Charter keeps 8 diplomats out (AfterCharterDiplomats) to win over the people of
    /// its fresh conquests, whose garrisons hold all its regiments. On main 0829865, seeds 1-96: 30 / 33 / 33 before, 29 / 43 / 24 with it (16
    /// diplomats: 29 / 42 / 25; 8 diplomats all game long: 16 / 48 / 32, the Collective stronger but the wars no shorter).
    /// </summary>
    private static SimpleAiParams PlayToWin(SimpleAiParams p) => p with { WinRushDefenders = 20, InvasionRegimentTarget = 30, TroopCapacityTarget = 36, AfterCharterDiplomats = 8 };

    /// <summary>
    /// The best navy (AI trainer 2026-11-26; Paul 2026-11-26: whichever side the AI plays, it researches and builds every ship it can and strives
    /// for the best navy; better tech is no change of doctrine, the Collective keeps its hit and run). Both sides, Medium and Hard: a
    /// construction yard per 5 worlds (up to 30, their upkeep from the margin), shipyards stacked 5 to a system and Advanced once researched
    /// (a big ship finishes within MaxBuildDays), idle warships below half the best's strength per upkeep scrapped for it at the room cap,
    /// three ship research teams at value 600, 15 shipyards, and a tenth of the warships Hold ships (Edict, Barricade). MedE, fog, day 3000,
    /// 32 seeds each, on main fa533ec, before / after. Medium mirror: top-tier ships built per game (Hand Decree II, Injunction, Mandate II,
    /// Edict; Collective Jubilee, Picket, Homeward, Barricade) Hand 0.3 / 3.1, Collective 3.2 / 18.8; Edicts 0 / 8 and Barricades 0 / 54 in
    /// all; ship research level at the end Hand 5.5 / 8, Collective 8 / 11; at day 1000 room Hand 1525 / 2100, Collective 2325 / 3325 and
    /// refined unspent Hand 1244 / 0, Collective 1671 / 0; Hand / Collective / open 4 / 21 / 7 and 1 / 26 / 5. Medium Hand against the Hard
    /// Collective: top-tier per game Hand 0.1 / 0.8, Collective 1.2 / 12.7 (Barricades 0 / 35); 1 / 25 / 6 and 2 / 28 / 2.
    /// Notes: specs/engine/ai-notes/2026-11-26-best-navy.md.
    /// </summary>
    private static SimpleAiParams BestNavy(SimpleAiParams p) => p with
    {
        WorldsPerConstructionYard = 5, MaxConstructionYards = 30, YardsFromMargin = true, ShipyardsPerSystem = 5, AdvancedShipyards = true,
        RefitBelowPercent = 50, ShipResearchMissions = 3, ShipResearchValue = 600, DesiredShipyards = 15, HoldShipPercent = 10,
        GarrisonsFirstHoldsNavy = true,
    };

    /// <summary>Neutralise at Medium and Hard (AI trainer "Rooster" 2026-11-27, numbers at Medium above): the Collective shells thin garrisons bare anywhere.
    /// </summary>
    /// <remarks>Rooster 2026-11-27 (Dev lead after #416: the refit found half as many idle ships, the warships being out shelling; Paul: the best navy):
    /// RefitAway with it. Collective refit scraps per 32 games, MedE, main 0a5c3b4: Medium mirror 38 without Neutralise, 17 with it, 32 with
    /// RefitAway too (Hand / Collective / open 4 / 18 / 10, 3 / 24 / 5, 2 / 21 / 9); Medium Hand against the Hard Collective 16, 9, 21
    /// (1 / 24 / 7, 0 / 28 / 4, 1 / 26 / 5). Wins move within noise (about 3 of 32).</remarks>
    /// <remarks>Rooster 2026-11-27 evening (with fog a fleet in orbit sees no facilities, so the Collective shelled shields it did not know of for
    /// no hits; Paul on #412: special forces aboard fleets, used hard): ShieldsFeltDays, RaidShieldsSeen and WreckShields with it. 96 seeds, MedE,
    /// main 94bf76b, Hand / Collective / open: Medium mirror 5 / 72 / 19 off, 7 / 80 / 9 shields felt and seen, 4 / 78 / 14 with the Wreckers
    /// too; walled Medium Hand (FortifyWorlds 4, FortifyBatteries 2) 3 / 76 / 17, 2 / 83 / 11, 4 / 87 / 5. Orbit sabotage vs the walled Hand:
    /// about 10 launched a game, about 4 in 10 started succeed. After review (#426, a shelling with no hits must not show the exact shields):
    /// noting the strike's P cost most of the walled gain (6 / 79 / 11 and 4 / 78 / 14), as the batteries ground ships before P is counted; noting
    /// the shelling fleets' own power (#430) gives 4 / 79 / 13 and 4 / 83 / 9. Notes: specs/engine/ai-notes/rooster-log.md.</remarks>
    private static SimpleAiParams Neutralised(SimpleAiParams p) => p with
    {
        Neutralise = true, NeutraliseElsewherePercent = NeutraliseAnywhere, RefitAway = true,
        ShieldsFeltDays = ShieldsFeltWindow, RaidShieldsSeen = true, WreckShields = true,
        FighterWaitMax = FightersWaiting, WinWallsForgetDays = WallsForgetDays,
    };

    /// <summary>
    /// FighterWaitMax at Medium and Hard (Rooster 2026-11-28; the Collective only): no new fighter order while two squadrons wait on the ground.
    /// 96 seeds, MedE, main 81990ff, Hand / Collective / open and Collective / Hand units lost in battle: Medium mirror 4 / 79 / 13, 5390 / 6492
    /// off, 4 / 78 / 14, 4389 / 6729 on; walled Medium Hand 4 / 83 / 9, 4660 / 5960 and 5 / 81 / 10, 3208 / 4767; Hard mirror 10 / 65 / 21,
    /// 4420 / 4057 and 6 / 72 / 18, 3205 / 4006. With the 120-day window (Ponytail #432; main a43252c, 96 seeds): Medium mirror 4 / 81 / 11 ->
    /// 3 / 84 / 9, Hard mirror 8 / 71 / 17 -> 9 / 69 / 18; games where the cap held more than 200 days in a row: 82 -> 42 (Medium), 73 -> 27 (Hard).
    /// </summary>
    private const int FightersWaiting = 2;

    /// <summary>
    /// WinWallsForgetDays at Medium and Hard (Rooster 2026-11-28; the Collective only): a look at Charter's facilities older than a year reads
    /// as unknown, as FogGroundForgetDays (365) forgets an old garrison; a landing refused for the shields there makes them count again
    /// (WallsRefused, Ponytail #436). 288 seeds (1-288), MedE, main b3979ed, Hand / Collective / open, Collective win mean day, wins by day
    /// 2000, off -> on: Hard mirror 24 / 211 / 53, 1950, 121 -> 27 / 211 / 50, 1910, 133; Medium mirror 10 / 248 / 30, 1951, 146 -> 9 / 250 / 29,
    /// 1872, 167; Medium Collective vs Maverick's Grinder Hand 11 / 229 / 48, 1980, 124 -> 9 / 233 / 46, 1903, 144. Hard seed 24: the Collective
    /// held 554 of 555 worlds and never struck Charter (0 regiments, 0 shields) behind a 1225-day-old look; on, it wins on day 2127.
    /// </summary>
    private const int WallsForgetDays = 365;

    /// <summary>ShieldsFeltDays at Medium and Hard: a shelling's lesson about the shields holds four months unless a look is newer.</summary>
    private const int ShieldsFeltWindow = 120;

    /// <summary>NeutraliseElsewherePercent at Medium and Hard: a world it can shell bare is worth a plain invasion target anywhere.</summary>
    private const int NeutraliseAnywhere = 100;

    /// <summary>The level named by a word (easy, medium, normal, hard; any case), or null.</summary>
    public static AiLevel? Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "easy" => AiLevel.Easy,
        "medium" or "normal" => AiLevel.Medium,
        "hard" => AiLevel.Hard,
        _ => null,
    };
}
