using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// QA-22 (QA round 38): the AI shelled worlds where it saw nothing to hit (Medium mirror seed 3: 401 of 461 strikes had no military target, a
// strike every 3 days on Redfen on days 259-268). Under B7 an empty Military strike always rolls collateral against the bomber. The AI now
// can order a Military strike only when its picture shows a military target there. rule: ai tuning, SimpleAiParams.NoBlindStrikes (DEFAULT
// off, measured: it does not end more games on B7 main and weakens Hard; New AI rule, no sheet); B7-bombardment §5.3; m3-bombardment §Targets.
public class SimpleAiNoBlindStrikesTests
{
    [Fact]
    public void NoBlindStrikesIsOffByDefault()
    {
        Assert.False(new SimpleAiParams().NoBlindStrikes);
        Assert.False(new SimpleAiParams().BlindProbeStrikes);
    }

    // Medium mirror with fog to day 600. A strike is empty when, at the end of the day before it, the target held no regiment of the other side
    // on the ground and no defence facility. Returns (strikes, empty strikes) over both sides.
    private static (int Strikes, int Empty) Strikes(int seed, bool on, bool probe = false)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
        // the guerrilla Collective (Underground, Guerrilla: 2026-10-26) shells less and raids instead; off here, so the test keeps measuring the same blind Military strikes
        var p = AiLevels.For(AiLevel.Medium) with { NoBlindStrikes = on, BlindProbeStrikes = probe, Underground = false, Guerrilla = false };
        var m = new AiMatch(h, p, p);
        int strikes = 0, empty = 0;
        var targets = new Dictionary<string, int>(StringComparer.Ordinal); // lookup only
        while (h.Game.Day < 600 && h.Victory.Winner == null)
        {
            targets.Clear();
            foreach (var o in h.Economy.Objects)
            {
                bool mil = (o.Category == ObjectCategory.Regiment && o.AboardShip == 0) || (o.Category == ObjectCategory.Facility && o.Completed && h.Defenses.Data.IsDefense(o.Class));
                if (mil && o.System != null) targets[o.System] = targets.GetValueOrDefault(o.System) + 1;
            }
            m.Step();
            foreach (var ai in new[] { m.Hand, m.Collective })
                foreach (var o in ai.Submitted.Where(x => x.Kind == "Bombard"))
                {
                    strikes++;
                    if (targets.GetValueOrDefault(o.Detail.Split(' ')[^1]) == 0) empty++;
                }
        }
        return (strikes, empty);
    }

    // Measured (seeds 1-4, strikes / empty ones): on 10/2, 14/3, 12/2, 8/0; off 79/59, 69/48, 84/69, 71/46. Floors: on, at most 5 empty
    // strikes on every seed; off, at least 30 (the test still measures the switch).
    [Fact]
    public void NoBlindStrikes_TheAiStopsShellingWorldsWithNothingToHit()
    {
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (s, On: Strikes(s, true), Off: Strikes(s, false)));
        // v6 re-pin 2026-10-25 (Dev lead; Paul chose "Ship now" on the spiral-map card, re-measure the AI checks on the new map; ai-notes/2026-10-24-spiral-map-v6.md): on 6/0, 13/2, 14/6, 11/0; off 41/30, 62/49, 116/82, 55/38. On-floor 5 -> 8; off-floor 30 unchanged.
        Assert.All(res, r => Assert.True(r.On.Empty <= 8, $"seed {r.s}: on, {r.On.Empty} of {r.On.Strikes} strikes had nothing to hit"));
        Assert.All(res, r => Assert.True(r.Off.Empty >= 30, $"seed {r.s}: off, only {r.Off.Empty} empty strikes (the test no longer measures the switch)"));
    }

    // BlindProbeStrikes: a garrison never seen may be probed until a strike there kills nothing. Measured (seeds 1-4, strikes / empty ones):
    // 38/22, 29/12, 30/16, 22/10, between on and off above. Floors: on every seed at most 25 empty strikes, and at least 5 (it does probe).
    [Fact]
    public void BlindProbeStrikes_ProbesAnUnseenGarrisonAndStopsWhenAStrikeFindsNothing()
    {
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (s, P: Strikes(s, true, true)));
        Assert.All(res, r => Assert.True(r.P.Empty is >= 5 and <= 25, $"seed {r.s}: probes on, {r.P.Empty} of {r.P.Strikes} strikes had nothing to hit"));
    }
}
