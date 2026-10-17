using Rebellion.Core.AI;
using Rebellion.Core.Assault;
using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Economy;
using Rebellion.Core.Setup;
using Rebellion.Core.Tests.Setup;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.AI;

// The simple AI as an opponent for a human: one side is a scripted passive player (it gives no orders), the AI plays the other side on the
// new-game MedA start. The passive side stands for a human who has not started yet.
// Rules checked: the AI acts only through orders (core/AI); m2-maintenance §Overdraft and autoscrap (the AI scraps its own least useful items).
public class SimpleAiOpponentTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiOpponentTests(ITestOutputHelper o) { _out = o; }

    private static (AiMatch Match, GameHost Host) NewMatch(int seed, Side? passive)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = Rebellion.Core.Tests.Setup.DealSettings.FileOldVictory });
        var m = new AiMatch(host, passive: passive);
        m.Empire.KnownEnemyHq = host.HqSystem; // no fog yet: the Empire knows the Rebel HQ
        return (m, host);
    }

    private static int Captures(GameHost h, Side to) => h.Game.Events.OfType<SystemCaptured>().Count(e => e.To == to);

    private (int Day, int Captures365, int Captures) PlayPassive(int seed, Side ai, int maxDays)
    {
        var (m, host) = NewMatch(seed, ai == Side.Empire ? Side.Alliance : Side.Empire);
        int at365 = -1;
        while (host.Game.Day < maxDays && host.Victory.Winner == null)
        {
            m.Step();
            if (host.Game.Day == 365) at365 = Captures(host, ai);
        }
        if (at365 < 0) at365 = Captures(host, ai); // the game ended before day 365
        int total = Captures(host, ai);
        _out.WriteLine($"{ai} AI vs passive: winner {host.Victory.Winner} on day {host.Game.Day}; captures by day 365 {at365}, total {total}; reason {host.Victory.Reason}; orders {m.TotalSent}, rejected {m.TotalRejected}");
        Assert.True(m.TotalRejected * 20 <= m.TotalSent, $"{m.TotalRejected} of {m.TotalSent} orders rejected");
        Assert.Equal(ai, host.Victory.Winner);
        return (host.Game.Day, at365, total);
    }

    [Fact]
    public void EmpireAi_AgainstPassiveAlliance_WinsByTakingTheRebelHq()
    {
        var (day, c365, total) = PlayPassive(1, Side.Empire, 1000);
        Assert.True(day <= 700, $"won only on day {day}");
        Assert.True(total >= 1);
    }

    [Fact]
    public void AllianceAi_AgainstPassiveEmpire_MakesSteadyProgress_AndWinsByTakingCoruscant()
    {
        var (day, c365, total) = PlayPassive(1, Side.Alliance, 1300);
        Assert.True(c365 >= 8, $"only {c365} captures by day 365");
        // measured: day 783 without characters and missions, 813 with them (SimpleAiParams.UseCharactersAndMissions, DEFAULT on: the game
        // takes another course; about 50 days of it a strike pile chased two Imperial ships that retreat between Basilisk and Kirrek), 795 since
        // the AI stops hunting fleets that only retreat (SimpleAiParams.RunnerRetreats, SimpleAiChaseTests)
        // // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns restoring the old target): measured day 1062 on v5 (16 captures by day 365), limit 850 -> 1200
        Assert.True(day <= 1200, $"won only on day {day}");
    }

    [Fact]
    public void PassiveSideGivesNoOrders()
    {
        var (m, host) = NewMatch(2, Side.Empire);
        m.RunTo(120);
        Assert.DoesNotContain(m.Sent.Keys, k => k.StartsWith("Empire ", StringComparison.Ordinal));
        Assert.Contains(m.Sent.Keys, k => k.StartsWith("Alliance ", StringComparison.Ordinal));
    }

    [Fact]
    public void AiVsAi_FleetsFight_AndNeitherSideGoesOverCapacityForLong()
    {
        // seed 5 is the game where the fleets meet most; the AI keeps clear of the autoscrap by scrapping its own least useful items
        var (m, host) = NewMatch(5, null);
        int overDays = 0;
        while (host.Game.Day < 700 && host.Victory.Winner == null)
        {
            m.Step();
            foreach (var s in new[] { Side.Empire, Side.Alliance }) if (host.Economy.Free(s) < 0) overDays++;
        }
        var ev = host.Game.Events;
        int battles = ev.OfType<BattleEnded>().Count();
        int autoscrap = ev.OfType<ObjectScrapped>().Count(e => e.Reason == "Autoscrap");
        _out.WriteLine($"day {host.Game.Day}, battles {battles}, autoscraps {autoscrap}, days over capacity {overDays}, rejected {m.TotalRejected} of {m.TotalSent}");
        Assert.True(battles >= 3, $"only {battles} battles");
        Assert.True(overDays <= 60, $"{overDays} side-days over the upkeep capacity");
        Assert.True(m.TotalRejected * 20 <= m.TotalSent);
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
        // Seed 4 (was 6). A fresh AI rebuilds its memories (recent battles, captures, bombardments, foiled spies) from the game's events, which a
        // save does not keep, so this property holds only on games where those memories do not matter between days 220 and 300: on main 6 of
        // seeds 1-8 already diverged; with the full 1998 mission tables (B1) seed 6 diverges at day 262 and seeds 3, 4, 5 hold.
        var (m, host) = NewMatch(4, null);
        m.RunTo(220);
        var save = host.Save();
        m.RunTo(300);
        var host2 = GameHost.Load(save, Rebellion.Core.Tests.Setup.DealSettings.FileOldVictory); // the same settings as the game (settings are not in the save)
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
        var host = NewGame.Create(new NewGameOptions { Seed = 2, Preset = StartPreset.MedA, Settings = Rebellion.Core.Tests.Setup.DealSettings.FileOldVictory });
        Assert.True(host.Construction.RespectResearchOrder);
        var m = new AiMatch(host);
        while (host.Game.Day < 500 && host.Victory.Winner == null) m.Step();
        Assert.DoesNotContain(host.Game.Events, e => e is OrderRejected r && r.Reason == RejectReason.NotResearched);
        Assert.Contains(host.Game.Events, e => e is Rebellion.Core.Research.ResearchLevelReached);
        _out.WriteLine($"levels after {host.Game.Day} days: " + string.Join("; ", new[] { Side.Alliance, Side.Empire }.Select(s =>
            $"{s} " + string.Join("/", Rebellion.Core.Research.ResearchModule.Kinds.Select(k => host.Research.Level(s, k))))));
    }
}
