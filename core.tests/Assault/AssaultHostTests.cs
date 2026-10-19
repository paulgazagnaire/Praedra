using Praedra.Core.Assault;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Setup;

namespace Praedra.Core.Tests.Assault;

// m3-assault wiring: GameHost settings and save/load, determinism (A20) and the scripted assault of the test campaign.
public class AssaultHostTests
{
    // CLAUDE.md hard rule 7: the settings' default is the module's own default, and a host has the module
    [Fact]
    public void AssaultSettingDefaultsToTheModuleDefaults()
    {
        Assert.Null(new GameSettings().Assault);
        Assert.Equal(new AssaultParams(), GameHost.New(1).Assault.Params);
        var s = new GameSettings { Assault = new AssaultParams { LandingCap = 3, Passes = 0, CollateralMode = CollateralMode.PerDuelChance } };
        Assert.Equal(s.Assault, GameHost.New(1, s).Assault.Params);
        Assert.Equal(s.Assault, GameHost.Load(GameHost.New(1, s).Save(), s).Assault.Params);
    }

    private static GameHost Scenario(ulong seed)
    {
        var h = GameHost.New(seed, new GameSettings { IncidentsEnabled = false });
        h.Fleets.SetOwner("Denon", Side.Alliance);
        h.Economy.SetAllianceSupport("Denon", 100);
        for (int i = 0; i < 5; i++) h.Economy.AddObject(Side.Alliance, "Alliance Army Regiment", "Denon");
        h.Economy.AddFacility("Denon", FacilityKind.Kdy150);
        int f = h.Fleets.AddFleet(Side.Empire, "Landing", new[] { "Victory Destroyer", "Victory Destroyer", "Victory Destroyer" }, "Denon");
        var ships = h.Game.State.Fleets[f].Ships;
        for (int i = 0; i < 6; i++)
        {
            var r = h.Economy.Find(h.Economy.AddObject(Side.Empire, "Stormtrooper Regiment", null))!;
            r.AboardShip = ships[i / 2]; r.System = null;
        }
        h.Assault.SubmitAssault(Side.Empire, f, "Denon");
        return h;
    }

    // m3-assault A20: the same seed and orders twice give the same events and state hash
    [Fact]
    public void A20_SameSeedSameAssault()
    {
        var a = Scenario(20); var b = Scenario(20);
        a.Game.Step(); b.Game.Step();
        Assert.Single(a.Game.Events.OfType<AssaultResolved>());
        Assert.True(a.Game.Events.SequenceEqual(b.Game.Events));
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.Equal(a.Game.Rng.Export(), b.Game.Rng.Export());
        var c = Scenario(21);
        c.Game.Step();
        Assert.NotEqual(a.StateHash(), c.StateHash());
    }

    // m3-assault: an order queued before a save is processed after the load with the uninterrupted game's result; nothing is pending at a save point
    [Fact]
    public void QueuedAssaultRunsAfterSaveAndLoad()
    {
        var whole = Scenario(7); whole.Game.Step();
        var first = Scenario(7);
        var loaded = GameHost.Load(first.Save(), new GameSettings { IncidentsEnabled = false });
        loaded.Game.Step();
        Assert.Single(loaded.Game.Events.OfType<AssaultStarted>());
        Assert.Equal(whole.StateHash(), loaded.StateHash());
        Assert.True(whole.Game.Events.SequenceEqual(loaded.Game.Events));
        Assert.Empty(loaded.Game.Queue);
        // a Step later the order is gone: the flag was cleared
        loaded.Game.Step();
        Assert.Single(loaded.Game.Events.OfType<AssaultResolved>());
    }

    // m3-assault: the assault order serializes with the type tag "Assault"
    [Fact]
    public void AssaultOrderRoundTripsThroughTheSave()
    {
        var h = Scenario(3);
        var json = h.Save();
        Assert.Contains("\"Assault\"", json);
        var l = GameHost.Load(json);
        Assert.Contains(l.Game.Queue, o => o is AssaultOrder { System: "Denon", Side: Side.Empire });
    }

    // the test campaign's one scripted assault (day 250, the interdiction fleet lands on Kinyen), with every daily invariant green
    [Fact]
    public void CampaignScriptedAssaultResolvesOnItsDay()
    {
        var run = CampaignHarness.Checked(1, Praedra.Core.Support.GarrisonRuleset.Classic);
        var started = Assert.Single(run.Events.OfType<AssaultStarted>());
        Assert.Equal(TestCampaign.AssaultDay + 1, started.Day);
        Assert.Equal((TestCampaign.BlockadedSystem, Side.Empire, TestCampaign.AssaultRegiments), (started.System, started.Side, started.A0));
        var res = Assert.Single(run.Events.OfType<AssaultResolved>());
        Assert.Equal(started.System, res.System);
        Assert.Empty(run.Events.OfType<AssaultCancelled>());
        Assert.DoesNotContain(run.Events.OfType<OrderRejected>(), e => e.Reason is AssaultReject.NoTroops or AssaultReject.OwnSystem or AssaultReject.EnemyFleetPresent or AssaultReject.FullyShielded);
        Assert.Equal(res.Result == "Won", run.Events.OfType<Praedra.Core.Control.SystemCaptured>().Any(c => c.Cause == Praedra.Core.Control.CaptureCause.Assault));
    }
}
