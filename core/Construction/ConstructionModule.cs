using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Construction;

/// <summary>How a finished unit came to stand where it stands (see <see cref="ConstructionModule.UnitAppeared"/>).</summary>
public enum UnitAppearance { Completed, Delivered }

/// <summary>
/// Construction: build orders, yard projects and queues, yard cycles paid through the refined line (slot 6.6), maintenance taken at
/// order time (the units exist from acceptance), deliveries along lanes (steps 3 and 4, after the fleets), cancel and starting
/// yards. Registers itself on a <see cref="Game"/> like <see cref="EconomyModule"/>; holds data and settings only, never game state
/// (that lives in <see cref="GameState.Construction"/> and the economy object list). After <see cref="GameSave.Load"/>, attach again.
/// Draws ZERO random numbers. rule: m2-construction (whole sheet).
///
/// Attach after <see cref="FleetModule.Attach"/> and <see cref="EconomyModule.Attach"/>: the hooks of steps 3 and 4 must run after the
/// fleets' (registration order), and the refined line is served from the economy's <see cref="EconomyModule.RefinedPoolGrew"/>.
/// </summary>
public sealed class ConstructionModule
{
    private readonly Game _game;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;
    private readonly Galaxy _galaxy;
    // Fleets placed at systems after step 3 of the Step being processed (the forced-stop snapshot). Rebuilt every Step, looked up by system only.
    private Dictionary<string, List<Fleet>> _present = new(StringComparer.Ordinal);

    public ConstructionParams Params { get; }

    /// <summary>Deliveries pause at an armed enemy fleet on an intermediate system. Decided yes (Paul 2026-10-12, D2), default on. rule: m2-construction §Completion and delivery.</summary>
    public bool DeliveryForcedStop { get; set; } = true;
    /// <summary>What happens when a delivery's destination is lost on the way. DEFAULT Pause (lead note); Refuse is the sheet body (C20).</summary>
    public LostDestinationRule LostDestination { get; set; } = LostDestinationRule.Pause;
    /// <summary>Ships, squadrons, regiments and special forces may only be ordered to a system the side controls (same rule as facilities). DEFAULT on (m3-troops-and-special-forces; the lead note says a delivery never lands on a foreign system). Off: any reachable system.</summary>
    public bool UnitDestinationMustBeControlled { get; set; } = true;
    /// <summary>
    /// The research stub (ResearchStub): off = AllAvailable, every item of the side counts as researched, NotResearched never fires (DEFAULT since M3,
    /// Decided by Paul); on = RespectResearchOrder, an item needs research order 0 or a setup grant (the m2 stub, used by the construction tests).
    /// rule: m3-ships-and-fighters §Sides and availability (Research stub).
    /// </summary>
    public bool RespectResearchOrder { get; set; }
    /// <summary>The Death Star class row can be ordered at a Shipyard. DEFAULT true; off: the order is rejected NotAvailable. rule: m3-ships-and-fighters §Death Star as an ordinary class row (S14).</summary>
    public bool DeathStarClassBuildable { get; set; } = true;
    /// <summary>Yards stop under the Classic 1998 blockade flag (<see cref="FleetModule.IsBlockadedClassic"/>) instead of the economic blockade. DEFAULT off. rule: m2-construction §Suspension.</summary>
    public bool UseClassicBlockadeFlag { get; set; }
    /// <summary>
    /// Is the system in uprising (its yards start no new cycle)? The flag belongs to the uprising sheet (m2-uprisings-and-garrison); this package
    /// sets nothing, so the default answers no. rule: m2-construction §Suspension.
    /// </summary>
    public Func<string, bool> IsInUprising { get; set; } = _ => false;
    /// <summary>
    /// Raised when a unit comes to exist for use: completed at the yard's own system (slot 6.6.a) or delivered (step 3 or 4), after it is
    /// marked completed. GameHost wires it to FleetModule.OnUnitAppeared, which makes the spare fleet (m3-fleets). Facilities are announced too (category Facility).
    /// </summary>
    public event Action<StepContext, EconObject, UnitAppearance>? UnitAppeared;

    private ConstructionModule(Game game, FleetModule fleets, EconomyModule eco, ConstructionParams p)
    {
        _game = game; _fleets = fleets; _eco = eco; _galaxy = fleets.Galaxy; Params = p;
    }

    /// <summary>Register the BuildOrder and StopBuild handlers, the delivery hooks of steps 3 and 4, and slot 6.6.</summary>
    public static ConstructionModule Attach(Game game, FleetModule fleets, EconomyModule eco, ConstructionParams? p = null, Support.SupportModule? support = null)
    {
        var m = new ConstructionModule(game, fleets, eco, p ?? new ConstructionParams());
        if (support != null) m.IsInUprising = support.IsUprising; // yards stop in an uprising (m2-uprisings-and-garrison)
        game.RegisterOrderHandler<BuildOrder>(m.HandleBuild);
        game.RegisterOrderHandler<StopBuildOrder>(m.HandleStop);
        game.RegisterHook("3", m.DeliveryArrivals);
        game.RegisterHook("4", m.DeliveryStops);
        game.RegisterHook("6.6", m.YardsHook);
        eco.RefinedPoolGrew += (ctx, side) => m.ServeRefinedLine(ctx, ctx.Day + 1, side);
        eco.ObjectRemoved += m.OnObjectRemoved;
        return m;
    }

    private GameState State => _game.State;
    private ConstructionState C => _game.State.Construction;
    private EconomyState E => _game.State.Economy;

    // ================================================================== setup and queries (between Steps)

    /// <summary>Setup only: grant a class to a side (the research stub). rule: m2-construction §Build order (research gate DEFAULT).</summary>
    public void GrantResearch(Side side, string className)
    {
        _eco.Costs.Get(className); // throws on an unknown class
        var list = C.Of(side).Granted;
        if (list.Contains(className)) return;
        list.Add(className);
        list.Sort(StringComparer.Ordinal);
    }

    public bool IsResearched(Side side, string className)
    {
        var row = _eco.Costs.Get(className);
        return !RespectResearchOrder || row.ResearchOrder == 0 || C.Of(side).Granted.Contains(className);
    }

    /// <summary>The project of a (system, family), if any.</summary>
    public Project? ProjectAt(string system, YardFamily family) => C.Find(system, family);
    public IReadOnlyCollection<Delivery> Deliveries => C.Deliveries.Values;
    public IReadOnlyList<int> RefinedLine(Side side) => C.Of(side).RefinedLine;

    /// <summary>Queue a BuildOrder (validated in the next Step). Returns its sequence number.</summary>
    public long SubmitBuild(Side side, string system, YardFamily family, string className, int count, string? destination = null) =>
        _game.Submit(seq => new BuildOrder(seq, side, system, family, className, count, destination));

    public long SubmitStop(Side side, string system, YardFamily family) =>
        _game.Submit(seq => new StopBuildOrder(seq, side, system, family));

    /// <summary>
    /// Days one standard-rate yard set needs for cost <paramref name="cost"/> with <paramref name="yards"/> identical yards of
    /// <paramref name="rate"/> days per cycle: ceil(C / y) x rate. rule: m2-construction §Build time.
    /// </summary>
    public static int BuildDays(int cost, int yards, int rate) => (cost + yards - 1) / yards * rate;

    /// <summary>
    /// Setup only (M5 calls it after control is final): place the data list of starting yards as finished facilities. The list's side
    /// must be the system's owner (null = neutral). rule: m2-construction §Starting yards.
    /// </summary>
    public void ApplyStartingFacilities(IEnumerable<StartingFacility> list)
    {
        foreach (var f in list)
        {
            if (!_galaxy.TryFindSystem(f.System, out _)) throw new ArgumentException($"Unknown system '{f.System}'.");
            if (_fleets.OwnerOf(f.System) != f.Side)
                throw new InvalidOperationException($"Starting facility on {f.System} is for {f.Side?.ToString() ?? "Neutral"} but the system is held by {_fleets.OwnerOf(f.System)?.ToString() ?? "nobody"}.");
            _eco.AddFacility(f.System, f.Kind);
        }
    }

    // ================================================================== helpers

    private IEnumerable<EconObject> YardObjects() =>
        E.Objects.Values.Where(o => o.Category == ObjectCategory.Facility && o.Completed && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k));

    private static FacilityKind KindOf(EconObject yard) => FacilityKinds.TryParse(yard.Class, out var k) ? k : throw new InvalidOperationException("Not a facility class.");

    private IEnumerable<EconObject> YardsOf(string system, YardFamily family) =>
        YardObjects().Where(o => o.System == system && Yards.FamilyOf(KindOf(o)) == family);

    private int RateOf(EconObject yard) => Yards.IsAdvanced(KindOf(yard)) ? Params.AdvancedCycleDays : Params.StandardCycleDays;

    private int CostOf(int objectId) => _eco.Costs.Get(E.Objects[objectId].Class).Cost;

    private bool Controls(string system, Side side) => _fleets.OwnerOf(system) == side;

    /// <summary>Delivery speed rating: a capital ship's hyperdrive; everything else the unrated speed. rule: m2-construction §Completion and delivery.</summary>
    private int DeliverySpeed(CostRow row) =>
        row.Category == ObjectCategory.CapitalShip && _fleets.ShipClasses.TryGetValue(row.Name, out var sc) ? sc.Speed(false) : Params.UnratedDeliverySpeed;

    /// <summary>A yard starts no new cycle under blockade or uprising (a neutral or unowned yard never works). rule: m2-construction §Suspension.</summary>
    private bool IsSuspended(EconObject yard)
    {
        if (yard.System == null || _fleets.OwnerOf(yard.System) is not Side owner) return true;
        if (IsInUprising(yard.System)) return true;
        return UseClassicBlockadeFlag ? _fleets.IsBlockadedClassic(yard.System) : _fleets.IsEconomicBlockade(yard.System, owner);
    }

    // ================================================================== step 1: BuildOrder, StopBuild

    // rule: m2-construction §Build order (rejections in the sheet's order; a rejection changes nothing)
    private OrderResult HandleBuild(StepContext ctx, BuildOrder o)
    {
        if (!_galaxy.TryFindSystem(o.System, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if (!_eco.Costs.TryGet(o.Class, out var row)) return OrderResult.Reject(RejectReason.UnknownItem);
        if (_fleets.OwnerOf(o.System) != o.Side) return OrderResult.Reject(RejectReason.NotYours); // also a neutral system (C24)
        if (!YardsOf(o.System, o.Family).Any()) return OrderResult.Reject(RejectReason.NoYard);
        if (Yards.BuiltBy(row.Category) != o.Family) return OrderResult.Reject(RejectReason.WrongFamily);
        // rule: m2-construction §Build order (NotAvailable: the file's side flag; cost 0 is not buildable, m3-troops-and-special-forces §Training Facility production)
        if (!(o.Side == Side.Empire ? row.Empire : row.Alliance) || row.Cost <= 0) return OrderResult.Reject(RejectReason.NotAvailable);
        if (o.Class == EconomyModule.DeathStarClass && !DeathStarClassBuildable) return OrderResult.Reject(RejectReason.NotAvailable); // rule: m3-ships-and-fighters §Death Star (S14)
        if (!IsResearched(o.Side, o.Class)) return OrderResult.Reject(RejectReason.NotResearched);
        if (o.Count < 1) return OrderResult.Reject(RejectReason.BadCount);

        bool facility = row.Category == ObjectCategory.Facility;
        string? dest = facility ? o.Destination : o.Destination ?? o.System; // a facility needs a destination
        if (dest == null) return OrderResult.Reject(RejectReason.BadDestination);
        if (!_galaxy.TryFindSystem(dest, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if ((facility || UnitDestinationMustBeControlled) && !Controls(dest, o.Side)) return OrderResult.Reject(RejectReason.BadDestination);

        var old = C.Find(o.System, o.Family);
        if (facility)
        {
            // The order replaces the old project, so its reservations count as free. rule: m2-construction §Projects and queues
            int energyFree = _eco.FreeEnergy(dest), rawFree = _eco.FreeRaw(dest);
            if (old != null)
                foreach (var id in old.Units)
                {
                    var u = E.Objects[id];
                    if (u.Category != ObjectCategory.Facility || u.System != dest) continue;
                    energyFree++;
                    if (u.Class == FacilityKinds.ClassName(FacilityKind.Mine)) rawFree++;
                }
            bool isMine = o.Class == FacilityKinds.ClassName(FacilityKind.Mine);
            if (energyFree < o.Count || (isMine && rawFree < o.Count)) return OrderResult.Reject(RejectReason.NoSlot);
        }
        if (dest != o.System)
        {
            int speed = DeliverySpeed(row);
            if (speed <= 0) return OrderResult.Reject(RejectReason.Immobile);
            if (!_fleets.FindPath(o.System, dest, speed).Ok) return OrderResult.Reject(RejectReason.Unreachable);
        }

        // ---- accepted
        if (old != null) CancelProject(ctx, ctx.Day, old, "Replaced");
        var ids = new List<int>();
        for (int n = 0; n < o.Count; n++) // every unit exists now, queued: its maintenance counts at once (DEFAULT, Lead)
        {
            int id;
            if (facility)
            {
                id = _eco.AddFacility(dest, FacilityKinds.TryParse(o.Class, out var kind) ? kind : throw new InvalidOperationException(), completed: false);
                E.Objects[id].Side = o.Side; // a queued facility belongs to the ordering side whatever happens to the system
            }
            else id = _eco.AddObject(o.Side, o.Class, null, completed: false);
            ids.Add(id);
        }
        C.Set(new Project { Side = o.Side, System = o.System, Family = o.Family, Destination = dest, Units = new List<int>(ids), Progress = 0 });
        int day = ctx.Day;
        ctx.Emit((i, _) => new ProjectStarted(i, day, o.Side, o.System, o.Family, o.Class, o.Count, dest, new ValueList<int>(ids)));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: m2-construction §Projects and queues (StopBuild = cancel without a new order)
    private OrderResult HandleStop(StepContext ctx, StopBuildOrder o)
    {
        if (!_galaxy.TryFindSystem(o.System, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if (_fleets.OwnerOf(o.System) != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        var p = C.Find(o.System, o.Family);
        if (p == null) return OrderResult.Reject(RejectReason.NoProject);
        CancelProject(ctx, ctx.Day, p, "Stopped");
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    /// <summary>
    /// Cancel a project: its unfinished units are removed (maintenance and slot reservations leave at once), the yards of the family
    /// on the system lose their running cycle and their place in the line, refined already spent is not returned (DEFAULT), the current
    /// unit's progress is lost. Units already finished stay. rule: m2-construction §Projects and queues.
    /// </summary>
    private void CancelProject(StepContext ctx, int day, Project p, string reason)
    {
        foreach (var y in YardsOf(p.System, p.Family).ToList())
        {
            y.Due = 0; y.InLine = false;
            C.Of(p.Side).RefinedLine.Remove(y.Id);
            C.Empire.RefinedLine.Remove(y.Id); C.Alliance.RefinedLine.Remove(y.Id);
        }
        var removed = p.Units.ToList();
        foreach (var id in removed) _eco.RemoveObject(id);
        C.Remove(p);
        var side = p.Side; string sys = p.System; var fam = p.Family;
        ctx.Emit((i, _) => new ProjectCancelled(i, day, side, sys, fam, reason, new ValueList<int>(removed)));
    }

    /// <summary>
    /// For the capture package, called right after a system changed owner: every project standing on the system whose side no longer
    /// controls it is cancelled now (reason SystemLost, no refund, maintenance freed), the same cancellation slot 6.6 would do, so
    /// the later 6.6 pass finds nothing left. Deliveries heading for the system are NOT touched here: they follow
    /// <see cref="LostDestination"/> when they reach it (steps 3 and 4). rule: m2-control-and-capture §What happens at the moment of
    /// capture (3); m2-construction §Projects and queues.
    /// </summary>
    public void CancelProjectsOnCapture(StepContext ctx, string system)
    {
        if (!C.Projects.TryGetValue(system, out var by)) return;
        foreach (var p in by.Values.ToList())
            if (!Controls(p.System, p.Side)) CancelProject(ctx, ctx.Day + 1, p, "SystemLost");
    }

    /// <summary>True if the object is a unit of a project or is travelling as a delivery: construction owns it and decides its fate.</summary>
    public bool OwnsObject(int objectId) =>
        C.Deliveries.Values.Any(d => d.Object == objectId) || C.AllProjects().Any(p => p.Units.Contains(objectId));

    /// <summary>A yard that was scrapped or destroyed leaves the line; if it was the last of its family on the system the project is cancelled. rule: m2-construction §Scrapping.</summary>
    private void OnObjectRemoved(StepContext ctx, EconObject o, int day)
    {
        if (o.Category != ObjectCategory.Facility || !FacilityKinds.TryParse(o.Class, out var k) || !FacilityKinds.IsYard(k)) return;
        C.Empire.RefinedLine.Remove(o.Id); C.Alliance.RefinedLine.Remove(o.Id);
        var fam = Yards.FamilyOf(k);
        var p = o.System == null ? null : C.Find(o.System, fam);
        if (p != null && !YardsOf(o.System!, fam).Any()) CancelProject(ctx, day, p, "YardLost");
    }

    // ================================================================== slot 6.6: yards

    // rule: m2-construction §Where it runs (6.6.a completions by yard id, 6.6.b idle sweep by project and (rate, id))
    private void YardsHook(StepContext ctx)
    {
        int t = ctx.Day + 1;

        // A project whose system changed hands, or that lost its last yard some other way (capture), ends. rule: m2-construction §Projects and queues
        foreach (var p in C.AllProjects())
        {
            if (!Controls(p.System, p.Side)) CancelProject(ctx, t, p, "SystemLost");
            else if (!YardsOf(p.System, p.Family).Any()) CancelProject(ctx, t, p, "YardLost");
        }

        // 6.6.a: cycles due now add their point; a unit that reaches its cost completes.
        foreach (var y in YardObjects().Where(o => o.Due == t).ToList())
        {
            y.Due = 0;
            var p = C.Find(y.System!, Yards.FamilyOf(KindOf(y)));
            if (p == null || p.Units.Count == 0) continue;
            p.Progress += Params.PointsPerCycle;
            if (p.Progress >= CostOf(p.Units[0])) CompleteUnit(ctx, t, p);
        }

        // Refined that arrived without an event (a setup change, a refund served elsewhere) reaches the head of each line now (Alliance, then Empire).
        ServeRefinedLine(ctx, t, Side.Alliance);
        ServeRefinedLine(ctx, t, Side.Empire);

        // 6.6.b: idle yards start cycles, project by project (system name ordinal, then family 0x28, 0x29, 0x2a).
        foreach (var p in C.AllProjects())
        {
            // DEFAULT: a unit of cost 0 needs no cycle (nothing is buildable at cost 0 by order, so this only guards setups).
            while (p.Units.Count > 0 && p.Progress >= CostOf(p.Units[0])) CompleteUnit(ctx, t, p);
            if (p.Units.Count == 0) continue;
            var yards = YardsOf(p.System, p.Family).OrderBy(RateOf).ThenBy(y => y.Id).ToList();
            foreach (var y in yards)
            {
                if (y.Due != 0 || y.InLine || IsSuspended(y)) continue;
                int running = yards.Count(x => x.Due != 0), waiting = yards.Count(x => x.InLine);
                // rule: m2-construction §Build time: a yard only starts if progress + running + waiting < C, so no point is wasted
                if (p.Progress + (running + waiting) * Params.PointsPerCycle >= CostOf(p.Units[0])) break;
                AskForRefined(ctx, t, y, p.Side);
            }
        }
    }

    // rule: m2-construction §Build time (the yard takes refined to start; waits in the line if there is none)
    private void AskForRefined(StepContext ctx, int t, EconObject yard, Side side)
    {
        C.Of(side).RefinedLine.Add(yard.Id);
        yard.InLine = true;
        ServeRefinedLine(ctx, t, side);
        if (yard.InLine)
        {
            int id = yard.Id; string sys = yard.System!;
            ctx.Emit((i, _) => new YardWaiting(i, t, id, sys, side));
        }
    }

    // rule: m2-mines-refineries-pools §Asking for raw / serving a line (the refined line works the same: first in first out,
    // a suspended yard keeps its place and is skipped). Called whenever the side's refined grew and when a yard asks.
    private void ServeRefinedLine(StepContext ctx, int t, Side side)
    {
        var line = C.Of(side).RefinedLine;
        var pool = E.Pool(side);
        for (int i = 0; i < line.Count && pool.Refined >= Params.RefinedPerCycle;)
        {
            E.Objects.TryGetValue(line[i], out var y);
            // An entry that is gone, no longer a working yard of this side, or whose project ended leaves the line.
            var proj = y == null || !y.Completed ? null : C.Find(y.System!, Yards.FamilyOf(KindOf(y)));
            if (y == null || proj == null || proj.Units.Count == 0 || _eco.OwnerOf(y) != side)
            {
                if (y != null) y.InLine = false;
                line.RemoveAt(i);
                continue;
            }
            if (IsSuspended(y)) { i++; continue; }
            line.RemoveAt(i);
            y.InLine = false;
            pool.Refined -= Params.RefinedPerCycle;
            y.Due = t + RateOf(y);
        }
    }

    /// <summary>The current unit of a project reached its cost: it appears at the yard's system or leaves as a delivery. rule: m2-construction §Completion and delivery.</summary>
    private void CompleteUnit(StepContext ctx, int t, Project p)
    {
        int id = p.Units[0];
        p.Units.RemoveAt(0);
        p.Progress = 0;
        if (p.Units.Count == 0) C.Remove(p);
        var obj = E.Objects[id];
        string dest = p.Destination;
        int deliveryId = 0;
        if (dest != p.System) deliveryId = Depart(ctx, t, p, obj);
        else Appear(ctx, obj, p.System, UnitAppearance.Completed);
        string cls = obj.Class, yard = p.System; var side = p.Side;
        ctx.Emit((i, _) => new UnitCompleted(i, t, id, cls, side, yard, dest, deliveryId));
        // The delivery events follow the completion event.
        if (deliveryId != 0) AnnounceDeparture(ctx, t, C.Deliveries[deliveryId]);
        if (dest == p.System) UnitAppeared?.Invoke(ctx, obj, UnitAppearance.Completed);
    }

    /// <summary>The unit stands where it is meant to: a facility installs (no side of its own), others get a location. rule: m2-construction §Completion and delivery.</summary>
    private void Appear(StepContext ctx, EconObject obj, string system, UnitAppearance how)
    {
        obj.Completed = true;
        if (obj.Category == ObjectCategory.Facility) obj.Side = null;
        else obj.System = system;
    }

    // ================================================================== deliveries

    private int Depart(StepContext ctx, int t, Project p, EconObject obj)
    {
        var row = _eco.Costs.Get(obj.Class);
        var path = _fleets.FindPath(p.System, p.Destination, DeliverySpeed(row));
        if (!path.Ok) throw new InvalidOperationException($"No path {p.System} to {p.Destination} for a delivery that was accepted."); // the lane graph does not change
        var d = new Delivery
        {
            Id = C.NextDeliveryId++, Side = p.Side, Object = obj.Id, From = p.System, To = p.Destination,
            Path = path.Names(_galaxy).ToList(), HopTicks = path.HopTicks.ToList(),
            Index = 0, InFlight = true, DueDay = t + path.HopTicks[0], // leaves at hook time T; first hop due T + ticks
        };
        // Lead note: if the very first hop ends at a destination the side no longer holds, the delivery waits at the yard.
        if (LostDestination == LostDestinationRule.Pause && d.Path.Count == 2 && !Controls(d.To, d.Side))
        { d.InFlight = false; d.Held = true; }
        C.Deliveries[d.Id] = d;
        return d.Id;
    }

    private void AnnounceDeparture(StepContext ctx, int t, Delivery d)
    {
        string cls = E.Objects[d.Object].Class;
        int id = d.Id, obj = d.Object, first = d.HopTicks[0], due = t + d.HopTicks[0];
        string from = d.From, to = d.To; var path = new ValueList<string>(d.Path.ToList());
        ctx.Emit((i, _) => new DeliveryDeparted(i, t, id, obj, cls, from, to, path, first, due));
        if (d.Held) EmitHeld(ctx, t, d, "DestinationLost", new List<int>());
    }

    private void EmitHeld(StepContext ctx, int day, Delivery d, string reason, List<int> blockers)
    {
        int id = d.Id, obj = d.Object; string sys = d.Path[d.Index];
        var b = new ValueList<int>(blockers);
        ctx.Emit((i, _) => new DeliveryHeld(i, day, id, obj, sys, reason, b));
    }

    // rule: m2-construction §Completion and delivery; m1-clock-and-orders step 3 (after all fleets, sorted by delivery id).
    // Also takes the snapshot of fleets placed in step 3, which step 4 uses for the forced-stop test.
    private void DeliveryArrivals(StepContext ctx)
    {
        int day = ctx.Day + 1;
        _present = new Dictionary<string, List<Fleet>>(StringComparer.Ordinal);
        foreach (var f in State.Fleets.Values) // fleet id order
        {
            if (f.At == null) continue;
            if (!_present.TryGetValue(f.At, out var l)) _present[f.At] = l = new List<Fleet>();
            l.Add(f);
        }
        foreach (var d in C.Deliveries.Values.ToList()) // delivery id order
        {
            if (!d.InFlight || d.DueDay != day) continue;
            d.Index++;
            d.InFlight = false;
            if (d.Index == d.Path.Count - 1) TryLand(ctx, day, d); // trip over; otherwise it sits "passing" until step 4
        }
    }

    // rule: m2-construction §Completion and delivery (forced stop D2, lost destination); m1-clock-and-orders step 4
    // (after all fleets: system name ordinal, then delivery id). Every sitting delivery is judged: passing ones and held ones alike.
    private void DeliveryStops(StepContext ctx)
    {
        int day = ctx.Day + 1;
        var sitting = C.Deliveries.Values.Where(d => !d.InFlight).OrderBy(d => d.Path[d.Index], StringComparer.Ordinal).ThenBy(d => d.Id).ToList();
        foreach (var d in sitting)
        {
            if (!E.Objects.ContainsKey(d.Object)) { C.Deliveries.Remove(d.Id); continue; }
            string sys = d.Path[d.Index];
            if (d.Index == d.Path.Count - 1) { TryLand(ctx, day, d); continue; } // held at the destination (lost), maybe regained

            // D2: an armed enemy fleet at an intermediate system holds the delivery; it goes on when none is there.
            if (DeliveryForcedStop && d.Index > 0)
            {
                var blockers = BlockersAt(d, sys);
                if (blockers.Count > 0)
                {
                    if (!d.Held) { d.Held = true; EmitHeld(ctx, day, d, "FleetPresent", blockers.Select(b => b.Id).ToList()); }
                    continue;
                }
            }
            // Lead note: the last system before the destination holds a delivery whose destination is not the side's.
            if (LostDestination == LostDestinationRule.Pause && d.Index + 1 == d.Path.Count - 1 && !Controls(d.To, d.Side))
            {
                if (!d.Held) { d.Held = true; EmitHeld(ctx, day, d, "DestinationLost", new List<int>()); }
                continue;
            }
            bool wasHeld = d.Held;
            d.Held = false;
            d.InFlight = true;
            d.DueDay = day + d.HopTicks[d.Index]; // next hop starts now: due on day K + 1 + hop ticks
            int id = d.Id, obj = d.Object;
            if (wasHeld) ctx.Emit((i, _) => new DeliveryResumed(i, day, id, obj, sys));
            else ctx.Emit((i, _) => new DeliveryPassed(i, day, id, obj, sys));
        }
    }

    /// <summary>The armed enemy fleets that hold this delivery, by the fleets' own test (armed enemy, ForcedStopTrigger). The delivery counts as a fleet with only its own ship in it. rule: m2-construction (same armed test and ForcedStopTrigger as fleets).</summary>
    private List<Fleet> BlockersAt(Delivery d, string system)
    {
        if (!_present.TryGetValue(system, out var here)) return new List<Fleet>();
        var enemies = here.Where(f => f.Side != d.Side).ToList();
        if (enemies.Count == 0) return enemies;
        var obj = E.Objects[d.Object];
        var self = new List<ShipClass>();
        if (_fleets.ShipClasses.TryGetValue(obj.Class, out var sc)) self.Add(sc);
        return _fleets.Blockers(self, enemies);
    }

    /// <summary>The delivery stands at its destination: land it, hold it (destination lost, Pause), or refuse a facility (Refuse, or no slot). rule: m2-construction §Completion and delivery, §Lead review note.</summary>
    private void TryLand(StepContext ctx, int day, Delivery d)
    {
        if (!E.Objects.TryGetValue(d.Object, out var obj)) { C.Deliveries.Remove(d.Id); return; }
        string sys = d.To;
        bool controlled = Controls(sys, d.Side);
        if (LostDestination == LostDestinationRule.Pause && !controlled)
        {
            if (!d.Held) { d.Held = true; EmitHeld(ctx, day, d, "DestinationLost", new List<int>()); }
            return;
        }
        if (obj.Category == ObjectCategory.Facility && (!controlled || !SlotIntact(obj)))
        {
            // DEFAULT: scrapped on arrival with the usual half refund, maintenance freed.
            int refund = _eco.Costs.Get(obj.Class).Cost / _eco.Params.RefundDivisor;
            string cls = obj.Class; int id = d.Id, oid = obj.Id; var side = d.Side;
            C.Deliveries.Remove(d.Id);
            _eco.Scrap(ctx, day, obj, side, "DeliveryRefused"); // raises the refined line service for the refund
            ctx.Emit((i, _) => new DeliveryRefused(i, day, id, oid, cls, sys, side, refund));
            return;
        }
        Appear(ctx, obj, sys, UnitAppearance.Delivered);
        C.Deliveries.Remove(d.Id);
        {
            int id = d.Id, oid = obj.Id; string cls = obj.Class;
            ctx.Emit((i, _) => new DeliveryArrived(i, day, id, oid, cls, sys));
        }
        UnitAppeared?.Invoke(ctx, obj, UnitAppearance.Delivered);
    }

    /// <summary>Is the reserved slot still there: energy (and raw for a mine) at least what the system's facilities use. rule: m2-construction §Completion and delivery ("its reserved slot is intact").</summary>
    private bool SlotIntact(EconObject facility)
    {
        string sys = facility.System!;
        if (_eco.EnergyUsed(sys) > _eco.SlotsOf(sys).Energy) return false;
        return facility.Class != FacilityKinds.ClassName(FacilityKind.Mine) || _eco.RawUsed(sys) <= _eco.EffectiveRaw(sys);
    }
}
