using Praedra.Core.Clock;
using Praedra.Core.Setup;

namespace Praedra.Core.Economy;

/// <summary>
/// A side's refined money flow for the screen: what its refineries make and what its yards use, both at their present pace, the player's and
/// the advisor's apart. Reads only; changes nothing (QA's UI game, 2026-11-27, item 2: "Refined went 300 to 22 by day 321 and the top bar shows
/// no income or spending"). rule: m2-mines-refineries-pools §Pools; m2-construction §Build time.
/// </summary>
public static class RefinedFlow
{
    /// <summary>The span both paces are given per, in days. DEFAULT (Dev lead 2026-11-27, screen only).</summary>
    public const int WindowDays = 30;

    /// <summary>
    /// Refined made per <see cref="WindowDays"/> days at the present pace: the side's refineries as they work today, raw-limited, after smuggling
    /// (<see cref="EconomyModule.RefinedPacePer30Days"/>). The trend mark by Refined compares it with <see cref="Use"/>, both in the present
    /// tense (QA-131, 2026-11-27: a count of the last 30 days of batches lagged lost refineries and slower cycles; it is gone, so the lag
    /// cannot come back through it, Ponytail on #422).
    /// </summary>
    public static int MadePace(GameHost host, Side side) => host.Economy.RefinedPacePer30Days(side).Refined;

    /// <summary>The raw cannot feed every refinery cycle: the mines are what holds the refineries back (UI review 37).</summary>
    public static bool RawShort(GameHost host, Side side) => host.Economy.RefinedPacePer30Days(side).RawShort;

    /// <summary>Refined used per <see cref="WindowDays"/> days at the present pace: (the player's builds, the advisor's).</summary>
    public static (int Player, int Advisor) Use(GameHost host, Side side) => host.Construction.RefinedUsePer30Days(side);

    /// <summary>The same use per yard set, idle ones included with the use they would have (UI review 38 P2 2).</summary>
    public static IReadOnlyList<Praedra.Core.Construction.YardSetUse> UseBySet(GameHost host, Side side) => host.Construction.RefinedUseBySet(side);
}
