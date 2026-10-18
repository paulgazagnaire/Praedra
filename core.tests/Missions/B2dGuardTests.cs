using Rebellion.Core.Battle;
using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Missions;
using Rebellion.Core.Setup;
using static Rebellion.Core.Tests.Missions.MissionWorld;

namespace Rebellion.Core.Tests.Missions;

// B2d-who-guards-and-the-escape-duel tests B2d-1 to B2d-13. rule: m5-missions §1 (check points), §3 (guards), §4 (foiled, duel).
// Unless a test says otherwise, an Alliance team flies from H (neutral) to T (Empire-held): the leaving checks at H meet nobody.
public class B2dGuardTests
{
    private const string Isd = "Imperial Star Destroyer";

    private static MissionWorld World(MissionParams? p = null, bool decoys = false)
    {
        var w = new MissionWorld(p, decoys);
        w.Hold(T, Side.Empire, 0);
        return w;
    }

    // A completed KDY-150 of the Alliance at the system.
    private static void Ion(MissionWorld w, string system) => w.Host.Economy.Find(w.Host.Economy.AddFacility(system, FacilityKind.Kdy150))!.Side = Side.Alliance;

    private static int Fleet(MissionWorld w, Side side, string system, params string[] ships) => w.Host.Fleets.AddFleet(side, "F", ships, system);

    private static int StartAndLand(MissionWorld w, MissionKind kind, int[] chars, int[]? sfs = null, int[]? decoys = null, string target = T)
    {
        long seq = w.Start(Side.Alliance, kind, chars, target, sfs, decoys);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        int id = w.LastMission;
        w.RunUntilWorking(id);
        return id;
    }

    // B2d-1: 3 regiments on the planet, 2 fighter squadrons, 1 ISD in orbit, no special forces: one detection draw at arrival (the ISD); none in
    // the job
    [Fact]
    public void B2d1_GarrisonAndFightersNeverGuard()
    {
        var w = World();
        w.Agent(Leia, H, esp: 50);
        for (int i = 0; i < 3; i++) w.Unit(Side.Empire, ImpArmy, T);
        w.Unit(Side.Empire, "TIE Fighter", T); w.Unit(Side.Empire, "TIE Fighter", T);
        int fleet = Fleet(w, Side.Empire, T, Isd);
        int ship = w.Game.State.Fleets[fleet].Ships[0];
        w.Stub!.Expect(0, 99, 99);
        int id = StartAndLand(w, MissionKind.Espionage, new[] { Leia });
        Assert.Equal(new[] { ship }, w.Events<MissionDetectionRoll>().Select(r => r.Defender));
        w.RunIteration(id);
        Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Empty(w.Events<MissionDecoyRoll>());
    }

    // B2d-2: the same plus one Imperial special force on the planet: one draw at arrival (the ISD), one in the job (the special force)
    [Fact]
    public void B2d2_SpecialForceGuardsThePlanet()
    {
        var w = World();
        w.Agent(Leia, H, esp: 50);
        for (int i = 0; i < 3; i++) w.Unit(Side.Empire, ImpArmy, T);
        int ship = w.Game.State.Fleets[Fleet(w, Side.Empire, T, Isd)].Ships[0];
        int probe = w.Unit(Side.Empire, Probe, T);
        w.Stub!.Expect(0, 99, 99, 99, 99, 99);
        int id = StartAndLand(w, MissionKind.Espionage, new[] { Leia });
        w.RunIteration(id);
        Assert.Equal(new[] { ship, probe }, w.Events<MissionDetectionRoll>().Select(r => r.Defender));
    }

    // B2d-3: as B2d-1, the Alliance also owns a KDY-150 there: no draw at arrival, none in the job
    [Fact]
    public void B2d3_OwnIonCannonShieldsFromFleets()
    {
        var w = World();
        w.Agent(Leia, H, esp: 50);
        for (int i = 0; i < 3; i++) w.Unit(Side.Empire, ImpArmy, T);
        Fleet(w, Side.Empire, T, Isd);
        Ion(w, T);
        int id = StartAndLand(w, MissionKind.Espionage, new[] { Leia });
        w.RunIteration(id);
        Assert.Empty(w.Events<MissionDetectionRoll>());
    }

    // B2d-4: Death Star Sabotage: the Death Star and 2 ISDs in orbit and an Imperial special force on the planet: in the job the 3 ships guard,
    // the special force does not (the target is in orbit)
    [Fact]
    public void B2d4_DeathStarSabotageIsGuardedByShips()
    {
        var w = World();
        w.Agent(Luke, H, esp: 100, com: 100);
        var ships = w.Game.State.Fleets[Fleet(w, Side.Empire, T, "Death Star", Isd, Isd)].Ships.OrderBy(x => x).ToList();
        int probe = w.Unit(Side.Empire, Probe, T);
        w.Stub!.Expect(0, 99, Enumerable.Repeat(99, 7).ToArray()); // arrival: 3 walks; job: betrayal, 3 walks
        int id = StartAndLand(w, MissionKind.DeathStarSabotage, new[] { Luke });
        int from = w.Events<MissionDetectionRoll>().Count;
        Assert.Equal(3, from);
        w.RunIteration(id);
        var job = w.Events<MissionDetectionRoll>().Skip(from).Select(r => r.Defender).ToList();
        Assert.Equal(ships, job);
        Assert.DoesNotContain(probe, job);
    }

    // B2d-5: a team leaving its own system where an Imperial fleet sits: the leaving orbit check walks the fleet's ship; with an own KDY-150 there,
    // no leaving draw
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void B2d5_LeavingPastAFleet(bool ion, int draws)
    {
        var w = World();
        w.Hold(H, Side.Alliance, 100);
        w.Agent(Leia, H, esp: 50);
        Fleet(w, Side.Empire, H, Isd);
        if (ion) Ion(w, H);
        w.Stub!.Expect(0, 99, 99);
        long seq = w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        Assert.Equal(draws, w.Events<MissionDetectionRoll>().Count);
        Assert.Single(w.Events<MissionDeparted>());
    }

    // B2d-6: Diplomacy on a neutral planet with no fleets: no detection draw at any step
    [Fact]
    public void B2d6_NeutralPlanetNoGuards()
    {
        var w = World();
        w.Hold(T, null, 50);
        w.Agent(Leia, H, dip: 80);
        w.Unit(Side.Empire, Probe, T);
        int id = StartAndLand(w, MissionKind.Diplomacy, new[] { Leia });
        w.RunIteration(id);
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Empty(w.Events<MissionDecoyRoll>());
    }

    // B2d-7: the duel: Han (combat 100) against an ISD whose Admiral has combat 85: x = 15, p = 70: draw 69 escapes, 70 is captured
    [Theory]
    [InlineData(69, true)]
    [InlineData(70, false)]
    public void B2d7_DuelAgainstACommander(int draw, bool escaped)
    {
        var w = World();
        w.Agent(Han, H, esp: 50, com: 100);
        int fleet = Fleet(w, Side.Empire, T, Isd);
        w.Agent(Piett, T, com: 85); w.Ch.SetPost(Piett, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 0, draw, 99); // arrival walk detects, then Han's duel, then (B2e moved) his Hurt roll (p = 1: 99 misses)
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Han }, T);
        w.Run(5);
        var d = w.Events<MissionDuel>().Single();
        Assert.Equal((Han, 15, 70, escaped), (d.Member, d.X, d.Chance, d.Escaped));
        Assert.Equal(escaped ? CharacterStatus.Free : CharacterStatus.Captured, w.Ch.Get(Han)!.Status);
        if (!escaped) Assert.Equal(MissionModule.CauseMissionDuel, w.Events<CharacterCaptured>().Single().Cause);
        // QA-7 moved (B2d §5 step 4, corrected 2026-10-18): a captured Han is held aboard the Admiral's fleet, located at T
        if (escaped) Assert.Equal(T, w.Ch.Get(Han)!.System);
        else Assert.Equal((fleet, (string?)null, T), (w.Ch.Get(Han)!.HeldAboard, w.Ch.Get(Han)!.HeldAt, w.Ch.LocationOf(w.Ch.Get(Han)!)));
    }

    // B2d-8: a special force with combat 0 (Bothan Spies) against a guard without a commander: x = 0, p = 40: draw 40 destroys it (MissionDuel)
    [Fact]
    public void B2d8_SpecialForceDestroyedInTheDuel()
    {
        var w = World();
        int bothan = w.Unit(Side.Alliance, Bothan, H);
        w.Unit(Side.Empire, Probe, T);
        w.Stub!.Expect(0, 99, 99, 0, 40); // the job: betrayal, the walk detects, the duel
        int id = StartAndLand(w, MissionKind.Espionage, Array.Empty<int>(), new[] { bothan });
        w.RunIteration(id);
        var d = w.Events<MissionDuel>().Single();
        Assert.Equal((bothan, false, 0, 40, false), (d.Member, d.IsCharacter, d.X, d.Chance, d.Escaped));
        Assert.Null(w.Host.Economy.Find(bothan));
        Assert.Equal(UnitLossReason.MissionDuel, w.Events<UnitDestroyed>().Single(e => e.Unit == bothan).Reason);
    }

    // B2d-9: foiled at arrival (code 4) or while leaving (code 3); every non-decoy member duels once; with one guard there is no pick draw
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void B2d9_EndCodesAndOneDuelEach(bool leaving)
    {
        var w = World();
        w.Agent(Leia, H, esp: 50); w.Agent(Han, H, esp: 50);
        if (leaving) w.Hold(H, Side.Alliance, 100);
        int isd = leaving ? Fleet(w, Side.Empire, H, Isd) : Fleet(w, Side.Empire, T, Isd);
        // the walk detects, two duels lost; B2e moved: each duel is followed by the member's Hurt roll (combat 0: p = 100, always a hit; majors
        // make no death draw), then the capture
        w.Stub!.Expect(0, 99, 0, 99, 99, 99, 99);
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia, Han }, T);
        w.Run(5);
        Assert.Equal(leaving ? MissionModule.FoilCodeAtOrigin : MissionModule.FoilCodeAtTarget, w.Events<MissionFoiled>().Single().EndCode);
        Assert.Equal(new[] { Leia, Han }, w.Events<MissionDuel>().Select(e => e.Member));
        Assert.Equal(0, w.Stub.Count(0, 1));
        Assert.Equal(MissionOutcome.Foiled, w.Events<MissionEnded>().Single().Outcome);
        Assert.Equal(leaving ? 0 : 1, w.Events<MissionDeparted>().Count);
        Assert.All(new[] { Leia, Han }, c => Assert.Equal(CharacterStatus.Captured, w.Ch.Get(c)!.Status));
        // QA-7 moved (B2d §5 step 4, corrected 2026-10-18): the ship itself is the captor (no Admiral), so both are held aboard its fleet
        Assert.All(new[] { Leia, Han }, c => Assert.Equal((isd, leaving ? H : T), (w.Ch.Get(c)!.HeldAboard, w.Ch.LocationOf(w.Ch.Get(c)!))));
    }

    // B2d-10: a failed decoy duels right after its roll and leaves the group whatever the result; the next guard cannot pick it
    [Fact]
    public void B2d10_FailedDecoyLeaves()
    {
        var w = World(decoys: true);
        w.Agent(Leia, H, esp: 50);
        int inf = w.Unit(Side.Alliance, Infiltrators, H);
        w.Unit(Side.Empire, Probe, T); w.Unit(Side.Empire, Probe, T);
        int id = StartAndLand(w, MissionKind.Espionage, new[] { Leia }, decoys: new[] { inf });
        w.Stub!.Expect(0, 99, 99, 99, 99, 0, 99, 99, 99); // betrayal x2, decoy fails, duel escapes (0), two walks miss, the success roll
        int from = w.Stub.Requests.Count;
        w.RunIteration(id);
        Assert.Equal(Enumerable.Repeat((0, 99), 7), w.RequestsSince(from));
        Assert.Single(w.Events<MissionDecoyRoll>());
        Assert.True(w.Events<MissionDuel>().Single().Escaped);
        Assert.DoesNotContain(inf, w.M.Get(id)?.DecoySpecialForces ?? new List<int>());
        Assert.Equal(T, w.Host.Economy.Find(inf)!.System);
        Assert.False(w.M.IsSpecialForceBusy(inf));
        Assert.Equal(2, w.Events<MissionDetectionRoll>().Count);
    }

    // B2d-11: Jedi Training can be foiled (MISSNSD word 15 = 1); the data's can_resign column replaces the fall-back table
    [Fact]
    public void B2d11_JediTrainingIsFoilable()
    {
        var d = GameData.Default.Missions;
        Assert.True(d.Get(MissionKind.JediTraining).Foilable);
        foreach (var k in new[] { MissionKind.Move, MissionKind.Return, MissionKind.Adrift, MissionKind.Dagobah, MissionKind.Bounty })
            Assert.False(d.Get(k).Foilable);
        Assert.Equal(14, d.Evade!.Count);
        Assert.Equal((15, 70, 99), (new MissionWorld().M.Lookup(d.Evade, -50), MissionModule.Lookup(d.Evade, 15, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue), MissionModule.Lookup(d.Evade, 101, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue)));
    }

    // B2d-12: the Bounty record (word 14 = 0) has no check at any step; every other record has the check; the kind words
    [Fact]
    public void B2d12_BountyHasNoCheck()
    {
        var d = GameData.Default.Missions;
        Assert.False(d.Get(MissionKind.Bounty).GuardChecks);
        Assert.All(d.Records.Where(r => r.Kind != MissionKind.Bounty), r => Assert.True(r.GuardChecks));
        Assert.Equal(MissionModule.CheckKind.Planet, MissionModule.KindAt(MissionModule.KindWord(MissionKind.Espionage), MissionModule.CheckPoint.LeavePlanet));
        Assert.Equal(MissionModule.CheckKind.Orbit, MissionModule.KindAt(MissionModule.KindWord(MissionKind.Espionage), MissionModule.CheckPoint.LeaveOrbit));
        Assert.Equal(MissionModule.CheckKind.Orbit, MissionModule.KindAt(MissionModule.KindWord(MissionKind.Espionage), MissionModule.CheckPoint.Arrive));
        Assert.Equal(MissionModule.CheckKind.NextToTarget, MissionModule.KindAt(MissionModule.KindWord(MissionKind.Espionage), MissionModule.CheckPoint.Job));
        Assert.Equal(MissionModule.CheckKind.OrbitThenPlanet, MissionModule.KindAt(MissionModule.KindWord(MissionKind.Adrift), MissionModule.CheckPoint.LeavePlanet));
        Assert.Equal(MissionModule.CheckKind.None, MissionModule.KindAt(MissionModule.KindWord(MissionKind.Move), MissionModule.CheckPoint.Job));
    }

    // B2d-13: GuardsAnySide on: an own ship and an enemy ship at the target are both orbit guards at arrival; off: only the enemy ship
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void B2d13_GuardsAnySide(bool anySide, int guards)
    {
        var w = World(new MissionParams { GuardsAnySide = anySide });
        Fleet(w, Side.Alliance, T, "Mon Calamari Cruiser");
        Fleet(w, Side.Empire, T, Isd);
        // read between Steps: in a Step the two fleets would fight first
        var group = new MissionGroup { Side = Side.Alliance, Kind = MissionKind.Espionage, Home = H, Target = T };
        var scope = w.M.ScopeOf(group, MissionModule.CheckKind.Orbit, T);
        Assert.True(scope.OrbitGuards);
        Assert.Equal(guards, w.M.Guards(Side.Alliance, T, scope, group).Count);
    }
}
