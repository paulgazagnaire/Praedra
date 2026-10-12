using Rebellion.Core.Assault;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;
using Rebellion.Core.Troops;

namespace Rebellion.Core.Setup;

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
///   6. ControlModule    (capture; calls support and construction)
///   7. DefenseModule, BombardmentModule (defense queries; the Bombard order and slot 5.1; reads fleets, economy and support, calls support's ChangeSupport)
///   8. AssaultModule (the Assault order and slot 5.2; reads troops, defenses and bombardment, calls control's SetHolder)
///   9. BattleModule (slot 5), RepairModule (slot 6.8)
///  10. FogModule (MoveHq; slot 2 after the fleet departures, slot 3 HQ arrival after the fleet arrivals and deliveries, 3.1 sightings, 5.4 live
///      refresh, 6.8 log pruning after repair; listens to control's HolderChanged; sets the game's event audience). rule: m5-fog-and-hq §F8
///  11. ResearchModule (slot 6.9; reads economy and fleets; construction's research gate reads its levels)
///  12. CharacterModule (orders; slots 2 and 3 after the fleets, 6.10; wires bombardment's AdmiralLeadership, assault's FleetGeneralLeadership and
///      SystemGeneralLeadership, the fleet Navigator speed and fleet-removal hooks, control's HolderChanged, support's incident codes 3-5). rule: m5-characters-and-force
///  13. VictoryModule (slot 6.11, the last day-end slot; must stay last; reads the characters' captured and dead states)
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
    /// <summary>The Assault order and slot 5.2. rule: m3-assault.</summary>
    public AssaultModule Assault { get; }
    /// <summary>The Classic battle, step 5 after the contacts and before slots 5.1 and 5.2. rule: m4-battle-classic.</summary>
    public Rebellion.Core.Battle.BattleModule Battles { get; }
    /// <summary>Strategic ship repair, slot 6.8. rule: m4-retreat-damage-repair §5.</summary>
    public Rebellion.Core.Repair.RepairModule Repair { get; }

    /// <summary>Research levels, points and the facility tick, slot 6.9. rule: m5-research.</summary>
    public Rebellion.Core.Research.ResearchModule Research { get; }

    /// <summary>Victory check (slot 6.11, last; runs only in a game made by <see cref="NewGame"/>): <c>Victory.Winner</c> is null while the game runs. rule: setup-victory/04.</summary>
    public Rebellion.Core.Victory.VictoryModule Victory { get; }

    /// <summary>Characters, posts, leadership, capture and death, the Force (slots 2, 3, 6.10). rule: m5-characters-and-force.</summary>
    public Rebellion.Core.Characters.CharacterModule Characters { get; }

    /// <summary>Fog of war (each side's picture, the read API for the AI and UI) and the Rebel HQ. rule: m5-fog-and-hq.</summary>
    public Rebellion.Core.Fog.FogModule Fog { get; }

    /// <summary>The Rebel HQ system (where it stands, or stood when destroyed), null in transit or if none. rule: m5-fog-and-hq §F5.</summary>
    public string? HqSystem => Game.State.Scenario.HqSystem;

    /// <summary>Is the system explored in the side's picture (dealt at setup, then widened by fleets, holdings and reveals)? rule: m5-fog-and-hq §F1.</summary>
    public bool IsExplored(string system, Rebellion.Core.Clock.Side side) => Fog.IsExplored(side, system);

    private GameHost(Game game, GameData data, GameSettings settings)
    {
        Game = game; Data = data; Settings = settings;
        Fleets = FleetModule.Attach(game, data.Galaxy, data.Ships, data.Blockade, settings.ForcedStopTrigger, data.NewBattle);

        Economy = EconomyModule.Attach(game, data.Galaxy, data.Slots, data.Costs, Fleets, settings.Economy);
        Economy.IncidentsEnabled = settings.IncidentsEnabled;
        Economy.BlockadeStopsMining = settings.BlockadeStopsMining;
        Economy.AutoscrapMode = settings.AutoscrapMode;
        Economy.SlotSource = settings.SlotSource;

        Support = SupportModule.Attach(game, data.Galaxy, Economy, Fleets, data.Weights, settings.Support);
        Support.Ruleset = settings.SupportRuleset;
        Support.BlockadeSupportEffect = settings.BlockadeSupportEffect;

        Construction = ConstructionModule.Attach(game, Fleets, Economy, settings.Construction, Support);
        Construction.DeliveryForcedStop = settings.DeliveryForcedStop;
        Construction.LostDestination = settings.LostDestination;
        Construction.UnitDestinationMustBeControlled = settings.UnitDestinationMustBeControlled;
        Construction.RespectResearchOrder = settings.RespectResearchOrder;
        Construction.DeathStarClassBuildable = settings.DeathStarClassBuildable;
        Construction.UseClassicBlockadeFlag = settings.UseClassicBlockadeFlag;

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
        Control.DebugOrdersEnabled = settings.DebugOrdersEnabled;
        Fleets.DebugOrdersEnabled = settings.DebugOrdersEnabled;

        Defenses = DefenseModule.Attach(Economy, Fleets, data.Defenses, settings.Defense);
        Bombardment = BombardmentModule.Attach(game, Economy, Fleets, Support, Defenses, settings.Bombard);
        Assault = AssaultModule.Attach(game, Economy, Fleets, Troops, Control, Defenses, Bombardment, settings.Assault);
        // Slot 5: registered after the fleet module's contact hook, so it runs after it, and (slot 5 precedes 5.1 and 5.2) before bombardment and assault.
        Battles = Rebellion.Core.Battle.BattleModule.Attach(game, data.Galaxy, Fleets, Economy, settings.Battle, data.NewBattle);
        Repair = Rebellion.Core.Repair.RepairModule.Attach(game, Economy, Fleets, data.Repair); // slot 6.8
        Repair.FoughtThisStep = Battles.FoughtThisStep; // no repair on a battle day. rule: m4-retreat-damage-repair §5
        Fog = Rebellion.Core.Fog.FogModule.Attach(game, data.Galaxy, Fleets, Economy, Support, Control, settings.Fog, settings.Hq); // after repair (shares 6.8)
        Research = Rebellion.Core.Research.ResearchModule.Attach(game, Economy, Fleets, data.Research, settings.Research); // slot 6.9, after repair
        Construction.ResearchOrderReached = Research.OrderReached; // the research gate reads the side's levels. rule: m5-research §Availability test
        // Characters: slots 2 and 3 after the fleets, 6.10 after research. The three M3 leadership hooks, the Navigator speed, the system General
        // cleared on a holder change and the uprising incident codes 3 to 5. rule: m5-characters-and-force §Where it runs in the day Step.
        Characters = Rebellion.Core.Characters.CharacterModule.Attach(game, Fleets, data.Characters, settings.Characters);
        Bombardment.AdmiralLeadership = Characters.AdmiralLeadership;
        Assault.FleetGeneralLeadership = Characters.FleetGeneralLeadership;
        Assault.SystemGeneralLeadership = Characters.SystemGeneralLeadership;
        Control.HolderChanged += Characters.OnHolderChanged;
        Support.CharacterIncident = Characters.UprisingIncident;
        Victory = Rebellion.Core.Victory.VictoryModule.Attach(game, settings.Victory); // slot 6.11, the last day-end slot; must stay last
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

    /// <summary>A game from a save (<see cref="GameSave.Save"/>), with every module attached again. Settings are not in the save: pass them again.</summary>
    public static GameHost Load(string saveJson, GameSettings? settings = null, GameData? data = null) =>
        new(GameSave.Load(saveJson), data ?? GameData.Default, settings ?? new GameSettings());

    public string Save() => GameSave.Save(Game);
    public string StateHash() => GameSave.StateHash(Game);
}
