using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Missions;
using static Rebellion.Core.Tests.Missions.MissionWorld;

namespace Rebellion.Core.Tests.Missions;

// m5-missions §Tests MI1 to MI26 and MI39 to MI43, MI57, MI60: the detection walk, the decoy phase, the success tables and the draw order.
public class MissionRollTests
{
    private static int Rolls(MissionWorld w) => w.Stub!.Count(0, 99);

    // An Alliance Espionage team at T (Empire-held, Empire support 100: no garrison needed), already there; the iteration runs in Step 1.
    private static (MissionWorld W, int Mission) EspionageAtT(Action<MissionWorld> setup, int[] rolls, bool decoys = false, MissionParams? p = null,
        int[]? chars = null, int[]? sfs = null, int[]? decoyIds = null)
    {
        var w = new MissionWorld(p, decoys);
        w.Hold(T, Side.Empire, 0);
        setup(w);
        w.Stub!.Expect(0, 99, rolls);
        var seq = w.Start(Side.Alliance, MissionKind.Espionage, chars ?? new[] { Leia }, T, sfs, decoyIds);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        int id = w.LastMission;
        w.RunIteration(id);
        return (w, id);
    }

    // m5-missions MI1
    [Theory]
    [InlineData(-21, 100)] [InlineData(-20, 99)] [InlineData(-19, 98)] [InlineData(-10, 98)] [InlineData(-9, 96)] [InlineData(0, 96)]
    [InlineData(1, 94)] [InlineData(10, 94)] [InlineData(11, 90)] [InlineData(100, 30)] [InlineData(101, 25)] [InlineData(500, 25)]
    public void MI1_FoilTable(int x, int p) => Assert.Equal(p, new MissionWorld().M.FoilChance(x));

    // m5-missions MI2: average 50 against detection 50 gives x = 1, p = 94 (here: espionage 15 against an Imperial Army Regiment, detection 15)
    [Theory]
    [InlineData(93, true)]
    [InlineData(94, false)]
    public void MI2_EqualSkillDetects94Percent(int draw, bool detected)
    {
        var (w, id) = EspionageAtT(w => { w.Agent(Leia, T, esp: 15); w.Unit(Side.Empire, ImpArmy, T); }, new[] { draw, 99 });
        var roll = Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Equal((1, 94, draw, detected), (roll.X, roll.Chance, roll.Roll, roll.Detected));
        Assert.Equal(detected, w.Events<MissionFoiled>().Count == 1);
        Assert.Equal(detected ? 0 : 1, w.Events<MissionMemberRoll>().Count); // a detection blocks every success roll
        Assert.Equal(detected ? MissionOutcome.Foiled : MissionOutcome.Failed, w.Events<MissionEnded>().Single().Outcome);
    }

    // m5-missions MI3: Bothan Spies (70) plus a character (50): average 60, one special force, detection 15: x = 60 - 15 - 1 + 1 = 45, p = 60
    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public void MI3_BothanAndCharacter(int draw, bool detected)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50);
        int bothan = w.Unit(Side.Alliance, Bothan, T);
        w.Unit(Side.Empire, ImpArmy, T);
        w.Stub!.Expect(0, 99, draw, 99, 99);
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, new[] { bothan });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var roll = Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Equal((45, 60, detected), (roll.X, roll.Chance, roll.Detected));
    }

    // m5-missions MI4, MI5, MI6, MI7, MI8 (the formula and the table)
    [Theory]
    [InlineData(80, 20, 0, 60, 40, 70)]   // MI4: 60 * 35 / 100 = 21
    [InlineData(60, 10, 0, 31, 41, 60)]   // MI5: 31 * 35 / 100 = 10 (truncated; 11 would give 70)
    [InlineData(70, 20, 2, 0, 49, 60)]    // MI6: two non-decoy special forces
    [InlineData(100, 0, 0, 0, 101, 25)]   // MI7
    [InlineData(40, 70, 0, 0, -29, 100)]  // MI8: no row
    public void MI4_to_MI8_DetectionInput(int avg, int det, int sfs, int officer, int x, int p)
    {
        var m = new MissionWorld().M;
        Assert.Equal(x, m.DetectionX(avg, det, sfs, officer));
        Assert.Equal(p, m.FoilChance(x));
    }

    // m5-missions MI4 in a game: the landed regiment's officer is the system General (Piett, espionage 60)
    [Fact]
    public void MI4_SystemGeneralIsTheOfficerOfALandedRegiment()
    {
        var (w, _) = EspionageAtT(w =>
        {
            w.Agent(Leia, T, esp: 80);
            w.Unit(Side.Empire, ImpFleet, T); // detection 20
            w.Agent(Piett, T, esp: 60);
            w.Ch.SetSystemGeneral(Piett, T);
        }, new[] { 99, 99 });
        var roll = Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Equal((40, 70), (roll.X, roll.Chance));
    }

    // m5-missions MI6 in a game: two Bothan Spies (70 each) against detection 20
    [Fact]
    public void MI6_TwoSpecialForces()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        int a = w.Unit(Side.Alliance, Bothan, T), b = w.Unit(Side.Alliance, Bothan, T);
        w.Unit(Side.Empire, ImpFleet, T);
        w.Stub!.Expect(0, 99, 99, 99, 99);
        w.Start(Side.Alliance, MissionKind.Espionage, Array.Empty<int>(), T, new[] { a, b });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal((49, 60), (w.Events<MissionDetectionRoll>().Single().X, w.Events<MissionDetectionRoll>().Single().Chance));
    }

    // m5-missions MI7: x = 101 (espionage 105 against a War Droid Regiment, detection 5), p = 25: draw 24 detects, 25 does not
    [Theory]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void MI7_LastRow(int draw, bool detected)
    {
        var (w, _) = EspionageAtT(w => { w.Agent(Leia, T, esp: 105); w.Unit(Side.Empire, WarDroid, T); }, new[] { draw, 99 });
        var roll = w.Events<MissionDetectionRoll>().Single();
        Assert.Equal((101, 25, detected), (roll.X, roll.Chance, roll.Detected));
    }

    // m5-missions MI8: x below -20 has no row: p = 100, the draw is still made (one draw consumed), detected
    [Fact]
    public void MI8_NoRowStillDraws()
    {
        var (w, _) = EspionageAtT(w => { w.Agent(Leia, T, esp: 1); w.Unit(Side.Empire, DarkTrooper, T); }, new[] { 99 });
        var roll = w.Events<MissionDetectionRoll>().Single();
        Assert.Equal((-28, 100, 99, true), (roll.X, roll.Chance, roll.Roll, roll.Detected));
        Assert.Equal(1, Rolls(w));
    }

    // m5-missions MI9: no defenders at the target: no decoy or detection draw; the first draw of the iteration is the member's success roll
    [Fact]
    public void MI9_NoDefendersNoDetectionDraw()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Agent(Leia, T, dip: 100);
        w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
        w.Game.Step();
        int from = w.Stub!.Requests.Count;
        w.RunIteration(w.LastMission);
        var req = w.RequestsSince(from);
        Assert.Equal((0, 99), req[0]);
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Single(w.Events<MissionMemberRoll>());
    }

    // m5-missions MI10: two defenders (detection 10), x = 51, p = 50; draws 70, 49: A not, B detects; draw 10: one draw, A detects
    [Theory]
    [InlineData(new[] { 70, 49 }, 2, 1)]
    [InlineData(new[] { 10 }, 1, 0)]
    public void MI10_WalkStopsAtTheFirstDetection(int[] draws, int expectedDraws, int detector)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Agent(Piett, T, esp: 60);
        int a = w.Unit(Side.Alliance, RebArmy, T), b = w.Unit(Side.Alliance, RebArmy, T);
        w.Stub!.Expect(0, 99, draws);
        w.Start(Side.Empire, MissionKind.Espionage, new[] { Piett }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var rolls = w.Events<MissionDetectionRoll>();
        Assert.Equal(expectedDraws, rolls.Count);
        Assert.All(rolls, r => Assert.Equal((51, 50), (r.X, r.Chance)));
        Assert.Equal(new[] { a, b }[detector], w.Events<MissionFoiled>().Single().Defender);
        Assert.Equal(expectedDraws, Rolls(w));
        var seen = Assert.Single(w.Events<Rebellion.Core.Fog.AgentDetected>()); // the fog's MissionDetected (m5-fog-and-hq HR11)
        Assert.Equal((Side.Alliance, T, Side.Empire), (seen.Viewer, seen.System, seen.AgentSide));
    }

    // m5-missions MI11: one defender (detection 20), one decoy (espionage 60), DT: x = 40, p = 50; draw 49: decoyed, no detection draw
    [Fact]
    public void MI11_DecoyedDefenderLeavesTheWalk()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50);
        int inf = w.Unit(Side.Alliance, Infiltrators, T);
        w.Unit(Side.Empire, ImpFleet, T);
        w.Stub!.Expect(0, 99, 49, 99);
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, decoys: new[] { inf });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var d = Assert.Single(w.Events<MissionDecoyRoll>());
        Assert.Equal((40, 50, 49, true), (d.X, d.Chance, d.Roll, d.Decoyed));
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Single(w.Events<MissionMemberRoll>()); // the iteration went on to the success rolls
        Assert.Equal(2, Rolls(w));
    }

    // m5-missions MI12: two defenders, one decoy; decoy draws 49 (A), 50 (B); then a detection draw for B only
    [Fact]
    public void MI12_OneDecoyTriesEveryDefender()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50);
        int inf = w.Unit(Side.Alliance, Infiltrators, T);
        int a = w.Unit(Side.Empire, ImpFleet, T), b = w.Unit(Side.Empire, ImpFleet, T);
        w.Stub!.Expect(0, 99, 49, 50, 99, 99);
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, decoys: new[] { inf });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(new[] { (a, true), (b, false) }, w.Events<MissionDecoyRoll>().Select(r => (r.Defender, r.Decoyed)));
        Assert.Equal(b, w.Events<MissionDetectionRoll>().Single().Defender);
    }

    // m5-missions MI13: one defender, two decoys (60 and 55); draws 50 (decoy 1 fails), 10 (decoy 2 succeeds): decoyed after two draws
    [Fact]
    public void MI13_SecondDecoySucceeds()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50);
        int d1 = w.Unit(Side.Alliance, Infiltrators, T), d2 = w.Unit(Side.Alliance, Guerrillas, T);
        w.Unit(Side.Empire, ImpFleet, T);
        w.Stub!.Expect(0, 99, 50, 10, 99);
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, decoys: new[] { d2, d1 });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(new[] { (d1, 40, false), (d2, 35, true) }, w.Events<MissionDecoyRoll>().Select(r => (r.Decoy, r.X, r.Decoyed)));
        Assert.Empty(w.Events<MissionDetectionRoll>());
    }

    // m5-missions MI14: Espionage member espionage 50: ESPIMSTB(50) = 50: draw 49 succeeds, 50 fails
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public void MI14_EspionageTable(int draw, bool ok)
    {
        var (w, _) = EspionageAtT(w => w.Agent(Leia, T, esp: 50), new[] { draw });
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((50, 50, ok), (r.Input, r.Chance, r.Success));
        Assert.Equal(ok ? MissionOutcome.Succeeded : MissionOutcome.Failed, w.Events<MissionEnded>().Single().Outcome);
        Assert.Equal(ok, w.Events<SystemRevealed>().Count == 1);
    }

    // m5-missions MI15: Assassination, combat 70 against 70 (x = 0): ASSNMSTB(0) = 25: draw 24 succeeds, 25 fails
    [Theory]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void MI15_AssassinationTable(int draw, bool ok)
    {
        var w = new MissionWorld();
        w.Agent(Vader, T, com: 70);
        w.Agent(Wedge, T, com: 70);
        w.Stub!.Expect(0, 99, draw);
        w.Start(Side.Empire, MissionKind.Assassination, new[] { Vader }, null, targetChar: Wedge);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((0, 25, ok), (r.Input, r.Chance, r.Success));
        Assert.Equal(ok, w.Ch.IsDead(Wedge)); // a minor dies (MinorsCanDie DEFAULT on)
    }

    // m5-missions MI16, MI17, MI21: table anchors
    [Theory]
    [InlineData("ASSNMSTB", 100, 55)] [InlineData("ASSNMSTB", 150, 55)]                                    // MI16: cap
    [InlineData("ABDCMSTB", -100, 5)] [InlineData("ABDCMSTB", 0, 50)] [InlineData("ABDCMSTB", 100, 99)]    // MI17
    [InlineData("RCRTMSTB", 100, 80)] [InlineData("RCRTMSTB", 0, 10)]                                       // MI21 (term 0)
    [InlineData("DIPLMSTB", 120, 70)] [InlineData("DIPLMSTB", 0, 20)] [InlineData("DIPLMSTB", -60, 1)]     // MI18, MI19, MI20
    [InlineData("SUBDMSTB", 100, 99)] [InlineData("INCTMSTB", -20, 1)] [InlineData("INCTMSTB", -70, 1)]    // MI22, MI23
    [InlineData("ESPIMSTB", 50, 50)] [InlineData("SBTGMSTB", 50, 50)] [InlineData("DSSBMSTB", 100, 99)] [InlineData("RESCMSTB", 10, 10)]
    public void MI16_MI17_MI21_Anchors(string table, int x, int p)
    {
        var m = new MissionWorld().M;
        Assert.Equal(p, m.Lookup(m.Data.Table(table), x));
    }

    // m5-missions MI20: below the first row: FirstRowValue gives 1 (draw 0 succeeds, 1 fails); Zero makes both fail
    [Fact]
    public void MI20_BelowFirstRow()
    {
        var t = new MissionWorld().M.Data.Table("DIPLMSTB");
        Assert.Equal(1, MissionModule.Lookup(t, -60, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue));
        Assert.Equal(0, MissionModule.Lookup(t, -60, MstbLookupMode.Step, MstbBelowFirstRow.Zero));
        Assert.Equal(100, MissionModule.Lookup(t, -60, MstbLookupMode.Step, MstbBelowFirstRow.Always));
        // in a game: Leia diplomacy 40 at Alliance support 0 (x = -100 + 40 = -60)
        foreach (var (mode, draw, ok) in new[] { (MstbBelowFirstRow.FirstRowValue, 0, true), (MstbBelowFirstRow.FirstRowValue, 1, false), (MstbBelowFirstRow.Zero, 0, false) })
        {
            var w = new MissionWorld(new MissionParams { MstbBelowFirstRow = mode });
            w.Hold(T, null, 0);
            w.Agent(Leia, T, dip: 40);
            w.Stub!.Expect(0, 99, draw);
            w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
            w.Game.Step();
            w.RunIteration(w.LastMission);
            var r = w.Events<MissionMemberRoll>().Single();
            Assert.Equal((-60, ok), (r.Input, r.Success));
        }
    }

    // The Linear lookup interpolates between rows (truncating toward zero); at a row both modes agree. rule: §Mission tables (MstbLookup)
    [Fact]
    public void LinearLookupBetweenRows()
    {
        var t = new MissionWorld().M.Data.Table("ESPIMSTB");
        Assert.Equal(30, MissionModule.Lookup(t, 30, MstbLookupMode.Linear, MstbBelowFirstRow.FirstRowValue)); // 10 + 20 * 40 / 40
        Assert.Equal(10, MissionModule.Lookup(t, 30, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue));
        Assert.Equal(74, MissionModule.Lookup(t, 75, MstbLookupMode.Linear, MstbBelowFirstRow.FirstRowValue)); // 50 + 25 * 49 / 50 = 74
        Assert.Equal(50, MissionModule.Lookup(t, 50, MstbLookupMode.Linear, MstbBelowFirstRow.FirstRowValue));
    }

    // m5-missions MI18, MI19: Diplomacy input (support - rival support) + diplomacy
    [Theory]
    [InlineData(120, 50, 120, 70, 69, true)]
    [InlineData(120, 50, 120, 70, 70, false)]
    [InlineData(60, 20, 0, 20, 19, true)]
    public void MI18_MI19_DiplomacyInput(int dip, int support, int x, int p, int draw, bool ok)
    {
        var w = new MissionWorld();
        w.Hold(T, null, support); // neutral: no garrison check
        w.Agent(Leia, T, dip: dip);
        w.Stub!.Expect(0, 99, draw);
        w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((x, p, ok), (r.Input, r.Chance, r.Success));
    }

    // m5-missions MI22: Subdue, Empire holds, Empire support 20, leadership 40: x = (80 - 20) + 40 = 100, p = 99
    [Fact]
    public void MI22_SubdueInput()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 80);
        w.Game.Step(); // 6.1: N = 4, G = 0: the uprising starts
        Assert.True(w.Host.Support.IsUprising(T));
        w.Agent(Veers, T, lead: 40);
        w.Start(Side.Empire, MissionKind.SubdueUprising, new[] { Veers }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((100, 99), (r.Input, r.Chance));
    }

    // m5-missions MI23: Incite by the Alliance on an Empire system, controller support 50, leadership 30: x = -20, p = 1; Documented: x = -70, p = 1
    [Theory]
    [InlineData(InciteInputMode.LeadershipMinusControllerSupport, 0, -20, true)]
    [InlineData(InciteInputMode.LeadershipMinusControllerSupport, 1, -20, false)]
    [InlineData(InciteInputMode.Documented, 0, -70, true)]
    public void MI23_InciteInput(InciteInputMode mode, int draw, int x, bool ok)
    {
        var w = new MissionWorld(new MissionParams { InciteInputMode = mode });
        w.Hold(T, Side.Empire, 50);
        w.Agent(Leia, T, lead: 30);
        w.Stub!.Expect(0, 99, draw);
        w.Start(Side.Alliance, MissionKind.InciteUprising, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().First();
        Assert.Equal((x, 1, ok), (r.Input, r.Chance, r.Success));
    }

    private static MissionWorld ResearchWorld(params (int Id, int Skill)[] members)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Host.Economy.AddFacility(T, FacilityKind.OrbitalShipyard);
        foreach (var (id, s) in members) w.Agent(id, T, ship: s);
        return w;
    }

    // m5-missions MI24: Ship Research, skill 40: rand(0..99) < skill, no table
    [Theory]
    [InlineData(39, true)]
    [InlineData(40, false)]
    public void MI24_ResearchRoll(int draw, bool ok)
    {
        var w = ResearchWorld((Leia, 40));
        w.Stub!.Expect(0, 99, draw);
        w.Start(Side.Alliance, MissionKind.ShipResearch, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((40, ok), (r.Chance, r.Success));
        Assert.Equal(MissionPhase.Working, w.M.Get(w.LastMission)!.Phase); // research repeats
    }

    // m5-missions MI25 (and m5-research RS29): A (40) succeeds, RollMissionPoints draw 3 gives 4 points and +1 skill; B fails; AddMissionPoints 4; draw order 10, 3, 70
    [Fact]
    public void MI25_ResearchDrawOrderAndPoints()
    {
        var w = ResearchWorld((Leia, 40), (Han, 40));
        w.Stub!.Expect(0, 99, 10, 70).Expect(0, 6, 3);
        w.Start(Side.Alliance, MissionKind.ShipResearch, new[] { Han, Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(new[] { (0, 99), (0, 6), (0, 99) }, w.Stub.Requests.Where(r => r is (0, 99) or (0, 6)));
        Assert.Equal(41, w.Ch.Get(Leia)!.Skill(Rebellion.Core.Characters.Skill.ShipDesign));
        Assert.Equal(40, w.Ch.Get(Han)!.Skill(Rebellion.Core.Characters.Skill.ShipDesign));
        Assert.Equal(4, w.Host.Research.Points(Side.Alliance, Rebellion.Core.Research.ResearchKind.Ship));
    }

    // m5-missions MI26: Reconnaissance on an undefended target: automatic success, no draw at all; SystemRevealed
    [Fact]
    public void MI26_ReconIsAutomatic()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 10);
        w.Start(Side.Alliance, MissionKind.Reconnaissance, new[] { Leia }, T);
        w.Game.Step();
        int from = w.Stub!.Requests.Count;
        w.RunIteration(w.LastMission);
        Assert.Empty(w.RequestsSince(from));
        Assert.Equal((Side.Alliance, T, MissionKind.Reconnaissance), (w.Events<SystemRevealed>().Single().Side, w.Events<SystemRevealed>().Single().Target, w.Events<SystemRevealed>().Single().Source));
        Assert.True(w.Host.IsExplored(T, Side.Alliance)); // the fog's Recon reveal
    }

    // A Sabotage team of Leia and Han (espionage 50, combat 50) at T with three facilities and one Imperial Fleet Regiment (detection 20).
    private static (MissionWorld W, List<int> Exposed) SabotageWorld(int[] rolls, bool decoy, bool defender = true, bool hooks = true, bool infiltratorMember = false)
    {
        var w = new MissionWorld(new MissionParams { ExposureHooks = hooks }, decoys: decoy);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50, com: 50);
        w.Agent(Han, T, esp: 50, com: 50);
        for (int i = 0; i < 3; i++) w.Host.Economy.AddFacility(T, FacilityKind.Mine);
        if (defender) w.Unit(Side.Empire, ImpFleet, T);
        var decoys = decoy ? new[] { w.Unit(Side.Alliance, Infiltrators, T) } : Array.Empty<int>();
        var sfs = infiltratorMember ? new[] { w.Unit(Side.Alliance, Infiltrators, T) } : Array.Empty<int>();
        var exposed = new List<int>();
        w.M.OnTeamExposed = (ctx, c, side, cause) => { Assert.Equal(Side.Empire, side); exposed.Add(c.Id * 10 + (int)cause); };
        w.Stub!.Expect(0, 99, rolls).Expect(0, 2, 0, 2); // the Sabotage timer (0..2) draws 0, then the facility pick draws 2
        w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Han, Leia }, T, sfs, decoys);
        w.Game.Step();
        return (w, exposed);
    }

    // m5-missions MI39: the full draw log: 50 (decoy fails), 70 (x = 31, p = 70: not detected), 49 (Leia succeeds), 99 (Han fails), 2 (pick)
    [Fact]
    public void MI39_FullDrawLog()
    {
        var (w, exposed) = SabotageWorld(new[] { 50, 70, 49, 99 }, decoy: true);
        var facilities = w.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.Facility).Select(o => o.Id).ToList();
        int from = w.Stub!.Requests.Count;
        w.RunIteration(w.LastMission);
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99), (0, 99), (0, 2) }, w.RequestsSince(from));
        Assert.False(w.Events<MissionDecoyRoll>().Single().Decoyed);
        Assert.Equal((31, 70, false), (w.Events<MissionDetectionRoll>().Single().X, w.Events<MissionDetectionRoll>().Single().Chance, w.Events<MissionDetectionRoll>().Single().Detected));
        Assert.Equal(new[] { (Leia, 50, true), (Han, 50, false) }, w.Events<MissionMemberRoll>().Select(r => (r.Member, r.Chance, r.Success)));
        var gone = w.Events<FacilityDestroyed>().Single();
        Assert.Equal((facilities[2], DestroyReason.Sabotage), (gone.Facility, gone.Reason));
        Assert.Equal(1, w.Events<MissionSucceeded>().Single().Effects); // AnyMember: once
        Assert.Equal(MissionOutcome.Succeeded, w.Events<MissionEnded>().Single().Outcome);
        Assert.Empty(exposed);
    }

    // m5-missions MI40: as MI39 with detection draw 69: MissionFoiled, no success draws, OnTeamExposed(Foiled) for Leia then Han; two draws
    [Fact]
    public void MI40_FoiledCallsTheExposureHook()
    {
        var (w, exposed) = SabotageWorld(new[] { 50, 69 }, decoy: true);
        int from = w.Stub!.Requests.Count;
        w.RunIteration(w.LastMission);
        Assert.Equal(2, w.RequestsSince(from).Count);
        Assert.Single(w.Events<MissionFoiled>());
        Assert.Empty(w.Events<MissionMemberRoll>());
        Assert.Equal(new[] { Leia * 10 + (int)ExposureCause.Foiled, Han * 10 + (int)ExposureCause.Foiled }, exposed);
        Assert.Equal(3, w.Host.Economy.Objects.Count(o => o.Category == ObjectCategory.Facility));
    }

    // m5-missions MI41: no defenders, every member fails: Failed, OnTeamExposed(Failed) for the characters in id order; no special force removed
    [Fact]
    public void MI41_FailedCallsTheExposureHook()
    {
        var (w, exposed) = SabotageWorld(new[] { 99, 99, 99 }, decoy: false, defender: false, infiltratorMember: true);
        int sfCount = w.Host.Economy.Objects.Count(o => o.Category == ObjectCategory.SpecialForce);
        w.RunIteration(w.LastMission);
        Assert.Equal(3, w.Events<MissionMemberRoll>().Count);
        Assert.Single(w.Events<MissionFailed>());
        Assert.Equal(MissionOutcome.Failed, w.Events<MissionEnded>().Single().Outcome);
        Assert.Equal(new[] { Leia * 10 + (int)ExposureCause.Failed, Han * 10 + (int)ExposureCause.Failed }, exposed);
        Assert.Equal(sfCount, w.Host.Economy.Objects.Count(o => o.Category == ObjectCategory.SpecialForce));
    }

    // m5-missions MI60: ExposureHooks off: a foiled team makes no hook call; everything else as MI40
    [Fact]
    public void MI60_ExposureHooksOff()
    {
        var (w, exposed) = SabotageWorld(new[] { 50, 69 }, decoy: true, hooks: false);
        w.RunIteration(w.LastMission);
        Assert.Single(w.Events<MissionFoiled>());
        Assert.Empty(exposed);
    }

    // m5-missions MI42: a Diplomacy iteration fails (no detection): no hook, the next timer is drawn, the mission stays Working
    [Fact]
    public void MI42_RepeatingFailureIsNotPenalised()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Agent(Leia, T, dip: 100);
        int calls = 0;
        w.M.OnTeamExposed = (_, _, _, _) => calls++;
        w.Stub!.Expect(0, 99, 99);
        w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
        w.Game.Step();
        int timers = w.Stub.Count(0, 10);
        w.RunIteration(w.LastMission);
        Assert.Equal(0, calls);
        Assert.Equal(timers + 1, w.Stub.Count(0, 10));
        Assert.Equal(MissionPhase.Working, w.M.Get(w.LastMission)!.Phase);
        Assert.False(w.Events<MissionIterationDone>().Single().Success);
    }

    // m5-missions MI43: Diplomacy foiled by a defender: the mission ends Foiled, the hook is called, no next timer draw
    [Fact]
    public void MI43_RepeatingMissionFoiledEnds()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Agent(Leia, T, dip: 100);
        w.Unit(Side.Empire, ImpArmy, T);
        int calls = 0;
        w.M.OnTeamExposed = (_, c, side, cause) => { calls++; Assert.Equal((Leia, Side.Empire, ExposureCause.Foiled), (c.Id, side, cause)); };
        w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
        w.Game.Step();
        int timers = w.Stub!.Count(0, 10);
        w.RunIteration(w.LastMission);
        Assert.Equal(1, calls);
        Assert.Equal(timers, w.Stub.Count(0, 10));
        Assert.Equal(MissionOutcome.Foiled, w.Events<MissionEnded>().Single().Outcome);
    }

    // m5-missions MI57: DetectionAtLanding off and a defended target: landing makes only the timer draw
    [Fact]
    public void MI57_NoDetectionAtLanding()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50);
        w.Unit(Side.Empire, ImpArmy, T);
        int from = w.Stub!.Requests.Count;
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T);
        w.Game.Step();
        Assert.Equal(new[] { (0, 20) }, w.RequestsSince(from));
        Assert.Single(w.Events<MissionLanded>());
        // switched on: the walk runs once at landing, after the timer draw
        var on = new MissionWorld(new MissionParams { DetectionAtLanding = true });
        on.Hold(T, Side.Empire, 0);
        on.Agent(Leia, T, esp: 50);
        on.Unit(Side.Empire, ImpArmy, T);
        on.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T);
        on.Game.Step();
        Assert.Equal(new[] { (0, 20), (0, 99) }, on.Stub!.Requests);
        Assert.Single(on.Events<MissionFoiled>()); // draw 0 < 60
    }

    // SuccessRule EachMember: the effect applies once per successful member. rule: §2 step 3
    [Fact]
    public void EachMemberAppliesTheEffectPerSuccess()
    {
        var w = new MissionWorld(new MissionParams { SuccessRule = SuccessRule.EachMember });
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50, com: 50);
        w.Agent(Han, T, esp: 50, com: 50);
        for (int i = 0; i < 3; i++) w.Host.Economy.AddFacility(T, FacilityKind.Mine);
        w.Stub!.Expect(0, 99, 0, 0);
        w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Leia, Han }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(2, w.Events<MissionSucceeded>().Single().Effects);
        Assert.Equal(2, w.Events<FacilityDestroyed>().Count);
    }
}
