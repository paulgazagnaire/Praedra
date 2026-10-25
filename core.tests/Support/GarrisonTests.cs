using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Support;

namespace Praedra.Core.Tests.Support;

// m2-uprisings-and-garrison, tests G1 to G25.
public class GarrisonTests
{
    private const string Ally = "Drift Harbour";
    private const string Emp = "Classis";
    private static readonly SupportParams P = new();

    // ---------------------------------------------------------------- required garrison N

    // G1: N for s = 0, 1, 39, 40, 49, 50, 59, 60, 100 (no flags)
    [Fact]
    public void G1_RequiredGarrison()
    {
        int[] s = { 0, 1, 39, 40, 49, 50, 59, 60, 100 };
        int[] n = { 6, 6, 3, 2, 2, 1, 1, 0, 0 };
        Assert.Equal(n, s.Select(x => SupportModule.RequiredGarrison(P, x, false, false)).ToArray());
    }

    // G2: s = 40 in uprising / Empire Death Star / both: 4 / 1 / 2
    [Fact]
    public void G2_Flags()
    {
        Assert.Equal(4, SupportModule.RequiredGarrison(P, 40, true, false));
        Assert.Equal(1, SupportModule.RequiredGarrison(P, 40, false, true));
        Assert.Equal(2, SupportModule.RequiredGarrison(P, 40, true, true));
    }

    // G3: s = 50, Empire Death Star: N = 0 (1 / 2 truncated)
    [Fact]
    public void G3_HalvingTruncates() => Assert.Equal(0, SupportModule.RequiredGarrison(P, 50, false, true));

    // the sheet's table, every row: no flag, uprising, Empire Death Star, both
    [Theory]
    [InlineData(0, 6, 12, 3, 6)]
    [InlineData(9, 6, 12, 3, 6)]
    [InlineData(10, 5, 10, 2, 5)]
    [InlineData(19, 5, 10, 2, 5)]
    [InlineData(20, 4, 8, 2, 4)]
    [InlineData(29, 4, 8, 2, 4)]
    [InlineData(30, 3, 6, 1, 3)]
    [InlineData(39, 3, 6, 1, 3)]
    [InlineData(40, 2, 4, 1, 2)]
    [InlineData(49, 2, 4, 1, 2)]
    [InlineData(50, 1, 2, 0, 1)]
    [InlineData(59, 1, 2, 0, 1)]
    [InlineData(60, 0, 0, 0, 0)]
    [InlineData(100, 0, 0, 0, 0)]
    public void RequiredGarrisonTable(int s, int plain, int uprising, int ds, int both)
    {
        Assert.Equal(plain, SupportModule.RequiredGarrison(P, s, false, false));
        Assert.Equal(uprising, SupportModule.RequiredGarrison(P, s, true, false));
        Assert.Equal(ds, SupportModule.RequiredGarrison(P, s, false, true));
        Assert.Equal(both, SupportModule.RequiredGarrison(P, s, true, true));
    }

    // the Death Star halving is the Empire's only; the module reads support of the holder, the flag and the uprising flag
    [Fact]
    public void RequiredGarrisonReadsTheSystem()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 40);
        Assert.Equal(2, w.Sup.RequiredGarrison(Emp));
        w.Sup.SetDeathStarSystem(Emp, true);
        Assert.Equal(1, w.Sup.RequiredGarrison(Emp));
        w.Hold(Ally, Side.Alliance, 40);
        w.Sup.SetDeathStarSystem(Ally, true);
        Assert.Equal(2, w.Sup.RequiredGarrison(Ally)); // Alliance: no halving
        Assert.Equal(0, w.Sup.RequiredGarrison("Navalia")); // neutral
    }

    // ---------------------------------------------------------------- uprising start, keep, end

    // G4: Classic, Empire holds, s = 30 (N = 3), 3 regiments: surplus 0, no uprising
    [Fact]
    public void G4_SurplusZeroNoUprising()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Regiments(Emp, Side.Empire, "Tranches", 3);
        w.Run(5);
        Assert.False(w.Sup.IsUprising(Emp));
        Assert.Empty(w.Events<UprisingStarted>());
    }

    // G5: same with 2 regiments: uprising starts at slot 6.1; UprisingStarted; one draw rand(0..70)
    [Fact]
    public void G5_UprisingStartsWithOneDraw()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Regiments(Emp, Side.Empire, "Tranches", 2);
        w.Stub.Queue(40);
        w.Run(1);
        var e = Assert.Single(w.Events<UprisingStarted>());
        Assert.Equal((Emp, Side.Empire, 1), (e.System, e.Holder, e.Day));
        Assert.Equal(new[] { (0, 70) }, w.Stub.Requests.ToArray());
        var st = w.Sup.StateOf(Emp)!;
        Assert.Equal(Side.Alliance, st.Rebels);
        Assert.Equal(31, st.ErosionDue);
        Assert.Equal(1 + 30 + 40, st.IncidentDue);
    }

    // G6: uprising on, s = 30, 5 regiments (N = 6): stays on
    [Fact]
    public void G6_UprisingStaysOn()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Regiments(Emp, Side.Empire, "Tranches", 5);
        w.Uprising(Emp, Side.Empire, erosionDue: 30, incidentDue: 90);
        w.Run(1);
        Assert.True(w.Sup.IsUprising(Emp));
        Assert.Empty(w.Events<UprisingEnded>());
    }

    // G7: same with 6 regiments: surplus 0, UprisingEnded; timers disarmed
    [Fact]
    public void G7_UprisingEndsAtTwiceN()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Regiments(Emp, Side.Empire, "Tranches", 6);
        w.Uprising(Emp, Side.Empire, erosionDue: 30, incidentDue: 90);
        w.Run(1);
        Assert.False(w.Sup.IsUprising(Emp));
        Assert.Equal("Garrison", w.Events<UprisingEnded>().Single().Reason);
        Assert.Null(w.Sup.StateOf(Emp)); // both timers disarmed, idle entry removed
        w.Run(200);
        Assert.Empty(w.Events<SupportChanged>());
        Assert.Empty(w.Events<IncidentRolled>());
    }

    // G8: uprising on, s rises from 30 to 60 with 0 regiments: N = 0, ended at the next refresh
    [Fact]
    public void G8_SupportRisesToSixty()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Uprising(Emp, Side.Empire, erosionDue: 30, incidentDue: 90);
        w.Run(1);
        Assert.True(w.Sup.IsUprising(Emp));
        w.W.Eco.SetAllianceSupport(Emp, 40); // Empire support 60
        w.Run(1);
        Assert.False(w.Sup.IsUprising(Emp));
        Assert.Equal("NoRequirement", w.Events<UprisingEnded>().Single().Reason);
    }

    // a garrison filled today clears an uprising at the next slot 6.1 (not within the same Step's earlier work)
    [Fact]
    public void GarrisonChangesAreSeenAtTheNextRefresh()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Stub.Queue(40);
        w.Run(1);
        Assert.True(w.Sup.IsUprising(Emp));
        w.Regiments(Emp, Side.Empire, "Tranches", 6);
        Assert.True(w.Sup.IsUprising(Emp)); // nothing changes between Steps
        w.Run(1);
        Assert.False(w.Sup.IsUprising(Emp));
    }

    // regiments aboard fleets, special forces, queued regiments and the other side's regiments do not count (Classic DEFAULT)
    [Fact]
    public void OnlyLandedCompletedRegimentsOfTheHolderCount()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 40); // N = 2
        w.W.Eco.AddObject(Side.Empire, "Tranches", Emp);
        w.W.Eco.AddObject(Side.Empire, "Tranches", Emp, completed: false);
        w.W.Eco.AddObject(Side.Empire, "Tranches");                     // no system: aboard something
        w.W.Eco.AddObject(Side.Alliance, "Militia", Emp);              // the other side's
        w.W.Eco.AddObject(Side.Empire, "Speculators", Emp);                  // a special force
        Assert.Equal(1, w.Sup.RegimentCount(Emp));
        Assert.Equal(10, w.Sup.Garrison10(Emp));
        w.Stub.Queue(0);
        w.Run(1);
        Assert.True(w.Sup.IsUprising(Emp));
    }

    // DEFAULT (capture sheet): a changed holder clears the flag, then the new holder is judged at once
    [Fact]
    public void ControlChangeClearsThenJudgesTheNewHolder()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Stub.Queue(10, 20);
        w.Run(1);
        Assert.True(w.Sup.IsUprising(Emp));
        w.W.Fleets.SetOwner(Emp, Side.Alliance); // A = 70: the Alliance holds a loyal world, N = 0
        w.Run(1);
        Assert.False(w.Sup.IsUprising(Emp));
        Assert.Equal("ControlChanged", w.Events<UprisingEnded>().Single().Reason);
        // captured back: Empire support 30 again, no garrison: a new uprising for the Empire
        w.W.Fleets.SetOwner(Emp, Side.Empire);
        w.Run(1);
        Assert.True(w.Sup.IsUprising(Emp));
        Assert.Equal(2, w.Events<UprisingStarted>().Count);
    }

    // the capture package's call: clears the state at once with UprisingEnded "Captured"
    [Fact]
    public void ClearUprisingOnCapture()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Uprising(Emp, Side.Empire, erosionDue: 30, incidentDue: 90);
        w.InStep(ctx => w.Sup.ClearUprisingOnCapture(ctx, Emp));
        w.Run(1);
        Assert.Equal("Captured", w.Events<UprisingEnded>().Single().Reason);
        Assert.Null(w.Sup.StateOf(Emp));
    }

    // ---------------------------------------------------------------- New ruleset: weights

    private static (int G10, bool Satisfied) NewCase(string cls, int n, int support = 40)
    {
        var w = new SupWorld(GarrisonRuleset.New);
        w.Hold(Emp, Side.Empire, support);
        w.Regiments(Emp, Side.Empire, cls, n);
        int need10 = 10 * w.Sup.RequiredGarrison(Emp);
        return (w.Sup.Garrison10(Emp), w.Sup.Garrison10(Emp) >= need10);
    }

    // G9: New, s = 40 (N = 2): 3 Stormtroopers (36), 2 Fleet (12), 4 Fleet (24), 5 War Droids (20), 2 Dark Troopers (32), 1 The Moorings (16)
    [Theory]
    [InlineData("Pacifiers", 3, 36, true)]
    [InlineData("Ballast", 2, 12, false)]
    [InlineData("Ballast", 4, 24, true)]
    [InlineData("Proxies", 5, 20, true)]
    [InlineData("Lictors", 2, 32, true)]
    [InlineData("Ilmar Shade Guard", 1, 16, false)]
    public void G9_WeightedGarrison(string cls, int n, int g10, bool ok)
    {
        Assert.Equal((g10, ok), NewCase(cls, n));
        // and the Step agrees: the uprising starts exactly when the check fails
        var w = new SupWorld(GarrisonRuleset.New);
        w.Hold(Emp, Side.Empire, 40);
        w.Regiments(Emp, Side.Empire, cls, n);
        w.Run(1);
        Assert.Equal(!ok, w.Sup.IsUprising(Emp));
    }

    // G10: New, 6 Fleet regiments (36) versus 3 Stormtroopers (36), N = 3 (s = 30): both satisfied; 5 Fleet (30) satisfied, 4 Fleet (24) not
    [Theory]
    [InlineData("Ballast", 6, 36, true)]
    [InlineData("Pacifiers", 3, 36, true)]
    [InlineData("Ballast", 5, 30, true)]
    [InlineData("Ballast", 4, 24, false)]
    public void G10_WeightedGarrisonN3(string cls, int n, int g10, bool ok) => Assert.Equal((g10, ok), NewCase(cls, n, 30));

    // G11: Classic and New with only Army regiments, any N: identical results (weight 1.0)
    [Theory]
    [InlineData(10, 0)] [InlineData(10, 1)] [InlineData(20, 2)] [InlineData(30, 2)] [InlineData(30, 3)] [InlineData(0, 5)] [InlineData(0, 6)] [InlineData(55, 1)] [InlineData(60, 0)]
    public void G11_ArmyOnlyIdentical(int support, int regs)
    {
        bool Run(GarrisonRuleset r)
        {
            var w = new SupWorld(r);
            w.Hold(Emp, Side.Empire, support);
            w.Regiments(Emp, Side.Empire, "Tranches", regs);
            w.Hold(Ally, Side.Alliance, support);
            w.Regiments(Ally, Side.Alliance, "Militia", regs);
            w.Stub.Queue(0, 0);
            w.Run(1);
            return w.Sup.IsUprising(Emp) == w.Sup.IsUprising(Ally) && w.Sup.IsUprising(Emp);
        }
        Assert.Equal(Run(GarrisonRuleset.Classic), Run(GarrisonRuleset.New));
    }

    // the Classic ruleset ignores the weights; the weights file is data (a regiment without a row weighs 1.0)
    [Fact]
    public void ClassicIgnoresWeights_UnknownClassWeighsOne()
    {
        var w = new SupWorld(GarrisonRuleset.Classic);
        w.Hold(Emp, Side.Empire, 40);
        w.Regiments(Emp, Side.Empire, "Proxies", 3);
        Assert.Equal(30, w.Sup.Garrison10(Emp));
        Assert.Equal(10, SupWorld.Weights.Value.Weight10("Some New Regiment"));
        Assert.Equal(6, SupWorld.Weights.Value.Weight10("Deckhands"));
        Assert.Equal(16, SupWorld.Weights.Value.Weight10("Lictors"));
    }

    // ---------------------------------------------------------------- incident score and lookup

    private static (int Score, int C1, int C2) Roll(int dice, int s, int r, int t = 0, bool ds = false)
    {
        // dice = d1 + d2 (split evenly is not needed: only the sum counts)
        int score = SupportModule.IncidentScore(P, dice, 0, s, r, t, ds);
        return (score, SupportModule.Lookup(P.Table1, score), SupportModule.Lookup(P.Table2, score));
    }

    [Fact] public void G12_Score() => Assert.Equal((11, 2, 4), Roll(9, 40, 0));                       // 9 + 2
    [Fact] public void G13_Score() => Assert.Equal((13, 2, 5), Roll(12, 25, 3));                      // 12 + 4 - 3
    [Fact] public void G14_Score() => Assert.Equal((9, 1, 3), Roll(10, 20, 2, t: 1, ds: true));       // 10 + 4 - 4 - 1
    [Fact] public void G15_Score() => Assert.Equal((5, 0, 0), Roll(5, 70, 0));
    [Fact] public void G17_NegativeScore() => Assert.Equal((-4, 0, 0), Roll(2, 70, 6));

    // G16: scores exactly 6, 9, 10, 11, 12 give codes (1,0), (1,3), (2,3), (2,4), (2,5)
    [Theory]
    [InlineData(6, 1, 0)] [InlineData(9, 1, 3)] [InlineData(10, 2, 3)] [InlineData(11, 2, 4)] [InlineData(12, 2, 5)]
    [InlineData(5, 0, 0)] [InlineData(8, 1, 0)] [InlineData(30, 2, 5)]
    public void G16_Lookup(int score, int c1, int c2)
    {
        Assert.Equal(c1, SupportModule.Lookup(P.Table1, score));
        Assert.Equal(c2, SupportModule.Lookup(P.Table2, score));
    }

    // G14 through the module: the Empire in a Death Star sector, a Stormtrooper counts in R and again in T
    [Fact]
    public void G14_ScoreFromTheSystem()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 20);
        w.Sup.SetDeathStarSystem(Emp, true);
        w.Regiments(Emp, Side.Empire, "Pacifiers", 1);
        w.Regiments(Emp, Side.Empire, "Tranches", 1);
        Assert.Equal(9, w.Sup.IncidentScore(Emp, Side.Empire, 5, 5)); // 10 + 4 - 2*2 - 1
        Assert.Equal(10 + 4 - 2 - 1, SupportModule.IncidentScore(P, 5, 5, 20, 2, 1, false));
        // the New ruleset still uses the flat count here (DEFAULT)
        w.Sup.Ruleset = GarrisonRuleset.New;
        Assert.Equal(9, w.Sup.IncidentScore(Emp, Side.Empire, 5, 5));
    }

    private static (int[] C1, int[] C2) Odds(int s, int r)
    {
        var c1 = new int[3]; var c2 = new int[6];
        for (int d1 = 1; d1 <= 10; d1++)
            for (int d2 = 1; d2 <= 10; d2++)
            {
                int score = SupportModule.IncidentScore(P, d1, d2, s, r, 0, false);
                c1[SupportModule.Lookup(P.Table1, score)]++;
                c2[SupportModule.Lookup(P.Table2, score)]++;
            }
        return (c1, c2);
    }

    // G18: exact odds s = 40, R = 0: code1 0/1/2 = 3/18/79; code2 0/3/4/5 = 15/13/8/64
    [Fact]
    public void G18_Odds()
    {
        var (c1, c2) = Odds(40, 0);
        Assert.Equal(new[] { 3, 18, 79 }, c1);
        Assert.Equal(new[] { 15, 13, 8, 64 }, new[] { c2[0], c2[3], c2[4], c2[5] });
    }

    // G19: s = 40, R = 2: code1 10/26/64; code2 28/17/10/45
    [Fact]
    public void G19_Odds()
    {
        var (c1, c2) = Odds(40, 2);
        Assert.Equal(new[] { 10, 26, 64 }, c1);
        Assert.Equal(new[] { 28, 17, 10, 45 }, new[] { c2[0], c2[3], c2[4], c2[5] });
    }

    // G20: s = 25, R = 3: code1 6/22/72; code2 21/15/9/55
    [Fact]
    public void G20_Odds()
    {
        var (c1, c2) = Odds(25, 3);
        Assert.Equal(new[] { 6, 22, 72 }, c1);
        Assert.Equal(new[] { 21, 15, 9, 55 }, new[] { c2[0], c2[3], c2[4], c2[5] });
    }

    // ---------------------------------------------------------------- incident effects and draws

    // G21: codes (2,5), system with 4 facilities and 3 regiments: one regiment destroyed (one draw over 3); code 5 is a no-op; re-arm draw last
    [Fact]
    public void G21_RegimentDestroyed_DrawOrder()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 40);
        foreach (var k in new[] { FacilityKind.OrbitalShipyard, FacilityKind.TrainingFacility, FacilityKind.ConstructionYard, FacilityKind.Kdy150 })
            w.W.Eco.AddFacility(Ally, k);
        var ids = Enumerable.Range(0, 3).Select(_ => w.W.Eco.AddObject(Side.Alliance, "Militia", Ally)).ToArray();
        w.Uprising(Ally, Side.Alliance, erosionDue: 31, incidentDue: 1);
        w.Stub.Queue(9, 9, 1, 40); // d1 = d2 = 10, victim index 1, re-arm 40
        w.Run(1);
        var inc = Assert.Single(w.Events<IncidentRolled>());
        Assert.Equal((Ally, 10 + 10 + 2 - 3, 2, 5, 1), (inc.System, inc.Score, inc.Code1, inc.Code2, inc.Day));
        var gone = Assert.Single(w.Events<RegimentDestroyed>());
        Assert.Equal(ids[1], gone.Object);
        Assert.Null(w.W.Eco.Find(ids[1]));
        Assert.Equal(2, w.W.Eco.Objects.Count(o => o.Category == ObjectCategory.Regiment));
        Assert.Equal(4, w.W.Eco.Objects.Count(o => o.Category == ObjectCategory.Facility)); // code 1 did not happen
        Assert.Equal(new[] { (0, 9), (0, 9), (0, 2), (0, 70) }, w.Stub.Requests.ToArray());
        Assert.Equal(1 + 30 + 40, w.Sup.StateOf(Ally)!.IncidentDue);
    }

    // G22: code 1 with no facility: nothing destroyed, no effect draw
    [Fact]
    public void G22_NoFacilityNoDraw()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 40);
        w.Uprising(Ally, Side.Alliance, erosionDue: 31, incidentDue: 1);
        w.Stub.Queue(1, 1); // d = 2 + 2 = 4, + 2 = 6: codes (1, 0)
        w.Run(1);
        var inc = Assert.Single(w.Events<IncidentRolled>());
        Assert.Equal((6, 1, 0), (inc.Score, inc.Code1, inc.Code2));
        Assert.Empty(w.Events<FacilityDestroyed>());
        Assert.Equal(new[] { (0, 9), (0, 9), (0, 70) }, w.Stub.Requests.ToArray());
    }

    // code 1 with facilities: one completed facility (not a queued one) chosen over the ids, reason UprisingIncident
    [Fact]
    public void Code1_DestroysOneCompletedFacility()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 40);
        int a = w.W.Eco.AddFacility(Ally, FacilityKind.OrbitalShipyard);
        int queued = w.W.Eco.AddFacility(Ally, FacilityKind.TrainingFacility, completed: false);
        int b = w.W.Eco.AddFacility(Ally, FacilityKind.ConstructionYard);
        w.Uprising(Ally, Side.Alliance, erosionDue: 31, incidentDue: 1);
        w.Stub.Queue(1, 1, 1, 5); // score 6 -> code 1; pick index 1 of the two completed (ids a, b) = b; re-arm 5
        w.Run(1);
        var d = Assert.Single(w.Events<FacilityDestroyed>());
        Assert.Equal((b, Ally, DestroyReason.UprisingIncident), (d.Facility, d.System, d.Reason));
        Assert.NotNull(w.W.Eco.Find(a)); Assert.NotNull(w.W.Eco.Find(queued));
        Assert.Equal(new[] { (0, 9), (0, 9), (0, 1), (0, 70) }, w.Stub.Requests.ToArray());
    }

    // code 2 hits only regiments of the holder on the system; with none, nothing is drawn
    [Fact]
    public void Code2_NeedsAHolderRegiment()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 40);
        w.W.Eco.AddObject(Side.Empire, "Tranches", Ally); // not the holder's
        w.Uprising(Ally, Side.Alliance, erosionDue: 31, incidentDue: 1);
        w.Stub.Queue(9, 9);
        w.Run(1);
        Assert.Empty(w.Events<RegimentDestroyed>());
        Assert.Equal(new[] { (0, 9), (0, 9), (0, 70) }, w.Stub.Requests.ToArray());
    }

    // G23: uprising at Step 10 with arm draw 40: first incident in Step 80 (P = 70), next arm drawn after
    [Fact]
    public void G23_IncidentTimer()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 60);
        w.Stub.Expect(0, 70, 40, 15);
        w.Run(10);
        w.W.Eco.SetAllianceSupport(Ally, 40);
        w.Run(70); // Steps 10..79: not yet
        Assert.Empty(w.Events<IncidentRolled>());
        w.Run(1);  // Step 80
        var inc = Assert.Single(w.Events<IncidentRolled>());
        Assert.Equal(81, inc.Day);
        Assert.Equal(81 + 30 + 15, w.Sup.StateOf(Ally)!.IncidentDue);
        Assert.Equal(3, w.Stub.Count(0, 70) + 1); // arm, re-arm (+1: the two Expect values are the only (0,70) draws)
    }

    // G24: same game, same seed, twice: identical draw log, events and state hash
    [Fact]
    public void G24_Deterministic()
    {
        (List<GameEvent> Ev, string Hash, long Draws) Run(ulong seed)
        {
            var w = new SupWorld(stub: false, seed: seed);
            w.Hold(Ally, Side.Alliance, 30); w.Hold(Emp, Side.Empire, 35);
            foreach (var k in new[] { FacilityKind.OrbitalShipyard, FacilityKind.ConstructionYard, FacilityKind.Kdy150 }) { w.W.Eco.AddFacility(Ally, k); w.W.Eco.AddFacility(Emp, k); }
            w.Regiments(Ally, Side.Alliance, "Militia", 3);
            w.Regiments(Emp, Side.Empire, "Tranches", 2);
            w.Run(500);
            return (w.Game.Events.ToList(), GameSave.StateHash(w.Game), w.Game.Rng.Draws);
        }
        var a = Run(21); var b = Run(21); var c = Run(22);
        Assert.Equal(a.Ev, b.Ev); Assert.Equal(a.Hash, b.Hash); Assert.Equal(a.Draws, b.Draws);
        Assert.NotEqual(a.Hash, c.Hash);
        Assert.Contains(a.Ev, e => e is IncidentRolled);
    }

    // G25: two uprising systems with incidents on the same Step: draws made in name order (Drift Harbour first, then Classis)
    [Fact]
    public void G25_SystemNameOrder()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 40);
        w.Hold(Ally, Side.Alliance, 40);
        w.Uprising(Emp, Side.Empire, erosionDue: 31, incidentDue: 1);
        w.Uprising(Ally, Side.Alliance, erosionDue: 31, incidentDue: 1);
        w.Stub.Queue(1, 2, 10, 3, 4, 20); // Classis: d = 2, 3, re-arm 10; Drift Harbour: d = 4, 5, re-arm 20
        w.Run(1);
        var ev = w.Events<IncidentRolled>();
        Assert.Equal(new[] { Emp, Ally }, ev.Select(e => e.System).ToArray());
        Assert.Equal(new[] { 2 + 3 + 2, 4 + 5 + 2 }, ev.Select(e => e.Score).ToArray());
        Assert.Equal(1 + 30 + 10, w.Sup.StateOf(Emp)!.IncidentDue);
        Assert.Equal(1 + 30 + 20, w.Sup.StateOf(Ally)!.IncidentDue);
    }
}
