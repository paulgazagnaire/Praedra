using Praedra.Core.Economy;
using Praedra.Core.Harvester;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Harvester;

// m5-harvester §1 Upkeep (Paul 2026-10-27): the harvester's upkeep is a setting over the Classic data value.
public class HarvesterUpkeepTests
{
    // m5-harvester HU1
    [Fact]
    public void HU1_TheGameUsesTheUpkeepSettingAndNullKeepsTheClassicValue()
    {
        Assert.Equal(450, GameHost.New(1).Economy.Costs.Get(EconomyModule.HarvesterClass).Maintenance);
        var classic = GameHost.New(1, new GameSettings { Harvester = new HarvesterParams { Upkeep = null } });
        Assert.Equal(600, classic.Economy.Costs.Get(EconomyModule.HarvesterClass).Maintenance);
        var other = GameHost.New(1, new GameSettings { Harvester = new HarvesterParams { Upkeep = 300 } });
        Assert.Equal(300, other.Economy.Costs.Get(EconomyModule.HarvesterClass).Maintenance);
        Assert.Equal(584, other.Economy.Costs.Get(EconomyModule.HarvesterClass).Cost); // only the upkeep changes
    }
}
