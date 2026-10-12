using Rebellion.Core.Clock;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Setup;
using Rebellion.Core.Support;
using Rebellion.Core.Tests.Setup;

namespace Rebellion.Core.Tests.Defenses;

// P3.4 wiring: GameHost settings and save/load, and the scripted bombardment of the test campaign (m3-bombardment).
public class BombardmentHostTests
{
    // CLAUDE.md hard rule 7: the settings' defaults are the modules' own defaults, and a host has both modules
    [Fact]
    public void DefenseAndBombardSettingsDefaultToTheModuleDefaults()
    {
        var s = new GameSettings();
        Assert.Null(s.Defense); Assert.Null(s.Bombard);
        var h = GameHost.New(1);
        Assert.Equal(new DefenseParams(), h.Defenses.Params);
        Assert.Equal(new BombardParams(), h.Bombardment.Params);
        Assert.Equal(2, h.Defenses.Params.AssaultGateShields);
        Assert.False(h.Defenses.Params.DeathStarShieldCountsForGate);
        Assert.Equal(40, h.Bombardment.Params.AdmiralStep);
        Assert.Equal(-20, h.Bombardment.Params.CivilianSupportChange);
        Assert.Same(GameData.Default.Defenses, h.Defenses.Data);
    }

    [Fact]
    public void SettingsReachTheModulesOnNewAndOnLoad()
    {
        var s = new GameSettings
        {
            Defense = new DefenseParams { AssaultGateShields = 3, DeathStarShieldCountsForGate = true },
            Bombard = new BombardParams { CivilianSupportChange = -5, AdmiralStep = 10 },
        };
        var h = GameHost.New(1, s);
        Assert.Equal(s.Defense, h.Defenses.Params);
        Assert.Equal(s.Bombard, h.Bombardment.Params);
        var l = GameHost.Load(h.Save(), s);
        Assert.Equal(s.Defense, l.Defenses.Params);
        Assert.Equal(s.Bombard, l.Bombardment.Params);
    }

    // m3-bombardment B21 through the host: an order queued before a save is processed after the load, with the uninterrupted game's result
    [Fact]
    public void QueuedBombardOrderRunsAfterSaveAndLoad()
    {
        GameHost Make()
        {
            var h = GameHost.New(5, new GameSettings { IncidentsEnabled = false });
            h.Fleets.SetOwner("Denon", Side.Alliance);
            h.Economy.SetAllianceSupport("Denon", 100);
            for (int i = 0; i < 4; i++) h.Economy.AddObject(Side.Alliance, "Alliance Army Regiment", "Denon");
            int f = h.Fleets.AddFleet(Side.Empire, "Raid", new[] { "Victory Destroyer", "Victory Destroyer" }, "Denon");
            h.Bombardment.SubmitBombard(Side.Empire, f, "Denon", BombardCategory.Military);
            return h;
        }
        var whole = Make();
        whole.Game.Step();
        var first = Make();
        var loaded = GameHost.Load(first.Save(), new GameSettings { IncidentsEnabled = false });
        loaded.Game.Step();
        Assert.Single(loaded.Game.Events.OfType<BombardmentStarted>());
        Assert.Equal(whole.StateHash(), loaded.StateHash());
        Assert.True(whole.Game.Events.SequenceEqual(loaded.Game.Events));
    }

    // the test campaign's one scripted bombardment (day 200, the interdiction fleet shells Kinyen), with every daily invariant green
    [Fact]
    public void CampaignScriptedBombardmentResolvesOnItsDay()
    {
        var run = CampaignHarness.Checked(1, GarrisonRuleset.Classic);
        var started = Assert.Single(run.Events.OfType<BombardmentStarted>());
        Assert.Equal(TestCampaign.BombardDay + 1, started.Day);
        Assert.Equal((TestCampaign.BlockadedSystem, Side.Empire, BombardCategory.General, 4, 0, 4), (started.System, started.Side, started.Category, started.P, started.S, started.Hits));
        var resolved = Assert.Single(run.Events.OfType<BombardmentResolved>());
        Assert.Equal(started.System, resolved.System);
        Assert.Equal(started.Hits, run.Events.OfType<TargetHit>().Count()); // the pool (regiments, a battery, mines, yards, Energy, Raw) never runs dry in 4 hits
        var fire = Assert.Single(run.Events.OfType<BatteryReturnFire>());
        Assert.Single(fire.Facilities.Items);
        Assert.Empty(run.Events.OfType<BombardCancelled>());
        Assert.DoesNotContain(run.Events.OfType<OrderRejected>(), e => e.Reason is BombardReject.OwnSystem or BombardReject.EnemyFleetPresent or BombardReject.NoBombardPower);
    }
}
