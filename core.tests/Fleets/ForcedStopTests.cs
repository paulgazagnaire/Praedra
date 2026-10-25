using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using Praedra.Core.Ships;

namespace Praedra.Core.Tests.Fleets;

// rule: m1-forced-stop-and-blockade §Tests B1-B17 on the synthetic map A(0,0) B(100,0) C(200,0): tier 3 lanes, a speed-80
// fleet needs 16 days per hop. F = an Alliance Dodger (the "CR90"; strength 0 (laser-only), speed 80) ordered A to C on day 0.
//
// TRIGGERS. The sheet wrote B2, B6, B7 for AnyArmedFleet; the lead's override (section "Update 2026-10-12") makes PaulRule the
// default. Each test says which trigger(s) it runs under. Tests that depend on strength levels carry "M4 re-check":
// CombatStrength is m4-battle-new §12 (the M1 placeholder, sum of refined_material_cost, is gone).
public class ForcedStopTests
{
    private const string Isd = "Mandate";
    private const string Corvette = "Dodger"; // "CR90 Corvette" in the lead's brief; the data calls it this

    public static IEnumerable<object[]> AllTriggers() => new[]
    {
        new object[] { ForcedStopTrigger.PaulRule }, new object[] { ForcedStopTrigger.AnyArmedFleet }, new object[] { ForcedStopTrigger.GravityWellShip },
    };
    public static IEnumerable<object[]> StopsForArmedIsd() => new[]
    {
        new object[] { ForcedStopTrigger.PaulRule }, new object[] { ForcedStopTrigger.AnyArmedFleet },
    };

    private static (World w, int f) Start(ForcedStopTrigger t)
    {
        var w = new World(World.BCMap(), t);
        int f = w.Add(Side.Alliance, "F", "A", Corvette);
        return (w, f);
    }

    private static void AssertReaches(World w, int f, string system, int day)
    {
        var arr = Assert.Single(w.Events<FleetArrived>(e => e.Fleet == f));
        Assert.Equal((day, system), (arr.Day, arr.System));
    }

    [Theory, MemberData(nameof(AllTriggers))] // B1 (all triggers: nothing to stop F)
    public void B1_no_other_fleets_F_passes_B_on_day_16_and_reaches_C_on_day_32(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        w.Move(Side.Alliance, f, "C");
        w.Run(40);
        var p = Assert.Single(w.Events<FleetPassed>());
        Assert.Equal((16, "B", f), (p.Day, p.System, p.Fleet));
        AssertReaches(w, f, "C", 32);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Empty(w.Events<Contact>());
    }

    [Theory, MemberData(nameof(StopsForArmedIsd))] // B2, under PaulRule (ISD 112 >= 0.5 x 0; M4 re-check) and AnyArmedFleet
    public void B2_stationary_empire_ISD_at_B_stops_F_on_day_16_and_cancels_its_order(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        int e = w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(16);
        var stop = Assert.Single(w.Events<ForcedStop>());
        Assert.Equal(new ForcedStop(stop.Index, 16, f, "B", new ValueList<int>(new[] { e })), stop);
        Assert.Equal("B", w.Fleet(f).At);
        Assert.Null(w.Fleet(f).Transit); // remaining order cancelled
        Assert.Equal("A", w.Fleet(f).PreviousSystem); // the lane it arrived on stays known (M4 retreat)
        Assert.Empty(w.Events<FleetPassed>());
        var c = Assert.Single(w.Events<Contact>());
        Assert.Equal(new Contact(c.Index, 16, "B", new ValueList<int>(new[] { e }), new ValueList<int>(new[] { f })), c);
        w.Run(20); // it stays; no further movement
        Assert.Equal("B", w.Fleet(f).At);
        Assert.Empty(w.Events<FleetArrived>(x => x.Fleet == f));
    }

    [Theory, MemberData(nameof(AllTriggers))] // B3: Barge has attack 0 in the data, so it is never armed under any trigger
    public void B3_empire_unarmed_transport_at_B_does_not_stop_F(ForcedStopTrigger t)
    {
        // Note: the ship data lists Barge as Alliance-only (is_empire = 0); the sheet calls it an Empire fleet. Side is not
        // checked against ship data in M1 (that is M3's CreateFleet), so the sheet's test runs as written.
        var (w, f) = Start(t);
        w.Add(Side.Empire, "E", "B", "Barge");
        w.Move(Side.Alliance, f, "C");
        w.Run(40);
        AssertReaches(w, f, "C", 32);
        Assert.Empty(w.Events<ForcedStop>());
    }

    [Theory, MemberData(nameof(AllTriggers))] // B4
    public void B4_friendly_ISD_at_B_does_not_stop_F(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        w.Add(Side.Alliance, "Friend", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(40);
        AssertReaches(w, f, "C", 32);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Empty(w.Events<Contact>());
    }

    [Theory, MemberData(nameof(AllTriggers))] // B5: the destination is a normal arrival under every trigger
    public void B5_empire_ISD_at_C_F_arrives_day_32_with_a_contact_and_no_stop(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        int e = w.Add(Side.Empire, "E", "C", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(32);
        AssertReaches(w, f, "C", 32);
        Assert.Empty(w.Events<ForcedStop>());
        var c = Assert.Single(w.Events<Contact>());
        Assert.Equal(("C", 32), (c.System, c.Day));
        Assert.Equal(new[] { e }, c.EmpireFleets.Items);
        Assert.Equal(new[] { f }, c.AllianceFleets.Items);
    }

    [Theory, MemberData(nameof(StopsForArmedIsd))] // B6, under PaulRule (M4 re-check) and AnyArmedFleet
    public void B6_empire_ISD_arriving_at_B_on_day_16_from_elsewhere_stops_F_that_day(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        int e = w.Add(Side.Empire, "E", "D", Isd); // lane D-B is also 16 days at speed 80
        w.Move(Side.Empire, e, "B");
        w.Move(Side.Alliance, f, "C");
        w.Run(16);
        var stop = Assert.Single(w.Events<ForcedStop>());
        Assert.Equal((16, f, "B"), (stop.Day, stop.Fleet, stop.System));
        Assert.Equal(new[] { e }, stop.Blockers.Items);
        Assert.Equal((16, "B"), (Assert.Single(w.Events<FleetArrived>()).Day, "B")); // E itself arrived at B the same day
    }

    [Theory, MemberData(nameof(StopsForArmedIsd))] // m1-forced-stop-and-blockade §When is an enemy "at the system": two passing enemy fleets both stop
    public void Two_enemy_fleets_passing_B_on_the_same_day_both_stop(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        int e = w.Add(Side.Empire, "E", "D", "Writ"); // laser-only: strength 0, like the corvette
        w.Move(Side.Empire, e, "C"); // D -> B -> C, reaches B on day 16 as well
        w.Move(Side.Alliance, f, "C");
        w.Run(16);
        if (t == ForcedStopTrigger.PaulRule)
        {
            // m4-battle-new §12: CS > 0 is required, and two laser-only fleets cannot hurt each other's shields: they do not stop each other
            // (changed from "equal-zero strengths stop each other by the inclusive rule 0 >= 0.5 x 0", the M1 placeholder)
            Assert.Empty(w.Events<ForcedStop>());
            return;
        }
        Assert.Equal(new[] { f, e }.OrderBy(x => x), w.Events<ForcedStop>().Select(s => s.Fleet).OrderBy(x => x));
        Assert.Equal("B", w.Fleet(f).At); Assert.Equal("B", w.Fleet(e).At);
        Assert.Equal(new[] { f, e }.OrderBy(x => x).ToArray(), w.Events<ForcedStop>().Select(s => s.Fleet).ToArray()); // fleet id order
    }

    [Theory, MemberData(nameof(StopsForArmedIsd))] // B7 (and K15), under PaulRule and AnyArmedFleet
    public void B7_ISD_ordered_away_in_the_step_F_arrives_departs_first_and_F_is_not_stopped(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        int e = w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.RunTo(15);
        w.Move(Side.Empire, e, "A");
        w.Run(1);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Null(w.Fleet(e).At); // gone before arrivals were judged
        w.Run(20);
        AssertReaches(w, f, "C", 32);
    }

    [Fact] // B8: GravityWellShip trigger, a fleet of ISDs has no gravity-well ship
    public void B8_gravity_well_trigger_ISD_fleet_at_B_does_not_stop_F()
    {
        var (w, f) = Start(ForcedStopTrigger.GravityWellShip);
        w.Add(Side.Empire, "E", "B", Isd, Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(40);
        AssertReaches(w, f, "C", 32);
        Assert.Empty(w.Events<ForcedStop>());
        // A fleet that passes on leaves the system in step 4, so step 5 (final positions) sees no contact at B.
        Assert.Empty(w.Events<Contact>());
    }

    [Fact] // B9: GravityWellShip trigger, one Subpoena
    public void B9_gravity_well_trigger_one_Interdictor_at_B_stops_F_on_day_16()
    {
        var (w, f) = Start(ForcedStopTrigger.GravityWellShip);
        int e = w.Add(Side.Empire, "E", "B", "Subpoena");
        w.Move(Side.Alliance, f, "C");
        w.Run(16);
        var stop = Assert.Single(w.Events<ForcedStop>());
        Assert.Equal((16, "B", f), (stop.Day, stop.System, stop.Fleet));
        Assert.Equal(new[] { e }, stop.Blockers.Items);
    }

    [Theory, MemberData(nameof(AllTriggers))] // B10
    public void B10_enemy_ISD_at_the_departure_system_does_not_stop_F(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        w.Add(Side.Empire, "E", "A", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(40);
        AssertReaches(w, f, "C", 32);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Equal(new[] { "B" }, w.Events<FleetPassed>().Select(p => p.System));
    }

    [Theory, MemberData(nameof(StopsForArmedIsd))] // B11, after B2
    public void B11_a_stopped_fleet_can_leave_the_blocked_system_and_is_not_stopped_there_again(ForcedStopTrigger t)
    {
        var (w, f) = Start(t);
        w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(16);
        Assert.Single(w.Events<ForcedStop>());
        w.Move(Side.Alliance, f, "C");
        w.Run(1);
        Assert.Equal(2, w.Events<OrderAccepted>().Count);
        Assert.Single(w.Events<ForcedStop>()); // no new stop: the departure system never stops a fleet leaving it
        var acc = w.Events<OrderAccepted>()[1];
        Assert.Equal(32, acc.ArrivalDay);
        w.Run(20);
        AssertReaches(w, f, "C", 32);
        Assert.Single(w.Events<ForcedStop>());
    }

    // ---- Classic blockade flag, B12-B16

    private static (World w, int e) EmpireAt(string system, Side? owner, string ship = Isd)
    {
        var w = new World(World.BCMap());
        w.Fleets.SetOwner(system, owner);
        int e = w.Add(Side.Empire, "E", system, ship);
        return (w, e);
    }

    [Fact] // B12
    public void B12_empire_fleet_alone_at_an_alliance_system_raises_the_classic_blockade()
    {
        var (w, _) = EmpireAt("B", Side.Alliance);
        w.Run(1);
        Assert.True(w.Fleets.IsBlockadedClassic("B"));
        var ev = Assert.Single(w.Events<BlockadeStarted>());
        Assert.Equal(("B", Side.Empire, 1), (ev.System, ev.Blockader, ev.Day));
        w.Run(5);
        Assert.Single(w.Events<BlockadeStarted>()); // raised once, not every day
    }

    [Fact] // B13
    public void B13_an_alliance_fleet_arriving_turns_the_battle_bit_on_and_lifts_the_blockade()
    {
        var (w, e) = EmpireAt("B", Side.Alliance);
        int f = w.Add(Side.Alliance, "F", "A", Corvette);
        w.Move(Side.Alliance, f, "B");
        w.Run(16);
        Assert.False(w.Fleets.IsBlockadedClassic("B"));
        var lifted = Assert.Single(w.Events<BlockadeLifted>());
        Assert.Equal(("B", Side.Empire, 16), (lifted.System, lifted.Blockader, lifted.Day));
        Assert.Equal(1, w.Events<BlockadeStarted>().Single().Day);
        Assert.Single(w.Events<Contact>(c => c.Day == 16));
        Assert.DoesNotContain("B", w.Game.State.Blockades.Keys);
        Assert.Equal(e, w.Events<Contact>().First().EmpireFleets.Items.Single());
    }

    [Fact] // B14
    public void B14_empire_fleet_at_a_neutral_system_is_not_a_blockade()
    {
        var (w, _) = EmpireAt("B", null);
        w.Run(3);
        Assert.False(w.Fleets.IsBlockadedClassic("B"));
        Assert.Empty(w.Events<BlockadeStarted>());
    }

    [Fact] // B15
    public void B15_empire_fleet_at_an_empire_owned_system_is_not_a_blockade()
    {
        var (w, _) = EmpireAt("B", Side.Empire);
        w.Run(3);
        Assert.False(w.Fleets.IsBlockadedClassic("B"));
        Assert.Empty(w.Events<BlockadeStarted>());
    }

    [Theory, MemberData(nameof(AllTriggers))] // B16: the flag has no armed test, the forced stop has
    public void B16_an_unarmed_galleon_blockades_classically_but_forces_no_stop(ForcedStopTrigger t)
    {
        var w = new World(World.BCMap(), t);
        w.Fleets.SetOwner("B", Side.Alliance);
        w.Add(Side.Empire, "E", "B", "Bond");
        int f = w.Add(Side.Alliance, "F", "A", Corvette);
        w.Move(Side.Alliance, f, "C");
        w.Run(15);
        Assert.True(w.Fleets.IsBlockadedClassic("B"));
        Assert.Single(w.Events<BlockadeStarted>());
        w.Run(25);
        Assert.Empty(w.Events<ForcedStop>());
        AssertReaches(w, f, "C", 32);
    }

    [Fact] // the flag also goes off when the blockader leaves (BlockadeLifted) and when the owner changes
    public void Classic_blockade_is_lifted_when_the_blockader_leaves_or_the_owner_changes()
    {
        var (w, e) = EmpireAt("B", Side.Alliance);
        w.Run(1);
        w.Move(Side.Empire, e, "A");
        w.Run(1);
        Assert.False(w.Fleets.IsBlockadedClassic("B"));
        Assert.Equal(("B", Side.Empire, 2), (w.Events<BlockadeLifted>().Single().System, w.Events<BlockadeLifted>().Single().Blockader, w.Events<BlockadeLifted>().Single().Day));

        var (w2, _) = EmpireAt("B", Side.Alliance);
        w2.Run(1);
        w2.Fleets.SetOwner("B", Side.Empire); // e.g. captured later (M3): the Empire's own fleet no longer blockades
        w2.Run(1);
        Assert.Single(w2.Events<BlockadeLifted>());
        Assert.Empty(w2.Game.State.Blockades);
    }

    [Fact] // m1-forced-stop-and-blockade §Defaults: one Contact per system per day while both sides are present
    public void A_contact_is_raised_each_day_while_both_sides_are_present()
    {
        var (w, f) = Start(ForcedStopTrigger.PaulRule);
        w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(19);
        Assert.Equal(new[] { 16, 17, 18, 19 }, w.Events<Contact>().Select(c => c.Day));
    }

    // ---- PaulRule (override, "Update 2026-10-12"). All carry M4 re-check where the strength level decides.

    [Fact]
    public void Default_trigger_is_PaulRule()
    {
        Assert.Equal(ForcedStopTrigger.PaulRule, new World(World.BCMap()).Fleets.Trigger);
        Assert.Equal(0, (int)ForcedStopTrigger.PaulRule);
    }

    [Fact] // PaulRule: one corvette does NOT stop an ISD fleet (M4 re-check: 0 < 0.5 x 224); AnyArmedFleet would
    public void PaulRule_one_corvette_does_not_stop_an_ISD_fleet_but_AnyArmedFleet_does()
    {
        foreach (var (t, stops) in new[] { (ForcedStopTrigger.PaulRule, false), (ForcedStopTrigger.AnyArmedFleet, true) })
        {
            var w = new World(World.BCMap(), t);
            int mover = w.Add(Side.Empire, "Fleet", "A", Isd, Isd);
            w.Add(Side.Alliance, "Corvette", "B", Corvette);
            w.Move(Side.Empire, mover, "C");
            w.Run(40);
            Assert.Equal(stops, w.Events<ForcedStop>().Count == 1);
            Assert.Equal(!stops, w.Fleet(mover).At == "C");
        }
    }

    [Fact] // PaulRule: an ISD stops a lone corvette (M4 re-check: 112 >= 0.5 x 0); the blocker is listed
    public void PaulRule_one_ISD_stops_a_lone_corvette()
    {
        var (w, f) = Start(ForcedStopTrigger.PaulRule);
        int e = w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(16);
        Assert.Equal(new[] { e }, Assert.Single(w.Events<ForcedStop>()).Blockers.Items);
    }

    [Fact] // PaulRule: an Interdictor stops anything (the gravity-well half has no strength test)
    public void PaulRule_an_Interdictor_stops_a_much_stronger_fleet()
    {
        var w = new World(World.BCMap(), ForcedStopTrigger.PaulRule);
        var inter = w.Fleets.AddFleet(Side.Empire, "Picket", new[] { "Subpoena" }, "B");
        int big = w.Add(Side.Alliance, "Big", "A", "Barricade", "Barricade"); // 2 x 248 against 40
        w.Move(Side.Alliance, big, "C");
        w.Run(12); // speed 60: 12 days per hop
        var stop = Assert.Single(w.Events<ForcedStop>());
        Assert.Equal((12, big, "B"), (stop.Day, stop.Fleet, stop.System));
        Assert.Equal(new[] { inter }, stop.Blockers.Items);
        // and the same Interdictor does not stop it under a pure strength rule: 40 x 100 < 50 x 496
        Assert.True(40 * 100L < 50 * 496L); // M4 re-check
    }

    [Fact] // PaulRule DEFAULT: a passing fleet's own gravity-well ship does not exempt it
    public void PaulRule_the_passing_fleets_own_gravity_well_ship_does_not_exempt_it()
    {
        var w = new World(World.BCMap(), ForcedStopTrigger.PaulRule);
        int mover = w.Add(Side.Alliance, "Mover", "A", "Snare"); // gravity_well_projector 4, speed 70
        w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, mover, "C");
        w.Run(14);
        Assert.Equal((14, mover, "B"), (w.Events<ForcedStop>().Single().Day, w.Events<ForcedStop>().Single().Fleet, w.Events<ForcedStop>().Single().System));
    }

    [Fact] // PaulRule: T is >= (exactly half stops); enemy fleets at the system add up (one force); M4 re-check
    public void PaulRule_threshold_is_inclusive_and_enemy_fleets_are_pooled()
    {
        // passing: Decree = 68; enemy Nebulon-B 34 = exactly 0.5 x 68 -> stops
        var w = new World(World.BCMap());
        int mover = w.Add(Side.Empire, "Mover", "A", "Decree");
        w.Add(Side.Alliance, "Nebulon", "B", "Mender");
        w.Move(Side.Empire, mover, "C");
        w.Run(16);
        Assert.Single(w.Events<ForcedStop>());

        // passing: 2 ISD = 224, half = 112. A Ark (92) alone passes; with a second fleet, a Nebulon-B (34), 126 stops.
        var alone = new World(World.BCMap());
        int m1 = alone.Add(Side.Empire, "Mover", "A", Isd, Isd);
        alone.Add(Side.Alliance, "MonCal", "B", "Ark");
        alone.Move(Side.Empire, m1, "C");
        alone.Run(40);
        Assert.Empty(alone.Events<ForcedStop>());

        var w2 = new World(World.BCMap());
        int m2 = w2.Add(Side.Empire, "Mover", "A", Isd, Isd);
        w2.Add(Side.Alliance, "MonCal", "B", "Ark");
        w2.Add(Side.Alliance, "Helper", "B", "Mender");
        w2.Move(Side.Empire, m2, "C");
        w2.Run(16);
        Assert.Equal(m2, Assert.Single(w2.Events<ForcedStop>()).Fleet);
        Assert.Equal(2, w2.Events<ForcedStop>().Single().Blockers.Items.Count);
    }

    [Fact] // space-combat/07 last section: laser-only boats count 0, so 8 corvettes do not stop an ISD (M4 re-check)
    public void PaulRule_eight_corvettes_do_not_stop_an_ISD_and_are_not_an_economic_blockade()
    {
        var w = new World(World.BCMap());
        int isd = w.Add(Side.Empire, "Isd", "A", Isd);
        var eight = Enumerable.Repeat(Corvette, 8).ToArray();
        w.Add(Side.Alliance, "Eight", "B", eight);
        w.Move(Side.Empire, isd, "C");
        w.Run(40);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Equal("C", w.Fleet(isd).At);
        Assert.False(w.Fleets.IsEconomicBlockade("B", Side.Empire));
    }

    [Fact] // m4-battle-new §12 (forced stop by strength) and its Lead update 3: CS > 0 is required, and a laser never penetrates a capital's shield, so a corvette (laser-only) cannot hurt a shielded transport: no stop. (N12 in the sheet body expected 3.4; changed by the Lead update.)
    public void PaulRule_corvettes_do_not_stop_a_shielded_transport()
    {
        var w = new World(World.BCMap());
        int t = w.Add(Side.Empire, "Bond", "A", "Bond"); // attack 0, speed 80, shield 100
        w.Add(Side.Alliance, "Corvette", "B", Corvette);       // armed (attack > 0), strength against a shielded ship 0
        w.Move(Side.Empire, t, "C");
        w.Run(40);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Equal("C", w.Fleet(t).At);
    }

    [Fact] // PaulRule (lead decision 2026-10-12): one corvette (0, laser-only) does NOT stop a single ISD (112); M4 re-check
    public void PaulRule_one_corvette_does_not_stop_a_single_ISD_but_an_ISD_stops_a_corvette()
    {
        var w = new World(World.BCMap());
        int isd = w.Add(Side.Empire, "Isd", "A", Isd);
        int cor = w.Add(Side.Alliance, "Corvette", "B", Corvette);
        w.Move(Side.Empire, isd, "C");
        w.Run(40);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Equal("C", w.Fleet(isd).At);
        Assert.Equal(0, w.Fleets.StrengthOf(w.Fleet(cor)));
        Assert.Equal(45.0, w.Fleets.StrengthOf(w.Fleet(isd)), 1e-9); // m4-battle-new N8: 1 ISD v REF (was the placeholder 112)
        var (w2, f) = Start(ForcedStopTrigger.PaulRule); // the other way round: stops
        w2.Add(Side.Empire, "E", "B", Isd);
        w2.Move(Side.Alliance, f, "C");
        w2.Run(16);
        Assert.Single(w2.Events<ForcedStop>());
    }

    [Fact] // m1-forced-stop-and-blockade §Rule (both rulesets) 6: the passing fleet need not be armed
    public void PaulRule_an_unarmed_passing_fleet_is_stopped_by_an_armed_enemy_fleet()
    {
        var w = new World(World.BCMap());
        int t = w.Add(Side.Alliance, "Transport", "A", "Barge"); // attack 0, speed 90: 18 days per hop
        w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, t, "C");
        w.Run(18);
        Assert.Equal(t, Assert.Single(w.Events<ForcedStop>()).Fleet);
    }

    [Theory] // m1-forced-stop-and-blockade §Rule (both rulesets) 3: system ownership does not matter
    [InlineData(null)] [InlineData(Side.Empire)] [InlineData(Side.Alliance)]
    public void Ownership_of_the_system_does_not_change_the_stop(Side? owner)
    {
        var (w, f) = Start(ForcedStopTrigger.PaulRule);
        w.Fleets.SetOwner("B", owner);
        w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(16);
        Assert.Single(w.Events<ForcedStop>());
    }

    [Fact] // PaulRule, GravityWellShip: the Snare (Alliance) has gravity_well_projector 4 too
    public void Gravity_well_trigger_stops_for_a_CC7700_as_well()
    {
        var w = new World(World.BCMap(), ForcedStopTrigger.GravityWellShip);
        int e = w.Add(Side.Empire, "E", "A", Isd);
        w.Add(Side.Alliance, "Picket", "B", "Snare");
        w.Move(Side.Empire, e, "C");
        w.Run(16);
        Assert.Single(w.Events<ForcedStop>());
    }

    // ---- Economic blockade (queryable now; the economic effects are M2)

    [Fact]
    public void E_is_computed_from_ship_data_not_typed_in()
    {
        var w = new World(World.BCMap());
        var data = World.Blockade;
        Assert.Equal(3, data.EconomicShipCount);
        Assert.Equal(50, data.ThresholdPercent);
        Assert.Equal(64.8, w.Fleets.EconomicStrength, 1e-9); // m4-battle-new N8: CS(3 Victory Destroyers, REF = 3 ISD) (was the placeholder 204)
        Assert.Equal(3, data.ReferenceShipCount);
        Assert.Equal("Mandate", data.ReferenceShipClass);
    }

    [Fact] // M4 re-check for the strength cases
    public void IsEconomicBlockade_needs_a_gravity_well_ship_or_strength_at_least_E()
    {
        var w = new World(World.BCMap());
        string Vd = World.Blockade.EconomicShipClass;
        Assert.False(w.Fleets.IsEconomicBlockade("B", Side.Alliance)); // nobody there
        w.Add(Side.Empire, "Picket", "B", "Subpoena");
        Assert.True(w.Fleets.IsEconomicBlockade("B", Side.Alliance)); // gravity-well ship: always
        Assert.False(w.Fleets.IsEconomicBlockade("B", Side.Empire)); // against the Empire, only Alliance ships count

        w.Add(Side.Empire, "One", "C", Isd);
        Assert.False(w.Fleets.IsEconomicBlockade("C", Side.Alliance)); // m4-battle-new N8: 1 ISD v REF = 45 < E (64.8)
        w.Add(Side.Empire, "Two", "C", Isd); // a second fleet: 45 + 45 = 90 >= E (64.8), pooled
        Assert.True(w.Fleets.IsEconomicBlockade("C", Side.Alliance));

        w.Add(Side.Empire, "Three", "A", Vd, Vd, Vd); // three of the E ship: exactly E (64.8)
        Assert.True(w.Fleets.IsEconomicBlockade("A", Side.Alliance));

        var w2 = new World(World.BCMap());
        w2.Add(Side.Empire, "Bond", "B", "Bond"); // unarmed: never
        Assert.False(w2.Fleets.IsEconomicBlockade("B", Side.Alliance));
        w2.Add(Side.Alliance, "Ally", "B", Corvette);
        Assert.False(w2.Fleets.IsEconomicBlockade("B", Side.Empire)); // 0 < E
    }

    [Fact] // m1-clock-and-orders §Seeded random numbers rule 3, with stops, contacts and blockades in play
    public void Movement_with_stops_and_contacts_never_draws_a_random_number()
    {
        var (w, f) = Start(ForcedStopTrigger.PaulRule);
        w.Fleets.SetOwner("B", Side.Alliance);
        w.Add(Side.Empire, "E", "B", Isd);
        w.Move(Side.Alliance, f, "C");
        w.Run(50);
        Assert.NotEmpty(w.Events<ForcedStop>());
        Assert.NotEmpty(w.Events<Contact>());
        Assert.Equal(0, w.Game.Rng.Draws);
    }

    [Fact] // B17 on the synthetic map
    public void B17_two_runs_with_the_same_inputs_give_identical_event_lists()
    {
        List<GameEvent> Run()
        {
            var (w, f) = Start(ForcedStopTrigger.PaulRule);
            int e = w.Add(Side.Empire, "E", "D", Isd);
            w.Fleets.SetOwner("B", Side.Alliance);
            w.Move(Side.Empire, e, "C");
            w.Move(Side.Alliance, f, "C");
            w.Run(50);
            return w.Game.Events.ToList();
        }
        var a = Run();
        Assert.NotEmpty(a);
        Assert.Equal(a, Run());
    }

    [Fact] // m4-battle-new §12 (the placeholder sum of cost is gone; the strength tests are in CombatStrengthTests); "armed" is still attack above zero
    public void Armed_is_attack_above_zero()
    {
        var s = World.Ships.Value;
        Assert.False(CombatStrength.IsArmed(new[] { s["Bond"], s["Barge"] }));
        Assert.True(CombatStrength.IsArmed(new[] { s["Bond"], s["Gleaner"] })); // a squadron arms a fleet
        Assert.Equal(0, new CombatStrength(Praedra.Core.Battle.NewBattleData.Default).Of(Array.Empty<ShipClass>(), new[] { s[Isd] }));
    }

    [Fact]
    public void BlockadeData_rejects_bad_files()
    {
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => BlockadeData.Parse("key,value\nforced_stop_threshold_percent,50\n"));
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => BlockadeData.Parse(
            "key,value\nforced_stop_threshold_percent,half\neconomic_blockade_ship_class,Bond\neconomic_blockade_ship_count,3\n"));
        var d = BlockadeData.Parse("key,value\nforced_stop_threshold_percent,50\neconomic_blockade_ship_class,No Such Ship\neconomic_blockade_ship_count,3\n");
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => d.EconomicStrength(World.Ships.Value));
    }
}
