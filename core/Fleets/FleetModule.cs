using Rebellion.Core.Clock;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;
using Rebellion.Core.Travel;

namespace Rebellion.Core.Fleets;

/// <summary>
/// Fleets moving along lanes: the MoveFleet order (step 1), departures (2), arrivals (3), forced stops (4), contacts and
/// the Classic blockade flag (5). Registers itself on a <see cref="Game"/>; holds only data and settings, never game
/// state (that lives in <see cref="GameState"/>). After <see cref="GameSave.Load"/>, attach again to the loaded game.
/// Movement draws ZERO random numbers. rule: m1-clock-and-orders §Seeded random numbers rule 3.
/// </summary>
public sealed class FleetModule
{
    private readonly Game _game;
    private readonly IReadOnlyDictionary<string, ShipClass> _ships;
    private readonly Pathfinder _pathfinder;

    public Galaxy Galaxy { get; }
    public BlockadeData Blockade { get; }
    /// <summary>The forced-stop trigger. A game setting; DEFAULT PaulRule. Not part of the save (it is configuration).</summary>
    public ForcedStopTrigger Trigger { get; set; }
    /// <summary>E, computed once from ship data. rule: m1-forced-stop-and-blockade §Update 2026-10-12.</summary>
    public long EconomicStrength { get; }

    private FleetModule(Game game, Galaxy galaxy, IReadOnlyDictionary<string, ShipClass> ships, BlockadeData blockade, ForcedStopTrigger trigger)
    {
        _game = game; Galaxy = galaxy; _ships = ships; Blockade = blockade; Trigger = trigger;
        _pathfinder = new Pathfinder(galaxy);
        EconomicStrength = blockade.EconomicStrength(ships);
    }

    /// <summary>Register the MoveFleet handler and the hooks for work-list steps 2 to 5 on <paramref name="game"/>.</summary>
    public static FleetModule Attach(Game game, Galaxy galaxy, IReadOnlyDictionary<string, ShipClass> ships, BlockadeData blockade,
        ForcedStopTrigger trigger = ForcedStopTrigger.PaulRule)
    {
        var m = new FleetModule(game, galaxy, ships, blockade, trigger);
        game.RegisterOrderHandler<MoveFleetOrder>(m.HandleMoveFleet);
        game.RegisterHook("2", m.Departures);
        game.RegisterHook("3", m.Arrivals);
        game.RegisterHook("4", m.ForcedStops);
        game.RegisterHook("5", m.ContactsAndBlockades);
        return m;
    }

    private GameState State => _game.State;

    // ------------------------------------------------------------------ setup and queries (between Steps)

    /// <summary>
    /// Setup only: place a new fleet at a system (games and tests). Fleet creation as an in-game order is M3
    /// (m3-fleets); this is not an order and raises no event. Call it between Steps.
    /// </summary>
    public int AddFleet(Side side, string name, IEnumerable<string> ships, string system, IEnumerable<string>? squadrons = null)
    {
        var shipList = ships.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var squadList = (squadrons ?? Array.Empty<string>()).OrderBy(s => s, StringComparer.Ordinal).ToList();
        // rule: m3-fleets §What a fleet is, invariant 1 (never empty)
        if (shipList.Count + squadList.Count == 0) throw new ArgumentException("A fleet needs at least one ship or squadron.");
        foreach (var c in shipList)
            if (!_ships.TryGetValue(c, out var k) || !k.IsCapital) throw new ArgumentException($"'{c}' is not a capital ship class.");
        foreach (var c in squadList)
            if (!_ships.TryGetValue(c, out var k) || k.IsCapital) throw new ArgumentException($"'{c}' is not a squadron class.");
        if (!Galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        int id = State.NextFleetId++;
        State.Fleets[id] = new Fleet { Id = id, Name = name, Side = side, Ships = shipList, Squadrons = squadList, At = system };
        return id;
    }

    /// <summary>Setup only: set a system's owner; null makes it neutral (DEFAULT for every system).</summary>
    public void SetOwner(string system, Side? owner)
    {
        if (!Galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        if (owner is Side s) State.SystemOwners[system] = s; else State.SystemOwners.Remove(system);
    }

    public Side? OwnerOf(string system) => State.SystemOwners.TryGetValue(system, out var s) ? s : null;

    /// <summary>Queue a MoveFleet order (validated in the next Step). Returns its sequence number.</summary>
    public long SubmitMove(Side side, int fleetId, string destination) =>
        _game.Submit(seq => new MoveFleetOrder(seq, side, fleetId, destination));

    /// <summary>Fleets placed at a system (not on a lane), by fleet id.</summary>
    public IReadOnlyList<Fleet> FleetsAt(string system) =>
        State.Fleets.Values.Where(f => f.At == system).ToList();

    /// <summary>Speed rating of a fleet (0 = immobile). rule: m1-travel §Rule (Classic); navigators are M5.</summary>
    public int SpeedOf(Fleet f) =>
        TravelFormula.FleetSpeed(f.Ships.Select(c => _ships[c].Speed(false)), f.Squadrons.Select(c => _ships[c].Speed(false)));

    /// <summary>Capital ship and squadron classes by name (construction reads hyperdrive ratings and combat data from here).</summary>
    public IReadOnlyDictionary<string, ShipClass> ShipClasses => _ships;

    /// <summary>Fastest lane path for a given speed rating (construction deliveries travel like fleets). rule: m1-travel §Path rule.</summary>
    public PathResult FindPath(string from, string to, int speed) => _pathfinder.Find(from, to, speed);

    public long StrengthOf(Fleet f) => CombatStrength.Of(Classes(f));

    private IEnumerable<ShipClass> Classes(Fleet f) => f.Ships.Concat(f.Squadrons).Select(c => _ships[c]);
    private IEnumerable<ShipClass> Classes(IEnumerable<Fleet> fs) => fs.SelectMany(Classes);

    // ------------------------------------------------------------------ step 1: MoveFleet

    // rule: m1-clock-and-orders §Orders in (MoveFleet and its rejections); m1-travel §Path rule, §Orders in transit
    private OrderResult HandleMoveFleet(StepContext ctx, MoveFleetOrder o)
    {
        if (!State.Fleets.TryGetValue(o.FleetId, out var fleet)) return OrderResult.Reject(RejectReason.UnknownFleet);
        if (fleet.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        // An accepted order marks the fleet in transit at once, so a second order in the same Step lands here (K6, K7).
        if (fleet.Transit != null || fleet.At == null) return OrderResult.Reject(RejectReason.InTransit);

        var path = _pathfinder.Find(fleet.At, o.Destination, SpeedOf(fleet));
        if (!path.Ok)
        {
            return OrderResult.Reject(path.Status switch
            {
                PathStatus.Immobile => RejectReason.Immobile,
                PathStatus.AlreadyThere => RejectReason.AlreadyThere,
                PathStatus.UnknownSystem => RejectReason.UnknownSystem,
                _ => RejectReason.Unreachable,
            });
        }

        var names = path.Names(Galaxy).ToList();
        fleet.Transit = new FleetTransit
        {
            Path = names, HopTicks = path.HopTicks.ToList(), HopIndex = 0,
            DueDay = ctx.Day + path.HopTicks[0], // first hop is due on day K + hop ticks
        };
        int arrival = ctx.Day + path.TotalTicks;
        int total = path.TotalTicks;
        int id = fleet.Id;
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, id, new ValueList<string>(names), total, arrival));
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
    internal List<Fleet> Blockers(Fleet passing, List<Fleet> enemies)
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
                // M4 re-check: strength is the placeholder (sum of refined_material_cost of armed ships). T is a percent from data.
                long enemy = CombatStrength.Of(Classes(armed), Classes(passing));
                long mine = CombatStrength.Of(Classes(passing));
                return enemy * 100 >= (long)Blockade.ThresholdPercent * mine ? armed : new List<Fleet>();
        }
    }

    // ------------------------------------------------------------------ step 5: contacts and blockade flags

    // rule: m1-clock-and-orders §Order of work, step 5 (sort: system name ordinal). One Contact per system per day while
    // both sides are present (DEFAULT, all fleets of a side are one force); then the Classic blockade flags are recomputed.
    private void ContactsAndBlockades(StepContext ctx)
    {
        int day = ctx.Day + 1;
        var present = PresentBySystem();
        foreach (var (sys, here) in present)
        {
            var emp = here.Where(f => f.Side == Side.Empire).Select(f => f.Id).ToList();
            var all = here.Where(f => f.Side == Side.Alliance).Select(f => f.Id).ToList();
            if (emp.Count > 0 && all.Count > 0)
                ctx.Emit((i, _) => new Contact(i, day, sys, new ValueList<int>(emp), new ValueList<int>(all)));
        }

        // rule: m1-forced-stop-and-blockade §Rule (Classic). The flag depends only on presence and owner, so recomputing
        // every system that has fleets or a raised flag gives the same flags as recomputing only the changed ones.
        var systems = new SortedSet<string>(present.Keys, StringComparer.Ordinal);
        foreach (var k in State.Blockades.Keys) systems.Add(k);
        foreach (var sys in systems) RefreshBlockadeFlag(ctx, sys, day);
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
    /// The Classic (1998) blockade flag: true when no battle bit is set (not both sides present), the system is owned by a
    /// side (neutral systems are never blockaded) and a fleet of the other side is there, armed or not (the original has no
    /// armed test). rule: m1-forced-stop-and-blockade §Rule (Classic) conditions 1 to 3; Traced ai/data/trace-d 1.1.
    /// </summary>
    public bool IsBlockadedClassic(string system) => ClassicBlockader(system) != null;

    private Side? ClassicBlockader(string system)
    {
        if (!State.SystemOwners.TryGetValue(system, out var owner)) return null; // neutral
        var here = FleetsAt(system);
        var other = Other(owner);
        bool hasOwner = here.Any(f => f.Side == owner), hasOther = here.Any(f => f.Side == other);
        if (hasOwner && hasOther) return null; // battle bit on
        return hasOther ? other : null;
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
        return CombatStrength.Of(Classes(blockers)) >= EconomicStrength; // M4 re-check
    }

    private static Side Other(Side s) => s == Side.Empire ? Side.Alliance : Side.Empire;

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
