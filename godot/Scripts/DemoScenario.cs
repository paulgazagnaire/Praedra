using System.Collections.Generic;
using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using Praedra.Core.Map;
using Praedra.Core.Ships;

namespace PraedraViewer;

/// <summary>
/// DEMO ONLY. A made-up scenario so the viewer has something to show: not a rules source, not the real start of a game.
/// It only uses the core's public API (AddFleet, SetOwner, SubmitMove); the core does all the moving.
/// Press R in the viewer to restart it.
/// </summary>
public static class DemoScenario
{
    public static FleetModule Build(Game game, Galaxy galaxy, IReadOnlyDictionary<string, ShipClass> ships, BlockadeData blockade)
    {
        var fm = FleetModule.Attach(game, galaxy, ships, blockade, ForcedStopTrigger.PaulRule);

        // Who owns what (so a blockade flag can fire: an enemy fleet sitting in an owned system).
        fm.SetOwner("Coruscant", Side.Empire);
        fm.SetOwner("Yavin", Side.Empire);
        fm.SetOwner("Sullust", Side.Empire);   // Home One arrives here: Rebel fleet in an Imperial system
        fm.SetOwner("Kinyen", Side.Alliance);  // the Imperial picket sits here: blockade on from day 1
        fm.SetOwner("Hoth", Side.Alliance);
        fm.SetOwner("Mon Calamari", Side.Alliance);
        fm.SetOwner("Fondor", Side.Empire);

        // Moving fleets: (side, name, ships, from, to)
        Go(fm, Side.Alliance, "Red", new[] { "Corellian Corvette", "Corellian Corvette" }, "Coruscant", "Hoth");        // stopped at Kinyen
        Go(fm, Side.Empire, "Death Squadron", new[] { "Imperial Star Destroyer", "Imperial Star Destroyer" }, "Coruscant", "Yavin");
        Go(fm, Side.Alliance, "Home One", new[] { "Mon Calamari Cruiser" }, "Mon Calamari", "Sullust");
        Go(fm, Side.Alliance, "Blue", new[] { "Nebulon-B Frigate", "Nebulon-B Frigate" }, "Hoth", "Corellia");
        Go(fm, Side.Empire, "Vengeance", new[] { "Victory Destroyer", "Victory Destroyer", "Victory Destroyer" }, "Fondor", "Utapau");
        Go(fm, Side.Alliance, "Supply Run", new[] { "Medium Transport" }, "Endor", "Naboo");
        Go(fm, Side.Empire, "Tarkin's Patrol", new[] { "Strike Cruiser" }, "Bastion", "Dagobah");

        // Stationary: the Imperial picket at Kinyen (an Interdictor Cruiser has a gravity-well projector).
        fm.AddFleet(Side.Empire, "Picket-1", new[] { "Interdictor Cruiser" }, "Kinyen");
        return fm;
    }

    // Fleets and their ships share one id counter, so fleet ids are not 1, 2, 3; orders go in by the id AddFleet returns.
    static void Go(FleetModule fm, Side side, string name, string[] shipClasses, string from, string to)
    {
        int id = fm.AddFleet(side, name, shipClasses, from);
        fm.SubmitMove(side, id, to);
    }
}
