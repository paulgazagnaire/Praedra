using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Fog;
using Rebellion.Core.Setup;
using Rebellion.Core.Travel;
using static Rebellion.Core.Tests.Fog.FogWorld;

namespace Rebellion.Core.Tests.Fog;

// Sensor Station tests SS1 to SS22 of m5-fog-and-hq §N4 (route-only, N-day warning: movement-redesign-draft §C; window 10 days Decided by
// Paul 2026-10-16), on Map M (FogWorld: chain C0 - R1 - R2 - R3 - R4, every one a route system; Ilum on no route; Empire holds C0 and R1,
// Alliance holds R2, R3, R4; Empire fleet E1 of 3 Imperial Star Destroyers at R1). Map M travels hop by hop along its links (TravelMode.Lanes),
// so every leg is a route leg; SS17 and SS7b use TravelMode.Straight for off-route jumps. The tests read the day a fleet really reaches a
// system from FleetArrived / FleetPassed, so they do not depend on a link's travel time.
public class SensorStationTests
{
    private static readonly string Cls = FacilityKinds.ClassName(FacilityKind.SensorStation);

    private static FogWorld World(GameSettings? s = null, params string[] allianceStations) => World(s, TravelMode.Lanes, allianceStations);

    private static FogWorld World(GameSettings? s, TravelMode travel, params string[] allianceStations)
    {
        var w = new FogWorld(s, travel: travel);
        foreach (var st in allianceStations) w.H.Economy.AddFacility(st, FacilityKind.SensorStation);
        return w;
    }

    private static int Send(FogWorld w, Side side, string from, string to, int ships)
    {
        int f = w.Fleet(side, from, Isd, ships);
        w.H.Fleets.SubmitMove(side, f, to);
        return f;
    }

    /// <summary>Step day by day until a SensorReport appears or <paramref name="cap"/> days have passed.</summary>
    private static List<SensorReport> RunToReport(FogWorld w, int cap = 120)
    {
        for (int i = 0; i < cap && !w.Events<SensorReport>().Any(); i++) w.Step();
        return w.Events<SensorReport>();
    }

    private static void RunAll(FogWorld w, int days) { for (int i = 0; i < days; i++) w.Step(); }

    /// <summary>The day fleet <paramref name="fleet"/> was really placed at <paramref name="sys"/> (arrived or passed), or null.</summary>
    private static int? ReachedOn(FogWorld w, int fleet, string sys) =>
        w.Events<FleetArrived>().Where(a => a.Fleet == fleet && a.System == sys).Select(a => (int?)a.Day).FirstOrDefault()
        ?? w.Events<FleetPassed>().Where(a => a.Fleet == fleet && a.System == sys).Select(a => (int?)a.Day).FirstOrDefault();

    private static List<SensorWarning> Memory(FogWorld w, Side side) => w.Game.State.Fog.Of(side).SensorWarnings ?? new List<SensorWarning>();

    // m5-fog-and-hq SS1: built by a Construction Yard like a mine; one per planet
    [Fact]
    public void SS1_BuiltByAConstructionYardOnePerPlanet()
    {
        var w = new FogWorld();
        w.H.Economy.AddFacility(R1, FacilityKind.ConstructionYard);
        w.H.Economy.SetPools(Side.Empire, 0, 500);
        w.H.Construction.RefuseOverdrawOrders = false; // B5b 2026-10-18: Map M has no mines (capacity 0); the upkeep refusal is not what this checks
        int energyBefore = w.H.Economy.FreeEnergy(R1), rawBefore = w.H.Economy.FreeRaw(R1);
        Assert.Null(w.H.Economy.CheckPlace(R1, FacilityKind.SensorStation));
        w.H.Construction.SubmitBuild(Side.Empire, R1, YardFamily.Construction, Cls, 1, R1);
        w.Step();
        Assert.Contains(w.Events<ProjectStarted>(), p => p.Class == Cls);
        Assert.Equal(energyBefore - 1, w.H.Economy.FreeEnergy(R1)); // one energy slot
        Assert.Equal(rawBefore, w.H.Economy.FreeRaw(R1));           // no raw slot
        // a second one while the first is queued: refused; so is two in one order; CheckPlace agrees
        Assert.Equal("OnePerPlanet", w.H.Economy.CheckPlace(R1, FacilityKind.SensorStation));
        Assert.Equal("OnePerPlanet", w.H.Economy.CheckPlace(R2, FacilityKind.SensorStation, 2));
        for (int i = 0; i < 40 && !w.Events<UnitCompleted>().Any(p => p.Class == Cls); i++) w.Step();
        Assert.Contains(w.Events<UnitCompleted>(), p => p.Class == Cls);
        Assert.Equal(1, w.H.Economy.OfKindAt(R1, FacilityKind.SensorStation));
        long seq = w.H.Construction.SubmitBuild(Side.Empire, R1, YardFamily.Construction, Cls, 1, R1);
        int from = w.Game.Events.Count;
        w.Step();
        Assert.Equal(RejectReason.OnePerPlanet, w.Game.Events.Skip(from).OfType<OrderRejected>().Single(r => r.Sequence == seq).Reason);
        seq = w.H.Construction.SubmitBuild(Side.Empire, R1, YardFamily.Construction, Cls, 2, C0);
        from = w.Game.Events.Count;
        w.Step();
        Assert.Equal(RejectReason.OnePerPlanet, w.Game.Events.Skip(from).OfType<OrderRejected>().Single(r => r.Sequence == seq).Reason);
    }

    // m5-fog-and-hq SS2: cost 10 refined, maintenance 5, research level 0, from the data; the sensor switches and their DEFAULTs
    [Fact]
    public void SS2_CostMaintenanceEnergyAndResearchComeFromData()
    {
        var w = new FogWorld();
        var row = w.H.Data.Costs.Get(Cls);
        Assert.Equal(ObjectCategory.Facility, row.Category);
        Assert.Equal((10, 5, 0), (row.Cost, row.Maintenance, row.ResearchOrder));
        Assert.True(row.Alliance && row.Empire);
        Assert.True(w.H.Construction.IsResearched(Side.Empire, Cls));
        Assert.True(w.H.Construction.IsResearched(Side.Alliance, Cls));
        int load = w.H.Economy.Load(Side.Alliance), free = w.H.Economy.FreeEnergy(R3);
        w.H.Economy.AddFacility(R3, FacilityKind.SensorStation);
        Assert.Equal(load + 5, w.H.Economy.Load(Side.Alliance));
        Assert.Equal(free - 1, w.H.Economy.FreeEnergy(R3));
        var p = new FogParams();
        Assert.Equal(10, p.SensorWarningDays);        // Decided, Paul 2026-10-16
        Assert.True(p.SensorSeesOffRouteArrivals);   // Proposal (lead) DEFAULT
        Assert.Equal((3, 9), (p.SensorFewMax, p.SensorSquadronMax));
    }

    // m5-fog-and-hq SS3: a fleet whose trip ends at the station's system is warned of: system, side, size class, expected day; on the first
    // day it is inside the window (exactly SensorWarningDays ahead on this map: the trip is longer than the window)
    [Fact]
    public void SS3_WarningForAFleetHeadingForTheStation()
    {
        var w = World(null, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R3);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal((R3, Side.Empire, SensorSize.Few, Side.Alliance), (r.System, r.FleetSide, r.Size, r.Viewer));
        Assert.Equal(w.Game.Day, r.Day); // stamp K + 1 = the day the Step produces
        var s = Assert.Single(w.Fog.Sightings(Side.Alliance), x => x.Kind == SightingKind.SensorArrival);
        Assert.Equal((R3, Side.Empire, SensorSize.Few, (int?)r.ReachDay, r.Day), (s.System, s.Side, s.Size, s.ReachDay, s.Day));
        Assert.Null(s.Detail);
        Assert.Single(w.Fog.ViewFor(Side.Alliance).SensorReports());
        RunAll(w, 60);
        Assert.Equal(ReachedOn(w, w.E1, R3), r.ReachDay); // the expected day is the day it really arrives
        Assert.Equal(r.ReachDay - 10, r.Day);
        Assert.Single(w.Events<SensorReport>()); // nothing more once it arrived and sits there
        // the sensor does not explore anything: R4 stays unknown to the Empire, and the warning wrote no picture group of R3's neighbours
        Assert.Equal(KnowledgeLevel.Unknown, w.Fog.Level(Side.Empire, R4));
    }

    // m5-fog-and-hq SS4: the window edge: warned when the reach day is exactly SensorWarningDays ahead, not one day earlier (11 ahead);
    // the window is a data value
    [Theory]
    [InlineData(10)]
    [InlineData(3)]
    public void SS4_WindowEdge(int window)
    {
        var w = World(new GameSettings { Fog = new FogParams { SensorWarningDays = window } }, R4);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4);
        var seen = new List<int>(); // days with a report
        for (int i = 0; i < 150 && ReachedOn(w, w.E1, R4) == null; i++)
        {
            int before = w.Events<SensorReport>().Count;
            w.Step();
            if (w.Events<SensorReport>().Count > before) seen.Add(w.Game.Day);
        }
        int reach = ReachedOn(w, w.E1, R4)!.Value;
        Assert.True(reach - 1 > window + 1, "the trip must be longer than the window for this test");
        Assert.Equal(new[] { reach - window }, seen); // day reach - window: yes; day reach - window - 1: no (and no other day)
        Assert.Equal(reach, w.Events<SensorReport>().Single().ReachDay);
    }

    // m5-fog-and-hq SS5: a fleet that only passes the station's system is warned of too, with the day it passes
    [Fact]
    public void SS5_PassingFleetIsWarnedOf()
    {
        var w = World(null, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal(R3, r.System);
        RunAll(w, 80);
        Assert.Contains(w.Events<FleetPassed>(), p => p.Fleet == w.E1 && p.System == R3 && p.Day == r.ReachDay);
        Assert.Single(w.Events<SensorReport>());
    }

    // m5-fog-and-hq SS6: none for the owner's own fleets
    [Fact]
    public void SS6_NoReportForOwnFleets()
    {
        var w = World(null, R3);
        Send(w, Side.Alliance, R4, R2, 2); // flies through R3, the station's own system, and on to R2
        Send(w, Side.Alliance, R2, R3, 1); // and one heading for R3
        RunAll(w, 80);
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R2);
        Assert.Empty(w.Events<SensorReport>());
        Assert.Empty(Memory(w, Side.Alliance));
    }

    // m5-fog-and-hq SS7: a fleet whose path does not contain the station's system is not seen, even when it rides the same route
    // (station at R4, the fleet flies R1 - R2 - R3 and stops at R3) or flies over the station on a straight off-route leg
    [Fact]
    public void SS7_FleetWhosePathDoesNotContainTheStationIsNotSeen()
    {
        var w = World(null, R4);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R3);
        RunAll(w, 80);
        Assert.NotNull(ReachedOn(w, w.E1, R3));
        Assert.Empty(w.Events<SensorReport>());
        Assert.DoesNotContain(w.Fog.Sightings(Side.Alliance), x => x.Kind == SightingKind.SensorArrival);
    }

    // m5-fog-and-hq SS7b: TravelMode.Straight: R1 to R4 is one off-route leg whose line crosses R2 and R3; its path is [R1, R4], so the
    // station at R3 sees nothing (off-route legs pass no system), and the station at R4 (its destination) does (SensorSeesOffRouteArrivals)
    [Fact]
    public void SS7b_OffRouteLegPassesNoSystem()
    {
        var w = World(null, TravelMode.Straight, R3, R4);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4);
        w.Step();
        Assert.Equal(new[] { R1, R4 }, w.Game.State.Fleets[w.E1].Transit!.Path);
        RunAll(w, 80);
        var r = Assert.Single(w.Events<SensorReport>());
        Assert.Equal(R4, r.System);
        Assert.Equal(ReachedOn(w, w.E1, R4), r.ReachDay);
    }

    // m5-fog-and-hq SS8: one warning per trip: one report over the whole trip, remembered (saved state) until the reach day, then dropped
    [Fact]
    public void SS8_OneWarningPerTrip()
    {
        var w = World(null, R4);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4);
        var r = Assert.Single(RunToReport(w));
        var m = Assert.Single(Memory(w, Side.Alliance));
        Assert.Equal((w.E1, R4, r.ReachDay), (m.FleetId, m.System, m.ReachDay));
        Assert.Empty(Memory(w, Side.Empire));
        while (w.Game.Day < r.ReachDay - 1) { w.Step(); Assert.Single(Memory(w, Side.Alliance)); }
        w.Step(); // the reach day: the fleet is there, the memory entry is dropped (empty = null, left out of the saved form)
        Assert.Null(w.Game.State.Fog.Of(Side.Alliance).SensorWarnings);
        RunAll(w, 20);
        Assert.Single(w.Events<SensorReport>());
    }

    // m5-fog-and-hq SS8b: a second trip warns again (the same fleet, the same station, a new reach day)
    [Fact]
    public void SS8b_SecondTripWarnsAgain()
    {
        var w = World(null, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4); // passes R3
        for (int i = 0; i < 150 && ReachedOn(w, w.E1, R4) == null; i++) w.Step();
        Assert.Single(w.Events<SensorReport>());
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2); // back through R3
        RunAll(w, 80);
        var r = w.Events<SensorReport>();
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Equal(R3, x.System));
        Assert.True(r[1].ReachDay > r[0].ReachDay);
        Assert.Equal(2, w.Events<FleetPassed>().Count(p => p.Fleet == w.E1 && p.System == R3));
    }

    // m5-fog-and-hq SS9: size classes of the one fleet at the thresholds (Few 1..3, Squadron 4..9, Large 10 and over)
    [Theory]
    [InlineData(1, SensorSize.Few)]
    [InlineData(3, SensorSize.Few)]
    [InlineData(4, SensorSize.Squadron)]
    [InlineData(9, SensorSize.Squadron)]
    [InlineData(10, SensorSize.Large)]
    [InlineData(25, SensorSize.Large)]
    public void SS9_SizeClassesAtTheThresholds(int ships, SensorSize expected)
    {
        var w = World(null, R3);
        Send(w, Side.Empire, R1, R3, ships);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal(expected, r.Size);
    }

    // m5-fog-and-hq SS9b: the thresholds are data (FogParams), not code; a squadron-only fleet counts 1
    [Fact]
    public void SS9b_ThresholdsAreSwitchesAndSquadronsCountOne()
    {
        var w = World(new GameSettings { Fog = new FogParams { SensorFewMax = 1, SensorSquadronMax = 2 } }, R3);
        Send(w, Side.Empire, R1, R3, 3);
        Assert.Equal(SensorSize.Large, Assert.Single(RunToReport(w)).Size);
    }

    // m5-fog-and-hq SS10: no merging: two stations on one path give one warning each (each with its own reach day); two fleets on the
    // same trip give one warning each, each sized by its own ships
    [Fact]
    public void SS10_OneWarningPerFleetPerStation()
    {
        var w = World(new GameSettings { Fog = new FogParams { ObservedWindowDays = 1000 } }, R2, R3); // keep the log entries for the count below
        int a = Send(w, Side.Empire, R1, R4, 3);
        int b = Send(w, Side.Empire, R1, R4, 6);
        RunAll(w, 100);
        var r = w.Events<SensorReport>();
        Assert.Equal(4, r.Count);
        foreach (var (f, size) in new[] { (a, SensorSize.Few), (b, SensorSize.Squadron) })
            foreach (var st in new[] { R2, R3 })
                Assert.Single(r, x => x.System == st && x.ReachDay == ReachedOn(w, f, st) && x.Size == size);
        Assert.Equal(4, w.Fog.Sightings(Side.Alliance).Count(x => x.Kind == SightingKind.SensorArrival));
    }

    // m5-fog-and-hq SS11: works under blockade and uprising
    [Fact]
    public void SS11_WorksUnderBlockadeAndUprising()
    {
        var w = World(null, R3);
        w.Fleet(Side.Empire, R3, Isd, 1); // an Empire fleet at the station's system blockades it
        w.Step(2);
        Assert.True(w.Game.State.Blockades.TryGetValue(R3, out var by) && by == Side.Empire);
        w.InStep(ctx => w.H.Support.StartUprisingNow(ctx, R3));
        w.Step();
        Assert.True(w.H.Support.IsUprising(R3));
        Assert.Equal(Side.Alliance, w.H.Fleets.OwnerOf(R3)); // still the Alliance's station
        Send(w, Side.Empire, R1, R3, 2);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal((R3, Side.Alliance), (r.System, r.Viewer));
        Assert.Equal(1, w.H.Economy.OfKindAt(R3, FacilityKind.SensorStation)); // not destroyed by the blockade
    }

    // m5-fog-and-hq SS12: a captured station warns the new holder, not the old one
    [Fact]
    public void SS12_CapturedStationWarnsTheNewHolder()
    {
        var w = World(null, R3);
        w.H.Control.SubmitDebugSetHolder(Side.Empire, R3, Side.Empire);
        w.Step();
        Assert.Equal(Side.Empire, w.H.Fleets.OwnerOf(R3));
        Send(w, Side.Alliance, R4, R2, 1); // passes R3
        RunToReport(w);
        Assert.Empty(w.Fog.Sightings(Side.Alliance).Where(s => s.Kind == SightingKind.SensorArrival));
        Assert.Contains(w.Fog.Sightings(Side.Empire), s => s.Kind == SightingKind.SensorArrival && s.System == R3);
        RunAll(w, 80);
        var reports = w.Events<SensorReport>();
        var r = Assert.Single(reports);
        Assert.Equal((Side.Empire, Side.Alliance, R3), (r.Viewer, r.FleetSide, r.System));
    }

    // m5-fog-and-hq SS13: Classic switches the sensor effect off
    [Fact]
    public void SS13_ClassicGivesNoReport()
    {
        var w = World(new GameSettings { Fog = new FogParams { Ruleset = FogRuleset.Classic } }, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R3);
        RunAll(w, 80);
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R3);
        Assert.Empty(w.Events<SensorReport>());
        Assert.DoesNotContain(w.Fog.Sightings(Side.Alliance), x => x.Kind == SightingKind.SensorArrival);
        Assert.Empty(Memory(w, Side.Alliance));
    }

    // m5-fog-and-hq SS14: a station still queued or in delivery gives no report
    [Fact]
    public void SS14_IncompleteStationGivesNoReport()
    {
        var w = new FogWorld();
        w.H.Economy.AddFacility(R3, FacilityKind.SensorStation, completed: false);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R3);
        RunAll(w, 80);
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R3);
        Assert.Empty(w.Events<SensorReport>());
    }

    // m5-fog-and-hq SS15: same seed, same orders, same everything (events, both knowledge hashes, state hash)
    [Fact]
    public void SS15_Determinism()
    {
        List<string> Run()
        {
            var w = World(null, R2, R3);
            w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4);
            Send(w, Side.Empire, R1, R3, 12);
            Send(w, Side.Empire, R1, R2, 1);
            RunAll(w, 70);
            return w.Game.Events.Select(e => e.ToString()).Append(w.Fog.KnowledgeHash(Side.Empire)).Append(w.Fog.KnowledgeHash(Side.Alliance)).Append(w.H.StateHash()).ToList();
        }
        var a = Run(); var b = Run();
        Assert.Equal(a, b);
        Assert.Contains(a, e => e.Contains(nameof(SensorReport)));
    }

    // m5-fog-and-hq SS16: fairness. Only the station's owner is told; the other side's view, log and feed hold nothing of it; no fleet id
    [Fact]
    public void SS16_OnlyTheOwnersSideIsTold()
    {
        var w = World(null, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R3);
        RunToReport(w);
        RunAll(w, 1);
        Assert.NotEmpty(w.Fog.ViewFor(Side.Alliance).SensorReports());
        Assert.NotEmpty(w.Fog.EventsFor(Side.Alliance).OfType<SensorReport>());
        Assert.Empty(w.Fog.ViewFor(Side.Empire).SensorReports());
        Assert.Empty(w.Fog.EventsFor(Side.Empire).OfType<SensorReport>());
        Assert.Empty(w.Fog.ViewFor(Side.Empire).Events.OfType<SensorReport>());
        Assert.DoesNotContain(typeof(SensorReport).GetProperties(), p => p.Name.Contains("Fleet") && p.Name != nameof(SensorReport.FleetSide));
    }

    // m5-fog-and-hq SS17: a trip that ends at the station's system after an off-route jump into it: reported with
    // SensorSeesOffRouteArrivals on (DEFAULT, Proposal), not with it off; a trip ending there along a route link is reported either way
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SS17_OffRouteArrivalSwitch(bool sees)
    {
        var s = new GameSettings { Fog = new FogParams { SensorSeesOffRouteArrivals = sees } };
        var straight = World(s, TravelMode.Straight, R3);
        straight.H.Fleets.SubmitMove(Side.Empire, straight.E1, R3); // one off-route leg R1 - R3 (no link between them)
        straight.Step();
        Assert.Equal(new[] { R1, R3 }, straight.Game.State.Fleets[straight.E1].Transit!.Path);
        RunAll(straight, 80);
        Assert.NotNull(ReachedOn(straight, straight.E1, R3));
        Assert.Equal(sees ? 1 : 0, straight.Events<SensorReport>().Count);

        var lanes = World(s, R3);
        lanes.H.Fleets.SubmitMove(Side.Empire, lanes.E1, R3); // the last leg R2 - R3 runs along a route link
        RunAll(lanes, 80);
        Assert.Single(lanes.Events<SensorReport>());
    }

    // m5-fog-and-hq SS18: the log entry is pruned after the observed window like the others
    [Fact]
    public void SS18_SensorEntryIsPrunedWithTheWindow()
    {
        var w = World(new GameSettings { Fog = new FogParams { ObservedWindowDays = 5 } }, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R3);
        var r = Assert.Single(RunToReport(w));
        Assert.Contains(w.Fog.Sightings(Side.Alliance), s => s.Kind == SightingKind.SensorArrival);
        RunAll(w, 8);
        Assert.DoesNotContain(w.Fog.Sightings(Side.Alliance), s => s.Kind == SightingKind.SensorArrival);
        Assert.Single(w.Events<SensorReport>()); // the event stays
        Assert.Equal(r.Day, w.Events<SensorReport>()[0].Day);
    }

    // m5-fog-and-hq SS19: route-only: a Sensor Station ordered for a system on no route is rejected NotOnRoute (CheckPlace agrees);
    // other facilities may still go there
    [Fact]
    public void SS19_RouteOnlyBuild()
    {
        var w = new FogWorld();
        w.H.Fleets.SetOwner(Ilum, Side.Empire);
        w.H.Economy.AddFacility(R1, FacilityKind.ConstructionYard);
        w.H.Economy.SetPools(Side.Empire, 0, 500);
        w.H.Construction.RefuseOverdrawOrders = false; // B5b 2026-10-18: Map M has no mines (capacity 0); the upkeep refusal is not what this checks
        Assert.False(w.H.Data.Galaxy.OnRoute(w.H.Data.Galaxy.Get(Ilum).Index));
        Assert.Equal("NotOnRoute", w.H.Economy.CheckPlace(Ilum, FacilityKind.SensorStation));
        Assert.Null(w.H.Economy.CheckPlace(Ilum, FacilityKind.Mine));
        long seq = w.H.Construction.SubmitBuild(Side.Empire, R1, YardFamily.Construction, Cls, 1, Ilum);
        int from = w.Game.Events.Count;
        w.Step();
        Assert.Equal(RejectReason.NotOnRoute, w.Game.Events.Skip(from).OfType<OrderRejected>().Single(r => r.Sequence == seq).Reason);
        Assert.Equal(0, w.H.Economy.OfKindAt(Ilum, FacilityKind.SensorStation));
        // a route system is fine
        seq = w.H.Construction.SubmitBuild(Side.Empire, R1, YardFamily.Construction, Cls, 1, C0);
        from = w.Game.Events.Count;
        w.Step();
        Assert.DoesNotContain(w.Game.Events.Skip(from).OfType<OrderRejected>(), r => r.Sequence == seq);
        Assert.Equal(1, w.H.Economy.OfKindAt(C0, FacilityKind.SensorStation));
    }

    // m5-fog-and-hq SS20: the warning memory is saved state: a save and load mid-trip gives no second warning and the same state hash
    [Fact]
    public void SS20_MemorySurvivesSaveAndLoad()
    {
        var whole = World(null, R4);
        whole.H.Fleets.SubmitMove(Side.Empire, whole.E1, R4);
        var r = Assert.Single(RunToReport(whole));
        int end = r.ReachDay + 3;
        var first = World(null, R4);
        first.H.Fleets.SubmitMove(Side.Empire, first.E1, R4);
        first.StepTo(r.Day + 1);
        Assert.Single(Memory(first, Side.Alliance));
        var loaded = GameHost.Load(first.H.Save(), first.H.Settings, FogWorld.Data);
        Assert.Equal(first.H.StateHash(), loaded.StateHash());
        int from = loaded.Game.Events.Count;
        while (loaded.Game.Day < end) loaded.Game.Step();
        whole.StepTo(end);
        Assert.Empty(loaded.Game.Events.Skip(from).OfType<SensorReport>());
        Assert.Equal(whole.H.StateHash(), loaded.StateHash());
    }
}
