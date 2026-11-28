namespace Praedra.Core.AI;

/// <summary>
/// Sparring bots (AI trainer, 2026-11-27; Paul "Bots first"): Hand AI presets that play the way a human Hand would, so every Collective change
/// is judged against them as well as against the plain Hand. Each one is a level's parameters with a style on top; none gets more income, more
/// sight or anything the plain AI lacks (no cheats, Paul 2026-10-12). Findings and numbers: specs/engine/ai-notes/2026-11-27-sparring-bots.md.
/// </summary>
public static class SparringBots
{
    /// <summary>The bots by name, in the order the notes list them.</summary>
    public static IReadOnlyList<string> Names { get; } = new[] { "Wall", "Grinder" };

    /// <summary>
    /// The bot's parameters on top of <paramref name="level"/>, or null for an unknown name.
    /// Wall: a Hand that is hard to kill. It walls Charter and its three biggest yard and mine worlds (2 shields and 2 batteries each), shields
    /// every mine world the Collective shells (2 shields, up to 6 worlds), and retreats and repairs early to keep its navy. Measured 2026-11-27
    /// against the Medium Collective (main f5482cc, seeds 1-64 start MedE and 1-32 start MedA): Collective wins 56 -> 45 of 96, open games 26 -> 43,
    /// Hand wins 14 -> 8. With 3 shields and shelled worlds reinforced to 4 regiments it was no harder to kill (Collective 60 of 96). On main
    /// b3979ed (Neutralise, Wreckers) it no longer helps: 6/76/14 against the plain Hand's 5/75/16 (MedE 1-64, MedA 1-32). Kept as a style,
    /// not as a stronger Hand: a Collective change is also checked against a Hand that turtles, so it does not only learn to beat Grinder.
    /// Grinder: the evolved Hand (AI trainer 2026-11-28, 55 trials of a random-mutation search on training seeds 101-132). It attacks at 110%
    /// odds instead of 150%, sends task forces only at twice the strength they need, retreats at even odds, builds projects of up to 320 days,
    /// garrisons 2 regiments at a time and strikes at the enemy HQ with 20 strength. Measured against the Medium Collective on main b3979ed,
    /// 192 fresh games (MedE 1-64 and 133-196, MedA 1-64), Hand / Collective / open: plain Medium Hand 10/149/33, Grinder 14/138/40.
    /// Rechecked on main 24447f5 over 192 more fresh games (MedE 301-428, MedA 133-196): plain 11/154/27, Grinder 15/135/42. Over all 384
    /// games: plain 21/303/60, Grinder 29/273/82. Grinder mostly stalls the Collective (more open games); it rarely beats it.
    /// </summary>
    public static SimpleAiParams? For(string name, AiLevel level = AiLevel.Medium)
    {
        var p = AiLevels.For(level);
        return name switch
        {
            "Wall" => p with
            {
                FortifyWorlds = 4, FortifyBatteries = 2,
                GuardRaidedDays = 360, GuardRaidedShields = 2,
                RetreatOddsPercent = 110, RepairBelowPercent = 90,
            },
            "Grinder" => p with
            {
                AttackOddsPercent = 110, TaskForceMarginPercent = 200, RetreatOddsPercent = 100,
                MaxBuildDays = 320, GarrisonBatch = 2, WinStrikeStrength = 20,
            },
            _ => null,
        };
    }
}
