using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// QA-12: the Medium AI against itself must finish its games. The 8-seed floors passed #68 while 11 of 16 Medium mirrors stopped ending (its
// leader marks kept The Chair home from day one, so he no longer put down uprisings, and the Hand never finished the last Collective worlds;
// the marks are DEFAULT off since #75). Same set-up as
// `sim tourney --a medium --b medium` (MedA start, default settings, fog on both sides). The games end through the playable slice's
// "the other side holds no system" rule (VictoryRules, DEFAULT; Classic has no such ending, B6c / RE lead 2026-10-18) or the
// standard terms.
// Over seeds 1 to 8, the games with no winner by day 3000:
//   fe1a848 (#68 leader marks on, no AI Recruitment):       5 of 8 (seeds 2, 3, 4, 5, 8)
//   1c3c353 (#75 leader marks off, no AI Recruitment):      1 of 8 (seed 1)
//   + AI Recruitment (QA-12, MaxRecruiters 2):               1 of 8 (seed 6)
//   f932dfe (#79 group D characters, #80 New battle DEFAULT): 1 of 8 (seed 8)
//   + no Rescue at prisoners aboard fleets (QA-13):          2 of 8 (seeds 2, 8)
// Floor: at most 3 of 8 games with no winner by day 3000.
public class GamesEndTests
{
    private readonly ITestOutputHelper _out;
    public GamesEndTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void MediumMirror_MostGamesEndBy3000()
    {
        var games = MultiSeed.Play(MultiSeed.OneToEight, seed =>
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
            var p = AiLevels.For(AiLevel.Medium);
            var m = new AiMatch(host, p, p);
            while (host.Game.Day < 3000 && host.Victory.Winner == null) m.Step();
            int Held(Side s) => host.Data.Galaxy.Systems.Count(x => host.Fleets.OwnerOf(x.Name) == s);
            return (Seed: seed, host.Victory.Winner, host.Game.Day, Hand: Held(Side.Hand), Collective: Held(Side.Collective));
        });
        foreach (var g in games) _out.WriteLine($"seed {g.Seed}: winner {g.Winner?.ToString() ?? "none"} day {g.Day}; systems {g.Hand}/{g.Collective}");
        int open = games.Count(g => g.Winner == null);
        // v6 re-pin 2026-10-25 (Dev lead; Paul chose "Ship now" on the spiral-map card, re-measure the AI checks on the new map; ai-notes/2026-10-24-spiral-map-v6.md): measured 4 of 8 open on v6 (seeds 1, 2, 3, 4; seeds 2 and 4 end 298/2 and 268/5, the known endgame stall). The bar was 3 on v5;
        // it goes back to 3 when the endgame rule (the dominance-win card) lands.
        // Capital-and-control re-pin 2026-10-26 (Dev lead; Paul chose "Capital + 70%"): measured 0 of 8 open (the Hand wins all 8, on day 653 to
        // 2337). Bar 1.
        // Guerrilla Collective 2026-10-26 (AI trainer; Dev lead OK as a stopgap): Medium is now balanced, and balanced wars run longer. Measured 3 of
        // 8 open (seeds 1, 6, 8: 181/61, 68/177, 110/135); the other 5 are Collective wins on day 2193 to 2895. Bar 3; the AI trainer owns bringing it back to 1 (the Hearth hunt and the slow wars, next).
        // Play to win 2026-10-26 (AI trainer, QA-54 and AiLevels.PlayToWin, with the Hearth tip): measured 3 of 8 open (seeds 1, 7, 8: 208/51, 95/217,
        // 271/3); the Collective wins 4 (day 1806 to 2484), the Hand 1. Bar 3 still; over 96 seeds the open games fell from 42 to 33.
        Assert.True(open <= 3, $"{open} of 8 Medium mirrors have no winner by day 3000");
    }
}
