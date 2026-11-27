using Praedra.Core.Clock;
using Praedra.Core.Setup;

namespace Praedra.Core.Economy;

/// <summary>
/// A side's refined money flow for the screen: what its refineries made lately and what its yards use at their present pace, the player's and
/// the advisor's apart. Reads only; changes nothing (QA's UI game, 2026-11-27, item 2: "Refined went 300 to 22 by day 321 and the top bar shows
/// no income or spending"). rule: m2-mines-refineries-pools §Pools; m2-construction §Build time.
/// </summary>
public static class RefinedFlow
{
    /// <summary>The window income is averaged over, in days. DEFAULT (Dev lead 2026-11-27, screen only).</summary>
    public const int WindowDays = 30;

    /// <summary>
    /// Refined the side's refineries made over the last <see cref="WindowDays"/> days, and the days that covers: events are not saved, so right
    /// after a load the window is only the days played since (Days 0 = nothing to go on yet).
    /// </summary>
    public static (int Made, int Days) Made(GameHost host, Side side) => Made(host, side, WindowDays);

    /// <summary>
    /// The same over the last <paramref name="windowDays"/> days (at most <see cref="WindowDays"/>): the screen counts a whole number of
    /// refinery cycles, so each refinery puts the same number of batches in the window whatever the day (UI review 36 on #419: with a 7-day
    /// cycle a 30-day window holds 4 or 5 batches of each refinery, and the trend mark by Refined flickered near balance). Reads only.
    /// </summary>
    public static (int Made, int Days) Made(GameHost host, Side side, int windowDays)
    {
        windowDays = Math.Clamp(windowDays, 0, WindowDays);
        int today = host.Game.Day, from = today - windowDays, made = 0, first = today;
        var events = host.Game.Events;
        for (int i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            if (e.Day <= from) { first = from; break; }
            first = Math.Min(first, e.Day - 1);
            if (e is ResourceProduced p && p.PoolSide == side && p.Resource == "Refined") made++;
        }
        return (made, today - first);
    }

    /// <summary>Refined used per <see cref="WindowDays"/> days at the present pace: (the player's builds, the advisor's).</summary>
    public static (int Player, int Advisor) Use(GameHost host, Side side) => host.Construction.RefinedUsePer30Days(side);
}
