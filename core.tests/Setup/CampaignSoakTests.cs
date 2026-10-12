using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Setup;
using Rebellion.Core.Support;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.Setup;

// P2.5, the M2 exit test: 1000 days of the scripted two-sided test campaign (core/Setup/TestCampaign), both support rulesets.
// Plan M2 exit: "a 1000-day headless run doesn't crash or drift to nonsense". Sheet tests used: m2-mines-refineries-pools P21
// (1000-day run, pools >= 0, same seed same hash), m2-slots-and-resources S14 (twice identical), m2-popular-support S-soak, m2-construction.
public class CampaignSoakTests
{
    private readonly ITestOutputHelper _out;
    public CampaignSoakTests(ITestOutputHelper output) { _out = output; }

    public static IEnumerable<object[]> Rulesets => new[] { new object[] { GarrisonRuleset.Classic }, new object[] { GarrisonRuleset.New } };

    // ---------------------------------------------------------------- no crash, invariants every day (the checks are in CampaignInvariants)

    // m2-mines-refineries-pools P21
    [Theory, MemberData(nameof(Rulesets))]
    public void ThousandDaysRunWithEveryInvariantHoldingEveryDay(GarrisonRuleset ruleset)
    {
        var run = CampaignHarness.Checked(1, ruleset); // throws on the first broken invariant, naming the day
        Assert.Equal(CampaignHarness.Days, run.Host.Game.Day);
        Assert.True(run.Host.Game.Rng.Draws > 0);
    }

    // ---------------------------------------------------------------- determinism and saves

    // m2-mines-refineries-pools P21, m2-slots-and-resources S14: same seed, same hash; another seed, another game
    [Theory, MemberData(nameof(Rulesets))]
    public void SameSeedSameHashDifferentSeedDifferentHash(GarrisonRuleset ruleset)
    {
        string a = CampaignHarness.Checked(1, ruleset).Host.StateHash();
        string b = CampaignHarness.Execute(1, ruleset, CampaignHarness.Days, checkInvariants: false).Host.StateHash();
        string c = CampaignHarness.Checked(2, ruleset).Host.StateHash();
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // m1-clock-and-orders §The clock (a save is made between Steps and holds everything that decides the future), rule 10 (hash)
    [Theory, MemberData(nameof(Rulesets))]
    public void SaveAtDay500LoadThroughHostRunToDay1000GivesTheUninterruptedHash(GarrisonRuleset ruleset)
    {
        var whole = CampaignHarness.Checked(1, ruleset);

        var first = TestCampaign.New(1, CampaignHarness.Settings(ruleset));
        first.RunTo(500);
        string json = first.Host.Save();
        var loaded = TestCampaign.Load(json, CampaignHarness.Settings(ruleset));
        Assert.Equal(500, loaded.Game.Day);
        Assert.Equal(first.Host.StateHash(), loaded.Host.StateHash());
        for (int i = 500; i < CampaignHarness.Days; i++)
        {
            loaded.Step();
            if (i % 50 == 0) CampaignInvariants.Check(loaded.Host, $"loaded game, day {loaded.Game.Day}");
        }
        Assert.Equal(whole.Host.StateHash(), loaded.Host.StateHash());
        Assert.Equal(whole.Host.Game.Rng.Draws, loaded.Host.Game.Rng.Draws);
    }

    // a save taken just before the scripted capture still sees the capture after loading (the hook is registered again by the campaign)
    [Fact]
    public void SaveJustBeforeTheCaptureDayStillCapturesAfterLoad()
    {
        var first = TestCampaign.New(3);
        first.RunTo(TestCampaign.CaptureDay);
        Assert.Equal(Side.Alliance, first.Host.Fleets.OwnerOf(TestCampaign.CaptureSystem));
        var loaded = TestCampaign.Load(first.Host.Save());
        first.Step(); loaded.Step();
        Assert.Equal(Side.Empire, loaded.Host.Fleets.OwnerOf(TestCampaign.CaptureSystem));
        Assert.Equal(first.Host.StateHash(), loaded.Host.StateHash());
    }

    // ---------------------------------------------------------------- "doesn't drift to nonsense": loose sanity bands at day 1000

    /// <summary>
    /// Upper bound on a side's pool, derived from the rates, not tuned. A mine or a refinery finishes a cycle of at least
    /// d = (ceil(share / k) + ProcessingRate) x eff / 100 days, and share >= 0 and eff >= 100 (support is clamped to at most 100), so d >= ProcessingRate = 5.
    /// Its first cycle is rand(0..d) + d / 2, which can be as short as 2 days, so over D days one facility ends at most D / 5 + 1 cycles. Each cycle adds
    /// one unit. A side's pool therefore cannot exceed: (most facilities of that kind it held at once) x (D / 5 + 1), plus what came in from the other side
    /// by smuggling, plus (refined only) the refunds of scrapped objects (which are events). Spending only lowers the pool.
    /// </summary>
    private static int PoolBound(CampaignRun run, Side side, bool refined)
    {
        int peak = refined ? run.PeakRefineries[side] : run.PeakMines[side];
        int rate = run.Host.Economy.Params.ProcessingRate;
        int cycles = run.Days / rate + 1;
        string resource = refined ? "Refined" : "Raw";
        int smuggledIn = run.Events.OfType<ResourceProduced>().Count(e => e.Resource == resource && e.PoolSide == side && e.Diverted);
        int refunds = refined ? run.Events.OfType<ObjectScrapped>().Where(e => e.Side == side).Sum(e => e.Refund) : 0;
        return peak * cycles + smuggledIn + refunds;
    }

    // m2-mines-refineries-pools P21 (no drift); plan M2 exit ("doesn't drift to nonsense")
    [Theory, MemberData(nameof(Rulesets))]
    public void Day1000StaysInsideLooseSanityBands(GarrisonRuleset ruleset)
    {
        var run = CampaignHarness.Checked(1, ruleset);
        var host = run.Host; var ev = run.Events;
        _out.WriteLine($"Ruleset {ruleset}, seed 1, 1000 days.");
        foreach (var side in new[] { Side.Empire, Side.Alliance }) _out.WriteLine(SideStats.Of(host, side).ToString());

        foreach (var side in new[] { Side.Empire, Side.Alliance })
        {
            var s = SideStats.Of(host, side);
            int refinedBound = PoolBound(run, side, true), rawBound = PoolBound(run, side, false);
            int producedLast100 = ev.OfType<ResourceProduced>().Count(e => e.Resource == "Refined" && e.PoolSide == side && e.Day > run.Days - 100);
            int producedTotal = ev.OfType<ResourceProduced>().Count(e => e.Resource == "Refined" && e.PoolSide == side);
            _out.WriteLine($"{side}: refined {s.Refined} (bound {refinedBound}, {100 * s.Refined / Math.Max(1, refinedBound)}% of it), raw {s.Raw} (bound {rawBound}); " +
                           $"refined produced {producedTotal}, in the last 100 days {producedLast100}; days with refined > 0: {run.DaysWithRefined[side]}; " +
                           $"peak mines {run.PeakMines[side]}, peak refineries {run.PeakRefineries[side]}");

            // not stuck at zero forever: refined was made, was in the pool on some days, and is still being made at the end
            Assert.True(producedTotal > 0, $"{side}: no refined produced in 1000 days");
            Assert.True(run.DaysWithRefined[side] > 0, $"{side}: refined pool never above 0");
            Assert.True(producedLast100 > 0, $"{side}: no refined produced in the last 100 days");
            // not exploding: below what the facilities could possibly make (see PoolBound)
            Assert.InRange(s.Refined, 0, refinedBound);
            Assert.InRange(s.Raw, 0, rawBound);

            // autoscrap did not delete everything: at least half of the objects the side started with are still there
            int now = CampaignHarness.CountOwned(host, side);
            _out.WriteLine($"{side}: finished objects {run.StartObjects[side]} at day 0, {now} at day 1000; scrapped by autoscrap {ev.OfType<ObjectScrapped>().Count(e => e.Side == side && e.Reason == "Autoscrap")}");
            Assert.True(2 * now >= run.StartObjects[side], $"{side}: {now} of {run.StartObjects[side]} objects left");
        }

        // the scripted story really happened
        Assert.True(ev.OfType<UprisingStarted>().Any(), "no uprising in 1000 days");
        Assert.True(ev.OfType<UnitCompleted>().Any(), "nothing was built");
        Assert.True(ev.OfType<DeliveryArrived>().Any(), "nothing was delivered");
        Assert.Contains(ev, e => e is SystemCaptured { System: TestCampaign.CaptureSystem, To: Side.Empire, Day: TestCampaign.CaptureDay + 1 });
        Assert.Contains(ev, e => e is BlockadeStarted { System: TestCampaign.BlockadedSystem, Blockader: Side.Empire });
        Assert.Contains(ev, e => e is ProjectCancelled { System: TestCampaign.CaptureSystem, Reason: "SystemLost" });
        Assert.Equal(34, host.Game.State.SystemOwners.Count);
        Assert.Equal(Side.Empire, host.Fleets.OwnerOf(TestCampaign.CaptureSystem));

        // what the script asked for, and what the modules answered (printed so a run can be read)
        var rejected = ev.OfType<OrderRejected>().GroupBy(e => e.Reason).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}");
        _out.WriteLine($"Orders rejected: {string.Join(", ", rejected)}. Accepted: {ev.OfType<OrderAccepted>().Count()}.");
        _out.WriteLine($"Uprisings started {ev.OfType<UprisingStarted>().Count()}, ended {ev.OfType<UprisingEnded>().Count()}, incidents {ev.OfType<IncidentRolled>().Count()}; " +
                       $"built {ev.OfType<UnitCompleted>().Count()}, delivered {ev.OfType<DeliveryArrived>().Count()}, held {ev.OfType<DeliveryHeld>().Count()}; " +
                       $"autoscrapped {ev.OfType<ObjectScrapped>().Count(e => e.Reason == "Autoscrap")}; facilities destroyed {ev.OfType<FacilityDestroyed>().Count()}; draws {host.Game.Rng.Draws}.");
        _out.WriteLine($"State hash {host.StateHash()}");
    }

    // The script's orders must not be nonsense: no unknown fleets, systems or items (those would be script bugs, not game outcomes).
    // A NoYard, NoSlot, InTransit, AlreadyThere or BadDestination after a capture or a destroyed yard is a legal outcome.
    [Theory, MemberData(nameof(Rulesets))]
    public void ScriptedOrdersNeverNameAnythingUnknown(GarrisonRuleset ruleset)
    {
        var rejected = CampaignHarness.Checked(1, ruleset).Events.OfType<OrderRejected>().Select(e => e.Reason).ToHashSet();
        foreach (var bad in new[] { RejectReason.UnknownFleet, RejectReason.UnknownSystem, RejectReason.UnknownItem, RejectReason.WrongFamily,
                                    RejectReason.NotAvailable, RejectReason.NotResearched, RejectReason.BadCount, RejectReason.UnknownOrderType,
                                    RejectReason.Unreachable, RejectReason.Immobile })
            Assert.DoesNotContain(bad, rejected);
    }

    // ---------------------------------------------------------------- the settings reach the modules through the host

    // m2-uprisings-and-garrison §Garrison G and the check: Taanab holds 2 Imperial Fleet Regiments for a requirement of 2.
    // Classic counts 1.0 each (enough); New weighs them 0.6 each (1.2 < 2: an uprising on the first day).
    [Fact]
    public void RulesetSettingChangesTheOutcomeAtTaanab()
    {
        bool Rises(GarrisonRuleset r)
        {
            var c = TestCampaign.New(1, CampaignHarness.Settings(r));
            c.Step();
            return c.Host.Support.IsUprising("Taanab");
        }
        Assert.False(Rises(GarrisonRuleset.Classic));
        Assert.True(Rises(GarrisonRuleset.New));
        Assert.NotEqual(CampaignHarness.Checked(1, GarrisonRuleset.Classic).Host.StateHash(), CampaignHarness.Checked(1, GarrisonRuleset.New).Host.StateHash());
    }

    // The ruleset is configuration, not state: Load must be given it again, and then it applies.
    [Fact]
    public void LoadAppliesTheSettingsGivenToIt()
    {
        var c = TestCampaign.New(1, CampaignHarness.Settings(GarrisonRuleset.Classic));
        c.RunTo(10);
        var asNew = TestCampaign.Load(c.Host.Save(), CampaignHarness.Settings(GarrisonRuleset.New));
        Assert.Equal(GarrisonRuleset.New, asNew.Host.Support.Ruleset);
        asNew.Step();
        Assert.True(asNew.Host.Support.IsUprising("Taanab"));
        var asClassic = TestCampaign.Load(c.Host.Save());
        asClassic.Step();
        Assert.False(asClassic.Host.Support.IsUprising("Taanab"));
    }
}
