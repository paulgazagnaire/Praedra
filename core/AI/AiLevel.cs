namespace Rebellion.Core.AI;

/// <summary>
/// How well the computer opponent plays. Every level sees only its side's fog-of-war view and gets no hidden bonus (ai/12 D3, Paul
/// 2026-10-12: Hard is smarter, never a cheat; economy-ai D4). The 1998 head start (more systems and forces at setup, setup-victory/02) is a
/// separate setup choice (StartPreset), so a player can mix the two. rule: ai/09 §6 (difficulty is an AI parameter set, not a head start).
/// </summary>
public enum AiLevel
{
    /// <summary>Today's AI, handicapped: slower reviews, one offensive at a time, sometimes a worse target, fewer scouts and spies.</summary>
    Easy,
    /// <summary>The DEFAULT AI (<see cref="SimpleAiParams"/> defaults).</summary>
    Medium,
    /// <summary>Medium plus every upgrade that beat Medium in a seeded tournament (sim tourney). Same view, same rules, same costs.</summary>
    Hard,
}

public static class AiLevels
{
    /// <summary>
    /// The AI parameters of a level, on top of <paramref name="baseParams"/> (DEFAULT: the SimpleAiParams defaults with fog on, the fair
    /// setting of every level). Each value below is AI tuning measured with `sim tourney`; ai/09 §6 and specs/ai/data/difficulty-table.csv
    /// give the axes (review delay, pick noise, concurrent offensives, scouting and search quality).
    /// </summary>
    public static SimpleAiParams For(AiLevel level, SimpleAiParams? baseParams = null)
    {
        var p = baseParams ?? new SimpleAiParams { UseFog = true };
        return level switch
        {
            // ai/09 §6 Easy column: reacts late (reviews about twice as far apart), one offensive at a time, 30% pick noise, few scouts and spies.
            AiLevel.Easy => p with
            {
                MilitaryEvery = 6, ProductionEvery = 8, EconomyEvery = 10, AgentsEvery = 4,
                PickNoisePercent = 30, MaxOffensives = 1, ScoutCount = 2, HqSpies = 2,
            },
            AiLevel.Hard => p with { },
            _ => p,
        };
    }

    /// <summary>The level named by a word (easy, medium, normal, hard; any case), or null.</summary>
    public static AiLevel? Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "easy" => AiLevel.Easy,
        "medium" or "normal" => AiLevel.Medium,
        "hard" => AiLevel.Hard,
        _ => null,
    };
}
