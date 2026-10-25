using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using Praedra.Core.Map;
using Praedra.Core.Ships;
using Praedra.Core.Support;

namespace Praedra.Core.Economy;

/// <summary>
/// A small deterministic economy on the real galaxy, for the headless runner and the soak tests: four owned systems per side
/// with mines and refineries, some yards, troops and ships for load, two fleets (one passing, one blockading) and varied
/// support so smuggling and the load brake show. Not a rule and not a starting position; M5 deals the real game.
/// Setup draws no random numbers except what <see cref="EconomyModule.SetupNewGame"/> draws (the two incident timers).
/// </summary>
public sealed class DemoScenario
{
    public required Game Game { get; init; }
    public required FleetModule Fleets { get; init; }
    public required EconomyModule Economy { get; init; }
    public required Galaxy Galaxy { get; init; }
    /// <summary>Present when the scenario was made with support on (popular support, garrisons, uprisings).</summary>
    public SupportModule? Support { get; init; }

    // (system, Collective support, mines, refineries); mines never exceed the system's raw slots.
    private static readonly (string System, int CollectiveSupport, int Mines, int Refineries)[] HandSystems =
    {
        ("Charter", 10, 3, 3), ("Navalia", 20, 4, 3), ("Classis", 65, 4, 2), ("Incus", 75, 3, 2),
    };
    private static readonly (string System, int CollectiveSupport, int Mines, int Refineries)[] CollectiveSystems =
    {
        ("Caminus", 80, 4, 3), ("The Moorings", 25, 3, 2), ("The Shoal", 100, 2, 2), ("Drift Harbour", 30, 3, 2),
    };

    public static DemoScenario Create(string repoRoot, ulong seed, bool incidents = true, bool support = false)
    {
        var d = Setup.GameData.Load(repoRoot); // the composition root's data loader
        var (galaxy, ships, blockade, slots, costs, weights) = (d.Galaxy, d.Ships, d.Blockade, d.Slots, d.Costs, d.Weights);
        return Create(new Game(seed), galaxy, ships, blockade, slots, costs, incidents, support, weights);
    }

    public static DemoScenario Create(Game game, Galaxy galaxy, IReadOnlyDictionary<string, ShipClass> ships, BlockadeData blockade,
        SlotValues slots, CostData costs, bool incidents = true, bool support = false, GarrisonWeights? weights = null)
    {
        var fleets = FleetModule.Attach(game, galaxy, ships, blockade);
        var eco = EconomyModule.Attach(game, galaxy, slots, costs, fleets);
        eco.IncidentsEnabled = incidents;

        void Place(Side side, (string System, int CollectiveSupport, int Mines, int Refineries) s)
        {
            fleets.SetOwner(s.System, side);
            eco.SetCollectiveSupport(s.System, s.CollectiveSupport);
            for (int i = 0; i < s.Mines; i++) eco.AddFacility(s.System, FacilityKind.Mine);
            for (int i = 0; i < s.Refineries; i++) eco.AddFacility(s.System, FacilityKind.Refinery);
        }
        foreach (var s in HandSystems) Place(Side.Hand, s);
        foreach (var s in CollectiveSystems) Place(Side.Collective, s);

        // Load (maintenance): yards, ships and troops as economy objects. The Hand ends up above its capacity so autoscrap runs.
        eco.AddFacility("Navalia", FacilityKind.OrbitalShipyard);
        eco.AddFacility("Incus", FacilityKind.OrbitalShipyard);
        eco.AddFacility("Charter", FacilityKind.ConstructionYard);
        for (int i = 0; i < 5; i++) eco.AddObject(Side.Hand, "Mandate");
        eco.AddObject(Side.Hand, "Edict");
        for (int i = 0; i < 10; i++) eco.AddObject(Side.Hand, "Pacifiers", "Charter");
        eco.AddFacility("Caminus", FacilityKind.Lien150);
        for (int i = 0; i < 3; i++) eco.AddObject(Side.Collective, "Ark");
        for (int i = 0; i < 4; i++) eco.AddObject(Side.Collective, "Mender");
        for (int i = 0; i < 6; i++) eco.AddObject(Side.Collective, "Militia", "The Shoal");

        // Fleets: a Hand picket at Navalia, a Collective picket at Caminus, an Interdictor sent to blockade Drift Harbour and a Collective
        // transport sent from The Thaw to The Shoal. Movement draws no random numbers.
        fleets.AddFleet(Side.Hand, "Navalia Picket", new[] { "Mandate" }, "Navalia");
        fleets.AddFleet(Side.Collective, "Caminus Picket", new[] { "Dodger" }, "Caminus");
        eco.AddObject(Side.Collective, "Swift", "Caminus"); // a squadron based at Caminus (a corvette has no berth)
        int interdictor = fleets.AddFleet(Side.Hand, "Interdiction", new[] { "Subpoena" }, "Tierra Larga");
        int transport = fleets.AddFleet(Side.Collective, "Supply", new[] { "Mule" }, "The Thaw");
        fleets.SubmitMove(Side.Hand, interdictor, "Drift Harbour");
        fleets.SubmitMove(Side.Collective, transport, "The Shoal");

        // Support on: garrisons and the support module. The Moorings (support 25, no regiments) is the system that rises; Classis,
        // Incus and Drift Harbour are garrisoned. Drift Harbour is blockaded by the Interdictor (the Hand has 70 there, a majority, so
        // Collective support there drops 1 per 30 days; see m2-popular-support, presence timer). rule: m2-uprisings-and-garrison, m2-popular-support.
        SupportModule? sup = null;
        if (support)
        {
            sup = SupportModule.Attach(game, galaxy, eco, fleets, weights);
            for (int i = 0; i < 3; i++) eco.AddObject(Side.Hand, "Tranches", "Classis");
            for (int i = 0; i < 4; i++) eco.AddObject(Side.Hand, "Tranches", "Incus");
            for (int i = 0; i < 4; i++) eco.AddObject(Side.Collective, "Militia", "Drift Harbour");
        }

        eco.SetupNewGame();
        return new DemoScenario { Game = game, Fleets = fleets, Economy = eco, Galaxy = galaxy, Support = sup };
    }
}
