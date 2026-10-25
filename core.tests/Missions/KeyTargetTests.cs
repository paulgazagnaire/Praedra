using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Missions;
using Praedra.Core.Setup;
using Praedra.Core.Victory;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-missions §6a (key targets of Assassination, and Abduction of the four key characters), tests KT1 to KT14.
public class KeyTargetTests
{
    private const string Protected = MissionReject.TargetProtected;

    // Vader qualified for a kill or kidnap: combat 100 and espionage 60. Luke or Mon stands at T (Alliance), HQ placed at T when asked.
    private static MissionWorld Assassin(int target, int vaderCombat = 100, int vaderEsp = 60, int regiments = -1, bool hq = false)
    {
        var w = new MissionWorld(characters: Praedra.Core.Tests.Characters.CharWorld.ClassicForce()); // the Classic Force (100 / 100 / 50): m5-characters-and-force §Force CH37
        w.Hold(T, Side.Alliance, 30); // N = ceil(30 / 10) = 3 regiments
        for (int i = 0; i < Math.Max(regiments, 0); i++) w.Unit(Side.Alliance, RebArmy, T);
        w.Agent(Vader, T, esp: vaderEsp, com: vaderCombat);
        w.Agent(target, T);
        if (hq) w.Host.Fog.PlaceHq(T);
        return w;
    }

    private static string? StartReason(MissionWorld w, MissionKind kind, int agent, int target, Side side = Side.Empire)
    {
        long s = w.Start(side, kind, new[] { agent }, null, targetChar: target);
        w.Game.Step();
        return w.Reason(s);
    }

    private static void RideFleet(MissionWorld w, int character, Side side)
    {
        int f = w.Host.Fleets.AddFleet(side, "Escort", new[] { "Mandate" }, T);
        w.Ch.Get(character)!.AttachedFleet = f;
    }

    // m5-missions KT1: the exposure gate refuses a key target riding a fleet (Assassination and Abduction)
    [Theory]
    [InlineData(Luke)]
    [InlineData(MonMothma)]
    public void KT1_GateRefusesAKeyTargetInAFleet(int target)
    {
        var w = Assassin(target);
        RideFleet(w, target, Side.Alliance);
        Assert.Equal(Protected, StartReason(w, MissionKind.Assassination, Vader, target));
    }

    // m5-missions KT2: at the Rebel HQ the gate refuses only while the Alliance garrison covers the required N (3 here)
    [Fact]
    public void KT2_GateAtTheHqNeedsAFullGarrison()
    {
        var full = Assassin(Luke, regiments: 3, hq: true);
        Assert.Equal(Protected, StartReason(full, MissionKind.Assassination, Vader, Luke));
        var short2 = Assassin(Luke, regiments: 2, hq: true);
        Assert.Null(StartReason(short2, MissionKind.Assassination, Vader, Luke));
    }

    // m5-missions KT3: a fully garrisoned system that is not the HQ, and a free-standing target with no HQ there, are not protected
    [Fact]
    public void KT3_GateAllowsOtherwise()
    {
        var notHq = Assassin(Luke, regiments: 5, hq: false);
        Assert.Null(StartReason(notHq, MissionKind.Assassination, Vader, Luke));
        var hqElsewhere = Assassin(MonMothma, regiments: 5, hq: false);
        hqElsewhere.Host.Fog.PlaceHq(H);
        hqElsewhere.Hold(H, Side.Alliance, 100);
        Assert.Null(StartReason(hqElsewhere, MissionKind.Assassination, Vader, MonMothma));
    }

    // m5-missions KT4: the gate is judged again at each iteration start: a garrison completed after the order ends the mission TargetInvalid, no roll
    [Fact]
    public void KT4_GateIsJudgedAtEachIteration()
    {
        var w = Assassin(Luke, regiments: 2, hq: true);
        Assert.Null(StartReason(w, MissionKind.Assassination, Vader, Luke));
        int id = w.LastMission;
        for (int i = 0; i < 4; i++) w.Unit(Side.Alliance, RebArmy, T); // reinforcements (the short garrison started an uprising: N is doubled to 6)
        w.RunIteration(id);
        Assert.Empty(w.Events<MissionMemberRoll>());
        Assert.Equal(MissionOutcome.TargetInvalid, w.Events<MissionEnded>().Single().Outcome);
        Assert.False(w.Ch.IsDead(Luke));
    }

    // m5-missions KT5: the cap applies: a perfect team (table 55) gets 4 percent against Luke, 2 against Mae Abara (draws 3 / 4 and 1 / 2)
    [Theory]
    [InlineData(Luke, 3, true, 4)]
    [InlineData(Luke, 4, false, 4)]
    [InlineData(MonMothma, 1, true, 2)]
    [InlineData(MonMothma, 2, false, 2)]
    public void KT5_CapApplies(int target, int roll, bool success, int cap)
    {
        var w = Assassin(target);
        w.Stub!.Expect(0, 99, Bt(1, roll, 50)); // B2b: the betrayal draws come first
        Assert.Null(StartReason(w, MissionKind.Assassination, Vader, target));
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((cap, roll, success), (r.Chance, r.Roll, r.Success));
        Assert.Equal(success, w.Ch.IsDead(target));
    }

    // m5-missions KT6: one success roll per iteration even with three members; the best member's input is used
    [Fact]
    public void KT6_OneRollPerIterationWithThreeMembers()
    {
        var w = Assassin(Luke);
        w.Agent(Piett, T, com: 20);
        w.Agent(Veers, T, com: 40, esp: 10);
        w.Stub!.Expect(0, 99, Bt(3, 50)); // B2b: the betrayal draws come first
        long s = w.Start(Side.Empire, MissionKind.Assassination, new[] { Vader, Piett, Veers }, null, targetChar: Luke);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((Vader, 220), (r.Member, r.Input)); // B2e moved: Vader's effective combat 100 + 100 * 120 / 100
        Assert.Equal(4, w.Stub.Count(0, 99)); // three betrayal draws (B2b) and the one roll
    }

    // m5-missions KT7: a qualified team draws k: k < 3 kidnaps (Capture by the Empire, cause Kidnap), else kills (Dead although MajorsCanDie is off)
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void KT7_KidnapVersusKill(int k, bool kidnap)
    {
        var w = Assassin(Luke);
        Assert.False(w.Ch.Params.MajorsCanDie);
        w.Stub!.Expect(0, 99, Bt(1, 0, k)); // B2b: the betrayal draws come first
        Assert.Null(StartReason(w, MissionKind.Assassination, Vader, Luke));
        w.RunIteration(w.LastMission);
        var luke = w.Ch.Get(Luke)!;
        if (kidnap)
        {
            Assert.Equal((CharacterStatus.Captured, Side.Empire), (luke.Status, luke.Captor!.Value));
            Assert.Equal(MissionModule.CauseKidnap, w.Events<CharacterCaptured>().Single().Cause);
            Assert.Empty(w.Events<CharacterKilled>());
        }
        else
        {
            Assert.Equal(CharacterStatus.Dead, luke.Status);
            Assert.Equal(MissionModule.CauseAssassination, w.Events<CharacterKilled>().Single().Cause);
            Assert.Empty(w.Events<CharacterCaptured>());
        }
        Assert.Equal(3, w.Stub.Count(0, 99)); // betrayal (B2b), roll, k
    }

    // m5-missions KT8: no member with combat >= 60 and espionage >= 60 (the same character): the target is injured and no k is drawn
    [Theory]
    [InlineData(59, 100)]
    [InlineData(100, 59)]
    public void KT8_UnqualifiedTeamInjures(int combat, int esp)
    {
        var w = Assassin(Luke, vaderCombat: combat, vaderEsp: esp);
        w.Ch.Get(Vader)!.ForceExperience = 0; // B2e: without his Force so the skills given are the effective ones
        w.Stub!.Expect(0, 99, Bt(1, 0, 0)); // B2b: the betrayal draws come first
        Assert.Null(StartReason(w, MissionKind.Assassination, Vader, Luke));
        w.RunIteration(w.LastMission);
        Assert.Single(w.Events<CharacterInjured>());
        // B2e moved: the injury is the Hurt roll on Luke's own combat (0, so p = 100: the third draw 0 hits); betrayal (B2b), roll, Hurt; no k
        Assert.Equal(3, w.Stub.Count(0, 99));
        Assert.Empty(w.Events<CharacterKilled>());
    }

    // m5-missions KT9: two members, one holding combat and the other espionage, do not qualify (one character needs both)
    [Fact]
    public void KT9_QualificationIsOneCharacter()
    {
        var w = Assassin(Luke, vaderCombat: 100, vaderEsp: 0);
        w.Agent(Piett, T, esp: 100, com: 1);
        w.Stub!.Expect(0, 99, Bt(2, 0)); // B2b: the betrayal draws come first
        long s = w.Start(Side.Empire, MissionKind.Assassination, new[] { Vader, Piett }, null, targetChar: Luke);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        w.RunIteration(w.LastMission);
        Assert.Single(w.Events<CharacterInjured>());
        Assert.Equal(4, w.Stub.Count(0, 99)); // two betrayal draws (B2b), roll, and (B2e moved) the Hurt roll of the injury
    }

    // m5-missions KT10: a key kill waives the Empire's Luke term (KeyDeathFallback default WaiveCondition); the Empire wins when Mae Abara is
    // captured and the HQ is gone, and not before
    [Fact]
    public void KT10_KillOfLukeSatisfiesTheEmpireTermAndTheEmpireWins()
    {
        var w = Assassin(Luke);
        w.Hold("X", Side.Alliance, 80); // the Alliance keeps a system, so "holds no system" does not end the game
        w.Hold(T, Side.Empire, 20);     // T fell: the HQ at rest on a system the Alliance no longer holds
        w.Host.Fog.PlaceHq(T);
        w.Game.State.Scenario.Active = true;
        w.Stub!.Expect(0, 99, Bt(1, 0, 50)); // B2b: the betrayal draws come first
        // the team is Empire, Luke is an Alliance character on a system now held by the Empire: still a legal target (free, there, no fleet, HQ not Alliance-held)
        Assert.Null(StartReason(w, MissionKind.Assassination, Vader, Luke));
        w.Game.Step();
        Assert.True(w.Host.Victory.Rules.StandardVictory); // standard victory is the default
        Assert.Equal(KeyDeathFallback.WaiveCondition, w.Host.Victory.Rules.KeyDeathFallback);
        Assert.Null(w.Host.Victory.Winner); // Luke's term waived, Mae Abara still free
        w.RunIteration(w.LastMission);
        Assert.True(w.Ch.IsDead(Luke));
        Assert.Null(w.Host.Victory.Winner);
        w.Ch.Capture(null, w.Ch.Get(MonMothma) ?? w.Agent(MonMothma, T), Side.Empire, T, CharacterCause.Debug);
        w.Game.Step();
        Assert.Equal(Side.Empire, w.Host.Victory.Winner);
    }

    // m5-missions KT11: non-key targets are unchanged: one roll per member, the table value uncapped, Kill as before
    [Fact]
    public void KT11_NonKeyTargetsUnchanged()
    {
        var w = new MissionWorld();
        w.Agent(Vader, T, com: 100);
        w.Agent(Piett, T, com: 100);
        w.Agent(Wedge, T);
        w.Stub!.Expect(0, 99, Bt(2, 54, 99)); // B2b: the betrayal draws come first
        long s = w.Start(Side.Empire, MissionKind.Assassination, new[] { Vader, Piett }, null, targetChar: Wedge);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        w.RunIteration(w.LastMission);
        var rolls = w.Events<MissionMemberRoll>();
        Assert.Equal(2, rolls.Count);
        Assert.All(rolls, r => Assert.Equal(55, r.Chance));
        Assert.True(w.Ch.IsDead(Wedge));
    }

    // m5-missions KT12: Vader and Palpatine cannot be assassinated: the Alliance has no Assassination mission
    [Theory]
    [InlineData(Vader)]
    [InlineData(Palpatine)]
    public void KT12_AllianceCannotAssassinateTheEmperorsSide(int target)
    {
        var w = new MissionWorld();
        w.Agent(Han, T, com: 100);
        w.Agent(target, T);
        Assert.Equal(MissionReject.WrongSide, StartReason(w, MissionKind.Assassination, Han, target, Side.Alliance));
    }

    // m5-missions KT13: Abduction of the four: the cap applies (Vader 4, Palpatine 2), a success is the normal capture (cause Abduction, no k draw)
    [Theory]
    [InlineData(Vader, 3, true)]
    [InlineData(Vader, 4, false)]
    [InlineData(Palpatine, 1, true)]
    [InlineData(Palpatine, 2, false)]
    public void KT13_AbductionOfTheFourIsCapped(int target, int roll, bool success)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 20);
        w.Agent(Han, T, com: 100);
        w.Agent(target, T);
        w.Stub!.Expect(0, 99, Bt(1, roll, 50)); // B2b: the betrayal draws come first (Han has no Force: no sensing draw)
        Assert.Null(StartReason(w, MissionKind.Abduction, Han, target, Side.Alliance));
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((target == Vader ? 4 : 2, success), (r.Chance, r.Success));
        Assert.Equal(success, w.Ch.IsCaptured(target));
        Assert.Equal(2, w.Stub.Count(0, 99)); // betrayal (B2b), roll
        if (success) Assert.Equal(MissionModule.CauseAbduction, w.Events<CharacterCaptured>().Single().Cause);
    }

    // m5-missions KT14: Abduction of the four: one roll per iteration with three members; Luke and Mae Abara are gated too (Empire team, fleet)
    [Fact]
    public void KT14_AbductionOneRollAndGateInFleet()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 20);
        w.Agent(Han, H, com: 100); // B2d: the team flies in from H (a team leaving T would meet Vader's sensing at the leaving check too)
        w.Agent(Leia, H, com: 50);
        w.Agent(Wedge, H, com: 50);
        w.Agent(Vader, T);
        // B2e moved: Leia and Wedge are Force users who are not aware, so their Force is 0 and Vader senses nobody (was two sense rolls); three
        // betrayal draws, then the roll
        w.Stub!.Expect(0, 99, 99, 99, 99, 50);
        w.Start(Side.Alliance, MissionKind.Abduction, new[] { Han, Leia, Wedge }, null, targetChar: Vader);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Single(w.Events<MissionMemberRoll>());
        Assert.Empty(w.Events<MissionForceSenseRoll>());

        var g = Assassin(Luke);
        RideFleet(g, Luke, Side.Alliance);
        Assert.Equal(Protected, StartReason(g, MissionKind.Abduction, Vader, Luke));
        var f = new MissionWorld();
        f.Agent(Han, T, com: 100); f.Agent(Palpatine, T);
        RideFleet(f, Palpatine, Side.Empire);
        Assert.Equal(Protected, StartReason(f, MissionKind.Abduction, Han, Palpatine, Side.Alliance));
    }

    // m5-missions KT15: an Empire key target at Charter is protected while the Empire's garrison there covers N, and not otherwise (real galaxy)
    [Fact]
    public void KT15_CoruscantIsTheEmpiresProtectedCapital()
    {
        var h = NewGame.Create(GameData.Default, new NewGameOptions { Seed = 1 });
        const string Cor = "Charter";
        h.Ch().Place(Palpatine, Cor);
        h.Ch().Place(Han, Cor);
        h.Economy.SetAllianceSupport(Cor, 100); // Empire support 0: N is large
        foreach (var o in h.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.System == Cor).ToList()) o.System = "The Shoal"; // clear the setup garrison
        var order = new StartMissionOrder(0, Side.Alliance, MissionKind.Abduction, new List<int> { Han }, new List<int>(), new List<int>(), null, Palpatine, null, null);
        Assert.True(h.Support.Garrison10(Cor) < 10 * h.Support.RequiredGarrison(Cor), "setup leaves Charter short of N");
        Assert.Null(h.Missions.CheckStart(order).Reason);
        while (h.Support.Garrison10(Cor) < 10 * h.Support.RequiredGarrison(Cor)) h.Economy.AddObject(Side.Empire, "Tranches", Cor, true);
        Assert.Equal(Protected, h.Missions.CheckStart(order).Reason);
    }

    // m5-missions KT16: with KeyTargetRules off the key targets are ordinary targets (switch)
    [Fact]
    public void KT16_SwitchOffRestoresOrdinaryTargets()
    {
        var w = new MissionWorld(new MissionParams { KeyTargetRules = false });
        w.Hold(T, Side.Alliance, 30);
        w.Agent(Vader, T, com: 100);
        w.Agent(Luke, T);
        RideFleet(w, Luke, Side.Alliance);
        // a fleet rider is still a legal target in the old rules only if the ordinary checks pass; the gate must not be the reason
        Assert.NotEqual(Protected, StartReason(w, MissionKind.Assassination, Vader, Luke));
    }

    // m5-missions §6a data: the file ships Luke 4, Mae Abara 2, Vader 4, Palpatine 2
    [Fact]
    public void KT17_ShippedCaps()
    {
        var k = GameData.Default.Missions.KeyTargets;
        Assert.Equal(new[] { (MonMothma, 2), (Luke, 4), (Palpatine, 2), (Vader, 4) }, k.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
    }
}

internal static class KeyTestExt
{
    public static CharacterModule Ch(this GameHost h) => h.Characters;
}
