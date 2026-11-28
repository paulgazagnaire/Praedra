using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Fog;
using Praedra.Core.Map;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Economy;
using static Praedra.Core.Tests.Fog.FogWorld;

namespace Praedra.Core.Tests.Fog;

// m5-fog-and-hq §N6d (New, Paul 2026-11-28), tests FE9 and FE10: an espionage success sees the leaders of the other side travelling alone
// whose trip ends at a copied system, with the day each will stand there.
public class InboundLeaderTests
{
    // Map E (as EspionageTests): the Hand's E0, then a chain of Collective systems A01..A12; the Hearth at A01.
    private static readonly Lazy<GameData> MapE = new(() =>
    {
        var names = Enumerable.Range(1, 12).Select(i => $"A{i:00}").ToList();
        var planets = "name,sector,group,map_x,map_y\nE0,S0,farreach,0,0\n" +
                      string.Join("\n", names.Select((n, i) => $"{n},{(i < 6 ? "S1" : "S2")},farreach,{(i + 1) * 80},0")) + "\n";
        var chain = new[] { "E0" }.Concat(names).ToList();
        var lanes = "from,to,kind,routes\n" + string.Join("\n", chain.Zip(chain.Skip(1)).Select(p => $"{p.First},{p.Second},generated,")) + "\n";
        var g = GalaxyLoader.Load(planets, lanes, TestData.Tiers);
        var slots = "name,sector,group,energy,raw,populated\n" + string.Join("\n", g.Systems.Select(s => $"{s.Name},{s.Sector},{s.Group},5,3,1"));
        return GameData.Default with { Galaxy = g, Slots = SlotValues.Parse(slots, g) };
    });

    private const int L1 = 833, L2 = 834, L3 = 576, L4 = 577; // Collective: Forty-One, Foreman, Mae Abara, Ledger (data/classic/characters.csv)

    private static GameHost Host(bool on, bool groups = true, int groupPercent = 100)
    {
        var h = GameHost.New(1, new GameSettings { Fog = new FogParams { EspionageSeesInbound = on, InboundSeesMissionGroups = groups,
            InboundMissionGroupPercent = groupPercent } }, MapE.Value);
        h.Fleets.SetOwner("E0", Side.Hand);
        for (int i = 1; i <= 12; i++) h.Fleets.SetOwner($"A{i:00}", Side.Collective);
        h.Fog.PlaceHq("A01");
        h.Fog.SetupNewGame(new[] { "A01", "A02", "A03" });
        foreach (var (id, at) in new[] { (L1, "A07"), (L2, "A07"), (L3, "A06"), (L4, "A08") })
        {
            h.Characters.Create(h.Characters.Data.Get(id), new StubRng());
            h.Characters.Place(id, at);
        }
        h.Characters.PlaceInFleet(L3, h.Fleets.AddFleet(Side.Collective, "ride", new[] { Corvette }, "A06")); // a rider: never seen
        // L1 goes to A05 (his trip ends there); L2 passes A05 on his way to A03
        h.Game.Submit(s => new MoveCharacterOrder(s, Side.Collective, L1, "A05"));
        h.Game.Submit(s => new MoveCharacterOrder(s, Side.Collective, L2, "A03"));
        h.Game.Submit(s => new Praedra.Core.Missions.MoveGroupOrder(s, Side.Collective, new List<int> { L4 }, new List<int>(), "A05")); // L4 in a group
        h.Game.Step();
        return h;
    }

    private static int L1Arrival(GameHost h) =>
        h.Game.Events.OfType<Praedra.Core.Clock.OrderAccepted>().First(e => e.Path != null && e.Path.Items[^1] == "A05").ArrivalDay;

    /// <summary>A Hand espionage success on <paramref name="target"/> inside the next Step; returns the draws it made and the stamp.</summary>
    private static (long Draws, int Stamp) Espionage(GameHost h, string target)
    {
        long draws = 0; int stamp = 0; bool done = false;
        h.Game.RegisterHook("6.8", ctx =>
        {
            if (done) return; done = true;
            long before = ctx.Rng.Draws;
            h.Fog.EspionageSuccess(ctx, Side.Hand, target);
            draws = ctx.Rng.Draws - before; stamp = ctx.Day + 1;
        });
        h.Game.Step();
        return (draws, stamp);
    }

    // m5-fog-and-hq FE9: inbound sightings for L1 (alone) and L4 (a Move group) at A05 with the exact day each stands there; nothing for L2
    // (passing) or L3 (riding a fleet); the events to the Hand only; draws as without the rule (the Hearth tip's one)
    [Fact]
    public void FE9_EspionageSeesTheLeaderOnHisWayIn()
    {
        var h = Host(true);
        Assert.NotNull(h.Characters.Get(L1)!.Transit);
        var (draws, stamp) = Espionage(h, "A05");
        Assert.Equal(1, draws);
        var all = h.Fog.LeadersInbound(Side.Hand);
        Assert.Equal(new[] { L1, L4 }.OrderBy(i => i), all.Select(i => i.Character));
        var group = all.Single(i => i.Character == L4);
        Assert.Equal(("A05", h.Missions.Missions.Single(m => m.Side == Side.Collective).ArrivalDay), (group.System, group.ReachDay));
        Assert.True(group.InGroup);
        var x = all.Single(i => i.Character == L1);
        Assert.False(x.InGroup);
        Assert.Equal((L1, "A05", stamp), (x.Character, x.System, x.Day));
        Assert.Empty(h.Fog.LeadersInbound(Side.Collective));
        var seen = h.Game.Events.OfType<LeaderInboundSeen>().Single(e => e.Character == L1);
        Assert.Equal(2, h.Game.Events.OfType<LeaderInboundSeen>().Count());
        Assert.Equal((Side.Hand, L1, "A05", x.ReachDay), (seen.Viewer, seen.Character, seen.System, seen.ReachDay));
        Assert.Contains(seen, h.Fog.EventsFor(Side.Hand));
        Assert.DoesNotContain(seen, h.Fog.EventsFor(Side.Collective));
        Assert.Equal(L1Arrival(h), x.ReachDay); // the reach day is the day the order said he stands there (MI78 sees him there)
        Assert.True(x.ReachDay > stamp);
    }

    // m5-fog-and-hq FE10: off, nothing; groups off, only the leader alone; a game without an inbound sighting saves with no inbound list
    [Fact]
    public void FE10_SwitchOffAndSavedForm()
    {
        var alone = Host(true, groups: false);
        Espionage(alone, "A05");
        Assert.Equal(L1, alone.Fog.LeadersInbound(Side.Hand).Single().Character);

        var h = Host(false);
        var (draws, _) = Espionage(h, "A05");
        Assert.Equal(1, draws);
        Assert.Empty(h.Fog.LeadersInbound(Side.Hand));
        Assert.Empty(h.Game.Events.OfType<LeaderInboundSeen>());
        Assert.Null(h.Game.State.Fog.Hand.LeadersInbound);
        Assert.DoesNotContain("LeadersInbound", h.Save());

        var on = Host(true);
        Espionage(on, "A05");
        string saved = on.Save();
        Assert.Contains("LeadersInbound", saved);
        var back = GameHost.Load(saved, new GameSettings(), MapE.Value);
        Assert.Equal(on.Fog.LeadersInbound(Side.Hand).Select(i => i.ReachDay), back.Fog.LeadersInbound(Side.Hand).Select(i => i.ReachDay));
    }

    // m5-fog-and-hq FE11 (Paul 2026-11-28, "chances are pretty low"): a mission group is seen only on a draw, one per group; 0 = never (no
    // draw, the leader alone still seen), 100 = always (no draw), between: exactly one extra draw for L4's group, the leader alone needs none
    [Fact]
    public void FE11_AMissionGroupIsSeenOnlyOnADraw()
    {
        Assert.Equal(15, new FogParams().InboundMissionGroupPercent);
        var never = Host(true, groupPercent: 0);
        var (d0, _) = Espionage(never, "A05");
        Assert.Equal(L1, never.Fog.LeadersInbound(Side.Hand).Single().Character);
        var always = Host(true, groupPercent: 100);
        var (d100, _) = Espionage(always, "A05");
        Assert.Equal(new[] { L4, L1 }, always.Fog.LeadersInbound(Side.Hand).Select(i => i.Character));
        Assert.Equal(d0, d100);
        var half = Host(true, groupPercent: 50);
        var (d50, _) = Espionage(half, "A05");
        Assert.Equal(d0 + 1, d50);
        Assert.Contains(L1, half.Fog.LeadersInbound(Side.Hand).Select(i => i.Character));
    }
}
