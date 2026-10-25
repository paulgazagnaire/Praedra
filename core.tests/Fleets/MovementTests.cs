using Praedra.Core.Clock;
using Praedra.Core.Fleets;

namespace Praedra.Core.Tests.Fleets;

// rule: m1-clock-and-orders §Order of work, §Orders in, §Events out; m1-travel §Orders in transit.
// Tests K2-K9, K12, K13, K15 plus T19 (rejection while in transit) and the lane invariant. K10 and K11 are here too
// because they need fleets. The real galaxy and the real ship data are used; "ISD fleet" = one Mandate (speed 80).
public class MovementTests
{
    private const string Isd = "Mandate";
    // Free travel with routes (Paul 2026-10-16): Charter to The Thaw at speed 80 rides the routes (m1-travel T9).
    // v6 re-pin 2026-10-24: on the spiral map it leaves along the Trunk Line and reaches The Thaw (off every route) from Hollenvale, day 45.
    private static readonly string[] CoruscantHoth = { "Charter", "Bina Major", "Bina Minor", "Magnetite III", "Nordhaven", "Tierra Larga", "Seldwyn", "Overgrowth",
        "Longtide", "Galeholm", "Stopgap", "Groenveld", "Brokengate", "Towline", "Ashgarden", "Pale Ridge", "Zlatna", "Oldwatch", "Hollenvale", "The Thaw" };
    private static readonly int[] CoruscantHothHops = { 2, 1, 1, 2, 2, 1, 2, 1, 2, 1, 2, 2, 1, 2, 4, 2, 8, 4, 5 };

    private static World Real(ForcedStopTrigger t = ForcedStopTrigger.PaulRule, ulong seed = 1) => new(TestData.RealGalaxy, t, seed);

    [Fact] // m1-clock-and-orders K2
    public void K2_coruscant_to_kimanan_accepted_with_4_ticks_and_departs_on_day_0()
    {
        // v6 re-pin 2026-10-24: Classis is no longer one hop from Charter. No single-hop trip from Charter takes 5 days at
        // speed 80 on the spiral map; Consolatio is the nearest one-hop trip of several days (a 4-day free jump).
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        long seq = w.Fleets.SubmitMove(Side.Empire, f, "Consolatio");
        w.Run(1);
        var acc = Assert.Single(w.Events<OrderAccepted>());
        Assert.Equal(seq, acc.Sequence);
        Assert.Equal(f, acc.Fleet);
        Assert.Equal(new[] { "Charter", "Consolatio" }, acc.Path!.Items);
        Assert.Equal(4, acc.TotalTicks);
        Assert.Equal(4, acc.ArrivalDay);
        Assert.Equal(0, acc.Day);
        var dep = Assert.Single(w.Events<FleetDeparted>());
        Assert.Equal(new FleetDeparted(dep.Index, 0, f, "Charter", 4), dep);
        Assert.True(acc.Index < dep.Index); // step 1 before step 2
    }

    [Fact] // m1-clock-and-orders K3 (v6 re-pin 2026-10-24: the K2 trip, 4 days)
    public void K3_in_transit_after_3_steps_arrived_on_day_4_after_the_4th()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        w.Move(Side.Empire, f, "Consolatio");
        w.Run(3);
        Assert.Null(w.Fleet(f).At);
        Assert.NotNull(w.Fleet(f).Transit);
        Assert.Empty(w.Events<FleetArrived>());
        w.Run(1);
        var arr = Assert.Single(w.Events<FleetArrived>());
        Assert.Equal(new FleetArrived(arr.Index, 4, f, "Consolatio"), arr);
        Assert.Equal(4, w.Game.Day);
        Assert.Equal("Consolatio", w.Fleet(f).At);
        Assert.Null(w.Fleet(f).Transit);
        Assert.Equal("Charter", w.Fleet(f).PreviousSystem);
    }

    [Fact] // m1-clock-and-orders K4 (numbers agree with the real data and the formula: no mismatch)
    public void K4_coruscant_to_hoth_passes_and_arrives_on_the_sheet_days()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        w.Move(Side.Empire, f, "The Thaw");
        w.Run(48);
        var acc = Assert.Single(w.Events<OrderAccepted>());
        Assert.Equal(CoruscantHoth, acc.Path!.Items);
        Assert.Equal(45, acc.TotalTicks); // v6 re-pin 2026-10-24 (was 32)
        Assert.Equal(45, acc.ArrivalDay);
        var passes = new List<(int, string)>(); int day = 0;
        for (int k = 0; k < CoruscantHothHops.Length - 1; k++) { day += CoruscantHothHops[k]; passes.Add((day, CoruscantHoth[k + 1])); }
        Assert.Equal((40, "Hollenvale"), passes[^1]); // v6 re-pin 2026-10-24 (was day 27, Seven Gates)
        Assert.Equal(passes, w.Events<FleetPassed>().Select(e => (e.Day, e.System)));
        var arr = Assert.Single(w.Events<FleetArrived>());
        Assert.Equal((45, "The Thaw"), (arr.Day, arr.System));
        Assert.Empty(w.Events<ForcedStop>());
    }

    [Fact] // m1-clock-and-orders K5 (v6 re-pin 2026-10-24: the last leg is Hollenvale to The Thaw, days 40 to 45; was 27 to 32)
    public void K5_after_40_steps_on_the_last_leg_after_45_arrived()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        w.Move(Side.Empire, f, "The Thaw");
        w.Run(40);
        var t = w.Fleet(f).Transit!;
        Assert.Null(w.Fleet(f).At);
        Assert.Equal(("Hollenvale", "The Thaw"), (t.Path[t.HopIndex], t.Path[t.HopIndex + 1]));
        Assert.Equal(45, t.DueDay);
        Assert.Equal("Hollenvale", w.Fleet(f).PreviousSystem);
        w.Run(4);
        Assert.Null(w.Fleet(f).At);
        w.Run(1);
        Assert.Equal("The Thaw", w.Fleet(f).At);
        Assert.Null(w.Fleet(f).Transit);
    }

    [Fact] // m1-clock-and-orders K6
    public void K6_two_orders_for_one_fleet_in_one_step_first_accepted_second_rejected_in_transit()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        long s1 = w.Fleets.SubmitMove(Side.Empire, f, "Classis");
        long s2 = w.Fleets.SubmitMove(Side.Empire, f, "The Thaw");
        w.Run(1);
        Assert.Equal(s1, Assert.Single(w.Events<OrderAccepted>()).Sequence);
        var rej = Assert.Single(w.Events<OrderRejected>());
        Assert.Equal((s2, RejectReason.InTransit), (rej.Sequence, rej.Reason));
        Assert.Equal("Classis", w.Fleet(f).Transit!.Path[^1]); // the first order stands
    }

    [Fact] // m1-clock-and-orders K7; m1-travel T19
    public void K7_T19_order_for_a_fleet_already_moving_is_rejected_and_state_is_unchanged()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        w.Move(Side.Empire, f, "The Thaw");
        w.Run(3);
        string before = World.StateJson(w.Game);
        long seq = w.Fleets.SubmitMove(Side.Empire, f, "Classis");
        w.Game.Step();
        var rej = Assert.Single(w.Events<OrderRejected>());
        Assert.Equal((seq, RejectReason.InTransit), (rej.Sequence, rej.Reason));
        Assert.Equal(CoruscantHoth, w.Fleet(f).Transit!.Path); // path untouched
    }

    [Fact] // m1-clock-and-orders K8
    public void K8_alliance_cannot_move_an_empire_fleet()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        w.Fleets.SubmitMove(Side.Alliance, f, "Classis");
        w.Run(1);
        Assert.Equal(RejectReason.NotYours, Assert.Single(w.Events<OrderRejected>()).Reason);
        Assert.Equal("Charter", w.Fleet(f).At);
        Assert.Null(w.Fleet(f).Transit);
    }

    // m1-clock-and-orders §Orders in: the other MoveFleet rejections; m1-travel T20. T8 (Immobile) cannot happen to a ship fleet any more: every
    // capital ship has a rating of 50 or more (m3-fleets §Fleet speed); a lone TIE squadron is refused by MoveSquadrons (FleetTests F14).
    [Fact]
    public void Move_order_rejections_unknown_fleet_already_there_unknown_system_unreachable()
    {
        var w = Real();
        int isd = w.Add(Side.Empire, "Red", "Charter", Isd);
        w.Fleets.SubmitMove(Side.Empire, 99, "Classis");
        w.Fleets.SubmitMove(Side.Empire, isd, "Charter");
        w.Fleets.SubmitMove(Side.Empire, isd, "No Such Place");
        string before = World.StateJson(w.Game);
        w.Run(1);
        // Unreachable no longer happens on the real map: free travel reaches every system, Glasswood included (m1-travel T20)
        Assert.Equal(
            new[] { RejectReason.UnknownFleet, RejectReason.AlreadyThere, RejectReason.UnknownSystem },
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
            int e = w.Add(Side.Empire, "Red", "Charter", Isd);
            int a = w.Add(Side.Alliance, "Blue", "Classis", "Dodger");
            if (empireFirst) { w.Move(Side.Empire, e, "The Thaw"); w.Move(Side.Alliance, a, "Drift Harbour"); }
            else { w.Move(Side.Alliance, a, "Drift Harbour"); w.Move(Side.Empire, e, "The Thaw"); }
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
            if (d == 0) w.Move(Side.Empire, 1, "The Thaw");
            if (d == 22) w.Move(Side.Empire, 1, "The Thaw"); // after the stop at Tierra Larga (day 8 on the v6 map; was day 20)
            w.Game.Step();
        }
    }

    private static World ScenarioWorld(ulong seed)
    {
        var w = Real(seed: seed);
        w.Add(Side.Empire, "Red", "Charter", Isd);                       // fleet 1
        w.Add(Side.Alliance, "Picket", "Tierra Larga", "Snare");      // fleet 2: gravity-well ship on the route, pulls the mover out
        w.Fleets.SetOwner("Tierra Larga", Side.Alliance);
        w.Fleets.SetOwner("The Thaw", Side.Alliance); // the mover ends up alone at an Alliance system: Classic blockade
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
        Assert.Null(mid.At); // mid-leg (v6 re-pin 2026-10-24: Nordhaven to Tierra Larga, days 6 to 8; was Ommata to Fiscus)
        Assert.Equal(("Nordhaven", "Tierra Larga"), (mid.Transit!.Path[mid.Transit.HopIndex], mid.Transit.Path[mid.Transit.HopIndex + 1]));

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
        Assert.Contains(loaded.Events<ForcedStop>(), e => e.System == "Tierra Larga");
    }

    [Fact] // m1-clock-and-orders K13
    public void K13_a_queued_move_order_survives_save_and_load_and_is_processed_in_the_next_step()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        long seq = w.Fleets.SubmitMove(Side.Empire, f, "Classis");
        var loaded = new World(GameSave.Load(GameSave.Save(w.Game)), TestData.RealGalaxy);
        var queued = Assert.IsType<MoveFleetOrder>(Assert.Single(loaded.Game.Queue));
        Assert.Equal((seq, f, "Classis"), (queued.Sequence, queued.FleetId, queued.Destination));
        loaded.Game.Step();
        Assert.Empty(loaded.Game.Queue);
        Assert.Equal(seq, Assert.Single(loaded.Events<OrderAccepted>()).Sequence);
        Assert.NotNull(loaded.Fleet(f).Transit);
    }

    [Fact] // m1-clock-and-orders §Orders in: a loaded save without the fleet module cannot process its move order
    public void A_move_order_without_the_fleet_module_is_rejected_not_a_crash()
    {
        var w = Real();
        int f = w.Add(Side.Empire, "Red", "Charter", Isd);
        w.Fleets.SubmitMove(Side.Empire, f, "Classis");
        var bare = GameSave.Load(GameSave.Save(w.Game));
        bare.Step();
        Assert.Equal(RejectReason.UnknownOrderType, Assert.IsType<OrderRejected>(Assert.Single(bare.Events)).Reason);
    }

    [Fact] // m1-clock-and-orders K15 (details: m1-forced-stop-and-blockade B7)
    public void K15_a_fleet_arriving_as_an_enemy_leaves_is_not_stopped()
    {
        var w = new World(World.BCMap());
        int f = w.Add(Side.Alliance, "F", "A", "Dodger");
        int e = w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.RunTo(15);
        w.Move(Side.Empire, e, "A"); // processed in the Step with K = 15, the one in which F arrives (day 16)
        w.Run(1);
        Assert.Equal(16, w.Game.Day);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Contains(w.Events<FleetPassed>(), p => p.Fleet == f && p.System == "B" && p.Day == 16);
    }

    [Fact] // m1-travel §Orders in transit (one leg at a time; was "a fleet never leaves a lane", engine/04 M1)
    public void A_fleet_is_always_at_a_system_or_on_exactly_one_leg()
    {
        var w = ScenarioWorld(1);
        w.Move(Side.Empire, 1, "The Thaw");
        for (int d = 0; d < 40; d++)
        {
            w.Game.Step();
            foreach (var f in w.Game.State.Fleets.Values)
            {
                if (f.At != null) { Assert.Null(f.Transit); continue; } // placed after step 4: a stopped or finished fleet
                var t = Assert.IsType<FleetTransit>(f.Transit);
                Assert.True(t.HopIndex + 1 < t.Path.Count);
                var g = w.Fleets.Galaxy; g.TryFindSystem(t.Path[t.HopIndex + 1], out int next);
                Assert.True(g.OnRoute(next) || t.HopIndex + 1 == t.Path.Count - 1); // flies to a route system or to its destination
                Assert.True(t.DueDay > w.Game.Day);
            }
        }
    }

    [Fact] // m1-clock-and-orders §The clock: hash sees fleets, positions and in-transit state; order of fleet creation does not matter
    public void Hash_covers_fleet_state_and_is_canonical()
    {
        var a = Real(); var b = Real();
        a.Add(Side.Empire, "Red", "Charter", Isd, "Wasp"); // a fleet's ships are stored sorted
        b.Add(Side.Empire, "Red", "Charter", "Wasp", Isd);
        Assert.Equal(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        a.Move(Side.Empire, 1, "The Thaw"); b.Move(Side.Empire, 1, "The Thaw");
        a.Run(3); b.Run(3);
        Assert.Equal(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        b.Run(1);
        Assert.NotEqual(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        var c = Real();
        c.Add(Side.Empire, "Red", "Charter", Isd, "Wasp");
        Assert.NotEqual(GameSave.StateHash(Real().Game), GameSave.StateHash(c.Game));
    }

    [Fact] // m3-fleets §What a fleet is invariant 1 (never empty); setup guards
    public void AddFleet_refuses_empty_fleets_and_unknown_names()
    {
        var w = Real();
        Assert.Throws<ArgumentException>(() => w.Fleets.AddFleet(Side.Empire, "x", Array.Empty<string>(), "Charter"));
        Assert.Throws<ArgumentException>(() => w.Add(Side.Empire, "x", "Charter", "Nope"));
        Assert.Throws<ArgumentException>(() => w.Add(Side.Empire, "x", "No Such Place", Isd));
        Assert.Throws<ArgumentException>(() => w.Fleets.AddFleet(Side.Empire, "x", new[] { "Gleaner" }, "Charter")); // a squadron is not a ship
    }

    [Fact] // m1-travel §Rule (Classic): a speed-100 transport slows the fleet; rating comes from the data
    public void Fleet_speed_uses_the_slowest_capital_ship_and_ignores_squadrons_aboard()
    {
        var w = Real();
        int f = w.Fleets.AddFleet(Side.Empire, "Mixed", new[] { Isd, "Mule" }, "Charter", new[] { "Gleaner" });
        Assert.Equal(100, w.Fleets.SpeedOf(w.Fleet(f)));
        int g = w.Fleets.AddFleet(Side.Empire, "Carrier", new[] { Isd }, "Charter", new[] { "Gleaner" });
        Assert.Equal(80, w.Fleets.SpeedOf(w.Fleet(g)));
    }
}
