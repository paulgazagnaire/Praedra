using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Map;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Economy;
using static Praedra.Core.Tests.Characters.CharWorld;

namespace Praedra.Core.Tests.Characters;

/// <summary>The tests of m5-characters-and-talent §Tests (CH1 to CH34), plus the switch defaults.</summary>
public class CharacterTests
{
    // ------------------------------------------------------------------ stats (§Rolling the stats)

    // m5-characters-and-talent CH1
    [Fact]
    public void CH1_KethCreationDrawsInOrder()
    {
        var w = new CharWorld();
        var stub = new StubRng().Queue(12, 49, 60, 0, 10, 9, 33);
        var c = w.Ch.Create(w.Ch.Data.Get(Keth), stub);
        Assert.Equal(new[] { 12, 49, 100, 0, 0, 0, 100 }, c.Skills);
        Assert.True(c.TalentHolder);
        // B2e moved: Talent is rolled only once he is aware (B2e §5), so a hidden user has Talent 0 and the level draw is not made at creation
        Assert.False(c.TalentDiscovered);
        Assert.Equal(0, c.EffectiveTalent);
        Assert.Equal(new[] { (0, 30), (0, 50), (0, 60), (0, 50), (0, 10), (0, 99) }, stub.Requests.Select(r => (r.Min, r.Max)));

        var stub2 = new StubRng().Queue(12, 49, 60, 0, 10, 10);
        var c2 = w.Ch.Create(w.Ch.Data.Get(Keth), stub2);
        Assert.False(c2.TalentHolder);
        Assert.Equal(0, c2.EffectiveTalent);
        Assert.Equal(6, stub2.Requests.Count);
    }

    // m5-characters-and-talent CH2
    [Fact]
    public void CH2_MajorsCostNoDraws()
    {
        var w = new CharWorld();
        var stub = new StubRng();
        var midwife = w.Ch.Create(w.Ch.Data.Get(Midwife), stub);
        var nobody = w.Ch.Create(w.Ch.Data.Get(Nobody), stub);
        var ledger = w.Ch.Create(w.Ch.Data.Get(Ledger), stub);
        Assert.Empty(stub.Requests);
        Assert.Equal(0, midwife.EffectiveTalent); Assert.False(midwife.TalentHolder);
        Assert.Equal(50, nobody.EffectiveTalent); Assert.True(nobody.TalentDiscovered);
        Assert.Equal(0, ledger.EffectiveTalent); Assert.True(ledger.TalentHolder); Assert.False(ledger.TalentDiscovered); // B2e moved: Talent 0 until aware
    }

    // ------------------------------------------------------------------ Talent (§Talent)

    private static (CharWorld W, CharacterRecord T, CharacterRecord S) Training(int trainerId, int trainerTalent, int studentId, int studentTalent, CharacterParams? p = null)
    {
        var w = new CharWorld(p);
        var t = w.Add(trainerId, Shoal); t.TalentHolder = true; t.TalentExperience = trainerTalent;
        var s = w.Add(studentId, Shoal); s.TalentHolder = true; s.TalentExperience = studentTalent; s.TalentTraining = 0;
        return (w, t, s);
    }

    // m5-characters-and-talent CH3 (the Classic roll: Classic values set explicitly, CH37)
    [Fact]
    public void CH3_RavillaTrainsAStudentOfTwenty()
    {
        var (w, ravilla, s) = Training(Ravilla, 120, Tura, 20, ClassicTalent());
        w.Stub!.Expect(0, 99, 99).Expect(0, 50, 37);
        w.StepWith("6.10", ctx => w.Ch.TalentTrainingRound(ctx, ravilla, new[] { s }));
        var e = Assert.Single(w.Events<TalentTrainingResult>());
        Assert.Equal((100, 99, true, 37, 57), (e.Gap, e.R, e.Success, e.Gain, e.NewTalent));
        Assert.Equal(37, s.TalentTraining);
        Assert.Equal(57, s.EffectiveTalent);
    }

    // m5-characters-and-talent CH4 (the Classic roll: Classic values set explicitly, CH37)
    [Fact]
    public void CH4_NobodyTrainsLedger()
    {
        var (w, nobody, ledger) = Training(Nobody, 50, Ledger, 10, ClassicTalent());
        w.Stub!.Expect(0, 99, 39).Expect(0, 20, 20);
        w.StepWith("6.10", ctx => w.Ch.TalentTrainingRound(ctx, nobody, new[] { ledger }));
        Assert.Equal(30, ledger.EffectiveTalent);
        int before = w.Stub.Requests.Count;
        w.Stub.Expect(0, 99, 40);
        w.StepWith("6.10", ctx => w.Ch.TalentTrainingRound(ctx, nobody, new[] { ledger }));
        var last = w.Events<TalentTrainingResult>().Last();
        Assert.False(last.Success);
        Assert.Equal(30, ledger.EffectiveTalent);
        Assert.Equal(new[] { (0, 99) }, w.Stub.Requests.Skip(before).Select(r => (r.Min, r.Max)));
    }

    // m5-characters-and-talent CH5 (the Classic roll: Classic values set explicitly; under New a gap of 1 has chance 1 * 50 / 100 = 0)
    [Fact]
    public void CH5_EqualOrHigherStudentsNeverSucceed_GapOneGainsZero()
    {
        var (w, t, s) = Training(Nobody, 50, Ledger, 50, ClassicTalent());
        w.StepWith("6.10", ctx => w.Ch.TalentTrainingRound(ctx, t, new[] { s }));
        s.TalentExperience = 60;
        w.StepWith("6.10", ctx => w.Ch.TalentTrainingRound(ctx, t, new[] { s }));
        Assert.All(w.Events<TalentTrainingResult>(), e => Assert.False(e.Success));
        Assert.Equal(new[] { (0, 99), (0, 99) }, w.Stub!.Requests.Select(r => (r.Min, r.Max)));
        s.TalentExperience = 49;
        w.Stub.Expect(0, 99, 0);
        w.StepWith("6.10", ctx => w.Ch.TalentTrainingRound(ctx, t, new[] { s }));
        var e3 = w.Events<TalentTrainingResult>().Last();
        Assert.True(e3.Success); Assert.Equal(0, e3.Gain);
        Assert.Equal((0, 0), (w.Stub.Requests[^1].Min, w.Stub.Requests[^1].Max)); // g = rand(0..0) is drawn
    }

    // m5-characters-and-talent CH6
    [Fact]
    public void CH6_NoStudentReachesTheTrainer()
    {
        var w = new CharWorld(seed: 6);
        var t = w.Add(Nobody, Shoal);
        var s = w.Add(Ledger, Shoal);
        s.TalentDiscovered = true; s.TalentExperience = 10; // B2e: her Talent is rolled once she is aware (level 10, variance 0)
        Assert.Equal((50, 10), (t.EffectiveTalent, s.EffectiveTalent));
        w.StepWith("6.10", ctx =>
        {
            for (int i = 0; i < 1000; i++)
            {
                w.Ch.TalentTrainingRound(ctx, t, new[] { s });
                Assert.True(s.EffectiveTalent < 50, $"round {i}: {s.EffectiveTalent}");
            }
        });
        Assert.True(s.EffectiveTalent > 10);
    }

    // m5-characters-and-talent CH35: New TalentSkillPercent 40 (DEFAULT): base 100, Talent 120 gives 148; Nobody (Talent 50) diplomacy 50 → 60,
    // espionage 50 → 60, combat 90 → 108, with 40 injury 68; research skills and leadership unchanged
    [Fact]
    public void CH35_NewTalentSkillPercentForty()
    {
        var w = new CharWorld();
        Assert.Equal(40, w.Ch.Params.TalentSkillPercent);
        var ravilla = w.Add(Ravilla, Charter); ravilla.TalentHolder = true; ravilla.TalentExperience = 120; ravilla.TalentTraining = 0;
        ravilla.Skills[(int)Skill.Combat] = 100; ravilla.Skills[(int)Skill.Espionage] = 100; ravilla.Skills[(int)Skill.Diplomacy] = 100;
        ravilla.Skills[(int)Skill.Leadership] = 70; ravilla.Skills[(int)Skill.ShipDesign] = 30; ravilla.Injury = 0; ravilla.LeadershipAdjustment = 0;
        Assert.Equal((148, 148, 148), (w.Ch.EffectiveSkill(ravilla, Skill.Combat), w.Ch.EffectiveSkill(ravilla, Skill.Espionage), w.Ch.EffectiveSkill(ravilla, Skill.Diplomacy)));
        Assert.Equal((70, 30), (w.Ch.EffectiveSkill(ravilla, Skill.Leadership), w.Ch.EffectiveSkill(ravilla, Skill.ShipDesign)));
        var nobody = w.Add(Nobody, Shoal); nobody.TalentHolder = true; nobody.TalentExperience = 50; nobody.TalentTraining = 0; nobody.Injury = 0;
        nobody.Skills[(int)Skill.Diplomacy] = 50; nobody.Skills[(int)Skill.Espionage] = 50; nobody.Skills[(int)Skill.Combat] = 90;
        Assert.Equal((60, 60, 108), (w.Ch.EffectiveSkill(nobody, Skill.Diplomacy), w.Ch.EffectiveSkill(nobody, Skill.Espionage), w.Ch.EffectiveSkill(nobody, Skill.Combat)));
        nobody.Injury = 40;
        Assert.Equal(68, w.Ch.EffectiveSkill(nobody, Skill.Combat));
    }

    // m5-characters-and-talent CH36: New training (chance percent 50, gain percent 25, DEFAULT): Ravilla 120, student 20 (gap 100): chance 50,
    // r = 49 succeeds, g = rand(0..25), g = 25 → 45; r = 50 fails with no g draw. Nobody 50, Ledger 10 (gap 40): chance 20, r = 19 succeeds, max gain 10
    [Fact]
    public void CH36_NewTrainingChanceAndGain()
    {
        var (w, ravilla, s) = Training(Ravilla, 120, Tura, 20);
        w.Stub!.Expect(0, 99, 49).Expect(0, 25, 25);
        w.StepWith("6.10", ctx => w.Ch.TalentTrainingRound(ctx, ravilla, new[] { s }));
        var e = Assert.Single(w.Events<TalentTrainingResult>());
        Assert.Equal((100, 49, true, 25, 45), (e.Gap, e.R, e.Success, e.Gain, e.NewTalent));
        s.TalentTraining = 0;
        int before = w.Stub.Requests.Count;
        w.Stub.Expect(0, 99, 50);
        w.StepWith("6.10", ctx => w.Ch.TalentTrainingRound(ctx, ravilla, new[] { s }));
        Assert.False(w.Events<TalentTrainingResult>().Last().Success);
        Assert.Equal(20, s.EffectiveTalent);
        Assert.Equal(new[] { (0, 99) }, w.Stub.Requests.Skip(before).Select(r => (r.Min, r.Max)));

        var (w2, nobody, ledger) = Training(Nobody, 50, Ledger, 10);
        w2.Stub!.Expect(0, 99, 19).Expect(0, 10, 10);
        w2.StepWith("6.10", ctx => w2.Ch.TalentTrainingRound(ctx, nobody, new[] { ledger }));
        Assert.Equal(20, ledger.EffectiveTalent);
        w2.Stub.Expect(0, 99, 10); // gap 30 now: chance 15, r = 10 succeeds, max gain 30 * 25 / 100 = 7
        w2.Stub.Expect(0, 7, 7);
        w2.StepWith("6.10", ctx => w2.Ch.TalentTrainingRound(ctx, nobody, new[] { ledger }));
        Assert.Equal(27, ledger.EffectiveTalent);
        w2.Stub.Expect(0, 99, 11); // gap 23: chance 11, r = 11 fails
        w2.StepWith("6.10", ctx => w2.Ch.TalentTrainingRound(ctx, nobody, new[] { ledger }));
        Assert.False(w2.Events<TalentTrainingResult>().Last().Success);
    }

    // m5-characters-and-talent CH37: the Classic values (100 / 100 / 50) reproduce CH3 and CH4 (those tests run with them) and B2e-1/B2e-2, and the
    // skill formula with percent 100 equals B + B * F / 100 for every base 0..120 and Talent 0..200 (bit-identical Classic)
    [Fact]
    public void CH37_ClassicValuesReproduceTheClassicTalent()
    {
        var w = new CharWorld(ClassicTalent());
        var nobody = w.Add(Nobody, Shoal); nobody.TalentHolder = true; nobody.TalentExperience = 50; nobody.TalentTraining = 0; nobody.Injury = 0;
        nobody.Skills[(int)Skill.Diplomacy] = 50; nobody.Skills[(int)Skill.Espionage] = 50; nobody.Skills[(int)Skill.Combat] = 90;
        Assert.Equal((75, 75, 135), (w.Ch.EffectiveSkill(nobody, Skill.Diplomacy), w.Ch.EffectiveSkill(nobody, Skill.Espionage), w.Ch.EffectiveSkill(nobody, Skill.Combat)));
        var c = new CharacterRecord { Skills = new List<int>(new int[7]) };
        for (int b = 0; b <= 120; b++)
            for (int f = 0; f <= 200; f++)
            {
                c.Skills[(int)Skill.Espionage] = b; c.TalentExperience = f;
                Assert.Equal(b + b * f / 100, c.EffectiveSkill(Skill.Espionage, CharacterParams.ClassicTalentSkillPercent));
            }
        CH3_RavillaTrainsAStudentOfTwenty();
        CH4_NobodyTrainsLedger();
    }

    // m5-characters-and-talent CH7
    [Theory]
    [InlineData(0, TalentRank.Latent), InlineData(1, TalentRank.Deviation), InlineData(79, TalentRank.Deviation), InlineData(80, TalentRank.Anomaly)]
    [InlineData(99, TalentRank.Anomaly), InlineData(100, TalentRank.Unforecastable), InlineData(150, TalentRank.Unforecastable)]
    public void CH7_Rank(int force, TalentRank rank)
    {
        var ch = new CharWorld().Ch;
        Assert.Equal(rank, ch.RankFor(true, force));
        Assert.Null(ch.RankFor(false, force));
    }

    // ------------------------------------------------------------------ posts and the leadership hooks (§Posts)

    // m5-characters-and-talent CH8
    [Fact]
    public void CH8_AdmiralMultipliesBombardment_UntilInjured()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Collective, Shoal, Ark);
        var ack = w.Add(Keth, Shoal); ack.Skills[(int)Skill.Leadership] = 100;
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Collective, Keth, f, CharacterPost.Admiral)));
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
        Assert.Equal((Keth, PostKind.Admiral), (pv.Character, pv.Post));
        Assert.Equal((f, CharacterPost.None), (ack.AttachedFleet, ack.Post));
        Assert.Equal(Shoal, w.Ch.LocationOf(ack));
    }

    // m5-characters-and-talent CH9
    [Fact]
    public void CH9_MidwifeAsFleetGeneralIsTheAssaultPlusOneRow()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Collective, Thaw);
        w.Add(Midwife, Thaw);
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Collective, Midwife, f, CharacterPost.General)));
        var a = w.Host.Assault;
        int lAtt = a.FleetGeneralLeadership(w.Game.State.Fleets[f]), lDef = a.SystemGeneralLeadership(Thaw);
        Assert.Equal((90, 0), (lAtt, lDef));
        Assert.Equal(3, a.LeadershipSteps(lAtt));
        for (int roll = 0; roll <= 10; roll++) // Army attack 3 vs Army defense 5 plus Midwife: net row +1 (A4)
            Assert.Equal(roll + 1, a.DuelValue(roll, 3, 5, lAtt, lDef));
    }

    // m5-characters-and-talent CH10
    [Fact]
    public void CH10_SystemGeneralLeadsTheDefenders()
    {
        var w = new CharWorld();
        w.Hold(Thaw, Side.Hand);
        var p = w.Add(Varo, Thaw); p.Skills[(int)Skill.Leadership] = 85;
        Assert.Null(w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Hand, Varo, Thaw)));
        Assert.Single(w.Events<SystemGeneralAssigned>());
        var a = w.Host.Assault;
        Assert.Equal(85, a.SystemGeneralLeadership(Thaw));
        Assert.Equal(2, a.LeadershipSteps(85));
        foreach (var (l, steps) in new[] { (0, 0), (29, 0), (30, 1), (59, 1), (60, 2), (89, 2), (90, 3), (119, 3), (120, 4) })
            Assert.Equal(steps, a.LeadershipSteps(l));
    }

    // m5-characters-and-talent CH11
    [Fact]
    public void CH11_AttachRejections_OtiAsPassenger()
    {
        // The Mandate at The Shoal starts a battle on the Step. Ruleset = Classic set explicitly (battle DEFAULT New since 2026-10-18): in Classic
        // both corvettes retreat together; in New the Mandate's Hold beam holds fleet f's corvette and destroys it (m4-retreat-damage-repair
        // HoldBeamsHoldRetreat), which dissolves the fleet and detaches Oti. This test checks the attach rules, not the battle.
        var w = new CharWorld(settings: new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true,
            Economy = new Praedra.Core.Economy.EconomyParams { ScrapPeriod = 1_000_000 },
            Battle = new Praedra.Core.Battle.BattleParams { Ruleset = Praedra.Core.Battle.BattleRuleset.Classic } });
        int f = w.Fleet(Side.Collective, Shoal);
        int moving = w.Fleet(Side.Collective, Shoal);
        int imp = w.Fleet(Side.Hand, Shoal, Mandate);
        w.Add(Keth, Shoal); w.Add(Wake, Shoal); w.Add(Oti, Shoal); w.Add(Petrenko, Shoal); w.Add(OldDebt, Shoal);
        w.Add(FortyOne, Thaw);
        w.Ch.IsOnMission = id => id == OldDebt;
        long Attach(Side s, int c, int fleet, CharacterPost p) => w.Submit(seq => new AttachCharacterOrder(seq, s, c, fleet, p));

        long a1 = Attach(Side.Hand, Keth, f, CharacterPost.Admiral);
        long a2 = Attach(Side.Collective, Keth, imp, CharacterPost.Admiral);
        long a3 = Attach(Side.Collective, FortyOne, f, CharacterPost.None);
        long mv = w.Submit(seq => new MoveFleetOrder(seq, Side.Collective, moving, Thaw));
        long a4 = Attach(Side.Collective, Keth, moving, CharacterPost.Admiral);
        long a5 = Attach(Side.Collective, Wake, f, CharacterPost.Admiral);
        long a6 = Attach(Side.Collective, OldDebt, f, CharacterPost.None);
        long ok = Attach(Side.Collective, Keth, f, CharacterPost.Admiral);
        long a7 = Attach(Side.Collective, Petrenko, f, CharacterPost.Admiral);
        long a8 = Attach(Side.Collective, Keth, f, CharacterPost.None);
        long chewie = Attach(Side.Collective, Oti, f, CharacterPost.None);
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
        Assert.Equal((f, CharacterPost.None), (w.C(Oti).AttachedFleet, w.C(Oti).Post));
        Assert.Equal((f, CharacterPost.Admiral), (w.C(Keth).AttachedFleet, w.C(Keth).Post));
        foreach (var id in new[] { Wake, Petrenko, OldDebt, FortyOne }) Assert.Equal(0, w.C(id).AttachedFleet);
    }

    // m5-characters-and-talent CH12
    [Fact]
    public void CH12_CapturedGeneralInFlight_SpeedFixedAtOrderTime()
    {
        var w = new CharWorld(new CharacterParams { NavigatorBonus = true });
        int f = w.Fleet(Side.Collective, Shoal);
        w.Add(Midwife, Shoal);
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Collective, Midwife, f, CharacterPost.General)));
        var fleet = w.Game.State.Fleets[f];
        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Collective, f, Charter)));
        var accepted = w.Events<OrderAccepted>().Last();
        var ticks = fleet.Transit!.HopTicks.ToList();
        Assert.Equal(25, w.Ch.NavigatorAdjust(fleet, 50)); // Midwife rides: the trip was planned at 80 * 50 / 100 = 40
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, w.C(Midwife), Side.Hand, null, CharacterCause.Battle));
        var midwife = w.C(Midwife);
        Assert.Equal(CharacterStatus.Captured, midwife.Status);
        Assert.Equal(0, midwife.AttachedFleet);
        Assert.Equal(fleet.Transit!.Path[fleet.Transit.HopIndex], midwife.HeldAt); // the fleet is on a lane: the system it last left
        Assert.NotNull(midwife.HeldAt);
        Assert.Equal(0, w.Host.Assault.FleetGeneralLeadership(fleet));
        Assert.Equal(ticks, fleet.Transit!.HopTicks);
        Assert.Equal(accepted.ArrivalDay, accepted.Day + fleet.Transit.HopTicks.Sum());
    }

    // ------------------------------------------------------------------ movement (§Moving a character alone, §Riding)

    private static GameData LineGalaxy(string kind)
    {
        var planets = "name,sector,group,map_x,map_y\nA,S,heartland,0,0\nB,S,heartland,500,0\nC,S,heartland,1000,0\n";
        string routes = kind == "route" ? "Test Run" : "";
        var lanes = $"from,to,kind,routes\nA,B,{kind},{routes}\nB,C,{kind},{routes}\n";
        var g = GalaxyLoader.Load(planets, lanes, TestData.Tiers);
        var slots = "name,sector,group,energy,raw,populated\n" + string.Join("\n", g.Systems.Select(s => $"{s.Name},{s.Sector},{s.Group},5,3,1"));
        return GameData.Default with { Galaxy = g, Slots = SlotValues.Parse(slots, g) };
    }

    // m5-characters-and-talent CH13
    [Theory]
    [InlineData("generated", 100)]
    [InlineData("route", 33)]
    public void CH13_LoneCharacterTravelsAtSpeedHundred(string kind, int days)
    {
        var w = new CharWorld(data: LineGalaxy(kind));
        w.Add(FortyOne, "A");
        int k = w.Game.Day;
        Assert.Null(w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, FortyOne, "B")));
        Assert.Null(w.Ch.LocationOf(w.C(FortyOne)));
        while (w.Events<CharacterArrived>().Count == 0) w.Game.Step();
        var e = Assert.Single(w.Events<CharacterArrived>());
        Assert.Equal(k + days, e.Day); // placed at step 3 of the Step K + days - 1
        Assert.Equal(k + days, w.Game.Day);
        Assert.Equal("B", w.Ch.LocationOf(w.C(FortyOne)));
    }

    // m5-characters-and-talent CH13 (not stopped on the way)
    [Fact]
    public void CH13_NotStoppedByAnEnemyFleet()
    {
        var w = new CharWorld(data: LineGalaxy("generated"));
        w.Fleet(Side.Hand, "B", Mandate, 3);
        w.Add(FortyOne, "A");
        Assert.Null(w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, FortyOne, "C")));
        w.Run(250);
        var e = Assert.Single(w.Events<CharacterArrived>());
        Assert.Equal(("C", 200), (e.System, e.Day));
        Assert.Equal(CharacterStatus.Free, w.C(FortyOne).Status);
    }

    // m5-characters-and-talent CH14
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CH14_RidersFollowTheirFleet(bool ride)
    {
        var w = new CharWorld(new CharacterParams { OfficersRideFleet = ride });
        int f = w.Fleet(Side.Collective, Shoal);
        w.Add(Keth, Shoal);
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Collective, Keth, f, CharacterPost.Admiral)));
        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Collective, f, Thaw)));
        var ack = w.C(Keth);
        if (ride)
        {
            Assert.Null(w.Ch.LocationOf(ack));
            while (w.Game.State.Fleets[f].At == null) w.Game.Step();
            Assert.Equal(Thaw, w.Ch.LocationOf(ack));
            Assert.Equal(f, ack.AttachedFleet);
        }
        else
        {
            Assert.Equal(0, ack.AttachedFleet);
            Assert.Equal(Shoal, w.Ch.LocationOf(ack));
            Assert.Equal(CharacterCause.FleetMoved, Assert.Single(w.Events<CharacterDetached>()).Cause);
        }
    }

    // ------------------------------------------------------------------ Navigator (§Navigator)

    private static (CharWorld W, Praedra.Core.Fleets.Fleet F) Nav(bool on, params int[] riders)
    {
        var w = new CharWorld(new CharacterParams { NavigatorBonus = on });
        int f = w.Fleet(Side.Collective, Shoal);
        foreach (var id in riders) { w.Add(id, Shoal); w.Ch.PlaceInFleet(id, f); }
        return (w, w.Game.State.Fleets[f]);
    }

    // m5-characters-and-talent CH15
    [Fact]
    public void CH15_MidwifeHalvesTheTrip()
    {
        var (w, f) = Nav(true, Midwife);
        Assert.Equal(80, w.Host.Fleets.SpeedOf(f));
        Assert.Equal(40, w.Ch.NavigatorAdjust(f, 80));
        Assert.Equal(40, w.Host.Fleets.SpeedAdjust!(f, 80)); // the fleet module reads the character hook at MoveFleet acceptance
    }

    // m5-characters-and-talent CH16
    [Fact]
    public void CH16_BestSingleNavigator()
    {
        Assert.Equal(40, Nav(true, Midwife, Foreman) is var (w1, f1) ? w1.Ch.NavigatorAdjust(f1, 80) : 0);
        Assert.Equal(68, Nav(true, Oti) is var (w2, f2) ? w2.Ch.NavigatorAdjust(f2, 80) : 0);
        Assert.Equal(80, Nav(true) is var (w3, f3) ? w3.Ch.NavigatorAdjust(f3, 80) : 0);
    }

    // m5-characters-and-talent CH17
    [Fact]
    public void CH17_FloorOnlyWithANavigator()
    {
        var (w, f) = Nav(true, Midwife);
        Assert.Equal(25, w.Ch.NavigatorAdjust(f, 40));
        var (w2, f2) = Nav(true);
        Assert.Equal(20, w2.Ch.NavigatorAdjust(f2, 20));
    }

    // m5-characters-and-talent CH18
    [Fact]
    public void CH18_NoBonusInClassicOrFromANonFreeNavigator()
    {
        var (w, f) = Nav(false, Midwife);
        Assert.Equal(80, w.Ch.NavigatorAdjust(f, 80));
        var (w2, f2) = Nav(true, Midwife);
        w2.StepWith("5.2", ctx => w2.Ch.Capture(ctx, w2.C(Midwife), Side.Hand, null, CharacterCause.Debug));
        Assert.Equal(80, w2.Ch.NavigatorAdjust(f2, 80));
        var (w3, f3) = Nav(true, Midwife);
        w3.C(Midwife).Status = CharacterStatus.Injured; // injured but still riding (state set directly: Injure would detach him)
        Assert.Equal(80, w3.Ch.NavigatorAdjust(f3, 80));
    }

    // ------------------------------------------------------------------ the harvester (§The harvester's roll on characters)

    // m5-characters-and-talent CH19
    [Fact]
    public void CH19_HarvesterRollOnMinors()
    {
        var w = new CharWorld();
        w.Add(Sobel, Thaw); w.Add(DayAfter, Thaw); var c = w.Add(Tura, Shoal); w.Add(Ravilla, Thaw);
        w.StepWith("5.1", ctx => w.Ch.Capture(ctx, c, Side.Hand, Thaw, CharacterCause.Debug));
        int before = w.Stub!.Requests.Count, ev = w.Game.Events.Count;
        // The die is rand(1..100), hit at 50 or less (m5-harvester §2 effect 3); the same chance as the characters sheet's rand(0..99) < 50, shifted by one: 50 hits, 51 misses, 11 hits.
        w.Stub.Expect(1, 100, 50, 51, 11);
        w.StepWith("5.1", ctx => w.Ch.HarvesterStrike(ctx, Thaw, Side.Hand));
        // B2e moved: the hit on a free minor is the Hurt roll (unqueued draws answer 0: hit, sizes 0 and 0, then the minor's death draw 0 < 25),
        // so Admiral Marta Sobel dies of the injury and the kill is not needed; the prisoner Tura is killed without it. 3 + 4 draws.
        Assert.Equal(7, w.Stub.Requests.Count - before);
        Assert.Equal(CharacterStatus.Dead, w.C(Sobel).Status);
        Assert.Equal(CharacterStatus.Free, w.C(DayAfter).Status);
        Assert.Equal(CharacterStatus.Dead, w.C(Tura).Status);
        Assert.Equal(CharacterStatus.Free, w.C(Ravilla).Status);
        var order = w.Game.Events.Skip(ev).Where(e => e is CharacterInjured or CharacterKilled)
            .Select(e => (e.Kind, e is CharacterInjured i ? i.Character : ((CharacterKilled)e).Character)).ToList();
        Assert.Equal(new[] { ("CharacterInjured", Sobel), ("CharacterKilled", Sobel), ("CharacterKilled", Tura) }, order);
    }

    // ------------------------------------------------------------------ the state machine (§Capture, injury, death, freeing)

    // m5-characters-and-talent CH20
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CH20_MajorsAreInjuredInsteadOfKilled(bool majorsCanDie)
    {
        var w = new CharWorld(new CharacterParams { MajorsCanDie = majorsCanDie });
        var nobody = w.Add(Nobody, Shoal);
        w.StepWith("5.2", ctx => w.Ch.Kill(ctx, nobody, CharacterCause.Mission));
        if (!majorsCanDie)
        {
            Assert.Single(w.Events<CharacterInjured>());
            Assert.Empty(w.Events<CharacterKilled>());
            // B2e moved: the Kill becomes the Hurt roll (p = 1 for Nobody's effective combat 135; the stub's 0 hits, size 0 + 0 + 1); The Shoal is
            // nobody's, so he does not heal
            Assert.Equal((CharacterStatus.Injured, 1), (nobody.Status, nobody.Injury));
            w.StepWith("5.2", ctx => w.Ch.Capture(ctx, nobody, Side.Hand, null, CharacterCause.Mission));
            Assert.True(w.Ch.IsCaptured(Nobody)); // the victory term stays reachable
        }
        else
        {
            Assert.Equal(CharacterStatus.Dead, nobody.Status);
            Assert.True(w.Ch.IsDead(Nobody));
        }
    }

    // m5-characters-and-talent CH21
    [Fact]
    public void CH21_CaptureNobody()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Collective, Shoal);
        var nobody = w.Add(Nobody, Shoal);
        w.Ch.SetPost(Nobody, f, CharacterPost.General);
        bool first = false, second = true, own = true;
        w.StepWith("5.2", ctx =>
        {
            first = w.Ch.Capture(ctx, nobody, Side.Hand, null, CharacterCause.Mission);
            second = w.Ch.Capture(ctx, nobody, Side.Hand, null, CharacterCause.Mission);
        });
        Assert.True(first); Assert.False(second);
        Assert.Equal((CharacterStatus.Captured, (Side?)Side.Hand, Shoal), (nobody.Status, nobody.Captor, nobody.HeldAt));
        Assert.Equal((0, CharacterPost.None), (nobody.AttachedFleet, nobody.Post));
        Assert.Equal(PostKind.General, Assert.Single(w.Events<PostVacated>()).Post);
        Assert.Single(w.Events<CharacterCaptured>());
        var ledger = w.Add(Ledger, Shoal);
        w.StepWith("5.2", ctx => own = w.Ch.Capture(ctx, ledger, Side.Collective, null, CharacterCause.Mission));
        Assert.False(own);
        Assert.Equal(CharacterStatus.Free, ledger.Status);
    }

    // m5-characters-and-talent CH22 (B2e moved: Injure is the Hurt roll on a Free or Injured character; never a captured or dead one)
    [Fact]
    public void CH22_InjureNotACapturedOrDeadCharacter()
    {
        var w = new CharWorld();
        var nobody = w.Add(Nobody, Shoal); var midwife = w.Add(Midwife, Shoal);
        w.StepWith("5.2", ctx => { w.Ch.Capture(ctx, nobody, Side.Hand, null, CharacterCause.Debug); w.Ch.Injure(ctx, midwife, CharacterCause.Debug); });
        Assert.Equal(CharacterStatus.Injured, midwife.Status);
        int ev = w.Game.Events.Count, pts = midwife.Injury;
        bool a = true, b = false;
        w.StepWith("5.2", ctx => { a = w.Ch.Injure(ctx, nobody, CharacterCause.Debug); b = w.Ch.Injure(ctx, midwife, CharacterCause.Debug); });
        Assert.False(a); Assert.True(b);
        Assert.True(midwife.Injury > pts);
        Assert.Equal(midwife.Id, Assert.Single(w.Game.Events.Skip(ev).OfType<CharacterInjured>()).Character);
    }

    // m5-characters-and-talent CH23 (B2e moved: an injury lasts until its points heal, 1 a day on a planet his side holds)
    [Fact]
    public void CH23_InjuryHealsAtSlot610()
    {
        var w = new CharWorld();
        w.Hold(Shoal, Side.Collective);
        var midwife = w.Add(Midwife, Shoal);
        w.StepWith("5.2", ctx => w.Ch.AddInjury(ctx, midwife, 3, CharacterCause.Debug));
        Assert.Equal(CharacterStatus.Injured, midwife.Status);
        int step = 1;
        while (midwife.Status != CharacterStatus.Free) { w.Game.Step(); step++; }
        Assert.Equal(4, step); // healing starts the day after it turned on: 3, 2, 1, 0
        Assert.Single(w.Events<CharacterHealed>());
    }

    // m5-characters-and-talent CH24 (B2e moved: CaptiveEscape is on by default; off makes no timer and no draw)
    [Fact]
    public void CH24_NoEscapeDrawsWhenOff_OneDelayDrawWhenOn()
    {
        var w = new CharWorld(new CharacterParams { CaptiveEscape = false });
        var nobody = w.Add(Nobody, Shoal);
        int before = w.Stub!.Requests.Count;
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, nobody, Side.Hand, null, CharacterCause.Debug));
        w.Run(200);
        Assert.Equal(before, w.Stub.Requests.Count);
        Assert.Equal(0, nobody.NextEscapeDay);
        var on = new CharWorld();
        var l2 = on.Add(Nobody, Shoal);
        on.Stub!.Expect(0, 1000, 400);
        on.StepWith("5.2", ctx => on.Ch.Capture(ctx, l2, Side.Hand, null, CharacterCause.Debug));
        Assert.Equal(0 + 100 + 400, l2.NextEscapeDay);
    }

    // m5-characters-and-talent CH25 (B2e moved: the input is Traced; a test table (-50, 1), (50, 30); Nobody alone: x = 135 + 75 = 210)
    [Theory]
    [InlineData(29, true), InlineData(30, false)]
    public void CH25_EscapeWithATestTable(int r, bool freed)
    {
        var w = new CharWorld(new CharacterParams { EscapeTable = new[] { (-50, 1), (50, 30) } });
        Assert.Equal((1, 1, 1, 30, 30), (w.Ch.EscapePercent(-80), w.Ch.EscapePercent(-50), w.Ch.EscapePercent(49), w.Ch.EscapePercent(50), w.Ch.EscapePercent(210)));
        var nobody = w.Add(Nobody, Shoal);
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, nobody, Side.Hand, null, CharacterCause.Debug));
        nobody.NextEscapeDay = w.Game.Day; // the attempt is due at this Step's slot 6.10
        w.Stub!.Expect(0, 99, r);
        int ev = w.Game.Events.Count;
        w.Game.Step();
        Assert.Equal(freed, nobody.Status == CharacterStatus.Free);
        Assert.Equal(freed ? 1 : 0, w.Game.Events.Skip(ev).OfType<CharacterEscaped>().Count());
    }

    // ------------------------------------------------------------------ other sheets' effects

    // m5-characters-and-talent CH26
    [Fact]
    public void CH26_UprisingInjuresOneFreeCharacterOfTheHolder()
    {
        var w = new CharWorld();
        w.Hold(Thaw, Side.Collective);
        int f = w.Fleet(Side.Collective, Thaw);
        w.Add(FortyOne, Thaw); w.Add(Oti, Thaw); w.Add(Sobel, Thaw); w.Ch.PlaceInFleet(Sobel, f); // attached ones count
        w.Add(Nobody, Shoal); // elsewhere
        w.Add(Varo, Thaw); // other side
        var ledger = w.Add(Ledger, Shoal);
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, ledger, Side.Hand, Thaw, CharacterCause.Debug)); // captured: not a candidate
        Assert.NotNull(w.Host.Support.CharacterIncident);
        int before = w.Stub!.Requests.Count;
        w.Stub.Expect(0, 2, 1);
        w.Stub.Expect(0, 99, 0, 99); // B2e moved: Injure is the Hurt roll (0 hits), then the minor's death draw (99: he lives)
        w.StepWith("6.3", ctx => w.Host.Support.CharacterIncident!(ctx, ctx.Day + 1, Thaw, Side.Collective, 3));
        Assert.Equal(new[] { (0, 2), (0, 99) }, w.Stub.Requests.Skip(before).Take(2).Select(x => (x.Min, x.Max)));
        Assert.Equal(CharacterStatus.Injured, w.C(Oti).Status);
        Assert.Equal(Oti, Assert.Single(w.Events<CharacterInjured>()).Character);

        var w2 = new CharWorld();
        w2.StepWith("6.3", ctx => w2.Ch.UprisingIncident(ctx, ctx.Day + 1, Thaw, Side.Collective, 3));
        Assert.Empty(w2.Stub!.Requests);
    }

    // m5-characters-and-talent CH27
    [Fact]
    public void CH27_UprisingFreesPrisoners()
    {
        CharWorld Make()
        {
            var w = new CharWorld();
            var a = w.Add(FortyOne, Thaw); var b = w.Add(Oti, Thaw);
            w.StepWith("5.2", ctx => { w.Ch.Capture(ctx, a, Side.Hand, null, CharacterCause.Debug); w.Ch.Capture(ctx, b, Side.Hand, null, CharacterCause.Debug); });
            return w;
        }
        var w4 = Make();
        w4.Stub!.Expect(0, 1, 0);
        w4.StepWith("6.3", ctx => w4.Ch.UprisingIncident(ctx, ctx.Day + 1, Thaw, Side.Hand, 4));
        Assert.Equal(CharacterStatus.Free, w4.C(FortyOne).Status);
        Assert.Equal(CharacterStatus.Captured, w4.C(Oti).Status);

        var w5 = Make();
        int before = w5.Stub!.Requests.Count;
        w5.StepWith("6.3", ctx => w5.Ch.UprisingIncident(ctx, ctx.Day + 1, Thaw, Side.Hand, 5));
        Assert.Equal(before, w5.Stub.Requests.Count);
        Assert.All(new[] { FortyOne, Oti }, id => Assert.Equal(CharacterStatus.Free, w5.C(id).Status));
        Assert.Equal(2, w5.Events<CharacterFreed>().Count);
    }

    // m5-characters-and-talent CH28
    [Fact]
    public void CH28_HolderChangeClearsTheOldGeneral()
    {
        var w = new CharWorld();
        w.Hold(Thaw, Side.Hand);
        w.Hold(Charter, Side.Hand); // (B5a, 2026-10-17) somewhere for the old side's officer to go
        w.Add(Varo, Thaw);
        Assert.Null(w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Hand, Varo, Thaw)));
        // (B5a, 2026-10-17) under ControlRule = Derived the holder follows the regiments: a Collective regiment lands on the empty world (B5a section 1)
        w.StepWith("5.2", ctx => w.Host.Control.DebugApplyTroops(ctx, Thaw, Side.Collective, "Militia", 1));
        Assert.Equal(Side.Collective, w.Host.Control.HolderOf(Thaw));
        var pv = Assert.Single(w.Events<PostVacated>());
        Assert.Equal((Varo, PostKind.SystemGeneral, CharacterCause.HolderChanged), (pv.Character, pv.Post, pv.Cause));
        Assert.Null(w.Ch.SystemGeneralOf(Thaw));
        // (B5a, 2026-10-17) the old side's character is not captured or hurt; he leaves (B5a section 4, 0x4ef260 Autorouting; DEFAULT destination: nearest held system)
        Assert.Equal(CharacterStatus.Free, w.C(Varo).Status);
        Assert.Equal(Thaw, Assert.Single(w.Events<CharacterDeparted>(), e => e.Character == Varo).From);
        Assert.Equal(0, w.Host.Assault.SystemGeneralLeadership(Thaw));
        // the old holder's officer may not be General there now ((B5a, 2026-10-17) he has left, so the first refusal is NotAtSystem)
        Assert.Equal(CharacterReject.NotAtSystem, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Hand, Varo, Thaw)));
    }

    // m5-characters-and-talent CH29
    [Fact]
    public void CH29_MergeKeepsTheReceivingAdmiral()
    {
        var w = new CharWorld();
        int into = w.Fleet(Side.Collective, Shoal), from = w.Fleet(Side.Collective, Shoal);
        w.Add(Keth, Shoal); w.Add(Petrenko, Shoal);
        w.Ch.SetPost(Keth, into, CharacterPost.Admiral);
        w.Ch.SetPost(Petrenko, from, CharacterPost.Admiral);
        Assert.Null(w.Try(seq => new MergeFleetsOrder(seq, Side.Collective, from, into)));
        Assert.Equal((into, CharacterPost.Admiral), (w.C(Keth).AttachedFleet, w.C(Keth).Post));
        Assert.Equal((into, CharacterPost.None), (w.C(Petrenko).AttachedFleet, w.C(Petrenko).Post));
        Assert.Equal(CharacterCause.Merged, Assert.Single(w.Events<PostVacated>()).Cause);
    }

    // m5-characters-and-talent CH30
    [Fact]
    public void CH30_DestroyedFleetLeavesItsRidersFree()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Collective, Thaw);
        w.Add(Keth, Thaw); w.Ch.SetPost(Keth, f, CharacterPost.Admiral);
        w.Add(Oti, Thaw); w.Ch.PlaceInFleet(Oti, f);
        int ship = w.Game.State.Fleets[f].Ships[0];
        w.StepWith("5", ctx => w.Host.Economy.DestroyUnit(ctx, ctx.Day + 1, w.Host.Economy.Find(ship)!));
        Assert.False(w.Game.State.Fleets.ContainsKey(f));
        foreach (var id in new[] { Keth, Oti })
        {
            var c = w.C(id);
            Assert.Equal((0, CharacterStatus.Free, Thaw), (c.AttachedFleet, c.Status, w.Ch.LocationOf(c)));
        }
        Assert.All(w.Events<CharacterDetached>(), e => Assert.Equal(CharacterCause.FleetDissolved, e.Cause));
    }

    // m5-characters-and-talent CH31
    [Fact]
    public void CH31_MissingRulesAreRefused()
    {
        foreach (var (p, rule) in new (CharacterParams, string)[]
                 {
                     (new() { TalentDiscovery = TalentDiscoveryMode.Detection }, "TalentDiscovery=Detection"), (new() { TalentGrowth = true }, "TalentGrowth"),
                     (new() { SkillGrowth = true }, "SkillGrowth"),
                     (new() { BlockadeEscapeRisk = true }, "BlockadeEscapeRisk"), (new() { AssaultHarmsCharacters = true }, "AssaultHarmsCharacters"),
                     (new() { CharactersLostWithFleet = true }, "CharactersLostWithFleet"), (new() { CharactersStoppedInTransit = true }, "CharactersStoppedInTransit"),
                 })
            Assert.Equal(rule, Assert.Throws<RuleMissingException>(() => GameHost.New(1, new GameSettings { Characters = p })).Rule);
    }

    // m5-characters-and-talent CH31 (play leaves loyalty untouched: ShiftLoyalty has no caller yet, so nobody betrays). B2b: betrayal exists now
    // (the rolls run at chance 0), so the old "no betrayal type" check is gone; B2b QA Q16.
    [Fact]
    public void CH31_LoyaltyNeverMoves()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 31 });
        for (int i = 0; i < 3000 && h.Victory.Winner == null; i++) h.Game.Step();
        Assert.All(h.Characters.All, c => Assert.Equal(100, c.Loyalty));
        Assert.Empty(h.Game.Events.OfType<Praedra.Core.Missions.MissionBetrayed>());
        Assert.Empty(h.Game.Events.OfType<LoyaltyChanged>());
    }

    // m5-characters-and-talent CH32
    [Fact]
    public void CH32_AddedHeroLimits()
    {
        var data = GameData.Default.Characters;
        var v = data.AddedHeroLimitViolations();
        Assert.Contains(v, x => x.Name == "Captain Sunday" && x.Rule == 6);
        foreach (var name in new[] { "Clementia Noll", "Mateus Kale", "Usura", "Mora", "Damnum", "Director Pythia Kallos", "Ushe" })
            Assert.Contains(v, x => x.Name == name && x.Rule == 2);
        Assert.All(v, x => Assert.True(data.Get(x.Character).Added)); // the original 60 pass (exempt)
        Assert.DoesNotContain(v, x => x.Rule is 1 or 3 or 4 or 7);
    }

    // m5-characters-and-talent CH33
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
        var nobody = a.Characters.Get(Nobody)!;
        Assert.Equal(nobody.Status, b.Characters.Get(Nobody)!.Status);
        Assert.NotNull(a.Characters.Get(Midwife)!.System);
    }

    private static void Orders(GameHost h)
    {
        var ch = h.Characters;
        var midwife = ch.Get(Midwife)!;
        var fleet = h.Game.State.Fleets.Values.FirstOrDefault(f => f.Side == Side.Collective && f.At == ch.LocationOf(midwife));
        if (fleet != null) h.Game.Submit(seq => new AttachCharacterOrder(seq, Side.Collective, Midwife, fleet.Id, CharacterPost.General));
        h.Game.Submit(seq => new MoveCharacterOrder(seq, Side.Collective, FortyOne, "The Thaw"));
        h.Game.Submit(seq => new MoveCharacterOrder(seq, Side.Hand, Ravilla, "Charter"));
    }

    // m5-characters-and-talent CH34
    [Fact]
    public void CH34_NoDetachInTransit()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Collective, Shoal);
        w.Add(Keth, Shoal);
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Collective, Keth, f, CharacterPost.Admiral)));
        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Collective, f, Charter)));
        Assert.Equal(CharacterReject.FleetInTransit, w.Try(seq => new DetachCharacterOrder(seq, Side.Collective, Keth)));
        Assert.Equal(f, w.C(Keth).AttachedFleet);
    }

    // ------------------------------------------------------------------ other orders, defaults

    // m5-characters-and-talent §Posts (Orders): DetachCharacter, ReleaseSystemGeneral, AssignSystemGeneral and MoveCharacter rejections
    [Fact]
    public void Orders_OtherRejections()
    {
        var w = new CharWorld();
        w.Hold(Shoal, Side.Collective);
        int f = w.Fleet(Side.Collective, Shoal);
        w.Add(Wake, Shoal); w.Add(FortyOne, Shoal); w.Add(Keth, Shoal); w.Add(Oti, Shoal);
        Assert.Equal(CharacterReject.NotAttached, w.Try(seq => new DetachCharacterOrder(seq, Side.Collective, Wake)));
        Assert.Equal(CharacterReject.NoSystemGeneral, w.Try(seq => new ReleaseSystemGeneralOrder(seq, Side.Collective, Shoal)));
        Assert.Equal(CharacterReject.NoCapability, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Collective, Oti, Shoal)));
        Assert.Equal(CharacterReject.NotAtSystem, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Collective, Wake, Thaw)));
        Assert.Null(w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Collective, Wake, Shoal)));
        Assert.Equal(CharacterReject.PostTaken, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Collective, FortyOne, Shoal)));
        Assert.Equal(CharacterReject.HoldsSystemPost, w.Try(seq => new AttachCharacterOrder(seq, Side.Collective, Wake, f, CharacterPost.General)));
        Assert.Equal(RejectReason.NotYours, w.Try(seq => new ReleaseSystemGeneralOrder(seq, Side.Hand, Shoal)));
        Assert.Null(w.Try(seq => new ReleaseSystemGeneralOrder(seq, Side.Collective, Shoal)));
        Assert.Null(w.Try(seq => new AttachCharacterOrder(seq, Side.Collective, Keth, f, CharacterPost.Admiral)));
        Assert.Equal(CharacterReject.Attached, w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, Keth, Thaw)));
        Assert.Equal(CharacterReject.AlreadyAttached, w.Try(seq => new AssignSystemGeneralOrder(seq, Side.Collective, Keth, Shoal)));
        Assert.Equal(RejectReason.AlreadyThere, w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, FortyOne, Shoal)));
        Assert.Equal(CharacterReject.NoPath, w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, FortyOne, "No Such Place")));
        Assert.Equal(CharacterReject.UnknownCharacter, w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, 999, Thaw)));
        Assert.Equal(CharacterReject.UnknownCharacter, w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, Nobody, Thaw))); // not placed
        Assert.Null(w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, FortyOne, Thaw)));
        Assert.Equal(RejectReason.InTransit, w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, FortyOne, Charter)));
        Assert.Equal(CharacterReject.NotAtSystem, w.Try(seq => new AttachCharacterOrder(seq, Side.Collective, FortyOne, f, CharacterPost.None)));
        Assert.Single(w.Events<CharacterDeparted>());
        var dead = w.Add(Sobel, Shoal);
        w.StepWith("5.2", ctx => w.Ch.Kill(ctx, dead, CharacterCause.Debug));
        Assert.Equal(CharacterReject.NotFree, w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, Sobel, Thaw)));
    }

    // m5-characters-and-talent §Defaults for undecided items: GameSettings null = the module's defaults, each the sheet's DEFAULT
    [Fact]
    public void Defaults_AreTheSheetDefaults()
    {
        Assert.Null(new GameSettings().Characters);
        var p = GameHost.New(1).Characters.Params;
        Assert.Equal(new CharacterParams(), p);
        Assert.True(p.OfficersRideFleet); Assert.False(p.CharactersLostWithFleet); Assert.False(p.CharactersStoppedInTransit);
        Assert.False(p.BlockadeEscapeRisk); Assert.False(p.AssaultHarmsCharacters);
        Assert.False(p.MajorsCanDie); Assert.True(p.MinorsCanDie);
        Assert.True(p.HarvesterHitsOwnCharacters); Assert.True(p.CaptiveEscape); // B2e: escape is Traced, DEFAULT on
        Assert.True(p.LoyaltyTriggers); Assert.True(p.SeatOfPower); // B2e: Seat of Power Traced, DEFAULT on
        Assert.Equal(TalentDiscoveryMode.Trainer, p.TalentDiscovery); Assert.False(p.TalentGrowth); Assert.False(p.SkillGrowth); // B2e: trainers awaken talented characters
        Assert.False(p.TrainerNeedsAnomaly); Assert.Equal(1, p.TalentDeviationMin); Assert.Equal(80, p.TalentAnomalyMin); Assert.Equal(100, p.TalentUnforecastableMin);
        Assert.False(p.NavigatorBonus); Assert.Equal(25, p.NavigatorFloor); Assert.Equal(50, p.HarvesterHitPercent); Assert.Equal(25, p.TalentTrainingGainPercent);
        Assert.Equal(40, p.TalentSkillPercent); Assert.Equal(50, p.TalentTrainingChancePercent); // m5-characters-and-talent §Talent (New, Paul 2026-10-18)
        Assert.Equal(100, p.CharacterSpeed); Assert.Equal(4, p.StartExtrasPerSide); Assert.Equal(StartCastOfficers.Classic, p.StartCastOfficers);
    }

    // m5-characters-and-talent §Where it runs: slot 6.10 sits after research (6.9) and before victory (6.11)
    [Fact]
    public void Slot610_BetweenResearchAndVictory()
    {
        var list = Game.WorkList.ToList();
        Assert.Equal(list.IndexOf("6.9") + 1, list.IndexOf("6.10"));
        Assert.Equal(list.IndexOf("6.10") + 1, list.IndexOf("6.11"));
    }

    // m5-characters-and-talent §Talent (Eligibility, Talent discovery)
    [Fact]
    public void TrainingEligibility_AndStoryDiscovery()
    {
        var w = new CharWorld(new CharacterParams { TrainerPick = TrainerPick.Fixed }); // the Classic trainer flag (Nobody, Ravilla); Random is CH39
        var nobody = w.Add(Nobody, Shoal); var ledger = w.Add(Ledger, Shoal);
        Assert.False(w.Ch.CanTrain(nobody, ledger)); // Ledger's Talent is hidden
        w.StepWith("5.2", ctx => Assert.True(w.Ch.DiscoverTalent(ctx, ledger, CharacterCause.Story)));
        Assert.Single(w.Events<TalentDiscovered>());
        Assert.True(w.Ch.CanTrain(nobody, ledger));
        Assert.False(w.Ch.CanTrain(ledger, nobody)); // no trainer flag
        var knight = new CharWorld(new CharacterParams { TrainerNeedsAnomaly = true });
        var l2 = knight.Add(Nobody, Shoal); var s2 = knight.Add(Ledger, Shoal); s2.TalentDiscovered = true;
        Assert.False(knight.Ch.CanTrain(l2, s2)); // Nobody 50 is not a Knight (80)
    }
}
