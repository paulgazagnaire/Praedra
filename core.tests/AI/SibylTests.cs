using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// m6-advisor-sibyl S1 to S8: Sibyl, the Hand player's advisor, on a Medium start (seed 3). Nothing else plays: the Collective gives no orders.
public class SibylTests
{
    private static GameHost NewHost() =>
        NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA });

    private static IEnumerable<EconObject> HandFacilities(GameHost h, FacilityKind k) =>
        h.Economy.Objects.Where(o => o.Completed && o.Class == FacilityKinds.ClassName(k) && o.System != null
            && h.Game.State.SystemOwners.TryGetValue(o.System, out var s) && s == Side.Hand).OrderBy(o => o.Id);

    /// <summary>A held, populated Hand world with no yard of any kind, ordinal first.</summary>
    private static string QuietWorld(GameHost h) =>
        h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Hand).Select(kv => kv.Key)
            .First(s => h.Economy.SlotsOf(s).Populated && !h.Economy.Objects.Any(o => o.System == s && o.Category == ObjectCategory.Facility
                && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)));

    /// <summary>Puts the world in an uprising: one Tranches standing, Hand support 45 (need 2, doubled to 4 once the uprising starts).</summary>
    private static void Riot(GameHost h, string world)
    {
        h.Control.DebugOrdersEnabled = true;
        h.Control.SubmitDebugSetTroops(Side.Hand, world, Side.Hand, "Tranches", 1);
        h.Control.SubmitDebugSetSupport(Side.Hand, world, 55);
        h.Game.Step();
        Assert.True(h.Support.IsUprising(world));
        Assert.Equal(4, h.Support.RequiredGarrison(world));
    }

    // S1
    [Fact]
    public void S1_BothSwitchesStartOff_AndSibylOrdersNothing()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        Assert.False(s.IsOn(SibylJob.ManageGarrisons));
        Assert.False(s.IsOn(SibylJob.ManageProduction));
        for (int d = 0; d < 5; d++) { Assert.Empty(s.PlanDay()); h.Game.Step(); }
        Assert.DoesNotContain(h.Game.State.Markers.Keys, k => k.StartsWith("sibyl.", StringComparison.Ordinal));
    }

    // S8, S2: the switch counts from the next day's plan; then a world short of its garrison gets regiments from a training facility
    [Fact]
    public void S2_S8_GarrisonsOn_FromTheNextDay_OrdersTheRegimentsAWorldIsShort()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string world = QuietWorld(h);
        s.Submit(SibylJob.ManageGarrisons, true);
        Assert.Empty(s.PlanDay()); // S8: not on yet today
        Riot(h, world);
        Assert.True(s.IsOn(SibylJob.ManageGarrisons));
        var orders = s.PlanDay();
        var o = Assert.Single(orders, x => x.Detail.EndsWith(" for " + world, StringComparison.Ordinal));
        Assert.StartsWith("3 x Tranches at ", o.Detail); // need 4 in the uprising, 1 standing
        Assert.All(orders, x => Assert.True(x.Detail.Contains("Tranches") || x.Detail.StartsWith("Training Facility at ", StringComparison.Ordinal), x.Detail));
        h.Game.Step();
        Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == o.Sequence);
    }

    // S3: with one idle training facility, the uprising with the fewest regiments standing gets it (a short world rises the same day, so the
    // ordering that matters in play is among worlds in uprising: fewest regiments, then name)
    [Fact]
    public void S3_OneIdleFacility_GoesToTheUprisingWithFewestRegiments()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        var quiet = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Hand).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal)
            .Where(w => h.Economy.SlotsOf(w).Populated && !h.Economy.Objects.Any(o => o.System == w && o.Category == ObjectCategory.Facility
                && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k))).Take(2).ToList();
        string few = quiet[1], more = quiet[0]; // the one with fewer regiments is second by name, so the name is not what decides
        var training = HandFacilities(h, FacilityKind.TrainingFacility).Select(o => o.System!).Distinct().ToList();
        foreach (var t in training.Skip(1)) h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 1, t); // busy: only the first is idle
        s.Submit(SibylJob.ManageGarrisons, true);
        h.Control.DebugOrdersEnabled = true;
        h.Control.SubmitDebugSetTroops(Side.Hand, few, Side.Hand, "Tranches", 1);
        h.Control.SubmitDebugSetTroops(Side.Hand, more, Side.Hand, "Tranches", 2);
        h.Control.SubmitDebugSetSupport(Side.Hand, few, 55);
        h.Control.SubmitDebugSetSupport(Side.Hand, more, 65); // Hand support 35: need 3, so 2 standing rise too
        h.Game.Step();
        Assert.True(h.Support.IsUprising(few) && h.Support.IsUprising(more));
        var orders = s.PlanDay().Where(x => x.Detail.Contains(" x Tranches at ", StringComparison.Ordinal)).ToList();
        var o = Assert.Single(orders);
        Assert.Equal($"3 x Tranches at {training[0]} for {few}", o.Detail);
    }

    // S4: regiments already on order count
    [Fact]
    public void S4_RegimentsOnOrderCount_NoSecondOrder()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string world = QuietWorld(h);
        s.Submit(SibylJob.ManageGarrisons, true);
        Riot(h, world);
        Assert.Contains(s.PlanDay(), x => x.Detail.EndsWith(" for " + world, StringComparison.Ordinal));
        h.Game.Step();
        Assert.DoesNotContain(s.PlanDay(), x => x.Detail.EndsWith(" for " + world, StringComparison.Ordinal));
    }

    // S5, S12: production builds construction yards while the Hand has few for its worlds, then mines and refineries; never anything else
    [Fact]
    public void S5_S12_ProductionOn_BuildsConstructionYardsThenMinesAndRefineries()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        var orders = s.PlanDay();
        Assert.NotEmpty(orders);
        Assert.StartsWith("Construction Yard at ", orders[0].Detail); // S12: one yard for many worlds, refined above 40, upkeep room
        h.Game.Step();
        Assert.All(orders, x => Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == x.Sequence));
        var seen = new List<string>();
        for (int d = 0; d < 400; d++)
        {
            foreach (var x in s.PlanDay())
            {
                Assert.True(x.Detail.StartsWith("Mine at ", StringComparison.Ordinal) || x.Detail.StartsWith("Refinery at ", StringComparison.Ordinal)
                    || x.Detail.StartsWith("Construction Yard at ", StringComparison.Ordinal), x.Detail);
                seen.Add(x.Detail);
            }
            // one order per idle construction yard system at most
            Assert.Equal(s.Submitted.Count, s.Submitted.Select(x => x.Detail[(x.Detail.LastIndexOf(" from ", StringComparison.Ordinal) + 6)..]).Distinct().Count());
            h.Game.Step();
            Assert.All(s.Submitted, x => Assert.DoesNotContain(h.Game.Events.OfType<OrderRejected>(), e => e.Sequence == x.Sequence));
        }
        Assert.Contains(seen, x => x.StartsWith("Mine at ", StringComparison.Ordinal) || x.StartsWith("Refinery at ", StringComparison.Ordinal));
        Assert.True(HandFacilities(h, FacilityKind.ConstructionYard).Count() > 1);
    }

    // S11: garrisons short and every training facility busy: an idle construction yard builds a training facility (production off)
    [Fact]
    public void S11_ShortAndEveryTrainingFacilityBusy_BuildsATrainingFacility()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string world = QuietWorld(h);
        foreach (var t in HandFacilities(h, FacilityKind.TrainingFacility).Select(o => o.System!).Distinct())
            h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 1, t);
        s.Submit(SibylJob.ManageGarrisons, true);
        Riot(h, world);
        var orders = s.PlanDay();
        Assert.DoesNotContain(orders, x => x.Detail.Contains(" x Tranches at ", StringComparison.Ordinal));
        var tf = Assert.Single(orders, x => x.Detail.StartsWith("Training Facility at ", StringComparison.Ordinal));
        h.Game.Step();
        Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == tf.Sequence);
    }

    // S13: the buffer: a calm world at support 62 with no regiments gets one (the rule's need there is 0)
    [Fact]
    public void S13_CalmWorldNearAStep_GetsOneRegiment()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string world = QuietWorld(h);
        s.Submit(SibylJob.ManageGarrisons, true);
        h.Control.DebugOrdersEnabled = true;
        h.Control.SubmitDebugSetTroops(Side.Hand, world, Side.Hand, "Tranches", 0);
        h.Control.SubmitDebugSetSupport(Side.Hand, world, 38); // Hand support 62
        h.Game.Step();
        Assert.False(h.Support.IsUprising(world));
        Assert.Equal(0, h.Support.RequiredGarrison(world));
        Assert.Equal(0, h.Support.RegimentCount(world));
        Assert.Contains(s.PlanDay(), x => x.Detail.StartsWith("1 x Tranches at ", StringComparison.Ordinal) && x.Detail.EndsWith(" for " + world, StringComparison.Ordinal));
    }

    // S14 (QA-106): every order of hers that costs upkeep leaves 100 free (two mine and refinery pairs), uprisings included
    [Fact]
    public void S14_HerOrdersLeaveTheUpkeepMargin()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        s.Submit(SibylJob.ManageGarrisons, true);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        int checkedDays = 0;
        for (int d = 0; d < 300; d++)
        {
            int free = h.Economy.Free(Side.Hand);
            int spent = s.PlanDay().Where(x => x.Kind == "Build").Sum(x =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(x.Detail, @"^(?:(\d+) x )?(.+?) at ");
                int n = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1;
                return n * h.Economy.Costs.Get(m.Groups[2].Value).Maintenance;
            });
            if (spent > 0) { Assert.True(free - spent >= 100, $"day {h.Game.Day}: free {free}, spent {spent}"); checkedDays++; }
            h.Game.Step();
        }
        Assert.True(checkedDays > 0);
    }

    // S15: with less than her margin free, an uprising gets no new regiments, and she says so
    [Fact]
    public void S15_NoRoomForAnUprising_IsSaid()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string world = QuietWorld(h);
        string other = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Hand && kv.Key != world).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).First();
        int free = h.Economy.Free(Side.Hand), standing = h.Support.RegimentCount(other);
        s.Submit(SibylJob.ManageGarrisons, true);
        h.Control.DebugOrdersEnabled = true;
        h.Control.SubmitDebugSetTroops(Side.Hand, other, Side.Hand, "Tranches", standing + (free - 60) / 3); // about 60 room left
        Riot(h, world);
        int room = h.Economy.Free(Side.Hand);
        Assert.InRange(room, 0, 99);
        var orders = s.PlanDay();
        Assert.DoesNotContain(orders, x => x.Detail.EndsWith(" for " + world, StringComparison.Ordinal));
        Assert.Contains(world, s.UnpaidUprisings);
    }

    // S6: a yard with the player's own project is left alone
    [Fact]
    public void S6_AYardWithThePlayersProjectIsNotUsed()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        var yard = HandFacilities(h, FacilityKind.ConstructionYard).First().System!;
        h.Construction.SubmitBuild(Side.Hand, yard, YardFamily.Construction, "Sensor Station", 1, yard);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        Assert.NotNull(h.Construction.ProjectAt(yard, YardFamily.Construction));
        Assert.DoesNotContain(s.PlanDay(), x => x.Detail.EndsWith(" from " + yard, StringComparison.Ordinal));
    }

    // S7: the switches are saved with the game, and a loaded game plans the same orders
    [Fact]
    public void S7_SwitchesSurviveSaveAndLoad_AndPlanTheSameOrders()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        s.Submit(SibylJob.ManageGarrisons, true);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        var loaded = GameHost.Load(h.SaveWithSettings());
        var s2 = new Sibyl(loaded, Side.Hand);
        Assert.True(s2.IsOn(SibylJob.ManageGarrisons) && s2.IsOn(SibylJob.ManageProduction));
        Assert.Equal(s.PlanDay().Select(x => x.Detail), s2.PlanDay().Select(x => x.Detail));
    }

    // Switching off stops her
    [Fact]
    public void SwitchingOff_StopsTheOrders()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        Assert.NotEmpty(s.PlanDay());
        s.Submit(SibylJob.ManageProduction, false);
        h.Game.Step();
        Assert.Empty(s.PlanDay());
    }

    // ---- redirect of Sibyl's own regiments in training (m6-advisor-sibyl §Manage Garrisons 8; m2-construction §Redirect training)

    private static string TrainingWorld(GameHost h) => HandFacilities(h, FacilityKind.TrainingFacility).First().System!;

    private static Project TrainingProject(GameHost h, string t) => h.Construction.ProjectAt(t, YardFamily.Training)!;

    // S9: her regiments in training for a quiet world go to the world that rises
    [Fact]
    public void S9_HerRegimentsInTraining_AreRedirectedToTheUprising()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string t = TrainingWorld(h), world = QuietWorld(h);
        s.Submit(SibylJob.ManageGarrisons, true);
        h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 2, t, byAdvisor: true);
        Riot(h, world);
        Assert.Equal(t, TrainingProject(h, t).Destination);
        var orders = s.PlanDay();
        var r = Assert.Single(orders, x => x.Kind == "Redirect");
        Assert.Equal($"2 x Tranches in training at {t} redirected from {t} to {world}", r.Detail);
        h.Game.Step();
        Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == r.Sequence);
        Assert.Equal(world, TrainingProject(h, t).Destination);
    }

    // S10: regiments the player ordered are never redirected
    [Fact]
    public void S10_ThePlayersRegimentsInTraining_AreNotRedirected()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string t = TrainingWorld(h), world = QuietWorld(h);
        s.Submit(SibylJob.ManageGarrisons, true);
        h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 2, t);
        Riot(h, world);
        Assert.DoesNotContain(s.PlanDay(), x => x.Kind == "Redirect");
        h.Game.Step();
        Assert.Equal(t, TrainingProject(h, t).Destination);
    }

    // m2-construction C26: the advisor's regiments in training get a new destination, keep their progress, and leave for it when finished
    [Fact]
    public void C26_RedirectTraining_ChangesTheDestination_AndTheyGoThere()
    {
        var h = NewHost();
        string t = TrainingWorld(h), world = QuietWorld(h);
        h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 2, t, byAdvisor: true);
        for (int d = 0; d < 3; d++) h.Game.Step();
        var p = TrainingProject(h, t);
        int first = p.Units[0], progress = p.Progress;
        long seq = h.Construction.SubmitRedirectTraining(Side.Hand, t, 5, world);
        h.Game.Step();
        Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == seq);
        p = TrainingProject(h, t);
        Assert.Equal(world, p.Destination);
        Assert.Equal(first, p.Units[0]);
        Assert.True(p.Progress >= progress);
        Assert.Null(p.Next);
        for (int d = 0; d < 400 && !h.Game.Events.OfType<UnitCompleted>().Any(e => e.Object == first); d++) h.Game.Step();
        var done = Assert.Single(h.Game.Events.OfType<UnitCompleted>(), e => e.Object == first);
        Assert.Equal(world, done.Destination);
        Assert.False(h.Economy.Objects.Single(o => o.Id == first).ByAdvisor);
    }

    // m2-construction C26 (save): the advisor's mark survives a save and load, and the loaded game can still redirect
    [Fact]
    public void C26_ByAdvisorSurvivesSaveAndLoad()
    {
        var h = NewHost();
        string t = TrainingWorld(h), world = QuietWorld(h);
        h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 2, t, byAdvisor: true);
        h.Game.Step();
        var loaded = GameHost.Load(h.SaveWithSettings());
        Assert.Equal(loaded.StateHash(), h.StateHash());
        Assert.Equal(2, loaded.Construction.AdvisorUnitsInTraining(t));
        long seq = loaded.Construction.SubmitRedirectTraining(Side.Hand, t, 2, world);
        loaded.Game.Step();
        Assert.Contains(loaded.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == seq);
    }

    // m2-construction C27: the player's own project cannot be redirected
    [Fact]
    public void C27_RedirectTraining_OnThePlayersProject_IsRefused()
    {
        var h = NewHost();
        string t = TrainingWorld(h), world = QuietWorld(h);
        h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 2, t);
        h.Game.Step();
        long seq = h.Construction.SubmitRedirectTraining(Side.Hand, t, 2, world);
        h.Game.Step();
        Assert.Contains(h.Game.Events.OfType<OrderRejected>(), e => e.Sequence == seq && e.Reason == RejectReason.NotAdvisorUnits);
        Assert.Equal(t, TrainingProject(h, t).Destination);
    }

    // m2-construction C28: the player's regiments that joined her order stay on the old destination, queued right behind
    [Fact]
    public void C28_RedirectTraining_LeavesThePlayersJoinedUnitsBehind()
    {
        var h = NewHost();
        string t = TrainingWorld(h), world = QuietWorld(h);
        h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 2, t, byAdvisor: true);
        h.Construction.SubmitBuild(Side.Hand, t, YardFamily.Training, "Tranches", 1, t);
        h.Game.Step();
        var p = TrainingProject(h, t);
        Assert.Equal(3, p.Units.Count);
        int players = p.Units[2];
        Assert.Equal(2, h.Construction.AdvisorUnitsInTraining(t));
        long seq = h.Construction.SubmitRedirectTraining(Side.Hand, t, 5, world);
        h.Game.Step();
        Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == seq);
        p = TrainingProject(h, t);
        Assert.Equal(world, p.Destination);
        Assert.Equal(2, p.Units.Count);
        var q = Assert.Single(p.Next!);
        Assert.Equal(t, q.Destination);
        Assert.Equal(new[] { players }, q.Units);
    }
}
