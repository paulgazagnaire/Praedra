using Rebellion.Core.Clock;

namespace Rebellion.Core.Tests.Clock;

// rule: m1-clock-and-orders §The clock, §Order of work inside one Step, §Orders in, §Events out.
// The sheet's tests K1-K15 mostly need fleets; these are the ones that apply to the bare loop,
// using the placeholder SetMarkerOrder where an order is needed.
public class GameClockTests
{
    private static Game Run(ulong seed, int days)
    {
        var g = new Game(seed);
        for (int d = 0; d < days; d++)
        {
            // A fixed order list: a marker order every 3rd day, one rejected order every 10th day.
            if (d % 3 == 0) g.Submit(Side.Empire, $"m{d % 7}", d);
            if (d % 10 == 0) g.Submit(Side.Alliance, "bad", -1);
            g.Step();
        }
        return g;
    }

    [Fact] // m1-clock-and-orders K1
    public void K1_ten_steps_make_day_ten_and_day_starts_at_zero()
    {
        var g = new Game(1);
        Assert.Equal(0, g.Day);
        for (int i = 0; i < 10; i++) g.Step();
        Assert.Equal(10, g.Day);
    }

    [Fact] // m1-clock-and-orders K10 (draw count part; no fleets yet)
    public void K10_nothing_draws_so_draw_count_stays_zero_and_seed_does_not_matter()
    {
        var a = Run(111, 100);
        var b = Run(222, 100);
        Assert.Equal(0, a.Rng.Draws);
        Assert.Equal(0, b.Rng.Draws);
        Assert.Equal(GameSave.WorldHash(a), GameSave.WorldHash(b));
        Assert.NotEqual(GameSave.StateHash(a), GameSave.StateHash(b)); // different seeds will diverge once something draws
    }

    [Fact] // m1-clock-and-orders K11
    public void K11_same_seed_and_orders_twice_gives_identical_events_and_hash()
    {
        var a = Run(5, 100);
        var b = Run(5, 100);
        Assert.NotEmpty(a.Events);
        Assert.Equal(a.Events, b.Events);
        Assert.Equal(GameSave.StateHash(a), GameSave.StateHash(b));
    }

    [Fact] // m1-clock-and-orders K12 (adapted: marker state instead of a fleet mid-lane)
    public void K12_save_mid_run_load_continue_matches_uninterrupted_run()
    {
        var whole = Run(5, 30);

        var first = Run(5, 7);
        var loaded = GameSave.Load(GameSave.Save(first));
        Assert.Equal(GameSave.StateHash(first), GameSave.StateHash(loaded));
        for (int d = 7; d < 30; d++)
        {
            if (d % 3 == 0) loaded.Submit(Side.Empire, $"m{d % 7}", d);
            if (d % 10 == 0) loaded.Submit(Side.Alliance, "bad", -1);
            loaded.Step();
        }
        Assert.Equal(GameSave.StateHash(whole), GameSave.StateHash(loaded));
        Assert.Equal(GameSave.Save(whole), GameSave.Save(loaded));
        // Events are not saved: the loaded game only has the events of the continued days.
        Assert.Equal(whole.Events.Skip(first.Events.Count).Select(e => (e.Day, e.Kind)),
                     loaded.Events.Select(e => (e.Day, e.Kind)));
    }

    [Fact] // m1-clock-and-orders K13
    public void K13_queued_order_survives_save_and_is_processed_in_the_next_step()
    {
        var g = new Game(9);
        long seq = g.Submit(Side.Empire, "x", 4);
        var loaded = GameSave.Load(GameSave.Save(g));
        Assert.Single(loaded.Queue);
        Assert.Equal(seq + 1, loaded.NextSequence);
        loaded.Step();
        Assert.Empty(loaded.Queue);
        Assert.Equal(4, loaded.State.Markers["x"]);
        Assert.IsType<OrderAccepted>(Assert.Single(loaded.Events));
        Assert.Equal(seq + 1, loaded.Submit(Side.Empire, "y", 1)); // numbering continues after load
    }

    [Fact] // m1-clock-and-orders K6 (adapted: same marker twice in one Step)
    public void K6_second_order_for_same_target_in_one_step_is_rejected()
    {
        var g = new Game(1);
        long s1 = g.Submit(Side.Empire, "a", 1);
        long s2 = g.Submit(Side.Empire, "a", 2);
        g.Step();
        Assert.Equal(1, g.State.Markers["a"]);
        Assert.Equal(new OrderAccepted(0, 0, s1), g.Events[0]);
        Assert.Equal(new OrderRejected(1, 0, s2, RejectReason.AlreadyChanged), g.Events[1]);
    }

    [Fact] // m1-clock-and-orders §Orders in: "Rejection never changes any state"
    public void Rejected_order_changes_no_state()
    {
        var g = new Game(1);
        g.Submit(Side.Empire, "a", 5);
        g.Step();
        string before = GameSave.StateHash(g);
        g.Submit(Side.Empire, "a", -1);
        g.Step();
        Assert.Equal(5, g.State.Markers["a"]);
        Assert.IsType<OrderRejected>(g.Events[^1]);
        Assert.NotEqual(before, GameSave.StateHash(g)); // only Day and queue counters moved
        Assert.Equal(new[] { "a" }, g.State.Markers.Keys);
    }

    [Fact] // m1-clock-and-orders §Order of work, step 1: processed by sequence number, not submission container
    public void Orders_are_processed_in_sequence_order_and_events_are_numbered_from_zero()
    {
        var g = new Game(1);
        for (int i = 0; i < 5; i++) g.Submit(Side.Alliance, $"k{i}", i);
        g.Step();
        Assert.Equal(Enumerable.Range(0, 5), g.Events.Select(e => e.Index));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, g.Events.Cast<OrderAccepted>().Select(e => e.Sequence));
    }

    [Fact] // m1-clock-and-orders §Order of work: the numbered work list runs in order, empty slots are hookable
    public void Work_list_hooks_run_in_the_numbered_order_and_day_changes_last()
    {
        var g = new Game(1);
        var seen = new List<string>();
        var days = new List<int>();
        foreach (var slot in Game.WorkList.Where(s => s is not ("1" or "7")))
        {
            g.RegisterHook(slot, ctx => { seen.Add(slot); days.Add(g.Day); Assert.Equal(0, ctx.Day); });
        }
        g.Step();
        Assert.Equal(Game.WorkList.Where(s => s is not ("1" or "7")), seen);
        Assert.All(days, d => Assert.Equal(0, d));
        Assert.Equal(1, g.Day);
        Assert.Throws<ArgumentException>(() => g.RegisterHook("7", _ => { }));
    }

    [Fact] // m1-clock-and-orders §Seeded random numbers rule 3 (a drawing hook moves the counter and the hash)
    public void A_hook_that_draws_advances_the_counter()
    {
        var g = new Game(1);
        g.RegisterHook("6.1", ctx => ctx.Rng.NextInt(1, 6));
        g.Step();
        Assert.True(g.Rng.Draws >= 1);
    }

    [Fact] // m1-clock-and-orders K9 (adapted: two sides, submission order swapped)
    public void K9_submission_order_across_sides_does_not_change_the_final_hash()
    {
        var a = new Game(1); a.Submit(Side.Empire, "e", 1); a.Submit(Side.Alliance, "r", 2); a.Step();
        var b = new Game(1); b.Submit(Side.Alliance, "r", 2); b.Submit(Side.Empire, "e", 1); b.Step();
        Assert.Equal(GameSave.StateHash(a), GameSave.StateHash(b));
    }

    [Fact]
    public void Save_json_is_deterministic_and_hash_is_stable_sha256_hex()
    {
        var g = Run(5, 12);
        Assert.Equal(GameSave.Save(g), GameSave.Save(GameSave.Load(GameSave.Save(g))));
        string h = GameSave.StateHash(g);
        Assert.Matches("^[0-9a-f]{64}$", h);
        Assert.Equal(h, GameSave.StateHash(g));
    }
}
