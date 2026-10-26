using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Map;
using Praedra.Core.Ships;
using Praedra.Core.Travel;

namespace Praedra.Core.Fleets;

/// <summary>
/// Fleets: the MoveFleet order and movement along lanes (step 1 to 5), forced stops, contacts and the Classic blockade flag, and, from
/// M3, fleets made of real registry ships: the spare fleet for new ships, CreateFleet, SplitFleet, TransferShips, MergeFleets,
/// MoveSquadrons (unescorted squadrons), LoadSquadrons, UnloadSquadrons, DebugSetDamage, the carrying capacity, and the removal of a
/// scrapped ship from its fleet. Registers itself on a <see cref="Game"/>; holds only data and settings, never game state (that lives
/// in <see cref="GameState"/>: the fleets and the one object registry in <see cref="GameState.Economy"/>). After
/// <see cref="GameSave.Load"/>, attach again to the loaded game. Movement and every fleet order draw ZERO random numbers.
/// rule: m1-clock-and-orders §Seeded random numbers rule 3; m3-fleets §Where it runs and what it draws.
/// The module needs only the registry state, not the economy module, so it also runs alone (tests, the viewer).
/// </summary>
public sealed partial class FleetModule
{
    private readonly Game _game;
    private readonly IReadOnlyDictionary<string, ShipClass> _ships;
    private readonly Pathfinder _pathfinder;

    public Galaxy Galaxy { get; }
    /// <summary>How ships travel in this game (configuration, not saved). rule: m1-travel §Switch.</summary>
    public TravelMode Travel { get; }
    public BlockadeData Blockade { get; }
    /// <summary>The forced-stop trigger. A game setting; DEFAULT GravityWellShip (Paul 2026-10-16). Not part of the save (it is configuration).</summary>
    public ForcedStopTrigger Trigger { get; set; }
    /// <summary>E, computed once from ship data. rule: m1-forced-stop-and-blockade §Update 2026-10-12.</summary>
    public double EconomicStrength { get; }
    /// <summary>The strength measure (m4-battle-new §12) with this game's per-weapon data.</summary>
    public CombatStrength Strength { get; }
    private readonly List<ShipClass> _reference;
    /// <summary>DebugSetDamage is accepted only when this is on (same switch as DebugSetHolder). DEFAULT off. rule: m3-ships-and-fighters §Damaged state.</summary>
    public bool DebugOrdersEnabled { get; set; }

    /// <summary>
    /// Adjusts a fleet's speed rating at the moment a MoveFleet is accepted (given the fleet and its data rating). Null = the data rating
    /// (Classic). The character module sets it for the New-rule Navigator. rule: m5-characters-and-talent §Navigator; m1-travel §Rule (New).
    /// </summary>
    public Func<Fleet, int, int>? SpeedAdjust { get; set; }
    /// <summary>A MoveFleet was accepted (step 1, after the transit is set). The character module listens (OfficersRideFleet). rule: m5-characters-and-talent §Riding.</summary>
    public event Action<StepContext, Fleet>? MoveAccepted;
    /// <summary>
    /// A fleet is about to leave the state (ctx null = silent: setup or cancelled work). The int? is the fleet that received its ships (a merge or a
    /// transfer that emptied it), else null. The character module listens. rule: m5-characters-and-talent §Riding (fleet operations).
    /// </summary>
    public event Action<StepContext?, int, Fleet, DissolveCause, int?>? FleetRemoving;

    private FleetModule(Game game, Galaxy galaxy, IReadOnlyDictionary<string, ShipClass> ships, BlockadeData blockade, ForcedStopTrigger trigger, NewBattleData combat, TravelMode travel)
    {
        _game = game; Galaxy = galaxy; _ships = ships; Blockade = blockade; Trigger = trigger; Travel = travel;
        _pathfinder = new Pathfinder(galaxy, travel);
        Strength = new CombatStrength(combat);
        _reference = Blockade.Reference(ships).ToList();
        EconomicStrength = blockade.EconomicStrength(ships, Strength);
    }

    /// <summary>Register the fleet order handlers and the hooks for work-list steps 2 to 5 on <paramref name="game"/>.</summary>
    public static FleetModule Attach(Game game, Galaxy galaxy, IReadOnlyDictionary<string, ShipClass> ships, BlockadeData blockade,
        ForcedStopTrigger trigger = ForcedStopTrigger.GravityWellShip, NewBattleData? combat = null, TravelMode travel = TravelMode.Routes)
    {
        var m = new FleetModule(game, galaxy, ships, blockade, trigger, combat ?? NewBattleData.Default, travel);
        game.RegisterOrderHandler<MoveFleetOrder>(m.HandleMoveFleet);
        game.RegisterOrderHandler<CreateFleetOrder>(m.HandleCreateFleet);
        game.RegisterOrderHandler<SplitFleetOrder>(m.HandleSplitFleet);
        game.RegisterOrderHandler<TransferShipsOrder>(m.HandleTransferShips);
        game.RegisterOrderHandler<MergeFleetsOrder>(m.HandleMergeFleets);
        game.RegisterOrderHandler<MoveSquadronsOrder>(m.HandleMoveSquadrons);
        game.RegisterOrderHandler<LoadSquadronsOrder>(m.HandleLoadSquadrons);
        game.RegisterOrderHandler<UnloadSquadronsOrder>(m.HandleUnloadSquadrons);
        game.RegisterOrderHandler<DebugSetDamageOrder>(m.HandleDebugSetDamage);
        game.RegisterHook("2", m.Departures);
        game.RegisterHook("3", m.Arrivals);
        game.RegisterHook("4", m.ForcedStops);
        game.RegisterHook("5", m.ContactsAndBlockades);
        return m;
    }

    private GameState State => _game.State;
    private EconomyState E => _game.State.Economy;

    // ------------------------------------------------------------------ setup and queries (between Steps)

    /// <summary>
    /// Setup only: place a new fleet of new, completed ships at a system (games and tests; not an order, raises no event). The fleet and its
    /// ships get ids from the shared object counter (the fleet first, then the ships and the squadrons, each in class-name order, or in the
    /// order given when <paramref name="listedOrder"/> is set), or <paramref name="id"/> for the fleet. <paramref name="squadrons"/> are created aboard the fleet's ships, each on the first ship with a
    /// free berth by ship id, and must fit. Call it between Steps. rule: m3-fleets §What a fleet is; §Carrying rules.
    /// </summary>
    public int AddFleet(Side side, string name, IEnumerable<string> ships, string system, IEnumerable<string>? squadrons = null, int? id = null,
        bool listedOrder = false)
    {
        // Sorted by class name (ordinal) so the same fleet always gets the same ids whatever the order it was listed in (canonical save and hash).
        // listedOrder: the caller's order is already fixed (a data file), so ids do not depend on class names and a rename leaves the game as it was.
        var shipList = listedOrder ? ships.ToList() : ships.OrderBy(c => c, StringComparer.Ordinal).ToList();
        var squadList = listedOrder ? (squadrons ?? Array.Empty<string>()).ToList()
            : (squadrons ?? Array.Empty<string>()).OrderBy(c => c, StringComparer.Ordinal).ToList();
        // rule: m3-fleets §What a fleet is, invariants 1 and 4 (never empty; ships, or only squadrons that fly)
        if (shipList.Count == 0) throw new ArgumentException("A fleet needs at least one capital ship (squadrons ride aboard ships).");
        foreach (var c in shipList)
            if (!_ships.TryGetValue(c, out var k) || !k.IsCapital) throw new ArgumentException($"'{c}' is not a capital ship class.");
        foreach (var c in squadList)
            if (!_ships.TryGetValue(c, out var k) || k.IsCapital) throw new ArgumentException($"'{c}' is not a squadron class.");
        if (!Galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        if (squadList.Count > shipList.Sum(c => _ships[c].FighterCapacity))
            throw new ArgumentException($"The fleet's ships have {shipList.Sum(c => _ships[c].FighterCapacity)} berths for {squadList.Count} squadrons.");

        int fleetId = id is int x ? ObjectRegistry.Take(E, x) : ObjectRegistry.NextId(E);
        var shipIds = shipList.Select(c => NewObject(ObjectCategory.CapitalShip, c, side, null)).ToList();
        var fleet = new Fleet { Id = fleetId, Name = name, Side = side, Ships = shipIds.OrderBy(i => i).ToList(), At = system };
        State.Fleets[fleetId] = fleet;
        var free = fleet.Ships.ToDictionary(s => s, s => _ships[E.Objects[s].Class].FighterCapacity);
        foreach (var c in squadList)
        {
            int sq = NewObject(ObjectCategory.Fighter, c, side, null);
            int carrier = fleet.Ships.First(s => free[s] > 0); // first ship with a free berth, ascending id (m3-fleets DEFAULT)
            free[carrier]--;
            E.Objects[sq].AboardShip = carrier;
        }
        return fleetId;
    }

    /// <summary>
    /// Setup only: put completed ships that are already in the registry (not in any fleet) into a new fleet at a system. Ids as in
    /// <see cref="AddFleet"/>. Call it between Steps.
    /// </summary>
    public int AddFleetOf(Side side, string name, string system, IEnumerable<int> shipIds, int? id = null)
    {
        var ids = shipIds.OrderBy(i => i).ToList();
        if (ids.Count == 0) throw new ArgumentException("A fleet needs at least one ship.");
        if (ids.Distinct().Count() != ids.Count) throw new ArgumentException("A ship may not be listed twice.");
        if (!Galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        foreach (var s in ids)
        {
            if (!E.Objects.TryGetValue(s, out var o) || o.Category != ObjectCategory.CapitalShip) throw new ArgumentException($"Object {s} is not a ship.");
            if (o.Side != side) throw new ArgumentException($"Ship {s} is not of side {side}.");
            if (!o.Completed) throw new ArgumentException($"Ship {s} is not completed.");
            if (FleetOfShip(s) != null) throw new ArgumentException($"Ship {s} is already in a fleet.");
        }
        int fleetId = id is int x ? ObjectRegistry.Take(E, x) : ObjectRegistry.NextId(E);
        foreach (var s in ids) E.Objects[s].System = null;
        State.Fleets[fleetId] = new Fleet { Id = fleetId, Name = name, Side = side, Ships = ids, At = system };
        return fleetId;
    }

    private int NewObject(ObjectCategory cat, string className, Side side, string? system)
    {
        var o = new EconObject { Category = cat, Class = className, Side = side, System = system, Completed = true };
        return ObjectRegistry.Add(E, o, null, _ships[className]);
    }

    /// <summary>Setup only: set a system's owner; null makes it neutral (DEFAULT for every system).</summary>
    public void SetOwner(string system, Side? owner)
    {
        if (!Galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        if (owner is Side s) State.SystemOwners[system] = s; else State.SystemOwners.Remove(system);
    }

    public Side? OwnerOf(string system) => State.SystemOwners.TryGetValue(system, out var s) ? s : null;

    /// <summary>Queue a MoveFleet order (validated in the next Step). Returns its sequence number.</summary>
    public long SubmitMove(Side side, int fleetId, string destination, IReadOnlyList<string>? waypoints = null) =>
        _game.Submit(seq => new MoveFleetOrder(seq, side, fleetId, destination, waypoints is { Count: > 0 } ? new ValueList<string>(waypoints.ToList()) : null));

    public long SubmitCreateFleet(Side side, IEnumerable<int> ships) => _game.Submit(seq => new CreateFleetOrder(seq, side, ships.ToList()));
    public long SubmitSplitFleet(Side side, int fleet, IEnumerable<int> ships) => _game.Submit(seq => new SplitFleetOrder(seq, side, fleet, ships.ToList()));
    public long SubmitTransferShips(Side side, IEnumerable<int> ships, int toFleet) => _game.Submit(seq => new TransferShipsOrder(seq, side, ships.ToList(), toFleet));
    public long SubmitMergeFleets(Side side, int from, int into) => _game.Submit(seq => new MergeFleetsOrder(seq, side, from, into));
    public long SubmitMoveSquadrons(Side side, IEnumerable<int> squadrons, string destination) =>
        _game.Submit(seq => new MoveSquadronsOrder(seq, side, squadrons.ToList(), destination));
    public long SubmitLoadSquadrons(Side side, int fleet, IEnumerable<int> squadrons) =>
        _game.Submit(seq => new LoadSquadronsOrder(seq, side, fleet, squadrons.ToList()));
    public long SubmitUnloadSquadrons(Side side, int fleet, IEnumerable<int> squadrons) =>
        _game.Submit(seq => new UnloadSquadronsOrder(seq, side, fleet, squadrons.ToList()));
    public long SubmitDebugSetDamage(Side side, int ship, int hull, IEnumerable<int> hits) =>
        _game.Submit(seq => new DebugSetDamageOrder(seq, side, ship, hull, hits.ToList()));

    /// <summary>Fleets placed at a system (not on a lane), by fleet id.</summary>
    public IReadOnlyList<Fleet> FleetsAt(string system) =>
        State.Fleets.Values.Where(f => f.At == system).ToList();

    /// <summary>The fleet a ship is a member of, or null (a ship that is queued, in delivery or set up outside any fleet).</summary>
    public Fleet? FleetOfShip(int shipId) => State.Fleets.Values.FirstOrDefault(f => f.Ships.Contains(shipId));

    /// <summary>The class row of a registry object, or null if the class is not a ship or fighter class.</summary>
    public ShipClass? ClassOf(EconObject o) => _ships.TryGetValue(o.Class, out var c) ? c : null;

    /// <summary>Speed rating of one ship now: its hyperdrive, or hyperdrive_if_damaged when its hyperdrive counter is above 0. rule: m3-ships-and-fighters §Hyperdrive and speed.</summary>
    public int SpeedOfShip(EconObject ship) =>
        (ClassOf(ship) ?? throw new InvalidOperationException($"No ship class '{ship.Class}'.")).Speed(ObjectRegistry.IsDamagedForTravel(ship));

    /// <summary>
    /// Speed rating of a fleet (0 = immobile): the largest rating among its ships; squadrons aboard never count; a squadron-only fleet uses
    /// its squadrons' ratings. rule: m3-fleets §Fleet speed; m1-travel §Rule (Classic); the New-rule Navigator is applied through <see cref="SpeedAdjust"/> (m5-characters-and-talent).
    /// </summary>
    public int SpeedOf(Fleet f) =>
        TravelFormula.FleetSpeed(f.Ships.Select(id => SpeedOfShip(E.Objects[id])), f.Squadrons.Select(id => SpeedOfShip(E.Objects[id])));

    /// <summary>Capital ship and squadron classes by name (construction reads hyperdrive ratings and combat data from here).</summary>
    public IReadOnlyDictionary<string, ShipClass> ShipClasses => _ships;

    /// <summary>Fastest lane path for a given speed rating (construction deliveries travel like fleets). rule: m1-travel §Path rule.</summary>
    public PathResult FindPath(string from, string to, int speed) => _pathfinder.Find(from, to, speed);
    /// <summary>
    /// The path a MoveFleet order for this fleet would fly if accepted now: the fleet's speed (navigator included), from where it stands,
    /// through the waypoints in order, then to the destination; the fastest path on each stretch. Rejected like the order would be
    /// (in transit, immobile, unknown system, unreachable, already there). The order handler uses this same function, so the preview is
    /// the path flown. rule: m1-travel §Path rule (3) (waypoints) and (5) (preview).
    /// </summary>
    public PathResult PlanPath(Fleet fleet, string destination, IReadOnlyList<string>? waypoints = null)
    {
        if (fleet.Transit != null || fleet.At == null) return PathResult.Rejected(PathStatus.InTransit);
        int speed = SpeedOf(fleet);
        if (SpeedAdjust != null) speed = SpeedAdjust(fleet, speed); // rule: m5-characters-and-talent §Navigator (fixed when the order is accepted)
        return FindVia(fleet.At, waypoints, destination, speed);
    }

    /// <summary>
    /// Fastest path through stops in order. A waypoint equal to where the path already is (the start, or the stop before it) is skipped;
    /// a waypoint equal to the destination is skipped; any stretch that fails fails the whole. rule: m1-travel §Path rule (3) (waypoints;
    /// DEFAULT for repeats, the sheet names none).
    /// </summary>
    public PathResult FindVia(string from, IReadOnlyList<string>? waypoints, string to, int speed)
    {
        if (waypoints is not { Count: > 0 }) return _pathfinder.Find(from, to, speed);
        var systems = new List<int>(); var ticks = new List<int>();
        string at = from;
        var stops = waypoints.Append(to).ToList();
        for (int k = 0; k < stops.Count; k++)
        {
            string w = stops[k];
            if (!Galaxy.TryFindSystem(w, out _)) return PathResult.Rejected(PathStatus.UnknownSystem);
            if (w == at || (k < stops.Count - 1 && w == to)) continue;
            var leg = _pathfinder.Find(at, w, speed);
            if (!leg.Ok) return leg;
            if (systems.Count == 0) systems.Add(leg.Systems[0]);
            for (int i = 1; i < leg.Systems.Count; i++) systems.Add(leg.Systems[i]);
            ticks.AddRange(leg.HopTicks);
            at = w;
        }
        if (ticks.Count == 0) return _pathfinder.Find(from, to, speed); // every stop was the start: the plain answer (AlreadyThere, Immobile)
        return PathResult.Found(systems.ToArray(), ticks.ToArray());
    }

    /// <summary>The fastest path with no stop at the given systems on the way (see Pathfinder.FindAvoiding).</summary>
    public PathResult FindPathAvoiding(int from, int to, int speed, IReadOnlyCollection<int> avoid) => _pathfinder.FindAvoiding(from, to, speed, avoid);

    /// <summary>A fleet's strength against the economic reference force REF (3 Mandate): the absolute measure E is compared with. rule: m4-battle-new §12.</summary>
    public double StrengthOf(Fleet f) => Strength.Of(StrengthUnits(f), _reference.Select(c => Strength.Unit(c)));

    /// <summary>
    /// The units of a fleet for the strength measure: its ships with their stored hull ratio and weapons counter, and every squadron flying with it or
    /// aboard its ships (aboard squadrons count as if launched). rule: m4-battle-new §12 (hull ratio and weapons counter come from the strategic ship object).
    /// </summary>
    internal List<Combatant> StrengthUnits(Fleet f)
    {
        var list = new List<Combatant>();
        foreach (var id in f.Ships)
        {
            var o = E.Objects[id]; var cls = ClassOf(o)!;
            double ratio = cls.Hull > 0 ? Math.Min(1.0, (double)o.Hull / cls.Hull) : 1.0;
            list.Add(Strength.Unit(cls, ratio, o.Hits is { Count: > 1 } ? o.Hits[1] : 0));
        }
        foreach (var id in f.Squadrons) list.Add(Strength.Unit(ClassOf(E.Objects[id])!));
        foreach (var o in E.Objects.Values.Where(o => o.Category == ObjectCategory.Fighter && o.AboardShip != 0 && f.Ships.Contains(o.AboardShip)).OrderBy(o => o.Id))
            list.Add(Strength.Unit(ClassOf(o)!));
        return list;
    }

    /// <summary>
    /// The classes of a fleet's members, for the armed test and combat strength: its ships, or its flying squadrons. Squadrons aboard
    /// are not counted: the carrier arms the fleet (always armed in the data). rule: m3-fleets §Fighters based at a system versus aboard a carrier.
    /// </summary>
    public IEnumerable<ShipClass> Classes(Fleet f) => f.Ships.Concat(f.Squadrons).Select(id => ClassOf(E.Objects[id])!);
    private IEnumerable<ShipClass> Classes(IEnumerable<Fleet> fs) => fs.SelectMany(Classes);

    /// <summary>The class names of everything in a fleet and aboard its ships (ships, squadrons aboard or flying), for display. Ships first, ids ascending.</summary>
    public IEnumerable<string> ClassNamesOf(Fleet f)
    {
        foreach (var id in f.Ships) yield return E.Objects[id].Class;
        foreach (var id in f.Squadrons) yield return E.Objects[id].Class;
        foreach (var u in E.Objects.Values)
            if (u.Category == ObjectCategory.Fighter && u.AboardShip != 0 && f.Ships.Contains(u.AboardShip)) yield return u.Class;
    }

    // ---- carrying (m3-fleets §Carrying rules). The troop side (LoadRegiments, UnloadRegiments, RegimentsLoaded...) is the
    // troops sheet's package (m3-troops-and-special-forces); it uses these queries and sets EconObject.AboardShip / System itself.

    /// <summary>Units of a category aboard one ship (squadrons: Fighter; regiments: Regiment), by object id.</summary>
    public IEnumerable<EconObject> UnitsAboard(int shipId, ObjectCategory category) =>
        E.Objects.Values.Where(u => u.AboardShip == shipId && u.Category == category);

    /// <summary>Free berths per ship id of a fleet (fighter_capacity less squadrons aboard). rule: m3-fleets §Carrying rules.</summary>
    public SortedDictionary<int, int> FreeBerthsByShip(Fleet f) => FreeByShip(f, ObjectCategory.Fighter, c => c.FighterCapacity);

    /// <summary>Free troop slots per ship id of a fleet (troop_capacity less regiments aboard). The hook for the troops sheet's LoadRegiments. rule: m3-fleets §Carrying rules.</summary>
    public SortedDictionary<int, int> FreeTroopSlotsByShip(Fleet f) => FreeByShip(f, ObjectCategory.Regiment, c => c.TroopCapacity);

    public int FreeBerths(Fleet f) => FreeBerthsByShip(f).Values.Sum();
    public int FreeTroopSlots(Fleet f) => FreeTroopSlotsByShip(f).Values.Sum();

    private SortedDictionary<int, int> FreeByShip(Fleet f, ObjectCategory cat, Func<ShipClass, int> capacity)
    {
        var free = new SortedDictionary<int, int>();
        foreach (var id in f.Ships) free[id] = capacity(ClassOf(E.Objects[id])!);
        foreach (var u in E.Objects.Values)
            if (u.Category == cat && u.AboardShip != 0 && free.ContainsKey(u.AboardShip)) free[u.AboardShip]--;
        return free;
    }

    /// <summary>
    /// The system <paramref name="side"/> holds that is fewest lane steps from <paramref name="from"/> (breadth-first over every lane, through any
    /// system; ties by system index, which is name order); the start itself never counts. Null if the side holds no reachable system.
    /// rule: m5-harvester §2 effect 3 (nearest system its side holds, fewest lane steps, ties by system id); used also by the capture (B5a).
    /// </summary>
    public string? NearestHeldSystem(string from, Side side)
    {
        var g = Galaxy;
        if (!g.TryFindSystem(from, out int start)) return null;
        var seen = new bool[g.Systems.Count];
        var level = new List<int> { start };
        seen[start] = true;
        while (level.Count > 0)
        {
            var held = level.Where(i => i != start && State.SystemOwners.TryGetValue(g.Systems[i].Name, out var o) && o == side).ToList();
            if (held.Count > 0) return g.Systems[held.Min()].Name;
            var next = new List<int>();
            foreach (var i in level)
                foreach (var nb in g.NearOf(i))
                    if (!seen[nb]) { seen[nb] = true; next.Add(nb); }
            level = next;
        }
        return null;
    }

    /// <summary>
    /// For the capture: the squadrons of every side but the new holder based at a world that just changed hands (completed, on it, not aboard, not
    /// flying) leave for the nearest system their side holds (Autorouting, B5a section 5: where they go was not found; DEFAULT the characters' rule).
    /// One squadron-only fleet per side, flying as a MoveSquadrons order would (same speed, path and events). Unfinished squadrons and squadrons in
    /// transit stay; with no held system, no path or speed 0 they stay. rule: m2-control-and-capture §What happens at the moment of capture (B5a, 2026-10-18).
    /// </summary>
    public void SendBasedSquadronsAway(StepContext ctx, string system, Side? holder)
    {
        foreach (var side in new[] { Side.Collective, Side.Hand })
        {
            if (side == holder) continue;
            var squads = BasedSquadrons(system, side).OrderBy(o => o.Id).ToList();
            if (squads.Count == 0) continue;
            var dest = NearestHeldSystem(system, side);
            if (dest == null) continue;
            int speed = TravelFormula.FleetSpeed(Array.Empty<int>(), squads.Select(SpeedOfShip));
            var path = _pathfinder.Find(system, dest, speed);
            if (!path.Ok) continue;
            int day = ctx.Day, first = path.HopTicks[0];
            foreach (var q in squads) q.System = null;
            int fid = ObjectRegistry.NextId(E);
            // A capture runs after step 2 (assaults, missions, support at day end), so no step 2 will depart this fleet in time: it takes off at
            // once (on the lane, PreviousSystem set, as step 2 leaves a fleet) and its first hop counts from the next day, as the characters leaving
            // at capture do. Left "at" the system with a trip set, step 4 took it for a passing fleet (index past the path: QA/AI trainer crash,
            // 2026-10-18) or step 3 never matched its due day. rule: m1-clock-and-orders §Order of work, step 2.
            var f = new Fleet
            {
                Id = fid, Name = FleetName(fid), Side = side, Squadrons = squads.Select(q => q.Id).ToList(), At = null, PreviousSystem = system,
                Transit = new FleetTransit { Path = path.Names(Galaxy).ToList(), HopTicks = path.HopTicks.ToList(), HopIndex = 0, DueDay = day + 1 + first },
            };
            State.Fleets[fid] = f;
            var members = new ValueList<int>(f.Squadrons.ToList());
            string from = system;
            ctx.Emit((i, _) => new FleetCreated(i, day, fid, from, members, FleetCreateCause.Squadrons));
            ctx.Emit((i, d) => new FleetDeparted(i, d, fid, from, first));
        }
    }

    /// <summary>Completed squadrons of a side based at a system (on it, not aboard, not flying), by id. rule: m3-fleets §Fighters based at a system.</summary>
    public IEnumerable<EconObject> BasedSquadrons(string system, Side side) =>
        E.Objects.Values.Where(o => o.Category == ObjectCategory.Fighter && o.Completed && o.Side == side && o.System == system && o.AboardShip == 0);

    // ------------------------------------------------------------------ common order checks

    // A fleet is movable/editable only at a system with no accepted order (an accepted move marks Transit at once). rule: m3-fleets §Orders.
    private static bool Moving(Fleet f) => f.Transit != null || f.At == null;

    /// <summary>Where a ship is: its fleet's system (null on a lane), or, with no fleet, the system on the object.</summary>
    private (Fleet? Fleet, string? System, bool Moving) PlaceOfShip(EconObject ship)
    {
        var f = FleetOfShip(ship.Id);
        return f != null ? (f, f.At, Moving(f)) : (null, ship.System, false);
    }

    // Duplicate, then unknown, then not-yours, in that order. rule: m3-fleets §Orders (common rejections).
    private string? CheckShipList(Side side, List<int> ids, out List<EconObject> ships)
    {
        ships = new List<EconObject>();
        if (ids.Distinct().Count() != ids.Count) return RejectReason.DuplicateShip;
        foreach (var id in ids)
        {
            if (!E.Objects.TryGetValue(id, out var o) || o.Category != ObjectCategory.CapitalShip) return RejectReason.UnknownShip;
            ships.Add(o);
        }
        if (ships.Any(o => o.Side != side)) return RejectReason.NotYours;
        return null;
    }

    private string? CheckSquadronList(Side side, List<int> ids, out List<EconObject> squads)
    {
        squads = new List<EconObject>();
        if (ids.Distinct().Count() != ids.Count) return RejectReason.DuplicateSquadron;
        foreach (var id in ids)
        {
            if (!E.Objects.TryGetValue(id, out var o) || o.Category != ObjectCategory.Fighter) return RejectReason.UnknownSquadron;
            squads.Add(o);
        }
        if (squads.Any(o => o.Side != side)) return RejectReason.NotYours;
        return null;
    }

    /// <summary>
    /// Battle write-back (New rules): ships of a retreating fleet that a Hold beam kept in the battle stay at the fleet's system in a new fleet;
    /// the rest of the fleet relocates. Not for the whole fleet (that fleet simply stays). Emits FleetCreated (cause HoldBeamHeld). Returns the new fleet id.
    /// rule: m4-retreat-damage-repair §6.
    /// </summary>
    internal int SplitOffHeld(StepContext ctx, int day, Fleet fleet, IReadOnlyCollection<int> ships)
    {
        string system = fleet.At!;
        foreach (var s in ships) fleet.Ships.Remove(s);
        var f = NewFleet(fleet.Side, system, ships);
        var members = new ValueList<int>(f.Ships.ToList());
        ctx.Emit((i, _) => new FleetCreated(i, day, f.Id, system, members, FleetCreateCause.HoldBeamHeld));
        return f.Id;
    }

    private static string FleetName(int id) => $"Fleet {id}";

    private Fleet NewFleet(Side side, string system, IEnumerable<int> ships, bool spare = false)
    {
        int id = ObjectRegistry.NextId(E);
        var f = new Fleet { Id = id, Name = spare ? "Spare Fleet" : FleetName(id), Side = side, Ships = ships.OrderBy(i => i).ToList(), At = system, Spare = spare };
        State.Fleets[id] = f;
        return f;
    }

    private static void AddSorted(List<int> list, int id)
    {
        int i = list.BinarySearch(id);
        if (i < 0) list.Insert(~i, id);
    }

    /// <summary>Remove a fleet from the state; the event only when there is a Step to carry it. rule: m3-fleets §What a fleet is invariant 1.</summary>
    private void RemoveFleet(StepContext? ctx, int day, Fleet f, DissolveCause cause, int? into = null)
    {
        FleetRemoving?.Invoke(ctx, day, f, cause, into);
        State.Fleets.Remove(f.Id);
        if (ctx == null) return;
        int id = f.Id;
        ctx.Emit((i, _) => new FleetDissolved(i, day, id, cause));
    }

    /// <summary>Take ships out of whatever fleets hold them; fleets left empty are removed (cause Empty). Returns the fleet ids they left, ascending.</summary>
    private List<int> TakeOut(StepContext? ctx, int day, IEnumerable<EconObject> ships, int? into = null)
    {
        var left = new SortedSet<int>();
        foreach (var s in ships)
        {
            var f = FleetOfShip(s.Id);
            if (f == null) continue;
            f.Ships.Remove(s.Id);
            left.Add(f.Id);
        }
        foreach (var fid in left)
            if (State.Fleets.TryGetValue(fid, out var f) && f.Ships.Count == 0 && f.Squadrons.Count == 0) RemoveFleet(ctx, day, f, DissolveCause.Empty, into);
        return left.ToList();
    }
}
