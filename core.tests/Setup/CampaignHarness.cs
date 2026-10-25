using System.Collections.Concurrent;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Setup;
using Praedra.Core.Support;

namespace Praedra.Core.Tests.Setup;

/// <summary>What a daily-checked campaign run leaves behind: the finished game plus counters collected on the way.</summary>
internal sealed class CampaignRun
{
    public required TestCampaign Campaign { get; init; }
    public GameHost Host => Campaign.Host;
    public int Days { get; init; }
    /// <summary>Most completed refineries and mines one side held on any one day (the base of the income bounds).</summary>
    public Dictionary<Side, int> PeakRefineries { get; } = new() { [Side.Empire] = 0, [Side.Alliance] = 0 };
    public Dictionary<Side, int> PeakMines { get; } = new() { [Side.Empire] = 0, [Side.Alliance] = 0 };
    public Dictionary<Side, int> DaysWithRefined { get; } = new() { [Side.Empire] = 0, [Side.Alliance] = 0 };
    public Dictionary<Side, int> StartObjects { get; } = new() { [Side.Empire] = 0, [Side.Alliance] = 0 };
    public IReadOnlyList<GameEvent> Events => Host.Game.Events;
}

/// <summary>Runs of the test campaign, cached by (seed, ruleset) so the soak tests share the expensive 1000-day runs.</summary>
internal static class CampaignHarness
{
    public const int Days = 1000;
    private static readonly ConcurrentDictionary<(ulong, GarrisonRuleset), Lazy<CampaignRun>> Cache = new();

    // The scripted campaign (TestCampaign) was written for planets that change hands only by invasion: under Derived control the day-201
    // bombardment empties Tierra Larga, it goes neutral, the day-251 assault wins and the day-300 relief battle never happens as scripted (AI trainer,
    // 2026-10-18). So the script pins ControlRule AssaultOnly, whatever the game DEFAULT is (m2-control-and-capture; Paul chose Derived 2026-10-18).
    public static GameSettings Settings(GarrisonRuleset ruleset) => new() { SupportRuleset = ruleset, ControlRule = Praedra.Core.Control.ControlRule.AssaultOnly };

    /// <summary>The 1000-day run with every invariant checked after every Step (cached).</summary>
    public static CampaignRun Checked(ulong seed, GarrisonRuleset ruleset) =>
        Cache.GetOrAdd((seed, ruleset), k => new Lazy<CampaignRun>(() => Execute(k.Item1, k.Item2, Days, checkInvariants: true))).Value;

    public static CampaignRun Execute(ulong seed, GarrisonRuleset ruleset, int days, bool checkInvariants)
    {
        var c = TestCampaign.New(seed, Settings(ruleset));
        var run = new CampaignRun { Campaign = c, Days = days };
        foreach (var side in new[] { Side.Empire, Side.Alliance }) run.StartObjects[side] = CountOwned(c.Host, side);
        if (checkInvariants) CampaignInvariants.Check(c.Host, "day 0");
        for (int i = 0; i < days; i++)
        {
            c.Step();
            if (!checkInvariants) continue;
            CampaignInvariants.Check(c.Host, $"day {c.Game.Day}");
            foreach (var side in new[] { Side.Empire, Side.Alliance })
            {
                var s = SideStats.Of(c.Host, side);
                run.PeakRefineries[side] = Math.Max(run.PeakRefineries[side], s.Refineries);
                run.PeakMines[side] = Math.Max(run.PeakMines[side], s.Mines);
                if (s.Refined > 0) run.DaysWithRefined[side]++;
            }
        }
        return run;
    }

    public static int CountOwned(GameHost host, Side side) => host.Economy.Objects.Count(o => o.Completed && host.Economy.OwnerOf(o) == side);
}

/// <summary>
/// Everything that must hold after every Step of a campaign game. Each line names the rule it checks; none of them is a tuning target.
/// rule: m2-mines-refineries-pools P21; m2-slots-and-resources §Over-capacity destruction; m2-maintenance §Load, §Capacity;
/// m2-uprisings-and-garrison §Uprising start, keep, end; m2-construction §Projects and queues, §Completion and delivery;
/// m1-clock-and-orders §Order of work (fleets at a system or on a lane).
/// </summary>
internal static class CampaignInvariants
{
    private static readonly Side[] Sides = { Side.Empire, Side.Alliance };
    private static Side Other(Side s) => s == Side.Empire ? Side.Alliance : Side.Empire;

    public static void Check(GameHost host, string where, bool fixedRegions = true)
    {
        var eco = host.Economy; var state = host.Game.State; var galaxy = host.Data.Galaxy;

        // ---- pools never negative (m2-mines-refineries-pools P21)
        foreach (var side in Sides)
        {
            Assert.True(eco.Raw(side) >= 0, $"{where}: {side} raw {eco.Raw(side)}");
            Assert.True(eco.Refined(side) >= 0, $"{where}: {side} refined {eco.Refined(side)}");
        }

        // ---- support 0..100 everywhere (m2-popular-support §What it stores)
        foreach (var (sys, a) in state.Economy.AllianceSupport) Assert.True(a is >= 0 and <= 100, $"{where}: support of {sys} is {a}");

        // ---- installed facilities within the slots (m2-slots-and-resources §Over-capacity destruction)
        foreach (var g in eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed).GroupBy(o => o.System!))
        {
            var slots = eco.SlotsOf(g.Key);
            Assert.True(g.Count() <= slots.Energy, $"{where}: {g.Key} has {g.Count()} installed facilities for {slots.Energy} energy slots");
            Assert.True(g.Count(o => o.Class == "Mine") <= eco.EffectiveRaw(g.Key), $"{where}: {g.Key} has too many mines for its raw slots");
        }

        // ---- maintenance load, capacity and free agree with the objects (m2-maintenance §Load, §Capacity)
        int owned = 0;
        foreach (var side in Sides)
        {
            int load = 0, mines = 0, refs = 0;
            foreach (var o in eco.Objects)
            {
                if (eco.OwnerOf(o) != side) continue;
                owned++;
                load += eco.Costs.Get(o.Class).Maintenance;
                // B5b 2026-10-18: a suspended mine or refinery (blockade, uprising) leaves the capacity sums (m2-maintenance §Capacity, SuspendedLeaveCapacity on)
                if (o.Completed && o.Category == ObjectCategory.Facility && !(eco.SuspendedLeaveCapacity && eco.IsSuspended(o)))
                {
                    if (o.Class == "Mine") mines++; else if (o.Class == "Refinery") refs++;
                }
            }
            Assert.True(load == eco.Load(side), $"{where}: {side} load {eco.Load(side)} but objects add up to {load}");
            Assert.True(eco.Capacity(side) == eco.Params.CapacityPerFacility * Math.Min(mines, refs), $"{where}: {side} capacity {eco.Capacity(side)}");
            Assert.Equal(eco.Capacity(side) - eco.Load(side), eco.Free(side));
        }
        int neutralFacilities = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.System != null && !state.SystemOwners.ContainsKey(o.System)); // m5: the neutral starting shipyards serve nobody
        Assert.True(owned + neutralFacilities == eco.Objects.Count, $"{where}: {eco.Objects.Count - owned} objects belong to nobody");

        // ---- no object in two places (m2-construction §Projects and queues, §Completion and delivery)
        var c = state.Construction;
        var inWork = new HashSet<int>();
        foreach (var p in c.AllProjects())
            foreach (var id in p.Units)
                Assert.True(inWork.Add(id), $"{where}: object {id} is in two projects");
        foreach (var d in c.Deliveries.Values)
        {
            Assert.True(inWork.Add(d.Object), $"{where}: object {d.Object} is in a project and a delivery, or in two deliveries");
            Assert.InRange(d.Index, 0, d.Path.Count - 1);
            Assert.Equal(d.Path.Count - 1, d.HopTicks.Count);
            if (d.InFlight) Assert.False(d.Held, $"{where}: delivery {d.Id} is in flight and held");
        }
        foreach (var o in eco.Objects)
        {
            // A unit that is not finished belongs to exactly one project or delivery; a finished one to neither.
            Assert.True(o.Completed != inWork.Contains(o.Id), $"{where}: object {o.Id} ({o.Class}) completed={o.Completed} but inWork={inWork.Contains(o.Id)}");
            if (o.Completed && o.Category == ObjectCategory.Regiment) Assert.True((o.System != null || o.AboardShip != 0) && o.Side != null, $"{where}: regiment {o.Id} has no place or side");
        }
        // every waiting refinery or yard is in exactly one line, and every line entry is a waiting, completed object
        var rawLines = eco.RawLine(Side.Empire).Concat(eco.RawLine(Side.Alliance)).ToList();
        var refinedLines = c.Empire.RefinedLine.Concat(c.Alliance.RefinedLine).ToList();
        Assert.Equal(rawLines.Count, rawLines.Distinct().Count());
        Assert.Equal(refinedLines.Count, refinedLines.Distinct().Count());
        Assert.Empty(rawLines.Intersect(refinedLines));
        foreach (var id in rawLines.Concat(refinedLines)) { var o = eco.Find(id); Assert.True(o is { Completed: true, InLine: true }, $"{where}: line entry {id}"); }
        Assert.Equal(rawLines.Count + refinedLines.Count, eco.Objects.Count(o => o.InLine));

        // ---- every fleet is at a system or on a lane, never both, never neither (m1-clock-and-orders §Order of work)
        foreach (var f in state.Fleets.Values)
        {
            Assert.True(f.Ships.Count + f.Squadrons.Count > 0, $"{where}: fleet {f.Id} is empty");
            if (f.At != null)
            {
                Assert.True(galaxy.TryFindSystem(f.At, out _), $"{where}: fleet {f.Id} at unknown system {f.At}");
                Assert.True(f.Transit == null, $"{where}: fleet {f.Id} is at {f.At} and still has an order in flight");
            }
            else
            {
                var t = f.Transit;
                Assert.True(t != null, $"{where}: fleet {f.Id} is nowhere");
                Assert.InRange(t!.HopIndex, 0, t.Path.Count - 2);
                Assert.Equal(t.Path.Count - 1, t.HopTicks.Count);
                Assert.True(t.DueDay > host.Game.Day, $"{where}: fleet {f.Id} should have landed by day {t.DueDay}");
                // free travel (m1-travel, 2026-10-16): a leg ends at a route system or at the destination, never at an off-route system on the way
                Assert.True(galaxy.TryFindSystem(t.Path[t.HopIndex + 1], out int next) && (galaxy.OnRoute(next) || t.HopIndex + 1 == t.Path.Count - 1),
                    $"{where}: fleet {f.Id} stops at an off-route system on the way");
            }
        }

        // ---- fleets hold live registry ships, no ship is in two fleets, and the speed rule holds (m3-fleets §Invariants, §Fleet speed)
        var seenShips = new HashSet<int>(); var seenSquadrons = new HashSet<int>();
        foreach (var f in state.Fleets.Values)
        {
            foreach (var id in f.Ships)
            {
                var o = eco.Find(id);
                Assert.True(o != null, $"{where}: fleet {f.Id} holds ship {id}, which is not in the registry");
                Assert.True(o!.Category == ObjectCategory.CapitalShip && o.Completed, $"{where}: fleet {f.Id} holds {id} ({o.Class}), not a finished ship");
                Assert.True(seenShips.Add(id), $"{where}: ship {id} is in two fleets");
            }
            foreach (var id in f.Squadrons)
            {
                var o = eco.Find(id);
                Assert.True(o is { Category: ObjectCategory.Fighter }, $"{where}: fleet {f.Id} holds {id}, which is not a squadron in the registry");
                Assert.True(seenSquadrons.Add(id), $"{where}: squadron {id} is in two fleets");
                Assert.True(o!.AboardShip == 0, $"{where}: squadron {id} flies in fleet {f.Id} and is also aboard a ship");
            }
            if (f.Ships.Count > 0)
                Assert.Equal(f.Ships.Max(id => host.Fleets.SpeedOfShip(eco.Find(id)!)), host.Fleets.SpeedOf(f)); // the largest rating decides; aboard squadrons never count
        }

        // ---- ship damage stays in range: hull 1..class maximum, six counters 0..15 (m4-battle-classic §10; m3-ships-and-fighters §Damaged state)
        foreach (var o in eco.Objects.Where(o => o.Category == ObjectCategory.CapitalShip && o.Completed))
        {
            int max = host.Data.Ships[o.Class].Hull;
            Assert.True(o.Hull >= 1 && o.Hull <= max, $"{where}: ship {o.Id} ({o.Class}) hull {o.Hull} outside 1..{max}");
            Assert.True(o.Hits is { Count: ObjectRegistry.HitCounters } && o.Hits.All(h => h is >= 0 and <= ObjectRegistry.MaxHit), $"{where}: ship {o.Id} has bad hit counters");
        }
        // ---- a squadron aboard a ship rides a live ship of its own side that is in a fleet (m4-battle-classic §10, DEFAULT: a lost carrier's squadrons are based)
        foreach (var o in eco.Objects.Where(o => o.Category == ObjectCategory.Fighter && o.AboardShip != 0))
        {
            var carrier = eco.Find(o.AboardShip);
            Assert.True(carrier != null && carrier.Side == o.Side, $"{where}: squadron {o.Id} rides ship {o.AboardShip}, which is gone or of the other side");
        }

        // ---- troops: a regiment is on a system or aboard a ship of a fleet of its side, never both; aboard counts stay within capacity;
        //      special forces are never aboard (m3-troops-and-special-forces §Where regiments live; m3-fleets §Carrying rules)
        var shipFleet = new Dictionary<int, Fleet>(); // lookup only
        foreach (var f in state.Fleets.Values) foreach (var id in f.Ships) shipFleet[id] = f;
        var aboard = new Dictionary<int, int>(); // ship id -> regiments aboard (lookup only; checked by key below)
        foreach (var o in eco.Objects.Where(o => o.Category is ObjectCategory.Regiment or ObjectCategory.SpecialForce))
        {
            if (o.Category == ObjectCategory.SpecialForce)
            {
                // a member of a mission group in flight is on no system (m5-missions §1: the group's members leave their place while it travels)
                Assert.True(o.AboardShip == 0 && (o.System != null || !o.Completed || host.Missions.IsSpecialForceBusy(o.Id)), $"{where}: special force {o.Id} aboard={o.AboardShip} system={o.System}");
                continue;
            }
            if (o.AboardShip == 0) { Assert.True(o.System != null || !o.Completed, $"{where}: regiment {o.Id} is on no system and aboard nothing"); continue; }
            Assert.True(o.System == null, $"{where}: regiment {o.Id} is aboard ship {o.AboardShip} and also on {o.System}");
            Assert.True(o.Completed, $"{where}: unfinished regiment {o.Id} is aboard a ship");
            Assert.True(shipFleet.TryGetValue(o.AboardShip, out var carrier), $"{where}: regiment {o.Id} rides ship {o.AboardShip}, which is in no fleet");
            Assert.True(carrier!.Side == o.Side, $"{where}: regiment {o.Id} of {o.Side} rides in a fleet of {carrier.Side}");
            aboard[o.AboardShip] = aboard.GetValueOrDefault(o.AboardShip) + 1;
        }
        foreach (var f in state.Fleets.Values)
            foreach (var id in f.Ships)
            {
                int cap = host.Data.Ships[eco.Find(id)!.Class].TroopCapacity;
                Assert.True(aboard.GetValueOrDefault(id) <= cap, $"{where}: ship {id} carries {aboard.GetValueOrDefault(id)} regiments, capacity {cap}");
            }

        // ---- owners and the Classic blockade flags (m1-forced-stop-and-blockade §Rule (Classic))
        foreach (var (sys, blockader) in state.Blockades)
        {
            Assert.True(state.SystemOwners.TryGetValue(sys, out var owner), $"{where}: blockade on neutral {sys}");
            Assert.Equal(Other(owner), blockader);
        }

        // ---- uprising flags and timers (m2-uprisings-and-garrison §Uprising start, keep, end; m2-popular-support §Periodic effects)
        foreach (var (sys, s) in state.Support.Systems)
        {
            Assert.False(s.IsIdle, $"{where}: idle support entry for {sys}");
            Assert.Equal(s.Uprising, s.ErosionDue != 0);                    // timers 0x384 and 0x38d live and die with the flag
            Assert.Equal(s.Uprising, s.IncidentDue != 0);
            if (s.Uprising)
            {
                Assert.True(state.SystemOwners.TryGetValue(sys, out var holder), $"{where}: uprising on neutral {sys}");
                Assert.Equal(Other(holder), s.Rebels);                       // the rebels are the other side than the holder
                Assert.True(s.ErosionDue > host.Game.Day && s.IncidentDue > host.Game.Day, $"{where}: overdue uprising timer on {sys}");
            }
            if (s.PresenceDue != 0)
            {
                Assert.True(state.Blockades.TryGetValue(sys, out var b) && b == s.PresenceSide, $"{where}: presence timer on {sys} without its blockade");
                Assert.True(s.PresenceDue > host.Game.Day, $"{where}: overdue presence timer on {sys}");
            }
        }
        if (host.Support.BlockadeSupportEffect)
            foreach (var sys in state.Blockades.Keys) // the other direction: every blockade has its presence timer by the end of the Step
                Assert.True(state.Support.Systems.TryGetValue(sys, out var s) && s.PresenceDue != 0, $"{where}: blockade on {sys} but no presence timer");

        // ---- control only moves by capture: the two regions plus the captured system stay owned (no neutral appears from nowhere)
        if (host.Control.Rule == ControlRule.AssaultOnly)
        {
            if (fixedRegions && !state.Scenario.Active) Assert.Equal(34, state.SystemOwners.Count); // the test campaign only; a new game (m5) deals 362, and AI games move owners
        }
        else
        {
            // (B5a, 2026-10-17) under derived control (the DEFAULT) a world does go neutral without a capture (its last regiment dies and its support
            // is 41..59), so the count above encodes the old rule. In its place, in every game: at the end of each Step every world's holder is the
            // one the derivation gives. One side's standing regiments only: that side; none: the support band (Alliance at 60 and over, Empire at 40
            // and under, else neutral; unpopulated: neutral); both: one of the two (the previous military side). rule: m2-control-and-capture
            // §Who holds a system (B5a section 1)
            var standing = new Dictionary<string, (bool A, bool E)>(StringComparer.Ordinal);
            foreach (var o in eco.Objects)
                if (o.Category == ObjectCategory.Regiment && o.Completed && o.AboardShip == 0 && o.System is { } at && o.Side is Side rs)
                {
                    var was = standing.GetValueOrDefault(at);
                    standing[at] = rs == Side.Alliance ? (true, was.E) : (was.A, true);
                }
            foreach (var star in galaxy.Systems)
            {
                if (eco.IsDestroyed(star.Name)) continue;
                var (a, e) = standing.GetValueOrDefault(star.Name);
                Side? holder = host.Control.HolderOf(star.Name);
                Side? want = a && e ? holder : a ? Side.Alliance : e ? Side.Empire : host.Control.SupportSide(star.Name);
                Assert.True(want == holder, $"{where}: {star.Name} held by {holder?.ToString() ?? "nobody"}, derived {want?.ToString() ?? "nobody"}");
                if (a && e) Assert.NotNull(holder);
            }
        }
    }
}
