using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Missions;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// The Collective's guerrilla (AI trainer 2026-10-26; Paul 2026-10-26: Easy about today's AI, Medium balanced, Hard really hard, the Collective
// "always spying and sabotaging"). Three Collective-only switches, DEFAULT off, on in Medium and Hard (AiLevel.cs): SafeHearth (the Hearth no
// longer moves onto a world that its arrival drop turns, m5-fog-and-hq §F6, m2-control-and-capture), Underground (Incite, Sabotage, Spike the
// Harvester, Espionage watch, special forces as agents: m5-missions) and Guerrilla (elusive fleets, civilian raids: m3-bombardment §3).
// rule: ai tuning, SimpleAiParams.SafeHearth / Underground / Guerrilla; notes specs/engine/ai-notes/2026-10-26-guerrilla-collective.md.
public class SimpleAiGuerrillaTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiGuerrillaTests(ITestOutputHelper o) { _out = o; }

    private static readonly SimpleAiParams Medium = AiLevels.For(AiLevel.Medium);
    private static readonly SimpleAiParams Old = Medium with { SafeHearth = false, Underground = false, Guerrilla = false };

    [Fact]
    public void Switches_AreOffByDefault_OnInMediumAndHard_OffInEasyButTheSafeHearth()
    {
        var d = new SimpleAiParams();
        Assert.False(d.SafeHearth || d.Underground || d.Guerrilla);
        foreach (var l in new[] { AiLevel.Medium, AiLevel.Hard }) { var p = AiLevels.For(l); Assert.True(p.SafeHearth && p.Underground && p.Guerrilla, l.ToString()); }
        var e = AiLevels.For(AiLevel.Easy);
        Assert.True(e.SafeHearth);
        Assert.False(e.Underground || e.Guerrilla);
    }

    // One Medium-Hand-against-a-Collective game to the day limit; counts what the Collective did.
    private sealed record Run(int Seed, int BadMoves, int HqFallsAfterMove, int Sabotage, int Incite, int Espionage, int AgentForces, int Raids, int CivilianStrikes);

    private static Run Play(int seed, SimpleAiParams collective, int days)
    {
        // Hearth tip pinned off (Dev lead 2026-10-26, m5-fog-and-hq §N6a): its draws move the seeds the floors below were measured on
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = new GameSettings { Fog = new Praedra.Core.Fog.FogParams { HearthTipPermille = 0, HearthTipSectorPermille = 0 } } });
        var m = new AiMatch(h, Medium, collective);
        int raids = 0, seen = h.Game.Events.Count, badMoves = 0;
        while (h.Game.Day < days && h.Victory.Winner == null)
        {
            m.Step();
            raids += m.Collective.Submitted.Count(o => o.Kind == "Raid");
            // a move the AI ordered onto a world with fewer regiments than SafeHearth asks (counted on the day it leaves); a flight from a lost
            // world is the core's own move (m5-fog-and-hq §F7 Flee, to the nearest Collective system), not the AI's choice
            bool ordered = m.Collective.Submitted.Any(o => o.Kind == "MoveHq");
            for (; seen < h.Game.Events.Count; seen++)
                if (ordered && h.Game.Events[seen] is HqDeparted d && h.Support.RegimentCount(d.Path.Items[^1]) < collective.HearthMinRegiments) badMoves++;
        }
        var ev = h.Game.Events;
        var arrivals = ev.OfType<HqArrived>().Select(a => a.Day).ToList();
        int falls = ev.OfType<HqDestroyed>().Count(d => arrivals.Any(a => d.Day >= a && d.Day - a <= 60));
        int Started(MissionKind k) => ev.OfType<MissionStarted>().Count(s => s.Side == Side.Collective && s.MissionKind == k);
        int agentForces = ev.OfType<MissionStarted>().Count(s => s.Side == Side.Collective && s.Characters.Items.Count == 0 && s.SpecialForces.Items.Count > 0);
        int civ = ev.OfType<Praedra.Core.Defenses.BombardmentStarted>().Count(b => b.Side == Side.Collective && b.Category == Praedra.Core.Defenses.BombardCategory.Civilian);
        return new Run(seed, badMoves, falls, Started(MissionKind.Sabotage), Started(MissionKind.InciteUprising), Started(MissionKind.Espionage), agentForces, raids, civ);
    }

    // SafeHearth: on main f00fbb8 the Medium Collective lost its Hearth just after a move in 9 of 12 traced mirrors (moved onto a world with 0 or
    // 1 regiment, which the -10 arrival drop then turned). Here a move the AI ordered counts as bad when, on the day the Hearth leaves, its
    // destination holds fewer than HearthMinRegiments own regiments. Seeds 1-8 to day 1500 against a Medium Hand, measured on main 9a594a9
    // (bad moves on / off): 0/5, 0/3, 0/5, 0/6, 0/4, 0/4, 0/2, 0/3. Floors: on, none on any seed; off (seeds 1-4 only, to keep the suite
    // short: 19 measured), at least 5 in all (the test still measures the switch).
    [Fact]
    public void SafeHearth_TheHearthNeverMovesOntoAThinlyHeldWorld()
    {
        var on = MultiSeed.Play(MultiSeed.OneToEight, s => Play(s, Old with { SafeHearth = true }, 1500));
        var off = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => Play(s, Old, 1500));
        foreach (var r in on) _out.WriteLine($"seed {r.Seed}: bad moves on {r.BadMoves}; falls within 60 days of a move {r.HqFallsAfterMove}");
        foreach (var r in off) _out.WriteLine($"seed {r.Seed}: bad moves off {r.BadMoves}; falls within 60 days of a move {r.HqFallsAfterMove}");
        Assert.All(on, r => Assert.True(r.BadMoves == 0, $"seed {r.Seed}: {r.BadMoves} moves onto a thinly held world with SafeHearth"));
        Assert.True(off.Sum(r => r.BadMoves) >= 5, $"only {off.Sum(r => r.BadMoves)} bad moves without SafeHearth (the test no longer measures the switch)");
    }

    // QA-54 (QA round 86): on main 47031bb SafeHearth pinned the Hearth on its start world, the one world with enough regiments, so it never
    // left and an assault took it with the Hand blind: Hand Hard against the Medium Collective seed 13 (captured day 284), Medium mirror seeds
    // 14 and 10 (days 247 and 472), each with 0 moves. With Hand warships close and no safe world the Hearth now takes the least-short refuge:
    // measured alive at day 600 in all three, after 1, 2 and 2 moves. Floor: none captured by day 600 without having moved.
    [Fact]
    public void SafeHearth_LeavesForARefugeWhenWarshipsCloseAndNoWorldIsSafe()
    {
        var games = new[] { (Seed: 13, Hand: AiLevel.Hard), (Seed: 14, Hand: AiLevel.Medium), (Seed: 10, Hand: AiLevel.Medium) };
        var res = MultiSeed.Play(games.Select(g => g.Seed).ToArray(), seed =>
        {
            var hand = games.First(g => g.Seed == seed).Hand;
            var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
            var m = new AiMatch(h, AiLevels.For(hand), Medium);
            while (h.Game.Day < 600 && h.Victory.Winner == null) m.Step();
            int moves = h.Game.Events.OfType<HqDeparted>().Count();
            var dead = h.Game.Events.OfType<HqDestroyed>().FirstOrDefault();
            return (Seed: seed, Moves: moves, Dead: dead);
        });
        foreach (var r in res) _out.WriteLine($"seed {r.Seed}: moves {r.Moves}, Hearth {(r.Dead == null ? "alive" : $"lost day {r.Dead.Day} ({r.Dead.Cause})")}");
        Assert.All(res, r => Assert.False(r.Dead != null && r.Moves == 0, $"seed {r.Seed}: the Hearth never moved and fell on day {r.Dead?.Day}"));
    }

    // Underground and Guerrilla in play: seeds 1-4 to day 1000 against a Medium Hand, the Medium Collective with and without them. Measured on
    // main f00fbb8 (Sabotage, Incite, Espionage started; missions by special forces alone; raids ordered; Civilian strikes): seed 1 16, 31, 16, 6, 32, 32;
    // seed 2 82, 19, 28, 25, 77, 66; seed 3 34, 5, 21, 8, 14, 14; seed 4 16, 58, 10, 11, 42, 42. Off: no Sabotage, Espionage, special-force
    // mission, raid or Civilian strike on any seed (Incite 1-6: the old border subversion). Floors: on, at least 5 of each on every seed; off (to
    // day 300 only, to keep the suite short: the switches gate every one of these orders), none.
    [Fact]
    public void Underground_AndGuerrilla_SpyAndSabotageAndRaid_OnlyWhenOn()
    {
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (On: Play(s, Medium, 1000), Off: Play(s, Medium with { Underground = false, Guerrilla = false }, 300)));
        foreach (var r in res) _out.WriteLine($"seed {r.On.Seed}: on {r.On}; off {r.Off}");
        foreach (var (on, off) in res)
        {
            Assert.True(on.Sabotage >= 5 && on.Espionage >= 5 && on.AgentForces >= 5 && on.Raids >= 5 && on.CivilianStrikes >= 5, $"seed {on.Seed}: on {on}");
            Assert.True(off.Sabotage == 0 && off.Espionage == 0 && off.AgentForces == 0 && off.Raids == 0 && off.CivilianStrikes == 0, $"seed {off.Seed}: off {off}");
        }
    }
}
