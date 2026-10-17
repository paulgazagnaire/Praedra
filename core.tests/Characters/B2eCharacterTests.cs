using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Missions;
using Rebellion.Core.Setup;
using Rebellion.Core.Tests.Economy;
using Rebellion.Core.Tests.Missions;
using static Rebellion.Core.Tests.Characters.CharWorld;

namespace Rebellion.Core.Tests.Characters;

// B2e-escape-injury-and-the-force tests B2e-1 to B2e-19. rule: m5-characters-and-force §Capture, injury, death, freeing; §Escape; §Force;
// §Seat of Power; m5-missions §4 (the Force-user encounter).
public class B2eCharacterTests
{
    private const string ImperialCommandos = "Imperial Commandos";

    private static CharacterRecord Skills(CharacterRecord c, int dip = 0, int esp = 0, int com = 0, int lead = 0, int ship = 0)
    {
        c.Skills[(int)Skill.Diplomacy] = dip; c.Skills[(int)Skill.Espionage] = esp; c.Skills[(int)Skill.Combat] = com;
        c.Skills[(int)Skill.Leadership] = lead; c.Skills[(int)Skill.ShipDesign] = ship;
        return c;
    }

    private static CharacterRecord Force(CharacterRecord c, int f)
    {
        c.ForceUser = f > 0; c.ForceDiscovered = f > 0; c.ForceExperience = f; c.ForceTraining = 0;
        return c;
    }

    // B2e-1: Luke, base diplomacy 50, espionage 50, combat 90, ship design 0, Force 50: effective 75, 75, 135, 0; with 40 injury points combat 95
    [Fact]
    public void B2e1_EffectiveSkillsOfLuke()
    {
        var w = new CharWorld();
        var luke = Force(Skills(w.Add(Luke, Yavin), dip: 50, esp: 50, com: 90, ship: 0), 50);
        Assert.Equal((75, 75, 135, 0), (CharacterModule.EffectiveSkill(luke, Skill.Diplomacy), luke.EffectiveSkill(Skill.Espionage),
            luke.EffectiveSkill(Skill.Combat), luke.EffectiveSkill(Skill.ShipDesign)));
        luke.Injury = 40;
        Assert.Equal(95, luke.EffectiveSkill(Skill.Combat));
        Assert.Equal(75, luke.EffectiveSkill(Skill.Diplomacy)); // the injury comes off combat only
    }

    // B2e-2: Vader Force 120, base combat 100: 220; a character with Force 0 keeps his base skills
    [Fact]
    public void B2e2_VaderAndAForcelessCharacter()
    {
        var w = new CharWorld();
        var vader = Force(Skills(w.Add(Vader, Coruscant), com: 100, esp: 50), 120);
        Assert.Equal((220, 110), (vader.EffectiveSkill(Skill.Combat), vader.EffectiveSkill(Skill.Espionage)));
        var han = Skills(w.Add(Han, Yavin), dip: 30, esp: 70, com: 100, lead: 90, ship: 10);
        Assert.Equal(0, han.EffectiveForce);
        foreach (Skill s in Enum.GetValues<Skill>()) Assert.Equal(han.BaseSkill(s), han.EffectiveSkill(s));
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
        var wedge = w.Add(Wedge, Yavin);
        w.Stub!.Expect(0, 99, draw);
        int before = w.Stub.Requests.Count;
        w.StepWith("5.2", ctx => w.Ch.AddInjury(ctx, wedge, 10, CharacterCause.Mission));
        Assert.Equal(dies ? CharacterStatus.Dead : CharacterStatus.Injured, wedge.Status);
        if (dies) Assert.Equal(CharacterCause.Mission, w.Events<CharacterKilled>().Single().Cause);
        Assert.Equal(1, w.Stub.Requests.Skip(before).Count(r => r == (0, 99)));

        var luke = w.Add(Luke, Yavin);
        before = w.Stub.Requests.Count;
        w.StepWith("5.2", ctx => w.Ch.AddInjury(ctx, luke, 10, CharacterCause.Mission));
        Assert.Equal(CharacterStatus.Injured, luke.Status);
        Assert.Equal(before, w.Stub.Requests.Count);
    }

    // B2e-6: 5 points standing on an own planet heal 1 a day (5 days); with Force 80, 2, 2, 1 (3 days); travelling, on an enemy planet or captured: none
    [Theory]
    [InlineData("own", 5)]
    [InlineData("fast", 3)]
    [InlineData("travelling", -1)]
    [InlineData("enemy", -1)]
    [InlineData("captured", -1)]
    public void B2e6_Healing(string where, int days)
    {
        var w = new CharWorld();
        w.Hold(Yavin, Side.Alliance); w.Hold(Hoth, Side.Empire);
        var han = w.Add(Han, where == "enemy" ? Hoth : Yavin);
        if (where == "fast") Force(han, 80);
        if (where == "travelling") Assert.Null(w.Try(seq => new MoveCharacterOrder(seq, Side.Alliance, Han, Coruscant)));
        w.StepWith("5.2", ctx =>
        {
            w.Ch.AddInjury(ctx, han, 5, CharacterCause.Debug);
            if (where == "captured") w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug, canEscape: false);
        });
        int before = w.Stub!.Requests.Count;
        for (int d = 1; d <= 6; d++)
        {
            w.Game.Step();
            if (days > 0 && d < days) Assert.True(han.Injury > 0, $"day {d}: {han.Injury}");
            if (days > 0 && d == days) { Assert.Equal(0, han.Injury); Assert.Equal(CharacterStatus.Free, han.Status); }
            if (where == "fast" && d == 1) Assert.Equal(3, han.Injury);
        }
        if (days < 0) Assert.Equal(5, han.Injury);
        Assert.Equal(before, w.Stub.Requests.Count); // B2e-19 in passing: the healing timer has spread 0 and draws nothing
    }

    // B2e-7: an injured Admiral loses his post (and keeps riding); an injured mission member whose record can resign leaves the mission
    [Fact]
    public void B2e7_InjuryEndsThePostAndTheMission()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Alliance, Yavin);
        w.Add(Ackbar, Yavin); w.Ch.SetPost(Ackbar, f, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 99); // Ackbar is a minor: his death draw misses
        w.StepWith("5.2", ctx => w.Ch.AddInjury(ctx, w.C(Ackbar), 5, CharacterCause.Debug));
        Assert.Equal((f, CharacterPost.None), (w.C(Ackbar).AttachedFleet, w.C(Ackbar).Post));
        Assert.Equal(PostKind.Admiral, w.Events<PostVacated>().Single().Post);

        var m = new MissionWorld();
        m.Hold(MissionWorld.T, Side.Empire, 0);
        var leia = m.Agent(Leia, MissionWorld.H, esp: 50);
        m.Agent(Han, MissionWorld.H, esp: 50);
        m.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia, Han }, MissionWorld.T);
        m.Game.Step();
        int id = m.LastMission;
        Assert.True(m.Ch.IsOnMission(Leia));
        bool done = false;
        m.Game.RegisterHook("5.2", ctx => { if (!done) { done = true; m.Ch.AddInjury(ctx, leia, 5, CharacterCause.Debug); } });
        m.Game.Step();
        Assert.False(m.Ch.IsOnMission(Leia)); // Espionage can resign (MISSNSD word 15)
        Assert.True(m.Ch.IsOnMission(Han));
        Assert.NotNull(leia.System);
        Assert.Equal(id, m.LastMission);
    }

    // B2e-8: a capture on day D with delay draw 400: the first attempt on day D + 500; the next delay is drawn right after the attempt
    [Fact]
    public void B2e8_EscapeTimer()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire);
        var han = Skills(w.Add(Han, Hoth), esp: 0, com: 0);
        w.Stub!.Expect(0, 1000, 400);
        int d = w.Game.Day;
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug));
        Assert.Equal(d + 500, han.NextEscapeDay);
        while (w.Game.Day < d + 500) w.Game.Step();
        Assert.Empty(w.Events<EscapeAttempted>());
        w.Stub.Expect(0, 99, 99).Expect(0, 1000, 7);
        int from = w.Stub.Requests.Count;
        w.Game.Step(); // the Step of day D + 500
        var a = w.Events<EscapeAttempted>().Single();
        Assert.False(a.Escaped);
        Assert.Equal(new[] { (0, 99), (0, 1000) }, w.Stub.Requests.Skip(from));
        Assert.Equal(d + 500 + 107, han.NextEscapeDay);
    }

    // B2e-9: Han (100, 100) alone: x = 200 -> 30 (29 escapes, 30 not); with Vader (220) and 2 Imperial special forces: x = -22 -> 3; Leia (50, 30) there: x = -142 -> 1
    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public void B2e9_EscapeChance(int draw, bool escapes)
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire);
        var han = Skills(w.Add(Han, Hoth), esp: 100, com: 100);
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug));
        han.NextEscapeDay = w.Game.Day;
        w.Stub!.Expect(0, 99, draw);
        w.Game.Step();
        var a = w.Events<EscapeAttempted>().Single();
        Assert.Equal((0, 0, 200, 30, escapes), (a.CaptorAverageCombat, a.CaptorSpecialForces, a.X, a.Chance, a.Escaped));
        Assert.Equal(escapes ? CharacterStatus.Free : CharacterStatus.Captured, han.Status);
        if (escapes) Assert.Single(w.Events<CharacterEscaped>());
    }

    [Fact]
    public void B2e9_EscapeChanceWithGuards()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire);
        Force(Skills(w.Add(Vader, Hoth), com: 100), 120);
        w.Host.Economy.AddObject(Side.Empire, ImperialCommandos, Hoth, true);
        w.Host.Economy.AddObject(Side.Empire, ImperialCommandos, Hoth, true);
        var han = Skills(w.Add(Han, Hoth), esp: 100, com: 100);
        var leia = Skills(w.Add(Leia, Hoth), esp: 30, com: 50);
        w.StepWith("5.2", ctx => { w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug); w.Ch.Capture(ctx, leia, Side.Empire, null, CharacterCause.Debug); });
        han.NextEscapeDay = leia.NextEscapeDay = w.Game.Day;
        w.Stub!.Expect(0, 99, 99, 99);
        w.Game.Step();
        Assert.Equal(new[] { (Leia, 220, 2, -142, 1), (Han, 220, 2, -22, 3) },
            w.Events<EscapeAttempted>().OrderBy(e => e.Character).Select(e => (e.Character, e.CaptorAverageCombat, e.CaptorSpecialForces, e.X, e.Chance)));
    }

    // B2e-10: an Imperial character aboard a fleet in orbit and a second prisoner on the planet do not count in avg
    [Fact]
    public void B2e10_RidersAndPrisonersDoNotGuard()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire);
        int f = w.Fleet(Side.Empire, Hoth, Isd);
        Skills(w.Add(Piett, Hoth), com: 80); w.Ch.PlaceInFleet(Piett, f);
        var han = Skills(w.Add(Han, Hoth), esp: 10, com: 10);
        var wedge = Skills(w.Add(Wedge, Hoth), com: 90);
        w.StepWith("5.2", ctx => { w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug); w.Ch.Capture(ctx, wedge, Side.Empire, null, CharacterCause.Debug); });
        han.NextEscapeDay = w.Game.Day; wedge.NextEscapeDay = 0;
        w.Stub!.Expect(0, 99, 99);
        w.Game.Step();
        Assert.Equal((0, 20), w.Events<EscapeAttempted>().Select(e => (e.CaptorAverageCombat, e.X)).Single());
    }

    // B2e-11: Han captured by the Palace story: no escape timer, no draw ever
    [Fact]
    public void B2e11_StoryPrisonersNeverTry()
    {
        var w = new CharWorld();
        var han = w.Add(Han, Hoth);
        int before = w.Stub!.Requests.Count;
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, han, Side.Empire, Hoth, CharacterCause.Story));
        w.Run(1200);
        Assert.Equal(0, han.NextEscapeDay);
        Assert.Equal(before, w.Stub.Requests.Count);
        Assert.Empty(w.Events<EscapeAttempted>());
    }

    // B2e-12: the Alliance retakes the planet holding Han: freed; the planet turns neutral: freed; a battle that changes nothing: nothing; no draw
    [Theory]
    [InlineData("retaken")]
    [InlineData("neutral")]
    [InlineData("nothing")]
    public void B2e12_Liberation(string how)
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire);
        var han = w.Add(Han, Hoth);
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug));
        int before = w.Stub!.Requests.Count;
        w.StepWith("5.2", ctx =>
        {
            if (how == "retaken") { w.Hold(Hoth, Side.Alliance); w.Ch.OnHolderChanged(new CaptureInfo(ctx, Hoth, Side.Empire, Side.Alliance, CaptureCause.Debug)); }
            else if (how == "neutral") { w.Host.Fleets.SetOwner(Hoth, null); w.Ch.Liberate(ctx, Hoth); }
            else w.Ch.Liberate(ctx, Hoth);
        });
        bool freed = how != "nothing";
        Assert.Equal(freed ? CharacterStatus.Free : CharacterStatus.Captured, han.Status);
        if (freed) Assert.Equal(CharacterCause.Liberation, w.Events<CharacterFreed>().Single().Cause);
        // the only draw is the support incident timer an Alliance-held planet starts (GNPRTB 7702, m2-popular-support), never a character draw
        int spread = new Rebellion.Core.Support.SupportParams().IncidentIntervalSpread;
        Assert.Empty(w.Stub.Requests.Skip(before).Where(r => r != (0, spread)));
    }

    // B2e-13: Luke (combat 135, Force 50) foiled with a mutual discovery of Vader (220, 120): duel x = -85 -> 15; the hurt rolls in order; Luke learns
    // his heritage; free, so one hand draw (1..100) and Force +17; both keys cleared
    [Fact]
    public void B2e13_LukeMeetsVader()
    {
        var w = new MissionWorld();
        w.Hold(MissionWorld.T, Side.Empire, 0);
        var luke = w.Agent(Luke, MissionWorld.H, esp: 50, com: 90);
        var vader = w.Agent(Vader, MissionWorld.T, com: 100);
        Assert.Equal((50, 120), (luke.EffectiveForce, vader.EffectiveForce));
        luke.DiscoveredForceUser = Vader; vader.DiscoveredForceUser = Luke; luke.PairId = vader.PairId = 1;
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Luke }, MissionWorld.T);
        w.Game.Step();
        w.RunUntilWorking(w.LastMission);
        // Vader senses Luke (p = 70: 0 hits; he has a discovered user, so one try), foiled; then the encounter: duel 0 escapes, Luke's Hurt (p = 1)
        // 99, Luke's HurtBy (p = 85) 99, Vader's HurtBy (p = 1) 99, the hand draw 39 (40 points)
        w.Stub!.Expect(0, 99, 0, 0, 99, 99, 99, 39);
        int from = w.Stub.Requests.Count;
        w.RunIteration(w.LastMission);
        var d = w.Events<MissionDuel>().Single();
        Assert.Equal((Luke, Vader, -85, 15, true), (d.Member, d.Guard, d.X, d.Chance, d.Escaped));
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99), (0, 99), (0, 99), (0, 99) }, w.RequestsSince(from).Where(r => r == (0, 99)).Take(6));
        Assert.True(luke.KnowsHeritage);
        Assert.Equal(40, luke.Injury);
        Assert.Equal(67, luke.EffectiveForce);
        Assert.Equal((0, 0, 0, 0), (luke.DiscoveredForceUser, vader.DiscoveredForceUser, luke.PairId, vader.PairId));
        var e = w.Events<ForceEncounterResolved>().Single();
        Assert.Equal((Luke, Vader, true, false, false), (e.Member, e.Other, e.MemberEscaped, e.MemberHurt, e.OtherHurt));
        Assert.Equal(17, w.Events<ForceGained>().Single().Gain);
    }

    // B2e-14: Leia (aware, Force 10) meets the Emperor (150): Leia +28 Force if free
    [Fact]
    public void B2e14_LeiaMeetsTheEmperor()
    {
        var w = new CharWorld();
        var leia = Force(Skills(w.Add(Leia, Yavin), com: 50), 10);
        var emp = Force(Skills(w.Add(Palpatine, Coruscant), com: 50), 150);
        w.Stub!.Expect(0, 99, 99, 99); // both HurtBy rolls miss
        w.StepWith("5.3", ctx => w.Ch.ForceEncounter(ctx, ctx.Day + 1, 0, leia, emp, mHurtInDuel: false, mEscaped: true));
        Assert.Equal(38, leia.EffectiveForce);
        Assert.Equal(150, emp.EffectiveForce);
    }

    // B2e-15: two minors with a mutual discovery: hurt rolls, base combat +1 to whoever hurt the other; no Force change
    [Fact]
    public void B2e15_TwoMinors()
    {
        var w = new CharWorld();
        var a = Force(Skills(w.Add(Wedge, Yavin), com: 40), 30);
        var b = Force(Skills(w.Add(Piett, Hoth), com: 60), 70);
        // a's HurtBy (p = 20): 0 hits, sizes 0, 0; a's death draw 99; b's HurtBy (p = 1): 0 hits, sizes 0, 0; b's death draw 99
        w.Stub!.Expect(0, 99, 0, 99, 0, 99);
        w.StepWith("5.3", ctx => w.Ch.ForceEncounter(ctx, ctx.Day + 1, 0, a, b, mHurtInDuel: false, mEscaped: true));
        Assert.Equal((1, 1), (a.Injury, b.Injury));
        Assert.Equal((41, 61), (a.BaseSkill(Skill.Combat), b.BaseSkill(Skill.Combat)));
        Assert.Equal((30, 70), (a.EffectiveForce, b.EffectiveForce));
        Assert.Empty(w.Events<ForceGained>());
    }

    // B2e-16: a mutual discovery with different pair ids: nothing, no draw (the member then duels a guard as in B2d)
    [Fact]
    public void B2e16_PairIdsMustMatch()
    {
        var w = new CharWorld();
        var luke = w.Add(Luke, Yavin); var vader = w.Add(Vader, Coruscant);
        luke.DiscoveredForceUser = Vader; vader.DiscoveredForceUser = Luke; luke.PairId = 1; vader.PairId = 2;
        Assert.False(w.Ch.MutualDiscovery(luke, out _));
        vader.PairId = 1;
        Assert.True(w.Ch.MutualDiscovery(luke, out var x));
        Assert.Equal(Vader, x!.Id);
        luke.PairId = vader.PairId = 0;
        Assert.False(w.Ch.MutualDiscovery(luke, out _));
        Assert.Empty(w.Stub!.Requests);
    }

    // B2e-17: Vader awakens an unaware Imperial Force user on his planet the next day, Force rolled then; Luke at 50 awakens nobody; at 80 he awakens
    // Alliance minors (level 10 + rand(0..40)); Leia only after Luke knows his heritage
    [Fact]
    public void B2e17_Awakening()
    {
        var w = new CharWorld();
        w.Hold(Yavin, Side.Alliance); w.Hold(Coruscant, Side.Empire);
        Force(w.Add(Vader, Coruscant), 120);
        var piett = w.Add(Piett, Coruscant); piett.ForceUser = true; // an unaware Force user
        var luke = Force(w.Add(Luke, Yavin), 50);
        var wedge = w.Add(Wedge, Yavin); var leia = w.Add(Leia, Yavin);
        Assert.True(wedge.ForceUser && leia.ForceUser && !wedge.ForceDiscovered && !leia.ForceDiscovered);
        w.Game.Step(); // the timers turn on
        Assert.False(piett.ForceDiscovered);
        w.Game.Step();
        Assert.True(piett.ForceDiscovered);
        Assert.Equal((Vader, 10), (piett.AwakenedBy, piett.EffectiveForce)); // his record: jedi_level 10 + rand(0..40), the stub answers 0
        Assert.False(wedge.ForceDiscovered); // Luke at 50 is no trainer

        luke.ForceExperience = 80;
        w.Stub!.Expect(0, 40, 33);
        w.Run(2);
        Assert.True(wedge.ForceDiscovered);
        Assert.Equal((Luke, 43), (wedge.AwakenedBy, wedge.EffectiveForce));
        Assert.False(leia.ForceDiscovered); // Luke does not know his heritage
        luke.KnowsHeritage = true;
        w.Game.Step();
        Assert.True(leia.ForceDiscovered && leia.KnowsHeritage);
        Assert.Equal(10, leia.EffectiveForce);
    }

    // B2e-18: the Emperor on Coruscant, the Empire holds it: Imperial base leadership 100 -> 150, Alliance unchanged; once he is captured, 100
    [Fact]
    public void B2e18_SeatOfPower()
    {
        var w = new CharWorld();
        w.Hold(Coruscant, Side.Empire);
        var piett = Skills(w.Add(Piett, Hoth), lead: 100);
        var ackbar = Skills(w.Add(Ackbar, Yavin), lead: 100);
        var emp = w.Add(Palpatine, Coruscant);
        w.Game.Step();
        Assert.True(w.Ch.SeatOfPowerOn);
        Assert.Equal((150, 100), (w.Ch.EffectiveLeadership(piett), w.Ch.EffectiveLeadership(ackbar)));
        w.StepWith("5.2", ctx => w.Ch.Capture(ctx, emp, Side.Alliance, null, CharacterCause.Debug));
        Assert.False(w.Ch.SeatOfPowerOn);
        Assert.Equal((100, 100), (w.Ch.EffectiveLeadership(piett), w.Ch.EffectiveLeadership(ackbar)));
    }

    // B2e-19: a timer with spread 0 makes no draw (the awakening timer of Vader runs every day)
    [Fact]
    public void B2e19_SpreadZeroTimersDrawNothing()
    {
        var w = new CharWorld();
        w.Hold(Coruscant, Side.Empire);
        Force(w.Add(Vader, Coruscant), 120);
        w.Run(30);
        var bare = new CharWorld(); // the same galaxy without Vader: its other timers (support incidents, m2-popular-support) draw the same
        bare.Hold(Coruscant, Side.Empire);
        bare.Run(30);
        Assert.Equal(bare.Stub!.Requests, w.Stub!.Requests);
        Assert.NotEqual(0, w.C(Vader).NextAwakenDay);
    }

    // Work 1, 2, 4: injury points, escape timers and the Force fields are saved and hashed
    [Fact]
    public void InjuryAndTimersAreSavedAndHashed()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire);
        var han = w.Add(Han, Hoth);
        w.StepWith("5.2", ctx => { w.Ch.AddInjury(ctx, han, 7, CharacterCause.Debug); w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug); });
        han.PairId = 3; han.KnowsHeritage = true; w.Game.State.Characters.NextPairId = 3;
        string hash = GameSave.StateHash(w.Game);
        w.Reload();
        var back = w.C(Han);
        Assert.Equal((7, han.NextEscapeDay, 3, true, 3), (back.Injury, back.NextEscapeDay, back.PairId, back.KnowsHeritage, w.Game.State.Characters.NextPairId));
        Assert.Equal(hash, GameSave.StateHash(w.Game));
        back.Injury = 8;
        Assert.NotEqual(hash, GameSave.StateHash(w.Game));
    }
}
