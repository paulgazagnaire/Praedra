namespace Praedra.Core.AI;

/// <summary>
/// Sparring bots (AI trainer, 2026-11-27; Paul "Bots first"): Hand AI presets that play the way a human Hand would, so every Collective change
/// is judged against them as well as against the plain Hand. Each one is a level's parameters with a style on top; none gets more income, more
/// sight or anything the plain AI lacks (no cheats, Paul 2026-10-12). Findings and numbers: specs/engine/ai-notes/2026-11-27-sparring-bots.md.
/// </summary>
public static class SparringBots
{
    /// <summary>The bots by name, in the order the notes list them.</summary>
    public static IReadOnlyList<string> Names { get; } = new[] { "Wall" };

    /// <summary>
    /// The bot's parameters on top of <paramref name="level"/>, or null for an unknown name.
    /// Wall: a Hand that is hard to kill. It walls Charter and its three biggest yard and mine worlds (2 shields and 2 batteries each), shields
    /// every mine world the Collective shells (3 shields, up to 6 worlds), reinforces shelled worlds to 4 regiments, and retreats and repairs
    /// early to keep its navy.
    /// </summary>
    public static SimpleAiParams? For(string name, AiLevel level = AiLevel.Medium)
    {
        var p = AiLevels.For(level);
        return name switch
        {
            "Wall" => p with
            {
                FortifyWorlds = 4, FortifyBatteries = 2,
                GuardRaidedDays = 360, GuardRaidedShields = 3,
                ShelledGarrison = 4,
                RetreatOddsPercent = 110, RepairBelowPercent = 90,
            },
            _ => null,
        };
    }
}
