using Rebellion.Core.Clock;
using Rebellion.Core.Fleets;

namespace Rebellion.Core.Tests.Fleets;

// rule: m1-clock-and-orders §Order of work, §Orders in, §Events out; m1-travel §Orders in transit.
// Tests K2-K9, K12, K13, K15 plus T19 (rejection while in transit) and the lane invariant. K10 and K11 are here too
// because they need fleets. The real galaxy and the real ship data are used; "ISD fleet" = one Imperial Star Destroyer (speed 80).
public class MovementTests
{
    private const string Isd = "Imperial Star Destroyer";
    private static readonly string[] CoruscantHoth = { "Coruscant", "Corellia", "Hosnian Prime", "Kinyen", "Anoat", "Bespin", "Hoth" };

    private static World Real(ForcedStopTrigger t = ForcedStopTrigger.PaulRule, ulong seed = 1) => new(TestData.RealGalaxy, t, seed);

    [Fact] // m1-clock-and-orders K2
    public void K2_coruscant_to_corellia_accepted_with_5_ticks_and_departs_on_day_0()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        long seq = w.Fleets.SubmitMove(Side.Empire, f, "Corellia");
        w.Run(1);
        var acc = Assert.Single(w.Events<OrderAccepted>());
        Assert.Equal(seq, acc.Sequence);
        Assert.Equal(f, acc.Fleet);
        Assert.Equal(new[] { "Coruscant", "Corellia" }, acc.Path!.Items);
        Assert.Equal(5, acc.TotalTicks);
        Assert.Equal(5, acc.ArrivalDay);
        Assert.Equal(0, acc.Day);
        var dep = Assert.Single(w.Events<FleetDeparted>());
        Assert.Equal(new FleetDeparted(dep.Index, 0, f, "Coruscant", 5), dep);
        Assert.True(acc.Index < dep.Index); // step 1 before step 2
    }

    [Fact] // m1-clock-and-orders K3
    public void K3_in_transit_after_4_steps_arrived_on_day_5_after_the_5th()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        w.Move(Side.Empire, f, "Corellia");
        w.Run(4);
        Assert.Null(w.Fleet(f).At);
        Assert.NotNull(w.Fleet(f).Transit);
        Assert.Empty(w.Events<FleetArrived>());
        w.Run(1);
        var arr = Assert.Single(w.Events<FleetArrived>());
        Assert.Equal(new FleetArrived(arr.Index, 5, f, "Corellia"), arr);
        Assert.Equal(5, w.Game.Day);
        Assert.Equal("Corellia", w.Fleet(f).At);
        Assert.Null(w.Fleet(f).Transit);
        Assert.Equal("Coruscant", w.Fleet(f).PreviousSystem);
    }

    [Fact] // m1-clock-and-orders K4 (numbers agree with the real data and the formula: no mismatch)
    public void K4_coruscant_to_hoth_passes_and_arrives_on_the_sheet_days()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        w.Move(Side.Empire, f, "Hoth");
        w.Run(25);
        var acc = Assert.Single(w.Events<OrderAccepted>());
        Assert.Equal(CoruscantHoth, acc.Path!.Items);
        Assert.Equal(21, acc.TotalTicks);
        Assert.Equal(21, acc.ArrivalDay);
        Assert.Equal(
            new[] { (5, "Corellia"), (6, "Hosnian Prime"), (12, "Kinyen"), (19, "Anoat"), (20, "Bespin") },
            w.Events<FleetPassed>().Select(e => (e.Day, e.System)));
        var arr = Assert.Single(w.Events<FleetArrived>());
        Assert.Equal((21, "Hoth"), (arr.Day, arr.System));
        Assert.Empty(w.Events<ForcedStop>());
    }

    [Fact] // m1-clock-and-orders K5
    public void K5_after_20_steps_on_the_bespin_hoth_hop_after_21_arrived()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        w.Move(Side.Empire, f, "Hoth");
        w.Run(20);
        var t = w.Fleet(f).Transit!;
        Assert.Null(w.Fleet(f).At);
        Assert.Equal(("Bespin", "Hoth"), (t.Path[t.HopIndex], t.Path[t.HopIndex + 1]));
        Assert.Equal(21, t.DueDay);
        Assert.Equal("Bespin", w.Fleet(f).PreviousSystem);
        w.Run(1);
        Assert.Equal("Hoth", w.Fleet(f).At);
        Assert.Null(w.Fleet(f).Transit);
    }

    [Fact] // m1-clock-and-orders K6
    public void K6_two_orders_for_one_fleet_in_one_step_first_accepted_second_rejected_in_transit()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        long s1 = w.Fleets.SubmitMove(Side.Empire, f, "Corellia");
        long s2 = w.Fleets.SubmitMove(Side.Empire, f, "Hoth");
        w.Run(1);
        Assert.Equal(s1, Assert.Single(w.Events<OrderAccepted>()).Sequence);
        var rej = Assert.Single(w.Events<OrderRejected>());
        Assert.Equal((s2, RejectReason.InTransit), (rej.Sequence, rej.Reason));
        Assert.Equal("Corellia", w.Fleet(f).Transit!.Path[^1]); // the first order stands
    }

    [Fact] // m1-clock-and-orders K7; m1-travel T19
    public void K7_T19_order_for_a_fleet_already_moving_is_rejected_and_state_is_unchanged()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        w.Move(Side.Empire, f, "Hoth");
        w.Run(3);
        string before = World.StateJson(w.Game);
        long seq = w.Fleets.SubmitMove(Side.Empire, f, "Corellia");
        w.Game.Step();
        var rej = Assert.Single(w.Events<OrderRejected>());
        Assert.Equal((seq, RejectReason.InTransit), (rej.Sequence, rej.Reason));
        Assert.Equal(CoruscantHoth, w.Fleet(f).Transit!.Path); // path untouched
    }

    [Fact] // m1-clock-and-orders K8
    public void K8_alliance_cannot_move_an_empire_fleet()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        w.Fleets.SubmitMove(Side.Alliance, f, "Corellia");
        w.Run(1);
        Assert.Equal(RejectReason.NotYours, Assert.Single(w.Events<OrderRejected>()).Reason);
        Assert.Equal("Coruscant", w.Fleet(f).At);
        Assert.Null(w.Fleet(f).Transit);
    }

    [Fact] // m1-clock-and-orders §Orders in: the other MoveFleet rejections; m1-travel T8, T20
    public void Move_order_rejections_unknown_fleet_immobile_already_there_unknown_system_unreachable()
    {
        var w = Real();
        int isd = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        int tie = w.Fleets.AddFleet(Side.Empire, "Ties", Array.Empty<string>(), "Coruscant", new[] { "TIE Fighter" });
        w.Fleets.SubmitMove(Side.Empire, 99, "Corellia");
        w.Fleets.SubmitMove(Side.Empire, tie, "Corellia");
        w.Fleets.SubmitMove(Side.Empire, isd, "Coruscant");
        w.Fleets.SubmitMove(Side.Empire, isd, "Nowhere");
        w.Fleets.SubmitMove(Side.Empire, isd, "Ilum");
        string before = World.StateJson(w.Game);
        w.Run(1);
        Assert.Equal(
            new[] { RejectReason.UnknownFleet, RejectReason.Immobile, RejectReason.AlreadyThere, RejectReason.UnknownSystem, RejectReason.Unreachable },
            w.Events<OrderRejected>().Select(e => e.Reason));
        Assert.Empty(w.Events<OrderAccepted>());
        Assert.Empty(w.Events<FleetDeparted>());
        Assert.Equal(before, World.StateJson(w.Game)); // rejection never changes state
    }

    [Fact] // m1-clock-and-orders K9
    public void K9_orders_from_both_sides_in_either_submission_order_give_the_same_hash()
    {
        string Run(bool empireFirst)
        {
            var w = Real();
            int e = w.Add(Side.Empire, "Red", "Coruscant", Isd);
            int a = w.Add(Side.Alliance, "Blue", "Corellia", "Corellian Corvette");
            if (empireFirst) { w.Move(Side.Empire, e, "Hoth"); w.Move(Side.Alliance, a, "Bespin"); }
            else { w.Move(Side.Alliance, a, "Bespin"); w.Move(Side.Empire, e, "Hoth"); }
            w.Run(15);
            return GameSave.StateHash(w.Game);
        }
        Assert.Equal(Run(true), Run(false));
    }

    // A busy fixed scenario: one mover, one picket that forces a stop, a second order after the stop.
    private static void Scenario(World w, int fromDay, int toDay)
    {
        for (int d = fromDay; d < toDay; d++)
        {
            if (d == 0) w.Move(Side.Empire, 1, "Hoth");
            if (d == 22) w.Move(Side.Empire, 1, "Hoth"); // after the stop at Anoat (day 19)
            w.Game.Step();
        }
    }

    private static World ScenarioWorld(ulong seed)
    {
        var w = Real(seed: seed);
        w.Add(Side.Empire, "Red", "Coruscant", Isd);                       // fleet 1
        w.Add(Side.Alliance, "Picket", "Anoat", "Corellian Corvette");     // fleet 2
        w.Fleets.SetOwner("Anoat", Side.Alliance);
        w.Fleets.SetOwner("Hoth", Side.Alliance); // the mover ends up alone at an Alliance system: Classic blockade
        return w;
    }

    [Fact] // m1-clock-and-orders K10 (movement only: no random numbers, so the seed cannot matter)
    public void K10_movement_draws_no_random_numbers_and_the_seed_does_not_change_the_world()
    {
        var a = ScenarioWorld(111); Scenario(a, 0, 100);
        var b = ScenarioWorld(222); Scenario(b, 0, 100);
        Assert.Equal(0, a.Game.Rng.Draws);
        Assert.Equal(0, b.Game.Rng.Draws);
        Assert.Equal(GameSave.WorldHash(a.Game), GameSave.WorldHash(b.Game));
        Assert.NotEqual(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game)); // only the RNG state differs
        Assert.NotEmpty(a.Events<ForcedStop>());
        Assert.NotEmpty(a.Events<Contact>());
        Assert.NotEmpty(a.Events<BlockadeStarted>());
    }

    [Fact] // m1-clock-and-orders K11; m1-forced-stop-and-blockade B17
    public void K11_B17_same_seed_and_orders_twice_give_identical_events_and_hash()
    {
        var a = ScenarioWorld(5); Scenario(a, 0, 100);
        var b = ScenarioWorld(5); Scenario(b, 0, 100);
        Assert.NotEmpty(a.Game.Events);
        Assert.Equal(a.Game.Events, b.Game.Events);
        Assert.Equal(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
    }

    [Fact] // m1-clock-and-orders K12
    public void K12_save_at_day_7_with_a_fleet_mid_lane_load_and_continue_matches_the_uninterrupted_run()
    {
        var whole = ScenarioWorld(5); Scenario(whole, 0, 30);

        var first = ScenarioWorld(5); Scenario(first, 0, 7);
        Assert.Equal(7, first.Game.Day);
        var mid = first.Fleet(1);
        Assert.Null(mid.At); // mid-lane (Hosnian Prime to Kinyen)
        Assert.Equal(("Hosnian Prime", "Kinyen"), (mid.Transit!.Path[mid.Transit.HopIndex], mid.Transit.Path[mid.Transit.HopIndex + 1]));

        string json = GameSave.Save(first.Game);
        var loaded = new World(GameSave.Load(json), TestData.RealGalaxy);
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded.Game));
        Assert.Equal(json, GameSave.Save(loaded.Game));
        Scenario(loaded, 7, 30);

        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded.Game));
        Assert.Equal(GameSave.Save(whole.Game), GameSave.Save(loaded.Game));
        // Events are not saved: the loaded game only has the events of the continued days (and counts its indexes from 0).
        Assert.Equal(whole.Game.Events.Skip(first.Game.Events.Count).Select(e => e with { Index = 0 }),
                     loaded.Game.Events.Select(e => e with { Index = 0 }));
        Assert.Contains(loaded.Events<ForcedStop>(), e => e.System == "Anoat");
    }

    [Fact] // m1-clock-and-orders K13
    public void K13_a_queued_move_order_survives_save_and_load_and_is_processed_in_the_next_step()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        long seq = w.Fleets.SubmitMove(Side.Empire, f, "Corellia");
        var loaded = new World(GameSave.Load(GameSave.Save(w.Game)), TestData.RealGalaxy);
        var queued = Assert.IsType<MoveFleetOrder>(Assert.Single(loaded.Game.Queue));
        Assert.Equal((seq, f, "Corellia"), (queued.Sequence, queued.FleetId, queued.Destination));
        loaded.Game.Step();
        Assert.Empty(loaded.Game.Queue);
        Assert.Equal(seq, Assert.Single(loaded.Events<OrderAccepted>()).Sequence);
        Assert.NotNull(loaded.Fleet(f).Transit);
    }

    [Fact] // m1-clock-and-orders §Orders in: a loaded save without the fleet module cannot process its move order
    public void A_move_order_without_the_fleet_module_is_rejected_not_a_crash()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Coruscant", Isd);
        w.Fleets.SubmitMove(Side.Empire, f, "Corellia");
        var bare = GameSave.Load(GameSave.Save(w.Game));
        bare.Step();
        Assert.Equal(RejectReason.UnknownOrderType, Assert.IsType<OrderRejected>(Assert.Single(bare.Events)).Reason);
    }

    [Fact] // m1-clock-and-orders K15 (details: m1-forced-stop-and-blockade B7)
    public void K15_a_fleet_arriving_as_an_enemy_leaves_is_not_stopped()
    {
        var w = new World(World.BCMap());
        int f = w.Add(Side.Alliance, "F", "A", "Corellian Corvette");
        int e = w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.RunTo(15);
        w.Move(Side.Empire, e, "A"); // processed in the Step with K = 15, the one in which F arrives (day 16)
        w.Run(1);
        Assert.Equal(16, w.Game.Day);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Contains(w.Events<FleetPassed>(), p => p.Fleet == f && p.System == "B" && p.Day == 16);
    }

    [Fact] // m1-travel §Orders in transit ("a fleet never leaves a lane", engine/04 M1)
    public void A_fleet_is_always_at_a_system_or_on_exactly_one_lane_hop()
    {
        var w = ScenarioWorld(1);
        w.Move(Side.Empire, 1, "Hoth");
        for (int d = 0; d < 40; d++)
        {
            w.Game.Step();
            foreach (var f in w.Game.State.Fleets.Values)
            {
                if (f.At != null) { Assert.Null(f.Transit); continue; } // placed after step 4: a stopped or finished fleet
                var t = Assert.IsType<FleetTransit>(f.Transit);
                Assert.NotNull(w.Fleets.Galaxy.FindLane(t.Path[t.HopIndex], t.Path[t.HopIndex + 1]));
                Assert.True(t.DueDay > w.Game.Day);
            }
        }
    }

    [Fact] // m1-clock-and-orders §The clock: hash sees fleets, positions and in-transit state; order of fleet creation does not matter
    public void Hash_covers_fleet_state_and_is_canonical()
    {
        var a = Real(); var b = Real();
        a.Add(Side.Empire, "Red", "Coruscant", Isd, "Corellian Gunship"); // a fleet's ships are stored sorted
        b.Add(Side.Empire, "Red", "Coruscant", "Corellian Gunship", Isd);
        Assert.Equal(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        a.Move(Side.Empire, 1, "Hoth"); b.Move(Side.Empire, 1, "Hoth");
        a.Run(3); b.Run(3);
        Assert.Equal(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        b.Run(1);
        Assert.NotEqual(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        var c = Real();
        c.Add(Side.Empire, "Red", "Coruscant", Isd, "Corellian Gunship");
        Assert.NotEqual(GameSave.StateHash(Real().Game), GameSave.StateHash(c.Game));
    }

    [Fact] // m3-fleets §What a fleet is invariant 1 (never empty); setup guards
    public void AddFleet_refuses_empty_fleets_and_unknown_names()
    {
        var w = Real();
        Assert.Throws<ArgumentException>(() => w.Fleets.AddFleet(Side.Empire, "x", Array.Empty<string>(), "Coruscant"));
        Assert.Throws<ArgumentException>(() => w.Add(Side.Empire, "x", "Coruscant", "Nope"));
        Assert.Throws<ArgumentException>(() => w.Add(Side.Empire, "x", "Nowhere", Isd));
        Assert.Throws<ArgumentException>(() => w.Fleets.AddFleet(Side.Empire, "x", new[] { "TIE Fighter" }, "Coruscant")); // a squadron is not a ship
    }

    [Fact] // m1-travel §Rule (Classic): a speed-100 transport slows the fleet; rating comes from the data
    public void Fleet_speed_uses_the_slowest_capital_ship_and_ignores_squadrons_aboard()
    {
        var w = Real();
        int f = w.Fleets.AddFleet(Side.Empire, "Mixed", new[] { Isd, "Medium Transport" }, "Coruscant", new[] { "TIE Fighter" });
        Assert.Equal(100, w.Fleets.SpeedOf(w.Fleet(f)));
        int g = w.Fleets.AddFleet(Side.Empire, "Carrier", new[] { Isd }, "Coruscant", new[] { "TIE Fighter" });
        Assert.Equal(80, w.Fleets.SpeedOf(w.Fleet(g)));
    }
}
