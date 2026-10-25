using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Map;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.Setup;

// m5-setup-and-victory §Mode Anchored (DealMode, DEFAULT since 2026-10-16). Test ids AN1..AN16.
public class AnchoredDealTests
{
    private readonly ITestOutputHelper _out;
    public AnchoredDealTests(ITestOutputHelper output) { _out = output; }

    private static readonly Galaxy G = GameData.Default.Galaxy;
    private static NewGameData ND => NewGameData.Default;
    private static readonly StartPreset[] Presets = Enum.GetValues<StartPreset>();
    public static IEnumerable<object[]> AllPresets => Presets.Select(p => new object[] { p });

    private static GameHost Make(StartPreset p, int seed, DealMode mode = DealMode.Anchored) =>
        NewGame.Create(GameData.Default, new NewGameOptions { Seed = seed, Preset = p, Settings = new GameSettings { DealMode = mode } });

    private static AnchoredDeal Deal(StartPreset p, int seed) =>
        AnchoredDealer.Deal(G, new GameRng((ulong)(uint)seed), ND.Anchors, ND.Totals[p], new DealParams());

    private static int Count(GameHost h, Side s) => h.Game.State.SystemOwners.Count(kv => kv.Value == s);
    private static Side? Holder(GameHost h, string s) => h.Control.HolderOf(s);

    // m5-setup-and-victory AN1: the same seed gives the identical deal (and the identical whole game)
    [Theory, MemberData(nameof(AllPresets))]
    public void AN1_SameSeedSameDeal(StartPreset p)
    {
        var a = Deal(p, 7); var b = Deal(p, 7);
        Assert.Equal(a.Rows, b.Rows);
        Assert.Equal(Make(p, 7).StateHash(), Make(p, 7).StateHash());
    }

    // m5-setup-and-victory AN2: different seeds give a different HQ and a different deal
    [Fact]
    public void AN2_DifferentSeedsDiffer()
    {
        var deals = Enumerable.Range(1, 8).Select(s => Deal(StartPreset.MedA, s)).ToList();
        Assert.True(deals.Select(d => d.Hq).Distinct().Count() >= 4, "HQ should vary with the seed");
        Assert.True(deals.Select(d => string.Join("|", d.Rows.Where(r => r.Owner != null).Select(r => r.Name + r.Owner))).Distinct().Count() == deals.Count);
        Assert.NotEqual(Make(StartPreset.MedA, 1).StateHash(), Make(StartPreset.MedA, 2).StateHash());
    }

    // m5-setup-and-victory AN3: exact totals per preset, scaled to the galaxy (systems in sectors / 360, rounded half up), Glasswood and Frostholm neutral
    [Theory, MemberData(nameof(AllPresets))]
    public void AN3_ExactTotals(StartPreset p)
    {
        var t = ND.Totals[p];
        int inSectors = TestData.RealGalaxy.Systems.Count(s => !s.IsOutsideSectors); int Scaled(int x) => (x * inSectors * 2 + 360) / 720;
        Assert.Equal((38, 138), (Scaled(ND.Totals[StartPreset.MedA].CollectiveTotal), Scaled(ND.Totals[StartPreset.MedA].HandTotal))); // setup-victory/10 banner
        for (int seed = 1; seed <= 6; seed++)
        {
            var h = Make(p, seed);
            Assert.Equal(Scaled(t.CollectiveTotal), Count(h, Side.Collective));
            Assert.Equal(Scaled(t.HandTotal), Count(h, Side.Hand));
            Assert.Null(Holder(h, "Glasswood")); Assert.Null(Holder(h, "Frostholm"));
        }
    }

    // m5-setup-and-victory AN4: every anchor has its owner; Charter Hand, The Shoal and the HQ Collective
    [Theory, MemberData(nameof(AllPresets))]
    public void AN4_AnchorsHaveTheirOwner(StartPreset p)
    {
        var h = Make(p, 3);
        Assert.Equal(23, ND.Anchors.Count(a => a.Owner == Side.Hand));
        Assert.Equal(3, ND.Anchors.Count(a => a.Owner == Side.Collective));
        Assert.Equal(11, ND.Anchors.Count(a => a.Owner == null));
        foreach (var a in ND.Anchors) Assert.Equal(a.Owner, Holder(h, a.System));
        Assert.Equal(Side.Hand, Holder(h, "Charter"));
        Assert.Equal(Side.Collective, Holder(h, "The Shoal"));
        Assert.Equal(Side.Collective, Holder(h, h.HqSystem!));
    }

    // m5-setup-and-victory AN5: locked neutrals stay neutral on every seed (and Fiscus is a Hand anchor)
    [Fact]
    public void AN5_LockedNeutralsStayNeutral()
    {
        var locked = ND.Anchors.Where(a => a.Owner == null).Select(a => a.System).ToList();
        Assert.Contains("Amargura", locked); Assert.Contains("Breakyard", locked);
        foreach (var p in Presets)
            for (int seed = 1; seed <= 10; seed++)
            {
                var d = Deal(p, seed);
                foreach (var s in locked) Assert.Null(d.Rows.Single(r => r.Name == s).Owner);
            }
        Assert.Equal(Side.Hand, Holder(Make(StartPreset.Easy, 1), "Fiscus"));
    }

    // m5-setup-and-victory AN6: the HQ satisfies the candidate rule (outer, not The Shoal, not an anchor, at least 200 map units from Charter)
    [Theory, MemberData(nameof(AllPresets))]
    public void AN6_HqSatisfiesCandidateRule(StartPreset p)
    {
        var anchors = ND.Anchors.Select(a => a.System).ToHashSet(StringComparer.Ordinal);
        var c = G.Get("Charter");
        for (int seed = 1; seed <= 12; seed++)
        {
            var hq = Deal(p, seed).Hq;
            var s = G.Get(hq);
            Assert.Contains(s.Group, new[] { "midreach", "farreach" });
            Assert.NotEqual("The Shoal", hq);
            Assert.DoesNotContain(hq, anchors);
            Assert.True(Praedra.Core.Travel.TravelFormula.MapDistance(s.MapX, s.MapY, c.MapX, c.MapY) >= 200);
        }
    }

    // m5-setup-and-victory AN7: Collective systems are never group heartland (anchors, pockets and scattered cells are all outer)
    [Theory, MemberData(nameof(AllPresets))]
    public void AN7_CollectiveIsReachOnly(StartPreset p)
    {
        for (int seed = 1; seed <= 12; seed++)
            foreach (var r in Deal(p, seed).Rows.Where(r => r.Owner == Side.Collective))
                Assert.NotEqual("heartland", G.Get(r.Name).Group);
    }

    // m5-setup-and-victory AN8: Lenitas 57, Caminus 58 (Hand-held, Collective sympathy), no draw
    [Theory, MemberData(nameof(AllPresets))]
    public void AN8_AnchorSupport(StartPreset p)
    {
        var h = Make(p, 5);
        Assert.Equal(57, h.Economy.SupportOf("Lenitas", Side.Collective)); Assert.Equal(Side.Hand, Holder(h, "Lenitas"));
        Assert.Equal(58, h.Economy.SupportOf("Caminus", Side.Collective)); Assert.Equal(Side.Hand, Holder(h, "Caminus"));
        // other anchors use their holder's strong band: Hand 10..40 (Collective support), Collective 60..90
        foreach (var a in ND.Anchors.Where(a => a.Owner == Side.Hand && a.CollectiveSupport == null))
            Assert.InRange(h.Economy.SupportOf(a.System, Side.Collective), 10, 40);
        foreach (var a in ND.Anchors.Where(a => a.Owner == Side.Collective))
            Assert.InRange(h.Economy.SupportOf(a.System, Side.Collective), 60, 90);
    }

    // m5-setup-and-victory AN9: Charter 0, The Shoal and the HQ 100
    [Theory, MemberData(nameof(AllPresets))]
    public void AN9_FixedSupportBands(StartPreset p)
    {
        var h = Make(p, 4);
        Assert.Equal(0, h.Economy.SupportOf("Charter", Side.Collective));
        Assert.Equal(100, h.Economy.SupportOf("The Shoal", Side.Collective));
        Assert.Equal(100, h.Economy.SupportOf(h.HqSystem!, Side.Collective));
        Assert.True(h.IsExplored("Charter", Side.Collective) && h.IsExplored("Charter", Side.Hand));
        Assert.True(h.IsExplored("The Shoal", Side.Hand));
        Assert.False(h.IsExplored(h.HqSystem!, Side.Hand));
    }

    // m5-setup-and-victory AN10: File mode reproduces the old deal (fixed CSVs and fixed-owners.csv)
    [Theory, MemberData(nameof(AllPresets))]
    public void AN10_FileModeReproducesTheOldDeal(StartPreset p)
    {
        var h = Make(p, 1, DealMode.File);
        var owners = ND.DayOne[p].ToDictionary(r => r.Name, r => r.Owner);
        foreach (var (sys, o) in ND.FixedOwners) owners[sys] = o;
        Assert.Equal(owners.Values.Count(v => v == Side.Collective), Count(h, Side.Collective));
        Assert.Equal(owners.Values.Count(v => v == Side.Hand), Count(h, Side.Hand));
        Assert.Equal("Altofen", h.HqSystem); // v6 re-pin 2026-10-24: the day-one files' HQ on the spiral map (was Cutbank)
        foreach (var s in new[] { "Navalia", "Incus", "Classis", "Castra" }) Assert.Equal(Side.Hand, Holder(h, s));
        foreach (var s in new[] { "Caminus", "The Moorings", "Ferraria" }) Assert.Null(Holder(h, s));
        Assert.Equal(h.StateHash(), Make(p, 1, DealMode.File).StateHash());
        Assert.Equal(DealMode.Anchored, new GameSettings().DealMode); // the DEFAULT
    }

    // m5-setup-and-victory AN11: anchor validation (unknown system, duplicate, Charter, The Shoal) throws SetupException
    [Fact]
    public void AN11_AnchorValidation()
    {
        AnchorRow R(string s) => new(s, Side.Hand, null, "test");
        void Check(string text, params AnchorRow[] rows)
        {
            var ex = Assert.Throws<SetupException>(() => AnchoredDealer.Validate(G, rows));
            Assert.Contains(text, ex.Message);
        }
        Check("not a system", R("No Such Place"));
        Check("twice", R("Navalia"), R("Navalia"));
        Check("Charter", R("Charter"));
        Check("The Shoal", R("The Shoal"));
        AnchoredDealer.Validate(G, ND.Anchors); // the shipped file is valid
        Assert.Throws<SetupException>(() => AnchoredDealer.Deal(G, new GameRng(1), new[] { R("The Shoal") }, ND.Totals[StartPreset.Easy], new DealParams()));
    }

    // m5-setup-and-victory AN12: no shortfall on the shipped data (every preset, 25 seeds)
    [Fact]
    public void AN12_NoShortfall()
    {
        foreach (var p in Presets)
            for (int seed = 1; seed <= 25; seed++) Assert.Empty(Deal(p, seed).Shortfalls);
    }

    // m5-setup-and-victory AN13: the starting yards: The Moorings Collective, Caminus and Ferraria Hand, each holder gets the Orbital Shipyard
    [Theory, MemberData(nameof(AllPresets))]
    public void AN13_StartingYardsFollowTheAnchors(StartPreset p)
    {
        var h = Make(p, 2);
        foreach (var (s, side) in new[] { ("The Moorings", Side.Collective), ("Caminus", Side.Hand), ("Ferraria", Side.Hand), ("Navalia", Side.Hand) })
        {
            Assert.Equal(side, Holder(h, s));
            Assert.Contains(h.Economy.Objects, o => o.Class == "Orbital Shipyard" && o.System == s && h.Economy.OwnerOf(o) == side);
        }
    }

    // m5-setup-and-victory AN14: strong/weak: Collective all strong (wA = 0), anchors, Charter, The Shoal and the HQ never weak, the Hand has weak systems when wE > 0
    [Theory, MemberData(nameof(AllPresets))]
    public void AN14_StrongAndWeak(StartPreset p)
    {
        var anchors = ND.Anchors.Select(a => a.System).ToHashSet(StringComparer.Ordinal);
        for (int seed = 1; seed <= 6; seed++)
        {
            var d = Deal(p, seed);
            foreach (var r in d.Rows)
            {
                if (r.Weak) Assert.Equal(Side.Hand, r.Owner);
                if (r.Weak) Assert.DoesNotContain(r.Name, anchors);
                if (r.Owner == null) Assert.False(r.Weak);
            }
            Assert.True(d.Rows.Count(r => r.Weak) > 0);
            Assert.False(d.Rows.Single(r => r.Name == "Charter").Weak);
        }
    }

    // m5-setup-and-victory AN15: the deal draws come first from the game's one generator (HQ pick is draw 1)
    [Fact]
    public void AN15_DealUsesTheGamesGenerator()
    {
        var expected = Deal(StartPreset.MedA, 9);
        var h = Make(StartPreset.MedA, 9);
        Assert.Equal(expected.Hq, h.HqSystem);
        foreach (var r in expected.Rows) Assert.Equal(r.Owner, Holder(h, r.Name));
    }

    // m5-setup-and-victory AN16: sanity table for seeds 1..5 (printed; asserts only that the front line exists)
    [Fact]
    public void AN16_DealTable()
    {
        _out.WriteLine("preset seed collective hand neutral hq front");
        foreach (var p in Presets)
            for (int seed = 1; seed <= 5; seed++)
            {
                var d = Deal(p, seed);
                var own = d.Rows.ToDictionary(r => r.Name, r => r.Owner);
                int a = own.Values.Count(v => v == Side.Collective), e = own.Values.Count(v => v == Side.Hand);
                int front = own.Where(kv => kv.Value == Side.Collective).Count(kv =>
                {
                    G.TryFindSystem(kv.Key, out int i);
                    return G.NearOf(i).Any(nb => own[G.Systems[nb].Name] == Side.Hand); // nearby (m1-galaxy-data), no lanes since 2026-10-16
                });
                _out.WriteLine($"{StartPresets.Key(p),-6} {seed} {a,3} {e,4} {own.Count - a - e,4} {d.Hq,-16} {front}");
                Assert.True(front > 0, $"{p} seed {seed}: no Collective system touches a Hand system");
            }
    }
}
