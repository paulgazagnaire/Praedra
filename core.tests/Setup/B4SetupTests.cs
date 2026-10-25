using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;
using Praedra.Core.Support;
using static Praedra.Core.Tests.Characters.CharWorld;

namespace Praedra.Core.Tests.Setup;

/// <summary>
/// Reverse-engineering batch B4 (new-game setup), part A: the code on main today (NewGame with the Anchored deal and new-game-forces.csv).
/// Day-one knowledge (m5-fog-and-hq §F2 Setup, §F4; B4 §11), garrisons (m5-setup-and-victory §P6; B4 §6), names (§P7 Names; B4 §12) and the
/// The Shoal order of the starting characters (§P9; B4 §9). The Classic setup tests B4-6 to B4-13 wait for DealMode Classic (part B).
/// </summary>
public class B4SetupTests
{
    private static readonly int[] Seeds = { 1, 2, 3, 7 };
    private static GameHost Make(int seed = 1) => NewGame.Create(new NewGameOptions { Seed = seed });
    /// <summary>The Classic garrisons and HQ forces: MinGarrison 0 with HqFixedForces on (what the Classic setup, part B, will switch on).</summary>
    private static readonly GameSettings Classic = new() { MinGarrison = 0, HqFixedForces = true };
    private static GameHost MakeClassic(int seed = 1) => NewGame.Create(new NewGameOptions { Seed = seed, Settings = Classic });
    private static IEnumerable<GameHost> Games() => Seeds.Select(Make);
    /// <summary>The Classic day-one reveal: SetupRevealsFacilities on (DEFAULT on since 2026-10-18; explicit here so the test pins the rule).</summary>
    private static GameHost MakeReveal(int seed) => NewGame.Create(new NewGameOptions { Seed = seed, Settings = new GameSettings { Fog = new Praedra.Core.Fog.FogParams { SetupRevealsFacilities = true } } });

    private static int Standing(GameHost h, Side side, string cls, string sys) =>
        h.Economy.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == side && o.Class == cls && o.System == sys && o.AboardShip == 0);

    // B4-1: on day 0 the Alliance's picture of a core Empire system lists its facilities (group C) and has no regiments, squadrons, fleets or
    // characters (D, E never written). With SetupRevealsFacilities on (Classic). rule: m5-fog-and-hq §F2 Setup, §F4 1 (B4 §11)
    [Fact]
    public void B4_1_DayOneCorePictureHoldsFacilitiesOnly()
    {
        // DEFAULT on (Classic) since 2026-10-18 (AI trainer: the AI floors pass with it); with the switch off there is no C.
        Assert.True(new Praedra.Core.Fog.FogParams().SetupRevealsFacilities);
        var off = NewGame.Create(new NewGameOptions { Seed = 1, Settings = new GameSettings { Fog = new Praedra.Core.Fog.FogParams { SetupRevealsFacilities = false } } });
        Assert.Null(off.Fog.Picture(Side.Alliance, "Charter")!.Facilities);
        foreach (var h in Seeds.Select(MakeReveal))
        {
            var fog = h.Game.State.Fog;
            int checkedSystems = 0;
            foreach (var sys in fog.StartExploredAll)
            {
                if (h.Control.HolderOf(sys) != Side.Empire) continue;
                var pic = h.Fog.Picture(Side.Alliance, sys);
                Assert.NotNull(pic);
                var truth = h.Economy.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == sys && o.Completed)
                    .GroupBy(o => o.Class).ToDictionary(g => g.Key, g => g.Count());
                Assert.NotNull(pic!.Facilities);
                Assert.Equal(0, pic.Facilities!.Stamp);
                Assert.Equal(truth.OrderBy(kv => kv.Key, StringComparer.Ordinal), pic.Facilities.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal));
                Assert.False(pic.HqHere);
                Assert.Null(pic.Ground); // D: never written
                Assert.Null(pic.Fleets); // E: never written
                Assert.Null(pic.LastLookDay); // §F4 4: the day-0 picture is Charted, not Observed
                checkedSystems++;
            }
            Assert.True(checkedSystems > 0);
            // Charter stands for the batch's example: its Construction Yard is seen, its troops and fleet are not.
            var cor = h.Fog.Picture(Side.Alliance, "Charter")!;
            Assert.True(cor.Facilities!.Value.GetValueOrDefault("Construction Yard") > 0);
            Assert.True(Standing(h, Side.Empire, "Pacifiers", "Charter") > 0);
            Assert.Contains(h.Game.State.Fleets.Values, f => f.Side == Side.Empire && f.At == "Charter");
            Assert.Null(cor.Ground); Assert.Null(cor.Fleets);
        }
    }

    // B4-2: on day 0 the Empire's picture of The Shoal is the full copy taken after the characters are placed; its picture of the HQ system has no
    // facilities and HqHere false. rule: m5-fog-and-hq §F4 3 and 4; m5-setup-and-victory §P9 (B4 §9, §11)
    // The fog picture has no character list (group D holds regiments and special forces only), so the six characters are checked at The Shoal in the
    // state when the copy is taken, and their order in start-cast.csv.
    [Fact]
    public void B4_2_EmpireSeesYavinInFullAndNothingOfTheHq()
    {
        int[] yavinSix = { Luke, Leia, Han, Chewbacca, Dodonna, Wedge };
        var cast = GameData.Default.Characters.StartCast.Where(r => r.Rule == StartRule.FixedSystem && r.Place == "The Shoal").Select(r => r.Character);
        Assert.Equal(yavinSix, cast);
        foreach (var h in Games())
        {
            foreach (var id in yavinSix)
            {
                var c = h.Characters.Get(id)!;
                Assert.True(c.Placed); Assert.Equal("The Shoal", c.System); Assert.Equal(0, c.AttachedFleet);
            }
            var k = h.Game.State.Fog.Empire.Systems["The Shoal"];
            Assert.True(k.Explored);
            Assert.Equal(0, k.StampC); Assert.Equal(0, k.StampD); Assert.Equal(0, k.StampE);
            var ground = k.Ground.Single(g => g.Side == Side.Alliance);
            Assert.Equal(Standing(h, Side.Alliance, "Militia", "The Shoal"), ground.Units.GetValueOrDefault("Militia"));

            string hq = h.HqSystem!;
            Assert.False(h.Fog.IsExplored(Side.Empire, hq));
            Assert.Null(h.Fog.Picture(Side.Empire, hq));
            if (h.Game.State.Fog.Empire.Systems.TryGetValue(hq, out var hk))
            {
                Assert.Null(hk.StampC); Assert.Empty(hk.Facilities); Assert.False(hk.HqHere);
            }
        }
    }

    // B4-3: an Empire system at Empire support 37 has 3 Imperial Army regiments on day 0, one at 75 has none; Charter, The Shoal and the HQ have
    // no garrison regiments. Every held system gets exactly N = ceil((60 - s) / 10) below 60 and none at 60 or more (min_garrison 0, Classic).
    // rule: m5-setup-and-victory §P6 ([EXE-own] 0x51ab20; B4 §6); m2-uprisings-and-garrison §Required garrison N
    [Fact]
    public void B4_3_GarrisonsOnlyBelowSixty()
    {
        var p = new SupportParams();
        Assert.Equal(3, SupportModule.RequiredGarrison(p, 37, false, false));
        Assert.Equal(0, SupportModule.RequiredGarrison(p, 75, false, false));
        Assert.Equal(0, SupportModule.RequiredGarrison(p, 60, false, false));
        Assert.Equal(1, SupportModule.RequiredGarrison(p, 59, false, false));
        var forces = NewGameData.Default.Forces;
        // the data keeps 1 until the Classic setup (part B); the switches give the Classic rule
        foreach (var side in new[] { Side.Empire, Side.Alliance }) Assert.Equal(1, forces.Int(side, "min_garrison"));
        Assert.Null(new GameSettings().MinGarrison); Assert.False(new GameSettings().HqFixedForces);

        foreach (var seed in Seeds)
        {
            var h = MakeClassic(seed);
            int below = 0;
            foreach (var side in new[] { Side.Empire, Side.Alliance })
            {
                string gClass = forces.Str(side, "garrison_class"), baseSys = forces.Str(side, "base_system");
                bool baseSameClass = forces.Str(side, "base_regiment_class") == gClass;
                foreach (var sys in h.Game.State.SystemOwners.Where(kv => kv.Value == side).Select(kv => kv.Key))
                {
                    int s = h.Economy.SupportOf(sys, side);
                    int want = s < 60 ? (60 - s + 9) / 10 : 0;
                    if (want > 0) below++;
                    int extra = (baseSameClass && sys == baseSys ? forces.Int(side, "base_regiments") : 0)
                        + (sys == h.HqSystem ? forces.HqUnits.Where(u => u.Side == side && u.Class == gClass).Sum(u => u.Count) : 0); // the fixed HQ forces (§P7)
                    Assert.True(want + extra == Standing(h, side, gClass, sys), $"seed {seed}: {sys} ({side} support {s})");
                }
            }
            Assert.True(below > 0); // the rule is exercised: some holdings start below 60
            // the fixed worlds: none of the garrison class beyond the base regiments
            Assert.Equal(0, Standing(h, Side.Empire, "Tranches", "Charter"));
            Assert.Equal(forces.Int(Side.Alliance, "base_regiments"), Standing(h, Side.Alliance, "Militia", "The Shoal"));
            Assert.Equal(2, Standing(h, Side.Alliance, "Militia", h.HqSystem!)); // the fixed HQ forces only, no garrison
            // the batch's anchors: Lenitas (Alliance 57) and Caminus (58), Empire-held at Empire support 43 and 42, get 2 regiments each
            foreach (var sys in new[] { "Lenitas", "Caminus" })
                if (h.Control.HolderOf(sys) == Side.Empire && h.Economy.SupportOf(sys, Side.Alliance) is 57 or 58)
                    Assert.Equal(2, Standing(h, Side.Empire, "Tranches", sys));
        }
    }

    // B4-4: with every Alliance system at support 60 or more, the Alliance has no garrison regiment on day 0 outside the fixed The Shoal (base) and
    // HQ troops. rule: m5-setup-and-victory §P6 (B4 §6: in Classic only the Empire starts with garrisons)
    [Fact]
    public void B4_4_AllianceAtSixtyOrMoreHasNoGarrison()
    {
        int games = 0;
        foreach (var seed in Enumerable.Range(1, 12))
        {
            var h = MakeClassic(seed);
            var held = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Alliance).Select(kv => kv.Key).ToList();
            if (held.Any(s => h.Economy.SupportOf(s, Side.Alliance) < 60)) continue; // the batch's condition
            games++;
            var outside = h.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Side == Side.Alliance && o.AboardShip == 0
                && o.System != null && o.System != "The Shoal" && o.System != h.HqSystem).ToList();
            Assert.Empty(outside);
        }
        Assert.True(games > 0, "no seed in 1..12 gives every Alliance holding support 60 or more");
    }

    // B4-4 (the HQ troops it names): with HqFixedForces on, the Rebel HQ system starts with the fixed CMUNHQTB forces standing on it, not in a
    // fleet: 2 Alliance Army Regiments, 1 Deckhands, 4 Swift, 2 Plough. Off (DEFAULT until the Classic setup) it gets none of
    // them. rule: m5-setup-and-victory §P7 ([EXE-own] 0x51ad90; B4 §7 step 2)
    [Fact]
    public void B4_4_TheHqStartsWithItsFixedForces()
    {
        foreach (var seed in Seeds)
        {
            var h = MakeClassic(seed);
            string hq = h.HqSystem!;
            var there = h.Economy.Objects.Where(o => o.Side == Side.Alliance && o.System == hq && o.AboardShip == 0 && o.Category != ObjectCategory.Facility)
                .GroupBy(o => o.Class).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(new Dictionary<string, int> { ["Militia"] = 2, ["Deckhands"] = 1, ["Swift"] = 4, ["Plough"] = 2 }
                .OrderBy(kv => kv.Key, StringComparer.Ordinal), there.OrderBy(kv => kv.Key, StringComparer.Ordinal));
            Assert.All(h.Economy.Objects.Where(o => o.System == hq && o.Category != ObjectCategory.Facility), o => Assert.True(o.Completed));
        }
        // File mode too (the HQ is Altofen in the v6 day-one files; Cutbank on v5)
        var f = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA, Settings = Classic with { DealMode = DealMode.File } });
        Assert.Equal(2, Standing(f, Side.Alliance, "Militia", f.HqSystem!));
        // switch off (DEFAULT): only the garrison stands there (min_garrison 1 of the data), no Fleet regiment, no squadron
        var off = Make(1);
        Assert.Equal(1, Standing(off, Side.Alliance, "Militia", off.HqSystem!));
        Assert.DoesNotContain(off.Economy.Objects, o => o.System == off.HqSystem && o.Side == Side.Alliance && o.Class is "Deckhands" or "Swift" or "Plough");
    }

    // B4-5: the first fleet of each side is "Fleet 1", fleets count per side in id order; two Corellian Corvettes of one side are "... 1" and
    // "... 2" (ships count per side and class in id order). rule: m5-setup-and-victory §P7 Names ([EXE-own] 0x51b7f0; B4 §12)
    [Fact]
    public void B4_5_FleetsAndShipsAreNumberedPerSide()
    {
        foreach (var h in Games())
        {
            var st = h.Game.State;
            foreach (var side in new[] { Side.Empire, Side.Alliance })
            {
                var fleets = st.Fleets.Values.Where(f => f.Side == side).OrderBy(f => f.Id).ToList();
                Assert.NotEmpty(fleets);
                Assert.Equal(Enumerable.Range(1, fleets.Count).Select(n => $"Fleet {n}"), fleets.Select(f => f.Name));
                var ships = st.Economy.Objects.Values.Where(o => o.Category == ObjectCategory.CapitalShip && o.Side == side).OrderBy(o => o.Id);
                foreach (var g in ships.GroupBy(o => o.Class))
                    Assert.Equal(Enumerable.Range(1, g.Count()).Select(n => $"{g.Key} {n}"), g.Select(o => o.Name));
            }
            var corvettes = st.Economy.Objects.Values.Where(o => o.Side == Side.Alliance && o.Class == "Dodger").OrderBy(o => o.Id).ToList();
            Assert.True(corvettes.Count >= 2);
            Assert.Equal("Dodger 1", corvettes[0].Name);
            Assert.Equal("Dodger 2", corvettes[1].Name);
            // fighters and regiments get no name
            Assert.All(st.Economy.Objects.Values.Where(o => o.Category != ObjectCategory.CapitalShip), o => Assert.Null(o.Name));
        }
    }

    // B4-5 (save): the names survive a save and load; an object with no name keeps the old save form (no "Name" key).
    [Fact]
    public void B4_5_NamesSurviveSaveAndLoad()
    {
        var h = Make();
        var loaded = GameSave.Load(GameSave.Save(h.Game));
        foreach (var (id, o) in h.Game.State.Economy.Objects) Assert.Equal(o.Name, loaded.State.Economy.Objects[id].Name);
        foreach (var (id, f) in h.Game.State.Fleets) Assert.Equal(f.Name, loaded.State.Fleets[id].Name);
        Assert.DoesNotContain("\"Name\":null", GameSave.Save(h.Game), StringComparison.Ordinal);
    }
}
