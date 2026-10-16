using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Construction;

/// <summary>
/// A small deterministic build scenario on the real galaxy, for the headless runner and the soak test: an Empire home with a
/// shipyard, a training facility, two construction yards and two mine and refinery pairs builds fighters, a mine and regiments;
/// the regiments are sent to a far system along a path that passes a Rebel picket, which holds them (D2) until it moves away.
/// Not a rule and not a starting position. Setup draws no random numbers; the home's four mines and refineries draw their first cycles.
/// </summary>
public sealed class BuildDemo
{
    public const string HomeSystem = "Coruscant";
    public const string NearSystem = "Corellia";
    public const string FarSystem = "Hoth";

    public required Game Game { get; init; }
    public required FleetModule Fleets { get; init; }
    public required EconomyModule Economy { get; init; }
    public required ConstructionModule Construction { get; init; }
    public required Galaxy Galaxy { get; init; }
    /// <summary>The Rebel picket (fleet id), the system it sits at, and the system it moves to when released.</summary>
    public required int Picket { get; init; }
    public required string PicketAt { get; init; }
    public required string PicketMovesTo { get; init; }
    public required IReadOnlyList<string> RegimentPath { get; init; }

    public static BuildDemo Create(string repoRoot, ulong seed)
    {
        var d = Setup.GameData.Load(repoRoot); // the composition root's data loader
        var (galaxy, ships, blockade, slots, costs) = (d.Galaxy, d.Ships, d.Blockade, d.Slots, d.Costs);
        var game = new Game(seed);
        var fleets = FleetModule.Attach(game, galaxy, ships, blockade);
        // Autoscrap is out of reach: the Rebel picket is a fleet ship and pays upkeep (m3-fleets), but the demo gives the Alliance no mine or
        // refinery, so its capacity is 0 and the picket would be scrapped on day 10. The demo is about deliveries, not about overdraft.
        var eco = EconomyModule.Attach(game, galaxy, slots, costs, fleets, new EconomyParams { ScrapPeriod = 1_000_000 });
        eco.IncidentsEnabled = false;
        var build = ConstructionModule.Attach(game, fleets, eco);

        foreach (var s in new[] { HomeSystem, NearSystem, FarSystem })
        {
            fleets.SetOwner(s, Side.Empire);
            eco.SetAllianceSupport(s, 0); // Empire support 100
        }
        eco.AddFacility(HomeSystem, FacilityKind.OrbitalShipyard);
        eco.AddFacility(HomeSystem, FacilityKind.TrainingFacility);
        eco.AddFacility(HomeSystem, FacilityKind.ConstructionYard);
        eco.AddFacility(HomeSystem, FacilityKind.ConstructionYard);
        for (int i = 0; i < 2; i++) { eco.AddFacility(HomeSystem, FacilityKind.Mine); eco.AddFacility(HomeSystem, FacilityKind.Refinery); }
        eco.SetupNewGame();
        eco.SetPools(Side.Empire, 0, 1000); // after SetupNewGame, which sets the starting stockpile

        var path = fleets.FindPath(HomeSystem, FarSystem, 100).Names(galaxy);
        string at = path[1];
        int atIndex = galaxy.Systems.ToList().FindIndex(s => s.Name == at);
        string away = galaxy.NearOf(atIndex).Select(n => galaxy.Systems[n].Name).Where(n => !path.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).First();
        int picket = fleets.AddFleet(Side.Alliance, "Rebel Picket", new[] { "Corellian Corvette" }, at);

        build.SubmitBuild(Side.Empire, HomeSystem, YardFamily.Shipyard, "TIE Fighter", 2);
        build.SubmitBuild(Side.Empire, HomeSystem, YardFamily.Training, "Stormtrooper Regiment", 2, FarSystem);
        build.SubmitBuild(Side.Empire, HomeSystem, YardFamily.Construction, "Mine", 1, NearSystem);
        return new BuildDemo
        {
            Game = game, Fleets = fleets, Economy = eco, Construction = build, Galaxy = galaxy,
            Picket = picket, PicketAt = at, PicketMovesTo = away, RegimentPath = path,
        };
    }
}
