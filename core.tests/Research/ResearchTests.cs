using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Data;
using Praedra.Core.Economy;
using Praedra.Core.Research;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Construction;

namespace Praedra.Core.Tests.Research;

// Rule sheet: m5-research (research and tech unlocks). Each test names its test id (RS1..RS36).
public class ResearchTests
{
    private const Side A = Side.Alliance, E = Side.Empire;
    private const ResearchKind Ship = ResearchKind.Ship, Troop = ResearchKind.Troop, Facility = ResearchKind.Facility;
    private const string Yard1 = "Charter", Yard2 = "Classis", Yard3 = "Navalia", Yard4 = "Caminus", Enemy = "Salus", Neutral = "Fiscus";

    private static ResearchWorld AllianceShipyard()
    {
        var w = new ResearchWorld();
        w.Hold(Yard1, A);
        w.Fac(Yard1, FacilityKind.OrbitalShipyard);
        return w;
    }

    private static List<string> BuildableShips(GameHost h, Side side) =>
        h.Economy.Costs.Rows.Where(r => r.Category is ObjectCategory.CapitalShip or ObjectCategory.Fighter && r.Cost > 0
                && (side == Side.Empire ? r.Empire : r.Alliance) && h.Construction.IsResearched(side, r.Name))
            .Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    // m5-research RS1
    [Fact]
    public void RS1_AtGameStartEachSideCanBuildEightShipClasses()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        Assert.True(h.Construction.RespectResearchOrder);
        Assert.Equal(new[] { "Barge", "Dodger", "Grudge", "Hauler", "Mule",
            "Nest", "Plough", "Swift" }, BuildableShips(h, A));
        Assert.Equal(new[] { "Bond", "Death Star", "Decree", "Gleaner", "Mandate",
            "Statute", "Tender", "Warrant" }, BuildableShips(h, E));
        foreach (var c in new[] { "Mender", "Needle" }) Assert.DoesNotContain(c, BuildableShips(h, A));
        Assert.DoesNotContain("Edict", BuildableShips(h, E));
        // every side can build something in every family at the MedA start (troops and facilities at level 0)
        foreach (var side in new[] { A, E })
        {
            Assert.Contains(h.Economy.Costs.Rows, r => r.Category is ObjectCategory.Regiment && (side == E ? r.Empire : r.Alliance) && h.Construction.IsResearched(side, r.Name));
            Assert.True(h.Construction.IsResearched(side, "Mine"));
            Assert.True(h.Construction.IsResearched(side, "Orbital Shipyard"));
        }
    }

    // m5-research RS2
    [Fact]
    public void RS2_NebulonBAtLevelZeroIsNotResearched() =>
        Assert.Equal(RejectReason.NotResearched, AllianceShipyard().TryOrder(A, Yard1, YardFamily.Shipyard, "Mender"));

    // m5-research RS3
    [Fact]
    public void RS3_SwitchOffIsAllAvailable()
    {
        var w = AllianceShipyard();
        w.Build.RespectResearchOrder = false;
        Assert.Null(w.TryOrder(A, Yard1, YardFamily.Shipyard, "Mender"));
    }

    // m5-research RS4
    [Fact]
    public void RS4_LevelOneOpensNebulonBButNotMonCalamari()
    {
        var w = AllianceShipyard();
        w.Research.SetLevel(A, Ship, 1);
        Assert.Null(w.TryOrder(A, Yard1, YardFamily.Shipyard, "Mender"));
        Assert.Equal(RejectReason.NotResearched, w.TryOrder(A, Yard1, YardFamily.Shipyard, "Ark"));
    }

    // m5-research RS5
    [Fact]
    public void RS5_AGrantOverridesTheLevel()
    {
        var w = AllianceShipyard();
        w.Build.GrantResearch(A, "Ark");
        Assert.Null(w.TryOrder(A, Yard1, YardFamily.Shipyard, "Ark"));
        Assert.Equal(RejectReason.NotResearched, w.TryOrder(A, Yard1, YardFamily.Shipyard, "Wasp"));
    }

    // m5-research RS6
    [Fact]
    public void RS6_ZeroTotalAddsNothing()
    {
        var w = new ResearchWorld();
        w.Research.SetPoints(A, Ship, 11);
        Assert.Equal(0, w.Add(A, Ship, 0));
        Assert.Empty(w.Events<ResearchLevelReached>());
        Assert.Equal(0, w.Research.Level(A, Ship));
        Assert.Equal(11, w.Research.Points(A, Ship));
    }

    // m5-research RS7
    [Fact]
    public void RS7_TwelvePointsReachShipLevelOne()
    {
        var w = new ResearchWorld();
        Assert.Equal(1, w.Add(A, Ship, 12));
        Assert.Equal(1, w.Research.Level(A, Ship));
        Assert.Equal(0, w.Research.Points(A, Ship));
        var ev = Assert.Single(w.Events<ResearchLevelReached>());
        Assert.Equal((A, Ship, 1), (ev.Side, ev.ResearchKind, ev.Level));
        Assert.Equal(new[] { "Mender" }, ev.Classes.Items);
    }

    // m5-research RS8
    [Fact]
    public void RS8_ElevenPointsAreNotEnough()
    {
        var w = new ResearchWorld();
        Assert.Equal(0, w.Add(A, Ship, 11));
        Assert.Equal(0, w.Research.Level(A, Ship));
        Assert.Equal(11, w.Research.Points(A, Ship));
        Assert.Empty(w.Events<ResearchLevelReached>());
    }

    // m5-research RS9
    [Fact]
    public void RS9_TheEmptyFacilityRungThreeIsSkipped()
    {
        var w = new ResearchWorld();
        Assert.Equal(3, w.Add(A, Facility, 104));
        Assert.Equal(4, w.Research.Level(A, Facility));
        Assert.Equal(0, w.Research.Points(A, Facility));
        Assert.Equal(new[] { 1, 2, 4 }, w.Events<ResearchLevelReached>().Select(e => e.Level));
        Assert.Equal(new[] { "Mordant Covenant II" }, w.Events<ResearchLevelReached>().Last().Classes.Items);
    }

    // m5-research RS10
    [Fact]
    public void RS10_OnePointShortStopsAtLevelTwo()
    {
        var w = new ResearchWorld();
        w.Add(A, Facility, 103);
        Assert.Equal(2, w.Research.Level(A, Facility));
        Assert.Equal(39, w.Research.Points(A, Facility));
        Assert.DoesNotContain(w.Events<ResearchLevelReached>(), e => e.Level == 4);
    }

    // m5-research RS11
    [Fact]
    public void RS11_EmpireTroopsPayRungOneOnly()
    {
        var w = new ResearchWorld();
        w.Add(E, Troop, 111);
        Assert.Equal(1, w.Research.Level(E, Troop));
        Assert.Equal(65, w.Research.Points(E, Troop));
        Assert.False(w.Research.Exhausted(E, Troop));
    }

    // m5-research RS12
    [Fact]
    public void RS12_EmpireTroopLadderIsExhausted()
    {
        var w = new ResearchWorld();
        w.Add(E, Troop, 112);
        Assert.Equal(2, w.Research.Level(E, Troop));
        Assert.Equal(0, w.Research.Points(E, Troop));
        Assert.True(w.Research.Exhausted(E, Troop));
    }

    // m5-research RS13
    [Fact]
    public void RS13_AllianceTroopLadderClimbsThreeRungs()
    {
        var w = new ResearchWorld();
        Assert.Equal(3, w.Add(A, Troop, 112));
        Assert.Equal(new[] { 1, 2, 3 }, w.Events<ResearchLevelReached>().Select(e => e.Level));
        Assert.Equal(0, w.Research.Points(A, Troop));
        Assert.True(w.Research.Exhausted(A, Troop));
    }

    // m5-research RS14
    [Fact]
    public void RS14_AfterExhaustionPointsAccumulateAndMissionsAreRefused()
    {
        var w = new ResearchWorld();
        w.Add(A, Troop, 112);
        int events = w.Events<ResearchLevelReached>().Count;
        Assert.Equal(0, w.Add(A, Troop, 50));
        Assert.Equal(50, w.Research.Points(A, Troop));
        Assert.Equal(events, w.Events<ResearchLevelReached>().Count);
        w.Hold(Yard1, A); w.Fac(Yard1, FacilityKind.TrainingFacility);
        Assert.Equal(RejectReason.NothingToResearch, w.Research.CanResearch(A, Troop, Yard1));
        Assert.Equal(RejectReason.NothingToResearch, w.Research.CanResearch(A, Troop, Neutral));
    }

    // m5-research RS15
    [Fact]
    public void RS15_FullShipLaddersReachLevelEleven()
    {
        var w = new ResearchWorld();
        w.Add(A, Ship, 648);
        w.Add(E, Ship, 604);
        foreach (var s in new[] { A, E })
        {
            Assert.Equal(11, w.Research.Level(s, Ship));
            Assert.Equal(0, w.Research.Points(s, Ship));
            Assert.True(w.Research.Exhausted(s, Ship));
        }
    }

    // m5-research RS16
    [Fact]
    public void RS16_DeathStarShieldIsEmpireRungThree()
    {
        var w = new ResearchWorld();
        w.Add(E, Facility, 24 + 40);
        Assert.Equal(2, w.Research.Level(E, Facility));
        Assert.False(w.Build.IsResearched(E, "Death Star Shield"));
        w.Add(E, Facility, 24);
        Assert.Equal(3, w.Research.Level(E, Facility));
        Assert.True(w.Build.IsResearched(E, "Death Star Shield"));
        w.Hold(Yard1, E); w.Fac(Yard1, FacilityKind.ConstructionYard);
        Assert.Null(w.TryOrder(E, Yard1, YardFamily.Construction, "Death Star Shield", 1, Yard1));
        w.Hold(Yard2, A); w.Fac(Yard2, FacilityKind.ConstructionYard);
        w.Research.SetLevel(A, Facility, 6);
        Assert.Equal(RejectReason.NotAvailable, w.TryOrder(A, Yard2, YardFamily.Construction, "Death Star Shield", 1, Yard2));
    }

    // m5-research RS17
    [Fact]
    public void RS17_SystemCountScalesTheCost()
    {
        var p = new ResearchParams { ResearchCostScale = ResearchScale.SystemCount };
        var w = new ResearchWorld(p); w.Arm(362);
        Assert.Equal(362, w.Research.CostPercent);
        Assert.Equal((1, 43, "Mender"), w.Research.NextRung(A, Ship));
        w.Add(A, Ship, 43);
        Assert.Equal((1, 0), (w.Research.Level(A, Ship), w.Research.Points(A, Ship)));
        var w2 = new ResearchWorld(p); w2.Arm(362);
        w2.Add(A, Ship, 42);
        Assert.Equal(0, w2.Research.Level(A, Ship));
        // without a count given, systems in play = every system of the loaded galaxy
        var w3 = new ResearchWorld(p); w3.Arm();
        Assert.Equal(TestData.RealGalaxy.Systems.Count, w3.Research.CostPercent);
    }

    // m5-research RS18
    [Fact]
    public void RS18_FixedPercentScalesTheCost()
    {
        var p = new ResearchParams { ResearchCostScale = ResearchScale.Fixed, ResearchCostPercentFixed = 200 };
        var w = new ResearchWorld(p); w.Arm();
        w.Add(A, Ship, 24);
        Assert.Equal((1, 0), (w.Research.Level(A, Ship), w.Research.Points(A, Ship)));
        var w2 = new ResearchWorld(p); w2.Arm();
        w2.Add(A, Ship, 23);
        Assert.Equal(0, w2.Research.Level(A, Ship));
    }

    // m5-research RS19
    [Fact]
    public void RS19_MissionPointsAreOneToSevenUniform()
    {
        var w = new ResearchWorld(7UL); w.Arm();
        var counts = new int[8];
        long sum = 0; const int N = 20_000;
        long before = 0, after = 0;
        w.StepWith(ctx =>
        {
            before = ctx.Rng.Draws;
            for (int i = 0; i < N; i++) { int p = w.Research.RollMissionPoints(ctx); counts[p]++; sum += p; }
            after = ctx.Rng.Draws;
        });
        Assert.Equal(0, counts[0]);
        Assert.Equal(N, counts.Skip(1).Sum());
        for (int v = 1; v <= 7; v++) Assert.InRange(counts[v] * 100.0 / N, 14.29 - 1.0, 14.29 + 1.0);
        Assert.InRange(sum / (double)N, 3.95, 4.05);
        Assert.True(after - before >= N); // one NextInt per call (rejection sampling may take more than one raw draw)
    }

    // m5-research RS20
    [Fact]
    public void RS20_MissionPointsScale()
    {
        Assert.Equal(6, ResearchModule.ScaleMissionPoints(3, 200));
        Assert.Equal(2, ResearchModule.ScaleMissionPoints(1, 200));
        Assert.Equal(3, ResearchModule.ScaleMissionPoints(1, 362));
        // through the module: Fixed 200 and an injected draw of 2 (base 3) gives 6
        var w = new ResearchWorld(new ResearchParams { MissionPointsScale = ResearchScale.Fixed, MissionPointsPercentFixed = 200 });
        w.Arm();
        w.Stub!.Expect(0, 6, 2, 0);
        int a = 0, b = 0;
        w.StepWith(ctx => { a = w.Research.RollMissionPoints(ctx); b = w.Research.RollMissionPoints(ctx); });
        Assert.Equal((6, 2), (a, b));
        var w2 = new ResearchWorld(new ResearchParams { MissionPointsScale = ResearchScale.SystemCount });
        w2.Arm(362);
        w2.Stub!.Expect(0, 6, 0);
        int c = 0;
        w2.StepWith(ctx => c = w2.Research.RollMissionPoints(ctx));
        Assert.Equal(3, c);
    }

    // m5-research RS21
    [Fact]
    public void RS21_SetupArmsBothTicksAllianceFirst()
    {
        var w = new ResearchWorld();
        w.Stub!.Queue(5, 17);
        w.Arm();
        Assert.Equal(new[] { (0, 30), (0, 30) }, w.Stub.Requests);
        Assert.Equal(35, w.Research.TickDue(A));
        Assert.Equal(47, w.Research.TickDue(E));
        foreach (ulong seed in new ulong[] { 1, 2, 3, 99 })
        {
            var s = new ResearchWorld(seed);
            long d0 = s.Game.Rng.Draws;
            s.Arm();
            Assert.True(s.Game.Rng.Draws - d0 >= 2);
            foreach (var side in new[] { A, E }) Assert.InRange(s.Research.TickDue(side), 30, 60);
        }
        // a new game arms both ticks too
        var h = NewGame.Create(new NewGameOptions { Seed = 4 });
        foreach (var side in new[] { A, E }) Assert.InRange(h.Research.TickDue(side), 30, 60);
    }

    private static ResearchWorld Rs22World(ResearchParams? p = null)
    {
        var w = new ResearchWorld(p);
        foreach (var s in new[] { Yard1, Yard2, Yard3, Yard4 }) w.Hold(s, A);
        w.Hold(Enemy, E);
        for (int i = 0; i < 3; i++) w.Fac(Yard1, FacilityKind.OrbitalShipyard);
        w.Fac(Yard2, FacilityKind.AdvancedShipyard);
        w.Fac(Yard2, FacilityKind.TrainingFacility); w.Fac(Yard3, FacilityKind.TrainingFacility);
        w.Fac(Yard3, FacilityKind.ConstructionYard); w.Fac(Yard4, FacilityKind.AdvancedConstructionYard);
        for (int i = 0; i < 4; i++) w.Fac(Yard4, FacilityKind.Mine);
        w.Stub!.Expect(0, 30, 0, 30, 7, 9); // arming: Alliance 30, Empire 60; then Alliance re-arms with 7
        w.Arm();
        return w;
    }

    // m5-research RS22
    [Fact]
    public void RS22_TheTickCountsCompletedYardsPerTrack()
    {
        var w = Rs22World();
        Assert.Equal(30, w.Research.TickDue(A));
        w.RunTo(29);
        Assert.Equal(0, w.Research.Points(A, Ship));
        int before = w.Stub!.Requests.Count(r => r == (0, 30));
        w.Run(1); // the Step whose hook time is T = 30
        Assert.Equal(4, w.Research.Points(A, Ship));
        Assert.Equal(2, w.Research.Points(A, Troop));
        Assert.Equal(2, w.Research.Points(A, Facility));
        Assert.Equal(before + 1, w.Stub.Requests.Count(r => r == (0, 30))); // one re-arming draw
        Assert.Equal(30 + 30 + 7, w.Research.TickDue(A));
        Assert.Equal(new[] { 4, 2, 2 }, w.Research.CountYards(A));
    }

    // m5-research RS23
    [Fact]
    public void RS23_QueuedAndEnemyHeldYardsDoNotCount()
    {
        var w = new ResearchWorld();
        foreach (var s in new[] { Yard1, Yard2, Yard3, Yard4 }) w.Hold(s, A);
        w.Hold(Enemy, E);
        for (int i = 0; i < 2; i++) w.Fac(Yard1, FacilityKind.OrbitalShipyard);
        w.Fac(Yard1, FacilityKind.OrbitalShipyard, completed: false); // queued
        w.Fac(Yard2, FacilityKind.AdvancedShipyard);
        w.Fac(Yard2, FacilityKind.TrainingFacility); w.Fac(Enemy, FacilityKind.TrainingFacility); // one on an enemy-held system
        w.Fac(Yard3, FacilityKind.ConstructionYard); w.Fac(Yard4, FacilityKind.AdvancedConstructionYard);
        w.Fac(Neutral, FacilityKind.OrbitalShipyard); // neutral systems never count
        w.Stub!.Expect(0, 30, 0, 30);
        w.Arm();
        w.RunTo(30);
        Assert.Equal(3, w.Research.Points(A, Ship));
        Assert.Equal(1, w.Research.Points(A, Troop));
    }

    // m5-research RS24
    [Fact]
    public void RS24_ASideWithoutYardsStillReArms()
    {
        var w = new ResearchWorld();
        w.Stub!.Expect(0, 30, 0, 30, 4);
        w.Arm();
        w.RunTo(30);
        Assert.Equal(new[] { 0, 0, 0 }, ResearchModule.Kinds.Select(k => w.Research.Points(A, k)));
        Assert.Empty(w.Events<ResearchLevelReached>());
        Assert.Equal(3, w.Stub.Requests.Count(r => r == (0, 30)));
        Assert.Equal(64, w.Research.TickDue(A));
    }

    // m5-research RS25
    [Fact]
    public void RS25_BothSidesFireInOneStepAllianceFirst()
    {
        var w = new ResearchWorld();
        w.Stub!.Expect(0, 30, 0, 0, 3, 9);
        w.Arm();
        w.RunTo(30);
        Assert.Equal(63, w.Research.TickDue(A));
        Assert.Equal(69, w.Research.TickDue(E));
    }

    // m5-research RS26
    [Fact]
    public void RS26_FacilityResearchOffAddsNothingButDrawsTheSame()
    {
        var on = Rs22World();
        var off = Rs22World(new ResearchParams { FacilityResearch = false });
        on.RunTo(30); off.RunTo(30);
        Assert.Equal(new[] { 0, 0, 0 }, ResearchModule.Kinds.Select(k => off.Research.Points(A, k)));
        Assert.Equal(on.Stub!.Requests, off.Stub!.Requests);
        Assert.Equal(on.Research.TickDue(A), off.Research.TickDue(A));
    }

    // m5-research RS27
    [Fact]
    public void RS27_TwelveShipyardsFourTicks()
    {
        var w = new ResearchWorld();
        w.Hold(Yard1, A); w.Hold(Yard2, A);
        for (int i = 0; i < 6; i++) { w.Fac(Yard1, FacilityKind.OrbitalShipyard); w.Fac(Yard2, FacilityKind.OrbitalShipyard); }
        w.Stub!.Expect(0, 30, 0, 30, 0, 0, 0, 0); // every Alliance tick 30 days apart
        w.Arm();
        var expected = new[] { (1, 0), (1, 12), (2, 0), (2, 12) };
        for (int tick = 0; tick < 4; tick++)
        {
            w.RunTo(30 * (tick + 1));
            Assert.Equal(expected[tick], (w.Research.Level(A, Ship), w.Research.Points(A, Ship)));
        }
    }

    // m5-research RS32 (Paul 2026-10-16: a blockaded yard adds no research; switch BlockadeStopsResearch)
    [Fact]
    public void RS32_ABlockadedYardDoesNotCount()
    {
        var w = new ResearchWorld();
        w.Hold(Yard1, A); w.Hold(Yard2, A);
        w.Fac(Yard1, FacilityKind.OrbitalShipyard); w.Fac(Yard2, FacilityKind.OrbitalShipyard);
        Assert.Equal(2, w.Research.CountYards(A)[(int)Ship]);
        w.Fleets.AddFleet(Side.Empire, "Picket", new List<string> { "Subpoena" }, Yard1, new List<string>());
        Assert.True(w.Fleets.IsEconomicBlockade(Yard1, A));
        Assert.Equal(1, w.Research.CountYards(A)[(int)Ship]);
        var off = new ResearchWorld(ResearchWorld.Unscaled with { BlockadeStopsResearch = false });
        off.Hold(Yard1, A); off.Fac(Yard1, FacilityKind.OrbitalShipyard);
        off.Fleets.AddFleet(Side.Empire, "Picket", new List<string> { "Subpoena" }, Yard1, new List<string>());
        Assert.Equal(1, off.Research.CountYards(A)[(int)Ship]);
    }

    // m5-research RS28
    [Fact]
    public void RS28_CanResearchNeedsAMatchingYardOnAHeldSystem()
    {
        var w = new ResearchWorld();
        w.Hold(Yard1, A);
        w.Fac(Yard1, FacilityKind.TrainingFacility);
        Assert.Equal(RejectReason.NoMatchingYard, w.Research.CanResearch(A, Facility, Yard1));
        int cy = w.Fac(Yard1, FacilityKind.ConstructionYard);
        Assert.Null(w.Research.CanResearch(A, Facility, Yard1));
        w.Hold(Yard2, A); w.Fac(Yard2, FacilityKind.AdvancedConstructionYard);
        Assert.Null(w.Research.CanResearch(A, Facility, Yard2));
        w.Hold(Yard3, E); w.Fac(Yard3, FacilityKind.ConstructionYard);
        Assert.Equal(RejectReason.NoMatchingYard, w.Research.CanResearch(A, Facility, Yard3));
        w.Fac(Neutral, FacilityKind.ConstructionYard);
        Assert.Equal(RejectReason.NoMatchingYard, w.Research.CanResearch(A, Facility, Neutral));
        w.Fac(Yard4, FacilityKind.ConstructionYard, completed: false); w.Hold(Yard4, A); // a queued yard does not count
        Assert.Equal(RejectReason.NoMatchingYard, w.Research.CanResearch(A, Facility, Yard4));
        Assert.True(cy > 0);
    }

    // m5-research RS29
    [Fact]
    public void RS29_AMissionIterationAddsItsTotalOnceAtTheEnd()
    {
        var w = new ResearchWorld();
        w.Stub!.Expect(0, 6, 2, 6);
        var success = new[] { true, false, true }; // injected success rolls of members 1, 2, 3 (ascending character id)
        int total = 0, gained = -1; var pointsDuring = new List<int>();
        w.StepWith(ctx =>
        {
            foreach (var ok in success)
            {
                if (!ok) continue; // a failed member makes no call and no draw
                total += w.Research.RollMissionPoints(ctx);
                pointsDuring.Add(w.Research.Points(A, Ship));
            }
            gained = w.Research.AddMissionPoints(ctx, A, Ship, total);
        });
        Assert.Equal(10, total);
        Assert.Equal(new[] { 0, 0 }, pointsDuring); // nothing added before the iteration's end
        Assert.Equal(2, w.Stub.Requests.Count(r => r == (0, 6)));
        Assert.Equal(0, gained); // 10 < 12
        Assert.Equal(10, w.Research.Points(A, Ship));
        // MissionResearch off: the call adds nothing
        var off = new ResearchWorld(new ResearchParams { MissionResearch = false });
        Assert.Equal(0, off.Add(A, Ship, 50));
        Assert.Equal(0, off.Research.Points(A, Ship));
    }

    // m5-research RS30
    [Fact]
    public void RS30_ALevelGainedAtSlot69IsSeenByOrdersFromTheNextStep()
    {
        var w = AllianceShipyard();
        w.Research.SetPoints(A, Ship, 11);
        w.Stub!.Expect(0, 30, 0, 30);
        w.Arm(); // Alliance tick at T = 30, in the Step whose K = 29
        w.RunTo(29);
        Assert.Equal(RejectReason.NotResearched, w.TryOrder(A, Yard1, YardFamily.Shipyard, "Mender")); // step 1 runs before 6.9
        Assert.Equal(1, w.Research.Level(A, Ship)); // reached at 6.9 of that same Step
        Assert.Equal(29, w.Events<ResearchLevelReached>().Single().Day);
        Assert.Null(w.TryOrder(A, Yard1, YardFamily.Shipyard, "Mender"));
    }

    private static List<GameEvent> NoIndex(IEnumerable<GameEvent> ev) => ev.Select(e => e with { Index = 0 }).ToList();

    // m5-research RS31
    [Fact]
    public void RS31_SaveAndLoadGiveTheSameFuture()
    {
        ResearchWorld Make()
        {
            var w = new ResearchWorld(11UL);
            foreach (var s in new[] { Yard1, Yard2, Yard3, Yard4 }) w.Hold(s, A);
            w.Hold(Enemy, E);
            for (int i = 0; i < 12; i++) w.Fac(Yard1, FacilityKind.OrbitalShipyard);
            for (int i = 0; i < 6; i++) { w.Fac(Yard2, FacilityKind.TrainingFacility); w.Fac(Yard3, FacilityKind.ConstructionYard); w.Fac(Enemy, FacilityKind.OrbitalShipyard); }
            w.Arm();
            return w;
        }
        var whole = Make(); var split = Make();
        whole.Run(40); split.Run(40);
        split.Reload();
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(split.Game));
        int wFrom = whole.Game.Events.Count;
        whole.Run(100); split.Run(100);
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(split.Game));
        Assert.Equal(whole.Game.Rng.Draws, split.Game.Rng.Draws);
        Assert.Equal(NoIndex(whole.Game.Events.Skip(wFrom)), NoIndex(split.Game.Events));
        Assert.NotEmpty(whole.Events<ResearchLevelReached>()); // research did move in the window
        Assert.True(whole.Research.Level(A, Ship) >= 1);
    }

    // m5-research RS32
    [Fact]
    public void RS32_SameSeedSameGame()
    {
        GameHost Play()
        {
            var h = NewGame.Create(new NewGameOptions { Seed = 5 });
            for (int i = 0; i < 400; i++) h.Game.Step();
            return h;
        }
        var a = Play(); var b = Play();
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.Equal(a.Game.Rng.Draws, b.Game.Rng.Draws);
        Assert.Equal(a.Game.Events.Select(e => e.ToString()), b.Game.Events.Select(e => e.ToString()));
        Assert.Equal(a.Game.Events.OfType<ResearchLevelReached>(), b.Game.Events.OfType<ResearchLevelReached>());
        Assert.NotEmpty(a.Game.Events.OfType<ResearchLevelReached>()); // the Empire's nine yards climb the ladders
        // ladders are lists sorted by order then catalogue row, never a dictionary order
        foreach (var side in new[] { A, E })
            foreach (var k in ResearchModule.Kinds)
            {
                var l = a.Research.Ladder(side, k);
                Assert.Equal(l.OrderBy(r => r.Order).ThenBy(r => r.Catalogue), l);
            }
    }

    // m5-research RS33
    [Fact]
    public void RS33_SensorTier2OnWithoutADifficultyRefusesToStart()
    {
        var ex = Assert.Throws<DataLoadException>(() => new ResearchWorld(new ResearchParams { SensorTier2Research = true }));
        Assert.Contains("Advanced Sensor Station", ex.Message);
        var w = new ResearchWorld();
        Assert.Null(w.Research.KindOf(ResearchData.AdvancedSensorStation));
        Assert.Equal(6, w.Research.Ladder(A, Facility).Max(r => r.Order));
        Assert.Equal(204, w.Research.Ladder(A, Facility).Sum(r => r.Difficulty));
        Assert.Equal(228, w.Research.Ladder(E, Facility).Sum(r => r.Difficulty));
        // the other totals of the sheet's Numbers table
        Assert.Equal(648, w.Research.Ladder(A, Ship).Sum(r => r.Difficulty));
        Assert.Equal(604, w.Research.Ladder(E, Ship).Sum(r => r.Difficulty));
        Assert.Equal(112, w.Research.Ladder(A, Troop).Sum(r => r.Difficulty));
        Assert.Equal(112, w.Research.Ladder(E, Troop).Sum(r => r.Difficulty));
    }

    // m5-research RS34
    [Fact]
    public void RS34_SensorTier2RungSeven()
    {
        var w = new ResearchWorld(new ResearchParams { SensorTier2Research = true, SensorTier2Difficulty = 30 });
        w.Research.SetLevel(A, Facility, 6);
        Assert.Equal((7, 30, ResearchData.AdvancedSensorStation), w.Research.NextRung(A, Facility));
        Assert.False(w.Research.OrderReached(A, ResearchData.AdvancedSensorStation));
        w.Add(A, Facility, 29);
        Assert.Equal(6, w.Research.Level(A, Facility));
        w.Add(A, Facility, 1);
        Assert.Equal(7, w.Research.Level(A, Facility));
        Assert.True(w.Research.OrderReached(A, ResearchData.AdvancedSensorStation));
        Assert.Equal(new[] { ResearchData.AdvancedSensorStation }, w.Events<ResearchLevelReached>().Single().Classes.Items);
        // scaled by the percent
        var s = new ResearchWorld(new ResearchParams { SensorTier2Research = true, SensorTier2Difficulty = 30, ResearchCostScale = ResearchScale.Fixed, ResearchCostPercentFixed = 200 });
        s.Arm();
        s.Research.SetLevel(A, Facility, 6);
        Assert.Equal(60, s.Research.NextRung(A, Facility)!.Value.Cost);
    }

    private static ResearchData Modified(Func<string, string> edit)
    {
        var lines = TestData.MergedText(ResearchData.LadderFileName).Split('\n').Select(edit);
        return new ResearchData(ResearchData.ParseLadder(string.Join("\n", lines), "modified.csv", null), ResearchWorld.RealResearch.Value.Rules);
    }

    // m5-research RS35
    [Fact]
    public void RS35_OneClassPerRungDataCheck()
    {
        // Hauler (an Alliance capital ship listed before the Needle) moved to rung 3 with difficulty 99
        var data = Modified(l => l.StartsWith("0x28 ship,capital ship,65,Hauler,", StringComparison.Ordinal) ? "0x28 ship,capital ship,65,Hauler,1,0,30,3,99,locked,n/a" : l);
        var ex = Assert.Throws<DataLoadException>(() => new ResearchWorld(data: data));
        Assert.Contains("rung 3", ex.Message);
        var w = new ResearchWorld(new ResearchParams { OneClassPerRung = false }, data);
        w.Research.SetLevel(A, Ship, 2);
        Assert.Equal((3, 99, "Hauler"), w.Research.NextRung(A, Ship)); // the first in file order sets the cost
    }

    // m5-research RS36 (m2-construction C18)
    [Fact]
    public void RS36_AdvancedShipyardNeedsFacilityLevelFive()
    {
        var w = new ResearchWorld();
        w.Hold(Yard1, E); w.Fac(Yard1, FacilityKind.ConstructionYard);
        Assert.Equal(RejectReason.NotResearched, w.TryOrder(E, Yard1, YardFamily.Construction, "Advanced Shipyard", 1, Yard1));
        w.Research.SetLevel(E, Facility, 5);
        Assert.Null(w.TryOrder(E, Yard1, YardFamily.Construction, "Advanced Shipyard", 1, Yard1));
    }

    // m5-research §Ladders: the ladder file and the costs file agree on every research order; a mismatch refuses to load
    [Fact]
    public void LadderMustMatchTheCostsFile()
    {
        var text = TestData.MergedText(ResearchData.LadderFileName)
            .Replace("0x28 ship,capital ship,67,Mender,1,0,34,1,12", "0x28 ship,capital ship,67,Mender,1,0,34,2,12");
        var ex = Assert.Throws<DataLoadException>(() => ResearchData.ParseLadder(text, "bad.csv", BuildWorld.RealCosts.Value));
        Assert.Contains("Mender", ex.Message);
        Assert.Equal(BuildWorld.RealCosts.Value.Rows.Count(), ResearchWorld.RealResearch.Value.Rows.Count);
    }

    // m5-research §Rule (Classic) and §Facility income: research draws only at setup and at tick firings, and an unarmed game never ticks
    [Fact]
    public void AnUnarmedGameNeverTicks()
    {
        var w = new ResearchWorld(3UL);
        w.Hold(Yard1, A); w.Fac(Yard1, FacilityKind.OrbitalShipyard);
        long d = w.Game.Rng.Draws;
        w.Run(200);
        Assert.Equal(d, w.Game.Rng.Draws);
        Assert.Equal(0, w.Research.Points(A, Ship));
    }
}
