using Rebellion.Core.Clock;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;
using Rebellion.Core.Support;

namespace Rebellion.Core.Economy;

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

    // (system, Alliance support, mines, refineries); mines never exceed the system's raw slots.
    private static readonly (string System, int AllianceSupport, int Mines, int Refineries)[] EmpireSystems =
    {
        ("Coruscant", 10, 3, 3), ("Kuat", 20, 4, 3), ("Corellia", 65, 4, 2), ("Fondor", 75, 3, 2),
    };
    private static readonly (string System, int AllianceSupport, int Mines, int Refineries)[] AllianceSystems =
    {
        ("Sullust", 80, 4, 3), ("Mon Calamari", 25, 3, 2), ("Yavin", 100, 2, 2), ("Bespin", 30, 3, 2),
    };

    public static DemoScenario Create(string repoRoot, ulong seed, bool incidents = true, bool support = false)
    {
        var galaxy = GalaxyLoader.LoadFolder(Path.Combine(repoRoot, "data", "design"));
        var ships = ShipData.LoadAll(Path.Combine(repoRoot, "data", "interim"));
        var blockade = BlockadeData.Load(Path.Combine(repoRoot, "data", "design", BlockadeData.FileName));
        var slots = SlotValues.Load(Path.Combine(repoRoot, "data", "design", SlotValues.File), galaxy);
        var costs = CostData.Load(Path.Combine(repoRoot, "data", "interim", CostData.FileName));
        var weights = GarrisonWeights.Load(Path.Combine(repoRoot, "data", "design", GarrisonWeights.FileName));
        return Create(new Game(seed), galaxy, ships, blockade, slots, costs, incidents, support, weights);
    }

    public static DemoScenario Create(Game game, Galaxy galaxy, IReadOnlyDictionary<string, ShipClass> ships, BlockadeData blockade,
        SlotValues slots, CostData costs, bool incidents = true, bool support = false, GarrisonWeights? weights = null)
    {
        var fleets = FleetModule.Attach(game, galaxy, ships, blockade);
        var eco = EconomyModule.Attach(game, galaxy, slots, costs, fleets);
        eco.IncidentsEnabled = incidents;

        void Place(Side side, (string System, int AllianceSupport, int Mines, int Refineries) s)
        {
            fleets.SetOwner(s.System, side);
            eco.SetAllianceSupport(s.System, s.AllianceSupport);
            for (int i = 0; i < s.Mines; i++) eco.AddFacility(s.System, FacilityKind.Mine);
            for (int i = 0; i < s.Refineries; i++) eco.AddFacility(s.System, FacilityKind.Refinery);
        }
        foreach (var s in EmpireSystems) Place(Side.Empire, s);
        foreach (var s in AllianceSystems) Place(Side.Alliance, s);

        // Load (maintenance): yards, ships and troops as economy objects. The Empire ends up above its capacity so autoscrap runs.
        eco.AddFacility("Kuat", FacilityKind.OrbitalShipyard);
        eco.AddFacility("Fondor", FacilityKind.OrbitalShipyard);
        eco.AddFacility("Coruscant", FacilityKind.ConstructionYard);
        for (int i = 0; i < 5; i++) eco.AddObject(Side.Empire, "Imperial Star Destroyer");
        eco.AddObject(Side.Empire, "Super Star Destroyer");
        for (int i = 0; i < 10; i++) eco.AddObject(Side.Empire, "Stormtrooper Regiment", "Coruscant");
        eco.AddFacility("Sullust", FacilityKind.Kdy150);
        for (int i = 0; i < 3; i++) eco.AddObject(Side.Alliance, "Mon Calamari Cruiser");
        for (int i = 0; i < 4; i++) eco.AddObject(Side.Alliance, "Nebulon-B Frigate");
        for (int i = 0; i < 6; i++) eco.AddObject(Side.Alliance, "Alliance Army Regiment", "Yavin");

        // Fleets: an Imperial picket at Kuat, a Rebel picket at Sullust, an Interdictor sent to blockade Bespin and a Rebel
        // transport sent from Hoth to Yavin. Movement draws no random numbers.
        fleets.AddFleet(Side.Empire, "Kuat Picket", new[] { "Imperial Star Destroyer" }, "Kuat");
        fleets.AddFleet(Side.Alliance, "Sullust Picket", new[] { "Corellian Corvette" }, "Sullust", new[] { "X-wing" });
        int interdictor = fleets.AddFleet(Side.Empire, "Interdiction", new[] { "Interdictor Cruiser" }, "Kinyen");
        int transport = fleets.AddFleet(Side.Alliance, "Supply", new[] { "Medium Transport" }, "Hoth");
        fleets.SubmitMove(Side.Empire, interdictor, "Bespin");
        fleets.SubmitMove(Side.Alliance, transport, "Yavin");

        // Support on: garrisons and the support module. Mon Calamari (support 25, no regiments) is the system that rises; Corellia,
        // Fondor and Bespin are garrisoned. Bespin is blockaded by the Interdictor (the Empire has 70 there, a majority, so
        // Alliance support there drops 1 per 30 days; see m2-popular-support, presence timer). rule: m2-uprisings-and-garrison, m2-popular-support.
        SupportModule? sup = null;
        if (support)
        {
            sup = SupportModule.Attach(game, galaxy, eco, fleets, weights);
            for (int i = 0; i < 3; i++) eco.AddObject(Side.Empire, "Imperial Army Regiment", "Corellia");
            for (int i = 0; i < 4; i++) eco.AddObject(Side.Empire, "Imperial Army Regiment", "Fondor");
            for (int i = 0; i < 4; i++) eco.AddObject(Side.Alliance, "Alliance Army Regiment", "Bespin");
        }

        eco.SetupNewGame();
        return new DemoScenario { Game = game, Fleets = fleets, Economy = eco, Galaxy = galaxy, Support = sup };
    }
}
