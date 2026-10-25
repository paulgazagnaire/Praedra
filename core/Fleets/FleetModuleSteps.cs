using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Ships;
using Praedra.Core.Travel;

namespace Praedra.Core.Fleets;

/// <summary>Fleets during a Step: fleet orders, MoveFleet, new and removed ships, departures, arrivals, forced stops, contacts and blockade flags (moved unchanged from FleetModule.cs).</summary>
public sealed partial class FleetModule
{
    // ------------------------------------------------------------------ step 1: fleet orders (m3-fleets §Orders)

    // rule: m3-fleets §Orders (CreateFleet): rejections DuplicateShip, UnknownShip, NotYours, InTransit, NotCompleted, NotSameSystem, TooFewShips, NoChange
    private OrderResult HandleCreateFleet(StepContext ctx, CreateFleetOrder o)
    {
        if (CheckShipList(o.Side, o.Ships, out var ships) is { } bad) return OrderResult.Reject(bad);
        var places = ships.Select(PlaceOfShip).ToList();
        if (places.Any(p => p.Moving)) return OrderResult.Reject(RejectReason.InTransit);
        if (ships.Any(s => !s.Completed)) return OrderResult.Reject(RejectReason.NotCompleted);
        string? system = places.Count > 0 ? places[0].System : null;
        if (system == null || places.Any(p => p.System != system)) return OrderResult.Reject(RejectReason.NotSameSystem);
        if (ships.Count < 2) return OrderResult.Reject(RejectReason.TooFewShips); // Traced: Create Fleet needs more than one entry (FUN_00580db0)
        var from = places.Select(p => p.Fleet).Distinct().ToList();
        if (from.Count == 1 && from[0] is { } only && only.Ships.Count == ships.Count) return OrderResult.Reject(RejectReason.NoChange);

        int day = ctx.Day;
        var left = TakeOut(ctx, day, ships);
        foreach (var s in ships) s.System = null;
        var f = NewFleet(o.Side, system, ships.Select(s => s.Id));
        var members = new ValueList<int>(f.Ships.ToList());
        ctx.Emit((i, _) => new FleetCreated(i, day, f.Id, system, members, FleetCreateCause.CreateFleet));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, f.Id));
    }

    // rule: m3-fleets §Orders (SplitFleet): UnknownFleet, NotYours, ship checks, InTransit, ShipNotInFleet, EmptySelection, WholeFleet
    private OrderResult HandleSplitFleet(StepContext ctx, SplitFleetOrder o)
    {
        if (!State.Fleets.TryGetValue(o.Fleet, out var fleet)) return OrderResult.Reject(RejectReason.UnknownFleet);
        if (fleet.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (CheckShipList(o.Side, o.Ships, out var ships) is { } bad) return OrderResult.Reject(bad);
        if (Moving(fleet)) return OrderResult.Reject(RejectReason.InTransit);
        if (ships.Any(s => !fleet.Ships.Contains(s.Id))) return OrderResult.Reject(RejectReason.ShipNotInFleet);
        if (ships.Count == 0) return OrderResult.Reject(RejectReason.EmptySelection);
        if (ships.Count == fleet.Ships.Count) return OrderResult.Reject(RejectReason.WholeFleet);

        int day = ctx.Day;
        string system = fleet.At!;
        foreach (var s in ships) fleet.Ships.Remove(s.Id);
        var f = NewFleet(o.Side, system, ships.Select(s => s.Id));
        var members = new ValueList<int>(f.Ships.ToList());
        ctx.Emit((i, _) => new FleetCreated(i, day, f.Id, system, members, FleetCreateCause.SplitFleet));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, f.Id));
    }

    // rule: m3-fleets §Orders (TransferShips): UnknownFleet, NotYours, ship checks, EmptySelection (DEFAULT name), InTransit, NotCompleted,
    // NotSameSystem, SameFleet, SquadronFleet
    private OrderResult HandleTransferShips(StepContext ctx, TransferShipsOrder o)
    {
        if (!State.Fleets.TryGetValue(o.ToFleet, out var to)) return OrderResult.Reject(RejectReason.UnknownFleet);
        if (to.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (CheckShipList(o.Side, o.Ships, out var ships) is { } bad) return OrderResult.Reject(bad);
        if (ships.Count == 0) return OrderResult.Reject(RejectReason.EmptySelection);
        var places = ships.Select(PlaceOfShip).ToList();
        if (Moving(to) || places.Any(p => p.Moving)) return OrderResult.Reject(RejectReason.InTransit);
        if (ships.Any(s => !s.Completed)) return OrderResult.Reject(RejectReason.NotCompleted);
        if (places.Any(p => p.System != to.At)) return OrderResult.Reject(RejectReason.NotSameSystem);
        if (places.All(p => p.Fleet == to)) return OrderResult.Reject(RejectReason.SameFleet);
        if (to.Ships.Count == 0) return OrderResult.Reject(RejectReason.SquadronFleet);

        MoveShips(ctx, ctx.Day, ships.Where((s, i) => places[i].Fleet != to).ToList(), to);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // Move ships (not yet in `to`) into `to`; aboard units stay on their ship (AboardShip is untouched).
    private void MoveShips(StepContext ctx, int day, List<EconObject> ships, Fleet to)
    {
        var left = TakeOut(ctx, day, ships, to.Id);
        foreach (var s in ships) { AddSorted(to.Ships, s.Id); s.System = null; }
        var ids = new ValueList<int>(ships.Select(s => s.Id).OrderBy(i => i).ToList());
        var from = new ValueList<int>(left);
        ctx.Emit((i, _) => new ShipsTransferred(i, day, ids, from, to.Id));
    }

    // rule: m3-fleets §Orders (MergeFleets): UnknownFleet, NotYours, SameFleet, InTransit, NotSameSystem, SquadronFleet
    private OrderResult HandleMergeFleets(StepContext ctx, MergeFleetsOrder o)
    {
        if (!State.Fleets.TryGetValue(o.From, out var from) || !State.Fleets.TryGetValue(o.Into, out var into))
            return OrderResult.Reject(RejectReason.UnknownFleet);
        if (from.Side != o.Side || into.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (from == into) return OrderResult.Reject(RejectReason.SameFleet);
        if (Moving(from) || Moving(into)) return OrderResult.Reject(RejectReason.InTransit);
        if (from.At != into.At) return OrderResult.Reject(RejectReason.NotSameSystem);
        if (from.Ships.Count == 0 || into.Ships.Count == 0) return OrderResult.Reject(RejectReason.SquadronFleet);

        int day = ctx.Day;
        var moved = from.Ships.ToList();
        foreach (var id in moved) { AddSorted(into.Ships, id); }
        from.Ships.Clear();
        var ids = new ValueList<int>(moved);
        var left = new ValueList<int>(new List<int> { from.Id });
        ctx.Emit((i, _) => new ShipsTransferred(i, day, ids, left, into.Id));
        RemoveFleet(ctx, day, from, DissolveCause.Merged, into.Id); // the spare flag leaves with the removed fleet (m3-fleets §New ships)
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: m3-fleets §Orders (MoveSquadrons), §Unescorted squadrons: UnknownSquadron, NotYours, SquadronAboard, NotCompleted, NotSameSystem,
    // then the MoveFleet reasons (Immobile when any rating is 0). Nothing is created unless the order is accepted (F14).
    private OrderResult HandleMoveSquadrons(StepContext ctx, MoveSquadronsOrder o)
    {
        if (CheckSquadronList(o.Side, o.Squadrons, out var squads) is { } bad) return OrderResult.Reject(bad);
        if (squads.Count == 0) return OrderResult.Reject(RejectReason.EmptySelection);
        if (squads.Any(s => s.AboardShip != 0)) return OrderResult.Reject(RejectReason.SquadronAboard);
        if (squads.Any(s => !s.Completed)) return OrderResult.Reject(RejectReason.NotCompleted);
        if (squads.Any(s => s.System == null)) return OrderResult.Reject(RejectReason.InTransit); // already flying in a squadron-only fleet
        string from = squads[0].System!;
        if (squads.Any(s => s.System != from)) return OrderResult.Reject(RejectReason.NotSameSystem);

        // speed = largest rating among the squadrons (same formula, path and forced stop as MoveFleet); any 0 makes it Immobile
        int speed = TravelFormula.FleetSpeed(Array.Empty<int>(), squads.Select(SpeedOfShip));
        var path = _pathfinder.Find(from, o.Destination, speed);
        if (!path.Ok) return OrderResult.Reject(RejectStatus(path.Status));

        int day = ctx.Day;
        foreach (var s in squads) s.System = null;
        int fid = ObjectRegistry.NextId(E);
        var names = path.Names(Galaxy).ToList();
        var f = new Fleet
        {
            Id = fid, Name = FleetName(fid), Side = o.Side, Squadrons = squads.Select(s => s.Id).OrderBy(i => i).ToList(), At = from,
            Transit = new FleetTransit { Path = names, HopTicks = path.HopTicks.ToList(), HopIndex = 0, DueDay = day + path.HopTicks[0] },
        };
        State.Fleets[fid] = f;
        var members = new ValueList<int>(f.Squadrons.ToList());
        ctx.Emit((i, _) => new FleetCreated(i, day, fid, from, members, FleetCreateCause.Squadrons));
        int arrival = day + path.TotalTicks, total = path.TotalTicks;
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, fid, new ValueList<string>(names), total, arrival));
    }

    // rule: m3-fleets §Orders (LoadSquadrons): UnknownFleet, NotYours, squadron checks, InTransit, NotCompleted, SquadronNotBased, NotAtFleetSystem, NoCapacity
    private OrderResult HandleLoadSquadrons(StepContext ctx, LoadSquadronsOrder o)
    {
        if (!State.Fleets.TryGetValue(o.Fleet, out var fleet)) return OrderResult.Reject(RejectReason.UnknownFleet);
        if (fleet.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (CheckSquadronList(o.Side, o.Squadrons, out var squads) is { } bad) return OrderResult.Reject(bad);
        if (Moving(fleet)) return OrderResult.Reject(RejectReason.InTransit);
        if (squads.Any(s => !s.Completed)) return OrderResult.Reject(RejectReason.NotCompleted);
        if (squads.Any(s => s.AboardShip != 0 || s.System == null)) return OrderResult.Reject(RejectReason.SquadronNotBased);
        if (squads.Any(s => s.System != fleet.At)) return OrderResult.Reject(RejectReason.NotAtFleetSystem);
        var free = FreeBerthsByShip(fleet);
        if (free.Values.Sum() < squads.Count) return OrderResult.Reject(RejectReason.NoCapacity); // all or nothing

        // first free berth by ship id ascending (m3-fleets DEFAULT)
        int day = ctx.Day;
        string system = fleet.At!;
        var carriers = new List<int>();
        foreach (var s in squads)
        {
            int ship = free.First(kv => kv.Value > 0).Key;
            free[ship]--;
            s.AboardShip = ship; s.System = null;
            carriers.Add(ship);
        }
        var ids = new ValueList<int>(squads.Select(s => s.Id).ToList());
        var on = new ValueList<int>(carriers);
        ctx.Emit((i, _) => new SquadronsLoaded(i, day, ids, fleet.Id, on, system));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: m3-fleets §Orders (UnloadSquadrons): UnknownFleet, NotYours, squadron checks, InTransit, NotAboard, NotYourSystem
    private OrderResult HandleUnloadSquadrons(StepContext ctx, UnloadSquadronsOrder o)
    {
        if (!State.Fleets.TryGetValue(o.Fleet, out var fleet)) return OrderResult.Reject(RejectReason.UnknownFleet);
        if (fleet.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (CheckSquadronList(o.Side, o.Squadrons, out var squads) is { } bad) return OrderResult.Reject(bad);
        if (Moving(fleet)) return OrderResult.Reject(RejectReason.InTransit);
        if (squads.Any(s => !fleet.Ships.Contains(s.AboardShip))) return OrderResult.Reject(RejectReason.NotAboard);
        string system = fleet.At!;
        if (OwnerOf(system) != o.Side) return OrderResult.Reject(RejectReason.NotYourSystem); // DEFAULT: unload only where the side holds the system

        int day = ctx.Day;
        var carriers = squads.Select(s => s.AboardShip).ToList();
        foreach (var s in squads) { s.AboardShip = 0; s.System = system; }
        var ids = new ValueList<int>(squads.Select(s => s.Id).ToList());
        var from = new ValueList<int>(carriers);
        ctx.Emit((i, _) => new SquadronsUnloaded(i, day, ids, fleet.Id, from, system));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: m3-ships-and-fighters §Damaged state (DebugSetDamage; S13 BadValue for a hull above the class maximum)
    private OrderResult HandleDebugSetDamage(StepContext ctx, DebugSetDamageOrder o)
    {
        if (!DebugOrdersEnabled) return OrderResult.Reject(RejectReason.DebugDisabled);
        if (!E.Objects.TryGetValue(o.Ship, out var ship) || ship.Category != ObjectCategory.CapitalShip) return OrderResult.Reject(RejectReason.UnknownShip);
        if (ship.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        var cls = ClassOf(ship);
        if (cls == null || o.Hull < 0 || o.Hull > cls.Hull || o.Hits.Count != ObjectRegistry.HitCounters
            || o.Hits.Any(h => h < 0 || h > ObjectRegistry.MaxHit)) return OrderResult.Reject(RejectReason.BadValue);
        ship.Hull = o.Hull;
        ship.Hits = new List<int>(o.Hits);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // ------------------------------------------------------------------ step 1: MoveFleet

    private static string RejectStatus(PathStatus s) => s switch
    {
        PathStatus.Immobile => RejectReason.Immobile,
        PathStatus.AlreadyThere => RejectReason.AlreadyThere,
        PathStatus.UnknownSystem => RejectReason.UnknownSystem,
        PathStatus.InTransit => RejectReason.InTransit,
        _ => RejectReason.Unreachable,
    };

    // rule: m1-clock-and-orders §Orders in (MoveFleet and its rejections); m1-travel §Path rule, §Orders in transit
    private OrderResult HandleMoveFleet(StepContext ctx, MoveFleetOrder o)
    {
        if (!State.Fleets.TryGetValue(o.FleetId, out var fleet)) return OrderResult.Reject(RejectReason.UnknownFleet);
        if (fleet.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        // An accepted order marks the fleet in transit at once, so a second order in the same Step lands here (K6, K7).
        if (fleet.Transit != null || fleet.At == null) return OrderResult.Reject(RejectReason.InTransit);

        var path = PlanPath(fleet, o.Destination, o.Waypoints?.Items); // rule: m1-travel §Path rule (3), (5): the same function as the preview
        if (!path.Ok) return OrderResult.Reject(RejectStatus(path.Status));

        var names = path.Names(Galaxy).ToList();
        fleet.Transit = new FleetTransit
        {
            Path = names, HopTicks = path.HopTicks.ToList(), HopIndex = 0,
            DueDay = ctx.Day + path.HopTicks[0], // first hop is due on day K + hop ticks
        };
        fleet.Spare = false; // rule: m3-fleets §New ships: the spare fleet (cleared when a MoveFleet is accepted)
        MoveAccepted?.Invoke(ctx, fleet);
        int arrival = ctx.Day + path.TotalTicks;
        int total = path.TotalTicks;
        int id = fleet.Id;
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, id, new ValueList<string>(names), total, arrival));
    }

    // ------------------------------------------------------------------ new ships and removed ships

    /// <summary>
    /// A unit came to exist for use (completed at its yard's system in slot 6.6.a, or delivered in step 3). A capital ship joins the spare
    /// fleet of its side at its system, which is created now if there is none; squadrons stay based and regiments stay on the system, in no
    /// fleet. GameHost wires <c>ConstructionModule.UnitAppeared</c> to this. rule: m3-fleets §New ships: the spare fleet (F1, F2, F22).
    /// </summary>
    public void OnUnitAppeared(StepContext ctx, EconObject obj, ShipJoinCause cause)
    {
        if (obj.Category != ObjectCategory.CapitalShip || !obj.Completed || obj.Side is not Side side || obj.System is not string sys) return;
        int day = ctx.Day + 1; // hook time T = K + 1
        var fleet = State.Fleets.Values.FirstOrDefault(f => f.Spare && f.Side == side && f.At == sys && f.Transit == null);
        if (fleet == null)
        {
            fleet = NewFleet(side, sys, new[] { obj.Id }, spare: true);
            int fid = fleet.Id;
            var members = new ValueList<int>(new List<int> { obj.Id });
            ctx.Emit((i, _) => new FleetCreated(i, day, fid, sys, members, FleetCreateCause.Spare));
        }
        else AddSorted(fleet.Ships, obj.Id);
        obj.System = null; // a fleet member has no place of its own
        int shipId = obj.Id, flt = fleet.Id; string cls = obj.Class;
        ctx.Emit((i, _) => new ShipJoinedFleet(i, day, shipId, cls, sys, flt, cause));
    }

    /// <summary>
    /// An object left the registry (scrapped, autoscrapped, cancelled; the economy calls this after removing it). A ship leaves its fleet and a
    /// fleet left empty is removed (<see cref="DissolveCause.Empty"/>); regiments and squadrons aboard land on the fleet's system (a fleet on a
    /// lane: the system it last left), squadrons becoming based there, in the same removal; a squadron of a squadron-only fleet leaves that
    /// fleet. <paramref name="ctx"/> null = silent (setup, cancelled work). rule: m3-fleets §What a fleet is invariant 1; §Carrying rules
    /// (DEFAULT: scrapping a loaded ship puts its units on the system, held or not). The lane case is a DEFAULT the sheet lacks.
    /// </summary>
    internal void ObjectRemoved(StepContext? ctx, int day, EconObject o)
    {
        if (o.Category == ObjectCategory.CapitalShip)
        {
            var fleet = FleetOfShip(o.Id);
            string? place = fleet?.At ?? fleet?.Transit?.Path[fleet.Transit.HopIndex] ?? o.System;
            var landed = new List<EconObject>();
            foreach (var u in E.Objects.Values.Where(u => u.AboardShip == o.Id).ToList())
            {
                u.AboardShip = 0; u.System = place; landed.Add(u);
            }
            var squads = landed.Where(u => u.Category == ObjectCategory.Fighter).ToList();
            if (ctx != null && squads.Count > 0 && place != null)
            {
                int fid = fleet?.Id ?? 0, ship = o.Id;
                var ids = new ValueList<int>(squads.Select(u => u.Id).ToList());
                var carriers = new ValueList<int>(squads.Select(_ => ship).ToList());
                ctx.Emit((i, _) => new SquadronsBased(i, day, ids, fid, carriers, place));
            }
            var regs = landed.Where(u => u.Category == ObjectCategory.Regiment).ToList();
            if (ctx != null && regs.Count > 0 && place != null)
            {
                int fid = fleet?.Id ?? 0, ship = o.Id;
                var ids = new ValueList<int>(regs.Select(u => u.Id).ToList());
                var carriers = new ValueList<int>(regs.Select(_ => ship).ToList());
                ctx.Emit((i, _) => new Troops.RegimentsLanded(i, day, ids, fid, carriers, place)); // m3-troops: aboard regiments land on the last-left system (DEFAULT)
            }
            if (fleet != null)
            {
                fleet.Ships.Remove(o.Id);
                if (fleet.Ships.Count == 0 && fleet.Squadrons.Count == 0) RemoveFleet(ctx, day, fleet, DissolveCause.Empty);
            }
        }
        else if (o.Category == ObjectCategory.Fighter && o.AboardShip == 0 && o.System == null)
        {
            var fleet = State.Fleets.Values.FirstOrDefault(f => f.Squadrons.Contains(o.Id));
            if (fleet != null)
            {
                fleet.Squadrons.Remove(o.Id);
                if (fleet.Ships.Count == 0 && fleet.Squadrons.Count == 0) RemoveFleet(ctx, day, fleet, DissolveCause.Empty);
            }
        }
    }

    // ------------------------------------------------------------------ step 2: departures

    // rule: m1-clock-and-orders §Order of work, step 2 (sort: fleet id). Before step 3 a fleet that has Transit and is
    // still At a system has just had its order accepted, so it departs now. (Deliveries, M2: hook here, after fleets,
    // by delivery id.)
    private void Departures(StepContext ctx)
    {
        foreach (var f in State.Fleets.Values) // SortedDictionary: fleet id order
        {
            if (f.Transit == null || f.At == null) continue;
            string from = f.At;
            int first = f.Transit.HopTicks[0];
            f.PreviousSystem = from;
            f.At = null;
            ctx.Emit((i, d) => new FleetDeparted(i, d, f.Id, from, first));
        }
    }

    // ------------------------------------------------------------------ step 3: arrivals

    // rule: m1-clock-and-orders §Order of work, step 3 (sort: fleet id, then deliveries by delivery id, M2: hook here).
    // Every fleet whose hop ends at day K + 1 is placed at the system it reaches. A fleet whose trip ends there is done;
    // the others stay placed (Transit still set) as "passing" until step 4 judges them.
    private void Arrivals(StepContext ctx)
    {
        int arrivalDay = ctx.Day + 1;
        foreach (var f in State.Fleets.Values)
        {
            if (f.Transit is not { } t || f.At != null || t.DueDay != arrivalDay) continue;
            string sys = t.Path[t.HopIndex + 1];
            f.At = sys;
            if (t.HopIndex + 1 == t.Path.Count - 1)
            {
                f.Transit = null;
                ctx.Emit((i, _) => new FleetArrived(i, arrivalDay, f.Id, sys));
            }
        }
    }

    // ------------------------------------------------------------------ step 4: forced stops

    // rule: m1-clock-and-orders §Order of work, step 4 (sort: system name ordinal, then fleet id, then deliveries, M2: hook
    // here, paused at an enemy fleet per Paul D2). All fleets placed in step 3 count as present, enemy fleets that arrived
    // the same day included; fleets that departed in step 2 do not. Decisions are taken on one snapshot per system, so
    // two enemy fleets passing the same system on the same day both stop (the rule is symmetric).
    private void ForcedStops(StepContext ctx)
    {
        int day = ctx.Day + 1;
        var present = PresentBySystem();
        foreach (var (sys, here) in present)
        {
            var passing = here.Where(f => f.Transit != null).ToList(); // placed in step 3, trip not over: passing
            if (passing.Count == 0) continue;
            foreach (var f in passing)
            {
                var enemies = here.Where(e => e.Side != f.Side).ToList();
                var blockers = Blockers(f, enemies);
                if (blockers.Count > 0)
                {
                    // rule: m1-forced-stop-and-blockade §Rule (both rulesets) 5 (the order is cancelled; the fleet stays)
                    f.Transit = null;
                    var ids = new ValueList<int>(blockers.Select(b => b.Id).ToList());
                    ctx.Emit((i, _) => new ForcedStop(i, day, f.Id, sys, ids));
                    continue;
                }
                var t = f.Transit!;
                t.HopIndex++;
                t.DueDay = day + t.HopTicks[t.HopIndex]; // next hop starts now: due on day K + 1 + next hop ticks
                f.PreviousSystem = sys;
                f.At = null;
                ctx.Emit((i, _) => new FleetPassed(i, day, f.Id, sys));
            }
        }
    }

    /// <summary>
    /// The enemy fleets that stop <paramref name="passing"/>, or none. Only armed enemy ships count; the passing fleet need
    /// not be armed; ownership of the system is ignored; the passing fleet's own gravity-well ship does not exempt it
    /// (DEFAULT, Paul has not decided). rule: m1-forced-stop-and-blockade §Update 2026-10-12, §Rule (both rulesets) 3 and 6.
    /// </summary>
    internal List<Fleet> Blockers(Fleet passing, List<Fleet> enemies) => Blockers(StrengthUnits(passing), enemies);

    /// <summary>The same for a passing force given by its classes (a delivery counts as a fleet with only its own unit, full health). rule: m2-construction (same armed test and ForcedStopTrigger as fleets).</summary>
    internal List<Fleet> Blockers(IReadOnlyList<ShipClass> passing, List<Fleet> enemies) =>
        Blockers(passing.Select(c => Strength.Unit(c)).ToList(), enemies);

    private List<Fleet> Blockers(List<Combatant> passing, List<Fleet> enemies)
    {
        var armed = enemies.Where(e => CombatStrength.IsArmed(Classes(e))).ToList();
        if (armed.Count == 0) return armed;
        var gravityFleets = armed.Where(e => Classes(e).Any(c => c.GravityWellProjector > 0)).ToList();
        switch (Trigger)
        {
            case ForcedStopTrigger.AnyArmedFleet:
                return armed;
            case ForcedStopTrigger.GravityWellShip:
                return gravityFleets;
            default: // PaulRule
                if (gravityFleets.Count > 0) return armed;
                // rule: m4-battle-new §12 (forced stop by strength): X stops Y if CS(X, Y) > 0 and CS(X, Y) >= T x CS(Y, X). T is a percent from data.
                // DEFAULT: a delivery that carries no ship (a regiment, a special force, a facility: cargo) has nothing to measure and is stopped by any armed enemy
                // (as under the placeholder: m1 rule 6, the passing force need not be armed; m2-construction C21)
                if (passing.Count == 0) return armed;
                var enemyUnits = armed.SelectMany(StrengthUnits).ToList();
                double enemy = Strength.Of(enemyUnits, passing);
                if (enemy <= 0) return new List<Fleet>();
                double mine = Strength.Of(passing, enemyUnits);
                return enemy * 100.0 >= Blockade.ThresholdPercent * mine ? armed : new List<Fleet>();
        }
    }

    // ------------------------------------------------------------------ step 5: contacts and blockade flags

    // rule: m1-clock-and-orders §Order of work, step 5 (sort: system name ordinal). One Contact per system per day while
    // both sides are present (DEFAULT, all fleets of a side are one force); then the Classic blockade flags are recomputed.
    // First, rule: m3-fleets §Where it runs: a squadron-only fleet that is at a system after step 4 (arrived or stopped) dissolves
    // into based squadrons, in fleet id order, before the contacts.
    private void ContactsAndBlockades(StepContext ctx)
    {
        int day = ctx.Day + 1;
        DissolveLandedSquadronFleets(ctx, day);

        var present = PresentBySystem();
        foreach (var (sys, here) in present)
        {
            var emp = here.Where(f => f.Side == Side.Hand).Select(f => f.Id).ToList();
            var all = here.Where(f => f.Side == Side.Collective).Select(f => f.Id).ToList();
            if (emp.Count > 0 && all.Count > 0)
                ctx.Emit((i, _) => new Contact(i, day, sys, new ValueList<int>(emp), new ValueList<int>(all)));
        }

        // rule: m1-forced-stop-and-blockade §Rule (Classic). The flag depends only on presence and owner, so recomputing
        // every system that has fleets or a raised flag gives the same flags as recomputing only the changed ones.
        var systems = new SortedSet<string>(present.Keys, StringComparer.Ordinal);
        foreach (var k in State.Blockades.Keys) systems.Add(k);
        foreach (var sys in systems) RefreshBlockadeFlag(ctx, sys, day);
    }

    // rule: m3-fleets §Unescorted squadrons (squadron-only fleets dissolve after step 4: FleetDissolved cause SquadronsArrived, SquadronsBased)
    private void DissolveLandedSquadronFleets(StepContext ctx, int day)
    {
        foreach (var f in State.Fleets.Values.Where(f => f.Ships.Count == 0 && f.Squadrons.Count > 0 && f.At != null && f.Transit == null).ToList())
        {
            string sys = f.At!;
            foreach (var id in f.Squadrons) E.Objects[id].System = sys;
            var ids = new ValueList<int>(f.Squadrons.ToList());
            var none = new ValueList<int>(f.Squadrons.Select(_ => 0).ToList());
            int fid = f.Id;
            FleetRemoving?.Invoke(ctx, day, f, DissolveCause.SquadronsArrived, null);
            State.Fleets.Remove(fid);
            ctx.Emit((i, _) => new FleetDissolved(i, day, fid, DissolveCause.SquadronsArrived));
            ctx.Emit((i, _) => new SquadronsBased(i, day, ids, fid, none, sys));
        }
    }

    /// <summary>
    /// Recompute one system's Classic blockade flag now and raise BlockadeLifted / BlockadeStarted if it changed. Step 5 calls it for
    /// every system; the capture package calls it when a system changes owner. rule: m1-forced-stop-and-blockade §Rule (Classic);
    /// m2-control-and-capture §Capture steps (8).
    /// </summary>
    public void RefreshBlockadeFlag(StepContext ctx, string sys, int day)
    {
        Side? now = ClassicBlockader(sys);
        bool was = State.Blockades.TryGetValue(sys, out var wasSide);
        if (was && now == wasSide) return;
        if (was)
        {
            State.Blockades.Remove(sys);
            ctx.Emit((i, _) => new BlockadeLifted(i, day, sys, wasSide));
        }
        if (now is Side s)
        {
            State.Blockades[sys] = s;
            ctx.Emit((i, _) => new BlockadeStarted(i, day, sys, s));
        }
    }

    /// <summary>
    /// The Classic blockade flag: true when no battle bit is set (not both sides present, a fleet or a based fighter squadron
    /// counting as presence), the system is owned by a side (neutral systems are never blockaded) and a fleet of the other side is
    /// there, armed or not (the original has no armed test). rule: m1-forced-stop-and-blockade §Rule (Classic) conditions 1 to 3;
    /// m3-fleets §Fighters based at a system (based squadrons count as presence); Traced ai/data/trace-d 1.1.
    /// </summary>
    public bool IsBlockadedClassic(string system) => ClassicBlockader(system) != null;

    private Side? ClassicBlockader(string system)
    {
        if (!State.SystemOwners.TryGetValue(system, out var owner)) return null; // neutral
        var here = FleetsAt(system);
        var other = Other(owner);
        if (!here.Any(f => f.Side == other)) return null; // condition 3: a fleet of the other side
        bool ownerPresent = here.Any(f => f.Side == owner) || BasedSquadrons(system, owner).Any();
        return ownerPresent ? null : other; // both sides present: battle bit on
    }

    /// <summary>
    /// Economic blockade of a system against a side: the other side has armed ships there and either a live gravity-well ship
    /// or combat strength at least E. The economic effects themselves are M2. rule: m1-forced-stop-and-blockade §Update 2026-10-12.
    /// </summary>
    public bool IsEconomicBlockade(string system, Side againstSide)
    {
        var blockers = FleetsAt(system).Where(f => f.Side == Other(againstSide)).ToList();
        if (!CombatStrength.IsArmed(Classes(blockers))) return false;
        if (Classes(blockers).Any(c => c.GravityWellProjector > 0)) return true;
        // rule: m4-battle-new §12 (economic blockade by strength): CS(blockers, REF) >= E, REF = 3 Mandate (DEFAULT)
        return Strength.Of(blockers.SelectMany(StrengthUnits), _reference.Select(c => Strength.Unit(c))) >= EconomicStrength;
    }

    private static Side Other(Side s) => s == Side.Hand ? Side.Collective : Side.Hand;

    /// <summary>Fleets placed at a system, grouped by system in ordinal name order, each group by fleet id.</summary>
    private SortedDictionary<string, List<Fleet>> PresentBySystem()
    {
        var by = new SortedDictionary<string, List<Fleet>>(StringComparer.Ordinal);
        foreach (var f in State.Fleets.Values)
        {
            if (f.At == null) continue;
            if (!by.TryGetValue(f.At, out var list)) by[f.At] = list = new List<Fleet>();
            list.Add(f);
        }
        return by;
    }
}
