using Praedra.Core.Clock;
using Praedra.Core.Fog;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Fog;

// m5-fog-and-hq §Defaults for undecided items: every switch has the sheet's DEFAULT, and the settings reach the module on New and on Load.
public class FogSettingsTests
{
    [Fact]
    public void DefaultsAreTheSheets()
    {
        var p = new FogParams();
        Assert.Equal(FogRuleset.New, p.Ruleset);
        Assert.True(p.LanesPublic); Assert.True(p.HeartlandReportsInstantly); Assert.True(p.PassingFleetsExplore); Assert.True(p.AgentPresenceCounts);
        Assert.True(p.ReconRevealsFacilities); Assert.False(p.PresenceRevealsHq); Assert.Equal(SectorSuspicion.Off, p.SectorSuspicion);
        Assert.Equal(30, p.ObservedWindowDays); Assert.True(p.SingleHqCleanup);
        Assert.Equal(new[] { "The Shoal" }, p.StartFullCopyHand); Assert.Empty(p.StartFullCopyCollective);
        Assert.Equal((4, 1, 4, 1, 0, 1), (p.EspionageRichSpread, p.EspionageRichBase, p.EspionageCapitalSpread, p.EspionageCapitalBase, p.EspionageOtherSpread, p.EspionageOtherBase));
        var h = new HqParams();
        Assert.Equal((100, 0, false, true), (h.Speed, h.MoveCost, h.UsesFacilitySlot, h.DestinationMustBeHeld));
        Assert.Equal(HqDepartureOperands.Blockaders, h.DepartureOperands);
        Assert.Equal((100, 5, 2, 31), (h.DepartureBase, h.DepartureShipWeight, h.DepartureFighterWeight, h.DepartureLimit));
        Assert.True(h.IgnoresForcedStop);
        Assert.Equal(-10, h.ArrivalSupportDelta);
        Assert.Equal(HqLostOnHolderChange.Flee, h.LostOnHolderChange);
        Assert.Equal(HqLostDestination.DestroyOnArrival, h.LostDestination);
        var s = new GameSettings();
        Assert.Null(s.Fog); Assert.Null(s.Hq);
        var host = GameHost.New(1);
        Assert.Equal(p, host.Fog.Params with { StartFullCopyHand = p.StartFullCopyHand, StartFullCopyCollective = p.StartFullCopyCollective });
        Assert.Equal(h, host.Fog.HqRules);
    }

    [Fact]
    public void SettingsReachTheModuleOnNewAndLoad()
    {
        var s = new GameSettings { Fog = new FogParams { Ruleset = FogRuleset.Classic, ObservedWindowDays = 12 }, Hq = new HqParams { MoveCost = 5 } };
        var h = GameHost.New(1, s);
        var l = GameHost.Load(h.Save(), s);
        foreach (var x in new[] { h, l })
        {
            Assert.Equal(FogRuleset.Classic, x.Fog.Params.Ruleset);
            Assert.Equal(12, x.Fog.Params.ObservedWindowDays);
            Assert.Equal(5, x.Fog.HqRules.MoveCost);
        }
    }

    [Fact]
    public void ReturnOnLostDestinationIsNotBuilt() =>
        Assert.Throws<NotSupportedException>(() => GameHost.New(1, new GameSettings { Hq = new HqParams { LostDestination = HqLostDestination.Return } }));

    // HqMoveCost above 0 (a data value, DEFAULT 0): paid at acceptance, NoFunds when short. rule: m5-fog-and-hq §F6 Cost.
    [Fact]
    public void MoveCostIsPaidOrRefused()
    {
        var w = new FogWorld(new GameSettings { Hq = new HqParams { MoveCost = 25 } });
        w.H.Economy.SetPools(Side.Collective, 0, 20);
        Assert.Equal(HqRejectReason.NoFunds, w.TryMoveHq(Side.Collective, FogWorld.R3));
        w.H.Economy.SetPools(Side.Collective, 0, 30);
        Assert.Null(w.TryMoveHq(Side.Collective, FogWorld.R3));
        Assert.Equal(5, w.Game.State.Economy.Collective.Refined);
    }
}
