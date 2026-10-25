using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Missions;
using Praedra.Core.Support;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-missions §Tests MI27 to MI38 and MI56: the effects of a successful iteration and the incident-score terms.
public class MissionEffectTests
{
    // A Diplomacy team at T; the success roll draws 0; the gain draws as given. Returns the world after the first iteration.
    private static MissionWorld Diplomacy(Side side, Side? holder, int collectiveSupport, int gainDraw, int gainMax, int dip = 120, int regiments = 0, bool harvester = false,
        Praedra.Core.Control.ControlRule rule = Praedra.Core.Control.ControlRule.AssaultOnly)
    {
        var w = new MissionWorld(rule: rule);
        w.Hold(T, holder, collectiveSupport);
        if (holder is Side h) for (int i = 0; i < regiments; i++) w.Unit(h, h == Side.Collective ? RebArmy : ImpArmy, T);
        // The sector flag is recomputed every day at slot 5.6 from the harvesters in fleets (m5-death-star §3), so a hand-set flag would be cleared:
        // a Hand harvester at H (same sector as T) raises it the honest way.
        if (harvester) w.Host.Fleets.AddFleet(Side.Hand, "harvester", new[] { "harvester" }, H);
        int agent = side == Side.Collective ? Ledger : Varo;
        w.Agent(agent, T, dip: dip);
        w.Stub!.Expect(0, 99, 0).Expect(0, gainMax, gainDraw);
        w.Start(side, MissionKind.Diplomacy, new[] { agent }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        return w;
    }

    private static int A(MissionWorld w) => w.Host.Economy.SupportOf(T, Side.Collective);

    // m5-missions MI27: Collective holds, A = 40, gain draw rand(0..19) = 6: g = 7, A = 47, SupportChanged{Diplomacy}
    [Fact]
    public void MI27_DiplomacyOwnSystem()
    {
        var w = Diplomacy(Side.Collective, Side.Collective, 40, 6, 19, regiments: 2);
        Assert.Equal(47, A(w));
        var sc = w.Events<SupportChanged>().Single(e => e.Cause == SupportCause.Diplomacy);
        Assert.Equal((40, 47), (sc.Old, sc.New));
    }

    // m5-missions MI28: Collective on a neutral system, A = 50, rand(0..9) = 9: g = 10, A = 60 (AssaultOnly, the DEFAULT until the AI copes, 2026-10-18).
    [Fact]
    public void MI28_DiplomacyNeutral() => Assert.Equal(60, A(Diplomacy(Side.Collective, null, 50, 9, 9)));

    // (B5a, 2026-10-17) MI28 under Derived control (set explicitly): at 60 the world joins the Collective and the sector step from neutral (+1, the
    // world included) makes it 61.
    [Fact]
    public void MI28_DiplomacyNeutral_Derived()
    {
        var w = Diplomacy(Side.Collective, null, 50, 9, 9, rule: Praedra.Core.Control.ControlRule.Derived);
        Assert.Equal(61, A(w));
        Assert.Equal((50, 60), w.Events<SupportChanged>().Where(e => e.Cause == SupportCause.Diplomacy).Select(e => (e.Old, e.New)).Single());
        Assert.Equal(Side.Collective, w.Host.Control.HolderOf(T));
    }

    // m5-missions MI29: Hand on a Hand system, A = 40 (Hand 60), rand(0..19) = 4: Hand 65, A = 35
    [Fact]
    public void MI29_DiplomacyHand() => Assert.Equal(35, A(Diplomacy(Side.Hand, Side.Hand, 40, 4, 19)));

    // m5-missions MI30: MI27 in a harvester sector: 7 / 2 = 3, A = 43
    [Fact]
    public void MI30_HarvesterDamping() => Assert.Equal(43, A(Diplomacy(Side.Collective, Side.Collective, 40, 6, 19, regiments: 2, harvester: true)));

    // m5-missions MI31: A = 95 with g = 10: clamp, A = 100
    [Fact]
    public void MI31_Clamp() => Assert.Equal(100, A(Diplomacy(Side.Collective, Side.Collective, 95, 9, 19)));

    // Subdue at T, Hand holds with Hand support 20 (A = 80): Step 0 starts the uprising (no regiment), then five regiments land.
    private static MissionWorld Subdue(int gainDraw, MissionParams? p = null)
    {
        var w = new MissionWorld(p);
        w.Hold(T, Side.Hand, 80);
        w.Game.Step();
        Assert.True(w.Host.Support.IsUprising(T));
        for (int i = 0; i < 5; i++) w.Unit(Side.Hand, ImpArmy, T);
        w.Agent(EightyEight, T, lead: 40);
        w.Stub!.Expect(0, 99, 0).Expect(0, 19, gainDraw);
        w.Start(Side.Hand, MissionKind.SubdueUprising, new[] { EightyEight }, T);
        w.Game.Step();
        Assert.True(w.Host.Support.IsUprising(T)); // doubled requirement 8 > 5
        w.RunIteration(w.LastMission);
        return w;
    }

    // m5-missions MI32: gain draw 19: g = 20, Hand support 40; slot 6.1 of the same Step: N = 2 doubled 4, G = 5: UprisingEnded
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

    // m5-missions MI33: gain draw 9: g = 10, Hand support 30, N = 3 doubled 6 > 5: the uprising stays; the next iteration is still due
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

    // m5-missions MI35: Incite success, Collective on a Hand system, A = 30 (Hand 70): ChangeSupport(Hand, -2): Hand 68, A = 32; no uprising flag
    [Fact]
    public void MI35_InciteLowersTheHolderSupport()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Hand, 30);
        w.Agent(Ledger, T, lead: 100); // x = 100 - 70 = 30: INCTMSTB step gives 1; draw 0 succeeds
        w.Stub!.Expect(0, 99, 0);
        w.Start(Side.Collective, MissionKind.InciteUprising, new[] { Ledger }, T);
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
        w.Hold(T, Side.Hand, 30);
        w.Agent(Ledger, T, lead: 100);
        w.Stub!.Expect(0, 99, 0);
        w.Start(Side.Collective, MissionKind.InciteUprising, new[] { Ledger }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Empty(w.Events<SupportChanged>());
        Assert.Equal(MissionPhase.Working, w.M.Get(w.LastMission)!.Phase);
        // an incident fires (the hook the support module calls after the effects)
        bool done = false;
        w.Game.RegisterHook("6.3", ctx => { if (!done) { done = true; w.Host.Support.IncidentFired!(ctx, T, Side.Hand); } });
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
        w.Hold(T, Side.Hand, 0);
        var ids = Enumerable.Range(0, facilities).Select(_ => w.Host.Economy.AddFacility(T, FacilityKind.Mine)).ToList();
        w.Agent(Ledger, T, esp: 50, com: 50);
        w.Stub!.Expect(0, 99, 0).Expect(0, 2, 0, 2);
        w.Start(Side.Collective, MissionKind.Sabotage, new[] { Ledger }, T);
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
        w.Hold(T, Side.Hand, 0);
        int a = w.Host.Economy.AddFacility(T, FacilityKind.Mine), b = w.Host.Economy.AddFacility(T, FacilityKind.Refinery);
        w.Agent(Ledger, T, esp: 50, com: 50);
        w.Stub!.Expect(0, 99, 0);
        Assert.Equal(MissionReject.BadTarget, w.Try(seq => new StartMissionOrder(seq, Side.Collective, MissionKind.Sabotage, new() { Ledger }, new(), new(), T, null, 999)));
        w.Start(Side.Collective, MissionKind.Sabotage, new[] { Ledger }, T, facility: a);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal(a, w.Events<FacilityDestroyed>().Single().Facility);
        Assert.NotNull(w.Host.Economy.Find(b));
        Assert.Equal(1, w.Stub.Count(0, 2)); // the timer only
    }

    // m5-missions MI37: Spike the Harvester x = 100 (p = 99) with a stub harvester: Destroy called once
    [Fact]
    public void MI37_HarvesterSabotage()
    {
        var w = new MissionWorld(characters: Praedra.Core.Tests.Characters.CharWorld.ClassicTalent()); // the Classic Talent (m5-characters-and-force §Talent CH37)
        w.Hold(T, Side.Hand, 0);
        w.Agent(Nobody, T, esp: 100, com: 100);
        var calls = new List<(string, Side)>();
        w.M.HasHarvester = (owner, sys) => owner == Side.Hand && sys == T;
        w.M.DestroyHarvester = (_, sys, owner) => calls.Add((sys, owner));
        w.Start(Side.Collective, MissionKind.SpikeTheHarvester, new[] { Nobody }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Equal((150, 99), (w.Events<MissionMemberRoll>().Single().Input, w.Events<MissionMemberRoll>().Single().Chance)); // B2e moved: Nobody's Talent 50 raises both skills to 150
        Assert.Equal(new[] { (T, Side.Hand) }, calls);
    }

    // m5-missions MI38: Abduction, Rescue and Assassination successes call Capture(target, side, Abduction), Free(prisoner), Kill(target, Assassination)
    [Fact]
    public void MI38_CharacterEffects()
    {
        // Abduction: Midwife (combat 100) takes Ravilla (combat 0): ABDCMSTB(100) = 99
        var w = new MissionWorld();
        w.Agent(Midwife, T, com: 100);
        w.Agent(Ravilla, T);
        w.Start(Side.Collective, MissionKind.Abduction, new[] { Midwife }, null, targetChar: Ravilla);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var ravilla = w.Ch.Get(Ravilla)!;
        // QA-7 moved (B2e §3 step 4, B2e-22): the captor is a team member, so Ravilla is the team's captive (no planet yet), not held at T
        Assert.Equal((CharacterStatus.Captured, Side.Collective, (string?)null), (ravilla.Status, ravilla.Captor!.Value, ravilla.HeldAt));
        Assert.Contains(Ravilla, w.M.Get(w.LastMission)!.Captives!);
        Assert.True(w.M.IsOnMission(Ravilla));
        Assert.Equal(MissionModule.CauseAbduction, w.Events<CharacterCaptured>().Single().Cause);

        // Rescue: Ledger held by the Hand at T; Midwife frees her
        w = new MissionWorld();
        w.Agent(Midwife, T, com: 100);
        var ledger = w.Agent(Ledger, T);
        w.Ch.Capture(null, ledger, Side.Hand, T, CharacterCause.Debug);
        w.Start(Side.Collective, MissionKind.Rescue, new[] { Midwife }, null, targetChar: Ledger);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        // QA-9 moved (B2e §3 Autoroute): freed on T (no holder), she sets off at once for the nearest neutral world (step 6; the Collective holds none)
        Assert.Equal((CharacterStatus.Free, T), (ledger.Status, ledger.Transit!.Path[0]));
        Assert.Equal(MissionModule.CauseRescue, w.Events<CharacterFreed>().Single().Cause);
        Assert.False(w.M.IsOnMission(Ledger)); // not travelling with the team

        // Assassination: Ravilla kills Forty-One (a minor)
        w = new MissionWorld();
        w.Agent(Ravilla, T, com: 100);
        w.Agent(FortyOne, T);
        w.Start(Side.Hand, MissionKind.Assassination, new[] { Ravilla }, null, targetChar: FortyOne);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.True(w.Ch.IsDead(FortyOne));
        Assert.Equal(MissionModule.CauseAssassination, w.Events<CharacterKilled>().Single().Cause);
    }

    // A major target of an Assassination is injured, not killed (MajorsCanDie DEFAULT off: the character sheet's rule).
    [Fact]
    public void AssassinationOfAMajorInjures()
    {
        var w = new MissionWorld();
        w.Agent(Ravilla, T, com: 100);
        w.Agent(Nobody, T);
        w.Start(Side.Hand, MissionKind.Assassination, new[] { Ravilla }, null, targetChar: Nobody);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.False(w.Ch.IsDead(Nobody));
        Assert.Single(w.Events<CharacterInjured>());
    }

    // Standard victory needs a capture: an Abduction makes Nobody "captured" for the victory module (StandardVictory is on by default since 2026-10-16).
    [Fact]
    public void AbductionMakesAKeyCharacterCaptured()
    {
        var w = new MissionWorld();
        w.Agent(Ravilla, T, com: 100);
        w.Agent(Nobody, T, com: 0);
        w.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, null, targetChar: Nobody);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.True(w.Ch.IsCaptured(Nobody));
        Assert.True(w.Host.Victory.Rules.StandardVictory); // re-pinned 2026-10-16: standard victory default
    }

    // m5-missions MI56: a working Incite team (character leadership 70 + Strikers 50): incite_term = 7 + 5 = 12; Subdue the same members: -12; none: 0
    [Fact]
    public void MI56_IncidentTerms()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Hand, 30);
        w.Agent(Ledger, T, lead: 70);
        int g = w.Unit(Side.Collective, Guerrillas, T);
        Assert.Equal(0, w.M.IncidentTerm(T));
        w.Stub!.Expect(0, 99, 99, 99);
        w.Start(Side.Collective, MissionKind.InciteUprising, new[] { Ledger }, T, new[] { g });
        w.Game.Step();
        Assert.Equal(12, w.M.InciteTerm(T));
        Assert.Equal(12, w.M.IncidentTerm(T));
        // the support module's score adds it
        int baseScore = SupportModule.IncidentScore(w.Host.Support.Params, 1, 1, 70, 0, 0, false);
        Assert.Equal(baseScore + 12, w.Host.Support.IncidentScore(T, Side.Hand, 1, 1));

        // Subdue: Hand at a Hand system in uprising, Eighty-Eight (leadership 70) + Closers (50)
        var s = new MissionWorld();
        s.Hold(X, Side.Hand, 80);
        s.Game.Step();
        s.Agent(EightyEight, X, lead: 70);
        int c = s.Unit(Side.Hand, Commandos, X);
        s.Start(Side.Hand, MissionKind.SubdueUprising, new[] { EightyEight }, X, new[] { c });
        s.Game.Step();
        Assert.Equal(-12, s.M.SubdueTerm(X));
        Assert.Equal(0, s.M.IncidentTerm(T));
    }
}
