using Praedra.Core.AI;
using Praedra.Core.Assault;
using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Economy;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// The simple AI as an opponent for a human: one side is a scripted passive player (it gives no orders), the AI plays the other side on the
// new-game MedA start. The passive side stands for a human who has not started yet.
// Rules checked: the AI acts only through orders (core/AI); m2-maintenance §Overdraft and autoscrap (the AI scraps its own least useful items).
public class SimpleAiOpponentTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiOpponentTests(ITestOutputHelper o) { _out = o; }

    private static (AiMatch Match, GameHost Host) NewMatch(int seed, Side? passive)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = Praedra.Core.Tests.Setup.DealSettings.FileOldVictory });
        var m = new AiMatch(host, passive: passive);
        m.Empire.KnownEnemyHq = host.HqSystem; // no fog yet: the Empire knows the Rebel HQ
        return (m, host);
    }

    private static int Captures(GameHost h, Side to) => h.Game.Events.OfType<SystemCaptured>().Count(e => e.To == to);

    private static (int Seed, Side? Winner, int Day, int Captures365, int Captures, int Sent, int Rejected) PlayPassive(int seed, Side ai, int maxDays)
    {
        var (m, host) = NewMatch(seed, ai == Side.Empire ? Side.Alliance : Side.Empire);
        int at365 = -1;
        while (host.Game.Day < maxDays && host.Victory.Winner == null)
        {
            m.Step();
            if (host.Game.Day == 365) at365 = Captures(host, ai);
        }
        if (at365 < 0) at365 = Captures(host, ai); // the game ended before day 365
        return (seed, host.Victory.Winner, host.Game.Day, at365, Captures(host, ai), m.TotalSent, m.TotalRejected);
    }

    private void Report(Side ai, IEnumerable<(int Seed, Side? Winner, int Day, int Captures365, int Captures, int Sent, int Rejected)> games)
    {
        foreach (var g in games)
        {
            _out.WriteLine($"seed {g.Seed}: {ai} AI vs passive: winner {g.Winner} on day {g.Day}; captures by day 365 {g.Captures365}, total {g.Captures}; orders {g.Sent}, rejected {g.Rejected}");
            Assert.True(g.Rejected * 20 <= g.Sent, $"seed {g.Seed}: {g.Rejected} of {g.Sent} orders rejected");
        }
    }

    // Over seeds 1 to 8 (MultiSeed), the day the Empire AI takes the Rebel HQ (no fog: it knows where the HQ is):
    //   8a378a8 (pre-B2e): 271, 262, 256, 271, 271, 235, 235, 235;  9ec544f (B2e): 262, 271, 256, 256, 262, 271, 262, 262
    // Floor: the Empire wins by day 400 on at least 7 of 8. Replaces (AI trainer, 2026-10-18, the Dev lead's ask) the seed-1 limit of 700.
    [Fact]
    public void EmpireAi_AgainstPassiveAlliance_WinsByTakingTheRebelHq()
    {
        var games = MultiSeed.Play(MultiSeed.OneToEight, s => PlayPassive(s, Side.Empire, 400));
        Report(Side.Empire, games);
        Assert.True(games.Count(g => g.Winner == Side.Empire) >= 7, $"the Empire won by day 400 on only {games.Count(g => g.Winner == Side.Empire)} of 8 seeds");
    }

    // Over seeds 1 to 8 (MultiSeed), the day the Alliance AI takes Coruscant, and its captures by day 365:
    //   8a378a8 (pre-B2e): 1323, 1098, 1320, 1101, 1749, 1503, 1050, 1422;  captures 16 to 20
    //   9ec544f (B2e):     1191, 1056, 1530, 1320,  528,  972,  639,  966;  captures 16 to 18
    // Floor: the Alliance wins by day 1600 on at least 6 of 8 (7 and 8 measured), with at least 12 captures by day 365 on at least 7 of 8
    // (8 and 8 measured). Replaces (AI trainer, 2026-10-18, the Dev lead's ask) the seed-1 limit, re-pinned from 850 to 1350 in four
    // steps (v5, B2b, B2d) as each batch sent the one game another way. Earlier measurements on seed 1: day 783 without characters and
    // missions, 813 with them (SimpleAiParams.UseCharactersAndMissions), 795 since the AI stops hunting fleets that only retreat
    // (SimpleAiParams.RunnerRetreats, SimpleAiChaseTests), all on the 360-system map.
    [Fact]
    public void AllianceAi_AgainstPassiveEmpire_MakesSteadyProgress_AndWinsByTakingCoruscant()
    {
        var games = MultiSeed.Play(MultiSeed.OneToEight, s => PlayPassive(s, Side.Alliance, 1600));
        Report(Side.Alliance, games);
        Assert.True(games.Count(g => g.Winner == Side.Alliance) >= 6, $"the Alliance won by day 1600 on only {games.Count(g => g.Winner == Side.Alliance)} of 8 seeds");
        Assert.True(games.Count(g => g.Captures365 >= 12) >= 7, $"12 captures by day 365 on only {games.Count(g => g.Captures365 >= 12)} of 8 seeds");
    }

    [Fact]
    public void PassiveSideGivesNoOrders()
    {
        var (m, host) = NewMatch(2, Side.Empire);
        m.RunTo(120);
        Assert.DoesNotContain(m.Sent.Keys, k => k.StartsWith("Empire ", StringComparison.Ordinal));
        Assert.Contains(m.Sent.Keys, k => k.StartsWith("Alliance ", StringComparison.Ordinal));
    }

    // Over seeds 1 to 8 (MultiSeed), battles by day 700 (the Empire knows the Rebel HQ, so most games end by day 250 to 560):
    //   8a378a8 (pre-B2e): 1, 5, 1, 4, 2, 3, 4, 3 (23);  9ec544f (B2e): 8, 4, 4, 4, 2, 2, 3, 3 (30)
    // Floor: at least 16 battles in all, and at least 2 on at least 5 of 8 seeds (6 and 8 measured). On every seed, at most 60 side-days
    // over the upkeep capacity (0 measured everywhere: the AI keeps clear of the autoscrap by scrapping its own least useful items) and at
    // most 1 order in 20 rejected. Replaces (AI trainer, 2026-10-18, the Dev lead's ask) the one-seed floor of 3 battles (seed 5, then 1,
    // then 2 as B2b and B2d moved the games).
    [Fact]
    public void AiVsAi_FleetsFight_AndNeitherSideGoesOverCapacityForLong()
    {
        var games = MultiSeed.Play(MultiSeed.OneToEight, seed =>
        {
            var (m, host) = NewMatch(seed, null);
            int overDays = 0;
            while (host.Game.Day < 700 && host.Victory.Winner == null)
            {
                m.Step();
                foreach (var s in new[] { Side.Empire, Side.Alliance }) if (host.Economy.Free(s) < 0) overDays++;
            }
            var ev = host.Game.Events;
            int autoscrap = ev.OfType<ObjectScrapped>().Count(e => e.Reason == "Autoscrap");
            return (Seed: seed, host.Game.Day, Battles: ev.OfType<BattleEnded>().Count(), Autoscrap: autoscrap, OverDays: overDays, m.TotalSent, m.TotalRejected);
        });
        foreach (var g in games) _out.WriteLine($"seed {g.Seed}: day {g.Day}, battles {g.Battles}, autoscraps {g.Autoscrap}, days over capacity {g.OverDays}, rejected {g.TotalRejected} of {g.TotalSent}");
        Assert.True(games.Sum(g => g.Battles) >= 16, $"only {games.Sum(g => g.Battles)} battles over 8 seeds");
        Assert.True(games.Count(g => g.Battles >= 2) >= 5, $"2 battles or more on only {games.Count(g => g.Battles >= 2)} of 8 seeds");
        foreach (var g in games)
        {
            Assert.True(g.OverDays <= 60, $"seed {g.Seed}: {g.OverDays} side-days over the upkeep capacity");
            Assert.True(g.TotalRejected * 20 <= g.TotalSent, $"seed {g.Seed}: {g.TotalRejected} of {g.TotalSent} orders rejected");
        }
    }

    [Fact]
    public void UpkeepCrisis_TheAiScrapsItsOwnLeastUsefulItems_BeforeTheAutoscrapPicks()
    {
        // Captures can take mines and refineries away. Here the test scraps Empire mines (a player order) until the load is above the capacity.
        var (m, host) = NewMatch(3, Side.Alliance);
        m.RunTo(60);
        var eco = host.Economy;
        var mines = eco.Objects.Where(o => o.Completed && o.Class == "Mine" && eco.OwnerOf(o) == Side.Empire).OrderBy(o => o.Id).ToList();
        int n = Math.Min(mines.Count, eco.Free(Side.Empire) / 50 + 2);
        for (int i = 0; i < n; i++) { var id = mines[i].Id; host.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Empire, id)); }
        // the Alliance is passive, so the Empire AI is the only one planning; two steps: the scraps land, then the AI sees the overdraft
        m.Step();
        Assert.True(eco.Free(Side.Empire) < 0, "the test did not create an overdraft");
        int sentScraps = 0;
        for (int d = 0; d < 6; d++)
        {
            m.Step();
            sentScraps += m.Empire.Submitted.Count(o => o.Kind == "Scrap");
            if (eco.Free(Side.Empire) >= 0) break;
        }
        Assert.True(sentScraps >= 1, "the AI sent no ScrapObject order");
        Assert.True(eco.Free(Side.Empire) >= 0, $"still over capacity: free {eco.Free(Side.Empire)}");
        Assert.Equal(0, host.Game.Events.OfType<ObjectScrapped>().Count(e => e.Reason == "Autoscrap" && e.Side == Side.Empire));
        // never a yard, mine or refinery
        foreach (var e in host.Game.Events.OfType<ObjectScrapped>().Where(e => e.Side == Side.Empire && e.Reason == "Order" && e.Day > 60))
            Assert.DoesNotContain(e.Class, new[] { "Orbital Shipyard", "Training Facility", "Construction Yard", "Refinery" });
    }

    [Fact]
    public void SameSeed_SameHash_WithAPassiveSide()
    {
        var (a, ha) = NewMatch(4, Side.Alliance); a.RunTo(250);
        var (b, hb) = NewMatch(4, Side.Alliance); b.RunTo(250);
        Assert.Equal(ha.StateHash(), hb.StateHash());
    }

    [Fact]
    public void LoadedMedAGameContinuesLikeTheUninterruptedOne_WithFreshAis()
    {
        var (m, host) = NewMatch(6, null);
        m.RunTo(220);
        // The save with its AI memory line (QA-1): the fresh AIs read their look-backs from that memory. The plain core save (host.Save()) has no
        // AI memory by design (m1-clock-and-orders DEFAULT: events are not saved), so with it the fresh AIs plan from an empty memory and the
        // games may part (seed 6 does on day 262 with the full Classic mission tables).
        var save = host.SaveWithSettings();
        m.RunTo(300);
        var host2 = GameHost.Load(save, Praedra.Core.Tests.Setup.DealSettings.FileOldVictory); // the same settings as the game (settings are not in the save)
        var m2 = new AiMatch(host2); m2.Empire.KnownEnemyHq = host2.HqSystem;
        m2.RunTo(300);
        Assert.Equal(host.StateHash(), host2.StateHash());
    }

    [Fact]
    public void TheAiDoesNotDrawFromTheGameGenerator_OnTheNewGameStart()
    {
        var (m, host) = NewMatch(3, null);
        long before = host.Game.Rng.Draws;
        for (int d = 0; d < 30; d++) { m.Empire.PlanDay(); m.Alliance.PlanDay(); }
        Assert.Equal(before, host.Game.Rng.Draws);
    }

    // m5-research §Defaults (the AI uses the same IsResearched through the construction sheet): on the MedA start with the research gate on,
    // the AI never orders an item its side has not researched, and research moves (the yards' tick unlocks rungs).
    [Fact]
    public void AiVsAi_NeverOrdersUnresearchedItems()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 2, Preset = StartPreset.MedA, Settings = Praedra.Core.Tests.Setup.DealSettings.FileOldVictory });
        Assert.True(host.Construction.RespectResearchOrder);
        var m = new AiMatch(host);
        while (host.Game.Day < 500 && host.Victory.Winner == null) m.Step();
        Assert.DoesNotContain(host.Game.Events, e => e is OrderRejected r && r.Reason == RejectReason.NotResearched);
        Assert.Contains(host.Game.Events, e => e is Praedra.Core.Research.ResearchLevelReached);
        _out.WriteLine($"levels after {host.Game.Day} days: " + string.Join("; ", new[] { Side.Alliance, Side.Empire }.Select(s =>
            $"{s} " + string.Join("/", Praedra.Core.Research.ResearchModule.Kinds.Select(k => host.Research.Level(s, k))))));
    }
}
