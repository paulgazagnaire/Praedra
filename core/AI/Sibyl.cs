using Praedra.Core.Clock;
using Praedra.Core.Setup;

namespace Praedra.Core.AI;

/// <summary>The two jobs Sibyl can do, each with its own switch. rule: m6-advisor-sibyl §Switches.</summary>
public enum SibylJob { ManageGarrisons, ManageProduction }

/// <summary>
/// Sibyl, the Hand player's advisor (Praevisio's assistant): two switches, Manage Garrisons and Manage Production, both off at a new game. When on,
/// she queues each day the orders a player could give: garrison regiments for every held world short of the rule's need (uprisings first), and a
/// mine or refinery at every idle construction yard. The switches are game state (markers set by the ordinary SetMarker order), so they are
/// saved, hashed and replayed; Sibyl herself keeps nothing between days. Call <see cref="PlanDay"/> once per day before <c>Game.Step()</c>.
/// rule: m6-advisor-sibyl.
/// </summary>
public sealed class Sibyl
{
    private readonly GameHost _h;
    private readonly SimpleAi _ai;

    public Sibyl(GameHost host, Side side)
    {
        _h = host;
        Side = side;
        _ai = new SimpleAi(host, side);
    }

    public Side Side { get; }

    /// <summary>The marker that holds a switch: <c>sibyl.Hand.garrisons</c>, <c>sibyl.Hand.production</c>. rule: m6-advisor-sibyl §Switches.</summary>
    public static string Marker(Side side, SibylJob job) =>
        $"sibyl.{side}.{(job == SibylJob.ManageGarrisons ? "garrisons" : "production")}";

    /// <summary>Is the switch on in the game state (absent = off, Classic: both start off).</summary>
    public static bool IsOn(GameHost host, Side side, SibylJob job) =>
        host.Game.State.Markers.TryGetValue(Marker(side, job), out int v) && v > 0;

    public bool IsOn(SibylJob job) => IsOn(_h, Side, job);

    /// <summary>Queue the order that sets a switch; it takes effect in this day's Step, so from the next day's plan.</summary>
    public long Submit(SibylJob job, bool on) => _h.Game.Submit(Side, Marker(Side, job), on ? 1 : 0);

    /// <summary>The orders queued by the last <see cref="PlanDay"/>.</summary>
    public IReadOnlyList<SubmittedOrder> Submitted { get; private set; } = Array.Empty<SubmittedOrder>();

    /// <summary>
    /// Worlds in uprising the last <see cref="PlanDay"/> could not send new regiments to because she keeps her upkeep margin (the screen tells the
    /// player to free room). rule: m6-advisor-sibyl §Manage Garrisons 6, §Messages.
    /// </summary>
    public IReadOnlyList<string> UnpaidUprisings { get; private set; } = Array.Empty<string>();

    /// <summary>Queue today's orders for the jobs that are on. Reads state only; nothing when both are off.</summary>
    public IReadOnlyList<SubmittedOrder> PlanDay()
    {
        bool g = IsOn(SibylJob.ManageGarrisons), p = IsOn(SibylJob.ManageProduction);
        if (!g && !p) { UnpaidUprisings = Array.Empty<string>(); return Submitted = Array.Empty<SubmittedOrder>(); }
        _ai.PlanAdvisor(g, p);
        UnpaidUprisings = _ai.AdvisorUnpaidUprisings.ToList();
        return Submitted = _ai.Submitted.ToList();
    }
}
