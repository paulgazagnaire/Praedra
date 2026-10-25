using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Missions;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Economy;
using Praedra.Core.Tests.Missions;
using static Praedra.Core.Tests.Characters.CharWorld;

namespace Praedra.Core.Tests.Characters;

// B2e-escape-injury-and-the-force tests B2e-1 to B2e-19. rule: m5-characters-and-force §Capture, injury, death, freeing; §Escape; §Talent;
// §Seat of Power; m5-missions §4 (the Talent encounter).
public class B2eCharacterTests
{
    private const string HandCommandos = "Closers";

    private static CharacterRecord Skills(CharacterRecord c, int dip = 0, int esp = 0, int com = 0, int lead = 0, int ship = 0)
    {
        c.Skills[(int)Skill.Diplomacy] = dip; c.Skills[(int)Skill.Espionage] = esp; c.Skills[(int)Skill.Combat] = com;
        c.Skills[(int)Skill.Leadership] = lead; c.Skills[(int)Skill.ShipDesign] = ship;
        return c;
    }

    /// <summary>B2e tests the Classic effective skills: TalentSkillPercent 100 set explicitly. rule: m5-characters-and-force §Talent (Classic = 100).</summary>
    private const int Classic = CharacterParams.ClassicTalentSkillPercent;

    private static CharacterRecord Talent(CharacterRecord c, int f)
    {
        c.TalentHolder = f > 0; c.TalentDiscovered = f > 0; c.TalentExperience = f; c.TalentTraining = 0;
        return c;
    }

    // B2e-1: Nobody, base diplomacy 50, espionage 50, combat 90, ship design 0, Talent 50: effective 75, 75, 135, 0; with 40 injury points combat 95
    [Fact]
    public void B2e1_EffectiveSkillsOfNobody()
    {
        var w = new CharWorld();
        var nobody = Talent(Skills(w.Add(Nobody, Shoal), dip: 50, esp: 50, com: 90, ship: 0), 50);
        Assert.Equal((75, 75, 135, 0), (nobody.EffectiveSkill(Skill.Diplomacy, Classic), nobody.EffectiveSkill(Skill.Espionage, Classic),
            nobody.EffectiveSkill(Skill.Combat, Classic), nobody.EffectiveSkill(Skill.ShipDesign, Classic)));
        nobody.Injury = 40;
        Assert.Equal(95, nobody.EffectiveSkill(Skill.Combat, Classic));
        Assert.Equal(75, nobody.EffectiveSkill(Skill.Diplomacy, Classic)); // the injury comes off combat only
    }

    // B2e-2: Ravilla Talent 120, base combat 100: 220; a character with Talent 0 keeps his base skills
    [Fact]
    public void B2e2_RavillaAndAnUntalentedCharacter()
    {
        var w = new CharWorld();
        var ravilla = Talent(Skills(w.Add(Ravilla, Charter), com: 100, esp: 50), 120);
        Assert.Equal((220, 110), (ravilla.EffectiveSkill(Skill.Combat, Classic), ravilla.EffectiveSkill(Skill.Espionage, Classic)));
        var midwife = Skills(w.Add(Midwife, Shoal), dip: 30, esp: 70, com: 100, lead: 90, ship: 10);
        Assert.Equal(0, midwife.EffectiveTalent);
        foreach (Skill s in Enum.GetValues<Skill>()) Assert.Equal(midwife.BaseSkill(s), midwife.EffectiveSkill(s, Classic));
    }

    // B2e-3: Hurt(50) with draws 49, 10, 5: n = 16; with first draw 50: no injury and one draw only
    [Fact]
    public void B2e3_HurtRoll()
    {
        var ch = new CharWorld().Ch;
        var a = new StubRng().Expect(0, 99, 49).Expect(0, 50, 10).Expect(0, 29, 5);
        Assert.Equal(16, ch.HurtRoll(a, 50));
        Assert.Equal(new[] { (0, 99), (0, 50), (0, 29) }, a.Requests);
        var b = new StubRng().Expect(0, 99, 50);
        Assert.Equal(0, ch.HurtRoll(b, 50));
        Assert.Single(b.Requests);
    }

    // B2e-4: Hurt(100) and Hurt(250): p = 1, so only a first draw of 0 hurts
    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    public void B2e4_HurtChanceIsAtLeastOne(int v)
    {
        var ch = new CharWorld().Ch;
        Assert.True(ch.HurtRoll(new StubRng().Expect(0, 99, 0), v) > 0);
        Assert.Equal(0, ch.HurtRoll(new StubRng().Expect(0, 99, 1), v));
    }

    // B2e-5: a hurt minor dies on a death draw of 24 (cause kept), lives on 25; a major makes no death draw
    [Theory]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void B2e5_MinorDeathTest(int draw, bool dies)
    {
        var w = new CharWorld();
        var fortyOne = w.Add(FortyOne, Shoal);
        w.Stub!.Expect(0, 99, draw);
        int before = w.Stub.Requests.Count;
        w.StepWith("5.2", ctx => w.Ch.AddInjury(ctx, fortyOne, 10, CharacterCause.Mission));
        Assert.Equal(dies ? CharacterStatus.Dead : CharacterStatus.Injured, fortyOne.Status);
        if (dies) Assert.Equal(CharacterCause.Mission, w.Events<CharacterKilled>().Single().Cause);
        Assert.Equal(1, w.Stub.Requests.Skip(before).Count(r => r == (0, 99)));

        var nobody = w.Add(Nobody, Shoal);
        before = w.Stub.Requests.Count;
        w.StepWith("5.2", ctx => w.Ch.AddInjury(ctx, nobody, 10, CharacterCause.Mission));
        Assert.Equal(CharacterStatus.Injured, nobody.Status);
        Assert.Equal(before, w.Stub.Requests.Count);
    }

    // B2e-6: 5 points standing on an own planet heal 1 a day (5 days); with Talent 80, 2, 2, 1 (3 days); travelling, on an enemy planet or captured: none
    [Theory]
    [InlineData("own", 5)]
    [InlineData("fast", 3)]
    [InlineData("travelling", -1)]
    [InlineData("enemy", -1)]
    [InlineData("captured", -1)]
    public void B2e6_Healing(string where, int days)
    {
        var w = new CharWorld();
        w.Hold(Shoal, Side.Collective); w.Hold(Thaw, Side.Hand);
        var midwife = w.Add(Midwife, where == "enemy" ? Thaw : Shoal);
        if (where == "fast") Talent(midwife, 80);
        if (where == "travelling") Assert.Null(w.Try(seq => new MoveCharacterOrder(seq, Side.Collective, Midwife, Charter)));
        w.StepWith("5.2", ctx =>
        {
            w.Ch.AddInjury(ctx, midwife, 5, CharacterCause.Debug);
            if (where == "captured") w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug, canEscape: false);
        });
        int before = w.Stub!.Requests.Count;
        for (int d = 1; d <= 6; d++)
        {
            w.Game.Step();
            if (days > 0 && d < days) Assert.True(midwife.Injury > 0, $"day {d}: {midwife.Injury}");
            if (days > 0 && d == days) { Assert.Equal(0, midwife.Injury); Assert.Equal(CharacterStatus.Free, midwife.Status); }
            if (where == "fast" && d == 1) Assert.Equal(3, midwife.Injury);
        }
        if (days < 0) Assert.Equal(5, midwife.Injury);
        Assert.Equal(before, w.Stub.Requests.Count); // B2e-19 in passing: the healing timer has spread 0 and draws nothing
    }

    // B2e-7: an injured Admiral loses his post (and keeps riding); an injured mission member whose record can resign leaves the mission
    [Fact]
    public void B2e7_InjuryEndsThePostAndTheMission()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Collective, Shoal);
        w.Add(Keth, Shoal); w.Ch.SetPost(Keth, f, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 99); // Keth is a minor: his death draw misses
        w.StepWith("5.2", ctx => w.Ch.AddInjury(ctx, w.C(Keth), 5, CharacterCause.Debug));
        Assert.Equal((f, CharacterPost.None), (w.C(Keth).AttachedFleet, w.C(Keth).Post));
        Assert.Equal(PostKind.Admiral, w.Events<PostVacated>().Single().Post);

        var m = new MissionWorld();
        m.Hold(MissionWorld.T, Side.Hand, 0);
        var ledger = m.Agent(Ledger, MissionWorld.H, esp: 50);
        m.Agent(Midwife, MissionWorld.H, esp: 50);
        m.Start(Side.Collective, MissionKind.Espionage, new[] { Ledger, Midwife }, MissionWorld.T);
        m.Game.Step();
        int id = m.LastMission;
        Assert.True(m.Ch.IsOnMission(Ledger));
        bool done = false;
        m.Game.RegisterHook("5.2", ctx => { if (!done) { done = true; m.Ch.AddInjury(ctx, ledger, 5, CharacterCause.Debug); } });
        m.Game.Step();
        Assert.False(m.Ch.IsOnMission(Ledger)); // Espionage can resign (MISSNSD word 15)
        Assert.True(m.Ch.IsOnMission(Midwife));
        Assert.NotNull(ledger.System);
        Assert.Equal(id, m.LastMission);
    }

    // B2e-8: a capture on day D with delay draw 400: the first attempt on day D + 500; the next delay is drawn right after the attempt
    [Fact]
    public void B2e8_EscapeTimer()
    {
        var w = new CharWorld();
        w.Hold(Thaw, Side.Hand);
        // (B5a, 2026-10-17) under derived control an unguarded world at Collective support 50 is neutral: the first support change there (day 31)
        // would turn The Thaw neutral and free Midwife (Liberation). At Collective support 0 the Hand holds it by support for the whole test.
        w.Host.Economy.SetCollectiveSupport(Thaw, 0);
        var midwife = Skills(w.Add(Midwife, Thaw), esp: 0, com: 0);
        w.Stub!.Expect(0, 1000, 400);
        int d = w.Game.Day;
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug));
        Assert.Equal(d + 500, midwife.NextEscapeDay);
        while (w.Game.Day < d + 500) w.Game.Step();
        Assert.Empty(w.Events<EscapeAttempted>());
        w.Stub.Expect(0, 99, 99).Expect(0, 1000, 7);
        int from = w.Stub.Requests.Count;
        w.Game.Step(); // the Step of day D + 500
        var a = w.Events<EscapeAttempted>().Single();
        Assert.False(a.Escaped);
        Assert.Equal(new[] { (0, 99), (0, 1000) }, w.Stub.Requests.Skip(from));
        Assert.Equal(d + 500 + 107, midwife.NextEscapeDay);
    }

    // B2e-9: Midwife (100, 100) alone: x = 200 -> 30 (29 escapes, 30 not); with Ravilla (220) and 2 Hand special forces: x = -22 -> 3; Ledger (50, 30) there: x = -142 -> 1
    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public void B2e9_EscapeChance(int draw, bool escapes)
    {
        var w = new CharWorld();
        w.Hold(Thaw, Side.Hand);
        var midwife = Skills(w.Add(Midwife, Thaw), esp: 100, com: 100);
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug));
        midwife.NextEscapeDay = w.Game.Day;
        w.Stub!.Expect(0, 99, draw);
        w.Game.Step();
        var a = w.Events<EscapeAttempted>().Single();
        Assert.Equal((0, 0, 200, 30, escapes), (a.CaptorAverageCombat, a.CaptorSpecialForces, a.X, a.Chance, a.Escaped));
        Assert.Equal(escapes ? CharacterStatus.Free : CharacterStatus.Captured, midwife.Status);
        if (escapes) Assert.Single(w.Events<CharacterEscaped>());
    }

    [Fact]
    public void B2e9_EscapeChanceWithGuards()
    {
        var w = new CharWorld(CharWorld.ClassicTalent()); // the Classic Talent (m5-characters-and-force §Talent CH37)
        w.Hold(Thaw, Side.Hand);
        Talent(Skills(w.Add(Ravilla, Thaw), com: 100), 120);
        w.Host.Economy.AddObject(Side.Hand, HandCommandos, Thaw, true);
        w.Host.Economy.AddObject(Side.Hand, HandCommandos, Thaw, true);
        var midwife = Skills(w.Add(Midwife, Thaw), esp: 100, com: 100);
        var ledger = Skills(w.Add(Ledger, Thaw), esp: 30, com: 50);
        w.StepWith("5.2", ctx => { w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug); w.Ch.Capture(ctx, ledger, Side.Hand, null, CharacterCause.Debug); });
        midwife.NextEscapeDay = ledger.NextEscapeDay = w.Game.Day;
        w.Stub!.Expect(0, 99, 99, 99);
        w.Game.Step();
        Assert.Equal(new[] { (Ledger, 220, 2, -142, 1), (Midwife, 220, 2, -22, 3) },
            w.Events<EscapeAttempted>().OrderBy(e => e.Character).Select(e => (e.Character, e.CaptorAverageCombat, e.CaptorSpecialForces, e.X, e.Chance)));
    }

    // B2e-10: a Hand character aboard a fleet in orbit and a second prisoner on the planet do not count in avg
    [Fact]
    public void B2e10_RidersAndPrisonersDoNotGuard()
    {
        var w = new CharWorld();
        w.Hold(Thaw, Side.Hand);
        int f = w.Fleet(Side.Hand, Thaw, Mandate);
        Skills(w.Add(Varo, Thaw), com: 80); w.Ch.PlaceInFleet(Varo, f);
        var midwife = Skills(w.Add(Midwife, Thaw), esp: 10, com: 10);
        var fortyOne = Skills(w.Add(FortyOne, Thaw), com: 90);
        w.StepWith("5.2", ctx => { w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug); w.Ch.Capture(ctx, fortyOne, Side.Hand, null, CharacterCause.Debug); });
        midwife.NextEscapeDay = w.Game.Day; fortyOne.NextEscapeDay = 0;
        w.Stub!.Expect(0, 99, 99);
        w.Game.Step();
        Assert.Equal((0, 20), w.Events<EscapeAttempted>().Select(e => (e.CaptorAverageCombat, e.X)).Single());
    }

    // B2e-11: Midwife captured by the Palace story: no escape timer, no draw ever
    [Fact]
    public void B2e11_StoryPrisonersNeverTry()
    {
        var w = new CharWorld();
        var midwife = w.Add(Midwife, Thaw);
        int before = w.Stub!.Requests.Count;
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, midwife, Side.Hand, Thaw, CharacterCause.Story));
        w.Run(1200);
        Assert.Equal(0, midwife.NextEscapeDay);
        Assert.Equal(before, w.Stub.Requests.Count);
        Assert.Empty(w.Events<EscapeAttempted>());
    }

    // B2e-12: the Collective retakes the planet holding Midwife: freed; the planet turns neutral: freed; a battle that changes nothing: nothing; no draw
    [Theory]
    [InlineData("retaken")]
    [InlineData("neutral")]
    [InlineData("nothing")]
    public void B2e12_Liberation(string how)
    {
        var w = new CharWorld();
        w.Hold(Thaw, Side.Hand);
        var midwife = w.Add(Midwife, Thaw);
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug));
        int before = w.Stub!.Requests.Count;
        w.StepWith("5.2", ctx =>
        {
            if (how == "retaken") { w.Hold(Thaw, Side.Collective); w.Ch.OnHolderChanged(new CaptureInfo(ctx, Thaw, Side.Hand, Side.Collective, CaptureCause.Debug)); }
            else if (how == "neutral") { w.Host.Fleets.SetOwner(Thaw, null); w.Ch.Liberate(ctx, Thaw); }
            else w.Ch.Liberate(ctx, Thaw);
        });
        bool freed = how != "nothing";
        Assert.Equal(freed ? CharacterStatus.Free : CharacterStatus.Captured, midwife.Status);
        if (freed) Assert.Equal(CharacterCause.Liberation, w.Events<CharacterFreed>().Single().Cause);
        // the only draw is the support incident timer a Collective-held planet starts (GNPRTB 7702, m2-popular-support), never a character draw
        int spread = new Praedra.Core.Support.SupportParams().IncidentIntervalSpread;
        Assert.Empty(w.Stub.Requests.Skip(before).Where(r => r != (0, spread)));
    }

    // B2e-13: Nobody (combat 135, Talent 50) foiled with a mutual discovery of Ravilla (220, 120): duel x = -85 -> 15; the hurt rolls in order; Nobody learns
    // his heritage; free, so one hand draw (1..100) and Talent +17; both keys cleared
    [Fact]
    public void B2e13_NobodyMeetsRavilla()
    {
        var w = new MissionWorld(characters: CharWorld.ClassicTalent()); // the Classic Talent (m5-characters-and-force §Talent CH37)
        w.Hold(MissionWorld.T, Side.Hand, 0);
        var nobody = w.Agent(Nobody, MissionWorld.H, esp: 50, com: 90);
        var ravilla = w.Agent(Ravilla, MissionWorld.T, com: 100);
        Assert.Equal((50, 120), (nobody.EffectiveTalent, ravilla.EffectiveTalent));
        nobody.DiscoveredTalentHolder = Ravilla; ravilla.DiscoveredTalentHolder = Nobody; nobody.PairId = ravilla.PairId = 1;
        w.Start(Side.Collective, MissionKind.Espionage, new[] { Nobody }, MissionWorld.T);
        w.Game.Step();
        w.RunUntilWorking(w.LastMission);
        // Ravilla senses Nobody (p = 70: 0 hits; he has a discovered user, so one try), foiled; then the encounter: duel 0 escapes, Nobody's Hurt (p = 1)
        // 99, Nobody's HurtBy (p = 85) 99, Ravilla's HurtBy (p = 1) 99, the hand draw 39 (40 points)
        w.Stub!.Expect(0, 99, 0, 0, 99, 99, 99, 39);
        int from = w.Stub.Requests.Count;
        w.RunIteration(w.LastMission);
        var d = w.Events<MissionDuel>().Single();
        Assert.Equal((Nobody, Ravilla, -85, 15, true), (d.Member, d.Guard, d.X, d.Chance, d.Escaped));
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99), (0, 99), (0, 99), (0, 99) }, w.RequestsSince(from).Where(r => r == (0, 99)).Take(6));
        Assert.True(nobody.KnowsHeritage);
        Assert.Equal(40, nobody.Injury);
        Assert.Equal(67, nobody.EffectiveTalent);
        Assert.Equal((0, 0, 0, 0), (nobody.DiscoveredTalentHolder, ravilla.DiscoveredTalentHolder, nobody.PairId, ravilla.PairId));
        var e = w.Events<TalentEncounterResolved>().Single();
        Assert.Equal((Nobody, Ravilla, true, false, false), (e.Member, e.Other, e.MemberEscaped, e.MemberHurt, e.OtherHurt));
        Assert.Equal(17, w.Events<TalentGained>().Single().Gain);
    }

    // B2e-14: Ledger (aware, Talent 10) meets The Chair (150): Ledger +28 Talent if free
    [Fact]
    public void B2e14_LedgerMeetsTheChair()
    {
        var w = new CharWorld();
        var ledger = Talent(Skills(w.Add(Ledger, Shoal), com: 50), 10);
        var emp = Talent(Skills(w.Add(Chair, Charter), com: 50), 150);
        w.Stub!.Expect(0, 99, 99, 99); // both HurtBy rolls miss
        w.StepWith("5.3", ctx => w.Ch.TalentEncounter(ctx, ctx.Day + 1, 0, ledger, emp, mHurtInDuel: false, mEscaped: true));
        Assert.Equal(38, ledger.EffectiveTalent);
        Assert.Equal(150, emp.EffectiveTalent);
    }

    // B2e-15: two minors with a mutual discovery: hurt rolls, base combat +1 to whoever hurt the other; no Talent change
    [Fact]
    public void B2e15_TwoMinors()
    {
        var w = new CharWorld();
        var a = Talent(Skills(w.Add(FortyOne, Shoal), com: 40), 30);
        var b = Talent(Skills(w.Add(Varo, Thaw), com: 60), 70);
        // a's HurtBy (p = 20): 0 hits, sizes 0, 0; a's death draw 99; b's HurtBy (p = 1): 0 hits, sizes 0, 0; b's death draw 99
        w.Stub!.Expect(0, 99, 0, 99, 0, 99);
        w.StepWith("5.3", ctx => w.Ch.TalentEncounter(ctx, ctx.Day + 1, 0, a, b, mHurtInDuel: false, mEscaped: true));
        Assert.Equal((1, 1), (a.Injury, b.Injury));
        Assert.Equal((41, 61), (a.BaseSkill(Skill.Combat), b.BaseSkill(Skill.Combat)));
        Assert.Equal((30, 70), (a.EffectiveTalent, b.EffectiveTalent));
        Assert.Empty(w.Events<TalentGained>());
    }

    // B2e-16: a mutual discovery with different pair ids: nothing, no draw (the member then duels a guard as in B2d)
    [Fact]
    public void B2e16_PairIdsMustMatch()
    {
        var w = new CharWorld();
        var nobody = w.Add(Nobody, Shoal); var ravilla = w.Add(Ravilla, Charter);
        nobody.DiscoveredTalentHolder = Ravilla; ravilla.DiscoveredTalentHolder = Nobody; nobody.PairId = 1; ravilla.PairId = 2;
        Assert.False(w.Ch.MutualDiscovery(nobody, out _));
        ravilla.PairId = 1;
        Assert.True(w.Ch.MutualDiscovery(nobody, out var x));
        Assert.Equal(Ravilla, x!.Id);
        nobody.PairId = ravilla.PairId = 0;
        Assert.False(w.Ch.MutualDiscovery(nobody, out _));
        Assert.Empty(w.Stub!.Requests);
    }

    // B2e-17: Ravilla awakens an unaware Hand talented character on his planet the next day, Talent rolled then; Nobody at 50 awakens nobody; at 80 he awakens
    // Collective minors (level 10 + rand(0..40)); Ledger only after Nobody knows his heritage
    [Fact]
    public void B2e17_Awakening()
    {
        var w = new CharWorld(new CharacterParams { TrainerPick = TrainerPick.Fixed }); // the Classic trainer flag (Nobody, Ravilla); Random is CH39
        w.Hold(Shoal, Side.Collective); w.Hold(Charter, Side.Hand);
        Talent(w.Add(Ravilla, Charter), 120);
        var varo = w.Add(Varo, Charter); varo.TalentHolder = true; // an unaware talented character
        var nobody = Talent(w.Add(Nobody, Shoal), 50);
        var fortyOne = w.Add(FortyOne, Shoal); var ledger = w.Add(Ledger, Shoal);
        Assert.True(fortyOne.TalentHolder && ledger.TalentHolder && !fortyOne.TalentDiscovered && !ledger.TalentDiscovered);
        w.Game.Step(); // the timers turn on
        Assert.False(varo.TalentDiscovered);
        w.Game.Step();
        Assert.True(varo.TalentDiscovered);
        Assert.Equal((Ravilla, 10), (varo.AwakenedBy, varo.EffectiveTalent)); // his record: tlvl 10 + rand(0..40), the stub answers 0
        Assert.False(fortyOne.TalentDiscovered); // Nobody at 50 is no trainer

        nobody.TalentExperience = 80;
        w.Stub!.Expect(0, 40, 33);
        w.Run(2);
        Assert.True(fortyOne.TalentDiscovered);
        Assert.Equal((Nobody, 43), (fortyOne.AwakenedBy, fortyOne.EffectiveTalent));
        Assert.False(ledger.TalentDiscovered); // Nobody does not know his heritage
        nobody.KnowsHeritage = true;
        w.Game.Step();
        Assert.True(ledger.TalentDiscovered && ledger.KnowsHeritage);
        Assert.Equal(10, ledger.EffectiveTalent);
    }

    // B2e-18: The Chair on Charter, the Hand holds it: Hand base leadership 100 -> 150, Collective unchanged; once he is captured, 100
    [Fact]
    public void B2e18_SeatOfPower()
    {
        var w = new CharWorld();
        w.Hold(Charter, Side.Hand);
        var varo = Skills(w.Add(Varo, Thaw), lead: 100);
        var keth = Skills(w.Add(Keth, Shoal), lead: 100);
        var emp = w.Add(Chair, Charter);
        w.Game.Step();
        Assert.True(w.Ch.SeatOfPowerOn);
        Assert.Equal((150, 100), (w.Ch.EffectiveLeadership(varo), w.Ch.EffectiveLeadership(keth)));
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, emp, Side.Collective, null, CharacterCause.Debug));
        Assert.False(w.Ch.SeatOfPowerOn);
        Assert.Equal((100, 100), (w.Ch.EffectiveLeadership(varo), w.Ch.EffectiveLeadership(keth)));
    }

    // B2e-19: a timer with spread 0 makes no draw (the awakening timer of Ravilla runs every day)
    [Fact]
    public void B2e19_SpreadZeroTimersDrawNothing()
    {
        var w = new CharWorld(new CharacterParams { TrainerPick = TrainerPick.Fixed }); // the Classic trainer flag (Nobody, Ravilla); Random is CH39
        w.Hold(Charter, Side.Hand);
        Talent(w.Add(Ravilla, Charter), 120);
        w.Run(30);
        var bare = new CharWorld(); // the same galaxy without Ravilla: its other timers (support incidents, m2-popular-support) draw the same
        bare.Hold(Charter, Side.Hand);
        bare.Run(30);
        Assert.Equal(bare.Stub!.Requests, w.Stub!.Requests);
        Assert.NotEqual(0, w.C(Ravilla).NextAwakenDay);
    }

    // Work 1, 2, 4: injury points, escape timers and Talent fields are saved and hashed
    [Fact]
    public void InjuryAndTimersAreSavedAndHashed()
    {
        var w = new CharWorld();
        w.Hold(Thaw, Side.Hand);
        var midwife = w.Add(Midwife, Thaw);
        w.StepWith("5.2", ctx => { w.Ch.AddInjury(ctx, midwife, 7, CharacterCause.Debug); w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug); });
        midwife.PairId = 3; midwife.KnowsHeritage = true; w.Game.State.Characters.NextPairId = 3;
        string hash = GameSave.StateHash(w.Game);
        w.Reload();
        var back = w.C(Midwife);
        Assert.Equal((7, midwife.NextEscapeDay, 3, true, 3), (back.Injury, back.NextEscapeDay, back.PairId, back.KnowsHeritage, w.Game.State.Characters.NextPairId));
        Assert.Equal(hash, GameSave.StateHash(w.Game));
        back.Injury = 8;
        Assert.NotEqual(hash, GameSave.StateHash(w.Game));
    }
}
