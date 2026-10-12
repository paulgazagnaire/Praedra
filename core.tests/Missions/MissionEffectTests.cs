using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Missions;
using Rebellion.Core.Support;
using static Rebellion.Core.Tests.Missions.MissionWorld;

namespace Rebellion.Core.Tests.Missions;

// m5-missions §Tests MI27 to MI38 and MI56: the effects of a successful iteration and the incident-score terms.
public class MissionEffectTests
{
    // A Diplomacy team at T; the success roll draws 0; the gain draws as given. Returns the world after the first iteration.
    private static MissionWorld Diplomacy(Side side, Side? holder, int allianceSupport, int gainDraw, int gainMax, int dip = 120, int regiments = 0, bool deathStar = false)
    {
        var w = new MissionWorld();
        w.Hold(T, holder, allianceSupport);
        if (holder is Side h) for (int i = 0; i < regiments; i++) w.Unit(h, h == Side.Alliance ? RebArmy : ImpArmy, T);
        if (deathStar) w.Host.Support.SetDeathStarSystem(T, true);
        int agent = side == Side.Alliance ? Leia : Piett;
        w.Agent(agent, T, dip: dip);
        w.Stub!.Expect(0, 99, 0).Expect(0, gainMax, gainDraw);
        w.Start(side, MissionKind.Diplomacy, new[] { agent }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        return w;
    }

    private static int A(MissionWorld w) => w.Host.Economy.SupportOf(T, Side.Alliance);

    // m5-missions MI27: Alliance holds, A = 40, gain draw rand(0..19) = 6: g = 7, A = 47, SupportChanged{Diplomacy}
    [Fact]
    public void MI27_DiplomacyOwnSystem()
    {
        var w = Diplomacy(Side.Alliance, Side.Alliance, 40, 6, 19, regiments: 2);
        Assert.Equal(47, A(w));
        var sc = w.Events<SupportChanged>().Single(e => e.Cause == SupportCause.Diplomacy);
        Assert.Equal((40, 47), (sc.Old, sc.New));
    }

    // m5-missions MI28: Alliance on a neutral system, A = 50, rand(0..9) = 9: g = 10, A = 60
    [Fact]
    public void MI28_DiplomacyNeutral() => Assert.Equal(60, A(Diplomacy(Side.Alliance, null, 50, 9, 9)));

    // m5-missions MI29: Empire on an Empire system, A = 40 (Empire 60), rand(0..19) = 4: Empire 65, A = 35
    [Fact]
    public void MI29_DiplomacyEmpire() => Assert.Equal(35, A(Diplomacy(Side.Empire, Side.Empire, 40, 4, 19)));

    // m5-missions MI30: MI27 in a Death Star sector: 7 / 2 = 3, A = 43
    [Fact]
    public void MI30_DeathStarDamping() => Assert.Equal(43, A(Diplomacy(Side.Alliance, Side.Alliance, 40, 6, 19, regiments: 2, deathStar: true)));

    // m5-missions MI31: A = 95 with g = 10: clamp, A = 100
    [Fact]
    public void MI31_Clamp() => Assert.Equal(100, A(Diplomacy(Side.Alliance, Side.Alliance, 95, 9, 19)));

    // Subdue at T, Empire holds with Empire support 20 (A = 80): Step 0 starts the uprising (no regiment), then five regiments land.
    private static MissionWorld Subdue(int gainDraw, MissionParams? p = null)
    {
        var w = new MissionWorld(p);
        w.Hold(T, Side.Empire, 80);
        w.Game.Step();
        Assert.True(w.Host.Support.IsUprising(T));
        for (int i = 0; i < 5; i++) w.Unit(Side.Empire, ImpArmy, T);
        w.Agent(Veers, T, lead: 40);
        w.Stub!.Expect(0, 99, 0).Expect(0, 19, gainDraw);
        w.Start(Side.Empire, MissionKind.SubdueUprising, new[] { Veers }, T);
        w.Game.Step();
        Assert.True(w.Host.Support.IsUprising(T)); // doubled requirement 8 > 5
        w.RunIteration(w.LastMission);
        return w;
    }

    // m5-missions MI32: gain draw 19: g = 20, Empire support 40; slot 6.1 of the same Step: N = 2 doubled 4, G = 5: UprisingEnded
    [Fact]
    public void MI32_SubdueThenGarrisonEndsTheUprising()
    {
        var w = Subdue(19);
        Assert.Equal(60, A(w));
        Assert.Equal(SupportCause.Subdue, w.Events<SupportChanged>().Last().Cause);
        var ended = w.Events<UprisingEnded>().Single();
        Assert.Equal(("Garrison", w.Events<MissionMemberRoll>().Single().Day), (ended.Reason, ended.Day));
        Assert.False(w.Host.Support.IsUprising(T));
    }

    // m5-missions MI33: gain draw 9: g = 10, Empire support 30, N = 3 doubled 6 > 5: the uprising stays; the next iteration is still due
    [Fact]
    public void MI33_SubdueNotEnough()
    {
        var w = Subdue(9);
        Assert.Equal(70, A(w));
        Assert.True(w.Host.Support.IsUprising(T));
        var m = w.M.Get(w.LastMission)!;
        Assert.Equal(MissionPhase.Working, m.Phase);
        Assert.Equal(w.Events<MissionIterationDone>().Single().Due, m.Due);
    }

    // m5-missions MI34: MI33 with SubdueEndsUprising: the flag is cleared at once; the next legality check ends the mission TargetInvalid
    [Fact]
    public void MI34_SubdueEndsUprisingSwitch()
    {
        var w = Subdue(9, new MissionParams { SubdueEndsUprising = true });
        Assert.False(w.Host.Support.IsUprising(T));
        Assert.Equal(MissionModule.CauseSubdue, w.Events<UprisingEnded>().Single().Reason);
        Assert.Null(w.Host.Support.StateOf(T)); // timers disarmed (idle entry pruned)
        int rolls = w.Stub!.Count(0, 99);
        w.RunIteration(w.LastMission);
        Assert.Equal(MissionOutcome.TargetInvalid, w.Events<MissionEnded>().Single().Outcome);
        Assert.Equal(rolls, w.Stub.Count(0, 99));
    }

    // m5-missions MI35: Incite success, Alliance on an Empire system, A = 30 (Empire 70): ChangeSupport(Empire, -2): Empire 68, A = 32; no uprising flag
    [Fact]
    public void MI35_InciteLowersTheHolderSupport()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 30);
        w.Agent(Leia, T, lead: 100); // x = 100 - 70 = 30: INCTMSTB step gives 1; draw 0 succeeds
        w.Stub!.Expect(0, 99, 0);
        w.Start(Side.Alliance, MissionKind.InciteUprising, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(32, A(w));
        Assert.Equal(SupportCause.IncitedUprising, w.Events<SupportChanged>().Single().Cause);
        Assert.False(w.Host.Support.IsUprising(T));
    }

    // InciteSupportTiming PerIncident: no change at iteration end; each incident at a system with a working Incite mission costs 2 (slot 6.3)
    [Fact]
    public void InciteSupportPerIncident()
    {
        var w = new MissionWorld(new MissionParams { InciteSupportTiming = InciteSupportTiming.PerIncident });
        w.Hold(T, Side.Empire, 30);
        w.Agent(Leia, T, lead: 100);
        w.Stub!.Expect(0, 99, 0);
        w.Start(Side.Alliance, MissionKind.InciteUprising, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Empty(w.Events<SupportChanged>());
        Assert.Equal(MissionPhase.Working, w.M.Get(w.LastMission)!.Phase);
        // an incident fires (the hook the support module calls after the effects)
        bool done = false;
        w.Game.RegisterHook("6.3", ctx => { if (!done) { done = true; w.Host.Support.IncidentFired!(ctx, T, Side.Empire); } });
        w.Game.Step();
        Assert.Equal(32, A(w));
    }

    // m5-missions MI36: Sabotage with 3 completed facilities, pick draw 2: the third (highest id) is destroyed; with none: no draw, SabotageNoTarget
    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public void MI36_SabotagePick(int facilities)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        var ids = Enumerable.Range(0, facilities).Select(_ => w.Host.Economy.AddFacility(T, FacilityKind.Mine)).ToList();
        w.Agent(Leia, T, esp: 50, com: 50);
        w.Stub!.Expect(0, 99, 0).Expect(0, 2, 0, 2);
        w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        if (facilities == 0)
        {
            Assert.Single(w.Events<SabotageNoTarget>());
            Assert.Equal(1, w.Stub.Count(0, 2)); // the timer only
            return;
        }
        Assert.Equal(ids[2], w.Events<FacilityDestroyed>().Single().Facility);
        Assert.Null(w.Host.Economy.Find(ids[2]));
    }

    // SabotageTargetMode Chosen: the order names the facility; no pick draw
    [Fact]
    public void SabotageChosenFacility()
    {
        var w = new MissionWorld(new MissionParams { SabotageTargetMode = SabotageTargetMode.Chosen });
        w.Hold(T, Side.Empire, 0);
        int a = w.Host.Economy.AddFacility(T, FacilityKind.Mine), b = w.Host.Economy.AddFacility(T, FacilityKind.Refinery);
        w.Agent(Leia, T, esp: 50, com: 50);
        w.Stub!.Expect(0, 99, 0);
        Assert.Equal(MissionReject.BadTarget, w.Try(seq => new StartMissionOrder(seq, Side.Alliance, MissionKind.Sabotage, new() { Leia }, new(), new(), T, null, 999)));
        w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Leia }, T, facility: a);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(a, w.Events<FacilityDestroyed>().Single().Facility);
        Assert.NotNull(w.Host.Economy.Find(b));
        Assert.Equal(1, w.Stub.Count(0, 2)); // the timer only
    }

    // m5-missions MI37: Death Star Sabotage x = 100 (p = 99) with a stub Death Star: Destroy called once
    [Fact]
    public void MI37_DeathStarSabotage()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Luke, T, esp: 100, com: 100);
        var calls = new List<(string, Side)>();
        w.M.HasDeathStar = (owner, sys) => owner == Side.Empire && sys == T;
        w.M.DestroyDeathStar = (_, sys, owner) => calls.Add((sys, owner));
        w.Start(Side.Alliance, MissionKind.DeathStarSabotage, new[] { Luke }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal((100, 99), (w.Events<MissionMemberRoll>().Single().Input, w.Events<MissionMemberRoll>().Single().Chance));
        Assert.Equal(new[] { (T, Side.Empire) }, calls);
    }

    // m5-missions MI38: Abduction, Rescue and Assassination successes call Capture(target, side, Abduction), Free(prisoner), Kill(target, Assassination)
    [Fact]
    public void MI38_CharacterEffects()
    {
        // Abduction: Han (combat 100) takes Vader (combat 0): ABDCMSTB(100) = 99
        var w = new MissionWorld();
        w.Agent(Han, T, com: 100);
        w.Agent(Vader, T);
        w.Start(Side.Alliance, MissionKind.Abduction, new[] { Han }, null, targetChar: Vader);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var vader = w.Ch.Get(Vader)!;
        Assert.Equal((CharacterStatus.Captured, Side.Alliance, T), (vader.Status, vader.Captor!.Value, vader.HeldAt));
        Assert.Equal(MissionModule.CauseAbduction, w.Events<CharacterCaptured>().Single().Cause);

        // Rescue: Leia held by the Empire at T; Han frees her
        w = new MissionWorld();
        w.Agent(Han, T, com: 100);
        var leia = w.Agent(Leia, T);
        w.Ch.Capture(null, leia, Side.Empire, T, CharacterCause.Debug);
        w.Start(Side.Alliance, MissionKind.Rescue, new[] { Han }, null, targetChar: Leia);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal((CharacterStatus.Free, T), (leia.Status, leia.System));
        Assert.Equal(MissionModule.CauseRescue, w.Events<CharacterFreed>().Single().Cause);
        Assert.False(w.M.IsOnMission(Leia)); // not travelling with the team

        // Assassination: Vader kills Wedge (a minor)
        w = new MissionWorld();
        w.Agent(Vader, T, com: 100);
        w.Agent(Wedge, T);
        w.Start(Side.Empire, MissionKind.Assassination, new[] { Vader }, null, targetChar: Wedge);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.True(w.Ch.IsDead(Wedge));
        Assert.Equal(MissionModule.CauseAssassination, w.Events<CharacterKilled>().Single().Cause);
    }

    // A major target of an Assassination is injured, not killed (MajorsCanDie DEFAULT off: the character sheet's rule).
    [Fact]
    public void AssassinationOfAMajorInjures()
    {
        var w = new MissionWorld();
        w.Agent(Vader, T, com: 100);
        w.Agent(Luke, T);
        w.Start(Side.Empire, MissionKind.Assassination, new[] { Vader }, null, targetChar: Luke);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.False(w.Ch.IsDead(Luke));
        Assert.Single(w.Events<CharacterInjured>());
    }

    // Standard victory needs a capture: an Abduction makes Luke "captured" for the victory module (StandardVictory stays off by default).
    [Fact]
    public void AbductionMakesAKeyCharacterCaptured()
    {
        var w = new MissionWorld();
        w.Agent(Vader, T, com: 100);
        w.Agent(Luke, T, com: 0);
        w.Start(Side.Empire, MissionKind.Abduction, new[] { Vader }, null, targetChar: Luke);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.True(w.Ch.IsCaptured(Luke));
        Assert.False(w.Host.Victory.Rules.StandardVictory); // the lead's DEFAULT stays off (decision pending)
    }

    // m5-missions MI56: a working Incite team (character leadership 70 + Guerrillas 50): incite_term = 7 + 5 = 12; Subdue the same members: -12; none: 0
    [Fact]
    public void MI56_IncidentTerms()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 30);
        w.Agent(Leia, T, lead: 70);
        int g = w.Unit(Side.Alliance, Guerrillas, T);
        Assert.Equal(0, w.M.IncidentTerm(T));
        w.Stub!.Expect(0, 99, 99, 99);
        w.Start(Side.Alliance, MissionKind.InciteUprising, new[] { Leia }, T, new[] { g });
        w.Game.Step();
        Assert.Equal(12, w.M.InciteTerm(T));
        Assert.Equal(12, w.M.IncidentTerm(T));
        // the support module's score adds it
        int baseScore = SupportModule.IncidentScore(w.Host.Support.Params, 1, 1, 70, 0, 0, false);
        Assert.Equal(baseScore + 12, w.Host.Support.IncidentScore(T, Side.Empire, 1, 1));

        // Subdue: Empire at an Empire system in uprising, Veers (leadership 70) + Imperial Commandos (50)
        var s = new MissionWorld();
        s.Hold(X, Side.Empire, 80);
        s.Game.Step();
        s.Agent(Veers, X, lead: 70);
        int c = s.Unit(Side.Empire, Commandos, X);
        s.Start(Side.Empire, MissionKind.SubdueUprising, new[] { Veers }, X, new[] { c });
        s.Game.Step();
        Assert.Equal(-12, s.M.SubdueTerm(X));
        Assert.Equal(0, s.M.IncidentTerm(T));
    }
}
