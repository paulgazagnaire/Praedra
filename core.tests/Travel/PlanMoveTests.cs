using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Praedra.Core.Travel;

namespace Praedra.Core.Tests.Travel;

// m1-travel §Path rule (3) waypoints and known traps, (5) the preview is the path flown. On the v6 galaxy with GameHost.
public class PlanMoveTests
{
    private static (GameHost h, int fleet) Corvette(GameSettings? s = null)
    {
        var h = GameHost.New(1, s);
        int f = h.Fleets.AddFleet(Side.Collective, "F", new[] { "Dodger" }, "Classis");
        return (h, f);
    }

    private static OrderAccepted Accepted(GameHost h, long seq) => h.Game.Events.OfType<OrderAccepted>().Single(e => e.Sequence == seq);

    [Fact]
    public void PM1_ThePreviewIsThePathTheOrderFlies() // m1-travel PM1
    {
        var (h, f) = Corvette();
        var plan = h.PlanMove(Side.Collective, f, "The Thaw");
        Assert.True(plan.Ok);
        Assert.Equal("Classis", plan.Path[0]); Assert.Equal("The Thaw", plan.Path[^1]);
        Assert.Equal(plan.Path.Count - 1, plan.HopTicks.Count);
        Assert.Equal(plan.HopTicks.Count, plan.RouteHop.Count);
        Assert.Contains(true, plan.RouteHop); // rides a route
        Assert.False(plan.RouteHop[^1]);      // The Thaw is off the routes: the last hop is a free jump
        long seq = h.Fleets.SubmitMove(Side.Collective, f, "The Thaw");
        h.Game.Step();
        var a = Accepted(h, seq);
        Assert.Equal(plan.Path, a.Path!.Items);
        Assert.Equal(plan.ArrivalDay, a.ArrivalDay);
    }

    [Fact]
    public void PM2_WaypointsForceThePathAndTheOrderCarriesThem() // m1-travel PM2
    {
        var (h, f) = Corvette();
        var fast = h.PlanMove(Side.Collective, f, "The Thaw");
        Assert.DoesNotContain("Charter", fast.Path);
        var via = h.PlanMove(Side.Collective, f, "The Thaw", new[] { "Charter" });
        Assert.True(via.Ok);
        // v6 re-pin 2026-10-24: Classis is no longer linked to Charter; the path flies the router's own way to the waypoint
        // (Classis, Quies, Consolatio, Charter), then on to The Thaw.
        Assert.Equal(h.PlanMove(Side.Collective, f, "Charter").Path, via.Path.Take(4));
        Assert.Equal("The Thaw", via.Path[^1]);
        Assert.True(via.ArrivalDay >= fast.ArrivalDay); // the fastest path was the router's own choice
        long seq = h.Fleets.SubmitMove(Side.Collective, f, "The Thaw", new[] { "Charter" });
        h.Game.Step();
        var a = Accepted(h, seq);
        Assert.Equal(via.Path, a.Path!.Items);
        Assert.Equal(via.ArrivalDay, a.ArrivalDay);
    }

    [Fact]
    public void PM3_KnownInterdictorOnTheWayTurnsThePlanRed() // m1-travel PM3
    {
        var (h, f) = Corvette();
        var path = h.PlanMove(Side.Collective, f, "The Thaw").Path;
        string seenTrap = path[2], unseenTrap = path[^3];
        h.Fleets.SetOwner(seenTrap, Side.Collective); // live access: the Collective sees the fleets there
        h.Fleets.AddFleet(Side.Hand, "Net", new[] { "Subpoena" }, seenTrap);
        h.Fleets.AddFleet(Side.Hand, "Hidden", new[] { "Subpoena" }, unseenTrap); // nobody looks there
        h.Fleets.SetOwner("The Thaw", Side.Collective);
        h.Fleets.AddFleet(Side.Hand, "AtTheEnd", new[] { "Subpoena" }, "The Thaw"); // the destination: contact, not a pull-out
        h.Game.Step();
        var plan = h.PlanMove(Side.Collective, f, "The Thaw");
        Assert.True(plan.Red);
        var trap = Assert.Single(plan.Traps);
        Assert.Equal(seenTrap, trap.System);
        Assert.Equal(h.Game.Day, trap.SeenDay);
        // a waypoint at the trap still warns: the fleet passes it on the way (the player may force any path, traps included)
        Assert.Equal(seenTrap, Assert.Single(h.PlanMove(Side.Collective, f, "The Thaw", new[] { seenTrap }).Traps).System);
    }

    [Fact]
    public void PM4_NoTrapForTheSidesOwnInterdictor() // m1-travel PM4 (no friendly fire)
    {
        var h = GameHost.New(1);
        int f = h.Fleets.AddFleet(Side.Hand, "F", new[] { "Mandate" }, "Classis");
        var path = h.PlanMove(Side.Hand, f, "The Thaw").Path;
        h.Fleets.SetOwner(path[2], Side.Hand);
        h.Fleets.AddFleet(Side.Hand, "Own", new[] { "Subpoena" }, path[2]);
        h.Game.Step();
        Assert.Empty(h.PlanMove(Side.Hand, f, "The Thaw").Traps);
    }

    [Fact]
    public void PM5_WaypointEdgeCasesAndRejections() // m1-travel PM5 (DEFAULTs: repeats are skipped)
    {
        var (h, f) = Corvette();
        var plain = h.PlanMove(Side.Collective, f, "The Thaw");
        var repeats = h.PlanMove(Side.Collective, f, "The Thaw", new[] { "Classis", "The Thaw" }); // the start and the destination as waypoints
        Assert.Equal(plain.Path, repeats.Path);
        Assert.Equal(PathStatus.UnknownSystem, h.PlanMove(Side.Collective, f, "The Thaw", new[] { "No Such Place" }).Status);
        Assert.Equal(PathStatus.AlreadyThere, h.PlanMove(Side.Collective, f, "Classis", new[] { "Classis" }).Status);
        Assert.Equal(PathStatus.UnknownSystem, h.PlanMove(Side.Hand, f, "The Thaw").Status); // not the side's fleet
        h.Fleets.SubmitMove(Side.Collective, f, "The Thaw");
        h.Game.Step();
        Assert.Equal(PathStatus.InTransit, h.PlanMove(Side.Collective, f, "Amargura").Status);
    }

    [Fact]
    public void PM6_ClassicFliesStraightAndRoutesGiveNoSpeed() // m1-travel PM6
    {
        var (h, f) = Corvette(new GameSettings { Travel = TravelMode.Straight });
        var plan = h.PlanMove(Side.Collective, f, "The Thaw");
        Assert.Equal(new[] { "Classis", "The Thaw" }, plan.Path);
        Assert.Equal(new[] { false }, plan.RouteHop);
        var via = h.PlanMove(Side.Collective, f, "The Thaw", new[] { "Charter" });
        Assert.Equal(new[] { "Classis", "Charter", "The Thaw" }, via.Path);
    }
}
