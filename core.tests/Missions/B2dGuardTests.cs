using Praedra.Core.Battle;
using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Missions;
using Praedra.Core.Setup;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// B2d-who-guards-and-the-escape-duel tests B2d-1 to B2d-13. rule: m5-missions §1 (check points), §3 (guards), §4 (foiled, duel).
// Unless a test says otherwise, a Collective team flies from H (neutral) to T (Hand-held): the leaving checks at H meet nobody.
public class B2dGuardTests
{
    private const string Mandate = "Mandate";

    private static MissionWorld World(MissionParams? p = null, bool decoys = false, CharacterParams? characters = null)
    {
        var w = new MissionWorld(p, decoys, characters: characters);
        w.Hold(T, Side.Hand, 0);
        return w;
    }

    // A completed Mordant Lien-150 of the Collective at the system.
    private static void Ion(MissionWorld w, string system) => w.Host.Economy.Find(w.Host.Economy.AddFacility(system, FacilityKind.Lien150))!.Side = Side.Collective;

    private static int Fleet(MissionWorld w, Side side, string system, params string[] ships) => w.Host.Fleets.AddFleet(side, "F", ships, system);

    private static int StartAndLand(MissionWorld w, MissionKind kind, int[] chars, int[]? sfs = null, int[]? decoys = null, string target = T)
    {
        long seq = w.Start(Side.Collective, kind, chars, target, sfs, decoys);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        int id = w.LastMission;
        w.RunUntilWorking(id);
        return id;
    }

    // B2d-1: 3 regiments on the planet, 2 fighter squadrons, 1 Mandate in orbit, no special forces: one detection draw at arrival (the Mandate); none in
    // the job
    [Fact]
    public void B2d1_GarrisonAndFightersNeverGuard()
    {
        var w = World();
        w.Agent(Ledger, H, esp: 50);
        for (int i = 0; i < 3; i++) w.Unit(Side.Hand, ImpArmy, T);
        w.Unit(Side.Hand, "Gleaner", T); w.Unit(Side.Hand, "Gleaner", T);
        int fleet = Fleet(w, Side.Hand, T, Mandate);
        int ship = w.Game.State.Fleets[fleet].Ships[0];
        w.Stub!.Expect(0, 99, 99);
        int id = StartAndLand(w, MissionKind.Espionage, new[] { Ledger });
        Assert.Equal(new[] { ship }, w.Events<MissionDetectionRoll>().Select(r => r.Defender));
        w.RunIteration(id);
        Assert.Single(w.Events<MissionDetectionRoll>());
        Assert.Empty(w.Events<MissionDecoyRoll>());
    }

    // B2d-2: the same plus one Hand special force on the planet: one draw at arrival (the Mandate), one in the job (the special force)
    [Fact]
    public void B2d2_SpecialForceGuardsThePlanet()
    {
        var w = World();
        w.Agent(Ledger, H, esp: 50);
        for (int i = 0; i < 3; i++) w.Unit(Side.Hand, ImpArmy, T);
        int ship = w.Game.State.Fleets[Fleet(w, Side.Hand, T, Mandate)].Ships[0];
        int probe = w.Unit(Side.Hand, Probe, T);
        w.Stub!.Expect(0, 99, 99, 99, 99, 99);
        int id = StartAndLand(w, MissionKind.Espionage, new[] { Ledger });
        w.RunIteration(id);
        Assert.Equal(new[] { ship, probe }, w.Events<MissionDetectionRoll>().Select(r => r.Defender));
    }

    // B2d-3: as B2d-1, the Collective also owns a Mordant Lien-150 there: no draw at arrival, none in the job
    [Fact]
    public void B2d3_OwnIonCannonShieldsFromFleets()
    {
        var w = World();
        w.Agent(Ledger, H, esp: 50);
        for (int i = 0; i < 3; i++) w.Unit(Side.Hand, ImpArmy, T);
        Fleet(w, Side.Hand, T, Mandate);
        Ion(w, T);
        int id = StartAndLand(w, MissionKind.Espionage, new[] { Ledger });
        w.RunIteration(id);
        Assert.Empty(w.Events<MissionDetectionRoll>());
    }

    // B2d-4: Spike the Harvester: the harvester and 2 Mandates in orbit and a Hand special force on the planet: in the job the 3 ships guard,
    // the special force does not (the target is in orbit)
    [Fact]
    public void B2d4_HarvesterSabotageIsGuardedByShips()
    {
        var w = World();
        w.Agent(Nobody, H, esp: 100, com: 100);
        var ships = w.Game.State.Fleets[Fleet(w, Side.Hand, T, "harvester", Mandate, Mandate)].Ships.OrderBy(x => x).ToList();
        int probe = w.Unit(Side.Hand, Probe, T);
        w.Stub!.Expect(0, 99, Enumerable.Repeat(99, 7).ToArray()); // arrival: 3 walks; job: betrayal, 3 walks
        int id = StartAndLand(w, MissionKind.SpikeTheHarvester, new[] { Nobody });
        int from = w.Events<MissionDetectionRoll>().Count;
        Assert.Equal(3, from);
        w.RunIteration(id);
        var job = w.Events<MissionDetectionRoll>().Skip(from).Select(r => r.Defender).ToList();
        Assert.Equal(ships, job);
        Assert.DoesNotContain(probe, job);
    }

    // B2d-5: a team leaving its own system where a Hand fleet sits: the leaving orbit check walks the fleet's ship; with an own Mordant Lien-150 there,
    // no leaving draw
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void B2d5_LeavingPastAFleet(bool ion, int draws)
    {
        var w = World();
        w.Hold(H, Side.Collective, 100);
        w.Agent(Ledger, H, esp: 50);
        Fleet(w, Side.Hand, H, Mandate);
        if (ion) Ion(w, H);
        w.Stub!.Expect(0, 99, 99);
        long seq = w.Start(Side.Collective, MissionKind.Espionage, new[] { Ledger }, T);
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
        w.Agent(Ledger, H, dip: 80);
        w.Unit(Side.Hand, Probe, T);
        int id = StartAndLand(w, MissionKind.Diplomacy, new[] { Ledger });
        w.RunIteration(id);
        Assert.Empty(w.Events<MissionDetectionRoll>());
        Assert.Empty(w.Events<MissionDecoyRoll>());
    }

    // B2d-7: the duel: Midwife (combat 100) against an Mandate whose Admiral has combat 85: x = 15, p = 70: draw 69 escapes, 70 is captured
    [Theory]
    [InlineData(69, true)]
    [InlineData(70, false)]
    public void B2d7_DuelAgainstACommander(int draw, bool escaped)
    {
        // checks where the capture puts him; LandPrisoners off, else New lands him at Hand T the same day end (CH38)
        var w = World(characters: new CharacterParams { LandPrisoners = false });
        w.Agent(Midwife, H, esp: 50, com: 100);
        int fleet = Fleet(w, Side.Hand, T, Mandate);
        w.Agent(Varo, T, com: 85); w.Ch.SetPost(Varo, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 0, draw, 99); // arrival walk detects, then Midwife's duel, then (B2e moved) his Hurt roll (p = 1: 99 misses)
        w.Start(Side.Collective, MissionKind.Espionage, new[] { Midwife }, T);
        w.Run(5);
        var d = w.Events<MissionDuel>().Single();
        Assert.Equal((Midwife, 15, 70, escaped), (d.Member, d.X, d.Chance, d.Escaped));
        Assert.Equal(escaped ? CharacterStatus.Free : CharacterStatus.Captured, w.Ch.Get(Midwife)!.Status);
        if (!escaped) Assert.Equal(MissionModule.CauseMissionDuel, w.Events<CharacterCaptured>().Single().Cause);
        // QA-7 moved (B2d §5 step 4, corrected 2026-10-18): a captured Midwife is held aboard the Admiral's fleet, located at T
        if (escaped) Assert.Equal(T, w.Ch.Get(Midwife)!.System);
        else Assert.Equal((fleet, (string?)null, T), (w.Ch.Get(Midwife)!.HeldAboard, w.Ch.Get(Midwife)!.HeldAt, w.Ch.LocationOf(w.Ch.Get(Midwife)!)));
    }

    // B2d-8: a special force with combat 0 (Listeners) against a guard without a commander: x = 0, p = 40: draw 40 destroys it (MissionDuel)
    [Fact]
    public void B2d8_SpecialForceDestroyedInTheDuel()
    {
        var w = World();
        int listeners = w.Unit(Side.Collective, Listeners, H);
        w.Unit(Side.Hand, Probe, T);
        w.Stub!.Expect(0, 99, 99, 0, 40); // the job: betrayal, the walk detects, the duel
        int id = StartAndLand(w, MissionKind.Espionage, Array.Empty<int>(), new[] { listeners });
        w.RunIteration(id);
        var d = w.Events<MissionDuel>().Single();
        Assert.Equal((listeners, false, 0, 40, false), (d.Member, d.IsCharacter, d.X, d.Chance, d.Escaped));
        Assert.Null(w.Host.Economy.Find(listeners));
        Assert.Equal(UnitLossReason.MissionDuel, w.Events<UnitDestroyed>().Single(e => e.Unit == listeners).Reason);
    }

    // B2d-9: foiled at arrival (code 4) or while leaving (code 3); every non-decoy member duels once; with one guard there is no pick draw
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void B2d9_EndCodesAndOneDuelEach(bool leaving)
    {
        var w = World(characters: new CharacterParams { LandPrisoners = false }); // as B2d-7: where the capture puts them (CH38 lands them)
        w.Agent(Ledger, H, esp: 50); w.Agent(Midwife, H, esp: 50);
        if (leaving) w.Hold(H, Side.Collective, 100);
        int mandate = leaving ? Fleet(w, Side.Hand, H, Mandate) : Fleet(w, Side.Hand, T, Mandate);
        // the walk detects, two duels lost; B2e moved: each duel is followed by the member's Hurt roll (combat 0: p = 100, always a hit; majors
        // make no death draw), then the capture
        w.Stub!.Expect(0, 99, 0, 99, 99, 99, 99);
        w.Start(Side.Collective, MissionKind.Espionage, new[] { Ledger, Midwife }, T);
        w.Run(5);
        Assert.Equal(leaving ? MissionModule.FoilCodeAtOrigin : MissionModule.FoilCodeAtTarget, w.Events<MissionFoiled>().Single().EndCode);
        Assert.Equal(new[] { Ledger, Midwife }, w.Events<MissionDuel>().Select(e => e.Member));
        Assert.Equal(0, w.Stub.Count(0, 1));
        Assert.Equal(MissionOutcome.Foiled, w.Events<MissionEnded>().Single().Outcome);
        Assert.Equal(leaving ? 0 : 1, w.Events<MissionDeparted>().Count);
        Assert.All(new[] { Ledger, Midwife }, c => Assert.Equal(CharacterStatus.Captured, w.Ch.Get(c)!.Status));
        // QA-7 moved (B2d §5 step 4, corrected 2026-10-18): the ship itself is the captor (no Admiral), so both are held aboard its fleet
        Assert.All(new[] { Ledger, Midwife }, c => Assert.Equal((mandate, leaving ? H : T), (w.Ch.Get(c)!.HeldAboard, w.Ch.LocationOf(w.Ch.Get(c)!))));
    }

    // B2d-10: a failed decoy duels right after its roll and leaves the group whatever the result; the next guard cannot pick it
    [Fact]
    public void B2d10_FailedDecoyLeaves()
    {
        var w = World(decoys: true);
        w.Agent(Ledger, H, esp: 50);
        int inf = w.Unit(Side.Collective, Infiltrators, H);
        w.Unit(Side.Hand, Probe, T); w.Unit(Side.Hand, Probe, T);
        int id = StartAndLand(w, MissionKind.Espionage, new[] { Ledger }, decoys: new[] { inf });
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

    // B2d-11: Train Talent can be foiled (MISSNSD word 15 = 1); the data's can_resign column replaces the fall-back table
    [Fact]
    public void B2d11_TrainTalentIsFoilable()
    {
        var d = GameData.Default.Missions;
        Assert.True(d.Get(MissionKind.TrainTalent).Foilable);
        foreach (var k in new[] { MissionKind.Move, MissionKind.Return, MissionKind.Adrift, MissionKind.Leave, MissionKind.Repossession })
            Assert.False(d.Get(k).Foilable);
        Assert.Equal(14, d.Evade!.Count);
        Assert.Equal((15, 70, 99), (new MissionWorld().M.Lookup(d.Evade, -50), MissionModule.Lookup(d.Evade, 15, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue), MissionModule.Lookup(d.Evade, 101, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue)));
    }

    // B2d-12: the Repossession record (word 14 = 0) has no check at any step; every other record has the check; the kind words
    [Fact]
    public void B2d12_RepossessionHasNoCheck()
    {
        var d = GameData.Default.Missions;
        Assert.False(d.Get(MissionKind.Repossession).GuardChecks);
        Assert.All(d.Records.Where(r => r.Kind != MissionKind.Repossession), r => Assert.True(r.GuardChecks));
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
        Fleet(w, Side.Collective, T, "Ark");
        Fleet(w, Side.Hand, T, Mandate);
        // read between Steps: in a Step the two fleets would fight first
        var group = new MissionGroup { Side = Side.Collective, Kind = MissionKind.Espionage, Home = H, Target = T };
        var scope = w.M.ScopeOf(group, MissionModule.CheckKind.Orbit, T);
        Assert.True(scope.OrbitGuards);
        Assert.Equal(guards, w.M.Guards(Side.Collective, T, scope, group).Count);
    }
}
