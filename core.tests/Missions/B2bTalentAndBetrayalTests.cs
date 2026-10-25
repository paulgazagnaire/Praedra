using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// B2b-force-sensing-and-betrayal tests B2b-1 to B2b-11. rule: m5-missions §3 Talent sensing, Betrayal; m5-characters-and-talent §Loyalty.
// A Collective Espionage team flies from H to T (Hand-held) with no guards unless a test adds one: the job check at T is sensing, betrayal, then
// the success rolls. B2d: the team starts at H so the leaving and arriving checks have no sensers (sensers stand in the check's scopes).
public class B2bTalentAndBetrayalTests
{
    private const int Crix = 837, Halvorsen = 838; // Collective characters who can betray (nobetray 0)

    private static CharacterRecord Talent(MissionWorld w, int id, int force)
    {
        var c = w.Ch.Get(id)!;
        c.TalentHolder = force > 0; c.TalentExperience = force; c.TalentTraining = 0;
        return c;
    }

    // Agents with the given Talent (every skill 0 except espionage 50): Collective characters at H, the others at T.
    private static MissionWorld World(bool decoys = false, MissionParams? p = null, params (int Id, int Talent)[] agents)
    {
        var w = new MissionWorld(p, decoys);
        w.Hold(T, Side.Hand, 0);
        foreach (var (id, f) in agents) { var c = w.Agent(id, T, esp: 50); if (c.Side == Side.Collective) w.Ch.Place(id, H); Talent(w, id, f); }
        return w;
    }

    private static int StartEspionage(MissionWorld w, int[] team, int[]? sfs = null, int[]? decoys = null, int[]? decoyChars = null)
    {
        long seq = w.Start(Side.Collective, MissionKind.Espionage, team, T, sfs, decoys, decoyChars: decoyChars);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        w.RunUntilWorking(w.LastMission); // B2d: the team flies in from H; landing (and its timer draw) comes before the job check
        return w.LastMission;
    }

    // B2b-1: p = Talent + Talent - 100: Nobody (50) vs Ravilla (120) is 70; 30 vs 60 is -10 and no draw; Talent 0 vs 120 is 0 and no draw
    [Fact]
    public void B2b1_SenseChance()
    {
        var m = new MissionWorld().M;
        Assert.Equal((70, -10, 0), (m.SenseChance(50, 120), m.SenseChance(30, 60), m.SenseChance(0, 120)));

        var w = World(agents: new[] { (Nobody, 50), (Ravilla, 120) });
        w.Stub!.Expect(0, 99, 99);
        int id = StartEspionage(w, new[] { Nobody });
        w.RunIteration(id);
        Assert.Equal((Nobody, Ravilla, 70, 99, false), w.Events<MissionTalentSenseRoll>().Select(e => (e.Member, e.Senser, e.Chance, e.Roll, e.Sensed)).Single());

        foreach (var (member, senser) in new[] { (30, 60), (0, 120) })
        {
            var v = World(agents: new[] { (Ledger, member), (Ravilla, senser) });
            int mid = StartEspionage(v, new[] { Ledger });
            int from = v.Stub!.Requests.Count;
            v.RunIteration(mid);
            Assert.Empty(v.Events<MissionTalentSenseRoll>());
            Assert.Equal(new[] { (0, 99), (0, 99) }, v.RequestsSince(from)); // Ledger's betrayal draw and success roll only
        }
    }

    // B2b-2: the senser filter: an enemy at Talent 59, one at 60, an own-side character at 150, a captured enemy at 120: only the enemy at 60 senses
    [Fact]
    public void B2b2_SenserFilter()
    {
        var w = World(agents: new[] { (Nobody, 50), (Varo, 59), (EightyEight, 60), (MaeAbara, 150), (Chair, 120) });
        w.Ch.Capture(null, w.Ch.Get(Chair)!, Side.Collective, T, CharacterCause.Debug);
        w.Stub!.Expect(0, 99, 99);
        int id = StartEspionage(w, new[] { Nobody });
        Assert.Equal(new[] { EightyEight }, w.M.Sensers(w.M.Get(id)!).Select(c => c.Id));
        w.RunIteration(id);
        Assert.Equal((EightyEight, 10), w.Events<MissionTalentSenseRoll>().Select(e => (e.Senser, e.Chance)).Single());
    }

    // B2b-3: the member filter: a member with Talent 0 and a removed member with Talent 80 (captured after the start) are not rolled
    [Fact]
    public void B2b3_MemberFilter()
    {
        var w = World(agents: new[] { (Midwife, 0), (Foreman, 80), (Ravilla, 120) });
        int id = StartEspionage(w, new[] { Midwife, Foreman });
        w.Ch.Capture(null, w.Ch.Get(Foreman)!, Side.Hand, T, CharacterCause.Debug);
        w.RunIteration(id);
        Assert.Empty(w.Events<MissionTalentSenseRoll>());
        Assert.Single(w.Events<MissionBetrayalRoll>()); // Midwife only: Foreman left the group
    }

    // B2b-4: a decoy (Talent 80) sensed by Ravilla (p = 100): not foiled; the decoy pass and the detection walk still run
    [Fact]
    public void B2b4_SensedDecoyFoilsNothing()
    {
        var w = World(decoys: true, p: new MissionParams { DecoysFromSpecialForcesOnly = false }, agents: new[] { (Ledger, 0), (Foreman, 80), (Ravilla, 120) });
        int guard = w.Unit(Side.Hand, Probe, T); // B2d: a special force on the enemy-held planet guards a planet target
        // B2e moved: the mutual discovery makes Foreman meet Ravilla in the decoy pre-step (B2e §4) instead of rolling against the guard. Draws:
        // sensed (0 < 100), two betrayal draws, Foreman's duel against Ravilla (x = 0: 0 escapes, he leaves the group), his Hurt roll (combat 0: p = 100,
        // 99 hits; sizes 0 and 0), his death draw 99 (lives), Ravilla's HurtBy (p = 1: 99 misses), walk misses, success roll fails
        w.Stub!.Expect(0, 99, 0, 99, 99, 0, 99, 99, 99, 99, 99);
        int id = StartEspionage(w, new[] { Ledger }, decoyChars: new[] { Foreman });
        w.RunIteration(id);
        var sensed = w.Events<MissionTalentSensed>().Single();
        Assert.Equal((Foreman, true, Ravilla, false), (sensed.Member, sensed.Decoy, sensed.Senser, sensed.Foiled));
        Assert.Equal(2, w.Events<MissionBetrayalRoll>().Count);
        Assert.Empty(w.Events<MissionDecoyRoll>()); // Foreman left the group after his encounter
        var enc = w.Events<Praedra.Core.Characters.TalentEncounterResolved>().Single();
        Assert.Equal((Foreman, Ravilla, true), (enc.Member, enc.Other, enc.MemberEscaped));
        Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Empty(w.Events<MissionFoiled>());
        Assert.Equal((0, 0), (w.Ch.Get(Ravilla)!.DiscoveredTalentHolder, w.Ch.Get(Foreman)!.DiscoveredTalentHolder)); // cleared by the encounter
        Assert.Single(w.Events<TalentHolderDiscovered>());
        Assert.True(guard > 0);
    }

    // B2b-5: a team member sensed (p = 100): foiled, cause TalentSensed; no betrayal, decoy or detection draw in that check
    [Fact]
    public void B2b5_SensedTeamMemberFoils()
    {
        var w = World(decoys: true, agents: new[] { (Ledger, 80), (Ravilla, 120) });
        w.Unit(Side.Hand, Probe, T);
        int inf = w.Unit(Side.Collective, Infiltrators, H);
        int id = StartEspionage(w, new[] { Ledger }, decoys: new[] { inf });
        int from = w.Stub!.Requests.Count;
        w.RunIteration(id);
        // the sensing roll, then Ledger's escape duel: B2e moved, against Ravilla (the mutual discovery meets in an encounter, B2e §4), not the guard
        Assert.Equal(new[] { (0, 99), (0, 99) }, w.RequestsSince(from).Take(2));
        Assert.Equal((Ledger, Ravilla), w.Events<Praedra.Core.Characters.TalentEncounterResolved>().Select(e => (e.Member, e.Other)).Single());
        var f = w.Events<MissionFoiled>().Single();
        Assert.Equal((MissionFoilCause.TalentSensed, Ravilla, 0), (f.Cause, f.Character, f.Defender));
        Assert.Empty(w.Events<MissionBetrayalRoll>());
        Assert.Empty(w.Events<MissionDecoyRoll>());
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Equal(MissionOutcome.Foiled, w.Events<MissionEnded>().Single().Outcome);
    }

    // B2b-6: sensers Ravilla (120), X = Admiral Decimus Varo (70), Z = Eighty-Eight (80); group talented characters Nobody (50), Y = Forty-One (40), W = Foreman (45). By hand:
    //   Ravilla first: Nobody p 70 roll 99 (miss); pick (0..1) = 1 takes Foreman: p 65 roll 10 (hit, foiled; Ravilla's discovered user is set: he stops).
    //   pick (0..1) between Admiral Decimus Varo and Eighty-Eight = 0: Admiral Decimus Varo: Nobody p 20 roll 5 (hit: he stops).
    //   Eighty-Eight (last, no pick): Nobody p 30 roll 99; pick (0..1) = 1 takes Foreman: p 25 roll 99; Forty-One (last, no pick): p 20 roll 99.
    // Foiled, so no betrayal draw.
    [Fact]
    public void B2b6_SenseOrderAndPicks()
    {
        var w = World(agents: new[] { (Nobody, 50), (FortyOne, 40), (Foreman, 45), (Ravilla, 120), (Varo, 70), (EightyEight, 80) });
        w.Stub!.Expect(0, 1, 1, 0, 1).Expect(0, 99, 99, 10, 5, 99, 99, 99);
        int id = StartEspionage(w, new[] { Nobody, FortyOne, Foreman });
        int from = w.Stub.Requests.Count;
        w.RunIteration(id);
        // the sensing draws; B2e: the two mutual discoveries then meet in Talent encounters (B2e §4), whose draws follow
        Assert.Equal(new[] { (0, 99), (0, 1), (0, 99), (0, 1), (0, 99), (0, 99), (0, 1), (0, 99), (0, 99) }, w.RequestsSince(from).Take(9));
        Assert.Equal(new[] { (Nobody, Ravilla, 70, false), (Foreman, Ravilla, 65, true), (Nobody, Varo, 20, true), (Nobody, EightyEight, 30, false), (Foreman, EightyEight, 25, false), (FortyOne, EightyEight, 20, false) },
            w.Events<MissionTalentSenseRoll>().Select(e => (e.Member, e.Senser, e.Chance, e.Sensed)));
        Assert.Equal((MissionFoilCause.TalentSensed, Ravilla), (w.Events<MissionFoiled>().Single().Cause, w.Events<MissionFoiled>().Single().Character));
        Assert.Equal(new[] { (Foreman, Ravilla), (Nobody, Varo) }, w.Events<TalentHolderDiscovered>().Select(e => (e.Member, e.Senser)));
        // B2e: Nobody meets Admiral Decimus Varo (team order: Nobody first, then Foreman meets Ravilla); the encounters clear the keys
        Assert.Equal(new[] { (Nobody, Varo), (Foreman, Ravilla) }, w.Events<Praedra.Core.Characters.TalentEncounterResolved>().Select(e => (e.Member, e.Other)));
        Assert.Equal((0, 0, 0, 0), (w.Ch.Get(Ravilla)!.DiscoveredTalentHolder, w.Ch.Get(Foreman)!.DiscoveredTalentHolder,
            w.Ch.Get(Varo)!.DiscoveredTalentHolder, w.Ch.Get(Nobody)!.DiscoveredTalentHolder));
        Assert.Empty(w.Events<MissionBetrayalRoll>());
    }

    // B2b-7: a senser who already has a discovered talented character makes exactly one sensing roll per check, against the first member (Nobody)
    [Fact]
    public void B2b7_DiscoveredSenserRollsOncePerCheck()
    {
        var w = World(agents: new[] { (Nobody, 50), (FortyOne, 40), (Foreman, 45), (Ravilla, 120) });
        w.Ch.Get(Ravilla)!.DiscoveredTalentHolder = Chair;
        w.Stub!.Expect(0, 99, 99);
        int id = StartEspionage(w, new[] { Nobody, FortyOne, Foreman });
        w.RunIteration(id);
        Assert.Equal((Nobody, Ravilla), w.Events<MissionTalentSenseRoll>().Select(e => (e.Member, e.Senser)).Single());
        Assert.Equal(0, w.Stub.Count(0, 1)); // no member pick: he stopped after Nobody
    }

    // B2b-8: three members at loyalty 100: three betrayal draws (chance 0), no traitor, no identification draw
    [Fact]
    public void B2b8_LoyalMembersDrawButNeverBetray()
    {
        var w = World(agents: new[] { (Ledger, 0), (Midwife, 0), (Foreman, 0) });
        int id = StartEspionage(w, new[] { Ledger, Midwife, Foreman });
        int from = w.Stub!.Requests.Count;
        w.RunIteration(id);
        var rolls = w.Events<MissionBetrayalRoll>();
        Assert.Equal(new[] { (Ledger, 0, false), (Midwife, 0, false), (Foreman, 0, false) }, rolls.Select(r => (r.Member, r.Chance, r.Betrays)));
        Assert.Empty(w.Events<MissionBetrayed>());
        Assert.Empty(w.Events<TraitorIdentified>());
        Assert.Equal(6, w.RequestsSince(from).Count); // three betrayal draws, three success rolls
    }

    // B2b-9: member 1 (Ledger) at loyalty 0 betrays on the first draw: foiled, cause Betrayed; identification draws for member 2 (Midwife, Talent 0: a draw
    // even at 0) and member 3 (Forty-One, Talent 100: identifies), then the walk stops
    [Fact]
    public void B2b9_TraitorFoilsAndIsIdentified()
    {
        var w = World(agents: new[] { (Ledger, 0), (Midwife, 0), (FortyOne, 100) });
        w.Ch.Get(Ledger)!.Loyalty = 0;
        w.Stub!.Expect(0, 99, 0, 0, 50);
        int id = StartEspionage(w, new[] { Ledger, Midwife, FortyOne });
        int from = w.Stub.Requests.Count;
        w.RunIteration(id);
        Assert.Equal(new[] { (0, 99), (0, 99), (0, 99) }, w.RequestsSince(from));
        Assert.Equal((Ledger, 100, true), w.Events<MissionBetrayalRoll>().Select(r => (r.Member, r.Chance, r.Betrays)).Single());
        Assert.Equal(Ledger, w.Events<MissionBetrayed>().Single().Member);
        var f = w.Events<MissionFoiled>().Single();
        Assert.Equal((MissionFoilCause.Betrayed, Ledger), (f.Cause, f.Character));
        Assert.Equal((FortyOne, Ledger), w.Events<TraitorIdentified>().Select(e => (e.Identifier, e.Traitor)).Single());
        Assert.Equal((0, Ledger), (w.Ch.Get(Midwife)!.IdentifiedTraitor, w.Ch.Get(FortyOne)!.IdentifiedTraitor));
        Assert.Empty(w.Events<MissionMemberRoll>());
    }

    // B2b-10: ShiftLoyalty(1, -5): a Collective character at 100 -> 95, a Hand at 98 -> 100 (clamped), Nobody unchanged (unable to betray),
    // a Collective character at 3 -> 0 (clamped)
    [Fact]
    public void B2b10_ShiftLoyalty()
    {
        var w = World(agents: new[] { (Foreman, 0), (Varo, 0), (Nobody, 50), (Crix, 0) });
        w.Ch.Get(Varo)!.Loyalty = 98; w.Ch.Get(Crix)!.Loyalty = 3;
        w.Ch.ShiftLoyalty(new StepContext(w.Game, w.Game.Day), CharacterModule.SideNumberCollective, -5);
        Assert.Equal((95, 100, 100, 0), (w.Ch.Get(Foreman)!.Loyalty, w.Ch.Get(Varo)!.Loyalty, w.Ch.Get(Nobody)!.Loyalty, w.Ch.Get(Crix)!.Loyalty));
        Assert.Equal(new[] { (Foreman, 100, 95), (Varo, 98, 100), (Crix, 3, 0) }, w.Events<LoyaltyChanged>().Select(e => (e.Character, e.From, e.To)).OrderBy(e => e.Character != Foreman).ThenBy(e => e.Character != Varo));
    }

    // B2b-11: ShiftLoyalty(3, 5) (neutral) and ShiftLoyalty(1, 0): nothing changes, no event
    [Fact]
    public void B2b11_ShiftLoyaltyNoOps()
    {
        var w = World(agents: new[] { (Foreman, 0), (Varo, 0), (Halvorsen, 0) });
        w.Ch.Get(Halvorsen)!.Loyalty = 50;
        var ctx = new StepContext(w.Game, w.Game.Day);
        w.Ch.ShiftLoyalty(ctx, 3, 5);
        w.Ch.ShiftLoyalty(ctx, CharacterModule.SideNumberCollective, 0);
        Assert.Equal((100, 100, 50), (w.Ch.Get(Foreman)!.Loyalty, w.Ch.Get(Varo)!.Loyalty, w.Ch.Get(Halvorsen)!.Loyalty));
        Assert.Empty(w.Events<LoyaltyChanged>());
    }

    // B2b: the new character keys are saved (omitted while 0) and survive a reload
    [Fact]
    public void DiscoveredKeysAreSaved()
    {
        var w = World(agents: new[] { (Foreman, 0) });
        Assert.DoesNotContain("DiscoveredTalentHolder", w.Host.Save());
        w.Ch.Get(Foreman)!.DiscoveredTalentHolder = Ravilla; w.Ch.Get(Foreman)!.IdentifiedTraitor = Crix;
        string hash = w.Host.StateHash();
        w.Reload();
        Assert.Equal((Ravilla, Crix), (w.Ch.Get(Foreman)!.DiscoveredTalentHolder, w.Ch.Get(Foreman)!.IdentifiedTraitor));
        Assert.Equal(hash, w.Host.StateHash());
    }
}
