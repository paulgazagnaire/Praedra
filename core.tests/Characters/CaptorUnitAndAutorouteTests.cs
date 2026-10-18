using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Missions;
using Rebellion.Core.Tests.Missions;
using static Rebellion.Core.Tests.Missions.MissionWorld;

namespace Rebellion.Core.Tests.Characters;

// QA-7 (where a new prisoner is held) and QA-9 (where a freed, escaped or displaced character goes), 2026-10-18.
// rule: m5-characters-and-force §Capture, injury, death, freeing (B2e §3 "Where a new prisoner is held", "Where a freed, escaped or displaced
// character goes", Work 12, Work 13); m5-missions §4.
public class CaptorUnitAndAutorouteTests
{
    private static int Fleet(MissionWorld w, Side side, string system, params string[] ships) => w.Host.Fleets.AddFleet(side, "F", ships, system);
    private const string Isd = "Imperial Star Destroyer", Corvette = "Corellian Corvette";

    private static void StepWith(MissionWorld w, string slot, Action<StepContext> work)
    {
        bool done = false;
        w.Game.RegisterHook(slot, ctx => { if (!done) { done = true; work(ctx); } });
        w.Game.Step();
    }

    // B2e-20: the Emperor, on a mission at Imperial T, loses the duel to an Alliance fleet guard there (its Admiral): captured and aboard that fleet
    // the same step, not freed though T is Imperial; he travels with the fleet; T changing hands does not touch him
    [Fact]
    public void B2e20_DuelPrisonerIsHeldAboardTheCaptorFleet()
    {
        // KeyDuelCapture, 2026-10-18: the Emperor is a key character, so the DEFAULT (Capped) would add his cap roll; this test checks where a duel
        // prisoner is held, not whether he is taken, so it runs the 1998 duel explicitly (the cap roll is covered by m5-missions MI66 to MI70).
        var w = new MissionWorld(new MissionParams { KeyDuelCapture = KeyDuelCapture.Original });
        w.Hold(H, Side.Empire, 0); w.Hold(T, Side.Empire, 0);
        w.Agent(Palpatine, H, dip: 50);
        int fleet = Fleet(w, Side.Alliance, T, Corvette);
        w.Agent(Wedge, T, com: 80); w.Ch.SetPost(Wedge, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 0, 99, 99); // the arrival walk detects, the duel is lost (99), the Hurt roll misses
        w.Start(Side.Empire, MissionKind.Diplomacy, new[] { Palpatine }, T);
        w.Run(5);
        var emp = w.Ch.Get(Palpatine)!;
        Assert.Equal(CharacterStatus.Captured, emp.Status);
        Assert.Equal((fleet, (string?)null, T), (emp.HeldAboard, emp.HeldAt, w.Ch.LocationOf(emp)));
        var cap = w.Events<CharacterCaptured>().Single();
        Assert.Equal((MissionModule.CauseMissionDuel, fleet), (cap.Cause, cap.HeldAboard));
        Assert.Empty(w.Events<CharacterFreed>());
        Assert.False(w.M.IsOnMission(Palpatine));

        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Alliance, fleet, X)));
        Assert.Null(w.Ch.LocationOf(emp)); // in transit with the fleet
        w.Run(30);
        Assert.Equal(X, w.Ch.LocationOf(emp));
        StepWith(w, "5.2", ctx => { w.Hold(T, Side.Alliance, 100); w.Ch.Liberate(ctx, T); });
        Assert.Equal((CharacterStatus.Captured, fleet), (emp.Status, emp.HeldAboard));
    }

    // Q73 (B2e §3 "A prisoner held aboard stays aboard"): caught by a ship with no commander, his escape place is that ship, so avg = 0 even after
    // an officer joins the fleet; caught by the fleet's Admiral, his place is the fleet and avg counts its characters
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 80)]
    public void Q73_ShipCaptorWithNoCommanderGivesNoGuardAverage(bool admiralAtCapture, int avg)
    {
        var w = new MissionWorld(new MissionParams { KeyDuelCapture = KeyDuelCapture.Original });
        w.Hold(H, Side.Empire, 0); w.Hold(T, Side.Empire, 0);
        w.Agent(Palpatine, H, dip: 50);
        int fleet = Fleet(w, Side.Alliance, T, Corvette);
        w.Agent(Wedge, T, com: 80);
        if (admiralAtCapture) w.Ch.SetPost(Wedge, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 0, 99, 99); // the arrival walk detects, the duel is lost (99), the Hurt roll misses
        w.Start(Side.Empire, MissionKind.Diplomacy, new[] { Palpatine }, T);
        w.Run(5);
        var emp = w.Ch.Get(Palpatine)!;
        Assert.Equal((CharacterStatus.Captured, fleet), (emp.Status, emp.HeldAboard));
        Assert.Equal(admiralAtCapture ? 0 : w.Game.State.Fleets[fleet].Ships.Single(), emp.HeldAboardShip);
        if (!admiralAtCapture) w.Ch.SetPost(Wedge, fleet, CharacterPost.Admiral); // joins after the capture
        emp.NextEscapeDay = w.Game.Day;
        w.Stub.Expect(0, 99, 99);
        w.Game.Step();
        Assert.Equal((avg, 0), w.Events<EscapeAttempted>().Select(e => (e.CaptorAverageCombat, e.CaptorSpecialForces)).Single());
    }

    // CH38 (Prisoners land, New, Paul 2026-10-18): the Emperor, caught aboard an Alliance fleet at Imperial T, stays aboard there; the fleet reaches
    // Alliance X and puts him ashore at day end (PrisonerLanded), where he is a prisoner on the planet. With LandPrisoners off (1998) he stays aboard.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CH38_HoldingFleetLandsPrisonersAtAnOwnSystem(bool land)
    {
        var w = new MissionWorld(new MissionParams { KeyDuelCapture = KeyDuelCapture.Original }, characters: new CharacterParams { LandPrisoners = land });
        w.Hold(H, Side.Empire, 0); w.Hold(T, Side.Empire, 0); w.Hold(X, Side.Alliance, 100);
        w.Agent(Palpatine, H, dip: 50);
        int fleet = Fleet(w, Side.Alliance, T, Corvette);
        w.Agent(Wedge, T, com: 80); w.Ch.SetPost(Wedge, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 0, 99, 99); // the arrival walk detects, the duel is lost (99), the Hurt roll misses
        w.Start(Side.Empire, MissionKind.Diplomacy, new[] { Palpatine }, T);
        w.Run(5);
        var emp = w.Ch.Get(Palpatine)!;
        Assert.Equal((CharacterStatus.Captured, fleet), (emp.Status, emp.HeldAboard)); // T is not the captor's: no landing
        Assert.Empty(w.Events<PrisonerLanded>());
        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Alliance, fleet, X)));
        w.Run(30);
        Assert.Equal(X, w.Ch.LocationOf(emp));
        if (land)
        {
            Assert.Equal((0, X), (emp.HeldAboard, emp.HeldAt));
            var e = w.Events<PrisonerLanded>().Single();
            Assert.Equal((Palpatine, Side.Alliance, X, fleet), (e.Character, e.Captor, e.System, e.Fleet));
            Assert.Equal(CharacterStatus.Captured, emp.Status);
        }
        else
        {
            Assert.Equal((fleet, (string?)null), (emp.HeldAboard, emp.HeldAt));
            Assert.Empty(w.Events<PrisonerLanded>());
        }
    }

    // B2e-21: a capture whose captor (not on a mission) stands on a planet the captor's side does not hold: captured, then freed the same step there
    // (cause Liberation), no draw but the escape timer's; then he sets off for his side's world (Autoroute step 3)
    [Fact]
    public void B2e21_CaptureOnGroundNotTheCaptorsIsFreedAtOnce()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Alliance, 100); w.Hold(T, null, 50);
        var han = w.Agent(Han, T);
        int before = w.Stub!.Requests.Count;
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug, unit: new CaptorUnit(0, T)));
        Assert.Equal(CharacterCause.Liberation, w.Events<CharacterFreed>().Single().Cause);
        Assert.Single(w.Events<CharacterCaptured>());
        Assert.Equal(new[] { (0, 1000) }, w.RequestsSince(before).Where(r => r != (0, 70)));
        Assert.Equal(CharacterStatus.Free, han.Status);
        Assert.Equal(H, han.Transit!.Path[^1]);
    }

    // B2e-22: Abduction succeeds (Han takes Piett at Imperial T): Piett is the team's captive, ends where the team ends (home H, Alliance), and is
    // not freed on the way
    [Fact]
    public void B2e22_AbductedCharacterTravelsWithTheTeam()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Alliance, 100); w.Hold(T, Side.Empire, 0);
        w.Agent(Han, H, com: 100);
        w.Agent(Piett, T);
        w.Start(Side.Alliance, MissionKind.Abduction, new[] { Han }, null, targetChar: Piett);
        w.Game.Step();
        int id = w.LastMission;
        w.RunUntilWorking(id);
        w.RunIteration(id);
        var han = w.Ch.Get(Piett)!;
        Assert.Equal(CharacterStatus.Captured, han.Status);
        Assert.Contains(Piett, w.M.Get(id)!.Captives!);
        for (int i = 0; i < 100 && w.M.Get(id) != null; i++)
        {
            w.Game.Step();
            Assert.Equal(CharacterStatus.Captured, han.Status);
        }
        Assert.Null(w.M.Get(id));
        Assert.Equal((CharacterStatus.Captured, H, 0), (han.Status, han.HeldAt, han.HeldAboard));
        Assert.Equal(H, w.Ch.LocationOf(han));
    }

    // B2e-23: Mon Mothma freed (Rescue, liberation to neutral, escape) on an Imperial world; her last mission was to end at Alliance world X:
    // she sets off the same step for X
    [Theory]
    [InlineData("rescue")]
    [InlineData("neutral")]
    [InlineData("escape")]
    public void B2e23_FreedCharacterGoesToHerLastMissionsReturnPlace(string how)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0); w.Hold(X, Side.Alliance, 100); w.Hold(H, Side.Alliance, 100);
        var mm = w.Agent(MonMothma, T);
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, mm, Side.Empire, null, CharacterCause.Debug));
        mm.ReturnPlace = X; // as a capture that ended her mission to X would have saved it
        StepWith(w, "5.3", ctx =>
        {
            if (how == "rescue") w.Ch.Free(ctx, mm, MissionModule.CauseRescue);
            else if (how == "escape") w.Ch.Escape(ctx, mm);
            else { w.Hold(T, null, 50); w.Ch.Liberate(ctx, T); }
        });
        Assert.Equal(CharacterStatus.Free, mm.Status);
        Assert.Null(mm.System);
        Assert.Equal((T, X), (mm.Transit!.Path[0], mm.Transit.Path[^1]));
        Assert.Single(w.Events<CharacterDeparted>(), e => e.Character == MonMothma);
    }

    // B2e-24: no return place; from T, Alliance H (nearer) is blockaded and Alliance Alderaan (farther) is not: she goes to Alderaan
    [Fact]
    public void B2e24_TheNearestUnblockadedOwnWorldComesFirst()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0); w.Hold(H, Side.Alliance, 100); w.Hold(Alderaan, Side.Alliance, 100);
        Fleet(w, Side.Empire, H, Isd);
        var mm = w.Agent(MonMothma, T);
        StepWith(w, "5.2", ctx => { w.Host.Fleets.RefreshBlockadeFlag(ctx, H, ctx.Day + 1); w.Ch.Capture(ctx, mm, Side.Empire, null, CharacterCause.Debug); });
        Assert.True(w.Game.State.Blockades.ContainsKey(H));
        StepWith(w, "5.3", ctx => w.Ch.Free(ctx, mm, MissionModule.CauseRescue));
        Assert.Equal(Alderaan, mm.Transit!.Path[^1]);
    }

    // B2e-25: freed on a world his own side holds: stays; freed on an enemy world while a battle is pending there: waits, and leaves once
    // nothing is pending (re-checked at slot 6.10 of each later day, DEFAULT)
    [Fact]
    public void B2e25_StaysOnOwnGround_WaitsWhileABattleIsPending()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Alliance, 100); w.Hold(T, Side.Empire, 0);
        var han = w.Agent(Han, H);
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug));
        StepWith(w, "5.3", ctx => w.Ch.Free(ctx, han, MissionModule.CauseRescue));
        Assert.Equal(H, han.System); Assert.Null(han.Transit);

        var leia = w.Agent(Leia, T);
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, leia, Side.Empire, null, CharacterCause.Debug));
        int a = Fleet(w, Side.Alliance, T, Corvette); Fleet(w, Side.Empire, T, Isd);
        w.Ch.Free(null, leia, MissionModule.CauseRescue); // between Steps: both sides' fleets at T, a battle is pending
        Assert.True(leia.AutoroutePending);
        Assert.Equal(T, leia.System);
        w.Submit(seq => new MoveFleetOrder(seq, Side.Alliance, a, X)); // the Alliance fleet leaves (or the battle settles it)
        for (int i = 0; i < 10 && leia.Transit == null; i++) w.Game.Step();
        Assert.False(leia.AutoroutePending);
        Assert.Equal(H, leia.Transit!.Path[^1]);
    }

    // QA-9 (Round 10, seed 4): Mon Mothma, caught in a duel and then rescued on an Imperial world, sat there injured for 1,400 days (she cannot
    // heal on enemy ground and nothing moved her). Now she sets off for her side's world, arrives, and heals there.
    [Fact]
    public void QA9_RescuedKeyCharacterGoesHomeAndHeals()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Alliance, 100); w.Hold(T, Side.Empire, 0);
        var mm = w.Agent(MonMothma, T);
        StepWith(w, "5.2", ctx => { w.Ch.AddInjury(ctx, mm, 20, CharacterCause.Debug); w.Ch.Capture(ctx, mm, Side.Empire, null, CharacterCause.Debug); });
        StepWith(w, "5.3", ctx => w.Ch.Free(ctx, mm, MissionModule.CauseRescue));
        Assert.Equal(CharacterStatus.Injured, mm.Status);
        w.Run(60);
        Assert.Equal(H, mm.System);
        Assert.Equal(CharacterStatus.Free, mm.Status); // healed on her own side's ground
    }

    // Work 12, 13: the new fields are saved (a save and load keep a prisoner aboard and the routing fields)
    [Fact]
    public void HeldAboardAndRoutingFieldsAreSaved()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        int fleet = Fleet(w, Side.Empire, T, Isd);
        var han = w.Agent(Han, T);
        han.Skills[(int)Rebellion.Core.Characters.Skill.Combat] = 0;
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, han, Side.Empire, null, CharacterCause.Debug));
        han.HeldAboard = fleet; han.HeldAboardShip = 5; han.HeldAt = null; han.ReturnPlace = X; han.AutoroutePending = true; han.JoinFleet = 7;
        string hash = w.Host.StateHash();
        w.Reload();
        var h2 = w.Ch.Get(Han)!;
        Assert.Equal((fleet, 5, X, true, 7), (h2.HeldAboard, h2.HeldAboardShip, h2.ReturnPlace, h2.AutoroutePending, h2.JoinFleet));
        Assert.Equal(hash, w.Host.StateHash());
    }
}
