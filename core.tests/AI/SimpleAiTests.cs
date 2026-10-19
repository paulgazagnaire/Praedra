using Praedra.Core.AI;
using Praedra.Core.Assault;
using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// AI vs AI on the test campaign start (core/Setup/TestCampaign.cs). The campaign's scripted orders are NOT used (the AIs play instead);
// its one scripted capture of Denon on day 400 still fires (a hook inside TestCampaign), so captures are counted by cause.
public class SimpleAiTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiTests(ITestOutputHelper o) { _out = o; }

    private const int Days = 730;

    private static AiMatch Play(ulong seed, int days, bool check)
    {
        var c = TestCampaign.New(seed);
        var m = new AiMatch(c.Host);
        for (int i = 0; i < days; i++)
        {
            m.Step();
            if (check) CampaignInvariants.Check(c.Host, $"day {c.Game.Day}", fixedRegions: false);
        }
        return m;
    }

    private static readonly Lazy<AiMatch> Run1 = new(() => Play(1, Days, check: true));

    [Fact]
    public void AiVsAi_730Days_InvariantsHold_BothSidesBuild_AnAssaultHappens()
    {
        var m = Run1.Value;
        var ev = m.Host.Game.Events;
        int built(Side s) => ev.OfType<UnitCompleted>().Count(e => e.Side == s);
        int assaults = ev.OfType<AssaultResolved>().Count();
        int won = ev.OfType<AssaultResolved>().Count(e => e.Result == "Won");
        int capt = ev.OfType<SystemCaptured>().Count(e => e.Cause == CaptureCause.Assault);
        int battles = ev.OfType<BattleEnded>().Count();
        _out.WriteLine($"built E {built(Side.Empire)} A {built(Side.Alliance)}; assaults {assaults} won {won}; captures {capt}; battles {battles}; orders {m.TotalSent} rejected {m.TotalRejected}");
        foreach (var kv in m.Rejected) _out.WriteLine($"  rejected {kv.Key}: {kv.Value}");
        foreach (var kv in m.Sent) _out.WriteLine($"  sent {kv.Key}: {kv.Value}");
        Assert.True(built(Side.Empire) > 10);
        Assert.True(built(Side.Alliance) > 10);
        Assert.True(assaults >= 1, "no assault happened");
    }

    [Fact]
    public void AiOrdersAreMostlyAccepted()
    {
        var m = Run1.Value;
        Assert.True(m.TotalSent > 100);
        Assert.True(m.TotalRejected * 20 <= m.TotalSent, $"{m.TotalRejected} of {m.TotalSent} orders rejected");
    }

    [Fact]
    public void SameSeed_SameHash()
    {
        var a = Play(7, 300, check: false); var b = Play(7, 300, check: false);
        Assert.Equal(a.Host.StateHash(), b.Host.StateHash());
    }

    [Fact]
    public void AiDoesNotTouchTheGameRandomStream()
    {
        // Planning without stepping draws nothing from the game generator.
        var c = TestCampaign.New(3);
        long before = c.Game.Rng.Draws;
        var ai = new SimpleAi(c.Host, Side.Empire);
        for (int d = 0; d < 20; d++) ai.PlanDay();
        Assert.Equal(before, c.Game.Rng.Draws);
    }

    [Fact]
    public void LoadedGameContinuesLikeTheUninterruptedOne()
    {
        // A save loaded mid-game with fresh AIs plays on exactly as before (campaign hook re-registered by TestCampaign.Load). The AI's only
        // memory across days is the game's AiMemory (QA-1), carried by SaveWithSettings, not by the plain Save: with the New battle (DEFAULT since
        // 2026-10-18) the plain save diverged on day 202 (the memory of a retreat changed a move order); under Classic it happened not to matter.
        var c = TestCampaign.New(5); var m = new AiMatch(c.Host);
        m.RunTo(200);
        var save = c.Host.SaveWithSettings();
        m.RunTo(260);
        var c2 = TestCampaign.Load(save); var m2 = new AiMatch(c2.Host);
        m2.RunTo(260);
        Assert.Equal(c.Host.StateHash(), c2.Host.StateHash());
    }

    [Fact]
    public void AiVsAi_OnTheNewGameStart_730Days_NoCrash_OrdersMostlyAccepted()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        var m = new AiMatch(host);
        m.Empire.KnownEnemyHq = host.HqSystem;
        for (int i = 0; i < Days && host.Victory.Winner == null; i++)
        {
            m.Step();
            if (host.Game.Day % 10 == 0) CampaignInvariants.Check(host, $"day {host.Game.Day}", fixedRegions: false);
        }
        var ev = host.Game.Events;
        _out.WriteLine($"MedA: day {host.Game.Day}, winner {host.Victory.Winner}, assaults {ev.OfType<AssaultResolved>().Count()}, captures {ev.OfType<SystemCaptured>().Count()}, orders {m.TotalSent}, rejected {m.TotalRejected}");
        Assert.True(m.TotalSent > 100);
        Assert.True(m.TotalRejected * 20 <= m.TotalSent);
    }
}
