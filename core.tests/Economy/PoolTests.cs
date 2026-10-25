using Praedra.Core.Clock;
using Praedra.Core.Economy;

namespace Praedra.Core.Tests.Economy;

// Tests of m2-mines-refineries-pools (ids P1 to P22). Hook time T = K + 1; events carry Day = T.
// Setup "S1": Empire, one system at support 100, one mine (id 1) and one refinery (id 2), both completed and idle at Day 0,
// load 0, stockpile 0 / 0. Stub draws return 0 unless stated.
public class PoolTests
{
    private const string Home = "Charter";

    private static EcoWorld SetupS1(int support = 100, bool freeShips = false)
    {
        var w = new EcoWorld(freeShips: freeShips);
        w.Hold(Home, Side.Empire, support);
        w.Mine(Home, id: 1);
        w.Refinery(Home, id: 2);
        return w;
    }

    private static int[] RefinedDays(EcoWorld w) =>
        w.Events<ResourceProduced>().Where(e => e.Resource == "Refined").Select(e => e.Day).ToArray();

    // m2-mines-refineries-pools P1
    [Fact]
    public void P1_FirstCyclesAndRefinedArrivalDays()
    {
        var w = SetupS1();
        w.Run(1);
        Assert.Equal(3, w.E.Objects[1].Due); // first cycle 0 + 5/2 = 2 days, due 3
        w.Run(2);
        Assert.Equal(5, w.E.Objects[2].Due); // refinery got its first raw at hook time 3, cycle 2
        w.Run(27);
        Assert.Equal(30, w.Game.Day);
        Assert.Equal(new[] { 5, 13, 18, 23, 28 }, RefinedDays(w));
        Assert.Equal(5, w.Eco.Refined(Side.Empire));
        Assert.Equal(0, w.Eco.Raw(Side.Empire));
        Assert.Equal(2, w.Stub!.Requests.Count); // two first-cycle draws and nothing else
    }

    // m2-mines-refineries-pools P2
    [Fact]
    public void P2_FirstCycleDrawFiveGivesSevenDays()
    {
        var w = SetupS1();
        w.Stub!.Queue(5, 5);
        w.Run(1);
        Assert.Equal(8, w.E.Objects[1].Due); // 5 + 2 = 7 days, due 8
        w.Run(7); // through hook time 8
        Assert.Equal(15, w.E.Objects[2].Due); // raw at 8, first cycle 7
        w.Run(7);
        Assert.Equal(new[] { 15 }, RefinedDays(w));
    }

    // m2-mines-refineries-pools P3
    [Theory]
    [InlineData(0, 5)]
    [InlineData(10, 6)]
    [InlineData(25, 8)]
    [InlineData(50, 10)]
    public void P3_CycleFormulaAtSupport100(int share, int days) =>
        Assert.Equal(days, EconomyModule.CycleDays(new EconomyParams(), 100, share));

    // m2-mines-refineries-pools §Numbers (cycle days table, integer rounding)
    [Theory]
    [InlineData(80, 0, 6)] [InlineData(80, 10, 7)] [InlineData(80, 25, 10)] [InlineData(80, 50, 12)]
    [InlineData(60, 0, 8)] [InlineData(60, 10, 9)] [InlineData(60, 25, 13)] [InlineData(60, 50, 16)]
    [InlineData(50, 0, 10)] [InlineData(50, 10, 12)] [InlineData(50, 25, 16)] [InlineData(50, 50, 20)]
    [InlineData(40, 0, 12)] [InlineData(40, 10, 15)] [InlineData(40, 25, 20)] [InlineData(40, 50, 25)]
    [InlineData(25, 0, 20)] [InlineData(25, 10, 24)] [InlineData(25, 25, 32)] [InlineData(25, 50, 40)]
    [InlineData(10, 0, 50)] [InlineData(10, 10, 60)] [InlineData(10, 25, 80)] [InlineData(10, 50, 100)]
    public void CycleTable(int support, int share, int days) =>
        Assert.Equal(days, EconomyModule.CycleDays(new EconomyParams(), support, share));

    // m2-mines-refineries-pools P4
    [Theory]
    [InlineData(0, 11)]   // first cycle 0 + 10 = 10, due 1 + 10
    [InlineData(20, 31)]  // 20 + 10 = 30
    public void P4_SupportFiftyFullShareTwentyDays(int draw, int due)
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire, 50);
        w.Mine(Home, id: 1);
        for (int i = 0; i < 9; i++) w.Eco.AddObject(Side.Empire, "Pacifiers"); // load 54: share min(50, 54) = 50
        Assert.Equal(20, EconomyModule.CycleDays(new EconomyParams(), 50, 50));
        w.Stub!.Expect(0, 20, draw);
        w.Run(1);
        // B5b 2026-10-18: shares are dealt incrementally at the 6.5 hook (B5b §4), so they are read after the first day.
        Assert.Equal(50, w.Eco.ShareOf(1));
        Assert.Equal(due, w.E.Objects[1].Due);
        Assert.Single(w.Stub.Requests, (0, 20));
    }

    // m2-mines-refineries-pools P5
    [Fact]
    public void P5_SupportEightyShareZeroIsSixDays()
    {
        Assert.Equal(125, new EconomyParams().SlowdownNumerator * 100 / 80);
        Assert.Equal(6, EconomyModule.CycleDays(new EconomyParams(), 80, 0));
    }

    // m2-mines-refineries-pools P6
    [Fact]
    public void P6_SupportZeroIsClampedToOne()
    {
        Assert.Equal(500, EconomyModule.CycleDays(new EconomyParams(), 0, 0));
        var w = SetupS1(support: 0);
        w.Run(1);
        Assert.Single(w.Stub!.Requests.Where(r => r.Max == 500)); // d = 500, first cycle rand(0..500) + 250
        Assert.Equal(1 + 250, w.E.Objects[1].Due);
    }

    private static EcoWorld Block(int mines, int refineries)
    {
        var w = new EcoWorld();
        w.Hold("Navalia", Side.Alliance, 100);
        for (int i = 0; i < mines; i++) w.Mine("Navalia");
        for (int i = 0; i < refineries; i++) w.Refinery("Navalia");
        return w;
    }

    // m2-mines-refineries-pools P7
    [Fact]
    public void P7_SevenPairsAtLoad175ShareTwentyFiveEach()
    {
        var w = Block(7, 7);
        for (int i = 0; i < 5; i++) w.Eco.AddObject(Side.Alliance, "Mender"); // 5 x 35 = 175
        Assert.Equal(175, w.Eco.Load(Side.Alliance));
        w.Run(1); // B5b 2026-10-18: shares are dealt incrementally at the 6.5 hook (B5b §4), so they are read after the first day.
        foreach (var o in w.Eco.Objects.Where(o => o.Class is "Mine" or "Refinery"))
            Assert.Equal(25, w.Eco.ShareOf(o.Id));
        Assert.Equal(8, EconomyModule.CycleDays(new EconomyParams(), 100, 25));
    }

    // m2-mines-refineries-pools P8
    [Theory]
    [InlineData(350)]
    [InlineData(400)]
    public void P8_SharesCapAtFiftyAndExtraLoadIsNotDealt(int load)
    {
        var w = Block(7, 0);
        for (int i = 0; i < 10; i++) w.Eco.AddObject(Side.Alliance, "Mender"); // 350
        if (load == 400)
        {
            for (int i = 0; i < 10; i++) w.Eco.AddObject(Side.Alliance, "Militia"); // 30
            for (int i = 0; i < 5; i++) w.Eco.AddObject(Side.Alliance, "Swift"); // 20
        }
        Assert.Equal(load, w.Eco.Load(Side.Alliance));
        w.Run(1); // B5b 2026-10-18: shares are dealt incrementally at the 6.5 hook (B5b §4), so they are read after the first day.
        Assert.All(w.Eco.Objects.Where(o => o.Class == "Mine"), o => Assert.Equal(50, w.Eco.ShareOf(o.Id)));
    }

    // m2-mines-refineries-pools P9
    [Fact]
    public void P9_ThreeMinesLoadTenShareFourThreeThree()
    {
        var w = Block(3, 0);
        for (int i = 0; i < 10; i++) w.Eco.AddObject(Side.Alliance, "Strikers"); // maintenance 1 each
        w.Run(1); // B5b 2026-10-18: shares are dealt incrementally at the 6.5 hook (B5b §4), so they are read after the first day.
        Assert.Equal(new[] { 4, 3, 3 }, w.Eco.Objects.Where(o => o.Class == "Mine").Select(o => w.Eco.ShareOf(o.Id)).ToArray());
    }

    // m2-mines-refineries-pools P10
    [Fact]
    public void P10_TwoMinesLoadZeroShareZero()
    {
        var w = Block(2, 0);
        Assert.All(w.Eco.Objects, o => Assert.Equal(0, w.Eco.ShareOf(o.Id)));
    }

    private static EcoWorld Smuggle(int support, int regiments = 0, params string[] ships)
    {
        var w = new EcoWorld(freeShips: true); // fleet ships pay upkeep now; these sheet numbers assume only the regiments do
        w.Hold(Home, Side.Empire, support);
        for (int i = 0; i < regiments; i++) w.Eco.AddObject(Side.Empire, "Pacifiers", Home);
        if (ships.Length > 0) w.Fleets.AddFleet(Side.Empire, "F", ships, Home);
        return w;
    }

    // m2-mines-refineries-pools P11
    [Fact]
    public void P11_SupportTwentyFiveShipAndThreeRegiments()
    {
        var w = Smuggle(25, 3, "Mandate");
        Assert.Equal(34, w.Eco.DiversionPercent(Home, Side.Empire)); // 50 - 10 - 6
    }

    // m2-mines-refineries-pools P12
    [Fact]
    public void P12_DeathStarSectorDoublesEmpireRegiments()
    {
        var w = Smuggle(25, 3, "Mandate");
        w.Eco.IsDeathStarSector = s => s == Home;
        Assert.Equal(28, w.Eco.DiversionPercent(Home, Side.Empire)); // 50 - 10 - 12
    }

    // m2-mines-refineries-pools P13
    [Fact]
    public void P13_OwnDeathStarInAFleetMakesItZeroWithNoDraw()
    {
        var w = Smuggle(25, 3, "Mandate", "Death Star");
        Assert.Equal(0, w.Eco.DiversionPercent(Home, Side.Empire));
        w.Mine(Home);
        w.Run(30); // load 18 (three regiments): d = 28, first cycle 14, so it finishes at hook time 15
        Assert.NotEmpty(w.Events<ResourceProduced>());
        Assert.DoesNotContain(w.Stub!.Requests, r => r.Max == 99); // no diversion draw
        Assert.Equal(0, w.Events<ResourceProduced>()[0].SmugglePercent);
    }

    // m2-mines-refineries-pools P14
    [Theory]
    [InlineData(74, true)]
    [InlineData(75, false)]
    public void P14_SupportFifteenSeventyFivePercentSmuggled(int draw, bool diverted)
    {
        var w = Smuggle(15);
        w.Mine(Home, id: 1);
        Assert.Equal(75, w.Eco.DiversionPercent(Home, Side.Empire));
        w.Stub!.Expect(0, 99, draw);
        w.Run(20); // support 15: d = 5 x 10000 / 15 / 100 = 33, first cycle 0 + 16, ends at hook time 17
        var e = Assert.Single(w.Events<ResourceProduced>());
        Assert.Equal(diverted, e.Diverted);
        Assert.Equal(diverted ? 1 : 0, w.Eco.Raw(Side.Alliance));
        Assert.Equal(diverted ? 0 : 1, w.Eco.Raw(Side.Empire));
    }

    // m2-mines-refineries-pools P15
    [Fact]
    public void P15_SixDestroyersClampToZeroAndMakeNoDraw()
    {
        var w = Smuggle(25, 0, Enumerable.Repeat("Mandate", 6).ToArray());
        Assert.Equal(0, w.Eco.DiversionPercent(Home, Side.Empire));
        w.Mine(Home, id: 1);
        w.Run(25); // d = 20, first cycle 10, ends at hook time 11
        Assert.NotEmpty(w.Events<ResourceProduced>());
        Assert.DoesNotContain(w.Stub!.Requests, r => r.Max == 99);
    }

    // m2-mines-refineries-pools P16
    [Theory]
    [InlineData(40, 25)] [InlineData(41, 0)] [InlineData(20, 75)] [InlineData(21, 50)] [InlineData(30, 50)] [InlineData(31, 25)]
    public void P16_SupportTiers(int support, int v) =>
        Assert.Equal(v, Smuggle(support).Eco.DiversionPercent(Home, Side.Empire));

    // m2-mines-refineries-pools §Diversion: a squadron, and an enemy ship, count as the sheet says
    [Fact]
    public void DiversionCountsFightersOfTheControllerOnly()
    {
        var w = Smuggle(25);
        for (int i = 0; i < 2; i++) w.Eco.AddObject(Side.Empire, "Gleaner", Home); // based squadrons; squadrons aboard are not "on the system"
        w.Fleets.AddFleet(Side.Alliance, "E", new[] { "Dodger" }, Home);
        Assert.Equal(40, w.Eco.DiversionPercent(Home, Side.Empire)); // 50 - 2 x 5; the enemy corvette does not count
    }

    // m2-mines-refineries-pools P17
    [Fact]
    public void P17_TwoRefineriesWaitInIdOrderAndGetRawInOrder()
    {
        var w = SetupS1();
        w.Refinery(Home, id: 3);
        w.Run(1);
        Assert.Equal(new[] { 2, 3 }, w.Eco.RawLine(Side.Empire).ToArray());
        w.Run(2); // hook time 3: first raw
        Assert.Equal(5, w.E.Objects[2].Due);
        Assert.Equal(0, w.E.Objects[3].Due);
        Assert.Equal(new[] { 3 }, w.Eco.RawLine(Side.Empire).ToArray());
        w.Run(5); // hook time 8: second raw
        Assert.Equal(8 + 2, w.E.Objects[3].Due); // its first cycle: 0 + 5 / 2 = 2 days
        Assert.Equal(new[] { 2 }, w.Eco.RawLine(Side.Empire).ToArray()); // id 2 asked again at hook time 5, behind id 3
    }

    private static int EnemyInterdictor(EcoWorld w, Side enemy, string system) =>
        w.Fleets.AddFleet(enemy, "Blockade", new[] { "Subpoena" }, system);

    // m2-mines-refineries-pools P18
    [Fact]
    public void P18_BlockadeAtCycleStartStopsMiningUnlessSwitchedOff()
    {
        var w = SetupS1(freeShips: true); // the Interdictor must not be autoscrapped: the sheet's numbers ignore its upkeep
        EnemyInterdictor(w, Side.Alliance, Home);
        Assert.True(w.Fleets.IsEconomicBlockade(Home, Side.Empire));
        w.Run(30);
        Assert.Equal(0, w.E.Objects[1].Due);
        Assert.Empty(w.Events<ResourceProduced>());

        var off = SetupS1(freeShips: true);
        off.Eco.BlockadeStopsMining = false;
        EnemyInterdictor(off, Side.Alliance, Home);
        off.Run(30);
        var free = SetupS1();
        free.Run(30);
        Assert.Equal(RefinedDays(free), RefinedDays(off)); // cycles and output unchanged
        Assert.Equal(5, RefinedDays(off).Length);
    }

    // m2-mines-refineries-pools P19
    // B5b 2026-10-18: B5b §3 (Classic [EXE-own] 0x50bbb0) replaces "the running cycle finishes": a blockade abandons the
    // running cycle with no point, and the restart when the flag clears is a startup cycle (B5b §2).
    [Fact]
    public void P19_BlockadeAbandonsTheRunningCycleThenRestartsOnAStartupCycle()
    {
        var w = new EcoWorld(freeShips: true);
        w.Hold(Home, Side.Empire, 100);
        w.Eco.MineStopMode = StopMode.Abandon; // Blockade pause, 2026-10-18: this checks the Classic loss (Classic switch); the DEFAULT pauses (P19b)
        w.Mine(Home, id: 1);
        w.Run(1); // cycle started, due 3
        int blocker = EnemyInterdictor(w, Side.Alliance, Home);
        w.Run(2); // hook time 2: suspended, the cycle is abandoned with no point
        Assert.Equal(0, w.Eco.Raw(Side.Empire));
        Assert.Equal(0, w.E.Objects[1].Due);
        w.Run(20); // Day 23: still blockaded, no new cycle
        Assert.Equal(0, w.E.Objects[1].Due);
        Assert.Empty(w.Events<ResourceProduced>());
        w.State.Fleets.Remove(blocker); // flag clears before the next Step
        w.Run(1); // hook time 24: restart on a startup cycle, d = 5: rand(0..5) + 2 with the stub at 0
        Assert.Equal(24 + 2, w.E.Objects[1].Due);
        Assert.Equal(new[] { (0, 5), (0, 5) }, w.Stub!.Requests.ToArray()); // the first start and the restart each drew once
    }

    // P19b (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch): MineStopMode Pause (DEFAULT) freezes
    // the running cycle while blockaded; it resumes with the day it had left, no startup cycle, no extra draw, and pays its point.
    // rule: m2-mines-refineries-pools §Blockade and uprising
    [Fact]
    public void P19b_BlockadePausesTheRunningCycleAndResumesIt()
    {
        var w = new EcoWorld(freeShips: true);
        Assert.Equal(StopMode.Pause, w.Eco.MineStopMode);
        w.Hold(Home, Side.Empire, 100);
        w.Mine(Home, id: 1);
        w.Run(1); // cycle started, due 3
        int blocker = EnemyInterdictor(w, Side.Alliance, Home);
        w.Run(22); // hooks 2 to 23 suspended: the due day moved on 22 days
        Assert.Equal(3 + 22, w.E.Objects[1].Due);
        Assert.Equal(Side.Empire, w.E.Objects[1].PausedFor);
        Assert.Empty(w.Events<ResourceProduced>());
        w.State.Fleets.Remove(blocker);
        w.Run(1); // hook 24: resumed, one day still to run
        Assert.Null(w.E.Objects[1].PausedFor);
        Assert.Equal(25, w.E.Objects[1].Due);
        Assert.Empty(w.Events<StartupCycle>().Where(e => e.Day == 24));
        w.Run(1); // hook 25: the paused cycle pays
        Assert.Equal(1, w.Eco.Raw(Side.Empire));
        Assert.Equal(new[] { (0, 5) }, w.Stub!.Requests.ToArray()); // only the first start drew; the resume drew nothing
    }

    // P19c (Blockade pause, 2026-10-18): a paused cycle belongs to its side. The system changes hands while blockaded: the paused cycle is lost,
    // and the new holder starts on a startup cycle. rule: m2-mines-refineries-pools §Blockade and uprising, §Side change
    [Fact]
    public void P19c_APausedCycleIsLostWhenTheSystemChangesHands()
    {
        var w = new EcoWorld(freeShips: true);
        w.Hold(Home, Side.Empire, 100);
        w.Mine(Home, id: 1);
        w.Run(1);
        int blocker = EnemyInterdictor(w, Side.Alliance, Home);
        w.Run(3);
        Assert.Equal(Side.Empire, w.E.Objects[1].PausedFor);
        w.State.Fleets.Remove(blocker);
        w.Hold(Home, Side.Alliance, 100);
        w.Run(1);
        Assert.Null(w.E.Objects[1].PausedFor);
        Assert.Equal(Side.Alliance, w.E.Objects[1].WorkSide);
        Assert.Single(w.Events<StartupCycle>().Where(e => e.Day == 5 && e.Facility == 1));
    }

    // m2-mines-refineries-pools P20
    [Fact]
    public void P20_TwoSidesDueOnOneDayAreProcessedInIdOrder()
    {
        var w = new EcoWorld();
        w.Hold("Caminus", Side.Alliance); w.Hold(Home, Side.Empire);
        w.Mine("Caminus", id: 1); w.Mine(Home, id: 2);
        w.Run(3);
        var e = w.Events<ResourceProduced>();
        Assert.Equal(new[] { 1, 2 }, e.Select(x => x.Facility).ToArray());
        Assert.Equal((1, 1), (w.Eco.Raw(Side.Alliance), w.Eco.Raw(Side.Empire)));
    }

    private static EcoWorld Pairs(ulong seed, bool incidents)
    {
        var w = new EcoWorld(seed, incidents);
        var names = TestData.RealGalaxy.Systems.Where(s => s.Name != "Glasswood" && s.Name != "Frostholm").Take(20).Select(s => s.Name).ToArray();
        for (int i = 0; i < names.Length; i++)
        {
            var side = i % 2 == 0 ? Side.Empire : Side.Alliance;
            w.Hold(names[i], side, 60);
            w.Mine(names[i]); w.Refinery(names[i]);
        }
        w.Eco.SetupNewGame();
        return w;
    }

    // m2-mines-refineries-pools P21
    [Fact]
    public void P21_ThousandDaysTwentyPairsNeverNegativeAndSameSeedSameHash()
    {
        string Run(ulong seed, out long draws)
        {
            var w = Pairs(seed, incidents: false);
            for (int i = 0; i < 1000; i++)
            {
                w.Run(1);
                foreach (var s in new[] { Side.Empire, Side.Alliance })
                    Assert.True(w.Eco.Raw(s) >= 0 && w.Eco.Refined(s) >= 0);
            }
            draws = w.Game.Rng.Draws;
            return GameSave.StateHash(w.Game);
        }
        string a = Run(100, out long d1), a2 = Run(100, out _), b = Run(200, out _);
        Assert.Equal(a, a2);
        Assert.NotEqual(a, b);
        Assert.Equal(40, d1); // support 60: no smuggling draws; only the 40 first cycles (incidents and autoscrap are off or idle)
    }

    // m2-mines-refineries-pools P22
    // B5b 2026-10-18: B5b §3 side change (Classic [EXE-own] 0x52d0e0 -> 0x55b010) replaces "keeps its due day": the running
    // cycle is lost, no point for anyone, and the mine restarts for the new holder on a startup cycle.
    [Fact]
    public void P22_MineChangingSideMidCycleRestartsOnAStartupCycle()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire, 100);
        w.Mine(Home, id: 1);
        w.Run(2);
        Assert.Equal(3, w.E.Objects[1].Due);
        w.Fleets.SetOwner(Home, Side.Alliance); // capture
        w.Eco.SetAllianceSupport(Home, 50);
        w.Run(1); // hook time 3
        Assert.Equal(0, w.Eco.Raw(Side.Alliance));
        Assert.Equal(0, w.Eco.Raw(Side.Empire));
        Assert.Equal(3 + 5, w.E.Objects[1].Due); // d = 5 x 200 / 100 = 10, startup rand(0..10) + 5 with the stub at 0
        Assert.Equal(new[] { (0, 5), (0, 10) }, w.Stub!.Requests.ToArray()); // the first start, then the new holder's startup draw
    }

    // m2-mines-refineries-pools §Side change: a waiting refinery goes to the end of the new holder's line
    [Fact]
    public void WaitingRefineryMovesToTheNewHoldersLine()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire, 100);
        w.Refinery(Home, id: 2);
        w.Hold("Caminus", Side.Alliance); w.Refinery("Caminus", id: 5);
        w.Run(1);
        Assert.Equal(new[] { 2 }, w.Eco.RawLine(Side.Empire).ToArray());
        w.Fleets.SetOwner(Home, Side.Alliance);
        w.Run(1);
        Assert.Empty(w.Eco.RawLine(Side.Empire));
        Assert.Equal(new[] { 5, 2 }, w.Eco.RawLine(Side.Alliance).ToArray());
    }

    // m2-mines-refineries-pools §Where it runs: neutral systems have no cycles
    [Fact]
    public void NeutralSystemFacilitiesNeverCycle()
    {
        var w = new EcoWorld();
        w.Mine(Home, id: 1); w.Refinery(Home, id: 2);
        w.Run(50);
        Assert.Empty(w.Game.Events);
        Assert.Empty(w.Stub!.Requests);
    }

    // m2-mines-refineries-pools §Rule (Classic): uprising does not stop mines (nothing models uprisings yet); queued facilities do not cycle
    [Fact]
    public void FacilityInDeliveryDoesNotCycle()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire);
        w.Mine(Home, completed: false);
        w.Run(20);
        Assert.Empty(w.Game.Events);
    }
}
