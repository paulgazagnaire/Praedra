namespace Praedra.Core.Tests.AI;

// Multi-seed play tests. One seeded AI game is chaotic: any new draw in the game generator (a new original-game rule, a new AI switch)
// sends it another way, so a day limit on one game breaks on legal changes and says little. The AI play tests instead ask for a floor
// over several seeds ("at least 6 of 8 by day N"), set from the measured spread with margin. The games run in parallel (each has its
// own GameHost and generator, as in sim tourney); the results come back in seed order.
internal static class MultiSeed
{
    public static readonly int[] OneToEight = { 1, 2, 3, 4, 5, 6, 7, 8 };

    public static T[] Play<T>(IReadOnlyList<int> seeds, Func<int, T> game)
    {
        var results = new T[seeds.Count];
        Parallel.For(0, seeds.Count, i => results[i] = game(seeds[i]));
        return results;
    }
}
