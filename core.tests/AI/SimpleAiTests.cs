using Rebellion.Core.AI;
using Rebellion.Core.Assault;
using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Setup;
using Rebellion.Core.Tests.Setup;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.AI;

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
        // Stateless across days: a save loaded mid-game with fresh AIs plays on exactly as the original (campaign hook re-registered by TestCampaign.Load).
        var c = TestCampaign.New(5); var m = new AiMatch(c.Host);
        m.RunTo(200);
        var save = c.Host.Save();
        m.RunTo(260);
        var c2 = TestCampaign.Load(save); var m2 = new AiMatch(c2.Host);
        m2.RunTo(260);
        Assert.Equal(c.Host.StateHash(), c2.Host.StateHash());
    }
}
