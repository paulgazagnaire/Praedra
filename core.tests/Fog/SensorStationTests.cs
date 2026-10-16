using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Fog;
using Rebellion.Core.Setup;
using static Rebellion.Core.Tests.Fog.FogWorld;

namespace Rebellion.Core.Tests.Fog;

// Sensor Station tests SS1 to SS20 of m5-fog-and-hq §N4 (Paul approved 2026-10-16), on Map M (FogWorld: chain C0 - R1 - R2 - R3 - R4;
// Empire holds C0 and R1, Alliance holds R2, R3, R4; Empire fleet E1 of 3 Imperial Star Destroyers at R1). Reports are found by stepping
// until the first SensorReport (or the cap), so the tests do not depend on a lane's travel time.
public class SensorStationTests
{
    private static readonly string Cls = FacilityKinds.ClassName(FacilityKind.SensorStation);

    private static FogWorld World(GameSettings? s = null, params string[] allianceStations)
    {
        var w = new FogWorld(s);
        foreach (var st in allianceStations) w.H.Economy.AddFacility(st, FacilityKind.SensorStation);
        return w;
    }

    private static int Send(FogWorld w, Side side, string from, string to, int ships)
    {
        int f = w.Fleet(side, from, Isd, ships);
        w.H.Fleets.SubmitMove(side, f, to);
        return f;
    }

    /// <summary>Step day by day until a SensorReport appears or <paramref name="cap"/> days have passed; then one more Step is NOT run.</summary>
    private static List<SensorReport> RunToReport(FogWorld w, int cap = 120)
    {
        for (int i = 0; i < cap && !w.Events<SensorReport>().Any(); i++) w.Step();
        return w.Events<SensorReport>();
    }

    private static void RunAll(FogWorld w, int days) { for (int i = 0; i < days; i++) w.Step(); }

    // m5-fog-and-hq SS1: built by a Construction Yard like a mine; one per planet
    [Fact]
    public void SS1_BuiltByAConstructionYardOnePerPlanet()
    {
        var w = new FogWorld();
        w.H.Economy.AddFacility(R1, FacilityKind.ConstructionYard);
        w.H.Economy.SetPools(Side.Empire, 0, 500);
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

    // m5-fog-and-hq SS2: cost 10 refined, maintenance 5, research level 0, from the data
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
        Assert.Equal(1, FogParamsDefaults().SensorRangeHops);
        Assert.False(FogParamsDefaults().AdvancedSensorEnabled);
        Assert.Equal((3, 9), (FogParamsDefaults().SensorFewMax, FogParamsDefaults().SensorSquadronMax));
    }

    private static FogParams FogParamsDefaults() => new();

    // m5-fog-and-hq SS3: a report at 1 hop: system, side, size class, day; the same Step as the arrival
    [Fact]
    public void SS3_ReportAtOneHop()
    {
        var w = World(null, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal((R2, Side.Empire, SensorSize.Few, Side.Alliance), (r.System, r.FleetSide, r.Size, r.Viewer));
        Assert.Equal(w.Game.Day, r.Day); // stamp K + 1 = the day the Step produces
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R2 && a.Day == r.Day);
        var s = Assert.Single(w.Fog.Sightings(Side.Alliance), x => x.Kind == SightingKind.SensorArrival);
        Assert.Equal((R2, Side.Empire, SensorSize.Few, null), (s.System, s.Side, s.Size, (object?)s.Detail));
        Assert.Equal(r.Day, s.Day);
        Assert.Single(w.Fog.ViewFor(Side.Alliance).SensorReports());
        // the sensor does not explore or reveal anything about R2's neighbourhood: R4 stays unknown to the Empire
        Assert.Equal(KnowledgeLevel.Unknown, w.Fog.Level(Side.Empire, R4));
    }

    // m5-fog-and-hq SS4: none at 2 hops
    [Fact]
    public void SS4_NoReportAtTwoHops()
    {
        var w = World(null, R4);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        RunToReport(w, 100);
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R2);
        Assert.Empty(w.Events<SensorReport>());
        Assert.DoesNotContain(w.Fog.Sightings(Side.Alliance), x => x.Kind == SightingKind.SensorArrival);
    }

    // m5-fog-and-hq SS5: none at the station's own system (that is live access)
    [Fact]
    public void SS5_NoReportAtTheStationsOwnSystem()
    {
        var w = World(null, R2);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        for (int i = 0; i < 100 && !w.Events<FleetArrived>().Any(a => a.System == R2); i++) w.Step();
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R2);
        Assert.Empty(w.Events<SensorReport>());
        Assert.Contains(w.Fog.Sightings(Side.Alliance), x => x.Kind == SightingKind.Fleet && x.System == R2); // live access: exact detail
    }

    // m5-fog-and-hq SS6: none for the owner's own fleets
    [Fact]
    public void SS6_NoReportForOwnFleets()
    {
        var w = World(null, R3);
        Send(w, Side.Alliance, R4, R2, 2); // flies through R3, the station's own system, and on to R2, one hop away
        RunAll(w, 60);
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R2);
        Assert.Empty(w.Events<SensorReport>());
    }

    // m5-fog-and-hq SS7: a passing fleet is reported where it passes (1 hop), and not 2 hops away
    [Fact]
    public void SS7_PassingFleetIsReported()
    {
        var w = World(null, R4);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal(R3, r.System); // R2 is two hops from the station and gave nothing
        Assert.Contains(w.Events<FleetPassed>(), p => p.System == R3 && p.Day == r.Day);
        Assert.Equal(SensorSize.Few, r.Size);
    }

    // m5-fog-and-hq SS7b: a passing fleet is reported even when passing fleets do not explore (PassingFleetsExplore off)
    [Fact]
    public void SS7b_PassingFleetIsReportedWhateverPassingExploreSays()
    {
        var w = World(new GameSettings { Fog = new FogParams { PassingFleetsExplore = false } }, R4);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4);
        Assert.Equal(R3, Assert.Single(RunToReport(w)).System);
        Assert.Empty(w.Fog.Sightings(Side.Alliance).Where(s => s.Kind == SightingKind.Fleet));
    }

    // m5-fog-and-hq SS8: a fleet stopped by a forced stop is reported at the system where it stops
    [Fact]
    public void SS8_ForcedStopFleetIsReported()
    {
        var w = World(null, R4);
        w.Fleet(Side.Alliance, R3, "Interdictor Cruiser", 1); // a gravity well at R3 pulls the passing enemy fleet out (DEFAULT GravityWellShip, movement-redesign-draft §B)
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R4);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal(R3, r.System);
        Assert.Contains(w.Events<ForcedStop>(), f => f.System == R3 && f.Day == r.Day);
        int reports = 1;
        RunAll(w, 5); // sitting still after the stop gives nothing more (arrivals only)
        Assert.Equal(reports, w.Events<SensorReport>().Count);
    }

    // m5-fog-and-hq SS9: size classes at the thresholds (Few 1..3, Squadron 4..9, Large 10 and over)
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
        Send(w, Side.Empire, R1, R2, ships);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal(expected, r.Size);
    }

    // m5-fog-and-hq SS9b: the thresholds are data (FogParams), not code
    [Fact]
    public void SS9b_ThresholdsAreSwitches()
    {
        var w = World(new GameSettings { Fog = new FogParams { SensorFewMax = 1, SensorSquadronMax = 2 } }, R3);
        Send(w, Side.Empire, R1, R2, 3);
        Assert.Equal(SensorSize.Large, Assert.Single(RunToReport(w)).Size);
    }

    // m5-fog-and-hq SS10: two stations see one arrival: one report, one log entry (merged per side, system, day); two fleets merge their totals
    [Fact]
    public void SS10_TwoStationsAndTwoFleetsMergeIntoOneReport()
    {
        var w = World(null, R2, R4);
        Send(w, Side.Empire, R1, R3, 3);
        Send(w, Side.Empire, R1, R3, 3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R3); // three fleets of 3: 9, the top of Squadron
        var r = RunToReport(w).Where(x => x.System == R3).ToList();
        Assert.Single(r);
        Assert.Equal(SensorSize.Squadron, r[0].Size);
        Assert.Single(w.Fog.Sightings(Side.Alliance), x => x.Kind == SightingKind.SensorArrival && x.System == R3);
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
        Send(w, Side.Empire, R1, R2, 2);
        var r = Assert.Single(RunToReport(w));
        Assert.Equal((R2, Side.Alliance), (r.System, r.Viewer));
        Assert.Equal(1, w.H.Economy.OfKindAt(R3, FacilityKind.SensorStation)); // not destroyed by the blockade
    }

    // m5-fog-and-hq SS12: a captured station reports for the new holder, not the old one
    [Fact]
    public void SS12_CapturedStationReportsForTheNewHolder()
    {
        var w = World(null, R3);
        w.H.Control.SubmitDebugSetHolder(Side.Empire, R3, Side.Empire);
        w.Step();
        Assert.Equal(Side.Empire, w.H.Fleets.OwnerOf(R3));
        Send(w, Side.Alliance, R4, R2, 1); // not through R3's neighbours of interest: R4 -> R3 -> R2
        var reports = RunToReport(w);
        RunAll(w, 2);
        reports = w.Events<SensorReport>();
        Assert.NotEmpty(reports);
        Assert.All(reports, x => Assert.Equal((Side.Empire, Side.Alliance), (x.Viewer, x.FleetSide)));
        Assert.DoesNotContain(reports, x => x.Viewer == Side.Alliance);
        Assert.Empty(w.Fog.Sightings(Side.Alliance).Where(s => s.Kind == SightingKind.SensorArrival));
        Assert.Contains(w.Fog.Sightings(Side.Empire), s => s.Kind == SightingKind.SensorArrival && s.System == R2);
    }

    // m5-fog-and-hq SS13: Classic switches the sensor effect off
    [Fact]
    public void SS13_ClassicGivesNoReport()
    {
        var w = World(new GameSettings { Fog = new FogParams { Ruleset = FogRuleset.Classic } }, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        RunToReport(w, 100);
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R2);
        Assert.Empty(w.Events<SensorReport>());
        Assert.DoesNotContain(w.Fog.Sightings(Side.Alliance), x => x.Kind == SightingKind.SensorArrival);
    }

    // m5-fog-and-hq SS14: a station still queued or in delivery gives no report
    [Fact]
    public void SS14_IncompleteStationGivesNoReport()
    {
        var w = new FogWorld();
        w.H.Economy.AddFacility(R3, FacilityKind.SensorStation, completed: false);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        RunToReport(w, 100);
        Assert.Contains(w.Events<FleetArrived>(), a => a.System == R2);
        Assert.Empty(w.Events<SensorReport>());
    }

    // m5-fog-and-hq SS15: same seed, same orders, same everything (events, both knowledge hashes, state hash)
    [Fact]
    public void SS15_Determinism()
    {
        List<string> Run()
        {
            var w = World(null, R3);
            w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
            Send(w, Side.Empire, R1, R3, 12);
            RunAll(w, 70);
            return w.Game.Events.Select(e => e.ToString()).Append(w.Fog.KnowledgeHash(Side.Empire)).Append(w.Fog.KnowledgeHash(Side.Alliance)).Append(w.H.StateHash()).ToList();
        }
        var a = Run(); var b = Run();
        Assert.Equal(a, b);
        Assert.Contains(a, e => e.Contains(nameof(SensorReport)));
    }

    // m5-fog-and-hq SS16: fairness. Only the station's owner is told; the other side's view, log and feed hold nothing of it
    [Fact]
    public void SS16_OnlyTheOwnersSideIsTold()
    {
        var w = World(null, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        RunToReport(w);
        RunAll(w, 1);
        Assert.NotEmpty(w.Fog.ViewFor(Side.Alliance).SensorReports());
        Assert.NotEmpty(w.Fog.EventsFor(Side.Alliance).OfType<SensorReport>());
        Assert.Empty(w.Fog.ViewFor(Side.Empire).SensorReports());
        Assert.Empty(w.Fog.EventsFor(Side.Empire).OfType<SensorReport>());
        Assert.Empty(w.Fog.ViewFor(Side.Empire).Events.OfType<SensorReport>());
    }

    // m5-fog-and-hq SS17: the Advanced tier is off and cannot be switched on yet; range is a data value
    [Fact]
    public void SS17_AdvancedTierIsNotBuiltAndRangeIsData()
    {
        Assert.Throws<NotSupportedException>(() => new FogWorld(new GameSettings { Fog = new FogParams { AdvancedSensorEnabled = true } }));
        var w = World(new GameSettings { Fog = new FogParams { SensorRangeHops = 2 } }, R4);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        Assert.Equal(R2, Assert.Single(RunToReport(w)).System); // two hops from R4 with the range value 2
    }

    // m5-fog-and-hq SS18: the log entry is pruned after the observed window like the others
    [Fact]
    public void SS18_SensorEntryIsPrunedWithTheWindow()
    {
        var w = World(new GameSettings { Fog = new FogParams { ObservedWindowDays = 5 } }, R3);
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        var r = Assert.Single(RunToReport(w));
        Assert.Contains(w.Fog.Sightings(Side.Alliance), s => s.Kind == SightingKind.SensorArrival);
        RunAll(w, 8);
        Assert.DoesNotContain(w.Fog.Sightings(Side.Alliance), s => s.Kind == SightingKind.SensorArrival);
        Assert.Single(w.Events<SensorReport>()); // the event stays
        Assert.Equal(r.Day, w.Events<SensorReport>()[0].Day);
    }
}
