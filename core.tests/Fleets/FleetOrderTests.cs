using System.Text.Json;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Tests.Construction;
using Rebellion.Core.Tests.Economy;

namespace Rebellion.Core.Tests.Fleets;

// Tests of m3-fleets (ids F1 to F22 as in the sheet's table, then extra tests named after the rule they check).
// Fixture F: Empire holds Coruscant; spare fleet 200 holds ISD 201, ISD 202, Carrack 203 (completed, at Coruscant); TIE Fighter squadrons
// 301 to 307 (the sheet lists 301 to 306; 307 is the "one more" of F11) and Alliance X-wing squadrons 310 and 311, all based at Coruscant;
// Stormtrooper regiments 401 to 403 on Coruscant. The first id the fleet counter hands out is 204, as the sheet's F3 expects.
// Hook time T = K + 1; events of orders carry Day = K. The real galaxy, ship data and costs files are used.
public class FleetOrderTests
{
    private const string Home = "Coruscant", Far = "Corellia";
    private const string Isd = "Imperial Star Destroyer", Carrack = "Carrack Light Cruiser", Tie = "TIE Fighter", Xwing = "X-wing";
    private static readonly Side Emp = Side.Empire, Reb = Side.Alliance;

    private static BuildWorld F(bool wireFleets = false)
    {
        var w = new BuildWorld(wireFleets: wireFleets);
        w.Hold(Home, Emp);
        w.Eco.AddObject(Emp, Isd, null, true, 201);
        w.Eco.AddObject(Emp, Isd, null, true, 202);
        w.Eco.AddObject(Emp, Carrack, null, true, 203);
        w.Fleets.AddFleetOf(Emp, "Spare", Home, new[] { 201, 202, 203 }, 200);
        w.State.Fleets[200].Spare = true;
        for (int id = 301; id <= 307; id++) w.Eco.AddObject(Emp, Tie, Home, true, id);
        w.Eco.AddObject(Reb, Xwing, Home, true, 310);
        w.Eco.AddObject(Reb, Xwing, Home, true, 311);
        for (int id = 401; id <= 403; id++) w.Eco.AddObject(Emp, "Stormtrooper Regiment", Home, true, id);
        w.E.NextObjectId = 204;
        return w;
    }

    /// <summary>Run one Step and return the rejection reason of an order submitted before it (null = accepted).</summary>
    private static string? Step(BuildWorld w, long seq) { w.Run(1); return w.RejectionOf(seq); }

    /// <summary>Everything an order may change in the fleet and object lists, as text: used to show a rejection changed nothing.</summary>
    private static string Snap(BuildWorld w) => JsonSerializer.Serialize(new { w.State.Fleets, w.State.Economy.Objects, w.State.Economy.NextObjectId });

    private static List<int> Ships(BuildWorld w, int fleet) => w.State.Fleets[fleet].Ships;

    /// <summary>Make fleet 200 hold only ISD 201 (the rest goes to fleet 204): the "ISD 201 (6 berths)" of F11 and F13.</summary>
    private static void LoneIsd(BuildWorld w)
    {
        Assert.Null(Step(w, w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 202, 203 })));
        Assert.Equal(new[] { 201 }, Ships(w, 200));
    }

    // ---------------------------------------------------------------- the spare fleet (F1, F2, F22)

    // m3-fleets F1
    [Fact]
    public void F1_TwoIsdBuiltAtOneYardJoinOneSpareFleet()
    {
        var w = new BuildWorld(wireFleets: true);
        w.Hold(Home, Emp);
        w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(Emp, 1000);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Shipyard, "Imperial Star Destroyer", 2));
        w.RunTo(449);
        var created = Assert.Single(w.Events<FleetCreated>());
        var joined = Assert.Single(w.Events<ShipJoinedFleet>());
        Assert.Equal(FleetCreateCause.Spare, created.Cause);
        Assert.Equal(449, created.Day); // slot 6.6.a at hook time T = K + 1
        Assert.Equal(new[] { joined.Ship }, created.Members.Items);
        Assert.Equal(new ShipJoinedFleet(joined.Index, 449, joined.Ship, Isd, Home, created.Fleet, ShipJoinCause.Completed), joined);
        var f = w.State.Fleets[created.Fleet];
        Assert.True(f.Spare);
        Assert.Equal(Home, f.At);
        Assert.Equal(Emp, f.Side);
        Assert.Null(w.Obj(joined.Ship).System); // a fleet member has no place of its own
        w.RunTo(897);
        Assert.Single(w.Events<FleetCreated>()); // no new fleet: the second ship joins the same spare fleet
        Assert.Equal(2, w.Events<ShipJoinedFleet>().Count);
        Assert.Equal(2, f.Ships.Count);
        Assert.Equal(897, w.Events<ShipJoinedFleet>()[1].Day);
        Assert.Single(w.State.Fleets);
    }

    // m3-fleets F2
    [Fact]
    public void F2_AfterTheSpareFleetDepartsTheNextShipStartsANewSpareFleet()
    {
        var w = new BuildWorld(wireFleets: true);
        w.Hold(Home, Emp); w.Hold(Far, Emp);
        w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(Emp, 1000);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Shipyard, "Imperial Star Destroyer", 3));
        w.RunTo(897);
        int first = Assert.Single(w.Events<FleetCreated>()).Fleet;
        Assert.Equal(2, w.State.Fleets[first].Ships.Count);
        long seq = w.Fleets.SubmitMove(Emp, first, Far);
        w.Run(1);
        Assert.Null(w.RejectionOf(seq));
        Assert.False(w.State.Fleets[first].Spare); // cleared by the accepted MoveFleet
        w.RunTo(1345); // the third ISD completes
        var created = w.Events<FleetCreated>();
        Assert.Equal(2, created.Count);
        Assert.NotEqual(first, created[1].Fleet);
        Assert.Equal(FleetCreateCause.Spare, created[1].Cause);
        var second = w.State.Fleets[created[1].Fleet];
        Assert.True(second.Spare);
        Assert.Equal(Home, second.At);
        Assert.Single(second.Ships);
        Assert.Equal(2, w.State.Fleets[first].Ships.Count); // the departed fleet did not take it
    }

    // m3-fleets F22 (with m2-construction C13)
    [Fact]
    public void F22_ADeliveredShipJoinsTheSpareFleetAtItsDestination()
    {
        var w = new BuildWorld(wireFleets: true);
        w.Hold(Home, Emp); w.Hold(Far, Emp);
        w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(Emp, 1000);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Shipyard, "Imperial Star Destroyer", 1, Far));
        w.RunTo(453);
        Assert.Empty(w.Events<ShipJoinedFleet>()); // still in delivery: in no fleet
        w.RunTo(454);
        var joined = Assert.Single(w.Events<ShipJoinedFleet>());
        var created = Assert.Single(w.Events<FleetCreated>());
        Assert.Equal(ShipJoinCause.Delivered, joined.Cause);
        Assert.Equal(FleetCreateCause.Spare, created.Cause);
        Assert.Equal((454, Far, created.Fleet), (joined.Day, joined.System, joined.Fleet));
        var f = w.State.Fleets[created.Fleet];
        Assert.Equal(Far, f.At);
        Assert.True(f.Spare);
        Assert.Equal(new[] { joined.Ship }, f.Ships);
    }

    // m3-fleets §New ships: squadrons built at a yard stay based, regiments on the system, none enters a fleet
    [Fact]
    public void Built_squadrons_and_regiments_enter_no_fleet()
    {
        var w = new BuildWorld(wireFleets: true);
        w.Hold(Home, Emp);
        w.Yard(Home, FacilityKind.OrbitalShipyard); w.Yard(Home, FacilityKind.TrainingFacility);
        w.Refined(Emp, 1000);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Shipyard, "TIE Fighter", 1));
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Training, "Stormtrooper Regiment", 1));
        w.Run(100);
        Assert.Equal(2, w.Events<UnitCompleted>().Count);
        Assert.Empty(w.State.Fleets);
        var tie = w.Eco.Objects.Single(o => o.Class == Tie);
        Assert.Equal((Home, 0), (tie.System, tie.AboardShip)); // based
        Assert.Single(w.Fleets.BasedSquadrons(Home, Emp));
    }

    // ---------------------------------------------------------------- creating, splitting, merging (F3 to F10)

    // m3-fleets F3
    [Fact]
    public void F3_CreateFleetTakesTwoShipsOutOfTheSpareFleet()
    {
        var w = F();
        long seq = w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 203 });
        Assert.Null(Step(w, seq));
        Assert.Equal(new[] { 201, 203 }, Ships(w, 204));
        Assert.Equal(new[] { 202 }, Ships(w, 200));
        Assert.False(w.State.Fleets[204].Spare);
        Assert.True(w.State.Fleets[200].Spare);
        Assert.Equal((Emp, Home), (w.State.Fleets[204].Side, w.State.Fleets[204].At));
        var e = Assert.Single(w.Events<FleetCreated>());
        Assert.Equal(new FleetCreated(e.Index, 0, 204, Home, new ValueList<int>(new List<int> { 201, 203 }), FleetCreateCause.CreateFleet), e);
        Assert.Equal(204, Assert.Single(w.Events<OrderAccepted>()).Fleet);
    }

    // m3-fleets F4
    [Fact]
    public void F4_CreateFleetOfOneShipIsTooFew()
    {
        var w = F();
        string before = Snap(w);
        Assert.Equal(RejectReason.TooFewShips, Step(w, w.Fleets.SubmitCreateFleet(Emp, new[] { 201 })));
        Assert.Equal(before, Snap(w));
    }

    // m3-fleets F5
    [Fact]
    public void F5_CreateFleetOfAWholeFleetChangesNothing()
    {
        var w = F();
        string before = Snap(w);
        Assert.Equal(RejectReason.NoChange, Step(w, w.Fleets.SubmitCreateFleet(Emp, new[] { 203, 201, 202 })));
        Assert.Equal(before, Snap(w));
    }

    // m3-fleets F6
    [Fact]
    public void F6_SplitTakesShipsOutOfOneFleet()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 203 })));
        Assert.Equal(new[] { 203 }, Ships(w, 204));
        Assert.Equal(new[] { 201, 202 }, Ships(w, 200));
        var e = Assert.Single(w.Events<FleetCreated>());
        Assert.Equal((204, FleetCreateCause.SplitFleet), (e.Fleet, e.Cause));
    }

    // m3-fleets F7
    [Fact]
    public void F7_SplitOfEveryShipIsRefused()
    {
        var w = F();
        string before = Snap(w);
        Assert.Equal(RejectReason.WholeFleet, Step(w, w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 201, 202, 203 })));
        Assert.Equal(before, Snap(w));
    }

    // m3-fleets F8
    [Fact]
    public void F8_MergeMovesEveryShipAndRemovesTheSourceFleet()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 203 })));
        Assert.Null(Step(w, w.Fleets.SubmitMergeFleets(Emp, 204, 200)));
        Assert.Equal(new[] { 201, 202, 203 }, Ships(w, 200)); // id order
        Assert.DoesNotContain(204, w.State.Fleets.Keys);
        var d = Assert.Single(w.Events<FleetDissolved>());
        Assert.Equal((204, DissolveCause.Merged), (d.Fleet, d.Cause));
        var t = Assert.Single(w.Events<ShipsTransferred>());
        Assert.Equal(new[] { 201, 203 }, t.Ships.Items);
        Assert.Equal(new[] { 204 }, t.FromFleets.Items);
        Assert.Equal(200, t.ToFleet);
    }

    // m3-fleets F9
    [Fact]
    public void F9_TransferIntoAFleetInTransitIsRefused()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 203 })));
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 204, Far)));
        string before = Snap(w);
        Assert.Equal(RejectReason.InTransit, Step(w, w.Fleets.SubmitTransferShips(Emp, new[] { 202 }, 204)));
        Assert.Equal(before, Snap(w));
    }

    // m3-fleets F10
    [Fact]
    public void F10_ASplitAfterAMoveInTheSameStepIsInTransit()
    {
        var w = F();
        long move = w.Fleets.SubmitMove(Emp, 200, Far);
        long split = w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 203 });
        w.Run(1);
        Assert.Null(w.RejectionOf(move));
        Assert.Equal(RejectReason.InTransit, w.RejectionOf(split));
        Assert.Equal(new[] { 201, 202, 203 }, Ships(w, 200));
    }

    // ---------------------------------------------------------------- carrying squadrons (F11 to F13)

    // m3-fleets F11
    [Fact]
    public void F11_AnIsdHoldsSixSquadronsAndNotASeventh()
    {
        var w = F();
        LoneIsd(w);
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302, 303, 304, 305, 306 })));
        var e = Assert.Single(w.Events<SquadronsLoaded>());
        Assert.Equal(new[] { 301, 302, 303, 304, 305, 306 }, e.Squadrons.Items);
        Assert.Equal(new[] { 201, 201, 201, 201, 201, 201 }, e.Ships.Items);
        Assert.Equal((200, Home), (e.Fleet, e.System));
        Assert.All(new[] { 301, 302, 303, 304, 305, 306 }, id => Assert.Equal((201, (string?)null), (w.Obj(id).AboardShip, w.Obj(id).System)));
        Assert.Equal(0, w.Fleets.FreeBerths(w.State.Fleets[200]));
        string before = Snap(w);
        Assert.Equal(RejectReason.NoCapacity, Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 307 })));
        Assert.Equal(before, Snap(w));
    }

    // m3-fleets F12
    [Fact]
    public void F12_LoadingIsAllOrNothingOverTheWholeFleet()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 202 }))); // fleet 200 = ISD 201 + Carrack 203
        Assert.Equal(new[] { 201, 203 }, Ships(w, 200));
        Assert.Equal(6, w.Fleets.FreeBerths(w.State.Fleets[200])); // the Carrack has none
        string before = Snap(w);
        Assert.Equal(RejectReason.NoCapacity, Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302, 303, 304, 305, 306, 307 })));
        Assert.Equal(before, Snap(w));
        Assert.All(new[] { 301, 307 }, id => Assert.Equal(0, w.Obj(id).AboardShip));
    }

    // m3-fleets F13
    [Fact]
    public void F13_SquadronsAboardNeverSlowTheFleet()
    {
        var w = F();
        LoneIsd(w);
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302, 303, 304, 305, 306 })));
        Assert.Equal(80, w.Fleets.SpeedOf(w.State.Fleets[200])); // TIE rating 0 is ignored
        long mv = w.Fleets.SubmitMove(Emp, 200, Far);
        w.Run(1);
        Assert.Null(w.RejectionOf(mv));
        var acc = w.Events<OrderAccepted>().Last();
        Assert.Equal((5, 200), (acc.TotalTicks, acc.Fleet)); // 20 * 80 / 300 = 5 days
        w.RunTo(acc.ArrivalDay);
        Assert.Equal(Far, w.State.Fleets[200].At);
        Assert.All(new[] { 301, 306 }, id => Assert.Equal(201, w.Obj(id).AboardShip)); // they went along
    }

    // ---------------------------------------------------------------- unescorted squadrons (F14 to F16)

    // m3-fleets F14
    [Fact]
    public void F14_ATieSquadronCannotFlyAlone()
    {
        var w = F();
        string before = Snap(w);
        Assert.Equal(RejectReason.Immobile, Step(w, w.Fleets.SubmitMoveSquadrons(Emp, new[] { 301 }, Far)));
        Assert.Equal(before, Snap(w));
        Assert.Empty(w.Events<FleetCreated>()); // no fleet created
    }

    // m3-fleets F15
    [Fact]
    public void F15_XWingsFlyAsASquadronOnlyFleetAndLandBasedAtTheDestination()
    {
        var w = F();
        long seq = w.Fleets.SubmitMoveSquadrons(Reb, new[] { 311, 310 }, Far);
        w.Run(1);
        Assert.Null(w.RejectionOf(seq));
        var acc = w.Events<OrderAccepted>().Single();
        int fid = acc.Fleet!.Value;
        Assert.Equal((4, 4), (acc.TotalTicks, acc.ArrivalDay)); // 20 * 60 / 300 = 4 days
        var created = Assert.Single(w.Events<FleetCreated>());
        Assert.Equal(FleetCreateCause.Squadrons, created.Cause);
        Assert.Equal(new[] { 310, 311 }, created.Members.Items);
        var f = w.State.Fleets[fid];
        Assert.Equal(60, w.Fleets.SpeedOf(f));
        Assert.Empty(f.Ships);
        Assert.False(f.Spare);
        Assert.All(new[] { 310, 311 }, id => Assert.Null(w.Obj(id).System)); // flying: not based
        w.RunTo(3);
        Assert.Contains(fid, w.State.Fleets.Keys);
        w.RunTo(4);
        Assert.Equal(4, Assert.Single(w.Events<FleetArrived>()).Day);
        var d = Assert.Single(w.Events<FleetDissolved>());
        Assert.Equal((fid, DissolveCause.SquadronsArrived, 4), (d.Fleet, d.Cause, d.Day));
        var based = Assert.Single(w.Events<SquadronsBased>());
        Assert.Equal(new[] { 310, 311 }, based.Squadrons.Items);
        Assert.Equal(Far, based.System);
        Assert.DoesNotContain(fid, w.State.Fleets.Keys);
        Assert.All(new[] { 310, 311 }, id => Assert.Equal((Far, 0), (w.Obj(id).System, w.Obj(id).AboardShip)));
        // order of events: arrival, then dissolve, then the contacts of step 5
        var kinds = w.Game.Events.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf(nameof(FleetArrived)) < kinds.IndexOf(nameof(FleetDissolved)));
    }

    // m3-fleets F16
    [Fact]
    public void F16_ASquadronFleetStoppedAtAnIntermediateSystemLandsThere()
    {
        var w = F();
        w.Fleets.AddFleet(Emp, "Picket", new[] { Isd }, Far); // armed enemy on the way to Hoth
        long seq = w.Fleets.SubmitMoveSquadrons(Reb, new[] { 310, 311 }, "Hoth");
        w.Run(1);
        Assert.Null(w.RejectionOf(seq));
        int fid = w.Events<OrderAccepted>().Single().Fleet!.Value;
        w.RunTo(4);
        var stop = Assert.Single(w.Events<ForcedStop>());
        Assert.Equal((fid, Far, 4), (stop.Fleet, stop.System, stop.Day));
        Assert.Equal(DissolveCause.SquadronsArrived, Assert.Single(w.Events<FleetDissolved>()).Cause);
        Assert.DoesNotContain(fid, w.State.Fleets.Keys);
        Assert.All(new[] { 310, 311 }, id => Assert.Equal(Far, w.Obj(id).System));
    }

    // m3-fleets §Unescorted squadrons: MoveSquadrons rejections
    [Fact]
    public void MoveSquadrons_rejections()
    {
        var w = F();
        LoneIsd(w);
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301 })));
        string before = Snap(w);
        var seqs = new[]
        {
            w.Fleets.SubmitMoveSquadrons(Reb, new[] { 310, 310 }, Far),      // duplicate
            w.Fleets.SubmitMoveSquadrons(Reb, new[] { 999 }, Far),           // unknown
            w.Fleets.SubmitMoveSquadrons(Reb, new[] { 301 }, Far),           // the Empire's squadron
            w.Fleets.SubmitMoveSquadrons(Emp, new[] { 301 }, Far),           // aboard
            w.Fleets.SubmitMoveSquadrons(Reb, new[] { 310 }, "Nowhere"),
            w.Fleets.SubmitMoveSquadrons(Reb, new[] { 310 }, Home),          // already there
            w.Fleets.SubmitMoveSquadrons(Reb, new[] { 310 }, "Ilum"),        // unreachable
            w.Fleets.SubmitMoveSquadrons(Reb, Array.Empty<int>(), Far),
        };
        w.Run(1);
        Assert.Equal(
            new[] { RejectReason.DuplicateSquadron, RejectReason.UnknownSquadron, RejectReason.NotYours, RejectReason.SquadronAboard,
                    RejectReason.UnknownSystem, RejectReason.AlreadyThere, RejectReason.Unreachable, RejectReason.EmptySelection },
            seqs.Select(w.RejectionOf));
        Assert.Equal(before, Snap(w));
    }

    // m3-fleets §Unescorted squadrons: two squadrons based at different systems are NotSameSystem
    [Fact]
    public void MoveSquadrons_from_two_systems_is_refused()
    {
        var w = F();
        w.Eco.AddObject(Reb, Xwing, Far, true, 312);
        Assert.Equal(RejectReason.NotSameSystem, Step(w, w.Fleets.SubmitMoveSquadrons(Reb, new[] { 310, 312 }, "Hoth")));
    }

    // m3-fleets §Unescorted squadrons: the slowest rating decides, any rating 0 makes it immobile
    [Fact]
    public void A_squadron_fleet_flies_at_its_largest_rating()
    {
        var w = F();
        w.Eco.AddObject(Emp, "TIE Defender", Home, true, 320);
        Assert.Equal(RejectReason.Immobile, Step(w, w.Fleets.SubmitMoveSquadrons(Emp, new[] { 320, 301 }, Far)));
        Assert.Null(Step(w, w.Fleets.SubmitMoveSquadrons(Emp, new[] { 320 }, Far)));
        Assert.Equal(4, w.Events<OrderAccepted>().Single().TotalTicks); // Defender 60
    }

    // ---------------------------------------------------------------- unloading, determinism, scrapping (F17 to F19)

    // m3-fleets F17
    [Fact]
    public void F17_UnloadingWhereTheSideDoesNotHoldTheSystemIsRefused()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302 })));
        w.Fleets.SetOwner(Home, Reb);
        string before = Snap(w);
        Assert.Equal(RejectReason.NotYourSystem, Step(w, w.Fleets.SubmitUnloadSquadrons(Emp, 200, new[] { 301 })));
        Assert.Equal(before, Snap(w));
        w.Fleets.SetOwner(Home, Emp);
        Assert.Null(Step(w, w.Fleets.SubmitUnloadSquadrons(Emp, 200, new[] { 301 })));
        var e = Assert.Single(w.Events<SquadronsUnloaded>());
        Assert.Equal(new[] { 301 }, e.Squadrons.Items);
        Assert.Equal(new[] { 201 }, e.Ships.Items);
        Assert.Equal(Home, e.System);
        Assert.Equal((Home, 0), (w.Obj(301).System, w.Obj(301).AboardShip));
        Assert.Equal(201, w.Obj(302).AboardShip); // the other stays aboard
    }

    /// <summary>The scripted run behind F18: most fleet orders of F3 to F17 in one game, on a seeded generator (the stub of F() would hide draws).</summary>
    private static BuildWorld Scenario(ulong seed)
    {
        var w = new BuildWorld(seed);
        w.Hold(Home, Emp);
        w.Eco.AddObject(Emp, Isd, null, true, 201); w.Eco.AddObject(Emp, Isd, null, true, 202); w.Eco.AddObject(Emp, Carrack, null, true, 203);
        w.Fleets.AddFleetOf(Emp, "Spare", Home, new[] { 201, 202, 203 }, 200);
        w.State.Fleets[200].Spare = true;
        for (int id = 301; id <= 307; id++) w.Eco.AddObject(Emp, Tie, Home, true, id);
        w.Eco.AddObject(Reb, Xwing, Home, true, 310); w.Eco.AddObject(Reb, Xwing, Home, true, 311);
        w.E.NextObjectId = 204;
        w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 203 }); w.Run(1);                 // fleet 204 = {201, 203}; 200 = {202}
        w.Fleets.SubmitLoadSquadrons(Emp, 204, new[] { 301, 302, 303 }); w.Run(1);
        w.Fleets.SubmitTransferShips(Emp, new[] { 202 }, 204); w.Run(1);               // 200 dissolves (Empty)
        w.Fleets.SubmitSplitFleet(Emp, 204, new[] { 203 }); w.Run(1);                  // fleet 205 = {203}
        w.Fleets.SubmitMergeFleets(Emp, 205, 204); w.Run(1);                           // 205 dissolves (Merged)
        w.Fleets.SubmitUnloadSquadrons(Emp, 204, new[] { 302 }); w.Run(1);
        w.Fleets.SubmitLoadSquadrons(Emp, 204, new[] { 301 }); w.Run(1);              // rejected: already aboard
        w.Fleets.SubmitMoveSquadrons(Reb, new[] { 310, 311 }, "Hoth"); w.Run(1);
        w.Fleets.SubmitMove(Emp, 204, Far); w.Run(30);
        return w;
    }

    // m3-fleets F18
    [Fact]
    public void F18_TwoSeedsGiveTheSameWorldAndEventsAndNoRandomDraw()
    {
        var a = Scenario(1); var b = Scenario(2);
        Assert.Equal(0, a.Game.Rng.Draws);
        Assert.Equal(0, b.Game.Rng.Draws);
        Assert.Equal(GameSave.WorldHash(a.Game), GameSave.WorldHash(b.Game));
        Assert.Equal(a.Game.Events.Count, b.Game.Events.Count);
        Assert.True(a.Game.Events.SequenceEqual(b.Game.Events));
        Assert.NotEmpty(a.Events<SquadronsLoaded>());
        Assert.NotEmpty(a.Events<FleetDissolved>());
    }

    // m3-fleets F19
    [Fact]
    public void F19_ScrappingALoadedShipLandsItsUnitsOnTheSystem()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302, 303, 304, 305, 306 })));
        foreach (var id in new[] { 401, 402, 403 }) w.Obj(id).AboardShip = 201; // aboard regiments: the troops package loads them; set directly here
        foreach (var id in new[] { 401, 402, 403 }) w.Obj(id).System = null;
        int before = w.Eco.Load(Emp);
        w.Scrap(Emp, 201);
        w.Run(1);
        Assert.Equal(before - 71, w.Eco.Load(Emp)); // maintenance falls by the ship's 71 only
        Assert.Null(w.Eco.Find(201));
        Assert.Equal(new[] { 202, 203 }, Ships(w, 200));
        foreach (var id in new[] { 301, 302, 303, 304, 305, 306, 401, 402, 403 })
            Assert.Equal((Home, 0), (w.Obj(id).System, w.Obj(id).AboardShip));
        Assert.Equal(6, w.Fleets.BasedSquadrons(Home, Emp).Count(o => o.Id <= 306));
        var based = Assert.Single(w.Events<SquadronsBased>());
        Assert.Equal((6, Home), (based.Squadrons.Items.Count, based.System));
        Assert.Single(w.Events<ObjectScrapped>());
    }

    // m3-fleets §What a fleet is invariant 1: a fleet that loses its last ship is removed in the same order
    [Fact]
    public void A_fleet_that_loses_its_last_ship_is_dissolved()
    {
        var w = F();
        LoneIsd(w);
        w.Scrap(Emp, 201);
        w.Run(1);
        Assert.DoesNotContain(200, w.State.Fleets.Keys);
        var d = Assert.Single(w.Events<FleetDissolved>());
        Assert.Equal((200, DissolveCause.Empty), (d.Fleet, d.Cause));
        Assert.Contains(204, w.State.Fleets.Keys);
    }

    // m3-fleets §Carrying rules: scrapping a ship of a fleet on a lane lands its units at the system the fleet left (DEFAULT, the sheet lacks the case)
    [Fact]
    public void Scrapping_a_loaded_ship_in_flight_lands_its_units_where_the_fleet_left()
    {
        var w = F();
        LoneIsd(w);
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302 })));
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, Far)));
        w.Scrap(Emp, 201);
        w.Run(1);
        Assert.Equal((Home, 0), (w.Obj(301).System, w.Obj(301).AboardShip));
        Assert.DoesNotContain(200, w.State.Fleets.Keys);
    }

    // ---------------------------------------------------------------- unarmed fleets and presence (F20, F21)

    // m3-fleets F20
    [Fact]
    public void F20_AFleetOfOneBulkTransportIsUnarmedButStillAFleetAndCarriesSixRegiments()
    {
        var w = new BuildWorld();
        int f = w.Fleets.AddFleet(Reb, "Freighter", new[] { "Bulk Transport" }, Home);
        var fleet = w.State.Fleets[f];
        Assert.False(CombatStrength.IsArmed(w.Fleets.Classes(fleet)));
        Assert.Equal(0, w.Fleets.StrengthOf(fleet));
        Assert.Equal(6, w.Fleets.FreeTroopSlots(fleet));
        Assert.Equal(0, w.Fleets.FreeBerths(fleet));
        // as an enemy at the middle of a trip it does not stop a passing fleet (m1 B3 with a real fleet)
        var w2 = new World(World.BCMap());
        w2.Add(Side.Alliance, "Freight", "B", "Bulk Transport");
        int pass = w2.Add(Side.Empire, "Red", "A", Isd);
        w2.Move(Side.Empire, pass, "C");
        w2.Run(40);
        Assert.Empty(w2.Events<ForcedStop>());
    }

    // m3-fleets F21
    [Fact]
    public void F21_ABasedSquadronIsNotAFleetForTheForcedStopButCountsAsPresenceForTheBlockadeFlag()
    {
        var w = new BuildWorld();
        // an Alliance squadron based at Corellia; no Alliance fleet there
        w.Hold(Far, Reb);
        w.Eco.AddObject(Reb, Xwing, Far, true);
        int empire = w.Fleets.AddFleet(Emp, "Red", new[] { Isd }, Home);
        w.Fleets.SubmitMove(Emp, empire, "Hoth"); // passes Corellia
        w.Run(8);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Contains(w.Events<FleetPassed>(), e => e.System == Far);

        // the Classic blockade flag: an Empire fleet alone at an Alliance system blockades; with a based Alliance squadron the battle bit is on
        var w2 = new BuildWorld();
        w2.Hold(Far, Reb);
        w2.Fleets.AddFleet(Emp, "Raiders", new[] { Isd }, Far);
        Assert.True(w2.Fleets.IsBlockadedClassic(Far));
        w2.Eco.AddObject(Reb, Xwing, Far, true);
        Assert.False(w2.Fleets.IsBlockadedClassic(Far));
    }

    // ---------------------------------------------------------------- order rejections and what an order carries along

    // m3-fleets §Orders: common rejections
    [Fact]
    public void Common_rejections_for_ships_and_fleets()
    {
        var w = F();
        w.Eco.AddObject(Reb, Isd, null, true, 250); // a foreign ship (any side may own the class in a test)
        w.Fleets.AddFleetOf(Reb, "Rebels", Home, new[] { 250 }, 251);
        w.E.NextObjectId = 260;
        string before = Snap(w);
        var seqs = new[]
        {
            w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 201 }),             // DuplicateShip
            w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 999 }),             // UnknownShip
            w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 310 }),             // a squadron is not a ship: UnknownShip
            w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 250 }),             // NotYours
            w.Fleets.SubmitSplitFleet(Emp, 999, new[] { 201 }),              // UnknownFleet
            w.Fleets.SubmitSplitFleet(Emp, 251, new[] { 250 }),              // NotYours (a foreign fleet)
            w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 250 }),              // NotYours (foreign ship)
            w.Fleets.SubmitSplitFleet(Emp, 200, Array.Empty<int>()),         // EmptySelection
            w.Fleets.SubmitTransferShips(Emp, new[] { 201 }, 999),           // UnknownFleet
            w.Fleets.SubmitTransferShips(Emp, new[] { 201 }, 251),           // NotYours
            w.Fleets.SubmitTransferShips(Emp, new[] { 201 }, 200),           // SameFleet
            w.Fleets.SubmitMergeFleets(Emp, 200, 200),                       // SameFleet
            w.Fleets.SubmitMergeFleets(Emp, 200, 999),                       // UnknownFleet
            w.Fleets.SubmitMergeFleets(Emp, 200, 251),                       // NotYours
            w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 301 }),      // DuplicateSquadron
            w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 999 }),           // UnknownSquadron
            w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 310 }),           // NotYours (Alliance squadron)
            w.Fleets.SubmitUnloadSquadrons(Emp, 200, new[] { 301 }),         // NotAboard
            w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 201, 201 }),         // DuplicateShip
            w.Fleets.SubmitSplitFleet(Emp, 251, new[] { 201 }),              // NotYours (fleet checked first)
        };
        w.Run(1);
        Assert.Equal(
            new[]
            {
                RejectReason.DuplicateShip, RejectReason.UnknownShip, RejectReason.UnknownShip, RejectReason.NotYours,
                RejectReason.UnknownFleet, RejectReason.NotYours, RejectReason.NotYours, RejectReason.EmptySelection,
                RejectReason.UnknownFleet, RejectReason.NotYours, RejectReason.SameFleet, RejectReason.SameFleet,
                RejectReason.UnknownFleet, RejectReason.NotYours, RejectReason.DuplicateSquadron, RejectReason.UnknownSquadron,
                RejectReason.NotYours, RejectReason.NotAboard, RejectReason.DuplicateShip, RejectReason.NotYours,
            },
            seqs.Select(w.RejectionOf));
        Assert.Equal(before, Snap(w));
    }

    // m3-fleets §What a fleet is invariant 3: a queued or in-delivery ship is in no fleet and cannot be named
    [Fact]
    public void A_ship_that_is_not_completed_cannot_be_put_in_a_fleet()
    {
        var w = F();
        int queued = w.Eco.AddObject(Emp, Isd, null, completed: false);
        Assert.Equal(RejectReason.NotCompleted, Step(w, w.Fleets.SubmitCreateFleet(Emp, new[] { 201, queued })));
        Assert.Equal(RejectReason.NotCompleted, Step(w, w.Fleets.SubmitTransferShips(Emp, new[] { queued }, 200)));
    }

    // m3-fleets §Orders: CreateFleet NotSameSystem
    [Fact]
    public void CreateFleet_of_ships_at_two_systems_is_refused()
    {
        var w = F();
        w.Fleets.AddFleet(Emp, "Far", new[] { Isd }, Far);
        int far = w.Fleets.FleetsAt(Far).Single().Ships.Single();
        Assert.Equal(RejectReason.NotSameSystem, Step(w, w.Fleets.SubmitCreateFleet(Emp, new[] { 201, far })));
        // and a fleet in transit anywhere in the list wins: InTransit
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, "Hoth")));
        Assert.Equal(RejectReason.InTransit, Step(w, w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 202 })));
    }

    // m3-fleets §Orders: TransferShips takes ships from several fleets; aboard units go with their ship; emptied fleets go
    [Fact]
    public void TransferShips_moves_ships_with_their_units_and_dissolves_emptied_fleets()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitCreateFleet(Emp, new[] { 202, 203 }))); // fleet 204; 200 = {201}
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302 })));
        Assert.Null(Step(w, w.Fleets.SubmitTransferShips(Emp, new[] { 201, 202 }, 204)));
        Assert.Equal(new[] { 201, 202, 203 }, Ships(w, 204));
        Assert.DoesNotContain(200, w.State.Fleets.Keys); // fleet 200 lost its last ship
        Assert.Equal(201, w.Obj(301).AboardShip); // units stay on their ship
        var t = w.Events<ShipsTransferred>().Single();
        Assert.Equal(new[] { 201 }, t.Ships.Items); // 202 was in fleet 204 already: only 201 moves
        Assert.Equal(new[] { 200 }, t.FromFleets.Items);
        Assert.Equal(204, t.ToFleet);
        var d = Assert.Single(w.Events<FleetDissolved>());
        Assert.Equal((200, DissolveCause.Empty), (d.Fleet, d.Cause));
        Assert.Equal(new[] { 201, 202, 203 }, w.State.Fleets[204].Ships);
    }

    // m3-fleets §Orders: a partial overlap is allowed; only a transfer of ships that are all there already is SameFleet
    [Fact]
    public void TransferShips_with_some_ships_already_there_moves_the_others()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 203 }))); // 204 = {203}
        Assert.Null(Step(w, w.Fleets.SubmitTransferShips(Emp, new[] { 203, 201 }, 204)));
        Assert.Equal(new[] { 201, 203 }, Ships(w, 204));
        Assert.Equal(new[] { 202 }, Ships(w, 200));
    }

    // m3-fleets §New ships: the spare flag is cleared when the fleet is merged into another one (it is removed), and kept by the target
    [Fact]
    public void Merging_keeps_the_targets_spare_flag()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 203 }))); // 204 not spare
        Assert.Null(Step(w, w.Fleets.SubmitMergeFleets(Emp, 200, 204)));          // the spare fleet goes into 204
        Assert.DoesNotContain(200, w.State.Fleets.Keys);
        Assert.False(w.State.Fleets[204].Spare);
        Assert.Equal(3, Ships(w, 204).Count);
    }

    // m3-fleets §Orders: fleet orders need the fleet at a system (InTransit otherwise) for every order that names it
    [Fact]
    public void Fleet_orders_on_a_fleet_in_transit_are_refused()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, Far)));
        var seqs = new[]
        {
            w.Fleets.SubmitSplitFleet(Emp, 200, new[] { 201 }),
            w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301 }),
            w.Fleets.SubmitUnloadSquadrons(Emp, 200, new[] { 301 }),
            w.Fleets.SubmitMergeFleets(Emp, 200, 200),
        };
        w.Run(1);
        Assert.Equal(new[] { RejectReason.InTransit, RejectReason.InTransit, RejectReason.InTransit, RejectReason.SameFleet }, seqs.Select(w.RejectionOf));
    }

    // m3-fleets §Orders (LoadSquadrons rejections)
    [Fact]
    public void LoadSquadrons_rejections()
    {
        var w = F();
        w.Fleets.AddFleet(Emp, "Far", new[] { Isd }, Far);
        int farFleet = w.Fleets.FleetsAt(Far).Single().Id;
        w.Eco.AddObject(Emp, Tie, Home, completed: false);
        int queued = w.E.NextObjectId - 1;
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301 })));
        var seqs = new[]
        {
            w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { queued }),   // NotCompleted
            w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301 }),      // already aboard: not based
            w.Fleets.SubmitLoadSquadrons(Emp, farFleet, new[] { 302 }), // squadron at Coruscant, fleet at Corellia
        };
        w.Run(1);
        Assert.Equal(new[] { RejectReason.NotCompleted, RejectReason.SquadronNotBased, RejectReason.NotAtFleetSystem }, seqs.Select(w.RejectionOf));
    }

    // m3-fleets §Carrying rules: loading assigns squadrons to ships by ascending ship id, each to the first ship with a free berth
    [Fact]
    public void Loading_fills_ships_in_ascending_id_order()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302, 303, 304, 305, 306, 307 })));
        var e = Assert.Single(w.Events<SquadronsLoaded>());
        Assert.Equal(new[] { 201, 201, 201, 201, 201, 201, 202 }, e.Ships.Items); // ISD 201 fills first, the 7th goes to ISD 202
        Assert.Equal(5, w.Fleets.FreeBerths(w.State.Fleets[200]));
        Assert.Equal(new SortedDictionary<int, int> { [201] = 0, [202] = 5, [203] = 0 }, w.Fleets.FreeBerthsByShip(w.State.Fleets[200]));
    }

    // m3-fleets §Carrying rules: the troop side's hook (the troops package loads regiments): capacity per ship, never pooled
    [Fact]
    public void Troop_slots_are_counted_per_ship()
    {
        var w = F();
        var fleet = w.State.Fleets[200];
        Assert.Equal(new SortedDictionary<int, int> { [201] = 3, [202] = 3, [203] = 0 }, w.Fleets.FreeTroopSlotsByShip(fleet));
        w.Obj(401).AboardShip = 201; w.Obj(401).System = null;
        Assert.Equal(5, w.Fleets.FreeTroopSlots(fleet));
        Assert.Equal(new[] { 401 }, w.Fleets.UnitsAboard(201, ObjectCategory.Regiment).Select(o => o.Id));
    }

    // m3-fleets §Carrying rules: "aboard units are not on the system" for the garrison and the presence counts
    [Fact]
    public void Aboard_squadrons_and_regiments_do_not_count_as_on_the_system()
    {
        var w = new EcoWorld(freeShips: true);
        w.Hold(Home, Emp, 25);
        int fleet = w.Fleets.AddFleet(Emp, "F", new[] { Isd }, Home);
        for (int i = 0; i < 3; i++) w.Eco.AddObject(Emp, "Stormtrooper Regiment", Home);
        w.Eco.AddObject(Emp, Tie, Home);
        Assert.Equal(50 - 10 - 6 - 5, w.Eco.DiversionPercent(Home, Emp)); // ship 10, three regiments 6, one based squadron 5
        int ship = w.State.Fleets[fleet].Ships.Single();
        foreach (var o in w.Eco.Objects.Where(o => o.Category is ObjectCategory.Regiment or ObjectCategory.Fighter)) { o.AboardShip = ship; o.System = null; }
        Assert.Equal(50 - 10, w.Eco.DiversionPercent(Home, Emp)); // aboard: only the ship counts
    }

    // m1-travel §Rule (Classic) with the sheet's carrier qualifier
    [Fact]
    public void Damaged_hyperdrive_slows_the_fleet_through_the_registry_object()
    {
        var w = F();
        w.Fleets.DebugOrdersEnabled = true;
        Assert.Equal(80, w.Fleets.SpeedOf(w.State.Fleets[200]));
        Assert.Null(Step(w, w.Fleets.SubmitDebugSetDamage(Emp, 201, 2750, new[] { 0, 0, 0, 0, 2, 0 })));
        Assert.Equal(100, w.Fleets.SpeedOf(w.State.Fleets[200])); // the damaged ISD now rates 100: the largest rating decides
        Assert.Equal(100, w.Fleets.SpeedOfShip(w.Obj(201)));
        Assert.Equal(80, w.Fleets.SpeedOfShip(w.Obj(202)));
    }

    // m3-fleets §Ownership: the save carries fleets, aboard units, hull and counters; a queued fleet order survives the format
    [Fact]
    public void Fleets_aboard_units_and_orders_survive_the_save_format()
    {
        var w = F();
        Assert.Null(Step(w, w.Fleets.SubmitLoadSquadrons(Emp, 200, new[] { 301, 302 })));
        w.Fleets.SubmitCreateFleet(Emp, new[] { 201, 203 });
        w.Fleets.SubmitMoveSquadrons(Reb, new[] { 310 }, Far);
        w.Fleets.SubmitDebugSetDamage(Emp, 201, 10, new[] { 0, 1, 2, 3, 4, 5 });
        var loaded = GameSave.Load(GameSave.Save(w.Game));
        Assert.Equal(GameSave.StateHash(w.Game), GameSave.StateHash(loaded));
        Assert.Equal(new[] { 201, 202, 203 }, loaded.State.Fleets[200].Ships);
        Assert.True(loaded.State.Fleets[200].Spare);
        Assert.Equal(201, loaded.State.Economy.Objects[301].AboardShip);
        Assert.IsType<CreateFleetOrder>(loaded.Queue[0]);
        Assert.Equal(new[] { 201, 203 }, ((CreateFleetOrder)loaded.Queue[0]).Ships);
        Assert.IsType<MoveSquadronsOrder>(loaded.Queue[1]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, ((DebugSetDamageOrder)loaded.Queue[2]).Hits);
    }
}
