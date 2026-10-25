using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-missions §4, §6a, tests MI66 to MI72: Paul's key-target protections with the Classic behaviour behind switches (KeyDuelCapture, 2026-10-18;
// Key-target protections, 2026-10-18): the cap roll on a key character's duel capture, the full guard set at a key target, base skills.
public class KeyTargetProtectionTests
{
    private const string Corvette = "Dodger";

    // The B2e-20 setup: Hand member on Diplomacy from H to Hand T, where a Collective corvette (Admiral Forty-One, combat 80) detects him at
    // the arrival walk (draw 0) and wins the duel (draw 99); the member's Hurt roll misses (99); then, under Capped, the cap roll.
    private static MissionWorld DuelWorld(int member, MissionParams? p, params int[] after)
    {
        var w = new MissionWorld(p);
        w.Hold(H, Side.Hand, 0); w.Hold(T, Side.Hand, 0);
        w.Agent(member, H, dip: 50);
        int fleet = w.Host.Fleets.AddFleet(Side.Collective, "F", new[] { Corvette }, T);
        w.Agent(FortyOne, T, com: 80); w.Ch.SetPost(FortyOne, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, new[] { 0, 99, 99 }.Concat(after).ToArray());
        w.Start(Side.Hand, MissionKind.Diplomacy, new[] { member }, T);
        w.Run(5);
        return w;
    }

    // m5-missions MI66: under Capped (DEFAULT) The Chair (cap 2) who loses the duel is captured only if rand(0..99) < 2: draw 1 captured;
    // draw 2 not captured: he keeps the duel's Hurt roll (no second injury), leaves the group and stands free at the place of the check
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void MI66_CappedKeyDuelCapture(int roll, bool captured)
    {
        var w = DuelWorld(Chair, null, roll);
        var r = w.Events<MissionKeyDuelCaptureRoll>().Single();
        Assert.Equal((Chair, Side.Collective, 2, roll, captured), (r.Member, r.Captor, r.Cap, r.Roll, r.Captured));
        var emp = w.Ch.Get(Chair)!;
        Assert.Equal(captured, emp.Status == CharacterStatus.Captured);
        Assert.Equal(captured ? 1 : 0, w.Events<CharacterCaptured>().Count);
        Assert.False(w.M.IsOnMission(Chair));
        if (!captured) { Assert.Equal(0, emp.Injury); Assert.Equal(T, w.Ch.LocationOf(emp)); }
    }

    // m5-missions MI67: a character not in the key-target file keeps the Classic duel under Capped: captured, no cap roll
    [Fact]
    public void MI67_NonKeyCharacterUnaffected()
    {
        var w = DuelWorld(Varo, null, 99);
        Assert.Empty(w.Events<MissionKeyDuelCaptureRoll>());
        Assert.Equal(CharacterStatus.Captured, w.Ch.Get(Varo)!.Status);
    }

    // m5-missions MI68: Classic keeps the Classic duel for The Chair: captured outright, no cap roll (the queued 2 that would fail it is not drawn
    // as one); KeyTargetRules off behaves the same
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MI68_OriginalCapturesOutright(bool original)
    {
        var p = original ? new MissionParams { KeyDuelCapture = KeyDuelCapture.Classic } : new MissionParams { KeyTargetRules = false };
        var w = DuelWorld(Chair, p, 2);
        Assert.Empty(w.Events<MissionKeyDuelCaptureRoll>());
        Assert.Equal(CharacterStatus.Captured, w.Ch.Get(Chair)!.Status);
        Assert.Equal(MissionModule.CauseMissionDuel, w.Events<CharacterCaptured>().Single().Cause);
    }

    // m5-missions MI69: the Talent encounter is a duel too: Nobody loses to Ravilla (draw 99); under Capped his cap roll (4) decides; Classic
    // captures him outright
    [Theory]
    [InlineData(KeyDuelCapture.Capped, 3, true)]
    [InlineData(KeyDuelCapture.Capped, 4, false)]
    [InlineData(KeyDuelCapture.Classic, -1, true)]
    public void MI69_TalentEncounterDuel(KeyDuelCapture mode, int roll, bool captured)
    {
        var w = new MissionWorld(new MissionParams { KeyDuelCapture = mode });
        w.Hold(T, Side.Hand, 0);
        var nobody = w.Agent(Nobody, H, esp: 50, com: 90);
        var ravilla = w.Agent(Ravilla, T, com: 100);
        nobody.DiscoveredTalentHolder = Ravilla; ravilla.DiscoveredTalentHolder = Nobody; nobody.PairId = ravilla.PairId = 1;
        w.Start(Side.Collective, MissionKind.Espionage, new[] { Nobody }, T);
        w.Game.Step();
        w.RunUntilWorking(w.LastMission);
        // sensing hits (0), the duel is lost (99), Nobody's Hurt misses (99), [the cap roll], the two HurtBy rolls miss (99, 99), the hand draw 39
        var draws = new List<int> { 0, 99, 99 };
        if (roll >= 0) draws.Add(roll);
        draws.AddRange(new[] { 99, 99, 39 });
        w.Stub!.Expect(0, 99, draws.ToArray());
        w.RunIteration(w.LastMission);
        Assert.False(w.Events<MissionDuel>().Single().Escaped);
        Assert.Equal(roll >= 0 ? 1 : 0, w.Events<MissionKeyDuelCaptureRoll>().Count);
        Assert.Equal(captured, nobody.Status == CharacterStatus.Captured);
        Assert.False(w.Events<TalentEncounterResolved>().Single().MemberEscaped);
    }

    // m5-missions MI70: save and load after a Capped duel that did not capture: same state hash
    [Fact]
    public void MI70_SurvivesSaveLoad()
    {
        var w = DuelWorld(Chair, null, 50);
        Assert.False(w.Events<MissionKeyDuelCaptureRoll>().Single().Captured);
        string hash = w.Host.StateHash();
        w.Reload();
        Assert.Equal(hash, w.Host.StateHash());
        Assert.Equal(CharacterStatus.Free, w.Ch.Get(Chair)!.Status);
    }

    // Ravilla on Assassination of a target standing at Collective T with one landed Collective Army regiment.
    private static MissionWorld KeyJob(int target, MissionParams? p, int ravillaEsp = 60, int regiments = 1)
    {
        var w = new MissionWorld(p, characters: Praedra.Core.Tests.Characters.CharWorld.ClassicTalent()); // the Classic Talent (100 / 100 / 50): m5-characters-and-force §Talent CH37
        w.Hold(T, Side.Collective, 30);
        for (int i = 0; i < regiments; i++) w.Unit(Side.Collective, RebArmy, T);
        w.Agent(Ravilla, T, esp: ravillaEsp, com: 100);
        w.Agent(target, T);
        return w;
    }

    // m5-missions MI71: KeyTargetGuards: at the job check of a key-target mission, Full (DEFAULT) adds the landed regiment (the walk before B2d);
    // Classic keeps the B2d planet guards (special forces only: no detection draw); a non-key target (Midwife) never gets it
    [Theory]
    [InlineData(KeyTargetGuards.Full, Nobody, true)]
    [InlineData(KeyTargetGuards.Classic, Nobody, false)]
    [InlineData(KeyTargetGuards.Full, Midwife, false)]
    public void MI71_KeyTargetGuards(KeyTargetGuards mode, int target, bool walked)
    {
        var w = KeyJob(target, new MissionParams { KeyTargetGuards = mode });
        int regiment = w.Host.Economy.Objects.Single(o => o.Class == RebArmy).Id;
        w.Stub!.Expect(0, 99, Bt(1, 99, 99)); // betrayal, [detection misses], the success roll fails
        long s = w.Start(Side.Hand, MissionKind.Assassination, new[] { Ravilla }, null, targetChar: target);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        w.RunIteration(w.LastMission);
        var job = w.Events<MissionDetectionRoll>().Select(r => r.Defender).ToList();
        Assert.Equal(walked ? new[] { regiment } : Array.Empty<int>(), job);
    }

    // m5-missions MI72: KeyTargetBaseSkills: Ravilla (espionage 50, Talent 120: effective 110). On (DEFAULT): the detection walk reads 50, and he does
    // not qualify for a kill (espionage below 60): the target is injured, no k draw. Off (Classic Talent boost): the walk reads 110 (x 60 higher) and a
    // qualified success draws k (50: kill)
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MI72_KeyTargetBaseSkills(bool baseSkills)
    {
        var p = new MissionParams { KeyTargetBaseSkills = baseSkills };
        var walk = KeyJob(Nobody, p, ravillaEsp: 50);
        walk.Stub!.Expect(0, 99, Bt(1, 99, 99));
        walk.Start(Side.Hand, MissionKind.Assassination, new[] { Ravilla }, null, targetChar: Nobody);
        walk.Game.Step();
        walk.RunIteration(walk.LastMission);
        int x = walk.Events<MissionDetectionRoll>().First().X;
        var reference = KeyJob(Nobody, p with { KeyTargetBaseSkills = !baseSkills }, ravillaEsp: 50);
        reference.Stub!.Expect(0, 99, Bt(1, 99, 99));
        reference.Start(Side.Hand, MissionKind.Assassination, new[] { Ravilla }, null, targetChar: Nobody);
        reference.Game.Step();
        reference.RunIteration(reference.LastMission);
        Assert.Equal(baseSkills ? -60 : 60, x - reference.Events<MissionDetectionRoll>().First().X);

        var w = KeyJob(Nobody, p, ravillaEsp: 50, regiments: 0);
        w.Stub!.Expect(0, 99, Bt(1, 0, 50)); // betrayal, the success roll passes (0 < 4), [k = 50: kill]
        w.Start(Side.Hand, MissionKind.Assassination, new[] { Ravilla }, null, targetChar: Nobody);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.True(w.Events<MissionMemberRoll>().Single().Success);
        Assert.Equal(!baseSkills, w.Ch.IsDead(Nobody));
    }
}
