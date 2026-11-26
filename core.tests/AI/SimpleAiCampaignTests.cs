using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// rule: ai tuning, SimpleAiParams.Campaign (AI trainer 2026-11-26; Paul 2026-11-26, "VERY important": the AI builds plans to fight, invade and
// harass that make sense; the Collective weakens a sector's defences and threats by sabotage before it invades, and the preparation is the
// player's hint of what is coming; targets vary from game to game, Paul 2026-11-19).
public class SimpleAiCampaignTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiCampaignTests(ITestOutputHelper o) { _out = o; }

    private static SimpleAiParams Collective(bool campaign) => AiLevels.For(AiLevel.Medium) with { Campaign = campaign };

    [Fact]
    public void CampaignsAreOffByDefault()
    {
        Assert.False(new SimpleAiParams().Campaign);
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        var m = new AiMatch(host, Collective(false), Collective(false));
        m.RunTo(30);
        Assert.Null(m.Collective.CampaignNow);
        Assert.Null(host.AiMemoryFor(Side.Collective, true).Campaign);
    }

    // The same plan made the AI Hand lose 31 of 32 Medium mirrors: the Hand never campaigns, even with the switch on (SimpleAiParams.Campaign).
    [Fact]
    public void TheHandNeverCampaigns()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        var m = new AiMatch(host, Collective(true), Collective(true));
        m.RunTo(30);
        Assert.Null(m.Hand.CampaignNow);
        Assert.NotNull(m.Collective.CampaignNow);
    }

    // Seed 1 MedE, Medium mirror, the Collective with campaigns: on its first day it picks an enemy sector, prepares it (its subversion goes
    // there), and strikes by day CampaignPrepareDays + 1 at the latest. Measured 2026-11-26 (strike 800, agent 500, StrandedAgentsHome on): Ommata
    // Sector, 2 of 5 Collective Sabotage, Incite and Espionage missions there by day 122. Without StrandedAgentsHome, 2 of 13: an agent who
    // escaped at Charter started 11 Espionage missions from there, each sensed and foiled before it left.
    [Fact]
    public void ACampaignPicksAnEnemySectorPreparesThenStrikes()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        var p = Collective(true);
        var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), p);
        m.RunTo(2);
        var c = m.Collective.CampaignNow;
        Assert.NotNull(c);
        Assert.False(c!.Value.Striking);
        Assert.Equal(1, c.Value.Number);
        var g = host.Data.Galaxy;
        Assert.Contains(g.Systems, s => s.Sector == c.Value.Sector && host.Fleets.OwnerOf(s.Name) == Side.Hand);
        int inSector = 0, all = 0;
        while (host.Game.Day <= p.CampaignPrepareDays + 1)
        {
            int seen = host.Game.Events.Count;
            m.Step();
            for (int i = seen; i < host.Game.Events.Count; i++)
                if (host.Game.Events[i] is MissionStarted ms && ms.Side == Side.Collective && ms.MissionKind is MissionKind.Sabotage or MissionKind.InciteUprising or MissionKind.Espionage)
                {
                    all++;
                    if (g.TryFindSystem(ms.Target, out int ti) && g.Systems[ti].Sector == c.Value.Sector) inSector++;
                }
        }
        _out.WriteLine($"campaign {c.Value.Sector}: {inSector} of {all} Collective sabotage, incite and spy missions there by day {host.Game.Day}");
        Assert.True(inSector * 3 >= all && inSector > 0, $"{inSector} of {all} missions in the campaign's sector");
        var now = m.Collective.CampaignNow;
        Assert.True(now is { Striking: true } || now?.Number > 1, "no strike by the end of the preparation");
    }

    // Paul 2026-11-19: not predictable. The first campaign's sector over seeds 1-8 (MedE, Medium mirror): at least three different sectors.
    [Fact]
    public void TheFirstCampaignSectorChangesFromGameToGame()
    {
        var sectors = MultiSeed.Play(MultiSeed.OneToEight, seed =>
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), Collective(true));
            m.RunTo(2);
            return m.Collective.CampaignNow?.Sector;
        });
        _out.WriteLine(string.Join(", ", sectors));
        Assert.All(sectors, s => Assert.NotNull(s));
        Assert.True(sectors.Distinct().Count() >= 3, $"{sectors.Distinct().Count()} different first sectors in 8 games");
    }

    // m1-clock-and-orders K12 (with AI): the campaign rides in the AI memory of the save, so a loaded game goes on with the same campaign.
    [Fact]
    public void TheCampaignIsSavedWithTheGame()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), Collective(true));
        m.RunTo(150);
        var before = m.Collective.CampaignNow;
        Assert.NotNull(before);
        var loaded = GameHost.Load(host.SaveWithSettings());
        var c = loaded.AiMemoryFor(Side.Collective, true).Campaign;
        Assert.NotNull(c);
        Assert.Equal(before!.Value.Sector, c!.Sector);
        Assert.Equal(before.Value.Number, c.Number);
        Assert.Equal(before.Value.Striking, c.Strike > 0);
        var b = new AiMatch(loaded, AiLevels.For(AiLevel.Medium), Collective(true));
        var a = new AiMatch(host, AiLevels.For(AiLevel.Medium), Collective(true));
        for (int i = 0; i < 30; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }
}
