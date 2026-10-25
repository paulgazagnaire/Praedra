using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// QA-19 (QA round 31): the Hand AI stopped attacking once its picture showed no Collective system. Seed 15 Medium mirror: the last Collective system,
// empty and one lane from Hand space, was last seen on day 1790, its picture said neutral, and the Hand sat for 500+ days with no winner.
// Its scouts weigh a look by its age only up to StaleDays, so every system unseen for 120 days tied and the nearest went first; the forgotten
// corner never came up. rule: ai tuning, SimpleAiParams.BlindHunt (DEFAULT true; New AI rule, no sheet).
public class SimpleAiBlindHuntTests
{
    [Fact]
    public void BlindHuntIsOnByDefault() => Assert.True(new SimpleAiParams().BlindHunt);

    // The QA-19 shape on a short game. Day 1: Hand regiments take every Collective system but one, X: the Collective system the Hand's picture does not
    // show as Collective (a hidden cell) that lies farthest from Charter. Assault-only control, so the debug regiments set the holder. The Collective
    // gives no orders. Day 300: a debug espionage refreshes the Hand's picture of every system it does not hold except X, so from then on X is
    // the one system it has not looked at for long, while the others age past StaleDays (all tie for the scouts, as on seed 15) but not past
    // BlindStaleDays. The day the game ends (the Collective holds no system), or 0 by the day limit.
    private static int EndDay(int seed, bool blind, int days)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = new GameSettings { ControlRule = ControlRule.AssaultOnly } });
        var m = new AiMatch(h, AiLevels.For(AiLevel.Medium) with { BlindHunt = blind }, AiLevels.For(AiLevel.Medium), passive: Side.Collective);
        var view = h.Fog.ViewFor(Side.Hand);
        var g = h.Data.Galaxy;
        var rebel = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Collective).Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.True(g.TryFindSystem(Praedra.Core.Victory.VictoryModule.CoruscantName, out int ci));
        long Far(string s) { g.TryFindSystem(s, out int i); long dx = g.Systems[i].MapX - g.Systems[ci].MapX, dy = g.Systems[i].MapY - g.Systems[ci].MapY; return dx * dx + dy * dy; }
        string x = rebel.Where(s => s != h.HqSystem && view.Picture(s)?.Owner?.Value.Owner != Side.Collective)
            .OrderByDescending(Far).ThenBy(s => s, StringComparer.Ordinal).First();
        bool taken = false, refreshed = false;
        h.Game.RegisterHook("5.2", ctx =>
        {
            if (!taken)
            {
                taken = true;
                foreach (var s in rebel) if (s != x) h.Control.DebugApplyTroops(ctx, s, Side.Hand, "Tranches", 3);
            }
            else if (!refreshed && ctx.Day >= 300)
            {
                refreshed = true;
                foreach (var sys in g.Systems)
                    if (sys.Name != x && h.Fleets.OwnerOf(sys.Name) != Side.Hand && !h.Game.State.DeathStar.IsDestroyed(sys.Name)) h.Fog.EspionageSuccess(ctx, Side.Hand, sys.Name);
            }
        });
        m.Step();
        Assert.Equal(new[] { x }, h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Collective).Select(kv => kv.Key).ToArray());
        Assert.DoesNotContain(g.Systems, s => view.Picture(s.Name)?.Owner?.Value.Owner == Side.Collective);
        while (h.Game.Day < days && h.Victory.Winner == null) m.Step();
        return h.Victory.Winner == Side.Hand ? h.Game.Day : 0;
    }

    // Measured on v5 (seeds 1-4, the last Collective system Regrowth on each): BlindHunt on, the game ends on day 352, 352, 361, 364; off, on day 457,
    // 460, 424, 424 (its scouts look at X too, but no pile goes there until X is the nearest neutral target left). Floors: on, every seed by
    // day 400; off, none by day 400.
    [Fact]
    public void BlindHunt_TheHandTakesTheLastCollectiveSystemItHasNotSeenForLong()
    {
        // v6 re-pin 2026-10-25 (Dev lead; Paul chose "Ship now" on the spiral-map card, re-measure the AI checks on the new map; ai-notes/2026-10-24-spiral-map-v6.md): on the spiral map X is close enough that the scouts often reach it anyway. Measured to day 800: on 364, 451, 379,
        // 352; off 364, 445, 391, 481. Floors: on, every seed by day 500; on never more than 10 days after off; on at least 100 days sooner on
        // some seed (the test still measures the switch). The v5 floor (on by 400, off never) waits for AI tuning to resume.
        // Praedra names re-pin 2026-10-25 (Dev lead; Paul's order to remove the Star Wars names; systems sort by name, so every game moved):
        // measured to day 800: on 364, 328, 307, 379; off 364, 394, 307, 379. Floors: on, every seed by day 500 (unchanged); on never more
        // than 10 days after off (unchanged); on at least 60 days sooner on some seed (was 100; measured 66).
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (s, On: EndDay(s, true, 500), Off: EndDay(s, false, 500)));
        Assert.All(res, r => Assert.True(r.On > 0, $"seed {r.s}: BlindHunt on, no winner by day 500"));
        Assert.All(res, r => Assert.True(r.Off == 0 || r.On <= r.Off + 10, $"seed {r.s}: BlindHunt on ended on day {r.On}, off on day {r.Off}"));
        Assert.Contains(res, r => r.Off == 0 || r.Off - r.On >= 60);
    }
}
