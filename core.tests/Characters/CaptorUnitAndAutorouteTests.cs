using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using Praedra.Core.Tests.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Characters;

// QA-7 (where a new prisoner is held) and QA-9 (where a freed, escaped or displaced character goes), 2026-10-18.
// rule: m5-characters-and-force §Capture, injury, death, freeing (B2e §3 "Where a new prisoner is held", "Where a freed, escaped or displaced
// character goes", Work 12, Work 13); m5-missions §4.
public class CaptorUnitAndAutorouteTests
{
    private static int Fleet(MissionWorld w, Side side, string system, params string[] ships) => w.Host.Fleets.AddFleet(side, "F", ships, system);
    private const string Mandate = "Mandate", Corvette = "Dodger";

    private static void StepWith(MissionWorld w, string slot, Action<StepContext> work)
    {
        bool done = false;
        w.Game.RegisterHook(slot, ctx => { if (!done) { done = true; work(ctx); } });
        w.Game.Step();
    }

    // B2e-20: The Chair, on a mission at Hand T, loses the duel to a Collective fleet guard there (its Admiral): captured and aboard that fleet
    // the same step, not freed though T is Hand; he travels with the fleet; T changing hands does not touch him
    [Fact]
    public void B2e20_DuelPrisonerIsHeldAboardTheCaptorFleet()
    {
        // KeyDuelCapture, 2026-10-18: The Chair is a key character, so the DEFAULT (Capped) would add his cap roll; this test checks where a duel
        // prisoner is held, not whether he is taken, so it runs the Classic duel explicitly (the cap roll is covered by m5-missions MI66 to MI70).
        var w = new MissionWorld(new MissionParams { KeyDuelCapture = KeyDuelCapture.Classic });
        w.Hold(H, Side.Hand, 0); w.Hold(T, Side.Hand, 0);
        w.Agent(Chair, H, dip: 50);
        int fleet = Fleet(w, Side.Collective, T, Corvette);
        w.Agent(FortyOne, T, com: 80); w.Ch.SetPost(FortyOne, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 0, 99, 99); // the arrival walk detects, the duel is lost (99), the Hurt roll misses
        w.Start(Side.Hand, MissionKind.Diplomacy, new[] { Chair }, T);
        w.Run(5);
        var emp = w.Ch.Get(Chair)!;
        Assert.Equal(CharacterStatus.Captured, emp.Status);
        Assert.Equal((fleet, (string?)null, T), (emp.HeldAboard, emp.HeldAt, w.Ch.LocationOf(emp)));
        var cap = w.Events<CharacterCaptured>().Single();
        Assert.Equal((MissionModule.CauseMissionDuel, fleet), (cap.Cause, cap.HeldAboard));
        Assert.Empty(w.Events<CharacterFreed>());
        Assert.False(w.M.IsOnMission(Chair));

        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Collective, fleet, X)));
        Assert.Null(w.Ch.LocationOf(emp)); // in transit with the fleet
        w.Run(30);
        Assert.Equal(X, w.Ch.LocationOf(emp));
        StepWith(w, "5.2", ctx => { w.Hold(T, Side.Collective, 100); w.Ch.Liberate(ctx, T); });
        Assert.Equal((CharacterStatus.Captured, fleet), (emp.Status, emp.HeldAboard));
    }

    // Q73 (B2e §3 "A prisoner held aboard stays aboard"): caught by a ship with no commander, his escape place is that ship, so avg = 0 even after
    // an officer joins the fleet; caught by the fleet's Admiral, his place is the fleet and avg counts its characters
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 80)]
    public void Q73_ShipCaptorWithNoCommanderGivesNoGuardAverage(bool admiralAtCapture, int avg)
    {
        var w = new MissionWorld(new MissionParams { KeyDuelCapture = KeyDuelCapture.Classic });
        w.Hold(H, Side.Hand, 0); w.Hold(T, Side.Hand, 0);
        w.Agent(Chair, H, dip: 50);
        int fleet = Fleet(w, Side.Collective, T, Corvette);
        w.Agent(FortyOne, T, com: 80);
        if (admiralAtCapture) w.Ch.SetPost(FortyOne, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 0, 99, 99); // the arrival walk detects, the duel is lost (99), the Hurt roll misses
        w.Start(Side.Hand, MissionKind.Diplomacy, new[] { Chair }, T);
        w.Run(5);
        var emp = w.Ch.Get(Chair)!;
        Assert.Equal((CharacterStatus.Captured, fleet), (emp.Status, emp.HeldAboard));
        Assert.Equal(admiralAtCapture ? 0 : w.Game.State.Fleets[fleet].Ships.Single(), emp.HeldAboardShip);
        if (!admiralAtCapture) w.Ch.SetPost(FortyOne, fleet, CharacterPost.Admiral); // joins after the capture
        emp.NextEscapeDay = w.Game.Day;
        w.Stub.Expect(0, 99, 99);
        w.Game.Step();
        Assert.Equal((avg, 0), w.Events<EscapeAttempted>().Select(e => (e.CaptorAverageCombat, e.CaptorSpecialForces)).Single());
    }

    // CH38 (Prisoners land, New, Paul 2026-10-18): The Chair, caught aboard a Collective fleet at Hand T, stays aboard there; the fleet reaches
    // Collective X and puts him ashore at day end (PrisonerLanded), where he is a prisoner on the planet. With LandPrisoners off (Classic) he stays aboard.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CH38_HoldingFleetLandsPrisonersAtAnOwnSystem(bool land)
    {
        var w = new MissionWorld(new MissionParams { KeyDuelCapture = KeyDuelCapture.Classic }, characters: new CharacterParams { LandPrisoners = land });
        w.Hold(H, Side.Hand, 0); w.Hold(T, Side.Hand, 0); w.Hold(X, Side.Collective, 100);
        w.Agent(Chair, H, dip: 50);
        int fleet = Fleet(w, Side.Collective, T, Corvette);
        w.Agent(FortyOne, T, com: 80); w.Ch.SetPost(FortyOne, fleet, CharacterPost.Admiral);
        w.Stub!.Expect(0, 99, 0, 99, 99); // the arrival walk detects, the duel is lost (99), the Hurt roll misses
        w.Start(Side.Hand, MissionKind.Diplomacy, new[] { Chair }, T);
        w.Run(5);
        var emp = w.Ch.Get(Chair)!;
        Assert.Equal((CharacterStatus.Captured, fleet), (emp.Status, emp.HeldAboard)); // T is not the captor's: no landing
        Assert.Empty(w.Events<PrisonerLanded>());
        Assert.Null(w.Try(seq => new MoveFleetOrder(seq, Side.Collective, fleet, X)));
        w.Run(30);
        Assert.Equal(X, w.Ch.LocationOf(emp));
        if (land)
        {
            Assert.Equal((0, X), (emp.HeldAboard, emp.HeldAt));
            var e = w.Events<PrisonerLanded>().Single();
            Assert.Equal((Chair, Side.Collective, X, fleet), (e.Character, e.Captor, e.System, e.Fleet));
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
        w.Hold(H, Side.Collective, 100); w.Hold(T, null, 50);
        var midwife = w.Agent(Midwife, T);
        int before = w.Stub!.Requests.Count;
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug, unit: new CaptorUnit(0, T)));
        Assert.Equal(CharacterCause.Liberation, w.Events<CharacterFreed>().Single().Cause);
        Assert.Single(w.Events<CharacterCaptured>());
        Assert.Equal(new[] { (0, 1000) }, w.RequestsSince(before).Where(r => r != (0, 70)));
        Assert.Equal(CharacterStatus.Free, midwife.Status);
        Assert.Equal(H, midwife.Transit!.Path[^1]);
    }

    // B2e-22: Abduction succeeds (Midwife takes Admiral Decimus Varo at Hand T): Admiral Decimus Varo is the team's captive, ends where the team ends (home H, Collective), and is
    // not freed on the way
    [Fact]
    public void B2e22_AbductedCharacterTravelsWithTheTeam()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Collective, 100); w.Hold(T, Side.Hand, 0);
        w.Agent(Midwife, H, com: 100);
        w.Agent(Varo, T);
        w.Start(Side.Collective, MissionKind.Abduction, new[] { Midwife }, null, targetChar: Varo);
        w.Game.Step();
        int id = w.LastMission;
        w.RunUntilWorking(id);
        w.RunIteration(id);
        var midwife = w.Ch.Get(Varo)!;
        Assert.Equal(CharacterStatus.Captured, midwife.Status);
        Assert.Contains(Varo, w.M.Get(id)!.Captives!);
        for (int i = 0; i < 100 && w.M.Get(id) != null; i++)
        {
            w.Game.Step();
            Assert.Equal(CharacterStatus.Captured, midwife.Status);
        }
        Assert.Null(w.M.Get(id));
        Assert.Equal((CharacterStatus.Captured, H, 0), (midwife.Status, midwife.HeldAt, midwife.HeldAboard));
        Assert.Equal(H, w.Ch.LocationOf(midwife));
    }

    // B2e-23: Mae Abara freed (Rescue, liberation to neutral, escape) on a Hand world; her last mission was to end at Collective world X:
    // she sets off the same step for X
    [Theory]
    [InlineData("rescue")]
    [InlineData("neutral")]
    [InlineData("escape")]
    public void B2e23_FreedCharacterGoesToHerLastMissionsReturnPlace(string how)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Hand, 0); w.Hold(X, Side.Collective, 100); w.Hold(H, Side.Collective, 100);
        var mm = w.Agent(MaeAbara, T);
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, mm, Side.Hand, null, CharacterCause.Debug));
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
        Assert.Single(w.Events<CharacterDeparted>(), e => e.Character == MaeAbara);
    }

    // B2e-24: no return place; from T, Collective H (nearer) is blockaded and Collective Afar (farther) is not: she goes to Afar
    [Fact]
    public void B2e24_TheNearestUnblockadedOwnWorldComesFirst()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Hand, 0); w.Hold(H, Side.Collective, 100); w.Hold(Afar, Side.Collective, 100);
        Fleet(w, Side.Hand, H, Mandate);
        var mm = w.Agent(MaeAbara, T);
        StepWith(w, "5.2", ctx => { w.Host.Fleets.RefreshBlockadeFlag(ctx, H, ctx.Day + 1); w.Ch.Capture(ctx, mm, Side.Hand, null, CharacterCause.Debug); });
        Assert.True(w.Game.State.Blockades.ContainsKey(H));
        StepWith(w, "5.3", ctx => w.Ch.Free(ctx, mm, MissionModule.CauseRescue));
        Assert.Equal(Afar, mm.Transit!.Path[^1]);
    }

    // B2e-25: freed on a world his own side holds: stays; freed on an enemy world while a battle is pending there: waits, and leaves once
    // nothing is pending (re-checked at slot 6.10 of each later day, DEFAULT)
    [Fact]
    public void B2e25_StaysOnOwnGround_WaitsWhileABattleIsPending()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Collective, 100); w.Hold(T, Side.Hand, 0);
        var midwife = w.Agent(Midwife, H);
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug));
        StepWith(w, "5.3", ctx => w.Ch.Free(ctx, midwife, MissionModule.CauseRescue));
        Assert.Equal(H, midwife.System); Assert.Null(midwife.Transit);

        var ledger = w.Agent(Ledger, T);
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, ledger, Side.Hand, null, CharacterCause.Debug));
        int a = Fleet(w, Side.Collective, T, Corvette); Fleet(w, Side.Hand, T, Mandate);
        w.Ch.Free(null, ledger, MissionModule.CauseRescue); // between Steps: both sides' fleets at T, a battle is pending
        Assert.True(ledger.AutoroutePending);
        Assert.Equal(T, ledger.System);
        w.Submit(seq => new MoveFleetOrder(seq, Side.Collective, a, X)); // the Collective fleet leaves (or the battle settles it)
        for (int i = 0; i < 10 && ledger.Transit == null; i++) w.Game.Step();
        Assert.False(ledger.AutoroutePending);
        Assert.Equal(H, ledger.Transit!.Path[^1]);
    }

    // QA-9 (Round 10, seed 4): Mae Abara, caught in a duel and then rescued on a Hand world, sat there injured for 1,400 days (she cannot
    // heal on enemy ground and nothing moved her). Now she sets off for her side's world, arrives, and heals there.
    [Fact]
    public void QA9_RescuedKeyCharacterGoesHomeAndHeals()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Collective, 100); w.Hold(T, Side.Hand, 0);
        var mm = w.Agent(MaeAbara, T);
        StepWith(w, "5.2", ctx => { w.Ch.AddInjury(ctx, mm, 20, CharacterCause.Debug); w.Ch.Capture(ctx, mm, Side.Hand, null, CharacterCause.Debug); });
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
        w.Hold(T, Side.Hand, 0);
        int fleet = Fleet(w, Side.Hand, T, Mandate);
        var midwife = w.Agent(Midwife, T);
        midwife.Skills[(int)Praedra.Core.Characters.Skill.Combat] = 0;
        StepWith(w, "5.2", ctx => w.Ch.Capture(ctx, midwife, Side.Hand, null, CharacterCause.Debug));
        midwife.HeldAboard = fleet; midwife.HeldAboardShip = 5; midwife.HeldAt = null; midwife.ReturnPlace = X; midwife.AutoroutePending = true; midwife.JoinFleet = 7;
        string hash = w.Host.StateHash();
        w.Reload();
        var h2 = w.Ch.Get(Midwife)!;
        Assert.Equal((fleet, 5, X, true, 7), (h2.HeldAboard, h2.HeldAboardShip, h2.ReturnPlace, h2.AutoroutePending, h2.JoinFleet));
        Assert.Equal(hash, w.Host.StateHash());
    }
}
