using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;
using Rebellion.Core.Travel;

namespace Rebellion.Core.Fleets;

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
public sealed class FleetModule
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
    /// (Classic). The character module sets it for the New-rule Navigator. rule: m5-characters-and-force §Navigator; m1-travel §Rule (New).
    /// </summary>
    public Func<Fleet, int, int>? SpeedAdjust { get; set; }
    /// <summary>A MoveFleet was accepted (step 1, after the transit is set). The character module listens (OfficersRideFleet). rule: m5-characters-and-force §Riding.</summary>
    public event Action<StepContext, Fleet>? MoveAccepted;
    /// <summary>
    /// A fleet is about to leave the state (ctx null = silent: setup or cancelled work). The int? is the fleet that received its ships (a merge or a
    /// transfer that emptied it), else null. The character module listens. rule: m5-characters-and-force §Riding (fleet operations).
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
    /// ships get ids from the shared object counter (the fleet first, then the ships and the squadrons, each in class-name order), or
    /// <paramref name="id"/> for the fleet. <paramref name="squadrons"/> are created aboard the fleet's ships, each on the first ship with a
    /// free berth by ship id, and must fit. Call it between Steps. rule: m3-fleets §What a fleet is; §Carrying rules.
    /// </summary>
    public int AddFleet(Side side, string name, IEnumerable<string> ships, string system, IEnumerable<string>? squadrons = null, int? id = null)
    {
        // Sorted by class name (ordinal) so the same fleet always gets the same ids whatever the order it was listed in (canonical save and hash).
        var shipList = ships.OrderBy(c => c, StringComparer.Ordinal).ToList();
        var squadList = (squadrons ?? Array.Empty<string>()).OrderBy(c => c, StringComparer.Ordinal).ToList();
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
    /// its squadrons' ratings. rule: m3-fleets §Fleet speed; m1-travel §Rule (Classic); the New-rule Navigator is applied through <see cref="SpeedAdjust"/> (m5-characters-and-force).
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
        if (SpeedAdjust != null) speed = SpeedAdjust(fleet, speed); // rule: m5-characters-and-force §Navigator (fixed when the order is accepted)
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

    /// <summary>A fleet's strength against the economic reference force REF (3 ISD): the absolute measure E is compared with. rule: m4-battle-new §12.</summary>
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
    /// rule: m5-death-star §2 effect 3 (nearest system its side holds, fewest lane steps, ties by system id); used also by the capture (B5a).
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
        foreach (var side in new[] { Side.Alliance, Side.Empire })
        {
            if (side == holder) continue;
            var squads = BasedSquadrons(system, side).OrderBy(o => o.Id).ToList();
            if (squads.Count == 0) continue;
            var dest = NearestHeldSystem(system, side);
            if (dest == null) continue;
            int speed = TravelFormula.FleetSpeed(Array.Empty<int>(), squads.Select(SpeedOfShip));
            var path = _pathfinder.Find(system, dest, speed);
            if (!path.Ok) continue;
            int day = ctx.Day;
            foreach (var q in squads) q.System = null;
            int fid = ObjectRegistry.NextId(E);
            var f = new Fleet
            {
                Id = fid, Name = FleetName(fid), Side = side, Squadrons = squads.Select(q => q.Id).ToList(), At = system,
                Transit = new FleetTransit { Path = path.Names(Galaxy).ToList(), HopTicks = path.HopTicks.ToList(), HopIndex = 0, DueDay = day + path.HopTicks[0] },
            };
            State.Fleets[fid] = f;
            var members = new ValueList<int>(f.Squadrons.ToList());
            string from = system;
            ctx.Emit((i, _) => new FleetCreated(i, day, fid, from, members, FleetCreateCause.Squadrons));
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
    /// Battle write-back (New rules): ships of a retreating fleet that a tractor beam kept in the battle stay at the fleet's system in a new fleet;
    /// the rest of the fleet relocates. Not for the whole fleet (that fleet simply stays). Emits FleetCreated (cause TractorHeld). Returns the new fleet id.
    /// rule: m4-retreat-damage-repair §6.
    /// </summary>
    internal int SplitOffHeld(StepContext ctx, int day, Fleet fleet, IReadOnlyCollection<int> ships)
    {
        string system = fleet.At!;
        foreach (var s in ships) fleet.Ships.Remove(s);
        var f = NewFleet(fleet.Side, system, ships);
        var members = new ValueList<int>(f.Ships.ToList());
        ctx.Emit((i, _) => new FleetCreated(i, day, f.Id, system, members, FleetCreateCause.TractorHeld));
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
    /// The Classic (1998) blockade flag: true when no battle bit is set (not both sides present, a fleet or a based fighter squadron
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
        // rule: m4-battle-new §12 (economic blockade by strength): CS(blockers, REF) >= E, REF = 3 ISD (DEFAULT)
        return Strength.Of(blockers.SelectMany(StrengthUnits), _reference.Select(c => Strength.Unit(c))) >= EconomicStrength;
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
