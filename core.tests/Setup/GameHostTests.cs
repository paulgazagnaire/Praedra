using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Setup;
using Rebellion.Core.Support;
using Rebellion.Core.Tests.Control;

namespace Rebellion.Core.Tests.Setup;

// P2.5: the composition root. engine/03 (modules attach to one Game); each module's own switches keep their DEFAULT through GameSettings.
public class GameHostTests
{
    [Fact]
    public void GameDataLoadsEveryFile()
    {
        var d = GameData.Default;
        Assert.Equal(555, d.Galaxy.Systems.Count);
        Assert.Equal(7, d.StartingFacilities.Count);
        Assert.Equal(d.Galaxy.Systems.Count, d.Slots.Count);
        Assert.True(d.Ships.ContainsKey(d.Blockade.EconomicShipClass));
        Assert.Equal(6, d.Costs.Get("Imperial Army Regiment").Cost); // costs file is read
        Assert.Equal(10, d.Weights.Weight10("Imperial Army Regiment"));
        Assert.Same(GameData.Default, GameData.Default);
    }

    // every setting's default is the module's own default (so a default host behaves like a hand-attached game)
    [Fact]
    public void SettingDefaultsAreTheModuleDefaults()
    {
        var g = new Game(1);
        var data = GameData.Default;
        var fleets = FleetModule.Attach(g, data.Galaxy, data.Ships, data.Blockade);
        var eco = EconomyModule.Attach(g, data.Galaxy, data.Slots, data.Costs, fleets);
        var sup = SupportModule.Attach(g, data.Galaxy, eco, fleets, data.Weights);
        var con = ConstructionModule.Attach(g, fleets, eco, support: sup);
        var ctl = ControlModule.Attach(g, data.Galaxy, fleets, eco, sup, con);

        var s = new GameSettings();
        Assert.Equal(fleets.Trigger, s.ForcedStopTrigger);
        Assert.Equal(eco.IncidentsEnabled, s.IncidentsEnabled);
        Assert.Equal(eco.BlockadeStopsMining, s.BlockadeStopsMining);
        Assert.Equal(eco.SuspendedLeaveCapacity, s.SuspendedLeaveCapacity); Assert.False(s.SuspendedLeaveCapacity); // Blockade pause, 2026-10-18
        Assert.Equal(eco.MineStopMode, s.MineStopMode); Assert.Equal(StopMode.Pause, s.MineStopMode);
        Assert.Equal(con.YardStopMode, s.YardStopMode); Assert.Equal(StopMode.Pause, s.YardStopMode);
        Assert.Equal(eco.AutoscrapMode, s.AutoscrapMode);
        Assert.Equal(eco.SlotSource, s.SlotSource);
        Assert.Equal(sup.Ruleset, s.SupportRuleset);
        Assert.Equal(sup.BlockadeSupportEffect, s.BlockadeSupportEffect);
        Assert.Equal(con.DeliveryForcedStop, s.DeliveryForcedStop);
        Assert.Equal(con.LostDestination, s.LostDestination);
        Assert.Equal(con.UnitDestinationMustBeControlled, s.UnitDestinationMustBeControlled);
        Assert.Equal(con.RespectResearchOrder, s.RespectResearchOrder);
        Assert.Equal(con.UseClassicBlockadeFlag, s.UseClassicBlockadeFlag);
        Assert.Equal(ctl.DebugOrdersEnabled, s.DebugOrdersEnabled);
        Assert.Equal(fleets.DebugOrdersEnabled, s.DebugOrdersEnabled);
        Assert.Equal(con.DeathStarClassBuildable, s.DeathStarClassBuildable);
        var trp = Rebellion.Core.Troops.TroopModule.Attach(g, fleets, data.Troops);
        Assert.Equal(trp.UnloadOnlyAtHeldSystem, s.UnloadOnlyAtHeldSystem);
        Assert.Null(s.Economy); Assert.Null(s.Support); Assert.Null(s.Construction);

        var h = GameHost.New(1);
        // the number tables are records holding lists, so compare them with the list fields set equal
        Assert.Equal(eco.Params with { SmugglingTiers = h.Economy.Params.SmugglingTiers }, h.Economy.Params);
        Assert.Equal(sup.Params with { Table1 = h.Support.Params.Table1, Table2 = h.Support.Params.Table2 }, h.Support.Params);
        Assert.Equal(con.Params, h.Construction.Params);
        // research: null = the module's defaults (m5-research §Defaults); the gate is on
        Assert.Null(s.Research);
        Assert.Equal(new Rebellion.Core.Research.ResearchParams(), h.Research.Params);
        Assert.True(s.RespectResearchOrder);
    }

    [Fact]
    public void EverySettingReachesItsModuleOnNewAndOnLoad()
    {
        var s = new GameSettings
        {
            ForcedStopTrigger = ForcedStopTrigger.AnyArmedFleet, IncidentsEnabled = false, BlockadeStopsMining = false,
            AutoscrapMode = AutoscrapMode.Smart, SlotSource = SlotSource.Rolled, SupportRuleset = GarrisonRuleset.New,
            BlockadeSupportEffect = false, DeliveryForcedStop = false, LostDestination = LostDestinationRule.Refuse,
            UnitDestinationMustBeControlled = false, RespectResearchOrder = false, UseClassicBlockadeFlag = true, DebugOrdersEnabled = true,
            DeathStarClassBuildable = false, UnloadOnlyAtHeldSystem = false,
            SuspendedLeaveCapacity = true, MineStopMode = StopMode.Abandon, YardStopMode = StopMode.Abandon,
            Economy = new EconomyParams { ScrapPeriod = 7 }, Support = new SupportParams { ErosionPeriod = 11 },
            Construction = new ConstructionParams { StandardCycleDays = 3 },
            Research = new Rebellion.Core.Research.ResearchParams { FacilityResearch = false, ResearchCostScale = Rebellion.Core.Research.ResearchScale.Fixed },
        };
        void Check(GameHost h)
        {
            Assert.Equal(ForcedStopTrigger.AnyArmedFleet, h.Fleets.Trigger);
            Assert.False(h.Economy.IncidentsEnabled); Assert.False(h.Economy.BlockadeStopsMining);
            Assert.Equal(AutoscrapMode.Smart, h.Economy.AutoscrapMode); Assert.Equal(SlotSource.Rolled, h.Economy.SlotSource);
            Assert.Equal(GarrisonRuleset.New, h.Support.Ruleset); Assert.False(h.Support.BlockadeSupportEffect);
            Assert.False(h.Construction.DeliveryForcedStop); Assert.Equal(LostDestinationRule.Refuse, h.Construction.LostDestination);
            Assert.False(h.Construction.UnitDestinationMustBeControlled); Assert.False(h.Construction.RespectResearchOrder);
            Assert.False(h.Research.Params.FacilityResearch); Assert.Equal(Rebellion.Core.Research.ResearchScale.Fixed, h.Research.Params.ResearchCostScale);
            Assert.True(h.Construction.UseClassicBlockadeFlag); Assert.True(h.Control.DebugOrdersEnabled);
            Assert.True(h.Fleets.DebugOrdersEnabled); Assert.False(h.Construction.DeathStarClassBuildable);
            Assert.False(h.Troops.UnloadOnlyAtHeldSystem);
            Assert.True(h.Economy.SuspendedLeaveCapacity); Assert.Equal(StopMode.Abandon, h.Economy.MineStopMode); Assert.Equal(StopMode.Abandon, h.Construction.YardStopMode);
            Assert.Equal(7, h.Economy.Params.ScrapPeriod); Assert.Equal(11, h.Support.Params.ErosionPeriod); Assert.Equal(3, h.Construction.Params.StandardCycleDays);
        }
        var made = GameHost.New(5, s);
        Check(made);
        Check(GameHost.Load(made.Save(), s));
    }

    // cross-wiring: support into economy and construction, control into the others
    [Fact]
    public void ModulesAreWiredToEachOther()
    {
        var h = GameHost.New(1);
        // support -> economy: the Death Star flag is the support module's
        Assert.False(h.Economy.IsDeathStarSector("Coruscant"));
        h.Support.SetDeathStarSystem("Coruscant", true);
        Assert.True(h.Economy.IsDeathStarSector("Coruscant"));
        // support -> construction: an uprising stops yards
        Assert.False(h.Construction.IsInUprising("Kuat"));
        h.Game.State.Support.Systems["Kuat"] = new SystemSupportState { Uprising = true, Rebels = Side.Alliance, ErosionDue = 99, IncidentDue = 99 };
        Assert.True(h.Construction.IsInUprising("Kuat"));
        // control: the debug order is accepted only when the setting is on
        Assert.Equal(RejectReason.DebugDisabled, RunDebugCapture(GameHost.New(1)));
        Assert.Null(RunDebugCapture(GameHost.New(1, new GameSettings { DebugOrdersEnabled = true })));
    }

    private static string? RunDebugCapture(GameHost h)
    {
        h.Fleets.SetOwner("Kuat", Side.Alliance);
        long seq = h.Control.SubmitCapture(Side.Empire, "Kuat") /* (B5a, 2026-10-17) Derived control: troops take the world */;
        h.Game.Step();
        return h.Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == seq)?.Reason;
    }

    // construction -> control: capturing a system cancels the projects standing on it, through the host's wiring
    [Fact]
    public void CaptureCancelsTheLosersProjectsThroughTheHostWiring()
    {
        var h = GameHost.New(1, new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true });
        h.Fleets.SetOwner("Sullust", Side.Alliance);
        h.Economy.AddFacility("Sullust", FacilityKind.TrainingFacility);
        h.Economy.SetPools(Side.Alliance, 0, 50);
        h.Construction.RefuseOverdrawOrders = false; // B5b 2026-10-18: no mines here (capacity 0); the upkeep refusal is not what this checks
        long build = h.Construction.SubmitBuild(Side.Alliance, "Sullust", YardFamily.Training, "Alliance Army Regiment", 2);
        h.Game.Step();
        Assert.NotNull(h.Construction.ProjectAt("Sullust", YardFamily.Training));
        h.Control.SubmitCapture(Side.Empire, "Sullust") /* (B5a, 2026-10-17) Derived control: troops take the world */;
        h.Game.Step();
        Assert.Null(h.Construction.ProjectAt("Sullust", YardFamily.Training));
        Assert.Contains(h.Game.Events, e => e is ProjectCancelled { Reason: "SystemLost" });
        Assert.NotEqual(0, build);
    }

    // new and load give games that run the same days
    [Fact]
    public void NewThenSaveThenLoadRunsTheSameAsNew()
    {
        var a = GameHost.New(9); a.Economy.SetupNewGame();
        for (int i = 0; i < 50; i++) a.Game.Step();
        var b = GameHost.Load(a.Save());
        Assert.Equal(a.StateHash(), b.StateHash());
        for (int i = 0; i < 50; i++) { a.Game.Step(); b.Game.Step(); }
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    // ---------------------------------------------------------------- the campaign layout (a test scenario, not the real start)

    [Fact]
    public void CampaignLayoutIsTwoSeparateRegionsWithSlotsRespected()
    {
        var c = TestCampaign.New(1);
        var owners = c.Game.State.SystemOwners;
        int e = owners.Count(kv => kv.Value == Side.Empire), a = owners.Count(kv => kv.Value == Side.Alliance);
        Assert.InRange(e, 15, 20); Assert.InRange(a, 15, 20);
        // the starting-facilities rows inside the regions are placed: Kuat and Corellia (Empire), Sullust (Neutral row, held by the Alliance here)
        bool Has(string sys, FacilityKind k) => c.Host.Economy.Objects.Any(o => o.System == sys && o.Class == FacilityKinds.ClassName(k) && o.Completed);
        Assert.True(Has("Kuat", FacilityKind.OrbitalShipyard) && Has("Corellia", FacilityKind.OrbitalShipyard) && Has("Sullust", FacilityKind.OrbitalShipyard));
        Assert.False(Has("Fondor", FacilityKind.OrbitalShipyard)); // outside both regions: not used
        CampaignInvariants.Check(c.Host, "day 0");
        // every region system holds only its side's units
        foreach (var o in c.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.System != null))
            Assert.Equal(o.Side, owners[o.System!]);
        Assert.Equal(Side.Empire, owners["Coruscant"]);
        Assert.Equal(Side.Alliance, owners["Sullust"]);
    }

    // the script is a function of the day: the same orders on the same day whatever came before
    [Fact]
    public void ScriptedOrdersDependOnTheDayOnly()
    {
        var a = TestCampaign.New(1); var b = TestCampaign.New(2);
        foreach (int day in new[] { 5, 10, 20, 60, 380 })
        {
            int before = a.Game.Queue.Count; a.SubmitScriptFor(day);
            int added = a.Game.Queue.Count - before;
            int beforeB = b.Game.Queue.Count; b.SubmitScriptFor(day);
            Assert.Equal(added, b.Game.Queue.Count - beforeB);
            Assert.True(added > 0, $"day {day} has orders");
        }
    }
}
