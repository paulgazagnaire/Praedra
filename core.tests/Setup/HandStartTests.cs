using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Setup;

/// <summary>
/// The Hand's big starting military (Paul 2026-11-19), behind GameSettings.BigHandStart (DEFAULT on since 2026-11-20): one big fleet at Charter, Pacifiers at Charter for its troop berths, and one Pacifier
/// regiment spread to every sector the Hand holds. The Collective's start is unchanged. rule: m5-setup-and-victory §Starting forces on the v5
/// galaxy (The Hand starts with a big military).
/// </summary>
public class HandStartTests
{
    // m5-setup-and-victory §Starting forces (Hand big military), both medium presets
    [Theory]
    [InlineData(StartPreset.MedA, 1)]
    [InlineData(StartPreset.MedE, 1)]
    [InlineData(StartPreset.MedE, 2)]
    public void TheHandStartsWithABigFleetAndSpreadTroops(StartPreset preset, int seed)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = preset, Settings = new GameSettings { BigHandStart = true } });
        var forces = NewGameData.Default.Forces;
        var eco = h.Economy; var galaxy = h.Data.Galaxy;

        // the big fleet: 4 Mandate and 2 Decree at Charter, its fighter berths filled
        var big = h.Game.State.Fleets.Values.Where(f => f.Side == Side.Hand && f.At == "Charter")
            .OrderByDescending(f => f.Ships.Count).First();
        var classes = big.Ships.Select(id => eco.Find(id)!.Class).ToList();
        Assert.Equal(4, classes.Count(c => c == "Mandate"));
        Assert.Equal(2, classes.Count(c => c == "Decree"));
        Assert.Equal(12, eco.Objects.Count(o => o.Category == ObjectCategory.Fighter && o.Side == Side.Hand && big.Ships.Contains(o.AboardShip)));

        // the regiments at Charter fill the fleet's troop berths
        int berths = classes.Sum(c => h.Data.Ships[c].TroopCapacity);
        int atCharter = eco.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == Side.Hand && o.Class == "Pacifiers" && o.System == "Charter");
        Assert.Equal(forces.Int(Side.Hand, "base_regiments", true), atCharter);
        Assert.True(atCharter >= berths, $"{atCharter} Pacifiers at Charter for {berths} berths");

        // one Pacifier regiment in every sector the Hand holds (other than Charter's own system and the HQ)
        var held = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Hand).Select(kv => kv.Key)
            .Where(n => n != "Charter" && n != h.HqSystem && !galaxy.Get(n).IsOutsideSectors).ToList();
        foreach (var sector in held.GroupBy(n => galaxy.Get(n).Sector))
        {
            int n = eco.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == Side.Hand && o.Class == "Pacifiers" && o.System != null && sector.Contains(o.System));
            Assert.Equal(forces.Int(Side.Hand, "spread_regiments", true), n);
        }

        // the Collective spreads none, and the Hand keeps room to build: upkeep load under 80% of capacity on day one
        Assert.Equal(0, forces.Int(Side.Collective, "spread_regiments"));
        Assert.True(eco.Load(Side.Hand) * 100 < eco.Capacity(Side.Hand) * 80, $"Hand load {eco.Load(Side.Hand)} of {eco.Capacity(Side.Hand)}");
    }

    // m5-setup-and-victory §Starting forces (Hand big military), QA-114: the other presets keep the Hand under its upkeep capacity on day one.
    // Measured 2026-11-20 (seeds 1-4): Easy 63-64%, HardE 65-66%, HardA 85-87% (the Hard start for a Collective player deals the Hand the
    // fewest systems, so the least capacity; the AI Hand starts big but builds little at first). Medium presets: under 80% above.
    [Theory]
    [InlineData(StartPreset.Easy, 70)]
    [InlineData(StartPreset.HardE, 70)]
    [InlineData(StartPreset.HardA, 90)]
    public void TheBigStartFitsTheHandsUpkeepOnEveryPreset(StartPreset preset, int maxPercent)
    {
        foreach (int seed in new[] { 1, 2 })
        {
            var eco = NewGame.Create(new NewGameOptions { Seed = seed, Preset = preset }).Economy;
            Assert.True(eco.Load(Side.Hand) * 100 < eco.Capacity(Side.Hand) * maxPercent, $"{preset} seed {seed}: Hand load {eco.Load(Side.Hand)} of {eco.Capacity(Side.Hand)}");
        }
    }

    // m5-setup-and-victory §Starting forces (Hand big military): on by default since the Collective re-tune (2026-11-20), and off gives the old start
    [Fact]
    public void TheBigStartIsOnByDefault_AndOffGivesTheOldStart()
    {
        Assert.True(new GameSettings().BigHandStart);
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE, Settings = new GameSettings { BigHandStart = false } });
        var eco = h.Economy;
        var home = h.Game.State.Fleets.Values.Where(f => f.Side == Side.Hand && f.At == "Charter").OrderByDescending(f => f.Ships.Count).First();
        var classes = home.Ships.Select(id => eco.Find(id)!.Class).ToList();
        Assert.Equal(2, classes.Count(c => c == "Mandate"));
        Assert.Equal(1, classes.Count(c => c == "Decree"));
        Assert.Equal(NewGameData.Default.Forces.Int(Side.Hand, "base_regiments"),
            eco.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == Side.Hand && o.Class == "Pacifiers"));
    }
}
