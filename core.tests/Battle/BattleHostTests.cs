using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Repair;
using Rebellion.Core.Setup;

namespace Rebellion.Core.Tests.Battle;

// The battle module wired into GameHost (slot 5 before 5.1 and 5.2; repair at slot 6.8).
public class BattleHostTests
{
    // m4-retreat-damage-repair section 5 (B3 section 15 replaces "no repair on a battle day"): the repair timer of a ship at its own yard runs from the Step it
    // is seen damaged, so a ship that fought today has its first 2-day turn the day after
    [Fact]
    public void ShipThatFoughtAtItsOwnYardRepairsOnTheFastTimer()
    {
        var h = GameHost.New(1, new GameSettings { IncidentsEnabled = false, Economy = new EconomyParams { ScrapPeriod = 1_000_000 } });
        const string yard = "Kuat";
        h.Fleets.SetOwner(yard, Side.Empire);
        h.Economy.AddFacility(yard, FacilityKind.OrbitalShipyard);
        for (int i = 0; i < 3; i++) h.Economy.AddObject(Side.Empire, "Imperial Army Regiment", yard); // no uprising: the yard is usable
        int f = h.Fleets.AddFleet(Side.Empire, "F", new[] { "Imperial Star Destroyer" }, yard);
        var isd = h.Game.State.Economy.Objects[h.Game.State.Fleets[f].Ships[0]];
        isd.Hull = 1790;
        h.Fleets.AddFleet(Side.Alliance, "Raiders", new[] { "Corellian Corvette", "Corellian Corvette" }, yard);

        h.Game.Step(); // the battle day (hook time 1): the timer starts, nothing repaired yet
        Assert.Single(h.Game.Events.OfType<Rebellion.Core.Battle.BattleEnded>());
        Assert.Empty(h.Game.Events.OfType<ShipRepaired>());
        int afterBattle = isd.Hull;
        h.Game.Step(); // hook time 2: the first turn
        Assert.Single(h.Game.Events.OfType<Rebellion.Core.Battle.BattleEnded>());
        Assert.Equal(2, h.Game.Events.OfType<ShipRepaired>().Single().Day);
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
