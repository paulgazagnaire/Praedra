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
        fm.SetOwner("Charter", Side.Hand);
        fm.SetOwner("The Shoal", Side.Hand);
        fm.SetOwner("Caminus", Side.Hand);   // Home One arrives here: Collective fleet in a Hand system
        fm.SetOwner("Tierra Larga", Side.Collective);  // the Hand picket sits here: blockade on from day 1
        fm.SetOwner("The Thaw", Side.Collective);
        fm.SetOwner("The Moorings", Side.Collective);
        fm.SetOwner("Incus", Side.Hand);

        // Moving fleets: (side, name, ships, from, to)
        Go(fm, Side.Collective, "Red", new[] { "Dodger", "Dodger" }, "Charter", "The Thaw");        // stopped at Tierra Larga
        Go(fm, Side.Hand, "Death Squadron", new[] { "Mandate", "Mandate" }, "Charter", "The Shoal");
        Go(fm, Side.Collective, "Home One", new[] { "Ark" }, "The Moorings", "Caminus");
        Go(fm, Side.Collective, "Blue", new[] { "Mender", "Mender" }, "The Thaw", "Classis");
        Go(fm, Side.Hand, "Vengeance", new[] { "Decree", "Decree", "Decree" }, "Incus", "Saltreach");
        Go(fm, Side.Collective, "Supply Run", new[] { "Mule" }, "Fyrholm", "Solacia");
        Go(fm, Side.Hand, "Tarkin's Patrol", new[] { "Injunction" }, "Oldwatch", "Eldingar");

        // Stationary: the Hand picket at Tierra Larga (an Subpoena has a gravity-well projector).
        fm.AddFleet(Side.Hand, "Picket-1", new[] { "Subpoena" }, "Tierra Larga");
        return fm;
    }

    // Fleets and their ships share one id counter, so fleet ids are not 1, 2, 3; orders go in by the id AddFleet returns.
    static void Go(FleetModule fm, Side side, string name, string[] shipClasses, string from, string to)
    {
        int id = fm.AddFleet(side, name, shipClasses, from);
        fm.SubmitMove(side, id, to);
    }
}
