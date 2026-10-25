using Praedra.Core.Map;
using Praedra.Core.Ships;
using Praedra.Core.Travel;

namespace Praedra.Core.Tests.Travel;

// rules: m1-travel tests T1-T18, T20, T21 (T19 needs the clock and fleets; not in this package). Rewritten 2026-10-16 for free travel and routes.
public class TravelTests
{
    private static Galaxy G => TestData.RealGalaxy;
    private static readonly Lazy<IReadOnlyDictionary<string, ShipClass>> Ships = new(() => ShipData.LoadAll(TestData.ClassicDir));

    private static int SpeedOf(params string[] classes) =>
        TravelFormula.FleetSpeed(
            classes.Select(c => Ships.Value[c]).Where(s => s.IsCapital).Select(s => s.Speed(false)),
            classes.Select(c => Ships.Value[c]).Where(s => !s.IsCapital).Select(s => s.Speed(false)));

    private static int Leg(string a, string b, int speed) => TravelFormula.RouteLegTicks(MapDist(G, a, b), speed, G.FindLane(a, b)!.FactorPercent);
    private static int MapDist(Galaxy g, string a, string b) { var x = g.Get(a); var y = g.Get(b); return TravelFormula.MapDistance(x.MapX, x.MapY, y.MapX, y.MapY); }
    private static int OffRoute(string a, string b, int speed) => TravelFormula.HopTicks(MapDist(G, a, b) / 5, speed, 100);

    [Fact]
    public void T1_LongMajorRouteLeg() // m1-travel T1: factor 300: distance * speed / 1500, rounded half up
    {
        // v6 re-pin 2026-10-24: Coruscant-Corellia is no longer a link; Coruscant's great-route links are 18 and 29 units, too short
        // to show the rounding, so the leg is the longest great-route link, Epikonia-Vondarc (Long Haul, 111 units).
        Assert.Equal(1, G.FindLane("Ellerbeck", "Longquay")!.Tier);
        Assert.Equal(111, MapDist(G, "Ellerbeck", "Longquay"));
        Assert.Equal(new[] { 7, 6, 4, 4 }, new[] { 100, 80, 60, 50 }.Select(s => Leg("Ellerbeck", "Longquay", s))); // 7.4, 5.92, 4.44, 3.7
    }

    [Fact]
    public void T2_CoruscantYavinOffRouteIsTheOriginalFormula() // m1-travel T2: (isqrt / 5) * speed / 100, both divisions floored (Traced)
    {
        // v6 re-pin 2026-10-24: The Shoal is 294 units from Charter (was 240); 58 * 80 / 100 = 46.4, 58 * 50 / 100 = 29.
        Assert.Equal(58, MapDist(G, "Charter", "The Shoal") / 5);
        Assert.Equal(new[] { 58, 46, 29 }, new[] { 100, 80, 50 }.Select(s => OffRoute("Charter", "The Shoal", s)));
        Assert.Null(G.FindLane("Charter", "The Shoal"));
    }

    [Fact]
    public void T3_ShortMajorLeg() // m1-travel T3
    {
        // v6 re-pin 2026-10-24: Corellia-Duros is no longer a link; Azbian-Contruum is a 25-unit great-route link.
        Assert.Equal(1, G.FindLane("Kaolin", "Puerto Hierro")!.Tier);
        Assert.Equal(25, MapDist(G, "Kaolin", "Puerto Hierro"));
        Assert.Equal(new[] { 2, 1, 1 }, new[] { 100, 80, 50 }.Select(s => Leg("Kaolin", "Puerto Hierro", s))); // 1.67, 1.33, 0.83 raised to 1
    }

    [Fact]
    public void T4_CoruscantMinorRoute() // m1-travel T4: factor 150
    {
        // v6 re-pin 2026-10-24: Coruscant's minor link is Hartwell (was Tutela).
        int d = MapDist(G, "Charter", "Hartwell");
        Assert.Equal(150, G.FindLane("Charter", "Hartwell")!.FactorPercent);
        Assert.Equal((d * 100 * 2 + 750) / 1500, Leg("Charter", "Hartwell", 100));
        Assert.Equal(Math.Max(1, (d * 80 * 2 + 750) / 1500), Leg("Charter", "Hartwell", 80));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(80)]
    [InlineData(100)]
    public void T5_ShortHopCostsOneDay(int speed) // m1-travel T5
    {
        Assert.Equal(0, TravelFormula.BaseDistance(10, 10, 14, 10)); // 4 units apart
        Assert.Equal(1, TravelFormula.HopTicks(0, speed, 300));
        Assert.Equal(1, TravelFormula.RouteLegTicks(4, speed, 300));
    }

    [Fact]
    public void T6_FleetSpeedIsLargestRating() // m1-travel T6
        => Assert.Equal(100, SpeedOf("Mandate", "Mule"));

    [Fact]
    public void T7_MonCalamariAndIsd() // m1-travel T7
        => Assert.Equal(80, SpeedOf("Ark", "Mandate"));

    [Fact]
    public void T8_TieFighterAloneIsImmobile() // m1-travel T8
    {
        int speed = SpeedOf("Gleaner");
        Assert.Equal(0, speed);
        var r = new Pathfinder(G).Find("Charter", "Classis", speed);
        Assert.Equal(PathStatus.Immobile, r.Status);
    }

    [Fact]
    public void CapitalShipRatingZeroMakesFleetImmobileAndSquadronsAreIgnored() // m1-travel §Rule (Classic)
    {
        Assert.Equal(0, TravelFormula.FleetSpeed(new[] { 80, 0 }, Array.Empty<int>()));
        Assert.Equal(80, TravelFormula.FleetSpeed(new[] { 80 }, new[] { 0, 60 })); // carried squadrons never count
        Assert.Equal(60, SpeedOf("Swift")); // squadron-only fleet uses squadron ratings
        Assert.Equal(100, TravelFormula.FleetSpeed(Array.Empty<int>(), Array.Empty<int>())); // unrated: default 100
    }

    private static void AssertPath(PathResult r, Galaxy g, string[] names, int[] hops, int total)
    {
        Assert.True(r.Ok, r.Status.ToString());
        Assert.Equal(names, r.Names(g));
        Assert.Equal(hops, r.HopTicks);
        Assert.Equal(total, r.TotalTicks);
        Assert.Equal(r.HopTicks.Sum(), r.TotalTicks);
    }

    [Fact]
    public void T9_CoruscantToHothRidesTheRoutes() // m1-travel T9 (real data, speed 80)
    {
        var r = new Pathfinder(G).Find("Charter", "The Thaw", 80);
        Assert.True(r.Ok);
        Assert.Equal(45, r.TotalTicks); // v6 re-pin 2026-10-24 (was 32): The Thaw is 432 units out on the spiral rim
        var names = r.Names(G);
        Assert.Equal(new[] { "Charter", "Pyrite", "Vigilia" }, names.Take(3)); // QA-40 re-pin 2026-10-25: out along the Charter Road (v6: the Trunk Line)
        Assert.Equal("The Thaw", names[^1]);
        foreach (var mid in r.Systems.Skip(1).SkipLast(1)) Assert.True(G.OnRoute(mid)); // never stops at an off-route system on the way
        Assert.False(G.OnRoute(G.Get("The Thaw").Index)); // The Thaw is off every route: the last leg is off-route
    }

    [Fact]
    public void T10_ClassicStraightLineIsOneLeg() // m1-travel T10 (TravelMode.Straight, the Classic rule)
    {
        var pf = new Pathfinder(G, TravelMode.Straight);
        var r = pf.Find("Charter", "The Thaw", 80);
        AssertPath(r, G, new[] { "Charter", "The Thaw" }, new[] { OffRoute("Charter", "The Thaw", 80) }, OffRoute("Charter", "The Thaw", 80));
        Assert.Equal(68, r.TotalTicks); // v6 re-pin 2026-10-24: 432 / 5 = 86, 86 * 80 / 100 = 68.8
    }

    // S(0,0) and E(500,0) off-route; R1(100,0) - R2(400,0) a major route link; X(250,10) off-route, between them.
    private static Galaxy RouteLine() => TestData.Synthetic(
        new[] { ("E", 500, 0), ("R1", 100, 0), ("R2", 400, 0), ("S", 0, 0), ("X", 250, 10) },
        new[] { ("R1", "R2", "route") });

    [Fact]
    public void T11_MixedPathFliesToTheRouteRidesItAndFliesOff() // m1-travel T11, speed 100
    {
        var g = RouteLine();
        // straight: 500 / 5 = 100 days. Mixed: S-R1 20 (off), R1-R2 300 * 100 / 1500 = 20 (route), R2-E 20 (off) = 60.
        AssertPath(new Pathfinder(g).Find("S", "E", 100), g, new[] { "S", "R1", "R2", "E" }, new[] { 20, 20, 20 }, 60);
        AssertPath(new Pathfinder(g, TravelMode.Straight).Find("S", "E", 100), g, new[] { "S", "E" }, new[] { 100 }, 100);
    }

    [Fact]
    public void T12_NoStopAtAnOffRouteSystemOnTheWay() // m1-travel T12: X lies on the way but is never a waypoint
    {
        var g = RouteLine();
        var pf = new Pathfinder(g);
        foreach (var (a, b) in new[] { ("S", "E"), ("E", "S"), ("S", "R2"), ("R1", "E") })
            Assert.DoesNotContain("X", pf.Find(a, b, 100).Names(g));
        Assert.Equal(new[] { "S", "X" }, pf.Find("S", "X", 100).Names(g)); // 250 / 5 = 50 off-route beats S-R1-X (20 + 30)
    }

    [Fact]
    public void T13_RouteNotWorthItFliesStraight() // m1-travel T13: a route far off the line is ignored
    {
        var g = TestData.Synthetic(new[] { ("E", 100, 0), ("R1", 0, 300), ("R2", 100, 300), ("S", 0, 0) }, new[] { ("R1", "R2", "route") });
        AssertPath(new Pathfinder(g).Find("S", "E", 100), g, new[] { "S", "E" }, new[] { 20 }, 20);
    }

    [Fact]
    public void T14_TieOnTicksFewestLegsWins() // m1-travel T14: S-R1-E (10 + 10) ties S-E (20): one leg wins
    {
        var g = TestData.Synthetic(new[] { ("E", 100, 0), ("R1", 50, 0), ("S", 0, 0) }, new[] { ("R1", "E", "generated") });
        AssertPath(new Pathfinder(g).Find("S", "E", 100), g, new[] { "S", "E" }, new[] { 20 }, 20);
    }

    [Fact]
    public void T15_TieOnTicksAndLegsTakesTheSmallerPredecessor() // m1-travel T15: via P or Q both 28 + 9 = 37 in 2 legs: P (smaller name)
    {
        var g = TestData.Synthetic(new[] { ("E", 200, 100), ("P", 100, 200), ("Q", 100, 0), ("S", 0, 100) },
            new[] { ("P", "E", "route"), ("Q", "E", "route") });
        AssertPath(new Pathfinder(g).Find("S", "E", 100), g, new[] { "S", "P", "E" }, new[] { 28, 9 }, 37);
    }

    [Fact]
    public void T22_AvoidingAStopTakesTheNextBestPath() // m1-travel T22 (Proposal, the AI goes around known traps): no stop at P, so via Q
    {
        var g = TestData.Synthetic(new[] { ("E", 200, 100), ("P", 100, 200), ("Q", 100, 0), ("S", 0, 100) },
            new[] { ("P", "E", "route"), ("Q", "E", "route") });
        var pf = new Pathfinder(g);
        int Ix(string n) => g.Get(n).Index;
        AssertPath(pf.FindAvoiding(Ix("S"), Ix("E"), 100, new[] { Ix("P") }), g, new[] { "S", "Q", "E" }, new[] { 28, 9 }, 37);
        // avoiding both route systems leaves the straight line; the destination and the start are never avoided
        AssertPath(pf.FindAvoiding(Ix("S"), Ix("E"), 100, new[] { Ix("P"), Ix("Q"), Ix("E"), Ix("S") }), g, new[] { "S", "E" }, new[] { 40 }, 40);
        AssertPath(pf.Find("S", "E", 100), g, new[] { "S", "P", "E" }, new[] { 28, 9 }, 37); // the cached search is untouched
    }

    [Fact]
    public void T16_NavigatorOnIsdFleet() // m1-travel T16
    {
        var fleet = SpeedOf("Mandate");
        Assert.Equal(80, fleet);
        var nav = TravelFormula.NavigatorSpeed(fleet, 80);
        Assert.Equal(64, nav);
        Assert.Equal(5, Leg("Ellerbeck", "Longquay", nav)); // v6 re-pin 2026-10-24 (the T1 leg): 111 * 64 / 1500 = 4.74
    }

    [Fact]
    public void T17_NavigatorFloor() // m1-travel T17
    {
        Assert.Equal(25, TravelFormula.NavigatorSpeed(20, 80)); // 16, raised to the floor
        Assert.Equal(0, TravelFormula.NavigatorSpeed(0, 80));  // immobile stays immobile
    }

    [Fact]
    public void T18_HanMissionGroupSpeed50() // m1-travel T18
        => Assert.Equal(4, Leg("Ellerbeck", "Longquay", 50)); // v6 re-pin 2026-10-24 (the T1 leg): 111 * 50 / 1500 = 3.7

    [Fact]
    public void T20_IlumAndCsillaAreReachable() // m1-travel T20: free travel reaches the two systems outside sectors (no lanes needed)
    {
        var pf = new Pathfinder(G);
        var r = pf.Find("Charter", "Glasswood", 80);
        Assert.True(r.Ok);
        Assert.Equal("Glasswood", r.Names(G)[^1]);
        Assert.True(pf.Find("Frostholm", "Charter", 80).Ok);
    }

    [Fact]
    public void RejectionReasons() // m1-travel §Defaults (AlreadyThere, Immobile), §Path rule 6
    {
        var pf = new Pathfinder(G);
        Assert.Equal(PathStatus.AlreadyThere, pf.Find("Charter", "Charter", 80).Status);
        Assert.Equal(PathStatus.UnknownSystem, pf.Find("Charter", "No Such Place", 80).Status);
        Assert.Equal(PathStatus.UnknownSystem, pf.Find("No Such Place", "Charter", 80).Status);
        Assert.Equal(PathStatus.Immobile, pf.Find("Charter", "Classis", 0).Status);
    }

    [Fact]
    public void T21_SameInputsGiveIdenticalPaths() // m1-travel T21
    {
        var names = new[] { "Charter", "The Thaw", "Opifex", "Greenfever", "Drift Harbour", "The Lantern", "Amargura" };
        List<string> Run()
        {
            var pf = new Pathfinder(TestData.RealGalaxy);
            var lines = new List<string>();
            foreach (var a in names) foreach (var b in names) foreach (var sp in new[] { 50, 80, 100 })
            {
                var r = pf.Find(a, b, sp);
                lines.Add($"{a}>{b}@{sp}:{r.Status}:{string.Join("/", r.Names(G))}:{r.TotalTicks}");
            }
            return lines;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void PathsAreCachedPerStartEndAndSpeed() // m1-travel §Path rule 4
    {
        var pf = new Pathfinder(G);
        var a = pf.Find("Charter", "The Thaw", 50);
        Assert.Same(a, pf.Find("Charter", "The Thaw", 50));
        var b = pf.Find("Charter", "The Thaw", 80);
        Assert.NotSame(a, b);
        Assert.Equal(2, pf.CachedCount);
    }

    [Fact]
    public void EveryHopIsAtLeastOneDay() // m1-travel §Per-hop formula
    {
        var r = new Pathfinder(G).Find("Charter", "The Thaw", 50);
        Assert.All(r.HopTicks, t => Assert.True(t >= 1));
        Assert.True(r.TotalTicks >= r.Hops);
    }
}
