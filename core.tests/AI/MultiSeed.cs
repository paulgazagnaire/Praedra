using Praedra.Core.AI;
namespace Praedra.Core.Tests.AI;

// Multi-seed play tests. One seeded AI game is chaotic: any new draw in the game generator (a new original-game rule, a new AI switch)
// sends it another way, so a day limit on one game breaks on legal changes and says little. The AI play tests instead ask for a floor
// over several seeds ("at least 6 of 8 by day N"), set from the measured spread with margin. The games run in parallel (each has its
// own GameHost and generator, as in sim tourney); the results come back in seed order.
internal static class MultiSeed
{
    public static readonly int[] OneToEight = { 1, 2, 3, 4, 5, 6, 7, 8 };

    /// <summary>A level without AiLevels.PlayToWin (2026-10-26), for play tests whose floors were measured before it: its bigger troop wishes
    /// move every seed, while the tests measure other switches.</summary>
    public static SimpleAiParams WithoutPlayToWin(SimpleAiParams p)
    {
        var d = new SimpleAiParams();
        return p with { WinRushDefenders = d.WinRushDefenders, InvasionRegimentTarget = d.InvasionRegimentTarget, TroopCapacityTarget = d.TroopCapacityTarget, AfterCharterDiplomats = d.AfterCharterDiplomats };
    }

    /// <summary>
    /// Settings with PR #442's two leader rules pinned to before (Dev lead 2026-11-28): Autoroute without the guarded-first pass
    /// (m5-characters-and-talent §Autoroute step 3, AutorouteToGuarded) and every mission group on its way in seen with no draw (m5-fog-and-hq
    /// §N6d, InboundMissionGroupPercent 100). For single-seed play tests that measure an AI switch: the new rules move those games, not the
    /// switch. With them the games are exactly those of main before #442.
    /// </summary>
    /// <summary>
    /// Settings with m5-fog-and-hq §N6e (guards see an enemy team leave for their world, GuardsSeeInbound) off, for the single-seed AI play tests
    /// whose numbers were measured before it: the guards' draw moves the game's random stream.
    /// </summary>
    public static Praedra.Core.Setup.GameSettings GuardsSeeInboundOff => new() { Fog = new Praedra.Core.Fog.FogParams { GuardsSeeInbound = false } };

    /// <summary>LeaderRulesBefore442 and m5-fog-and-hq §N6e (GuardsSeeInbound) off: the single-seed Hard mirror tests measured before both.</summary>
    public static Praedra.Core.Setup.GameSettings LeaderRulesBeforeGuards => new()
    {
        Characters = new Praedra.Core.Characters.CharacterParams { AutorouteToGuarded = false },
        Fog = new Praedra.Core.Fog.FogParams { InboundMissionGroupPercent = 100, GuardsSeeInbound = false },
    };

    public static Praedra.Core.Setup.GameSettings LeaderRulesBefore442 => new()
    {
        Characters = new Praedra.Core.Characters.CharacterParams { AutorouteToGuarded = false },
        Fog = new Praedra.Core.Fog.FogParams { InboundMissionGroupPercent = 100 },
    };

    public static T[] Play<T>(IReadOnlyList<int> seeds, Func<int, T> game)
    {
        var results = new T[seeds.Count];
        Parallel.For(0, seeds.Count, i => results[i] = game(seeds[i]));
        return results;
    }
}
