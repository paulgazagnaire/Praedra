using Rebellion.Core.AI;
using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.AI;

// Regression: a strike pile chased a small enemy fleet that kept retreating between two systems (the morale retreat of m4-battle-classic,
// to a lane neighbour by m4-retreat-damage-repair §3), with no losses on either side, for 50 days and more (sim aivai --passive empire
// --seed 1, days 741 to 790, Basilisk and Kirrek). The AI keeps no memory between days, so it now reads the retreats from the side's own event
// feed: an enemy fleet that retreated from it at or next to a system is no longer hunted there for RunnerMemoryDays (SimpleAiParams.RunnerRetreats,
// DEFAULT 1), and a fleet does not go back to, or through, a system where its own armed fleet had to retreat lately (AvoidBlockedPaths).
// The thresholds are AI tuning (SimpleAiParams DEFAULTs), not rules.
public class SimpleAiChaseTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiChaseTests(ITestOutputHelper o) { _out = o; }

    /// <summary>The old behaviour: no memory of retreats.</summary>
    private static readonly SimpleAiParams NoChaseMemory = new() { RunnerRetreats = 0, RetreatCountsAsLoss = false, AvoidBlockedPaths = false };

    /// <summary>
    /// MedA seed 1 with the Empire passive. An Alliance strike pile (three Mon Calamari Cruisers, no troops) stands on an Alliance-held system
    /// A next to a system B it does not hold; a lone Carrack Light Cruiser (armed, far weaker) stands on B, which has another neighbour (Imperial or neutral) to
    /// retreat to. Returns the retreats of that Carrack from battles with no losses on either side, by day, over <paramref name="days"/> days.
    /// </summary>
    private List<int> Chase(SimpleAiParams? alliance, int days)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        var g = h.Data.Galaxy; var owners = h.Game.State.SystemOwners;
        string? a = null, b = null;
        foreach (var sys in g.Systems.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            if (!owners.TryGetValue(sys.Name, out var o) || o != Side.Alliance || sys.Name == h.HqSystem) continue;
            foreach (var nb in g.NeighboursOf(sys.Index).OrderBy(n => g.Systems[n.System].Name, StringComparer.Ordinal))
            {
                string bn = g.Systems[nb.System].Name;
                if (bn == "Coruscant" || (owners.TryGetValue(bn, out var ob) && ob == Side.Alliance)) continue;
                if (h.Fleets.FleetsAt(bn).Any()) continue;
                // B needs another neighbour, Imperial or neutral and without fleets (the Carrack's way out)
                if (!g.NeighboursOf(nb.System).Any(x => g.Systems[x.System].Name is var xn && xn != sys.Name && !(owners.TryGetValue(xn, out var ox) && ox == Side.Alliance) && !h.Fleets.FleetsAt(xn).Any())) continue;
                a = sys.Name; b = bn; break;
            }
            if (a != null) break;
        }
        Assert.NotNull(a);
        h.Fleets.AddFleet(Side.Alliance, "Strike", new[] { "Mon Calamari Cruiser", "Mon Calamari Cruiser", "Mon Calamari Cruiser" }, a!);
        int prey = h.Fleets.AddFleet(Side.Empire, "Prey", new[] { "Carrack Light Cruiser" }, b!);
        var m = new AiMatch(h, alliance: alliance, passive: Side.Empire);
        m.RunTo(days);
        var ev = h.Game.Events; var retreats = new List<int>(); var pending = new List<FleetRetreated>();
        foreach (var e in ev)
            if (e is FleetRetreated fr) pending.Add(fr);
            else if (e is BattleEnded be)
            {
                if (be.Result.AllianceLosses == 0 && be.Result.EmpireLosses == 0) retreats.AddRange(pending.Where(x => x.Fleet == prey).Select(x => x.Day));
                pending.Clear();
            }
        _out.WriteLine($"A {a}, B {b}: the Carrack retreated without losses on days {string.Join(", ", retreats)}");
        return retreats;
    }

    // m6 chase fix T1: the scenario reproduces the chase with the old behaviour (no memory of retreats): the Carrack is hunted again and again.
    [Fact]
    public void T1_WithoutRetreatMemory_ThePileChasesARetreatingFleet()
    {
        var r = Chase(NoChaseMemory, 120);
        Assert.True(r.Count >= 6, $"only {r.Count} lossless retreats: the scenario no longer shows the chase");
    }

    // m6 chase fix T2: with the DEFAULT parameters the hunt stops after the first retreat; it may be tried again once the retreat has left the
    // memory (RunnerMemoryDays, DEFAULT 30). Measured: retreats on days 3, 93 and 99 (without the fix: every 3 days, 36 times). Limits: at
    // most 2 lossless retreats in any RunnerMemoryDays window, at most 4 in 120 days.
    [Fact]
    public void T2_WithRetreatMemory_TheChaseEndsWithinDays()
    {
        var r = Chase(null, 120);
        int window = new SimpleAiParams().RunnerMemoryDays;
        Assert.True(r.Count <= 4, $"{r.Count} lossless retreats: {string.Join(", ", r)}");
        foreach (var d in r) Assert.True(r.Count(x => x >= d && x < d + window) <= 2, $"still chasing around day {d}: {string.Join(", ", r)}");
    }
}
