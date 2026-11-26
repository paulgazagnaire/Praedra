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
    // SimpleAiParams.CampaignsAtOnce (AI trainer 2026-11-26; Paul 2026-11-26: the Collective, scattered on the outer rim, attacks from several
    // angles at once, more of them with difficulty): the Hard Collective runs two campaigns by day 300, in sectors at least CampaignApartHops
    // lanes apart, on each of seeds 1-8 (MedE, Medium Hand); Medium runs one. Measured 2026-11-26: both from day 1 on seeds 1, 4, 5 and 6, from
    // days 19-37 on the others (on day 1 the fog shows only the sectors near Charter); 2 to 5 lanes apart. Apart 4 gave a second campaign on day
    // 1 in none of the 8 games, apart 3 in one.
    [Fact]
    public void TheHardCollectiveRunsTwoCampaignsFarApart()
    {
        var hard = AiLevels.For(AiLevel.Hard);
        Assert.Equal(2, hard.CampaignsAtOnce);
        Assert.Equal(1, AiLevels.For(AiLevel.Medium).CampaignsAtOnce);
        var res = MultiSeed.Play(MultiSeed.OneToEight, seed =>
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), hard);
            var now = m.Collective.CampaignsNow;
            int day = 0;
            while (host.Game.Day < 300 && now.Count < 2) { m.Step(); now = m.Collective.CampaignsNow; day = host.Game.Day; }
            _out.WriteLine($"seed {seed}: two campaigns from day {day}");
            var g = host.Data.Galaxy;
            int lanes = int.MaxValue;
            if (now.Count == 2)
                foreach (var a in g.Systems.Where(x => x.Sector == now[0].Sector))
                {
                    var dist = new Dictionary<int, int> { [a.Index] = 0 }; var q = new Queue<int>(); q.Enqueue(a.Index);
                    while (q.Count > 0) { int c = q.Dequeue(); foreach (var n in g.NearOf(c)) if (!dist.ContainsKey(n)) { dist[n] = dist[c] + 1; q.Enqueue(n); } }
                    foreach (var b in g.Systems.Where(x => x.Sector == now[1].Sector)) if (dist.TryGetValue(b.Index, out int d)) lanes = Math.Min(lanes, d);
                }
            return (Seed: seed, Sectors: now.Select(c => c.Sector).ToList(), Lanes: lanes);
        });
        foreach (var r in res) _out.WriteLine($"seed {r.Seed}: {string.Join(" + ", r.Sectors)}, {r.Lanes} lanes apart");
        Assert.All(res, r => Assert.True(r.Sectors.Count == 2 && r.Sectors[0] != r.Sectors[1] && r.Lanes >= hard.CampaignApartHops,
            $"seed {r.Seed}: {string.Join(" + ", r.Sectors)}, {r.Lanes} lanes apart"));
    }

    // m1-clock-and-orders K12 (with AI): the other campaigns ride in the save too.
    // QA-125: a Hard save (two campaigns) played on at Medium (one slot) drops the extra campaign and goes on; the trim once left an empty
    // list and PlanCampaign threw "Sequence contains no elements" the first time the slot needed a new sector (MedA seeds 7, 23, 61, 88).
    [Theory]
    [InlineData(7)]
    [InlineData(23)]
    public void AHardSavePlayedAtMediumDropsTheExtraCampaignAndGoesOn(int seed)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
        new AiMatch(host, AiLevels.For(AiLevel.Hard), AiLevels.For(AiLevel.Hard)).RunTo(300);
        var loaded = GameHost.Load(host.SaveWithSettings());
        var m = new AiMatch(loaded, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium));
        int firstNumber = m.Collective.CampaignsNow.Count > 0 ? m.Collective.CampaignsNow[0].Number : 0;
        int newSectors = 0; string? last = null;
        while (loaded.Game.Day < 300 + AiLevels.For(AiLevel.Medium).CampaignMaxDays + 60)
        {
            m.Step();
            var now = m.Collective.CampaignsNow;
            Assert.True(now.Count <= 1, $"day {loaded.Game.Day}: {now.Count} campaigns at Medium");
            if (now.Count == 1 && now[0].Sector != last) { if (last != null) newSectors++; last = now[0].Sector; }
        }
        _out.WriteLine($"seed {seed}: first campaign {firstNumber}, {newSectors} new sectors after the load, day {loaded.Game.Day}");
        Assert.True(newSectors >= 1, "no campaign ended after the load: the case this guards never arose");
    }

    [Fact]
    public void BothHardCampaignsAreSavedWithTheGame()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Hard));
        m.RunTo(150);
        var before = m.Collective.CampaignsNow;
        Assert.Equal(2, before.Count);
        var loaded = GameHost.Load(host.SaveWithSettings());
        var mem = loaded.AiMemoryFor(Side.Collective, true);
        var saved = new[] { mem.Campaign! }.Concat(mem.MoreCampaigns ?? new()).Where(c => c.Sector.Length > 0).Select(c => (c.Sector, c.Strike > 0, c.Number)).ToList();
        Assert.Equal(before, saved);
        var b = new AiMatch(loaded, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Hard));
        var a = new AiMatch(host, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Hard));
        for (int i = 0; i < 30; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }
}
