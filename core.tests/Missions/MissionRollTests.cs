using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-missions §Tests MI1 to MI26 and MI39 to MI43, MI57, MI60: the detection walk, the decoy phase, the success tables and the draw order.
public class MissionRollTests
{
    private static int Rolls(MissionWorld w) => w.Stub!.Count(0, 99);

    // An Alliance Espionage team to T (Empire-held, Empire support 100: no garrison needed). B2d: guards of a planet target are the special forces
    // on the enemy-held planet; a setup that puts the team at H makes it fly in (the leaving check at H and the arriving check at T draw nothing).
    private static (MissionWorld W, int Mission) EspionageAtT(Action<MissionWorld> setup, int[] rolls, bool decoys = false, MissionParams? p = null,
        int[]? chars = null, int[]? sfs = null, int[]? decoyIds = null)
    {
        var w = new MissionWorld(p, decoys);
        w.Hold(T, Side.Empire, 0);
        setup(w);
        // B2b: the check opens with one betrayal draw per member
        w.Stub!.Expect(0, 99, Bt((chars?.Length ?? 1) + (sfs?.Length ?? 0) + (decoyIds?.Length ?? 0), rolls));
        var seq = w.Start(Side.Alliance, MissionKind.Espionage, chars ?? new[] { Leia }, T, sfs, decoyIds);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        int id = w.LastMission;
        w.RunIteration(id);
        return (w, id);
    }

    // m5-missions MI1
    [Theory]
    [InlineData(-21, 99)] [InlineData(-20, 99)] [InlineData(-19, 98)] [InlineData(-10, 98)] [InlineData(-9, 96)] [InlineData(0, 96)]
    [InlineData(1, 94)] [InlineData(10, 94)] [InlineData(11, 90)] [InlineData(100, 30)] [InlineData(101, 25)] [InlineData(500, 25)]
    public void MI1_FoilTable(int x, int p) => Assert.Equal(p, new MissionWorld().M.FoilChance(x));

    // m5-missions MI2: average 50 against detection 50 gives x = 1, p = 94 (here, B2d: espionage 1 against an Imperial Probe Droid on the planet,
    // detection 0 (SPECFCSD word 13), x = 2, the same row)
    [Theory]
    [InlineData(93, true)]
    [InlineData(94, false)]
    public void MI2_EqualSkillDetects94Percent(int draw, bool detected)
    {
        var (w, id) = EspionageAtT(w => { w.Agent(Leia, H, esp: 1); w.Unit(Side.Empire, Probe, T); }, new[] { draw, 99 });
        var roll = Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Equal((2, 94, draw, detected), (roll.X, roll.Chance, roll.Roll, roll.Detected));
        Assert.Equal(detected, w.Events<MissionFoiled>().Count == 1);
        Assert.Equal(detected ? 0 : 1, w.Events<MissionMemberRoll>().Count); // a detection blocks every success roll
        Assert.Equal(detected ? MissionOutcome.Foiled : MissionOutcome.Failed, w.Events<MissionEnded>().Single().Outcome);
    }

    // m5-missions MI3: Bothan Spies (70) plus a character (50): average 60, one special force, a Probe Droid guard (detection 0, B2d):
    // x = 60 - 0 - 1 + 1 = 60, p = 50
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public void MI3_BothanAndCharacter(int draw, bool detected)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 50);
        int bothan = w.Unit(Side.Alliance, Bothan, H);
        w.Unit(Side.Empire, Probe, T);
        w.Stub!.Expect(0, 99, Bt(2, draw, 99, 99)); // B2b: two betrayal draws first
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, new[] { bothan });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var roll = Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Equal((60, 50, detected), (roll.X, roll.Chance, roll.Detected));
    }

    // m5-missions MI4, MI5, MI6, MI7, MI8 (the formula and the table)
    [Theory]
    [InlineData(80, 20, 0, 60, 40, 70)]   // MI4: 60 * 35 / 100 = 21
    [InlineData(60, 10, 0, 31, 41, 60)]   // MI5: 31 * 35 / 100 = 10 (truncated; 11 would give 70)
    [InlineData(70, 20, 2, 0, 49, 60)]    // MI6: two non-decoy special forces
    [InlineData(100, 0, 0, 0, 101, 25)]   // MI7
    [InlineData(40, 70, 0, 0, -29, 99)]   // MI8: below the first row, the first row (B2a: was 100)
    public void MI4_to_MI8_DetectionInput(int avg, int det, int sfs, int officer, int x, int p)
    {
        var m = new MissionWorld().M;
        Assert.Equal(x, m.DetectionX(avg, det, sfs, officer));
        Assert.Equal(p, m.FoilChance(x));
    }

    // m5-missions MI4 in a game (B2d): a guarding ship's officer is its fleet's Admiral (Piett, espionage 60): the arriving check (orbit) walks the
    // Imperial Star Destroyer (detection 10): x = 80 - 10 - 0 - 21 + 1 = 50, p = 60
    [Fact]
    public void MI4_FleetAdmiralIsTheOfficerOfAGuardingShip()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 80);
        int fleet = w.Host.Fleets.AddFleet(Side.Empire, "E", new[] { "Imperial Star Destroyer" }, T);
        w.Agent(Piett, T, esp: 60); w.Ch.SetPost(Piett, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 99, 99, 99); // the arriving walk, then the job check's betrayal draw and the success roll
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var roll = Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Equal((50, 60), (roll.X, roll.Chance));
    }

    // m5-missions MI6 in a game: two Bothan Spies (70 each) against a Probe Droid (detection 0, B2d): x = 70 - 0 - 2 + 1 = 69, p = 45
    [Fact]
    public void MI6_TwoSpecialForces()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        int a = w.Unit(Side.Alliance, Bothan, H), b = w.Unit(Side.Alliance, Bothan, H);
        w.Unit(Side.Empire, Probe, T);
        w.Stub!.Expect(0, 99, Bt(2, 99, 99, 99)); // B2b: two betrayal draws first
        w.Start(Side.Alliance, MissionKind.Espionage, Array.Empty<int>(), T, new[] { a, b });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal((69, 45), (w.Events<MissionDetectionRoll>().Single().X, w.Events<MissionDetectionRoll>().Single().Chance));
    }

    // m5-missions MI7: x = 101 (espionage 100 against a Probe Droid, detection 0, B2d), p = 25: draw 24 detects, 25 does not
    [Theory]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void MI7_LastRow(int draw, bool detected)
    {
        var (w, _) = EspionageAtT(w => { w.Agent(Leia, H, esp: 100); w.Unit(Side.Empire, Probe, T); }, new[] { draw, 99 });
        var roll = w.Events<MissionDetectionRoll>().Single();
        Assert.Equal((101, 25, detected), (roll.X, roll.Chance, roll.Detected));
    }

    // m5-missions MI8: x below -20: the first row, p = 99 (B2a: was 100), the draw is still made. B2d: at the arriving check a Lancer Frigate
    // (detection 30) guards: x = 1 - 30 + 1 = -28; draw 98 detects; the escape duel follows (one draw), no betrayal (job check only)
    [Fact]
    public void MI8_NoRowStillDraws()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 1);
        w.Host.Fleets.AddFleet(Side.Empire, "E", new[] { "Lancer Frigate" }, T);
        w.Stub!.Expect(0, 99, 98, 0);
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T);
        w.Run(5);
        var roll = w.Events<MissionDetectionRoll>().Single();
        Assert.Equal((-28, 99, 98, true), (roll.X, roll.Chance, roll.Roll, roll.Detected));
        Assert.Equal(3, Rolls(w)); // detection, duel, and (B2e moved) Leia's Hurt roll after the duel (combat 0: p = 100, a hit; a major: no death draw)
        Assert.Equal(MissionFoiledAtArrival, w.Events<MissionFoiled>().Single().EndCode);
    }

    private const int MissionFoiledAtArrival = MissionModule.FoilCodeAtTarget;

    // m5-missions MI9: no defenders at the target: no decoy or detection draw; after the betrayal draw (B2b) the next draw is the member's success roll
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
        Assert.Equal(new[] { (0, 99), (0, 99) }, req.Take(2));
        Assert.Single(w.Events<MissionBetrayalRoll>());
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Single(w.Events<MissionMemberRoll>());
    }

    // m5-missions MI10: two guards (B2d: Guerrillas on the Alliance planet, detection 0), x = 61, p = 45; draws 70, 44: A not, B detects; draw 10:
    // one draw, A detects. The foiled handling then makes Piett's escape duel against one of the two guards (a pick draw, then the duel draw).
    [Theory]
    [InlineData(new[] { 70, 44 }, 2, 1)]
    [InlineData(new[] { 10 }, 1, 0)]
    public void MI10_WalkStopsAtTheFirstDetection(int[] draws, int expectedDraws, int detector)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Agent(Piett, H, esp: 60);
        int a = w.Unit(Side.Alliance, Guerrillas, T), b = w.Unit(Side.Alliance, Guerrillas, T);
        w.Stub!.Expect(0, 99, Bt(1, draws)); // B2b: Piett's betrayal draw first
        w.Start(Side.Empire, MissionKind.Espionage, new[] { Piett }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var rolls = w.Events<MissionDetectionRoll>();
        Assert.Equal(expectedDraws, rolls.Count);
        Assert.All(rolls, r => Assert.Equal((61, 45), (r.X, r.Chance)));
        Assert.Equal(new[] { a, b }[detector], w.Events<MissionFoiled>().Single().Defender);
        // and the betrayal draw and Piett's duel (B2d); B2e moved: his Hurt roll (combat 0: p = 100, a hit) and the minor's death draw
        Assert.Equal(expectedDraws + 4, Rolls(w));
        Assert.Equal(1, w.Stub.Count(0, 1)); // the duel's guard pick among the two guards
        var seen = Assert.Single(w.Events<Praedra.Core.Fog.AgentDetected>()); // the fog's MissionDetected (m5-fog-and-hq HR11)
        Assert.Equal((Side.Alliance, T, Side.Empire), (seen.Viewer, seen.System, seen.AgentSide));
    }

    // m5-missions MI11: one guard (a Probe Droid, detection 0, B2d), one decoy (espionage 60), DT: x = 60, p = 50; draw 49: decoyed, no detection draw
    [Fact]
    public void MI11_DecoyedDefenderLeavesTheWalk()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 50);
        int inf = w.Unit(Side.Alliance, Infiltrators, H);
        w.Unit(Side.Empire, Probe, T);
        w.Stub!.Expect(0, 99, Bt(2, 49, 99)); // B2b: betrayal draws for Leia and the decoy first
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, decoys: new[] { inf });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var d = Assert.Single(w.Events<MissionDecoyRoll>());
        Assert.Equal((60, 50, 49, true), (d.X, d.Chance, d.Roll, d.Decoyed));
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Single(w.Events<MissionMemberRoll>()); // the iteration went on to the success rolls
        Assert.Equal(4, Rolls(w)); // two betrayal draws, the decoy roll, Leia's success roll
    }

    // m5-missions MI12: two guards, one decoy; decoy draws 49 (A), 50 (B, failed: its escape duel follows, B2d); then a detection draw for B only
    [Fact]
    public void MI12_OneDecoyTriesEveryDefender()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 50);
        int inf = w.Unit(Side.Alliance, Infiltrators, H);
        int a = w.Unit(Side.Empire, Probe, T), b = w.Unit(Side.Empire, Probe, T);
        w.Stub!.Expect(0, 99, Bt(2, 49, 50, 99, 99, 99)); // B2b: two betrayal draws first; 99 is the failed decoy's duel
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, decoys: new[] { inf });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(new[] { (a, true), (b, false) }, w.Events<MissionDecoyRoll>().Select(r => (r.Defender, r.Decoyed)));
        Assert.Equal(b, w.Events<MissionDetectionRoll>().Single().Defender);
        Assert.Equal((inf, b, false), w.Events<MissionDuel>().Select(e => (e.Member, e.Guard, e.Escaped)).Single());
    }

    // m5-missions MI13 (B2a): one guard (a Probe Droid, B2d), two decoys (60 and 55): one pick k = rand(0..1) = 1 (the second decoy, Guerrillas), then one roll 10:
    // decoyed after two draws (pick, roll). Before B2a every decoy was tried in turn.
    [Fact]
    public void MI13_SecondDecoySucceeds()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 50);
        int d1 = w.Unit(Side.Alliance, Infiltrators, H), d2 = w.Unit(Side.Alliance, Guerrillas, H);
        w.Unit(Side.Empire, Probe, T);
        w.Stub!.Expect(0, 1, 1);
        w.Stub!.Expect(0, 99, Bt(3, 10, 99)); // B2b: three betrayal draws first
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, decoys: new[] { d2, d1 });
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(new[] { (d2, 55, true) }, w.Events<MissionDecoyRoll>().Select(r => (r.Decoy, r.X, r.Decoyed)));
        Assert.Equal(1, w.Stub.Count(0, 1));
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
        w.Agent(Vader, T, com: 70).ForceExperience = 0; // B2e: without his Force, so 70 is his effective combat
        w.Agent(Wedge, T, com: 70);
        w.Stub!.Expect(0, 99, Bt(1, draw)); // B2b: Vader's betrayal draw first
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
            w.Stub!.Expect(0, 99, Bt(1, draw)); // B2b: Leia's betrayal draw first
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
        // full ESPIMSTB since B1 (rows every 10 from 0 to 90, then 100 -> 99): the anchors-only table gave 30 / 10 / 74 / 50
        Assert.Equal(35, MissionModule.Lookup(t, 35, MstbLookupMode.Linear, MstbBelowFirstRow.FirstRowValue)); // 30 + 10 * 5 / 10
        Assert.Equal(30, MissionModule.Lookup(t, 35, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue));
        Assert.Equal(94, MissionModule.Lookup(t, 95, MstbLookupMode.Linear, MstbBelowFirstRow.FirstRowValue)); // 90 + 9 * 5 / 10 = 94
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
        w.Stub!.Expect(0, 99, Bt(1, draw)); // B2b: Leia's betrayal draw first
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
        w.Stub!.Expect(0, 99, Bt(1, draw)); // B2b: Leia's betrayal draw first
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
        w.Stub!.Expect(0, 99, Bt(1, draw)); // B2b: Leia's betrayal draw first
        w.Start(Side.Alliance, MissionKind.ShipResearch, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((40, ok), (r.Chance, r.Success));
        Assert.Equal(MissionPhase.Working, w.M.Get(w.LastMission)!.Phase); // research repeats
    }

    // m5-missions MI25 (and m5-research RS29): A (40) succeeds, RollMissionPoints draw 3 gives 4 points and +1 skill; B fails; AddMissionPoints 4;
    // draw order (after the two betrayal draws, B2b) 10, 3, 70
    [Fact]
    public void MI25_ResearchDrawOrderAndPoints()
    {
        var w = ResearchWorld((Leia, 40), (Han, 40));
        w.Stub!.Expect(0, 99, Bt(2, 10, 70)).Expect(0, 6, 3);
        w.Start(Side.Alliance, MissionKind.ShipResearch, new[] { Han, Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99), (0, 6), (0, 99) }, w.Stub.Requests.Where(r => r is (0, 99) or (0, 6)));
        Assert.Equal(41, w.Ch.Get(Leia)!.BaseSkill(Praedra.Core.Characters.Skill.ShipDesign));
        Assert.Equal(40, w.Ch.Get(Han)!.BaseSkill(Praedra.Core.Characters.Skill.ShipDesign));
        Assert.Equal(4, w.Host.Research.Points(Side.Alliance, Praedra.Core.Research.ResearchKind.Ship));
    }

    // m5-missions MI26: Reconnaissance on an undefended target: automatic success, no success draw (only the betrayal draw, B2b); SystemRevealed
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
        Assert.Equal(new[] { (0, 99) }, w.RequestsSince(from));
        Assert.Single(w.Events<MissionBetrayalRoll>());
        Assert.Equal((Side.Alliance, T, MissionKind.Reconnaissance), (w.Events<SystemRevealed>().Single().Side, w.Events<SystemRevealed>().Single().Target, w.Events<SystemRevealed>().Single().Source));
        Assert.True(w.Host.IsExplored(T, Side.Alliance)); // the fog's Recon reveal
    }

    // A Sabotage team of Leia and Han (espionage 50, combat 50) flies from H to T, which has three facilities and (B2d) one Imperial Probe Droid on
    // the planet as its guard (detection 0, no commander).
    private static (MissionWorld W, List<int> Exposed) SabotageWorld(int[] rolls, bool decoy, bool defender = true, bool hooks = true, bool infiltratorMember = false)
    {
        var w = new MissionWorld(new MissionParams { ExposureHooks = hooks }, decoys: decoy);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 50, com: 50);
        w.Agent(Han, H, esp: 50, com: 50);
        for (int i = 0; i < 3; i++) w.Host.Economy.AddFacility(T, FacilityKind.Mine);
        if (defender) w.Unit(Side.Empire, Probe, T);
        var decoys = decoy ? new[] { w.Unit(Side.Alliance, Infiltrators, H) } : Array.Empty<int>();
        var sfs = infiltratorMember ? new[] { w.Unit(Side.Alliance, Infiltrators, H) } : Array.Empty<int>();
        var exposed = new List<int>();
        w.M.OnTeamExposed = (ctx, c, side, cause) => { Assert.Equal(Side.Empire, side); exposed.Add(c.Id * 10 + (int)cause); };
        // B2b: one betrayal draw per member first; the Sabotage timer (0..2) draws 0, then the facility pick draws 2
        w.Stub!.Expect(0, 99, Bt(2 + decoys.Length + sfs.Length, rolls)).Expect(0, 2, 0, 2);
        w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Han, Leia }, T, sfs, decoys);
        w.Game.Step();
        return (w, exposed);
    }

    // m5-missions MI39: the full draw log: three betrayal draws (Han, Leia, the decoy; B2b), 50 (decoy fails), 99 (its escape duel, B2d: lost,
    // the decoy is destroyed), 70 (x = 51, p = 50: not detected), 49 (Leia succeeds), 99 (Han fails), 2 (pick)
    [Fact]
    public void MI39_FullDrawLog()
    {
        var (w, exposed) = SabotageWorld(new[] { 50, 99, 70, 49, 99 }, decoy: true);
        w.RunUntilWorking(w.LastMission);
        var facilities = w.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.Facility).Select(o => o.Id).ToList();
        int from = w.Stub!.Requests.Count;
        w.RunIteration(w.LastMission);
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99), (0, 99), (0, 99), (0, 99), (0, 99), (0, 99), (0, 2) }, w.RequestsSince(from));
        Assert.False(w.Events<MissionDecoyRoll>().Single().Decoyed);
        Assert.Single(w.Events<MissionDuel>());
        Assert.Equal((51, 50, false), (w.Events<MissionDetectionRoll>().Single().X, w.Events<MissionDetectionRoll>().Single().Chance, w.Events<MissionDetectionRoll>().Single().Detected));
        Assert.Equal(new[] { (Leia, 50, true), (Han, 50, false) }, w.Events<MissionMemberRoll>().Select(r => (r.Member, r.Chance, r.Success)));
        var gone = w.Events<FacilityDestroyed>().Single();
        Assert.Equal((facilities[2], DestroyReason.Sabotage), (gone.Facility, gone.Reason));
        Assert.Equal(1, w.Events<MissionSucceeded>().Single().Effects); // AnyMember: once
        Assert.Equal(MissionOutcome.Succeeded, w.Events<MissionEnded>().Single().Outcome);
        Assert.Empty(exposed);
    }

    // m5-missions MI40 (B2d): as MI39 with detection draw 49: MissionFoiled (end code 4), no success draws, no exposure hook; each member then
    // fights the escape duel against the one guard (no pick draw): combat 50 against no commander, x = 50, p = RLEVADTB(50) = 88: Leia draws 87
    // and escapes (leaves the group, free at T), Han draws 88 and is captured (held at T). Eight draws: three betrayal, the decoy and its duel,
    // the detection, two duels.
    [Fact]
    public void MI40_FoiledTeamDuels()
    {
        // B2e moved: each character's duel is followed by his Hurt roll (combat 50: p = 50); 99 misses both times; Han's capture draws his escape delay.
        var (w, exposed) = SabotageWorld(new[] { 50, 99, 49, 87, 99, 88, 99 }, decoy: true);
        w.RunUntilWorking(w.LastMission);
        int from = w.Stub!.Requests.Count;
        w.RunIteration(w.LastMission);
        Assert.Equal(11, w.RequestsSince(from).Count); // B2e: and the escape timer of Han's capture, rand(0..1000)
        Assert.Equal(MissionModule.FoilCodeAtTarget, w.Events<MissionFoiled>().Single().EndCode);
        Assert.Empty(w.Events<MissionMemberRoll>());
        Assert.Empty(exposed);
        Assert.Equal(new[] { (Leia, 50, 88, true), (Han, 50, 88, false) },
            w.Events<MissionDuel>().Where(e => !e.Decoy).Select(e => (e.Member, e.X, e.Chance, e.Escaped)));
        Assert.Equal((CharacterStatus.Free, T), (w.Ch.Get(Leia)!.Status, w.Ch.Get(Leia)!.System));
        var cap = w.Events<CharacterCaptured>().Single();
        Assert.Equal((Han, Side.Empire, T, MissionModule.CauseMissionDuel), (cap.Character, cap.Captor, cap.System, cap.Cause));
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

    // m5-missions MI60: ExposureHooks off: a foiled team makes no hook call (B2d: on or off, a foiled team duels instead)
    [Fact]
    public void MI60_ExposureHooksOff()
    {
        var (w, exposed) = SabotageWorld(new[] { 50, 99, 49 }, decoy: true, hooks: false);
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
        w.Stub!.Expect(0, 99, Bt(1, 99)); // B2b: Leia's betrayal draw first
        w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
        w.Game.Step();
        int timers = w.Stub.Count(0, 10);
        w.RunIteration(w.LastMission);
        Assert.Equal(0, calls);
        Assert.Equal(timers + 1, w.Stub.Count(0, 10));
        Assert.Equal(MissionPhase.Working, w.M.Get(w.LastMission)!.Phase);
        Assert.False(w.Events<MissionIterationDone>().Single().Success);
    }

    // m5-missions MI43 (B2d): a Diplomacy team flying to its own system is spotted at arrival by an Imperial ship in orbit (the planet has no
    // guard for a planet target the Empire does not hold): the mission ends Foiled with code 4 before its timer (no timer draw), no hook call
    [Fact]
    public void MI43_RepeatingMissionFoiledEnds()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Agent(Leia, H, dip: 100);
        w.Host.Fleets.AddFleet(Side.Empire, "E", new[] { "Imperial Star Destroyer" }, T);
        int calls = 0;
        w.M.OnTeamExposed = (_, _, _, _) => calls++;
        w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
        w.Run(5);
        Assert.Equal(0, calls);
        Assert.Equal(0, w.Stub!.Count(0, 10));
        Assert.Equal(MissionModule.FoilCodeAtTarget, w.Events<MissionFoiled>().Single().EndCode);
        Assert.Equal(MissionOutcome.Foiled, w.Events<MissionEnded>().Single().Outcome);
    }

    // m5-missions MI57 (B2d): MissionGuardChecks off and an enemy ship in orbit: landing makes only the timer draw. On (DEFAULT): the arriving
    // check (phase 7, orbit) draws once for the ship before the timer (phase 8); the team already at T counts no member at the leaving orbit check.
    [Fact]
    public void MI57_GuardChecksAtLanding()
    {
        var w = new MissionWorld(new MissionParams { MissionGuardChecks = false });
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50);
        w.Host.Fleets.AddFleet(Side.Empire, "E", new[] { "Imperial Star Destroyer" }, T);
        int from = w.Stub!.Requests.Count;
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T);
        w.Game.Step();
        Assert.Equal(new[] { (0, 20) }, w.RequestsSince(from));
        Assert.Single(w.Events<MissionLanded>());
        var on = new MissionWorld();
        on.Hold(T, Side.Empire, 0);
        on.Agent(Leia, T, esp: 50);
        on.Host.Fleets.AddFleet(Side.Empire, "E", new[] { "Imperial Star Destroyer" }, T);
        on.Stub!.Expect(0, 99, 99);
        from = on.Stub.Requests.Count;
        on.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T);
        on.Game.Step();
        Assert.Equal(new[] { (0, 99), (0, 20) }, on.RequestsSince(from));
        Assert.Single(on.Events<MissionDetectionRoll>());
        Assert.Single(on.Events<MissionLanded>());
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
