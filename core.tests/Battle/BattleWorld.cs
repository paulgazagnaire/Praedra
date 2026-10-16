using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Tests.Economy;
using Rebellion.Core.Tests.Fleets;

namespace Rebellion.Core.Tests.Battle;

/// <summary>
/// Fleets, economy and the battle module on the synthetic map of the retreat sheet: A(0,0), B(100,0), C(200,0), D(100,60); lanes A-B, B-C, B-D
/// (lengths 100, 100, 60). Real ship files. Rolls off unless asked.
/// </summary>
internal sealed class BattleWorld
{
    public Game Game { get; }
    public FleetModule Fleets { get; }
    public EconomyModule Eco { get; }
    public BattleModule Battles { get; }
    public Galaxy Galaxy { get; }

    public BattleWorld(BattleParams? p = null, ulong seed = 1, Galaxy? galaxy = null)
    {
        Galaxy = galaxy ?? Synthetic();
        Game = new Game(seed);
        var rows = "name,sector,group,energy,raw,populated\n" + string.Join("\n", Galaxy.Systems.Select(s => $"{s.Name},{s.Sector},{s.Group},1,1,1"));
        var slots = SlotValues.Parse(rows, Galaxy);
        Fleets = FleetModule.Attach(Game, Galaxy, World.Ships.Value, World.Blockade, travel: World.TravelFor(Galaxy));
        Eco = EconomyModule.Attach(Game, Galaxy, slots, EcoWorld.FreeShipCosts.Value, Fleets);
        Eco.IncidentsEnabled = false;
        Battles = BattleModule.Attach(Game, Galaxy, Fleets, Eco, p ?? new BattleParams { SubsystemRolls = false });
    }

    public static Galaxy Synthetic() => TestData.Synthetic(
        new[] { ("A", 0, 0), ("B", 100, 0), ("C", 200, 0), ("D", 100, 60) },
        new[] { ("A", "B"), ("B", "C"), ("B", "D") });

    public int Fleet(Side side, string system, string ship, int n) =>
        Fleets.AddFleet(side, $"{side}{Game.State.Fleets.Count}", Enumerable.Repeat(ship, n), system);

    public void Run(int n = 1) { for (int i = 0; i < n; i++) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();
    public Fleet F(int id) => Game.State.Fleets[id];
    public EconObject Obj(int id) => Game.State.Economy.Objects[id];
}
