using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-missions §Targets (Where a leader target is sought), tests MI73 to MI77: an Abduction or Assassination order that names the system where
// the side last saw the leader flies there and checks only what the side knows (switch CharacterTargetWhereSeen, DEFAULT on).
public class WhereSeenTests
{
    // Ravilla (Hand, combat 100) at H; the Collective leader stands at `at`.
    private static MissionWorld World(int target, string at, MissionParams? p = null)
    {
        var w = new MissionWorld(p);
        w.Agent(Ravilla, H, com: 100);
        w.Agent(target, at);
        return w;
    }

    private static MissionGroup Started(MissionWorld w) => w.M.Get(w.LastMission)!;

    // m5-missions MI73: the leader is still where seen: the order flies to that system and the team lands and works
    [Fact]
    public void MI73_LeaderStillThereIsWorkedOn()
    {
        var w = World(FortyOne, T);
        long s = w.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, T, targetChar: FortyOne);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        Assert.Equal(T, Started(w).Target);
        w.RunUntilWorking(w.LastMission);
        Assert.Equal(MissionPhase.Working, w.M.Get(w.LastMission)!.Phase);
    }

    // m5-missions MI74: the leader has moved (stands at X, seen at T): the order is accepted (no refusal tells the side), the team flies to T,
    // and the mission ends TargetInvalid on landing with no roll
    [Fact]
    public void MI74_LeaderWhoMovedEndsTargetInvalidOnLanding()
    {
        var w = World(FortyOne, X);
        long s = w.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, T, targetChar: FortyOne);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        Assert.Equal(T, Started(w).Target);
        w.Run(10);
        var end = w.Events<MissionEnded>().Single();
        Assert.Equal((T, MissionOutcome.TargetInvalid), (end.Target, end.Outcome));
        Assert.Empty(w.Events<MissionMemberRoll>());
    }

    // m5-missions MI75: a key target riding a fleet: a where-seen order is not refused TargetProtected at order time (that would tell the side
    // the leader is aboard); the gate still ends the mission at the iteration start. An order with no system is refused as before (KT1).
    [Fact]
    public void MI75_KeyGateWaitsForTheIterationStart()
    {
        var w = World(Nobody, T, new MissionParams { MissionGuardChecks = false }); // no guard checks: the escort's ships would catch the team on arrival
        int f = w.Host.Fleets.AddFleet(Side.Collective, "Escort", new[] { "Mandate" }, T);
        w.Ch.Get(Nobody)!.AttachedFleet = f;
        long noSystem = w.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, null, targetChar: Nobody);
        w.Game.Step();
        Assert.Equal(MissionReject.TargetProtected, w.Reason(noSystem));
        long s = w.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, T, targetChar: Nobody);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        w.Run(20);
        Assert.Equal(MissionOutcome.TargetInvalid, w.Events<MissionEnded>().Single().Outcome);
        Assert.Empty(w.Events<MissionMemberRoll>());
    }

    // m5-missions MI76: switch off: the named system is ignored and the team flies to where the leader is at order time (the sheet's earlier rule)
    [Fact]
    public void MI76_SwitchOffFliesToTheTruePosition()
    {
        var w = World(FortyOne, X, new MissionParams { CharacterTargetWhereSeen = false });
        long s = w.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, T, targetChar: FortyOne);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        Assert.Equal(X, Started(w).Target);
    }

    // m5-missions MI77: an order with no system (the AI's) still flies to the true position; a leader who is the side's own prisoner is refused
    [Fact]
    public void MI77_NoSystemAndOwnPrisoner()
    {
        var w = World(FortyOne, X);
        w.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, null, targetChar: FortyOne);
        w.Game.Step();
        Assert.Equal(X, Started(w).Target);

        var p = World(FortyOne, T);
        p.Ch.Capture(null, p.Ch.Get(FortyOne)!, Side.Hand, T, CharacterCause.Debug);
        long s = p.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, T, targetChar: FortyOne);
        p.Game.Step();
        Assert.Equal(MissionReject.BadTarget, p.Reason(s));
    }

    // Lying in wait (m5-missions §Targets, Paul 2026-11-28): Forty-One leaves X for T (7 ticks); a Hand espionage success on T the next day
    // records the inbound sighting (m5-fog-and-hq §N6d); then Ravilla's Abduction names T and lands before him. Returns the world, the reach day.
    private static (MissionWorld W, int Reach) Ambush(MissionParams? p = null, bool spy = true)
    {
        var w = World(FortyOne, X, p);
        w.Submit(s => new MoveCharacterOrder(s, Side.Collective, FortyOne, T));
        w.Game.Step();
        bool done = !spy;
        w.Game.RegisterHook("6.8", ctx => { if (done) return; done = true; w.Host.Fog.EspionageSuccess(ctx, Side.Hand, T); });
        w.Game.Step();
        int reach = spy ? w.Host.Fog.InboundReachDay(Side.Hand, FortyOne, T)!.Value : 0;
        w.Start(Side.Hand, MissionKind.Abduction, new[] { Ravilla }, T, targetChar: FortyOne);
        w.Game.Step();
        return (w, reach);
    }

    // m5-missions MI78: the team lands before the leader: no TargetInvalid, the first timer counts from his reach day (one timer draw), and the
    // first iteration runs with him there
    [Fact]
    public void MI78_TeamLiesInWaitForTheLeaderOnHisWayIn()
    {
        var (w, reach) = Ambush();
        int mission = w.LastMission;
        w.RunUntilWorking(mission);
        var landed = w.Events<MissionLanded>().Single();
        Assert.True(landed.Day < reach, $"landed {landed.Day}, reach {reach}");
        Assert.Equal(reach + w.M.Data.Get(MissionKind.Abduction).TimerMin, landed.Due); // the stub answers 0 for the spread
        Assert.Empty(w.Events<MissionEnded>());
        w.RunIteration(mission);
        Assert.NotEmpty(w.Events<MissionMemberRoll>());
        Assert.DoesNotContain(w.Events<MissionEnded>(), e => e.Outcome == MissionOutcome.TargetInvalid);
    }

    // m5-missions MI79: the reach day too far after the landing, no inbound sighting, or AmbushWaitMaxDays 0: TargetInvalid on landing, no roll
    [Fact]
    public void MI79_NoWaitWithoutASightingOrBeyondTheMax()
    {
        var cases = new[] { Ambush(new MissionParams { AmbushWaitMaxDays = 1 }), Ambush(spy: false), Ambush(new MissionParams { AmbushWaitMaxDays = 0 }) };
        foreach (var (w, reach) in cases)
        {
            w.Run(10);
            var end = w.Events<MissionEnded>().Single();
            Assert.Equal((T, MissionOutcome.TargetInvalid), (end.Target, end.Outcome));
            Assert.Empty(w.Events<MissionLanded>());
            Assert.Empty(w.Events<MissionMemberRoll>());
            if (reach > 0) Assert.True(reach - end.Day > 1, $"ended {end.Day}, reach {reach}"); // the first case is beyond its max of 1
        }
    }
}
