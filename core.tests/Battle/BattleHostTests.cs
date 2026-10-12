using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Repair;
using Rebellion.Core.Setup;

namespace Rebellion.Core.Tests.Battle;

// The battle module wired into GameHost (slot 5 before 5.1 and 5.2; the repair slot 6.8 reads FoughtThisStep).
public class BattleHostTests
{
    // m4-retreat-damage-repair section 5 (not in a battle this Step) with m4-battle-classic section 1: a ship that fought today at an own yard is repaired from the next day
    [Fact]
    public void ShipThatFoughtTodayAtAnOwnYardIsNotRepairedThatDay()
    {
        var h = GameHost.New(1, new GameSettings { IncidentsEnabled = false, Economy = new EconomyParams { ScrapPeriod = 1_000_000 } });
        const string yard = "Kuat";
        h.Fleets.SetOwner(yard, Side.Empire);
        h.Economy.AddFacility(yard, FacilityKind.OrbitalShipyard);
        int f = h.Fleets.AddFleet(Side.Empire, "F", new[] { "Imperial Star Destroyer" }, yard);
        var isd = h.Game.State.Economy.Objects[h.Game.State.Fleets[f].Ships[0]];
        isd.Hull = 1790;
        h.Fleets.AddFleet(Side.Alliance, "Raiders", new[] { "Corellian Corvette", "Corellian Corvette" }, yard);

        h.Game.Step(); // the battle day: the ISD fights (the corvettes retreat or die), no repair
        Assert.Single(h.Game.Events.OfType<Rebellion.Core.Battle.BattleEnded>());
        Assert.Empty(h.Game.Events.OfType<ShipRepaired>());
        Assert.True(isd.Hull <= 1790);
        int afterBattle = isd.Hull;
        h.Game.Step(); // no battle now: the repair runs
        Assert.Single(h.Game.Events.OfType<ShipRepaired>());
        Assert.True(isd.Hull > afterBattle);
    }

    // m4-battle-classic section 1.4: the host attaches the module and OnContact is part of the saved state
    [Fact]
    public void HostExposesBattlesAndSavesOnContact()
    {
        var h = GameHost.New(1);
        Assert.NotNull(h.Battles);
        h.Battles.SubmitSetOnContact(Side.Alliance, Rebellion.Core.Battle.OnContactMode.Retreat);
        h.Game.Step();
        var loaded = GameHost.Load(h.Save());
        Assert.Equal(Rebellion.Core.Battle.OnContactMode.Retreat, loaded.Battles.OnContactOf(Side.Alliance));
        Assert.Equal(h.StateHash(), loaded.StateHash());
    }
}
