using Praedra.Core.Clock;
using Praedra.Core.Fog;
using Praedra.Core.Map;
using Praedra.Core.Setup;
using static Praedra.Core.Tests.Fog.FogWorld;

namespace Praedra.Core.Tests.Fog;

// Espionage and recon reveals FE1 to FE7 of m5-fog-and-hq §Tests. The success itself is m5-missions'; these call the sheet's Reveal directly.
public class EspionageTests
{
    // Map E: 12 Collective systems A01..A12 in a chain from the Hand's E0; A01, A02, A03 are start_explored_all. HQ at A01 (= T).
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

    private static GameHost HostE(ulong seed, string hq = "A01", GameSettings? settings = null)
    {
        var h = GameHost.New(seed, settings, MapE.Value);
        h.Fleets.SetOwner("E0", Side.Hand);
        for (int i = 1; i <= 12; i++) h.Fleets.SetOwner($"A{i:00}", Side.Collective);
        h.Fog.PlaceHq(hq);
        h.Fog.SetupNewGame(new[] { "A01", "A02", "A03" });
        return h;
    }

    /// <summary>Run the espionage inside the next Step; returns the draws it made and the stamp it wrote.</summary>
    private static (long Draws, int Stamp) Espionage(GameHost h, Side side, string target)
    {
        long draws = 0; int stamp = 0;
        bool done = false;
        h.Game.RegisterHook("6.8", ctx =>
        {
            if (done) return; done = true;
            long before = ctx.Rng.Draws;
            h.Fog.EspionageSuccess(ctx, side, target);
            draws = ctx.Rng.Draws - before; stamp = ctx.Day + 1;
        });
        h.Game.Step();
        return (draws, stamp);
    }

    private static List<string> FullCopies(GameHost h, Side side, int stamp) =>
        h.Game.State.Fog.Of(side).Systems.Where(kv => h.Fleets.OwnerOf(kv.Key) != side && kv.Value.StampC == stamp && kv.Value.StampD == stamp).Select(kv => kv.Key).ToList();

    // m5-fog-and-hq FE1
    [Fact]
    public void FE1_EspionageOnTheHqSystem()
    {
        var w = new FogWorld();
        w.Step(29);
        int stamp = 0;
        w.InStep(ctx => { w.Fog.EspionageSuccess(ctx, Side.Hand, R2); stamp = ctx.Day + 1; });
        w.Step();
        var k = w.Know(Side.Hand, R2)!;
        Assert.True(k.Explored);
        Assert.Equal(stamp, k.StampA); Assert.Equal(stamp, k.StampB); Assert.Equal(stamp, k.StampC); Assert.Equal(stamp, k.StampD); Assert.Equal(stamp, k.StampE);
        Assert.True(k.HqHere);
        Assert.Equal(new KnownHqInfo(R2, stamp), w.Fog.KnownHq(Side.Hand));
    }

    // m5-fog-and-hq FE2
    [Fact]
    public void FE2_RichTargetGivesOneToFiveExtras()
    {
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var h = HostE(seed);
            var (draws, stamp) = Espionage(h, Side.Hand, "A01");
            var copies = FullCopies(h, Side.Hand, stamp);
            Assert.Contains("A01", copies);
            var extras = copies.Where(c => c != "A01").ToList();
            int n = extras.Count;
            Assert.InRange(n, 1, 5);
            Assert.Equal(1 + n, draws); // N <= 11 = pool size, so picks = N
            Assert.All(extras, e => Assert.Equal(Side.Collective, h.Fleets.OwnerOf(e)));
            var again = HostE(seed);
            var (_, stamp2) = Espionage(again, Side.Hand, "A01");
            Assert.Equal(copies, FullCopies(again, Side.Hand, stamp2)); // same input, same extras
        }
    }

    // m5-fog-and-hq FE3
    [Fact]
    public void FE3_OrdinaryTargetGivesOneExtraFromTheExploredClass()
    {
        var h = HostE(7);
        var (draws, stamp) = Espionage(h, Side.Hand, "A02");
        Assert.Equal(2, draws); // no draw for N (range 0), one pick, one Hearth tip draw (§N6a; the Hearth is at A01)
        var extras = FullCopies(h, Side.Hand, stamp).Where(c => c != "A02").ToList();
        var x = Assert.Single(extras);
        Assert.Contains(x, new[] { "A01", "A03" });
    }

    // m5-fog-and-hq FE4
    [Fact]
    public void FE4_UnexploredTargetGivesNoExtras()
    {
        var h = HostE(3);
        Assert.False(h.Fog.IsExplored(Side.Hand, "A05"));
        var (draws, stamp) = Espionage(h, Side.Hand, "A05");
        Assert.Equal(1, draws); // no extras; only the Hearth tip draw (§N6a)
        Assert.Equal(new[] { "A05" }, FullCopies(h, Side.Hand, stamp));
    }

    // m5-fog-and-hq FE5
    [Fact]
    public void FE5_ReconWritesFacilitiesAndTheHq()
    {
        var w = new FogWorld();
        int stamp = 0;
        w.InStep(ctx => { w.Fog.Reveal(ctx, Side.Hand, R2, RevealKind.Recon); stamp = ctx.Day + 1; });
        w.Step();
        var k = w.Know(Side.Hand, R2)!;
        Assert.Equal(stamp, k.StampA); Assert.Equal(stamp, k.StampB); Assert.Equal(stamp, k.StampC);
        Assert.True(k.HqHere);
        Assert.Null(k.StampD);
    }

    // m5-fog-and-hq FE6 (A and B written, C and HqHere not; the sheet's "A and B only" against its table's "Presence groups": see build notes)
    [Fact]
    public void FE6_ReconWithoutFacilities()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { ReconRevealsFacilities = false } });
        int stamp = 0;
        w.InStep(ctx => { w.Fog.Reveal(ctx, Side.Hand, R2, RevealKind.Recon); stamp = ctx.Day + 1; });
        w.Step();
        var k = w.Know(Side.Hand, R2)!;
        Assert.Equal(stamp, k.StampA); Assert.Equal(stamp, k.StampB);
        Assert.Null(k.StampC); Assert.Null(k.StampD);
        Assert.False(k.HqHere);
        Assert.Null(w.Fog.KnownHq(Side.Hand));
    }

    // m5-fog-and-hq §N6c FE8 (New, Paul 2026-11-25, pick 1A): an espionage success on A05 also writes the fleets (group E only) of A04 and A06,
    // the systems one jump away; A07 (two jumps) is not written; no extra draw; the neighbours' owner, facilities and ground are not written,
    // and the fleets-only write is not a look. Off (Classic): nothing next door.
    [Fact]
    public void FE8_EspionageSeesTheFleetsOneJumpAway()
    {
        foreach (bool on in new[] { true, false })
        {
            var h = HostE(1, settings: new GameSettings { Fog = new FogParams { EspionageSeesNearbyFleets = on } });
            h.Fleets.AddFleet(Side.Collective, "near", new[] { Corvette }, "A04");
            h.Fleets.AddFleet(Side.Collective, "far", new[] { Corvette }, "A07");
            var (draws, stamp) = Espionage(h, Side.Hand, "A05"); // A05 unexplored to the Hand: no extras, no draw for them
            var hand = h.Game.State.Fog.Of(Side.Hand).Systems;
            Assert.Equal(stamp, hand["A05"].StampD);
            Assert.False(hand.TryGetValue("A07", out var a7) && a7.StampE == stamp);
            if (on)
            {
                Assert.Equal((Side.Collective, 1), hand["A04"].Fleets.Select(f => (f.Side, f.ShipCount)).Single());
                Assert.Equal((stamp, stamp), (hand["A04"].StampE, hand["A06"].StampE));
                Assert.Empty(hand["A06"].Fleets);
                var k4 = hand["A04"];
                Assert.Equal((false, (int?)null, (int?)null, (int?)null, (int?)null), (k4.Explored, k4.StampB, k4.StampC, k4.StampD, k4.LastLookDay));
            }
            else Assert.False(hand.TryGetValue("A04", out var a4) && a4.StampE == stamp);
            Assert.Equal(1, draws); // the Hearth tip's one draw only (§N6a), on or off
        }
    }

    // m5-fog-and-hq FE7
    [Fact]
    public void FE7_HandStartsWithAFullCopyOfShoal()
    {
        // Classic's base, The Shoal (with CollectiveBaseDraw, DEFAULT on, the copy follows the drawn base: CollectiveBaseDrawTests)
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Settings = new GameSettings { CollectiveBaseDraw = false } });
        var k = h.Game.State.Fog.Hand.Systems["The Shoal"];
        Assert.True(k.Explored);
        Assert.Equal(0, k.StampC); Assert.Equal(0, k.StampD); Assert.Equal(0, k.StampE);
        Assert.False(k.HqHere); // it is The Shoal, not the HQ system
        Assert.NotEqual("The Shoal", h.HqSystem);
        Assert.False(h.Fog.IsExplored(Side.Hand, h.HqSystem!)); // nothing reveals the HQ system at start (§F4 4)
        Assert.Null(h.Fog.KnownHq(Side.Hand));
    }
}
