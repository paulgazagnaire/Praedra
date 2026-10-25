using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// QA-19 (QA round 31): the Empire AI stopped attacking once its picture showed no Rebel system. Seed 15 Medium mirror: the last Rebel system,
// empty and one lane from Imperial space, was last seen on day 1790, its picture said neutral, and the Empire sat for 500+ days with no winner.
// Its scouts weigh a look by its age only up to StaleDays, so every system unseen for 120 days tied and the nearest went first; the forgotten
// corner never came up. rule: ai tuning, SimpleAiParams.BlindHunt (DEFAULT true; New AI rule, no sheet).
public class SimpleAiBlindHuntTests
{
    [Fact]
    public void BlindHuntIsOnByDefault() => Assert.True(new SimpleAiParams().BlindHunt);

    // The QA-19 shape on a short game. Day 1: Imperial regiments take every Rebel system but one, X: the Rebel system the Empire's picture does not
    // show as Rebel (a hidden cell) that lies farthest from Coruscant. Assault-only control, so the debug regiments set the holder. The Alliance
    // gives no orders. Day 300: a debug espionage refreshes the Empire's picture of every system it does not hold except X, so from then on X is
    // the one system it has not looked at for long, while the others age past StaleDays (all tie for the scouts, as on seed 15) but not past
    // BlindStaleDays. The day the game ends (the Alliance holds no system), or 0 by the day limit.
    private static int EndDay(int seed, bool blind, int days)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = new GameSettings { ControlRule = ControlRule.AssaultOnly } });
        var m = new AiMatch(h, AiLevels.For(AiLevel.Medium) with { BlindHunt = blind }, AiLevels.For(AiLevel.Medium), passive: Side.Alliance);
        var view = h.Fog.ViewFor(Side.Empire);
        var g = h.Data.Galaxy;
        var rebel = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Alliance).Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.True(g.TryFindSystem(Praedra.Core.Victory.VictoryModule.CoruscantName, out int ci));
        long Far(string s) { g.TryFindSystem(s, out int i); long dx = g.Systems[i].MapX - g.Systems[ci].MapX, dy = g.Systems[i].MapY - g.Systems[ci].MapY; return dx * dx + dy * dy; }
        string x = rebel.Where(s => s != h.HqSystem && view.Picture(s)?.Owner?.Value.Owner != Side.Alliance)
            .OrderByDescending(Far).ThenBy(s => s, StringComparer.Ordinal).First();
        bool taken = false, refreshed = false;
        h.Game.RegisterHook("5.2", ctx =>
        {
            if (!taken)
            {
                taken = true;
                foreach (var s in rebel) if (s != x) h.Control.DebugApplyTroops(ctx, s, Side.Empire, "Imperial Army Regiment", 3);
            }
            else if (!refreshed && ctx.Day >= 300)
            {
                refreshed = true;
                foreach (var sys in g.Systems)
                    if (sys.Name != x && h.Fleets.OwnerOf(sys.Name) != Side.Empire && !h.Game.State.DeathStar.IsDestroyed(sys.Name)) h.Fog.EspionageSuccess(ctx, Side.Empire, sys.Name);
            }
        });
        m.Step();
        Assert.Equal(new[] { x }, h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Alliance).Select(kv => kv.Key).ToArray());
        Assert.DoesNotContain(g.Systems, s => view.Picture(s.Name)?.Owner?.Value.Owner == Side.Alliance);
        while (h.Game.Day < days && h.Victory.Winner == null) m.Step();
        return h.Victory.Winner == Side.Empire ? h.Game.Day : 0;
    }

    // Measured on v5 (seeds 1-4, the last Rebel system Kal'Shebbol on each): BlindHunt on, the game ends on day 352, 352, 361, 364; off, on day 457,
    // 460, 424, 424 (its scouts look at X too, but no pile goes there until X is the nearest neutral target left). Floors: on, every seed by
    // day 400; off, none by day 400.
    [Fact]
    public void BlindHunt_TheEmpireTakesTheLastRebelSystemItHasNotSeenForLong()
    {
        // v6 re-pin 2026-10-25 (Dev lead; Paul chose "Ship now" on the spiral-map card, re-measure the AI checks on the new map; ai-notes/2026-10-24-spiral-map-v6.md): on the spiral map X is close enough that the scouts often reach it anyway. Measured to day 800: on 364, 451, 379,
        // 352; off 364, 445, 391, 481. Floors: on, every seed by day 500; on never more than 10 days after off; on at least 100 days sooner on
        // some seed (the test still measures the switch). The v5 floor (on by 400, off never) waits for AI tuning to resume.
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (s, On: EndDay(s, true, 500), Off: EndDay(s, false, 500)));
        Assert.All(res, r => Assert.True(r.On > 0, $"seed {r.s}: BlindHunt on, no winner by day 500"));
        Assert.All(res, r => Assert.True(r.Off == 0 || r.On <= r.Off + 10, $"seed {r.s}: BlindHunt on ended on day {r.On}, off on day {r.Off}"));
        Assert.Contains(res, r => r.Off == 0 || r.Off - r.On >= 100);
    }
}
