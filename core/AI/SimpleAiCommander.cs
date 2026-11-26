using Praedra.Core.Clock;

namespace Praedra.Core.AI;

/// <summary>One plan the side's AI runs now, for the commander's readout: the plan's name, why it runs, and how far it has got.</summary>
public sealed record AiPlanNow(string Plan, string Why, string Progress);

public sealed partial class SimpleAi
{
    // ================================================================== the commander's readout (AI trainer 2026-11-26)

    /// <summary>
    /// The plans the side runs now, with why and how far, for a playtest readout and for logs (the adaptive commander,
    /// specs/engine/ai-notes/2026-11-26-adaptive-commander.md). Reads state only; never changes the AI's choices.
    /// </summary>
    public IReadOnlyList<AiPlanNow> PlansNow()
    {
        var list = new List<AiPlanNow>();
        if (_p.FortifyWorlds > 0)
        {
            var targets = FortifyTargets().Take(_p.FortifyWorlds).ToList();
            int done = targets.Count(w => { var (sh, bat) = OwnDefencesAt(w); return sh >= _p.FortifyShields && bat >= _p.FortifyBatteries; });
            list.Add(new AiPlanNow("Fortify", $"key worlds: {string.Join(", ", targets)}", $"{done} of {targets.Count} walled"));
        }
        foreach (var c in Campaigns())
        {
            string phase = c.Strike > 0 ? $"striking since day {c.Strike}" : $"preparing since day {c.Started}";
            list.Add(new AiPlanNow(c.Plan.ToString(), $"front {c.Number} in {c.Sector}", phase));
        }
        if (_p.SiegeShields) list.Add(new AiPlanNow("Siege", "shielded worlds are shelled before landing", ""));
        if (_side == Side.Hand && _view != null)
            list.Add(KnownEnemyHq is { } hq
                ? new AiPlanNow("Strike the Hearth", $"the Hearth was seen at {hq}", "")
                : new AiPlanNow("Hunt", "the Hearth is unknown", ""));
        if (_side == Side.Collective && OwnHearth() is { } home)
            list.Add(new AiPlanNow("Hide", $"the Hearth is at {home}", _p.SafeHearth ? "moves by risk" : ""));
        return list;
    }
}
