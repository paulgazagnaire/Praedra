using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;

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
///   5. ControlModule    (capture; calls support and construction)
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
    public ControlModule Control { get; }

    private GameHost(Game game, GameData data, GameSettings settings)
    {
        Game = game; Data = data; Settings = settings;
        Fleets = FleetModule.Attach(game, data.Galaxy, data.Ships, data.Blockade, settings.ForcedStopTrigger);

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

        // A finished or delivered ship joins its side's spare fleet at its system (m3-fleets §New ships: the spare fleet).
        Construction.UnitAppeared += (ctx, obj, how) =>
            Fleets.OnUnitAppeared(ctx, obj, how == UnitAppearance.Delivered ? ShipJoinCause.Delivered : ShipJoinCause.Completed);

        Control = ControlModule.Attach(game, data.Galaxy, Fleets, Economy, Support, Construction);
        Control.DebugOrdersEnabled = settings.DebugOrdersEnabled;
        Fleets.DebugOrdersEnabled = settings.DebugOrdersEnabled;
    }

    /// <summary>A new, empty game (day 0, no owners, no objects) with every module attached. <paramref name="data"/> null = the repository's data.</summary>
    public static GameHost New(ulong seed, GameSettings? settings = null, GameData? data = null) =>
        new(new Game(seed), data ?? GameData.Default, settings ?? new GameSettings());

    /// <summary>A game from a save (<see cref="GameSave.Save"/>), with every module attached again. Settings are not in the save: pass them again.</summary>
    public static GameHost Load(string saveJson, GameSettings? settings = null, GameData? data = null) =>
        new(GameSave.Load(saveJson), data ?? GameData.Default, settings ?? new GameSettings());

    public string Save() => GameSave.Save(Game);
    public string StateHash() => GameSave.StateHash(Game);
}
