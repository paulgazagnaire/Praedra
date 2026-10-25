using Praedra.Core.Assault;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Support;
using Praedra.Core.Troops;

namespace Praedra.Core.Setup;

/// <summary>
/// The composition root: the one place that loads the data, creates the <see cref="Game"/> and attaches every module in the right order
/// with their cross-wiring. A new game and a loaded game both come from here, so nobody re-implements the attach order (a save holds state
/// only; after <see cref="GameSave.Load"/> every module attaches again to the loaded game).
///
/// Attach order (it decides the order of hooks inside a slot, and what each module may read):
///   1. FleetModule      (steps 1-5; everyone reads owners and fleets from it)
///   2. EconomyModule    (6.4, 6.5, 6.7; reads fleets)
///   3. SupportModule    (6.1-6.3; reads economy and fleets; wires <see cref="EconomyModule.IsDeathStarSector"/>)
///   4. ConstructionModule (3, 4 after the fleets; 6.6; serves the refined line from the economy's event; yards stop in an uprising from support;
///      its UnitAppeared event is wired to <see cref="FleetModule.OnUnitAppeared"/>: new ships join the spare fleet)
///   5. TroopModule     (step 1 load and unload orders; reads fleets; the construction event also announces regiments and special forces)
///   6. ControlModule    (derived control: recomputed after every support change and every change in the regiments standing on a system; the flip
///      and its sector step; calls support and construction; B5a)
///   7. DefenseModule, BombardmentModule (defense queries; the Bombard order and slot 5.1; reads fleets, economy and support, calls support's ChangeSupport)
///   7b. DeathStarModule (the Destroy System order and slot 5.1 after the bombardment's hook, the sector flag at 5.6, the one-per-system build cap, the
///      DeathStarDeployed notice; reads fleets, economy, support, control, construction and defenses; its character, mission and fog hooks are wired
///      once those modules exist). rule: m5-death-star
///   8. AssaultModule (the Assault order and slot 5.2; reads troops, defenses and bombardment; the landing makes control's recompute flip the world)
///   9. BattleModule (slot 5), RepairModule (slot 6.8)
///  10. FogModule (MoveHq; slot 2 after the fleet departures, slot 3 HQ arrival after the fleet arrivals and deliveries, 3.1 sightings, 5.4 live
///      refresh, 6.8 log pruning after repair; listens to control's HolderChanged; sets the game's event audience). rule: m5-fog-and-hq §F8
///  11. ResearchModule (slot 6.9; reads economy and fleets; construction's research gate reads its levels)
///  12. CharacterModule (orders; slots 2 and 3 after the fleets, 6.10; wires bombardment's AdmiralLeadership, assault's FleetGeneralLeadership and
///      SystemGeneralLeadership, the fleet Navigator speed and fleet-removal hooks, control's HolderChanged and CharactersAtCapture, support's incident codes 3-5). rule: m5-characters-and-force
///  13. MissionModule (orders; slots 2 and 3 after the characters, 5.3 after assault and before the fog refresh; wires the characters' IsOnMission and
///      MissionMembershipEnded, the fog's AgentsAt, support's incident terms and per-incident hook). rule: m5-missions
///  14. VictoryModule (slot 6.11, the last day-end slot; must stay last; reads the characters' captured and dead states)
/// Cross-wiring done inside the Attach calls: support into economy (Death Star flag) and construction (uprising stops yards),
/// construction into economy (refined line, removed objects) and control (cancel projects on capture).
///
/// The host does not deal a starting position: it makes the Game and the modules only. Setup (owners, facilities, stockpile, the economy's
/// <see cref="EconomyModule.SetupNewGame"/>) is the caller's, M5's for the real game and <see cref="TestCampaign"/>'s for the soak test.
/// rule: engine/03-code-layout.md (modules attach to one Game); m1-clock-and-orders §Seeded random numbers rule 10.
/// </summary>
public sealed class GameHost
{
    public Game Game { get; }
    public GameData Data { get; }
    public GameSettings Settings { get; }
    public FleetModule Fleets { get; }
    public EconomyModule Economy { get; }
    public SupportModule Support { get; }
    public ConstructionModule Construction { get; }
    public TroopModule Troops { get; }
    public ControlModule Control { get; }
    /// <summary>The defense queries the assault and bombardment call. rule: m3-planetary-defenses §Queries.</summary>
    public DefenseModule Defenses { get; }
    public BombardmentModule Bombardment { get; }
    /// <summary>The Death Star: Destroy System (slot 5.1), the sector flag (5.6), the build cap. rule: m5-death-star.</summary>
    public Praedra.Core.DeathStar.DeathStarModule DeathStar { get; }
    /// <summary>The Assault order and slot 5.2. rule: m3-assault.</summary>
    public AssaultModule Assault { get; }
    /// <summary>The Classic battle, step 5 after the contacts and before slots 5.1 and 5.2. rule: m4-battle-classic.</summary>
    public Praedra.Core.Battle.BattleModule Battles { get; }
    /// <summary>Strategic ship repair, slot 6.8. rule: m4-retreat-damage-repair §5.</summary>
    public Praedra.Core.Repair.RepairModule Repair { get; }

    /// <summary>Research levels, points and the facility tick, slot 6.9. rule: m5-research.</summary>
    public Praedra.Core.Research.ResearchModule Research { get; }

    /// <summary>Victory check (slot 6.11, last; runs only in a game made by <see cref="NewGame"/>): <c>Victory.Winner</c> is null while the game runs. rule: setup-victory/04.</summary>
    public Praedra.Core.Victory.VictoryModule Victory { get; }

    /// <summary>Characters, posts, leadership, capture and death, the Force (slots 2, 3, 6.10). rule: m5-characters-and-force.</summary>
    public Praedra.Core.Characters.CharacterModule Characters { get; }
    /// <summary>What moves loyalty (B2c).</summary>
    public Praedra.Core.Characters.LoyaltyModule Loyalty { get; }

    /// <summary>Agent missions: teams, decoys, detection, success rolls and effects (slots 2, 3, 5.3). rule: m5-missions.</summary>
    public Praedra.Core.Missions.MissionModule Missions { get; }

    /// <summary>Fog of war (each side's picture, the read API for the AI and UI) and the Hearth. rule: m5-fog-and-hq.</summary>
    public Praedra.Core.Fog.FogModule Fog { get; }

    /// <summary>The Hearth system (where it stands, or stood when destroyed), null in transit or if none. rule: m5-fog-and-hq §F5.</summary>
    public string? HqSystem => Game.State.Scenario.HqSystem;

    /// <summary>Is the system explored in the side's picture (dealt at setup, then widened by fleets, holdings and reveals)? rule: m5-fog-and-hq §F1.</summary>
    public bool IsExplored(string system, Praedra.Core.Clock.Side side) => Fog.IsExplored(side, system);

    /// <summary>
    /// The move preview for one of the side's fleets: the path, hops, arrival day and the known traps on the way, from the side's own
    /// picture (never the truth). The MoveFleet order with the same waypoints flies exactly this path. rule: m1-travel §Path rule (3), (5);
    /// m5-fog-and-hq §N8 (what a side may read). A trap = a system passed on the way whose picture shows an enemy fleet with a gravity-well
    /// ship, however old the look (the age is given; DEFAULT, the sheet names no cut-off). Strength blockades are not traps: only gravity-well
    /// ships pull fleets out of hyperspace (m1-forced-stop-and-blockade, Paul 2026-10-16).
    /// </summary>
    public MovePlan PlanMove(Praedra.Core.Clock.Side side, int fleetId, string destination, IReadOnlyList<string>? waypoints = null)
    {
        var none = Array.Empty<string>();
        if (!Game.State.Fleets.TryGetValue(fleetId, out var fleet) || fleet.Side != side)
            return new MovePlan(Praedra.Core.Travel.PathStatus.UnknownSystem, none, Array.Empty<int>(), Array.Empty<bool>(), 0, Array.Empty<KnownTrap>());
        var path = Fleets.PlanPath(fleet, destination, waypoints);
        if (!path.Ok) return new MovePlan(path.Status, none, Array.Empty<int>(), Array.Empty<bool>(), 0, Array.Empty<KnownTrap>());
        var names = path.Names(Fleets.Galaxy);
        var route = new List<bool>();
        for (int i = 0; i + 1 < names.Count; i++)
            route.Add(Fleets.Travel != Praedra.Core.Travel.TravelMode.Straight && Fleets.Galaxy.FindLane(names[i], names[i + 1]) != null);
        var view = Fog.ViewFor(side);
        var traps = new List<KnownTrap>();
        for (int i = 1; i + 1 < names.Count; i++)
        {
            var seen = view.Picture(names[i])?.Fleets;
            if (seen == null) continue;
            bool gravity = seen.Value.Any(fp => fp.Side != side && fp.Ships.Any(kv => kv.Value > 0
                && Fleets.ShipClasses.TryGetValue(kv.Key, out var c) && c.GravityWellProjector > 0));
            if (gravity) traps.Add(new KnownTrap(names[i], seen.Stamp));
        }
        return new MovePlan(path.Status, names, path.HopTicks, route, Game.Day + path.TotalTicks, traps);
    }

    private GameHost(Game game, GameData data, GameSettings settings)
    {
        Game = game; Data = data; Settings = settings;
        Fleets = FleetModule.Attach(game, data.Galaxy, data.Ships, data.Blockade, settings.ForcedStopTrigger, data.NewBattle, settings.Travel);

        Economy = EconomyModule.Attach(game, data.Galaxy, data.Slots, data.Costs, Fleets, settings.Economy);
        Economy.IncidentsEnabled = settings.IncidentsEnabled;
        Economy.BlockadeStopsMining = settings.BlockadeStopsMining;
        Economy.AutoscrapMode = settings.AutoscrapMode;
        Economy.UprisingStopsMining = settings.UprisingStopsMining;
        Economy.SuspendedLeaveCapacity = settings.SuspendedLeaveCapacity;
        Economy.MineStopMode = settings.MineStopMode;
        Economy.AutoscrapTimer = settings.AutoscrapTimer;
        Economy.SlotSource = settings.SlotSource;

        Support = SupportModule.Attach(game, data.Galaxy, Economy, Fleets, data.Weights, settings.Support);
        Support.Ruleset = settings.SupportRuleset;
        Support.BlockadeSupportEffect = settings.BlockadeSupportEffect;
        Economy.IsInUprising = Support.IsUprising; // an uprising stops mines and refineries (B5b §3)

        Construction = ConstructionModule.Attach(game, Fleets, Economy, settings.Construction, Support);
        Construction.DeliveryForcedStop = settings.DeliveryForcedStop;
        Construction.LostDestination = settings.LostDestination;
        Construction.UnitDestinationMustBeControlled = settings.UnitDestinationMustBeControlled;
        Construction.RespectResearchOrder = settings.RespectResearchOrder;
        Construction.DeathStarClassBuildable = settings.DeathStarClassBuildable;
        Construction.UseClassicBlockadeFlag = settings.UseClassicBlockadeFlag;
        Construction.YardStopMode = settings.YardStopMode;
        Construction.RefuseOverdrawOrders = settings.RefuseOverdrawOrders;
        Construction.NewOrderReplacesProject = settings.NewOrderReplacesProject;

        Troops = TroopModule.Attach(game, Fleets, data.Troops);
        Troops.UnloadOnlyAtHeldSystem = settings.UnloadOnlyAtHeldSystem;

        // A finished or delivered ship joins its side's spare fleet at its system (m3-fleets §New ships: the spare fleet); a finished or
        // delivered regiment or special force is announced (m3-troops-and-special-forces §Where it runs).
        Construction.UnitAppeared += (ctx, obj, how) =>
        {
            Fleets.OnUnitAppeared(ctx, obj, how == UnitAppearance.Delivered ? ShipJoinCause.Delivered : ShipJoinCause.Completed);
            Troops.OnUnitAppeared(ctx, obj);
        };

        Control = ControlModule.Attach(game, data.Galaxy, Fleets, Economy, Support, Construction);
        Control.Rule = settings.ControlRule;
        Control.DebugOrdersEnabled = settings.DebugOrdersEnabled;
        Fleets.DebugOrdersEnabled = settings.DebugOrdersEnabled;

        Defenses = DefenseModule.Attach(Economy, Fleets, data.Defenses, settings.Defense);
        Bombardment = BombardmentModule.Attach(game, Economy, Fleets, Support, Defenses, settings.Bombard);
        // After the bombardment (its slot 5.1 hook runs first) and before the assault. rule: m5-death-star §2 ("resolved in slot 5.1 after the bombardment hook")
        DeathStar = Praedra.Core.DeathStar.DeathStarModule.Attach(game, data.Galaxy, Fleets, Economy, Support, Control, Construction, Defenses, settings.DeathStar);
        Construction.UnitAppeared += DeathStar.OnUnitAppeared; // after the fleet module's handler (the ship has joined its fleet): DeathStarDeployed
        Assault = AssaultModule.Attach(game, Economy, Fleets, Troops, Control, Defenses, Bombardment, settings.Assault);
        // Slot 5: registered after the fleet module's contact hook, so it runs after it, and (slot 5 precedes 5.1 and 5.2) before bombardment and assault.
        Battles = Praedra.Core.Battle.BattleModule.Attach(game, data.Galaxy, Fleets, Economy, settings.Battle, data.NewBattle);
        Repair = Praedra.Core.Repair.RepairModule.Attach(game, Economy, Fleets, data.Repair); // slot 6.8
        Repair.YardSuspended = Construction.IsSuspendedAt; // a blockade or an uprising drops FastRepair. rule: m4-retreat-damage-repair §5
        Fog = Praedra.Core.Fog.FogModule.Attach(game, data.Galaxy, Fleets, Economy, Support, Control, settings.Fog, settings.Hq); // after repair (shares 6.8)
        Research = Praedra.Core.Research.ResearchModule.Attach(game, Economy, Fleets, data.Research, settings.Research); // slot 6.9, after repair
        Construction.ResearchOrderReached = Research.OrderReached; // the research gate reads the side's levels. rule: m5-research §Availability test
        // Characters: slots 2 and 3 after the fleets, 6.10 after research. The three M3 leadership hooks, the Navigator speed, the system General
        // cleared on a holder change and the uprising incident codes 3 to 5. rule: m5-characters-and-force §Where it runs in the day Step.
        Characters = Praedra.Core.Characters.CharacterModule.Attach(game, Fleets, data.Characters, settings.Characters);
        Bombardment.AdmiralLeadership = Characters.AdmiralLeadership;
        // rule: B7-bombardment §4.3: the holder's General standing on the system multiplies the batteries' return fire (the system General post is
        // cleared on a holder change, so its holder is the system's)
        Bombardment.BatteryGeneralLeadership = Characters.SystemGeneralLeadership;
        Bombardment.IsHearth = s => s == HqSystem; // read only with HqBombardable (B7 C9, DEFAULT off)
        Assault.FleetGeneralLeadership = Characters.FleetGeneralLeadership;
        Assault.SystemGeneralLeadership = Characters.SystemGeneralLeadership;
        Control.HolderChanged += Characters.OnHolderChanged;
        Control.CharactersAtCapture = Characters.OnCapture; // prisoners freed, characters leave (B5a section 5)
        Support.CharacterIncident = Characters.UprisingIncident;
        // What moves loyalty: the four triggers react to their events as they are emitted. rule: m5-characters-and-force §Loyalty and betrayal (B2c)
        Loyalty = Praedra.Core.Characters.LoyaltyModule.Attach(game, Characters, Fleets, Economy, data.Troops);
        // Missions: orders, slots 2 and 3 after the characters, 5.3; wires the characters' IsOnMission and membership end, the fog's AgentsAt and the
        // support module's incident terms. rule: m5-missions §Where it runs in the day Step.
        Missions = Praedra.Core.Missions.MissionModule.Attach(game, Fleets, Economy, Support, Troops, Research, Characters, Fog, data.Missions, settings.Missions);
        Missions.FacilityFamily = cls => data.Defenses.TryGetClass(cls, out var dc) ? dc.Family : 0; // rule: m5-missions §3 (B2d: the Mordant Lien-150 test)
        // The escape attempt's inputs: ESCAPETB from mission-tables.csv (unless the settings give one) and the captor's special forces on the planet.
        // rule: m5-characters-and-force §Escape (B2e §3)
        if (Characters.EscapeTable == null && data.Missions.Escape is { } esc) Characters.EscapeTable = esc.Select(r => (r.Threshold, r.Value)).ToList();
        Characters.SpecialForcesOnPlanet = Missions.SpecialForcesOnPlanet;
        Missions.Recruit = Characters.Recruit; // rule: m5-missions §6 Recruitment (Recruitment pick, 2026-10-18)
        // The Death Star's hooks into the characters, missions and fog (they attach after it). rule: m5-death-star §2 effects 3, 4, 6; §5
        DeathStar.MissionsEnded = Missions.OnSystemDestroyed;
        DeathStar.CharacterRoll = Characters.DeathStarStrike;
        DeathStar.CharacterRelocate = Characters.DeathStarRelocate;
        DeathStar.FogNotice = Fog.OnSystemDestroyed;
        Missions.HasDeathStar = DeathStar.HasDeathStar;
        Missions.DestroyDeathStar = DeathStar.Destroy;
        Victory = Praedra.Core.Victory.VictoryModule.Attach(game, settings.Victory); // slot 6.11, the last day-end slot; must stay last
        Victory.Keys = data.Characters.VictoryKeys;
        Victory.IsCaptured = Characters.IsCaptured;
        Victory.IsDead = Characters.IsDead;
        Characters.DeathRefused = Victory.RefusesDeath;
    }

    /// <summary>A new, empty game (day 0, no owners, no objects) with every module attached. <paramref name="data"/> null = the repository's data.</summary>
    public static GameHost New(ulong seed, GameSettings? settings = null, GameData? data = null) =>
        new(new Game(seed), data ?? GameData.Default, settings ?? new GameSettings());

    /// <summary>A new, empty game on a given generator (tests pass a stub that returns queued draws).</summary>
    public static GameHost New(GameRng rng, GameSettings? settings = null, GameData? data = null) =>
        new(new Game(rng), data ?? GameData.Default, settings ?? new GameSettings());

    /// <summary>
    /// A game from a save (<see cref="GameSave.Save"/> or <see cref="SaveWithSettings"/>), with every module attached again. Settings are not in the
    /// plain save: pass them again. A save written by <see cref="SaveWithSettings"/> carries every game setting (QA-17); they are used when
    /// <paramref name="settings"/> is null, and passed settings replace them whole. It also carries the battle ruleset; it is applied when
    /// <paramref name="settings"/> does not already set <see cref="GameSettings.Battle"/>. A save without the header loads with the DEFAULT ruleset (New since 2026-10-18).
    /// It may also carry the AI memory line (<see cref="AiMemoryFor"/>); a save without it loads with an empty memory.
    /// rule: m4-battle-new §Rule (New) (ruleset switch, DEFAULT New, Paul 2026-10-18)
    /// </summary>
    public static GameHost Load(string saveJson, GameSettings? settings = null, GameData? data = null)
    {
        var (ruleset, afterHeader) = ReadHeader(saveJson);
        data ??= GameData.Default;
        var (saved, body) = ReadSettings(afterHeader, data.Names);
        settings ??= saved ?? new GameSettings();
        if (ruleset != null && settings.Battle == null)
            settings = settings with { Battle = new Praedra.Core.Battle.BattleParams { Ruleset = ruleset.Value } };
        var (memories, core) = ReadAiMemory(body, data.Names);
        var host = new GameHost(GameSave.Load(data.Names.Apply(core)), data, settings); // a save from before the Praedra names loads renamed
        host._aiMemory.AddRange(memories);
        return host;
    }

    // ------------------------------------------------------------------ the AI's memory (QA-1)

    private readonly List<Praedra.Core.AI.AiMemory> _aiMemory = new(); // one per (side, fog), found by search, saved sorted

    /// <summary>
    /// The simple AI's memory of its side's event feed for this game (one per side and fog setting; made on first use). It lives with the game,
    /// not with an AI object, so a new AI on this game, or on a game loaded from <see cref="SaveWithSettings"/>, reads the same notes.
    /// </summary>
    public Praedra.Core.AI.AiMemory AiMemoryFor(Side side, bool fog)
    {
        foreach (var m in _aiMemory) if (m.Side == side && m.Fog == fog) return m;
        var made = new Praedra.Core.AI.AiMemory { Side = side, Fog = fog };
        _aiMemory.Add(made);
        return made;
    }

    private const string AiMemoryTag = "AI-MEMORY ";
    private static readonly System.Text.Json.JsonSerializerOptions AiMemoryJson = new()
    {
        WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    // The AI memory line: "AI-MEMORY [json]" between the header and the core save, only when an AI has played this game. A save without it
    // (older saves, games without an AI) loads with an empty memory.
    private string AiMemoryLine()
    {
        if (_aiMemory.Count == 0) return "";
        foreach (var m in _aiMemory) m.CatchUp?.Invoke(); // the last Step's events, not yet noted by the AI (AiMemory.CatchUp)
        var list = _aiMemory.OrderBy(m => m.Side).ThenBy(m => m.Fog).Select(m => m.ForSave(Game.Day, Fog.GroundStamp)).ToList();
        return AiMemoryTag + System.Text.Json.JsonSerializer.Serialize(list, AiMemoryJson) + "\n";
    }

    private static (List<Praedra.Core.AI.AiMemory> Memories, string Core) ReadAiMemory(string body, NameMigrations names)
    {
        if (!body.StartsWith(AiMemoryTag, StringComparison.Ordinal)) return (new(), body);
        int nl = body.IndexOf('\n');
        if (nl < 0) throw new InvalidDataException("AI memory line without a game");
        var list = System.Text.Json.JsonSerializer.Deserialize<List<Praedra.Core.AI.AiMemory>>(names.Apply(body[AiMemoryTag.Length..nl]), AiMemoryJson)
            ?? throw new InvalidDataException("empty AI memory");
        return (list, body[(nl + 1)..]);
    }

    public string Save() => GameSave.Save(Game);

    private const string HeaderTag = "PRAEDRA-CORE 1 battle=";
    private const string OldHeaderTag = "REBELLION-CORE 1 battle="; // saves written before the Praedra rename (2026-10-19) still load

    /// <summary>
    /// The save plus a first line naming the battle ruleset ("PRAEDRA-CORE 1 battle=Classic|New"), so a loaded game fights by the same rules,
    /// then a line with every game setting ("SETTINGS {json}", QA-17), then, when an AI has played this game, a line with the AI's memory
    /// ("AI-MEMORY [json]"), so a loaded game's AI plans as before (QA-1). The game state itself is unchanged (<see cref="StateHash"/> includes
    /// none of these lines). rule: m4-battle-new §Rule (New); m1-clock-and-orders K12, K16 (a loaded game continues as before)
    /// </summary>
    public string SaveWithSettings() =>
        HeaderTag + (Settings.Battle ?? new Praedra.Core.Battle.BattleParams()).Ruleset + "\n"
        + SettingsTag + System.Text.Json.JsonSerializer.Serialize(Pinned(Settings), SettingsJson) + "\n" + AiMemoryLine() + Save();

    // ------------------------------------------------------------------ the settings line (QA-17)

    private const string SettingsTag = "SETTINGS ";

    // Enums by name (a reordered enum cannot change an old save's meaning); value tuples need fields; the delegates the host wires at
    // attach (leadership queries, victory lookups) are not settings and are left out.
    private static readonly System.Text.Json.JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = false, IncludeFields = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                ti =>
                {
                    for (int i = ti.Properties.Count - 1; i >= 0; i--)
                        if (typeof(Delegate).IsAssignableFrom(ti.Properties[i].PropertyType)) ti.Properties.RemoveAt(i);
                },
            },
        },
    };

    /// <summary>
    /// The settings with every null number table replaced by the module's own default (what attach does with a null), so the save holds the
    /// values this game was played with: a DEFAULT changed after the save does not change the loaded game.
    /// </summary>
    internal static GameSettings Pinned(GameSettings s) => s with
    {
        Deal = s.Deal ?? new DealParams(),
        Economy = s.Economy ?? new EconomyParams(),
        Support = s.Support ?? new SupportParams(),
        Construction = s.Construction ?? new ConstructionParams(),
        Defense = s.Defense ?? new DefenseParams(),
        Bombard = s.Bombard ?? new BombardParams(),
        DeathStar = s.DeathStar ?? new Praedra.Core.DeathStar.DeathStarParams(),
        Assault = s.Assault ?? new AssaultParams(),
        Battle = s.Battle ?? new Praedra.Core.Battle.BattleParams(),
        Research = s.Research ?? new Praedra.Core.Research.ResearchParams(),
        Characters = s.Characters ?? new Praedra.Core.Characters.CharacterParams(),
        Missions = s.Missions ?? new Praedra.Core.Missions.MissionParams(),
        Victory = s.Victory ?? new Praedra.Core.Victory.VictoryRules(),
        Fog = s.Fog ?? new Praedra.Core.Fog.FogParams(),
        Hq = s.Hq ?? new Praedra.Core.Fog.HqParams(),
    };

    private static (GameSettings? Settings, string Body) ReadSettings(string body, NameMigrations names)
    {
        if (!body.StartsWith(SettingsTag, StringComparison.Ordinal)) return (null, body); // saves made before QA-17: the caller's or the DEFAULT settings
        int nl = body.IndexOf('\n');
        if (nl < 0) throw new InvalidDataException("settings line without a game");
        // QA-41: settings name systems and units too (Fog.CapitalSystem, Support.StormtrooperClass...), so a save from before the Praedra names
        // gets its settings renamed like its state
        var s = System.Text.Json.JsonSerializer.Deserialize<GameSettings>(names.Apply(UpgradeSettingsJson(body[SettingsTag.Length..nl])), SettingsJson)
            ?? throw new InvalidDataException("empty settings line");
        return (s, body[(nl + 1)..]);
    }

    // Saves written before the Praedra rename (2026-10-19, QA-24) name the Classic values "Original": map them to today's names. The JSON is
    // compact (WriteIndented false), so each old token is exact.
    private static readonly (string Old, string New)[] RenamedSettings =
    {
        ("\"BattleRng\":\"Original\"", "\"BattleRng\":\"Classic\""),
        ("\"TaskForces\":\"Original\"", "\"TaskForces\":\"Classic\""),
        ("\"StartCastOfficers\":\"Original\"", "\"StartCastOfficers\":\"Classic\""),
        ("\"KeyDuelCapture\":\"Original\"", "\"KeyDuelCapture\":\"Classic\""),
        ("\"KeyTargetGuards\":\"Original\"", "\"KeyTargetGuards\":\"Classic\""),
        ("\"OriginalRngSeed\":", "\"ClassicRngSeed\":"),
        ("\"OriginalRngWarmup\":", "\"ClassicRngWarmup\":"),
    };

    private static string UpgradeSettingsJson(string json)
    {
        foreach (var (o, n) in RenamedSettings) json = json.Replace(o, n, StringComparison.Ordinal);
        return json;
    }

    private static (Praedra.Core.Battle.BattleRuleset? Ruleset, string Body) ReadHeader(string text)
    {
        string? tag = text.StartsWith(HeaderTag, StringComparison.Ordinal) ? HeaderTag
            : text.StartsWith(OldHeaderTag, StringComparison.Ordinal) ? OldHeaderTag : null;
        if (tag == null) return (null, text);
        int nl = text.IndexOf('\n');
        if (nl < 0) throw new InvalidDataException("save header without a body");
        string name = text[tag.Length..nl].Trim();
        if (!Enum.TryParse(name, false, out Praedra.Core.Battle.BattleRuleset r) || !Enum.IsDefined(r))
            throw new InvalidDataException("unknown battle ruleset in save: " + name);
        return (r, text[(nl + 1)..]);
    }
    public string StateHash() => GameSave.StateHash(Game);
}
