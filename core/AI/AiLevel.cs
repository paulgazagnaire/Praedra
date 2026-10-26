namespace Praedra.Core.AI;

/// <summary>
/// How well the computer opponent plays. Every level sees only its side's fog-of-war view and gets no hidden bonus (ai/12 D3, Paul
/// 2026-10-12: Hard is smarter, never a cheat; economy-ai D4). The Classic head start (more systems and forces at setup, setup-victory/02) is a
/// separate setup choice (StartPreset), so a player can mix the two. rule: ai/09 §6 (difficulty is an AI parameter set, not a head start).
/// </summary>
public enum AiLevel
{
    /// <summary>Today's AI, handicapped: slower reviews, one offensive at a time, sometimes a worse target, fewer scouts and spies.</summary>
    Easy,
    /// <summary>The DEFAULT AI (<see cref="SimpleAiParams"/> defaults) plus the Collective's guerrilla (SafeHearth, Underground, Guerrilla).</summary>
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
                PickNoisePercent = 30, MaxOffensives = 1, ScoutCount = 2, HqSpies = 2, SafeHearth = true,
            },
            // AI trainer 2026-10-26 (Paul: Easy about today's AI, Medium balanced, Hard really hard; the Collective plays guerrilla). Collective-only
            // switches, so the Hand plays as before. Medium Hand against the Collective, 96 seeds (1-96), day 3000, fog, Hand / Collective / no winner:
            // main f00fbb8 Medium 28 / 2 / 2 of 32; Easy here 94 / 0 / 2; Medium here 30 / 35 / 31; Hard here 14 / 58 / 24. Hard Hand against the
            // Hard Collective 36 / 42 / 18. Notes: specs/engine/ai-notes/2026-10-26-guerrilla-collective.md.
            AiLevel.Medium => p with { SafeHearth = true, Underground = true, Guerrilla = true },
            // AI trainer 2026-10-17 (sim tourney, 16 seeds x both sides, Medium against Medium plus one change; a placebo change scored 55%, so
            // only a gap well above that counts): a military review every day (Medium every 3) took 63% of the systems, the one clear win of 15
            // tried. Probe speculators on the HQ hunt found the Hearth in 13 of 16 games against 8 for Medium without costing systems (51%); kept
            // because against a player the Hand wins by finding the HQ, not by holding every system.
            // AI trainer 2026-10-17 (QA-2): Hard also went after the enemy's last systems before neutral ones once five times bigger than the
            // enemy it knows; since 2026-10-18 every level does (FinishWhenAheadRatio DEFAULT 5, needed to end games under derived control).
            // AI trainer 2026-10-26: the Hard Collective keeps more agents out and raids more; without these counts a Medium Hand won 24 of 96
            // instead of 14 (more still cost wins: 32 forces, 16 missions, 6 looks and 4 raids gave 28 Hand wins).
            AiLevel.Hard => p with
            {
                MilitaryEvery = 1, HuntSpeculators = 8, SafeHearth = true, Underground = true, Guerrilla = true,
                UndergroundForcesWanted = 20, UndergroundSubversion = 10, WatchLooks = 4, RaidStrikes = 3,
            },
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
