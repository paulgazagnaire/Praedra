using System.Text.Json;
using Rebellion.Core.Clock;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;
using Rebellion.Core.Travel;

namespace Rebellion.Core.Tests.Fleets;

/// <summary>A game with the fleet module attached, plus short helpers for the fleet tests.</summary>
internal sealed class World
{
    public static readonly Lazy<IReadOnlyDictionary<string, ShipClass>> Ships = new(() => ShipData.LoadAll(TestData.InterimDir));
    public static BlockadeData Blockade => BlockadeData.Load(Path.Combine(TestData.DesignDir, BlockadeData.FileName));

    public Game Game { get; }
    public FleetModule Fleets { get; }

    public World(Galaxy galaxy, ForcedStopTrigger trigger = ForcedStopTrigger.PaulRule, ulong seed = 1)
        : this(new Game(seed), galaxy, trigger) { }

    public World(Game game, Galaxy galaxy, ForcedStopTrigger trigger = ForcedStopTrigger.PaulRule)
    {
        Game = game;
        Fleets = FleetModule.Attach(game, galaxy, Ships.Value, Blockade, trigger, travel: TravelFor(galaxy));
    }

    /// <summary>The real galaxy flies the game's travel (Routes); the small hand-made maps keep the 2026-10-12 lane travel (TravelMode.Lanes)
    /// so their checks of passing, stops and contacts at the middle system still have a middle system (m1-travel §Switch).</summary>
    public static TravelMode TravelFor(Galaxy galaxy) => ReferenceEquals(galaxy, TestData.RealGalaxy) ? TravelMode.Routes : TravelMode.Lanes;

    /// <summary>Synthetic map of the forced-stop sheet: A(0,0), B(100,0), C(200,0), tier 3 lanes A-B and B-C; D(100,100) with lane B-D added for B6.</summary>
    public static Galaxy BCMap() => TestData.Synthetic(
        new[] { ("A", 0, 0), ("B", 100, 0), ("C", 200, 0), ("D", 100, 100) },
        new[] { ("A", "B"), ("B", "C"), ("B", "D") });

    public int Add(Side side, string name, string system, params string[] ships) => Fleets.AddFleet(side, name, ships, system);
    public int Move(Side side, int fleet, string to) { Fleets.SubmitMove(side, fleet, to); return fleet; }
    public void Run(int steps) { for (int i = 0; i < steps; i++) Game.Step(); }
    public Fleet Fleet(int id) => Game.State.Fleets[id];
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();
    public List<T> Events<T>(Func<T, bool> where) where T : GameEvent => Game.Events.OfType<T>().Where(where).ToList();

    /// <summary>Step until Game.Day reaches the day.</summary>
    public void RunTo(int day) { while (Game.Day < day) Game.Step(); }

    public static string StateJson(Game g) => JsonSerializer.Serialize(g.State);
}
