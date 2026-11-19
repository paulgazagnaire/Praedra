using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Setup;

/// <summary>
/// The Hand's big starting military (Paul 2026-11-19): one big fleet at Charter, Pacifiers at Charter for its troop berths, and one Pacifier
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
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = preset });
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
        Assert.Equal(forces.Int(Side.Hand, "base_regiments"), atCharter);
        Assert.True(atCharter >= berths, $"{atCharter} Pacifiers at Charter for {berths} berths");

        // one Pacifier regiment in every sector the Hand holds (other than Charter's own system and the HQ)
        var held = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Hand).Select(kv => kv.Key)
            .Where(n => n != "Charter" && n != h.HqSystem && !galaxy.Get(n).IsOutsideSectors).ToList();
        foreach (var sector in held.GroupBy(n => galaxy.Get(n).Sector))
        {
            int n = eco.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == Side.Hand && o.Class == "Pacifiers" && o.System != null && sector.Contains(o.System));
            Assert.Equal(forces.Int(Side.Hand, "spread_regiments"), n);
        }

        // the Collective spreads none, and the Hand keeps room to build: upkeep load under 80% of capacity on day one
        Assert.Equal(0, forces.Int(Side.Collective, "spread_regiments"));
        Assert.True(eco.Load(Side.Hand) * 100 < eco.Capacity(Side.Hand) * 80, $"Hand load {eco.Load(Side.Hand)} of {eco.Capacity(Side.Hand)}");
    }
}
