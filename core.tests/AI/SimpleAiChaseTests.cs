using Praedra.Core.AI;
using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// Regression: a strike pile chased a small enemy fleet that kept retreating between two systems (the morale retreat of m4-battle-classic,
// to a lane neighbour by m4-retreat-damage-repair §3), with no losses on either side, for 50 days and more (sim aivai --passive hand
// --seed 1, days 741 to 790, Scoria and Stonebrake). The AI keeps no memory between days, so it now reads the retreats from the side's own event
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
    /// MedA seed 1 with the Hand passive. A Collective strike pile (three The Moorings Cruisers, no troops) stands on a Collective-held system
    /// A next to a system B it does not hold; a lone Warrant (armed, far weaker) stands on B, which has another neighbour (Hand or neutral) to
    /// retreat to. Returns the retreats of that Warrant from battles with no losses on either side, by day, over <paramref name="days"/> days.
    /// </summary>
    private List<int> Chase(SimpleAiParams? collective, int days)
    {
        // Classic battle set explicitly: the scenario is the Classic morale retreat (battle DEFAULT New since 2026-10-18).
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA, Settings = new GameSettings { Battle = new BattleParams { Ruleset = BattleRuleset.Classic } } });
        var g = h.Data.Galaxy; var owners = h.Game.State.SystemOwners;
        string? a = null, b = null;
        foreach (var sys in g.Systems.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            if (!owners.TryGetValue(sys.Name, out var o) || o != Side.Collective || sys.Name == h.HqSystem) continue;
            foreach (var nb in g.NeighboursOf(sys.Index).OrderBy(n => g.Systems[n.System].Name, StringComparer.Ordinal))
            {
                string bn = g.Systems[nb.System].Name;
                if (bn == "Charter" || (owners.TryGetValue(bn, out var ob) && ob == Side.Collective)) continue;
                if (h.Fleets.FleetsAt(bn).Any()) continue;
                // B needs another neighbour, Hand or neutral and without fleets (the Warrant's way out)
                if (!g.NeighboursOf(nb.System).Any(x => g.Systems[x.System].Name is var xn && xn != sys.Name && !(owners.TryGetValue(xn, out var ox) && ox == Side.Collective) && !h.Fleets.FleetsAt(xn).Any())) continue;
                a = sys.Name; b = bn; break;
            }
            if (a != null) break;
        }
        Assert.NotNull(a);
        h.Fleets.AddFleet(Side.Collective, "Strike", new[] { "Ark", "Ark", "Ark" }, a!);
        int prey = h.Fleets.AddFleet(Side.Hand, "Prey", new[] { "Warrant" }, b!);
        var m = new AiMatch(h, collective: collective, passive: Side.Hand);
        m.RunTo(days);
        var ev = h.Game.Events; var retreats = new List<int>(); var pending = new List<FleetRetreated>();
        foreach (var e in ev)
            if (e is FleetRetreated fr) pending.Add(fr);
            else if (e is BattleEnded be)
            {
                if (be.Result.CollectiveLosses == 0 && be.Result.HandLosses == 0) retreats.AddRange(pending.Where(x => x.Fleet == prey).Select(x => x.Day));
                pending.Clear();
            }
        _out.WriteLine($"A {a}, B {b}: the Warrant retreated without losses on days {string.Join(", ", retreats)}");
        return retreats;
    }

    // m6 chase fix T1: the scenario reproduces the chase with the old behaviour (no memory of retreats): the Warrant is hunted again and again.
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
