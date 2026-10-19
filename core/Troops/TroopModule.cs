using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;

namespace Praedra.Core.Troops;

/// <summary>A regiment aboard a ship of a fleet that stands at a system: what the assault sheet needs. Sorted by regiment id.</summary>
public sealed record AboardRegiment(EconObject Regiment, int Ship, Fleet Fleet);

/// <summary>
/// Troops: LoadRegiments and UnloadRegiments (step 1) and the creation events of regiments and special forces. Regiments and special forces
/// are registry objects (<see cref="EconObject"/>) built by the construction module at a Training Facility; this module owns only the
/// two carrying orders and the queries the assault package calls. Holds no game state (a regiment's place is
/// <see cref="EconObject.System"/> or <see cref="EconObject.AboardShip"/>). Draws ZERO random numbers.
/// rule: m3-troops-and-special-forces §Orders, §Where it runs and what it draws (T22).
/// Special forces are never aboard a ship in M3 (DEFAULT): no order here accepts one (they are UnknownRegiment).
/// </summary>
public sealed class TroopModule
{
    private readonly Game _game;
    private readonly FleetModule _fleets;

    public TroopData Data { get; }

    /// <summary>
    /// UnloadRegiments is accepted only at a system the side holds. DEFAULT on. Off lets troops stand on any system the fleet is at, which skips
    /// the assault (a test and tooling switch, not a rule). rule: m3-troops-and-special-forces §Defaults (unload only at a held system).
    /// </summary>
    public bool UnloadOnlyAtHeldSystem { get; set; } = true;

    private TroopModule(Game game, FleetModule fleets, TroopData data) { _game = game; _fleets = fleets; Data = data; }

    /// <summary>Register the LoadRegiments and UnloadRegiments handlers on <paramref name="game"/>.</summary>
    public static TroopModule Attach(Game game, FleetModule fleets, TroopData data)
    {
        var m = new TroopModule(game, fleets, data);
        game.RegisterOrderHandler<LoadRegimentsOrder>(m.HandleLoad);
        game.RegisterOrderHandler<UnloadRegimentsOrder>(m.HandleUnload);
        return m;
    }

    private GameState State => _game.State;
    private EconomyState E => _game.State.Economy;

    public long SubmitLoad(Side side, int fleet, IEnumerable<int> regiments) =>
        _game.Submit(seq => new LoadRegimentsOrder(seq, side, fleet, regiments.ToList()));
    public long SubmitUnload(Side side, int fleet, IEnumerable<int> regiments) =>
        _game.Submit(seq => new UnloadRegimentsOrder(seq, side, fleet, regiments.ToList()));

    // ------------------------------------------------------------------ queries (the assault package's interface)

    /// <summary>
    /// Completed regiments of <paramref name="side"/> riding aboard capital ships of that side's fleets that stand at <paramref name="system"/>
    /// (not in transit), ascending by regiment id, each with its ship and fleet. This is the attacker list of the assault (m3-assault §Rule,
    /// step 0). Special forces are never in it. rule: m3-troops-and-special-forces §Where regiments live; m3-assault check 2.
    /// </summary>
    public List<AboardRegiment> RegimentsAboardAt(string system, Side side)
    {
        var shipFleet = new Dictionary<int, Fleet>(); // lookup only, never iterated
        foreach (var f in State.Fleets.Values)
            if (f.Side == side && f.At == system && f.Transit == null)
                foreach (var s in f.Ships) shipFleet[s] = f;
        var list = new List<AboardRegiment>();
        foreach (var o in E.Objects.Values) // SortedDictionary: id order
            if (o.Category == ObjectCategory.Regiment && o.Completed && o.Side == side && o.AboardShip != 0 && shipFleet.TryGetValue(o.AboardShip, out var f))
                list.Add(new AboardRegiment(o, o.AboardShip, f));
        return list;
    }

    /// <summary>Regiments aboard the ships of one fleet, ascending by regiment id.</summary>
    public List<AboardRegiment> RegimentsAboard(Fleet fleet)
    {
        var ships = new HashSet<int>(fleet.Ships); // lookup only
        return E.Objects.Values
            .Where(o => o.Category == ObjectCategory.Regiment && o.AboardShip != 0 && ships.Contains(o.AboardShip))
            .Select(o => new AboardRegiment(o, o.AboardShip, fleet)).ToList();
    }

    /// <summary>
    /// Completed regiments standing on a system (landed, not aboard) that belong to any side but <paramref name="exceptSide"/> (null = every side),
    /// ascending by id: the defenders list of the assault (m3-assault step 0).
    /// </summary>
    public List<EconObject> RegimentsOnSystem(string system, Side? exceptSide = null) =>
        E.Objects.Values.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.System == system && o.AboardShip == 0
            && (exceptSide == null || o.Side != exceptSide)).ToList();

    /// <summary>
    /// Land a regiment that is aboard a ship: it stands on <paramref name="system"/> and is no longer aboard. The assault calls this for the
    /// attackers that win a system (it raises its own events). The caller has checked the rules; this only moves the regiment.
    /// </summary>
    public static void Land(EconObject regiment, string system)
    {
        regiment.AboardShip = 0;
        regiment.System = system;
    }

    // ------------------------------------------------------------------ creation events

    /// <summary>
    /// A unit came to exist for use (completed or delivered): announce regiments and special forces. GameHost wires
    /// <c>ConstructionModule.UnitAppeared</c> to this next to <see cref="FleetModule.OnUnitAppeared"/>. The unit stands on its system, in no fleet
    /// and aboard nothing. rule: m3-troops-and-special-forces §Training Facility production, §Where it runs (RegimentCreated, SpecialForceCreated).
    /// </summary>
    public void OnUnitAppeared(StepContext ctx, EconObject obj)
    {
        if (!obj.Completed || obj.System is not string sys) return;
        int day = ctx.Day + 1; // hook time T = K + 1
        int id = obj.Id; string cls = obj.Class;
        if (obj.Category == ObjectCategory.Regiment) ctx.Emit((i, _) => new RegimentCreated(i, day, id, cls, sys));
        else if (obj.Category == ObjectCategory.SpecialForce) ctx.Emit((i, _) => new SpecialForceCreated(i, day, id, cls, sys));
    }

    // ------------------------------------------------------------------ orders

    private static bool Moving(Fleet f) => f.Transit != null || f.At == null;

    // Common checks. Reasons in this order: UnknownFleet, NotYours (fleet), UnknownRegiment (also a special force or any other object),
    // NotYours (regiment), DuplicateRegiment, EmptySelection, InTransit, NotCompleted. The sheet lists the reasons, not their order (DEFAULT).
    private string? Common(Side side, int fleetId, List<int> ids, out Fleet fleet, out List<EconObject> regs)
    {
        regs = new List<EconObject>();
        fleet = null!;
        if (!State.Fleets.TryGetValue(fleetId, out var f)) return RejectReason.UnknownFleet;
        fleet = f;
        if (f.Side != side) return RejectReason.NotYours;
        foreach (var id in ids)
            if (!E.Objects.TryGetValue(id, out var o) || o.Category != ObjectCategory.Regiment) return RejectReason.UnknownRegiment;
        foreach (var id in ids) if (E.Objects[id].Side != side) return RejectReason.NotYours;
        if (ids.Distinct().Count() != ids.Count) return RejectReason.DuplicateRegiment;
        if (ids.Count == 0) return RejectReason.EmptySelection;
        if (Moving(f)) return RejectReason.InTransit;
        regs = ids.Select(id => E.Objects[id]).ToList();
        if (regs.Any(r => !r.Completed)) return RejectReason.NotCompleted;
        return null;
    }

    // rule: m3-troops-and-special-forces §Orders (LoadRegiments): the fleet is at a system; every regiment is on that same system and of the
    // fleet's side; free troop slots at least the count (all or nothing); listed order to ships by ascending id, first free slot.
    private OrderResult HandleLoad(StepContext ctx, LoadRegimentsOrder o)
    {
        if (Common(o.Side, o.Fleet, o.Regiments, out var fleet, out var regs) is { } bad) return OrderResult.Reject(bad);
        if (regs.Any(r => r.AboardShip != 0)) return OrderResult.Reject(RejectReason.AlreadyAboard);
        if (regs.Any(r => r.System != fleet.At)) return OrderResult.Reject(RejectReason.NotAtFleetSystem);
        var free = _fleets.FreeTroopSlotsByShip(fleet);
        if (free.Values.Sum() < regs.Count) return OrderResult.Reject(RejectReason.NoCapacity);

        int day = ctx.Day;
        string system = fleet.At!;
        var ships = new List<int>();
        foreach (var r in regs)
        {
            int ship = free.First(kv => kv.Value > 0).Key; // SortedDictionary: ascending ship id
            free[ship]--;
            r.AboardShip = ship; r.System = null;
            ships.Add(ship);
        }
        var ids = new ValueList<int>(regs.Select(r => r.Id).ToList());
        var on = new ValueList<int>(ships);
        ctx.Emit((i, _) => new RegimentsLoaded(i, day, ids, fleet.Id, on, system));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: m3-troops-and-special-forces §Orders (UnloadRegiments): every regiment is aboard a ship of that fleet; the fleet is at a system,
    // not in transit; the system is held by the side (DEFAULT; a neutral or enemy system is the assault's, m3-assault).
    private OrderResult HandleUnload(StepContext ctx, UnloadRegimentsOrder o)
    {
        if (Common(o.Side, o.Fleet, o.Regiments, out var fleet, out var regs) is { } bad) return OrderResult.Reject(bad);
        if (regs.Any(r => r.AboardShip == 0 || !fleet.Ships.Contains(r.AboardShip))) return OrderResult.Reject(RejectReason.NotAboard);
        string system = fleet.At!;
        if (UnloadOnlyAtHeldSystem && _fleets.OwnerOf(system) != o.Side) return OrderResult.Reject(RejectReason.NotYourSystem);

        int day = ctx.Day;
        var ships = regs.Select(r => r.AboardShip).ToList();
        foreach (var r in regs) Land(r, system);
        var ids = new ValueList<int>(regs.Select(r => r.Id).ToList());
        var from = new ValueList<int>(ships);
        ctx.Emit((i, _) => new RegimentsUnloaded(i, day, ids, fleet.Id, from, system));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }
}
