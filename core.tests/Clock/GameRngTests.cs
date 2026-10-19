using Praedra.Core.Clock;

namespace Praedra.Core.Tests.Clock;

// rule: m1-clock-and-orders §Seeded random numbers (rules 1, 2; Open question 3: PCG32 chosen and frozen)
public class GameRngTests
{
    // Cross-checked against an independent Python PCG32 XSH-RR with pcg32_srandom_r(42, 54):
    //   state = 0; inc = (54 << 1) | 1; step; state += 42; step. The first value, 0xA15C02B7, is also the
    //   published pcg32 demo output for these arguments.
    private static readonly uint[] Seed42 =
    {
        2707161783, 2068313097, 3122475824, 2211639955, 3215226955,
        3421331566, 3217466285, 2167406445, 3860803674, 4181216144,
    };

    [Fact]
    public void Frozen_first_10_outputs_for_seed_42()
    {
        var rng = new GameRng(42);
        Assert.Equal(Seed42, Enumerable.Range(0, 10).Select(_ => rng.NextUInt()).ToArray());
    }

    [Fact]
    public void Same_seed_same_sequence_different_seed_differs()
    {
        var a = new GameRng(7); var b = new GameRng(7); var c = new GameRng(8);
        var sa = Enumerable.Range(0, 100).Select(_ => a.NextUInt()).ToArray();
        Assert.Equal(sa, Enumerable.Range(0, 100).Select(_ => b.NextUInt()).ToArray());
        Assert.NotEqual(sa, Enumerable.Range(0, 100).Select(_ => c.NextUInt()).ToArray());
    }

    [Fact]
    public void NextInt_stays_in_bounds_including_extremes()
    {
        var rng = new GameRng(1);
        for (int i = 0; i < 10_000; i++)
        {
            Assert.InRange(rng.NextInt(-3, 4), -3, 4);
            Assert.Equal(5, rng.NextInt(5, 5));
        }
        rng.NextInt(int.MinValue, int.MaxValue); // full range must not throw
        Assert.InRange(rng.NextInt(0, int.MaxValue), 0, int.MaxValue);
        Assert.Throws<ArgumentException>(() => rng.NextInt(2, 1));
    }

    [Fact]
    public void NextInt_is_roughly_uniform_over_100k_draws()
    {
        var rng = new GameRng(2026);
        var counts = new int[6];
        for (int i = 0; i < 100_000; i++) counts[rng.NextInt(1, 6) - 1]++;
        // Expected 16667 each; sd about 118, so +-600 is over 5 sigma.
        Assert.All(counts, c => Assert.InRange(c, 16_667 - 600, 16_667 + 600));
    }

    [Fact]
    public void Draw_counter_counts_every_draw()
    {
        var rng = new GameRng(3);
        Assert.Equal(0, rng.Draws);
        rng.NextUInt(); rng.NextInt(0, 9);
        Assert.True(rng.Draws >= 2);
    }

    [Fact]
    public void State_export_import_round_trips()
    {
        var a = new GameRng(99);
        for (int i = 0; i < 17; i++) a.NextUInt();
        var b = GameRng.Import(a.Export());
        Assert.Equal(a.Draws, b.Draws);
        for (int i = 0; i < 50; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
    }
}
