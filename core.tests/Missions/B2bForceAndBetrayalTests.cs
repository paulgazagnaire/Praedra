using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Missions;
using static Rebellion.Core.Tests.Missions.MissionWorld;

namespace Rebellion.Core.Tests.Missions;

// B2b-force-sensing-and-betrayal tests B2b-1 to B2b-11. rule: m5-missions §3 Force sensing, Betrayal; m5-characters-and-force §Loyalty.
// An Alliance Espionage team at T (Empire-held) with no defenders unless a test adds one: each check is sensing, betrayal, then the success rolls.
public class B2bForceAndBetrayalTests
{
    private const int Crix = 837, Rieekan = 838; // Alliance characters who can betray (nobetray 0)

    private static CharacterRecord Force(MissionWorld w, int id, int force)
    {
        var c = w.Ch.Get(id)!;
        c.ForceUser = force > 0; c.ForceBase = force; c.ForceAcquired = 0;
        return c;
    }

    // Agents at T with the given Force (every skill 0 except espionage 50).
    private static MissionWorld World(bool decoys = false, MissionParams? p = null, params (int Id, int Force)[] agents)
    {
        var w = new MissionWorld(p, decoys);
        w.Hold(T, Side.Empire, 0);
        foreach (var (id, f) in agents) { w.Agent(id, T, esp: 50); Force(w, id, f); }
        return w;
    }

    private static int StartEspionage(MissionWorld w, int[] team, int[]? sfs = null, int[]? decoys = null, int[]? decoyChars = null)
    {
        long seq = w.Start(Side.Alliance, MissionKind.Espionage, team, T, sfs, decoys, decoyChars: decoyChars);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        return w.LastMission;
    }

    // B2b-1: p = Force + Force - 100: Luke (50) vs Vader (120) is 70; 30 vs 60 is -10 and no draw; Force 0 vs 120 is 0 and no draw
    [Fact]
    public void B2b1_SenseChance()
    {
        var m = new MissionWorld().M;
        Assert.Equal((70, -10, 0), (m.SenseChance(50, 120), m.SenseChance(30, 60), m.SenseChance(0, 120)));

        var w = World(agents: new[] { (Luke, 50), (Vader, 120) });
        w.Stub!.Expect(0, 99, 99);
        int id = StartEspionage(w, new[] { Luke });
        w.RunIteration(id);
        Assert.Equal((Luke, Vader, 70, 99, false), w.Events<MissionForceSenseRoll>().Select(e => (e.Member, e.Senser, e.Chance, e.Roll, e.Sensed)).Single());

        foreach (var (member, senser) in new[] { (30, 60), (0, 120) })
        {
            var v = World(agents: new[] { (Leia, member), (Vader, senser) });
            int mid = StartEspionage(v, new[] { Leia });
            int from = v.Stub!.Requests.Count;
            v.RunIteration(mid);
            Assert.Empty(v.Events<MissionForceSenseRoll>());
            Assert.Equal(new[] { (0, 99), (0, 99) }, v.RequestsSince(from)); // Leia's betrayal draw and success roll only
        }
    }

    // B2b-2: the senser filter: an enemy at Force 59, one at 60, an own-side character at 150, a captured enemy at 120: only the enemy at 60 senses
    [Fact]
    public void B2b2_SenserFilter()
    {
        var w = World(agents: new[] { (Luke, 50), (Piett, 59), (Veers, 60), (MonMothma, 150), (Palpatine, 120) });
        w.Ch.Capture(null, w.Ch.Get(Palpatine)!, Side.Alliance, T, CharacterCause.Debug);
        w.Stub!.Expect(0, 99, 99);
        int id = StartEspionage(w, new[] { Luke });
        Assert.Equal(new[] { Veers }, w.M.Sensers(w.M.Get(id)!).Select(c => c.Id));
        w.RunIteration(id);
        Assert.Equal((Veers, 10), w.Events<MissionForceSenseRoll>().Select(e => (e.Senser, e.Chance)).Single());
    }

    // B2b-3: the member filter: a member with Force 0 and a removed member with Force 80 (captured after the start) are not rolled
    [Fact]
    public void B2b3_MemberFilter()
    {
        var w = World(agents: new[] { (Han, 0), (Lando, 80), (Vader, 120) });
        int id = StartEspionage(w, new[] { Han, Lando });
        w.Ch.Capture(null, w.Ch.Get(Lando)!, Side.Empire, T, CharacterCause.Debug);
        w.RunIteration(id);
        Assert.Empty(w.Events<MissionForceSenseRoll>());
        Assert.Single(w.Events<MissionBetrayalRoll>()); // Han only: Lando left the group
    }

    // B2b-4: a decoy (Force 80) sensed by Vader (p = 100): not foiled; the decoy pass and the detection walk still run
    [Fact]
    public void B2b4_SensedDecoyFoilsNothing()
    {
        var w = World(decoys: true, p: new MissionParams { DecoysFromSpecialForcesOnly = false }, agents: new[] { (Leia, 0), (Lando, 80), (Vader, 120) });
        int guard = w.Unit(Side.Empire, ImpFleet, T);
        w.Stub!.Expect(0, 99, 0, 99, 99, 99, 99, 99); // sensed (0 < 100), two betrayal draws, decoy roll fails, walk misses, success roll fails
        int id = StartEspionage(w, new[] { Leia }, decoyChars: new[] { Lando });
        w.RunIteration(id);
        var sensed = w.Events<MissionForceSensed>().Single();
        Assert.Equal((Lando, true, Vader, false), (sensed.Member, sensed.Decoy, sensed.Senser, sensed.Foiled));
        Assert.Equal(2, w.Events<MissionBetrayalRoll>().Count);
        Assert.Equal(guard, w.Events<MissionDecoyRoll>().Single().Defender);
        Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Empty(w.Events<MissionFoiled>());
        Assert.Equal((Lando, Vader), (w.Ch.Get(Vader)!.DiscoveredForceUser, w.Ch.Get(Lando)!.DiscoveredForceUser));
        Assert.Single(w.Events<ForceUserDiscovered>());
    }

    // B2b-5: a team member sensed (p = 100): foiled, cause ForceSensed; no betrayal, decoy or detection draw in that check
    [Fact]
    public void B2b5_SensedTeamMemberFoils()
    {
        var w = World(decoys: true, agents: new[] { (Leia, 80), (Vader, 120) });
        w.Unit(Side.Empire, ImpFleet, T);
        int inf = w.Unit(Side.Alliance, Infiltrators, T);
        int id = StartEspionage(w, new[] { Leia }, decoys: new[] { inf });
        int from = w.Stub!.Requests.Count;
        w.RunIteration(id);
        Assert.Equal(new[] { (0, 99) }, w.RequestsSince(from)); // the sensing roll only
        var f = w.Events<MissionFoiled>().Single();
        Assert.Equal((MissionFoilCause.ForceSensed, Vader, 0), (f.Cause, f.Character, f.Defender));
        Assert.Empty(w.Events<MissionBetrayalRoll>());
        Assert.Empty(w.Events<MissionDecoyRoll>());
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Equal(MissionOutcome.Foiled, w.Events<MissionEnded>().Single().Outcome);
    }

    // B2b-6: sensers Vader (120), X = Piett (70), Z = Veers (80); group Force users Luke (50), Y = Wedge (40), W = Lando (45). By hand:
    //   Vader first: Luke p 70 roll 99 (miss); pick (0..1) = 1 takes Lando: p 65 roll 10 (hit, foiled; Vader's discovered user is set: he stops).
    //   pick (0..1) between Piett and Veers = 0: Piett: Luke p 20 roll 5 (hit: he stops).
    //   Veers (last, no pick): Luke p 30 roll 99; pick (0..1) = 1 takes Lando: p 25 roll 99; Wedge (last, no pick): p 20 roll 99.
    // Foiled, so no betrayal draw.
    [Fact]
    public void B2b6_SenseOrderAndPicks()
    {
        var w = World(agents: new[] { (Luke, 50), (Wedge, 40), (Lando, 45), (Vader, 120), (Piett, 70), (Veers, 80) });
        w.Stub!.Expect(0, 1, 1, 0, 1).Expect(0, 99, 99, 10, 5, 99, 99, 99);
        int id = StartEspionage(w, new[] { Luke, Wedge, Lando });
        int from = w.Stub.Requests.Count;
        w.RunIteration(id);
        Assert.Equal(new[] { (0, 99), (0, 1), (0, 99), (0, 1), (0, 99), (0, 99), (0, 1), (0, 99), (0, 99) }, w.RequestsSince(from));
        Assert.Equal(new[] { (Luke, Vader, 70, false), (Lando, Vader, 65, true), (Luke, Piett, 20, true), (Luke, Veers, 30, false), (Lando, Veers, 25, false), (Wedge, Veers, 20, false) },
            w.Events<MissionForceSenseRoll>().Select(e => (e.Member, e.Senser, e.Chance, e.Sensed)));
        Assert.Equal((MissionFoilCause.ForceSensed, Vader), (w.Events<MissionFoiled>().Single().Cause, w.Events<MissionFoiled>().Single().Character));
        Assert.Equal((Lando, Vader, Luke, Piett), (w.Ch.Get(Vader)!.DiscoveredForceUser, w.Ch.Get(Lando)!.DiscoveredForceUser,
            w.Ch.Get(Piett)!.DiscoveredForceUser, w.Ch.Get(Luke)!.DiscoveredForceUser));
        Assert.Empty(w.Events<MissionBetrayalRoll>());
    }

    // B2b-7: a senser who already has a discovered Force user makes exactly one sensing roll per check, against the first member (Luke)
    [Fact]
    public void B2b7_DiscoveredSenserRollsOncePerCheck()
    {
        var w = World(agents: new[] { (Luke, 50), (Wedge, 40), (Lando, 45), (Vader, 120) });
        w.Ch.Get(Vader)!.DiscoveredForceUser = Palpatine;
        w.Stub!.Expect(0, 99, 99);
        int id = StartEspionage(w, new[] { Luke, Wedge, Lando });
        w.RunIteration(id);
        Assert.Equal((Luke, Vader), w.Events<MissionForceSenseRoll>().Select(e => (e.Member, e.Senser)).Single());
        Assert.Equal(0, w.Stub.Count(0, 1)); // no member pick: he stopped after Luke
    }

    // B2b-8: three members at loyalty 100: three betrayal draws (chance 0), no traitor, no identification draw
    [Fact]
    public void B2b8_LoyalMembersDrawButNeverBetray()
    {
        var w = World(agents: new[] { (Leia, 0), (Han, 0), (Lando, 0) });
        int id = StartEspionage(w, new[] { Leia, Han, Lando });
        int from = w.Stub!.Requests.Count;
        w.RunIteration(id);
        var rolls = w.Events<MissionBetrayalRoll>();
        Assert.Equal(new[] { (Leia, 0, false), (Han, 0, false), (Lando, 0, false) }, rolls.Select(r => (r.Member, r.Chance, r.Betrays)));
        Assert.Empty(w.Events<MissionBetrayed>());
        Assert.Empty(w.Events<TraitorIdentified>());
        Assert.Equal(6, w.RequestsSince(from).Count); // three betrayal draws, three success rolls
    }

    // B2b-9: member 1 (Leia) at loyalty 0 betrays on the first draw: foiled, cause Betrayed; identification draws for member 2 (Han, Force 0: a draw
    // even at 0) and member 3 (Wedge, Force 100: identifies), then the walk stops
    [Fact]
    public void B2b9_TraitorFoilsAndIsIdentified()
    {
        var w = World(agents: new[] { (Leia, 0), (Han, 0), (Wedge, 100) });
        w.Ch.Get(Leia)!.Loyalty = 0;
        w.Stub!.Expect(0, 99, 0, 0, 50);
        int id = StartEspionage(w, new[] { Leia, Han, Wedge });
        int from = w.Stub.Requests.Count;
        w.RunIteration(id);
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99) }, w.RequestsSince(from));
        Assert.Equal((Leia, 100, true), w.Events<MissionBetrayalRoll>().Select(r => (r.Member, r.Chance, r.Betrays)).Single());
        Assert.Equal(Leia, w.Events<MissionBetrayed>().Single().Member);
        var f = w.Events<MissionFoiled>().Single();
        Assert.Equal((MissionFoilCause.Betrayed, Leia), (f.Cause, f.Character));
        Assert.Equal((Wedge, Leia), w.Events<TraitorIdentified>().Select(e => (e.Identifier, e.Traitor)).Single());
        Assert.Equal((0, Leia), (w.Ch.Get(Han)!.IdentifiedTraitor, w.Ch.Get(Wedge)!.IdentifiedTraitor));
        Assert.Empty(w.Events<MissionMemberRoll>());
    }

    // B2b-10: ShiftLoyalty(1, -5): an Alliance character at 100 -> 95, an Imperial at 98 -> 100 (clamped), Luke unchanged (unable to betray),
    // an Alliance character at 3 -> 0 (clamped)
    [Fact]
    public void B2b10_ShiftLoyalty()
    {
        var w = World(agents: new[] { (Lando, 0), (Piett, 0), (Luke, 50), (Crix, 0) });
        w.Ch.Get(Piett)!.Loyalty = 98; w.Ch.Get(Crix)!.Loyalty = 3;
        w.Ch.ShiftLoyalty(new StepContext(w.Game, w.Game.Day), CharacterModule.SideNumberAlliance, -5);
        Assert.Equal((95, 100, 100, 0), (w.Ch.Get(Lando)!.Loyalty, w.Ch.Get(Piett)!.Loyalty, w.Ch.Get(Luke)!.Loyalty, w.Ch.Get(Crix)!.Loyalty));
        Assert.Equal(new[] { (Lando, 100, 95), (Piett, 98, 100), (Crix, 3, 0) }, w.Events<LoyaltyChanged>().Select(e => (e.Character, e.From, e.To)).OrderBy(e => e.Character != Lando).ThenBy(e => e.Character != Piett));
    }

    // B2b-11: ShiftLoyalty(3, 5) (neutral) and ShiftLoyalty(1, 0): nothing changes, no event
    [Fact]
    public void B2b11_ShiftLoyaltyNoOps()
    {
        var w = World(agents: new[] { (Lando, 0), (Piett, 0), (Rieekan, 0) });
        w.Ch.Get(Rieekan)!.Loyalty = 50;
        var ctx = new StepContext(w.Game, w.Game.Day);
        w.Ch.ShiftLoyalty(ctx, 3, 5);
        w.Ch.ShiftLoyalty(ctx, CharacterModule.SideNumberAlliance, 0);
        Assert.Equal((100, 100, 50), (w.Ch.Get(Lando)!.Loyalty, w.Ch.Get(Piett)!.Loyalty, w.Ch.Get(Rieekan)!.Loyalty));
        Assert.Empty(w.Events<LoyaltyChanged>());
    }

    // B2b: the new character keys are saved (omitted while 0) and survive a reload
    [Fact]
    public void DiscoveredKeysAreSaved()
    {
        var w = World(agents: new[] { (Lando, 0) });
        Assert.DoesNotContain("DiscoveredForceUser", w.Host.Save());
        w.Ch.Get(Lando)!.DiscoveredForceUser = Vader; w.Ch.Get(Lando)!.IdentifiedTraitor = Crix;
        string hash = w.Host.StateHash();
        w.Reload();
        Assert.Equal((Vader, Crix), (w.Ch.Get(Lando)!.DiscoveredForceUser, w.Ch.Get(Lando)!.IdentifiedTraitor));
        Assert.Equal(hash, w.Host.StateHash());
    }
}
