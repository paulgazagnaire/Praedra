using Rebellion.Core.AI;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.AI;

// QA-12: the Medium AI against itself must finish its games. The 8-seed floors passed #68 while 11 of 16 Medium mirrors stopped ending (its
// leader marks kept the Emperor home from day one, so he no longer put down uprisings, and the Empire never finished the last Rebel worlds;
// the marks are DEFAULT off since #75). Same set-up as
// `sim tourney --a medium --b medium` (MedA start, default settings, fog on both sides). The games end through the playable slice's
// "the other side holds no system" rule (VictoryRules, DEFAULT; the 1998 game has no such ending, B6c / RE lead 2026-10-18) or the
// standard terms.
// Over seeds 1 to 8, the games with no winner by day 3000:
//   fe1a848 (#68 leader marks on, no AI Recruitment):       5 of 8 (seeds 2, 3, 4, 5, 8)
//   1c3c353 (#75 leader marks off, no AI Recruitment):      1 of 8 (seed 1)
//   + AI Recruitment (QA-12, MaxRecruiters 2):               1 of 8 (seed 6)
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
            return (Seed: seed, host.Victory.Winner, host.Game.Day, Empire: Held(Side.Empire), Alliance: Held(Side.Alliance));
        });
        foreach (var g in games) _out.WriteLine($"seed {g.Seed}: winner {g.Winner?.ToString() ?? "none"} day {g.Day}; systems {g.Empire}/{g.Alliance}");
        int open = games.Count(g => g.Winner == null);
        Assert.True(open <= 3, $"{open} of 8 Medium mirrors have no winner by day 3000");
    }
}
