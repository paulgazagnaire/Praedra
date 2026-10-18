using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Map;
using Rebellion.Core.Setup;
using Rebellion.Core.Tests.Economy;
using static Rebellion.Core.Tests.Characters.CharWorld;

namespace Rebellion.Core.Tests.Characters;

/// <summary>The tests of m5-characters-and-force §Tests (CH1 to CH34), plus the switch defaults.</summary>
public class CharacterTests
{
    // ------------------------------------------------------------------ stats (§Rolling the stats)

    // m5-characters-and-force CH1
    [Fact]
    public void CH1_AckbarCreationDrawsInOrder()
    {
        var w = new CharWorld();
        var stub = new StubRng().Queue(12, 49, 60, 0, 10, 9, 33);
        var c = w.Ch.Create(w.Ch.Data.Get(Ackbar), stub);
        Assert.Equal(new[] { 12, 49, 100, 0, 0, 0, 100 }, c.Skills);
        Assert.True(c.ForceUser);
        // B2e moved: the Force is rolled only once he is aware (B2e §5), so a hidden user has Force 0 and the level draw is not made at creation
        Assert.False(c.ForceDiscovered);
        Assert.Equal(0, c.EffectiveForce);
        Assert.Equal(new[] { (0, 30), (0, 50), (0, 60), (0, 50), (0, 10), (0, 99) }, stub.Requests.Select(r => (r.Min, r.Max)));

        var stub2 = new StubRng().Queue(12, 49, 60, 0, 10, 10);
        var c2 = w.Ch.Create(w.Ch.Data.Get(Ackbar), stub2);
        Assert.False(c2.ForceUser);
        Assert.Equal(0, c2.EffectiveForce);
        Assert.Equal(6, stub2.Requests.Count);
    }

    // m5-characters-and-force CH2
    [Fact]
    public void CH2_MajorsCostNoDraws()
    {
        var w = new CharWorld();
        var stub = new StubRng();
        var han = w.Ch.Create(w.Ch.Data.Get(Han), stub);
        var luke = w.Ch.Create(w.Ch.Data.Get(Luke), stub);
        var leia = w.Ch.Create(w.Ch.Data.Get(Leia), stub);
        Assert.Empty(stub.Requests);
        Assert.Equal(0, han.EffectiveForce); Assert.False(han.ForceUser);
        Assert.Equal(50, luke.EffectiveForce); Assert.True(luke.ForceDiscovered);
        Assert.Equal(0, leia.EffectiveForce); Assert.True(leia.ForceUser); Assert.False(leia.ForceDiscovered); // B2e moved: Force 0 until aware
    }

    // ------------------------------------------------------------------ Force (§Force)

    private static (CharWorld W, CharacterRecord T, CharacterRecord S) Training(int trainerId, int trainerForce, int studentId, int studentForce, CharacterParams? p = null)
    {
        var w = new CharWorld(p);
        var t = w.Add(trainerId, Yavin); t.ForceUser = true; t.ForceExperience = trainerForce;
        var s = w.Add(studentId, Yavin); s.ForceUser = true; s.ForceExperience = studentForce; s.ForceTraining = 0;
        return (w, t, s);
    }

    // m5-characters-and-force CH3 (the 1998 roll: Classic values set explicitly, CH37)
    [Fact]
    public void CH3_VaderTrainsAStudentOfTwenty()
    {
        var (w, vader, s) = Training(Vader, 120, Tura, 20, Classic1998Force());
        w.Stub!.Expect(0, 99, 99).Expect(0, 50, 37);
        w.StepWith("6.10", ctx => w.Ch.ForceTrainingRound(ctx, vader, new[] { s }));
        var e = Assert.Single(w.Events<ForceTrainingResult>());
        Assert.Equal((100, 99, true, 37, 57), (e.Gap, e.R, e.Success, e.Gain, e.NewForce));
        Assert.Equal(37, s.ForceTraining);
        Assert.Equal(57, s.EffectiveForce);
    }

    // m5-characters-and-force CH4 (the 1998 roll: Classic values set explicitly, CH37)
    [Fact]
    public void CH4_LukeTrainsLeia()
    {
        var (w, luke, leia) = Training(Luke, 50, Leia, 10, Classic1998Force());
        w.Stub!.Expect(0, 99, 39).Expect(0, 20, 20);
        w.StepWith("6.10", ctx => w.Ch.ForceTrainingRound(ctx, luke, new[] { leia }));
        Assert.Equal(30, leia.EffectiveForce);
        int before = w.Stub.Requests.Count;
        w.Stub.Expect(0, 99, 40);
        w.StepWith("6.10", ctx => w.Ch.ForceTrainingRound(ctx, luke, new[] { leia }));
        var last = w.Events<ForceTrainingResult>().Last();
        Assert.False(last.Success);
        Assert.Equal(30, leia.EffectiveForce);
        Assert.Equal(new[] { (0, 99) }, w.Stub.Requests.Skip(before).Select(r => (r.Min, r.Max)));
    }

    // m5-characters-and-force CH5 (the 1998 roll: Classic values set explicitly; under New a gap of 1 has chance 1 * 50 / 100 = 0)
    [Fact]
    public void CH5_EqualOrHigherStudentsNeverSucceed_GapOneGainsZero()
    {
        var (w, t, s) = Training(Luke, 50, Leia, 50, Classic1998Force());
        w.StepWith("6.10", ctx => w.Ch.ForceTrainingRound(ctx, t, new[] { s }));
        s.ForceExperience = 60;
        w.StepWith("6.10", ctx => w.Ch.ForceTrainingRound(ctx, t, new[] { s }));
        Assert.All(w.Events<ForceTrainingResult>(), e => Assert.False(e.Success));
        Assert.Equal(new[] { (0, 99), (0, 99) }, w.Stub!.Requests.Select(r => (r.Min, r.Max)));
        s.ForceExperience = 49;
        w.Stub.Expect(0, 99, 0);
        w.StepWith("6.10", ctx => w.Ch.ForceTrainingRound(ctx, t, new[] { s }));
        var e3 = w.Events<ForceTrainingResult>().Last();
        Assert.True(e3.Success); Assert.Equal(0, e3.Gain);
        Assert.Equal((0, 0), (w.Stub.Requests[^1].Min, w.Stub.Requests[^1].Max)); // g = rand(0..0) is drawn
    }

    // m5-characters-and-force CH6
    [Fact]
    public void CH6_NoStudentReachesTheTrainer()
    {
        var w = new CharWorld(seed: 6);
        var t = w.Add(Luke, Yavin);
        var s = w.Add(Leia, Yavin);
        s.ForceDiscovered = true; s.ForceExperience = 10; // B2e: her Force is rolled once she is aware (level 10, variance 0)
        Assert.Equal((50, 10), (t.EffectiveForce, s.EffectiveForce));
        w.StepWith("6.10", ctx =>
        {
            for (int i = 0; i < 1000; i++)
            {
                w.Ch.ForceTrainingRound(ctx, t, new[] { s });
                Assert.True(s.EffectiveForce < 50, $"round {i}: {s.EffectiveForce}");
            }
        });
        Assert.True(s.EffectiveForce > 10);
    }

    // m5-characters-and-force CH35: New ForceSkillPercent 40 (DEFAULT): base 100, Force 120 gives 148; Luke (Force 50) diplomacy 50 → 60,
    // espionage 50 → 60, combat 90 → 108, with 40 injury 68; research skills and leadership unchanged
    [Fact]
    public void CH35_NewForceSkillPercentForty()
    {
        var w = new CharWorld();
        Assert.Equal(40, w.Ch.Params.ForceSkillPercent);
        var vader = w.Add(Vader, Coruscant); vader.ForceUser = true; vader.ForceExperience = 120; vader.ForceTraining = 0;
        vader.Skills[(int)Skill.Combat] = 100; vader.Skills[(int)Skill.Espionage] = 100; vader.Skills[(int)Skill.Diplomacy] = 100;
        vader.Skills[(int)Skill.Leadership] = 70; vader.Skills[(int)Skill.ShipDesign] = 30; vader.Injury = 0; vader.LeadershipAdjustment = 0;
        Assert.Equal((148, 148, 148), (w.Ch.EffectiveSkill(vader, Skill.Combat), w.Ch.EffectiveSkill(vader, Skill.Espionage), w.Ch.EffectiveSkill(vader, Skill.Diplomacy)));
        Assert.Equal((70, 30), (w.Ch.EffectiveSkill(vader, Skill.Leadership), w.Ch.EffectiveSkill(vader, Skill.ShipDesign)));
        var luke = w.Add(Luke, Yavin); luke.ForceUser = true; luke.ForceExperience = 50; luke.ForceTraining = 0; luke.Injury = 0;
        luke.Skills[(int)Skill.Diplomacy] = 50; luke.Skills[(int)Skill.Espionage] = 50; luke.Skills[(int)Skill.Combat] = 90;
        Assert.Equal((60, 60, 108), (w.Ch.EffectiveSkill(luke, Skill.Diplomacy), w.Ch.EffectiveSkill(luke, Skill.Espionage), w.Ch.EffectiveSkill(luke, Skill.Combat)));
        luke.Injury = 40;
        Assert.Equal(68, w.Ch.EffectiveSkill(luke, Skill.Combat));
    }

    // m5-characters-and-force CH36: New training (chance percent 50, gain percent 25, DEFAULT): Vader 120, student 20 (gap 100): chance 50,
    // r = 49 succeeds, g = rand(0..25), g = 25 → 45; r = 50 fails with no g draw. Luke 50, Leia 10 (gap 40): chance 20, r = 19 succeeds, max gain 10
    [Fact]
    public void CH36_NewTrainingChanceAndGain()
    {
        var (w, vader, s) = Training(Vader, 120, Tura, 20);
        w.Stub!.Expect(0, 99, 49).Expect(0, 25, 25);
        w.StepWith("6.10", ctx => w.Ch.ForceTrainingRound(ctx, vader, new[] { s }));
        var e = Assert.Single(w.Events<ForceTrainingResult>());
        Assert.Equal((100, 49, true, 25, 45), (e.Gap, e.R, e.Success, e.Gain, e.NewForce));
        s.ForceTraining = 0;
        int before = w.Stub.Requests.Count;
        w.Stub.Expect(0, 99, 50);
        w.StepWith("6.10", ctx => w.Ch.ForceTrainingRound(ctx, vader, new[] { s }));
        Assert.False(w.Events<ForceTrainingResult>().Last().Success);
        Assert.Equal(20, s.EffectiveForce);
        Assert.Equal(new[] { (0, 99) }, w.Stub.Requests.Skip(before).Select(r => (r.Min, r.Max)));

        var (w2, luke, leia) = Training(Luke, 50, Leia, 10);
        w2.Stub!.Expect(0, 99, 19).Expect(0, 10, 10);
        w2.StepWith("6.10", ctx => w2.Ch.ForceTrainingRound(ctx, luke, new[] { leia }));
        Assert.Equal(20, leia.EffectiveForce);
        w2.Stub.Expect(0, 99, 10); // gap 30 now: chance 15, r = 10 succeeds, max gain 30 * 25 / 100 = 7
        w2.Stub.Expect(0, 7, 7);
        w2.StepWith("6.10", ctx => w2.Ch.ForceTrainingRound(ctx, luke, new[] { leia }));
        Assert.Equal(27, leia.EffectiveForce);
        w2.Stub.Expect(0, 99, 11); // gap 23: chance 11, r = 11 fails
        w2.StepWith("6.10", ctx => w2.Ch.ForceTrainingRound(ctx, luke, new[] { leia }));
        Assert.False(w2.Events<ForceTrainingResult>().Last().Success);
    }

    // m5-characters-and-force CH37: the 1998 values (100 / 100 / 50) reproduce CH3 and CH4 (those tests run with them) and B2e-1/B2e-2, and the
    // skill formula with percent 100 equals B + B * F / 100 for every base 0..120 and Force 0..200 (bit-identical Classic)
    [Fact]
    public void CH37_ClassicValuesReproduceThe1998Force()
    {
        var w = new CharWorld(Classic1998Force());
        var luke = w.Add(Luke, Yavin); luke.ForceUser = true; luke.ForceExperience = 50; luke.ForceTraining = 0; luke.Injury = 0;
        luke.Skills[(int)Skill.Diplomacy] = 50; luke.Skills[(int)Skill.Espionage] = 50; luke.Skills[(int)Skill.Combat] = 90;
        Assert.Equal((75, 75, 135), (w.Ch.EffectiveSkill(luke, Skill.Diplomacy), w.Ch.EffectiveSkill(luke, Skill.Espionage), w.Ch.EffectiveSkill(luke, Skill.Combat)));
        var c = new CharacterRecord { Skills = new List<int>(new int[7]) };
        for (int b = 0; b <= 120; b++)
            for (int f = 0; f <= 200; f++)
            {
                c.Skills[(int)Skill.Espionage] = b; c.ForceExperience = f;
                Assert.Equal(b + b * f / 100, c.EffectiveSkill(Skill.Espionage, CharacterParams.ClassicForceSkillPercent));
            }
        CH3_VaderTrainsAStudentOfTwenty();
        CH4_LukeTrainsLeia();
    }

    // m5-characters-and-force CH7
    [Theory]
    [InlineData(0, ForceRank.Trainee), InlineData(1, ForceRank.Student), InlineData(79, ForceRank.Student), InlineData(80, ForceRank.Knight)]
    [InlineData(99, ForceRank.Knight), InlineData(100, ForceRank.Master), InlineData(150, ForceRank.Master)]
    public void CH7_Rank(int force, ForceRank rank)
    {
        var ch = new CharWorld().Ch;
        Assert.Equal(rank, ch.RankFor(true, force));
        Assert.Null(ch.RankFor(false, force));
    }

    // ------------------------------------------------------------------ posts and the leadership hooks (§Posts)

    // m5-characters-and-force CH8
    [Fact]
    public void CH8_AdmiralMultipliesBombardment_UntilInjured()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Alliance, Yavin, MonCal);
        var ack = w.Add(Ackbar, Yavin); ack.Skills[(int)Skill.Leadership] = 100;
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Alliance, Ackbar, f, CharacterPost.Admiral)));
        var fleet = w.Game.State.Fleets[f];
        var bomb = w.Host.Bombardment;
        int b = bomb.BaseValue(fleet);
        Assert.True(b > 0);
        Assert.Equal(100, bomb.AdmiralLeadership(fleet));
        Assert.Equal(3, bomb.AdmiralMultiplier(100));
        Assert.Equal(b * 3, bomb.Power(fleet));

        // B2e moved: Injure is the Hurt roll (combat 0: p = 100, the stub's 0 hits) and then the minor's death draw, answered 99 so he lives;
        // an injured character loses his post but keeps riding the fleet (B2e §2, the Injury notifier 0x4f1720 touches only the post)
        w.Stub!.Expect(0, 99, 0, 99);
        w.StepWith("5.1", ctx => w.Ch.Injure(ctx, ack, CharacterCause.Debug));
        Assert.Equal(CharacterStatus.Injured, ack.Status);
        Assert.Equal(0, bomb.AdmiralLeadership(fleet));
        Assert.Equal(b, bomb.Power(fleet));
        var pv = Assert.Single(w.Events<PostVacated>());
        Assert.Equal((Ackbar, PostKind.Admiral), (pv.Character, pv.Post));
        Assert.Equal((f, CharacterPost.None), (ack.AttachedFleet, ack.Post));
        Assert.Equal(Yavin, w.Ch.LocationOf(ack));
    }

    // m5-characters-and-force CH9
    [Fact]
    public void CH9_HanAsFleetGeneralIsTheAssaultPlusOneRow()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Alliance, Hoth);
        w.Add(Han, Hoth);
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Alliance, Han, f, CharacterPost.General)));
        var a = w.Host.Assault;
        int lAtt = a.FleetGeneralLeadership(w.Game.State.Fleets[f]), lDef = a.SystemGeneralLeadership(Hoth);
        Assert.Equal((90, 0), (lAtt, lDef));
        Assert.Equal(3, a.LeadershipSteps(lAtt));
        for (int roll = 0; roll <= 10; roll++) // Army attack 3 vs Army defense 5 plus Han: net row +1 (A4)
            Assert.Equal(roll + 1, a.DuelValue(roll, 3, 5, lAtt, lDef));
    }

    // m5-characters-and-force CH10
    [Fact]
    public void CH10_SystemGeneralLeadsTheDefenders()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire);
        var p = w.Add(Piett, Hoth); p.Skills[(int)Skill.Leadership] = 85;
        Assert.Null(w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Empire, Piett, Hoth)));
        Assert.Single(w.Events<SystemGeneralAssigned>());
        var a = w.Host.Assault;
        Assert.Equal(85, a.SystemGeneralLeadership(Hoth));
        Assert.Equal(2, a.LeadershipSteps(85));
        foreach (var (l, steps) in new[] { (0, 0), (29, 0), (30, 1), (59, 1), (60, 2), (89, 2), (90, 3), (119, 3), (120, 4) })
            Assert.Equal(steps, a.LeadershipSteps(l));
    }

    // m5-characters-and-force CH11
    [Fact]
    public void CH11_AttachRejections_ChewbaccaAsPassenger()
    {
        // The Empire ISD at Yavin starts a battle on the Step. Ruleset = Classic set explicitly (battle DEFAULT New since 2026-10-18): in Classic
        // both corvettes retreat together; in New the ISD's tractor beam holds fleet f's corvette and destroys it (m4-retreat-damage-repair
        // TractorBeamsHoldRetreat), which dissolves the fleet and detaches Chewbacca. This test checks the attach rules, not the battle.
        var w = new CharWorld(settings: new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true,
            Economy = new Rebellion.Core.Economy.EconomyParams { ScrapPeriod = 1_000_000 },
            Battle = new Rebellion.Core.Battle.BattleParams { Ruleset = Rebellion.Core.Battle.BattleRuleset.Classic } });
        int f = w.Fleet(Side.Alliance, Yavin);
        int moving = w.Fleet(Side.Alliance, Yavin);
        int imp = w.Fleet(Side.Empire, Yavin, Isd);
        w.Add(Ackbar, Yavin); w.Add(Madine, Yavin); w.Add(Chewbacca, Yavin); w.Add(Afyon, Yavin); w.Add(Dodonna, Yavin);
        w.Add(Wedge, Hoth);
        w.Ch.IsOnMission = id => id == Dodonna;
        long Attach(Side s, int c, int fleet, CharacterPost p) => w.Submit(seq => new AttachCharacterOrder(seq, s, c, fleet, p));

        long a1 = Attach(Side.Empire, Ackbar, f, CharacterPost.Admiral);
        long a2 = Attach(Side.Alliance, Ackbar, imp, CharacterPost.Admiral);
        long a3 = Attach(Side.Alliance, Wedge, f, CharacterPost.None);
        long mv = w.Submit(seq => new MoveFleetOrder(seq, Side.Alliance, moving, Hoth));
        long a4 = Attach(Side.Alliance, Ackbar, moving, CharacterPost.Admiral);
        long a5 = Attach(Side.Alliance, Madine, f, CharacterPost.Admiral);
        long a6 = Attach(Side.Alliance, Dodonna, f, CharacterPost.None);
        long ok = Attach(Side.Alliance, Ackbar, f, CharacterPost.Admiral);
        long a7 = Attach(Side.Alliance, Afyon, f, CharacterPost.Admiral);
        long a8 = Attach(Side.Alliance, Ackbar, f, CharacterPost.None);
        long chewie = Attach(Side.Alliance, Chewbacca, f, CharacterPost.None);
        w.Game.Step();
        Assert.Equal(RejectReason.NotYours, w.Reason(a1));
        Assert.Equal(RejectReason.NotYours, w.Reason(a2));
        Assert.Equal(CharacterReject.NotAtSystem, w.Reason(a3));
        Assert.Null(w.Reason(mv));
        Assert.Equal(CharacterReject.FleetInTransit, w.Reason(a4));
        Assert.Equal(CharacterReject.NoCapability, w.Reason(a5));
        Assert.Equal(CharacterReject.OnMission, w.Reason(a6));
        Assert.Null(w.Reason(ok));
        Assert.Equal(CharacterReject.PostTaken, w.Reason(a7));
        Assert.Equal(CharacterReject.AlreadyAttached, w.Reason(a8));
        Assert.Null(w.Reason(chewie));
        Assert.Equal((f, CharacterPost.None), (w.C(Chewbacca).AttachedFleet, w.C(Chewbacca).Post));
        Assert.Equal((f, CharacterPost.Admiral), (w.C(Ackbar).AttachedFleet, w.C(Ackbar).Post));
        foreach (var id in new[] { Madine, Afyon, Dodonna, Wedge }) Assert.Equal(0, w.C(id).AttachedFleet);
    }

    // m5-characters-and-force CH12
    [Fact]
    public void CH12_CapturedGeneralInFlight_SpeedFixedAtOrderTime()
    {
        var w = new CharWorld(new CharacterParams { NavigatorBonus = true });
        int f = w.Fleet(Side.Alliance, Yavin);
        w.Add(Han, Yavin);
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Alliance, Han, f, CharacterPost.General)));
        var fleet = w.Game.State.Fleets[f];
        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Alliance, f, Coruscant)));
        var accepted = w.Events<OrderAccepted>().Last();
        var ticks = fleet.Transit!.HopTicks.ToList();
        Assert.Equal(25, w.Ch.NavigatorAdjust(fleet, 50)); // Han rides: the trip was planned at 80 * 50 / 100 = 40
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, w.C(Han), Side.Empire, null, CharacterCause.Battle));
        var han = w.C(Han);
        Assert.Equal(CharacterStatus.Captured, han.Status);
        Assert.Equal(0, han.AttachedFleet);
        Assert.Equal(fleet.Transit!.Path[fleet.Transit.HopIndex], han.HeldAt); // the fleet is on a lane: the system it last left
        Assert.NotNull(han.HeldAt);
        Assert.Equal(0, w.Host.Assault.FleetGeneralLeadership(fleet));
        Assert.Equal(ticks, fleet.Transit!.HopTicks);
        Assert.Equal(accepted.ArrivalDay, accepted.Day + fleet.Transit.HopTicks.Sum());
    }

    // ------------------------------------------------------------------ movement (§Moving a character alone, §Riding)

    private static GameData LineGalaxy(string kind)
    {
        var planets = "name,sector,group,map_x,map_y\nA,S,core,0,0\nB,S,core,500,0\nC,S,core,1000,0\n";
        string routes = kind == "route" ? "Test Run" : "";
        var lanes = $"from,to,kind,routes\nA,B,{kind},{routes}\nB,C,{kind},{routes}\n";
        var g = GalaxyLoader.Load(planets, lanes, TestData.Tiers);
        var slots = "name,sector,group,energy,raw,populated\n" + string.Join("\n", g.Systems.Select(s => $"{s.Name},{s.Sector},{s.Group},5,3,1"));
        return GameData.Default with { Galaxy = g, Slots = SlotValues.Parse(slots, g) };
    }

    // m5-characters-and-force CH13
    [Theory]
    [InlineData("generated", 100)]
    [InlineData("route", 33)]
    public void CH13_LoneCharacterTravelsAtSpeedHundred(string kind, int days)
    {
        var w = new CharWorld(data: LineGalaxy(kind));
        w.Add(Wedge, "A");
        int k = w.Game.Day;
        Assert.Null(w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Wedge, "B")));
        Assert.Null(w.Ch.LocationOf(w.C(Wedge)));
        while (w.Events<CharacterArrived>().Count == 0) w.Game.Step();
        var e = Assert.Single(w.Events<CharacterArrived>());
        Assert.Equal(k + days, e.Day); // placed at step 3 of the Step K + days - 1
        Assert.Equal(k + days, w.Game.Day);
        Assert.Equal("B", w.Ch.LocationOf(w.C(Wedge)));
    }

    // m5-characters-and-force CH13 (not stopped on the way)
    [Fact]
    public void CH13_NotStoppedByAnEnemyFleet()
    {
        var w = new CharWorld(data: LineGalaxy("generated"));
        w.Fleet(Side.Empire, "B", Isd, 3);
        w.Add(Wedge, "A");
        Assert.Null(w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Wedge, "C")));
        w.Run(250);
        var e = Assert.Single(w.Events<CharacterArrived>());
        Assert.Equal(("C", 200), (e.System, e.Day));
        Assert.Equal(CharacterStatus.Free, w.C(Wedge).Status);
    }

    // m5-characters-and-force CH14
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CH14_RidersFollowTheirFleet(bool ride)
    {
        var w = new CharWorld(new CharacterParams { OfficersRideFleet = ride });
        int f = w.Fleet(Side.Alliance, Yavin);
        w.Add(Ackbar, Yavin);
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Alliance, Ackbar, f, CharacterPost.Admiral)));
        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Alliance, f, Hoth)));
        var ack = w.C(Ackbar);
        if (ride)
        {
            Assert.Null(w.Ch.LocationOf(ack));
            while (w.Game.State.Fleets[f].At == null) w.Game.Step();
            Assert.Equal(Hoth, w.Ch.LocationOf(ack));
            Assert.Equal(f, ack.AttachedFleet);
        }
        else
        {
            Assert.Equal(0, ack.AttachedFleet);
            Assert.Equal(Yavin, w.Ch.LocationOf(ack));
            Assert.Equal(CharacterCause.FleetMoved, Assert.Single(w.Events<CharacterDetached>()).Cause);
        }
    }

    // ------------------------------------------------------------------ Navigator (§Navigator)

    private static (CharWorld W, Rebellion.Core.Fleets.Fleet F) Nav(bool on, params int[] riders)
    {
        var w = new CharWorld(new CharacterParams { NavigatorBonus = on });
        int f = w.Fleet(Side.Alliance, Yavin);
        foreach (var id in riders) { w.Add(id, Yavin); w.Ch.PlaceInFleet(id, f); }
        return (w, w.Game.State.Fleets[f]);
    }

    // m5-characters-and-force CH15
    [Fact]
    public void CH15_HanHalvesTheTrip()
    {
        var (w, f) = Nav(true, Han);
        Assert.Equal(80, w.Host.Fleets.SpeedOf(f));
        Assert.Equal(40, w.Ch.NavigatorAdjust(f, 80));
        Assert.Equal(40, w.Host.Fleets.SpeedAdjust!(f, 80)); // the fleet module reads the character hook at MoveFleet acceptance
    }

    // m5-characters-and-force CH16
    [Fact]
    public void CH16_BestSingleNavigator()
    {
        Assert.Equal(40, Nav(true, Han, Lando) is var (w1, f1) ? w1.Ch.NavigatorAdjust(f1, 80) : 0);
        Assert.Equal(68, Nav(true, Chewbacca) is var (w2, f2) ? w2.Ch.NavigatorAdjust(f2, 80) : 0);
        Assert.Equal(80, Nav(true) is var (w3, f3) ? w3.Ch.NavigatorAdjust(f3, 80) : 0);
    }

    // m5-characters-and-force CH17
    [Fact]
    public void CH17_FloorOnlyWithANavigator()
    {
        var (w, f) = Nav(true, Han);
        Assert.Equal(25, w.Ch.NavigatorAdjust(f, 40));
        var (w2, f2) = Nav(true);
        Assert.Equal(20, w2.Ch.NavigatorAdjust(f2, 20));
    }

    // m5-characters-and-force CH18
    [Fact]
    public void CH18_NoBonusInClassicOrFromANonFreeNavigator()
    {
        var (w, f) = Nav(false, Han);
        Assert.Equal(80, w.Ch.NavigatorAdjust(f, 80));
        var (w2, f2) = Nav(true, Han);
        w2.StepWith("5.2", ctx => w2.Ch.Capture(ctx, w2.C(Han), Side.Empire, null, CharacterCause.Debug));
        Assert.Equal(80, w2.Ch.NavigatorAdjust(f2, 80));
        var (w3, f3) = Nav(true, Han);
        w3.C(Han).Status = CharacterStatus.Injured; // injured but still riding (state set directly: Injure would detach him)
        Assert.Equal(80, w3.Ch.NavigatorAdjust(f3, 80));
    }

    // ------------------------------------------------------------------ the Death Star (§The Death Star's roll on characters)

    // m5-characters-and-force CH19
    [Fact]
    public void CH19_DeathStarRollOnMinors()
    {
        var w = new CharWorld();
        w.Add(Drayson, Hoth); w.Add(Borsk, Hoth); var c = w.Add(Tura, Yavin); w.Add(Vader, Hoth);
        w.StepWith("5.1", ctx => w.Ch.Capture(ctx, c, Side.Empire, Hoth, CharacterCause.Debug));
        int before = w.Stub!.Requests.Count, ev = w.Game.Events.Count;
        // The die is rand(1..100), hit at 50 or less (m5-death-star §2 effect 3); the same chance as the characters sheet's rand(0..99) < 50, shifted by one: 50 hits, 51 misses, 11 hits.
        w.Stub.Expect(1, 100, 50, 51, 11);
        w.StepWith("5.1", ctx => w.Ch.DeathStarStrike(ctx, Hoth, Side.Empire));
        // B2e moved: the hit on a free minor is the Hurt roll (unqueued draws answer 0: hit, sizes 0 and 0, then the minor's death draw 0 < 25),
        // so Drayson dies of the injury and the kill is not needed; the prisoner Tura is killed without it. 3 + 4 draws.
        Assert.Equal(7, w.Stub.Requests.Count - before);
        Assert.Equal(CharacterStatus.Dead, w.C(Drayson).Status);
        Assert.Equal(CharacterStatus.Free, w.C(Borsk).Status);
        Assert.Equal(CharacterStatus.Dead, w.C(Tura).Status);
        Assert.Equal(CharacterStatus.Free, w.C(Vader).Status);
        var order = w.Game.Events.Skip(ev).Where(e => e is CharacterInjured or CharacterKilled)
            .Select(e => (e.Kind, e is CharacterInjured i ? i.Character : ((CharacterKilled)e).Character)).ToList();
        Assert.Equal(new[] { ("CharacterInjured", Drayson), ("CharacterKilled", Drayson), ("CharacterKilled", Tura) }, order);
    }

    // ------------------------------------------------------------------ the state machine (§Capture, injury, death, freeing)

    // m5-characters-and-force CH20
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CH20_MajorsAreInjuredInsteadOfKilled(bool majorsCanDie)
    {
        var w = new CharWorld(new CharacterParams { MajorsCanDie = majorsCanDie });
        var luke = w.Add(Luke, Yavin);
        w.StepWith("5.2", ctx => w.Ch.Kill(ctx, luke, CharacterCause.Mission));
        if (!majorsCanDie)
        {
            Assert.Single(w.Events<CharacterInjured>());
            Assert.Empty(w.Events<CharacterKilled>());
            // B2e moved: the Kill becomes the Hurt roll (p = 1 for Luke's effective combat 135; the stub's 0 hits, size 0 + 0 + 1); Yavin is
            // nobody's, so he does not heal
            Assert.Equal((CharacterStatus.Injured, 1), (luke.Status, luke.Injury));
            w.StepWith("5.2", ctx => w.Ch.Capture(ctx, luke, Side.Empire, null, CharacterCause.Mission));
            Assert.True(w.Ch.IsCaptured(Luke)); // the victory term stays reachable
        }
        else
        {
            Assert.Equal(CharacterStatus.Dead, luke.Status);
            Assert.True(w.Ch.IsDead(Luke));
        }
    }

    // m5-characters-and-force CH21
    [Fact]
    public void CH21_CaptureLuke()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Alliance, Yavin);
        var luke = w.Add(Luke, Yavin);
        w.Ch.SetPost(Luke, f, CharacterPost.General);
        bool first = false, second = true, own = true;
        w.StepWith("5.2", ctx =>
        {
            first = w.Ch.Capture(ctx, luke, Side.Empire, null, CharacterCause.Mission);
            second = w.Ch.Capture(ctx, luke, Side.Empire, null, CharacterCause.Mission);
        });
        Assert.True(first); Assert.False(second);
        Assert.Equal((CharacterStatus.Captured, (Side?)Side.Empire, Yavin), (luke.Status, luke.Captor, luke.HeldAt));
        Assert.Equal((0, CharacterPost.None), (luke.AttachedFleet, luke.Post));
        Assert.Equal(PostKind.General, Assert.Single(w.Events<PostVacated>()).Post);
        Assert.Single(w.Events<CharacterCaptured>());
        var leia = w.Add(Leia, Yavin);
        w.StepWith("5.2", ctx => own = w.Ch.Capture(ctx, leia, Side.Alliance, null, CharacterCause.Mission));
        Assert.False(own);
        Assert.Equal(CharacterStatus.Free, leia.Status);
    }

    // m5-characters-and-force CH22 (B2e moved: Injure is the Hurt roll on a Free or Injured character; never a captured or dead one)
    [Fact]
    public void CH22_InjureNotACapturedOrDeadCharacter()
    {
        var w = new CharWorld();
        var luke = w.Add(Luke, Yavin); var han = w.Add(Han, Yavin);
        w.StepWith("5.2", ctx => { w.Ch.Capture(ctx, luke, Side.Empire, null, CharacterCause.Debug); w.Ch.Injure(ctx, han, CharacterCause.Debug); });
        Assert.Equal(CharacterStatus.Injured, han.Status);
        int ev = w.Game.Events.Count, pts = han.Injury;
        bool a = true, b = false;
        w.StepWith("5.2", ctx => { a = w.Ch.Injure(ctx, luke, CharacterCause.Debug); b = w.Ch.Injure(ctx, han, CharacterCause.Debug); });
        Assert.False(a); Assert.True(b);
        Assert.True(han.Injury > pts);
        Assert.Equal(han.Id, Assert.Single(w.Game.Events.Skip(ev).OfType<CharacterInjured>()).Character);
    }

    // m5-characters-and-force CH23 (B2e moved: an injury lasts until its points heal, 1 a day on a planet his side holds)
    [Fact]
    public void CH23_InjuryHealsAtSlot610()
    {
        var w = new CharWorld();
        w.Hold(Yavin, Side.Alliance);
        var han = w.Add(Han, Yavin);
        w.StepWith("5.2", ctx => w.Ch.AddInjury(ctx, han, 3, CharacterCause.Debug));
        Assert.Equal(CharacterStatus.Injured, han.Status);
        int step = 1;
        while (han.Status != CharacterStatus.Free) { w.Game.Step(); step++; }
        Assert.Equal(4, step); // healing starts the day after it turned on: 3, 2, 1, 0
        Assert.Single(w.Events<CharacterHealed>());
    }

    // m5-characters-and-force CH24 (B2e moved: CaptiveEscape is on by default; off makes no timer and no draw)
    [Fact]
    public void CH24_NoEscapeDrawsWhenOff_OneDelayDrawWhenOn()
    {
        var w = new CharWorld(new CharacterParams { CaptiveEscape = false });
        var luke = w.Add(Luke, Yavin);
        int before = w.Stub!.Requests.Count;
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, luke, Side.Empire, null, CharacterCause.Debug));
        w.Run(200);
        Assert.Equal(before, w.Stub.Requests.Count);
        Assert.Equal(0, luke.NextEscapeDay);
        var on = new CharWorld();
        var l2 = on.Add(Luke, Yavin);
        on.Stub!.Expect(0, 1000, 400);
        on.StepWith("5.2", ctx => on.Ch.Capture(ctx, l2, Side.Empire, null, CharacterCause.Debug));
        Assert.Equal(0 + 100 + 400, l2.NextEscapeDay);
    }

    // m5-characters-and-force CH25 (B2e moved: the input is Traced; a test table (-50, 1), (50, 30); Luke alone: x = 135 + 75 = 210)
    [Theory]
    [InlineData(29, true), InlineData(30, false)]
    public void CH25_EscapeWithATestTable(int r, bool freed)
    {
        var w = new CharWorld(new CharacterParams { EscapeTable = new[] { (-50, 1), (50, 30) } });
        Assert.Equal((1, 1, 1, 30, 30), (w.Ch.EscapePercent(-80), w.Ch.EscapePercent(-50), w.Ch.EscapePercent(49), w.Ch.EscapePercent(50), w.Ch.EscapePercent(210)));
        var luke = w.Add(Luke, Yavin);
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, luke, Side.Empire, null, CharacterCause.Debug));
        luke.NextEscapeDay = w.Game.Day; // the attempt is due at this Step's slot 6.10
        w.Stub!.Expect(0, 99, r);
        int ev = w.Game.Events.Count;
        w.Game.Step();
        Assert.Equal(freed, luke.Status == CharacterStatus.Free);
        Assert.Equal(freed ? 1 : 0, w.Game.Events.Skip(ev).OfType<CharacterEscaped>().Count());
    }

    // ------------------------------------------------------------------ other sheets' effects

    // m5-characters-and-force CH26
    [Fact]
    public void CH26_UprisingInjuresOneFreeCharacterOfTheHolder()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Alliance);
        int f = w.Fleet(Side.Alliance, Hoth);
        w.Add(Wedge, Hoth); w.Add(Chewbacca, Hoth); w.Add(Drayson, Hoth); w.Ch.PlaceInFleet(Drayson, f); // attached ones count
        w.Add(Luke, Yavin); // elsewhere
        w.Add(Piett, Hoth); // other side
        var leia = w.Add(Leia, Yavin);
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, leia, Side.Empire, Hoth, CharacterCause.Debug)); // captured: not a candidate
        Assert.NotNull(w.Host.Support.CharacterIncident);
        int before = w.Stub!.Requests.Count;
        w.Stub.Expect(0, 2, 1);
        w.Stub.Expect(0, 99, 0, 99); // B2e moved: Injure is the Hurt roll (0 hits), then the minor's death draw (99: he lives)
        w.StepWith("6.3", ctx => w.Host.Support.CharacterIncident!(ctx, ctx.Day + 1, Hoth, Side.Alliance, 3));
        Assert.Equal(new[] { (0, 2), (0, 99) }, w.Stub.Requests.Skip(before).Take(2).Select(x => (x.Min, x.Max)));
        Assert.Equal(CharacterStatus.Injured, w.C(Chewbacca).Status);
        Assert.Equal(Chewbacca, Assert.Single(w.Events<CharacterInjured>()).Character);

        var w2 = new CharWorld();
        w2.StepWith("6.3", ctx => w2.Ch.UprisingIncident(ctx, ctx.Day + 1, Hoth, Side.Alliance, 3));
        Assert.Empty(w2.Stub!.Requests);
    }

    // m5-characters-and-force CH27
    [Fact]
    public void CH27_UprisingFreesPrisoners()
    {
        CharWorld Make()
        {
            var w = new CharWorld();
            var a = w.Add(Wedge, Hoth); var b = w.Add(Chewbacca, Hoth);
            w.StepWith("5.2", ctx => { w.Ch.Capture(ctx, a, Side.Empire, null, CharacterCause.Debug); w.Ch.Capture(ctx, b, Side.Empire, null, CharacterCause.Debug); });
            return w;
        }
        var w4 = Make();
        w4.Stub!.Expect(0, 1, 0);
        w4.StepWith("6.3", ctx => w4.Ch.UprisingIncident(ctx, ctx.Day + 1, Hoth, Side.Empire, 4));
        Assert.Equal(CharacterStatus.Free, w4.C(Wedge).Status);
        Assert.Equal(CharacterStatus.Captured, w4.C(Chewbacca).Status);

        var w5 = Make();
        int before = w5.Stub!.Requests.Count;
        w5.StepWith("6.3", ctx => w5.Ch.UprisingIncident(ctx, ctx.Day + 1, Hoth, Side.Empire, 5));
        Assert.Equal(before, w5.Stub.Requests.Count);
        Assert.All(new[] { Wedge, Chewbacca }, id => Assert.Equal(CharacterStatus.Free, w5.C(id).Status));
        Assert.Equal(2, w5.Events<CharacterFreed>().Count);
    }

    // m5-characters-and-force CH28
    [Fact]
    public void CH28_HolderChangeClearsTheOldGeneral()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire);
        w.Hold(Coruscant, Side.Empire); // (B5a, 2026-10-17) somewhere for the old side's officer to go
        w.Add(Piett, Hoth);
        Assert.Null(w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Empire, Piett, Hoth)));
        // (B5a, 2026-10-17) under ControlRule = Derived the holder follows the regiments: an Alliance regiment lands on the empty world (B5a section 1)
        w.StepWith("5.2", ctx => w.Host.Control.DebugApplyTroops(ctx, Hoth, Side.Alliance, "Alliance Army Regiment", 1));
        Assert.Equal(Side.Alliance, w.Host.Control.HolderOf(Hoth));
        var pv = Assert.Single(w.Events<PostVacated>());
        Assert.Equal((Piett, PostKind.SystemGeneral, CharacterCause.HolderChanged), (pv.Character, pv.Post, pv.Cause));
        Assert.Null(w.Ch.SystemGeneralOf(Hoth));
        // (B5a, 2026-10-17) the old side's character is not captured or hurt; he leaves (B5a section 4, 0x4ef260 Autorouting; DEFAULT destination: nearest held system)
        Assert.Equal(CharacterStatus.Free, w.C(Piett).Status);
        Assert.Equal(Hoth, Assert.Single(w.Events<CharacterDeparted>(), e => e.Character == Piett).From);
        Assert.Equal(0, w.Host.Assault.SystemGeneralLeadership(Hoth));
        // the old holder's officer may not be General there now ((B5a, 2026-10-17) he has left, so the first refusal is NotAtSystem)
        Assert.Equal(CharacterReject.NotAtSystem, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Empire, Piett, Hoth)));
    }

    // m5-characters-and-force CH29
    [Fact]
    public void CH29_MergeKeepsTheReceivingAdmiral()
    {
        var w = new CharWorld();
        int into = w.Fleet(Side.Alliance, Yavin), from = w.Fleet(Side.Alliance, Yavin);
        w.Add(Ackbar, Yavin); w.Add(Afyon, Yavin);
        w.Ch.SetPost(Ackbar, into, CharacterPost.Admiral);
        w.Ch.SetPost(Afyon, from, CharacterPost.Admiral);
        Assert.Null(w.Try(seq => new MergeFleetsOrder(seq, Side.Alliance, from, into)));
        Assert.Equal((into, CharacterPost.Admiral), (w.C(Ackbar).AttachedFleet, w.C(Ackbar).Post));
        Assert.Equal((into, CharacterPost.None), (w.C(Afyon).AttachedFleet, w.C(Afyon).Post));
        Assert.Equal(CharacterCause.Merged, Assert.Single(w.Events<PostVacated>()).Cause);
    }

    // m5-characters-and-force CH30
    [Fact]
    public void CH30_DestroyedFleetLeavesItsRidersFree()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Alliance, Hoth);
        w.Add(Ackbar, Hoth); w.Ch.SetPost(Ackbar, f, CharacterPost.Admiral);
        w.Add(Chewbacca, Hoth); w.Ch.PlaceInFleet(Chewbacca, f);
        int ship = w.Game.State.Fleets[f].Ships[0];
        w.StepWith("5", ctx => w.Host.Economy.DestroyUnit(ctx, ctx.Day + 1, w.Host.Economy.Find(ship)!));
        Assert.False(w.Game.State.Fleets.ContainsKey(f));
        foreach (var id in new[] { Ackbar, Chewbacca })
        {
            var c = w.C(id);
            Assert.Equal((0, CharacterStatus.Free, Hoth), (c.AttachedFleet, c.Status, w.Ch.LocationOf(c)));
        }
        Assert.All(w.Events<CharacterDetached>(), e => Assert.Equal(CharacterCause.FleetDissolved, e.Cause));
    }

    // m5-characters-and-force CH31
    [Fact]
    public void CH31_MissingRulesAreRefused()
    {
        foreach (var (p, rule) in new (CharacterParams, string)[]
                 {
                     (new() { ForceDiscovery = ForceDiscoveryMode.Detection }, "ForceDiscovery=Detection"), (new() { ForceGrowth = true }, "ForceGrowth"),
                     (new() { SkillGrowth = true }, "SkillGrowth"),
                     (new() { BlockadeEscapeRisk = true }, "BlockadeEscapeRisk"), (new() { AssaultHarmsCharacters = true }, "AssaultHarmsCharacters"),
                     (new() { CharactersLostWithFleet = true }, "CharactersLostWithFleet"), (new() { CharactersStoppedInTransit = true }, "CharactersStoppedInTransit"),
                 })
            Assert.Equal(rule, Assert.Throws<RuleMissingException>(() => GameHost.New(1, new GameSettings { Characters = p })).Rule);
    }

    // m5-characters-and-force CH31 (play leaves loyalty untouched: ShiftLoyalty has no caller yet, so nobody betrays). B2b: betrayal exists now
    // (the rolls run at chance 0), so the old "no betrayal type" check is gone; B2b QA Q16.
    [Fact]
    public void CH31_LoyaltyNeverMoves()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 31 });
        for (int i = 0; i < 3000 && h.Victory.Winner == null; i++) h.Game.Step();
        Assert.All(h.Characters.All, c => Assert.Equal(100, c.Loyalty));
        Assert.Empty(h.Game.Events.OfType<Rebellion.Core.Missions.MissionBetrayed>());
        Assert.Empty(h.Game.Events.OfType<LoyaltyChanged>());
    }

    // m5-characters-and-force CH32
    [Fact]
    public void CH32_AddedHeroLimits()
    {
        var data = GameData.Default.Characters;
        var v = data.AddedHeroLimitViolations();
        Assert.Contains(v, x => x.Name == "Hera Syndulla" && x.Rule == 6);
        foreach (var name in new[] { "Mara Jade", "Kyle Katarn", "Boba Fett", "Bossk", "Dengar", "Ysanne Isard", "Quarrie" })
            Assert.Contains(v, x => x.Name == name && x.Rule == 2);
        Assert.All(v, x => Assert.True(data.Get(x.Character).Added)); // the original 60 pass (exempt)
        Assert.DoesNotContain(v, x => x.Rule is 1 or 3 or 4 or 7);
    }

    // m5-characters-and-force CH33
    [Fact]
    public void CH33_SaveLoadReplayIsIdentical()
    {
        GameHost Run(bool reload)
        {
            var h = NewGame.Create(new NewGameOptions { Seed = 33 });
            for (int day = 0; day < 60; day++)
            {
                if (day == 1) Orders(h);
                if (reload && day == 20) h = GameHost.Load(h.Save());
                h.Game.Step();
            }
            return h;
        }
        var a = Run(false); var b = Run(true);
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.Equal(a.Game.Rng.Draws, b.Game.Rng.Draws);
        Assert.Equal(GameSave.Save(a.Game), GameSave.Save(b.Game));
        var luke = a.Characters.Get(Luke)!;
        Assert.Equal(luke.Status, b.Characters.Get(Luke)!.Status);
        Assert.NotNull(a.Characters.Get(Han)!.System);
    }

    private static void Orders(GameHost h)
    {
        var ch = h.Characters;
        var han = ch.Get(Han)!;
        var fleet = h.Game.State.Fleets.Values.FirstOrDefault(f => f.Side == Side.Alliance && f.At == ch.LocationOf(han));
        if (fleet != null) h.Game.Submit(seq => new AttachCharacterOrder(seq, Side.Alliance, Han, fleet.Id, CharacterPost.General));
        h.Game.Submit(seq => new MoveCharacterOrder(seq, Side.Alliance, Wedge, "Hoth"));
        h.Game.Submit(seq => new MoveCharacterOrder(seq, Side.Empire, Vader, "Coruscant"));
    }

    // m5-characters-and-force CH34
    [Fact]
    public void CH34_NoDetachInTransit()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Alliance, Yavin);
        w.Add(Ackbar, Yavin);
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Alliance, Ackbar, f, CharacterPost.Admiral)));
        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Alliance, f, Coruscant)));
        Assert.Equal(CharacterReject.FleetInTransit, w.Try(seq => new DetachCharacterOrder(seq, Side.Alliance, Ackbar)));
        Assert.Equal(f, w.C(Ackbar).AttachedFleet);
    }

    // ------------------------------------------------------------------ other orders, defaults

    // m5-characters-and-force §Posts (Orders): DetachCharacter, ReleaseSystemGeneral, AssignSystemGeneral and MoveCharacter rejections
    [Fact]
    public void Orders_OtherRejections()
    {
        var w = new CharWorld();
        w.Hold(Yavin, Side.Alliance);
        int f = w.Fleet(Side.Alliance, Yavin);
        w.Add(Madine, Yavin); w.Add(Wedge, Yavin); w.Add(Ackbar, Yavin); w.Add(Chewbacca, Yavin);
        Assert.Equal(CharacterReject.NotAttached, w.Try(seq => new DetachCharacterOrder(seq, Side.Alliance, Madine)));
        Assert.Equal(CharacterReject.NoSystemGeneral, w.Try(seq => new ReleaseSystemGeneralOrder(seq, Side.Alliance, Yavin)));
        Assert.Equal(CharacterReject.NoCapability, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Alliance, Chewbacca, Yavin)));
        Assert.Equal(CharacterReject.NotAtSystem, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Alliance, Madine, Hoth)));
        Assert.Null(w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Alliance, Madine, Yavin)));
        Assert.Equal(CharacterReject.PostTaken, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Alliance, Wedge, Yavin)));
        Assert.Equal(CharacterReject.HoldsSystemPost, w.Try(seq => new AttachCharacterOrder(seq, Side.Alliance, Madine, f, CharacterPost.General)));
        Assert.Equal(RejectReason.NotYours, w.Try(seq => new ReleaseSystemGeneralOrder(seq, Side.Empire, Yavin)));
        Assert.Null(w.Try(seq => new ReleaseSystemGeneralOrder(seq, Side.Alliance, Yavin)));
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Alliance, Ackbar, f, CharacterPost.Admiral)));
        Assert.Equal(CharacterReject.Attached, w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Ackbar, Hoth)));
        Assert.Equal(CharacterReject.AlreadyAttached, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Alliance, Ackbar, Yavin)));
        Assert.Equal(RejectReason.AlreadyThere, w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Wedge, Yavin)));
        Assert.Equal(CharacterReject.NoPath, w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Wedge, "Nowhere")));
        Assert.Equal(CharacterReject.UnknownCharacter, w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, 999, Hoth)));
        Assert.Equal(CharacterReject.UnknownCharacter, w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Luke, Hoth))); // not placed
        Assert.Null(w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Wedge, Hoth)));
        Assert.Equal(RejectReason.InTransit, w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Wedge, Coruscant)));
        Assert.Equal(CharacterReject.NotAtSystem, w.Try(seq => new AttachCharacterOrder(seq, Side.Alliance, Wedge, f, CharacterPost.None)));
        Assert.Single(w.Events<CharacterDeparted>());
        var dead = w.Add(Drayson, Yavin);
        w.StepWith("5.2", ctx => w.Ch.Kill(ctx, dead, CharacterCause.Debug));
        Assert.Equal(CharacterReject.NotFree, w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Drayson, Hoth)));
    }

    // m5-characters-and-force §Defaults for undecided items: GameSettings null = the module's defaults, each the sheet's DEFAULT
    [Fact]
    public void Defaults_AreTheSheetDefaults()
    {
        Assert.Null(new GameSettings().Characters);
        var p = GameHost.New(1).Characters.Params;
        Assert.Equal(new CharacterParams(), p);
        Assert.True(p.OfficersRideFleet); Assert.False(p.CharactersLostWithFleet); Assert.False(p.CharactersStoppedInTransit);
        Assert.False(p.BlockadeEscapeRisk); Assert.False(p.AssaultHarmsCharacters);
        Assert.False(p.MajorsCanDie); Assert.True(p.MinorsCanDie);
        Assert.True(p.DeathStarHitsOwnCharacters); Assert.True(p.CaptiveEscape); // B2e: escape is Traced, DEFAULT on
        Assert.True(p.LoyaltyTriggers); Assert.True(p.SeatOfPower); // B2e: Seat of Power Traced, DEFAULT on
        Assert.Equal(ForceDiscoveryMode.Trainer, p.ForceDiscovery); Assert.False(p.ForceGrowth); Assert.False(p.SkillGrowth); // B2e: trainers awaken Force users
        Assert.False(p.TrainerNeedsKnight); Assert.Equal(1, p.ForceStudentMin); Assert.Equal(80, p.ForceKnightMin); Assert.Equal(100, p.ForceMasterMin);
        Assert.False(p.NavigatorBonus); Assert.Equal(25, p.NavigatorFloor); Assert.Equal(50, p.DeathStarHitPercent); Assert.Equal(25, p.ForceTrainingGainPercent);
        Assert.Equal(40, p.ForceSkillPercent); Assert.Equal(50, p.ForceTrainingChancePercent); // m5-characters-and-force §Force (New, Paul 2026-10-18)
        Assert.Equal(100, p.CharacterSpeed); Assert.Equal(4, p.StartExtrasPerSide); Assert.Equal(StartCastOfficers.Original, p.StartCastOfficers);
    }

    // m5-characters-and-force §Where it runs: slot 6.10 sits after research (6.9) and before victory (6.11)
    [Fact]
    public void Slot610_BetweenResearchAndVictory()
    {
        var list = Game.WorkList.ToList();
        Assert.Equal(list.IndexOf("6.9") + 1, list.IndexOf("6.10"));
        Assert.Equal(list.IndexOf("6.10") + 1, list.IndexOf("6.11"));
    }

    // m5-characters-and-force §Force (Eligibility, Force discovery)
    [Fact]
    public void TrainingEligibility_AndStoryDiscovery()
    {
        var w = new CharWorld();
        var luke = w.Add(Luke, Yavin); var leia = w.Add(Leia, Yavin);
        Assert.False(w.Ch.CanTrain(luke, leia)); // Leia's Force is hidden
        w.StepWith("5.2", ctx => Assert.True(w.Ch.DiscoverForce(ctx, leia, CharacterCause.Story)));
        Assert.Single(w.Events<ForceDiscovered>());
        Assert.True(w.Ch.CanTrain(luke, leia));
        Assert.False(w.Ch.CanTrain(leia, luke)); // no trainer flag
        var knight = new CharWorld(new CharacterParams { TrainerNeedsKnight = true });
        var l2 = knight.Add(Luke, Yavin); var s2 = knight.Add(Leia, Yavin); s2.ForceDiscovered = true;
        Assert.False(knight.Ch.CanTrain(l2, s2)); // Luke 50 is not a Knight (80)
    }
}
