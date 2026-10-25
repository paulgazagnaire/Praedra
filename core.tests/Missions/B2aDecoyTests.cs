using Praedra.Core.Clock;
using Praedra.Core.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// B2a-decoys-and-detection tests B2a-1 to B2a-4 (B2a-5: the existing detection tests MI2 to MI10 run unchanged except MI8's below-row value).
// B2d: the team flies in from H and the guards are Hand Speculators on the planet (special forces guard a planet target; detection 0).
// rule: m5-missions §3 decoy pass and detection walk, §Mission tables (Lookup).
public class B2aDecoyTests
{
    // B2a-1: FOILTB below the first row is the first row's value (99), through the common lookup
    [Theory]
    [InlineData(-21)]
    [InlineData(-100)]
    public void B2a1_FoilBelowFirstRowIs99(int x) => Assert.Equal(99, new MissionWorld().M.FoilChance(x));

    // B2a-2: one guard (a Speculator, detection 0), two decoys each with p = 50 (synthetic decoy table: 50 from x = 0; Wreckers espionage 60
    // and Listeners espionage 70 give x = 60 and 70): exactly two draws, the pick then the roll
    [Fact]
    public void B2a2_TwoDecoysOnePickOneRoll()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Hand, 0);
        w.Agent(Ledger, H, esp: 50);
        int d1 = w.Unit(Side.Collective, Infiltrators, H), d2 = w.Unit(Side.Collective, Listeners, H);
        w.Unit(Side.Hand, Probe, T);
        w.Stub!.Expect(0, 99, Bt(3, 50, 99, 99)); // B2b: three betrayal draws (Ledger and the two decoys) open the check; then the roll, the duel (B2d), the walk
        w.Start(Side.Collective, MissionKind.Espionage, new[] { Ledger }, T, decoys: new[] { d1, d2 });
        w.Game.Step();
        w.RunUntilWorking(w.LastMission);
        int from = w.Stub.Requests.Count;
        w.RunIteration(w.LastMission);
        var req = w.RequestsSince(from);
        Assert.Equal(new[] { (0, 1), (0, 99) }, req.Skip(3).Take(2));
        var roll = Assert.Single(w.Events<MissionDecoyRoll>());
        Assert.Equal((50, 50, false), (roll.Chance, roll.Roll, roll.Decoyed));
        Assert.Single(w.Events<MissionDetectionRoll>()); // the failed decoy duels and leaves (B2d); the guard is walked
        Assert.Single(w.Events<MissionDuel>());
    }

    // B2a-2: over 10,000 seeded trials the guard is distracted 50 percent of the time (+- 1.5), not 75 (every decoy tried)
    [Fact]
    public void B2a2_DistractionRateIsOneDecoysChance()
    {
        const int trials = 10_000;
        int distracted = 0;
        for (int s = 1; s <= trials; s++)
        {
            var w = new MissionWorld(decoys: true, seed: (ulong)s);
            w.Hold(T, Side.Hand, 0);
            w.Agent(Ledger, H, esp: 50);
            int d1 = w.Unit(Side.Collective, Infiltrators, H), d2 = w.Unit(Side.Collective, Listeners, H);
            w.Unit(Side.Hand, Probe, T);
            w.Start(Side.Collective, MissionKind.Espionage, new[] { Ledger }, T, decoys: new[] { d1, d2 });
            w.Game.Step();
            w.RunIteration(w.LastMission);
            var rolls = w.Events<MissionDecoyRoll>();
            Assert.True(rolls.Count >= 1);
            if (rolls[0].Decoyed) distracted++;
        }
        Assert.InRange(distracted * 1000 / trials, 485, 515);
    }

    // B2a-3: one decoy with p = 100 (synthetic decoy table), three defenders: three draws in all (no pick draw for n = 1), every defender distracted
    // by the same decoy, and the walk makes no draw
    [Fact]
    public void B2a3_OneDecoyDistractsEveryDefender()
    {
        var w = new MissionWorld(decoyTable: new[] { new TableRow(-1000, 100) });
        w.Hold(T, Side.Hand, 0);
        w.Agent(Ledger, H, esp: 50);
        int inf = w.Unit(Side.Collective, Infiltrators, H);
        w.Unit(Side.Hand, Probe, T); w.Unit(Side.Hand, Probe, T); w.Unit(Side.Hand, Probe, T);
        w.Stub!.Expect(0, 99, Bt(2, 99, 99, 99, 0)); // B2b: two betrayal draws (Ledger, the decoy) open the check
        w.Start(Side.Collective, MissionKind.Espionage, new[] { Ledger }, T, decoys: new[] { inf });
        w.Game.Step();
        w.RunUntilWorking(w.LastMission);
        int from = w.Stub.Requests.Count;
        w.RunIteration(w.LastMission);
        var rolls = w.Events<MissionDecoyRoll>();
        Assert.Equal(3, rolls.Count);
        Assert.All(rolls, r => Assert.Equal((inf, 100, true), (r.Decoy, r.Chance, r.Decoyed)));
        Assert.Empty(w.Events<MissionDetectionRoll>());
        var req = w.RequestsSince(from);
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99) }, req.Skip(2).Take(3));
        Assert.Equal(6, req.Count(r => r == (0, 99))); // two betrayal, three decoy, one success roll: the walk makes no draw
        Assert.Single(w.Events<MissionMemberRoll>()); // the last draw is Ledger's success roll, not a detection
    }

    // B2a-4: a working mission whose non-decoy members are all gone (the team's Listeners removed by the test), decoys left, defenders present:
    // decoy draws happen, no detection draw, never foiled by the walk. The day Step prunes gone members first and ends an emptied team Cancelled
    // (m5-missions DEFAULT, before the decoy pass), so the test runs the pass and walk directly on the group.
    [Fact]
    public void B2a4_OnlyDecoysLeftIsNeverDetected()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Hand, 0);
        int member = w.Unit(Side.Collective, Listeners, H), dec = w.Unit(Side.Collective, Infiltrators, H);
        w.Unit(Side.Hand, Probe, T); w.Unit(Side.Hand, Probe, T);
        var seq = w.Start(Side.Collective, MissionKind.Espionage, Array.Empty<int>(), T, sfs: new[] { member }, decoys: new[] { dec });
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        var m = w.M.Get(w.LastMission)!;
        for (int i = 0; i < 10 && m.Phase != MissionPhase.Working; i++) w.Game.Step();
        Assert.Equal(MissionPhase.Working, m.Phase);
        w.Host.Economy.RemoveObject(member);
        w.Stub!.Expect(0, 99, 0, 99, 99);
        int from = w.Stub.Requests.Count;
        var detectedBy = w.M.Walk(new StepContext(w.Game, w.Game.Day), m, w.Game.Day + 1);
        Assert.Null(detectedBy);
        // the first guard decoyed, the second not: its escape duel (B2d) follows and the decoy leaves; nothing else
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99) }, w.RequestsSince(from));
        Assert.Equal(new[] { true, false }, w.Events<MissionDecoyRoll>().Select(r => r.Decoyed));
        Assert.Single(w.Events<MissionDuel>());
        Assert.Empty(w.Events<MissionDetectionRoll>());
    }

    // B2a-4 (the day Step): the same group in a Step is pruned and ended Cancelled before any decoy or detection draw (m5-missions DEFAULT)
    [Fact]
    public void B2a4_EmptiedTeamIsCancelledByTheStep()
    {
        var w = new MissionWorld(decoys: true);
        w.Hold(T, Side.Hand, 0);
        int member = w.Unit(Side.Collective, Listeners, H), dec = w.Unit(Side.Collective, Infiltrators, H);
        w.Unit(Side.Hand, Probe, T);
        w.Start(Side.Collective, MissionKind.Espionage, Array.Empty<int>(), T, sfs: new[] { member }, decoys: new[] { dec });
        w.Game.Step();
        w.Host.Economy.RemoveObject(member);
        w.RunIteration(w.LastMission);
        Assert.Empty(w.Events<MissionDecoyRoll>());
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Empty(w.Events<MissionFoiled>());
    }
}
