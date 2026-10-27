using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Map;
using Praedra.Core.Ships;

namespace Praedra.Core.Construction;

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
    /// The research gate. Off = AllAvailable, every item of the side counts as researched, NotResearched never fires (the M3 stub); on = an item
    /// needs its research order at most the side's level in its track (<see cref="ResearchOrderReached"/>), or a setup grant. DEFAULT on once
    /// research is built (m5-research §Defaults, Paul decides the flip). rule: m5-research §Availability test.
    /// </summary>
    public bool RespectResearchOrder { get; set; } = true;
    /// <summary>
    /// Step 2 of the availability test: is the class's research order at most the side's level in its track? GameHost wires it to
    /// <see cref="Research.ResearchModule.OrderReached"/>. Unwired (a construction module without research) = order 0 only, which is level 0.
    /// rule: m5-research §Availability test.
    /// </summary>
    public Func<Side, string, bool>? ResearchOrderReached { get; set; }
    /// <summary>The harvester class row can be ordered at a Shipyard. DEFAULT true; off: the order is rejected NotAvailable. rule: m3-ships-and-fighters §harvester as an ordinary class row (S14).</summary>
    public bool HarvesterClassBuildable { get; set; } = true;
    /// <summary>Yards stop under the Classic blockade flag (<see cref="FleetModule.IsBlockadedClassic"/>) instead of the economic blockade. DEFAULT off. rule: m2-construction §Suspension.</summary>
    public bool UseClassicBlockadeFlag { get; set; }
    /// <summary>
    /// A suspended yard's running cycle: Pause (DEFAULT, Paul economy/02: "shipyards are paused") keeps it frozen and resumes it where it left off;
    /// Abandon (Classic, B5b §3) loses it and the refined it took. Either way a waiting yard leaves the refined line. rule: m2-construction §Suspension
    /// (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch).
    /// </summary>
    public StopMode YardStopMode { get; set; } = StopMode.Pause;
    /// <summary>
    /// An order whose upkeep (maintenance x count) does not fit in the side's free capacity is refused whole (NoUpkeep). Classic = refused (B5b §7,
    /// [EXE-own] 0x52e970); DEFAULT refused in both rulesets (Dev lead 2026-10-18). False = accepted (the rule before B5b: the autoscrap sorts it out).
    /// rule: m2-maintenance §Overdraft; m2-construction §Build order.
    /// </summary>
    public bool RefuseOverdrawOrders { get; set; } = true;
    /// <summary>
    /// A new order for a (system, family) that has a project cancels it first (the rule before B5b, manual p.84). DEFAULT false: the Classic build
    /// command never cancels (B5b §7, inferred: the new units queue behind). rule: m2-construction §Projects and queues.
    /// </summary>
    public bool NewOrderReplacesProject { get; set; }
    /// <summary>
    /// Is the system in uprising (its yards start no new cycle)? The flag belongs to the uprising sheet (m2-uprisings-and-garrison); this package
    /// sets nothing, so the default answers no. rule: m2-construction §Suspension.
    /// </summary>
    public Func<string, bool> IsInUprising { get; set; } = _ => false;
    /// <summary>
    /// An extra build check run after the research gate and the count check (null = none): a rejection reason or null. The harvester module sets it
    /// for the one-per-system cap (HarvesterLimit). rule: m5-harvester §1.
    /// </summary>
    public Func<BuildOrder, string?>? ExtraBuildCheck { get; set; }
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
        eco.RefinedPoolGrew += (ctx, side) => m.ServeRefinedLine(ctx.Day + 1, side);
        eco.ObjectRemoved += m.OnObjectRemoved;
        return m;
    }

    private GameState State => _game.State;
    private ConstructionState C => _game.State.Construction;
    private EconomyState E => _game.State.Economy;

    // ================================================================== setup and queries (between Steps)

    /// <summary>Setup or tests only: grant a class to a side whatever its level (step 3 of the availability test). rule: m5-research §Availability test; §Defaults (Granted classes stay available).</summary>
    public void GrantResearch(Side side, string className)
    {
        _eco.Costs.Get(className); // throws on an unknown class
        var list = C.Of(side).Granted;
        if (list.Contains(className)) return;
        list.Add(className);
        list.Sort(StringComparer.Ordinal);
    }

    /// <summary>
    /// The availability test: (1) gate off = true; (2) research order at most the side's level in its track; (3) granted by setup or a test.
    /// Read when the order is processed (step 1). Also the build menu's query (the UI dims what this refuses). rule: m5-research §Availability test.
    /// </summary>
    public bool IsResearched(Side side, string className)
    {
        var row = _eco.Costs.Get(className);
        if (!RespectResearchOrder) return true;
        bool reached = ResearchOrderReached != null ? ResearchOrderReached(side, className) : row.ResearchOrder == 0;
        return reached || C.Of(side).Granted.Contains(className);
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
    private bool IsSuspended(EconObject yard) => yard.System == null || IsSuspendedAt(yard.System);

    /// <summary>The yards of a system are suspended: no owner, an uprising, or a blockade. Also read by repair (FastRepair). rule: m2-construction §Suspension; m4-retreat-damage-repair §5.</summary>
    public bool IsSuspendedAt(string system)
    {
        if (_fleets.OwnerOf(system) is not Side owner) return true;
        if (IsInUprising(system)) return true;
        return UseClassicBlockadeFlag ? _fleets.IsBlockadedClassic(system) : _fleets.IsEconomicBlockade(system, owner);
    }

    // ================================================================== step 1: BuildOrder, StopBuild

    // rule: m2-construction §Build order (rejections in the sheet's order; a rejection changes nothing)
    private OrderResult HandleBuild(StepContext ctx, BuildOrder o)
    {
        if (!_galaxy.TryFindSystem(o.System, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if (_eco.IsDestroyed(o.System) || (o.Destination != null && _eco.IsDestroyed(o.Destination))) return OrderResult.Reject(RejectReason.SystemDestroyed); // rule: m5-harvester §2 effect 5 (no builds)
        if (!_eco.Costs.TryGet(o.Class, out var row)) return OrderResult.Reject(RejectReason.UnknownItem);
        if (_fleets.OwnerOf(o.System) != o.Side) return OrderResult.Reject(RejectReason.NotYours); // also a neutral system (C24)
        if (!YardsOf(o.System, o.Family).Any()) return OrderResult.Reject(RejectReason.NoYard);
        if (Yards.BuiltBy(row.Category) != o.Family) return OrderResult.Reject(RejectReason.WrongFamily);
        // rule: m2-construction §Build order (NotAvailable: the file's side flag; cost 0 is not buildable, m3-troops-and-special-forces §Training Facility production)
        if (!(o.Side == Side.Hand ? row.Hand : row.Collective) || row.Cost <= 0) return OrderResult.Reject(RejectReason.NotAvailable);
        if (o.Class == EconomyModule.HarvesterClass && !HarvesterClassBuildable) return OrderResult.Reject(RejectReason.NotAvailable); // rule: m3-ships-and-fighters §harvester (S14)
        if (!IsResearched(o.Side, o.Class)) return OrderResult.Reject(RejectReason.NotResearched);
        if (o.Count < 1) return OrderResult.Reject(RejectReason.BadCount);
        if (ExtraBuildCheck?.Invoke(o) is { } extra) return OrderResult.Reject(extra);

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
            if (old != null && NewOrderReplacesProject)
                foreach (var id in old.Units)
                {
                    var u = E.Objects[id];
                    if (u.Category != ObjectCategory.Facility || u.System != dest) continue;
                    energyFree++;
                    if (u.Class == FacilityKinds.ClassName(FacilityKind.Mine)) rawFree++;
                }
            // rule: m5-fog-and-hq §N4 (Decided, Paul 2026-10-16): a Sensor Station only on a system on a hyperspace route.
            if (FacilityKinds.TryParse(o.Class, out var routeKind) && FacilityKinds.RouteOnly(routeKind)
                && !(_galaxy.TryFindSystem(dest, out int destAt) && _galaxy.OnRoute(destAt)))
                return OrderResult.Reject(RejectReason.NotOnRoute);
            // rule: m5-fog-and-hq §N4 (economy/15 §2): one Sensor Station per planet; the replaced project's own reservation counts as free.
            if (FacilityKinds.TryParse(o.Class, out var placeKind) && FacilityKinds.OnePerPlanet(placeKind))
            {
                int already = _eco.OfKindAt(dest, placeKind);
                if (old != null && NewOrderReplacesProject) foreach (var id in old.Units) if (E.Objects[id].System == dest && E.Objects[id].Class == o.Class) already--;
                if (o.Count > 1 || already > 0) return OrderResult.Reject(RejectReason.OnePerPlanet);
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

        // rule: m2-maintenance §Overdraft (B5b §7, 0x52e970): maintenance x count above capacity - dealt load (at most the capacity) is refused, whole
        if (RefuseOverdrawOrders)
        {
            int cap = _eco.Capacity(o.Side), dealt = Math.Min(_eco.Load(o.Side), cap);
            if ((long)row.Maintenance * o.Count > cap - dealt) return OrderResult.Reject(RejectReason.NoUpkeep);
        }

        // ---- accepted
        if (old != null && NewOrderReplacesProject) { CancelProject(ctx, ctx.Day, old, "Replaced"); old = null; }
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
        // rule: m2-construction §Projects and queues (B5b §7): the new units join the queue behind the current order
        if (old != null)
        {
            if (old.Next == null && old.Destination == dest) old.Units.AddRange(ids);
            else (old.Next ??= new List<QueuedOrder>()).Add(new QueuedOrder { Destination = dest, Units = new List<int>(ids) });
        }
        else C.Set(new Project { Side = o.Side, System = o.System, Family = o.Family, Destination = dest, Units = new List<int>(ids), Progress = 0 });
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
            C.Hand.RefinedLine.Remove(y.Id); C.Collective.RefinedLine.Remove(y.Id);
        }
        var removed = p.AllUnits().ToList(); // unfinished units only: finished ones have left the project (no refund is due, B5b §8)
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

    /// <summary>
    /// For the harvester: a system was destroyed. Every project standing on it or heading for it is cancelled (reason SystemDestroyed, no refund,
    /// maintenance freed), and every facility still travelling to it is removed with its delivery (no refund). Other units heading for it follow
    /// <see cref="LostDestination"/> on arrival. Call before the planet's own objects are removed. rule: m5-harvester §2 effect 2 ("completed or in a queue", no refund).
    /// </summary>
    public void CancelProjectsOnDestroy(StepContext ctx, string system)
    {
        foreach (var p in C.AllProjects().Where(p => p.System == system || p.Destination == system || (p.Next?.Any(q => q.Destination == system) ?? false)).ToList())
            CancelProject(ctx, ctx.Day + 1, p, "SystemDestroyed");
        foreach (var d in C.Deliveries.Values.Where(d => d.To == system).ToList()) // delivery id order
            if (E.Objects.TryGetValue(d.Object, out var obj) && obj.Category == ObjectCategory.Facility)
            {
                C.Deliveries.Remove(d.Id);
                _eco.RemoveObject(obj.Id);
            }
    }

    /// <summary>The first unfinished unit of class <paramref name="cls"/> in <paramref name="owner"/>'s projects on the system (family id order, then build order), or null. rule: m5-harvester §5 (Spike in the yard).</summary>
    public int? QueuedUnitOf(Side owner, string system, string cls)
    {
        if (!C.Projects.TryGetValue(system, out var by)) return null;
        foreach (var p in by.Values)
            if (p.Side == owner)
                foreach (int id in p.AllUnits())
                    if (E.Objects.TryGetValue(id, out var o) && o.Class == cls) return id;
        return null;
    }

    /// <summary>
    /// Sabotage removes one unfinished unit from its project: no refund, its maintenance (and any slot reservation) leaves at once; if it was the
    /// current unit, its progress is lost and the yards go on with the next unit. An order left empty is dropped; the last unit of a project
    /// cancels the project as a stop does (<see cref="ProjectCancelled"/> with the reason). Otherwise <see cref="UnitRemoved"/>.
    /// rule: m5-harvester §5 (Spike in the yard); m2-construction §Projects and queues.
    /// </summary>
    public void RemoveQueuedUnit(StepContext ctx, int id, string reason)
    {
        var p = C.AllProjects().FirstOrDefault(x => x.AllUnits().Contains(id));
        if (p == null) return;
        int day = ctx.Day + 1;
        if (p.AllUnits().Count() == 1) { CancelProject(ctx, day, p, reason); return; }
        int at = p.Units.IndexOf(id);
        if (at >= 0)
        {
            p.Units.RemoveAt(at);
            if (at == 0) p.Progress = 0;
            if (p.Units.Count == 0) { var next = p.Next!; p.Units = next[0].Units; p.Destination = next[0].Destination; next.RemoveAt(0); if (next.Count == 0) p.Next = null; }
        }
        else
        {
            var later = p.Next!;
            var q = later.First(o => o.Units.Contains(id));
            q.Units.Remove(id);
            if (q.Units.Count == 0) { later.Remove(q); if (later.Count == 0) p.Next = null; }
        }
        _eco.RemoveObject(id);
        var side = p.Side; string sys = p.System; var fam = p.Family;
        ctx.Emit((i, _) => new UnitRemoved(i, day, side, sys, fam, id, reason));
    }

    /// <summary>True if the object is a unit of a project or is travelling as a delivery: construction owns it and decides its fate.</summary>
    public bool OwnsObject(int objectId) =>
        C.Deliveries.Values.Any(d => d.Object == objectId) || C.AllProjects().Any(p => p.AllUnits().Contains(objectId));

    /// <summary>A yard that was scrapped or destroyed leaves the line; if it was the last of its family on the system the project is cancelled. rule: m2-construction §Scrapping.</summary>
    private void OnObjectRemoved(StepContext ctx, EconObject o, int day)
    {
        if (o.Category != ObjectCategory.Facility || !FacilityKinds.TryParse(o.Class, out var k) || !FacilityKinds.IsYard(k)) return;
        C.Hand.RefinedLine.Remove(o.Id); C.Collective.RefinedLine.Remove(o.Id);
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

        // A blockade or an uprising stops a yard at once: its running cycle is abandoned (the refined spent is lost) and it leaves the refined line;
        // it asks again when it is no longer suspended. rule: m2-construction §Suspension (B5b §3: yards are in the same families, 0x50bbb0)
        // YardStopMode Pause (DEFAULT): the running cycle is frozen instead, its due day moving on one day per suspended day, so it resumes where
        // it left off. rule: m2-construction §Suspension (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch)
        foreach (var y in YardObjects().Where(o => (o.Due != 0 || o.InLine) && IsSuspended(o)).ToList())
        {
            if (y.Due != 0) y.Due = YardStopMode == StopMode.Pause ? y.Due + 1 : 0;
            if (y.InLine) { C.Hand.RefinedLine.Remove(y.Id); C.Collective.RefinedLine.Remove(y.Id); y.InLine = false; }
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

        // Refined that arrived without an event (a setup change, a refund served elsewhere) reaches the head of each line now (Collective, then Hand).
        ServeRefinedLine(t, Side.Collective);
        ServeRefinedLine(t, Side.Hand);

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
        ServeRefinedLine(t, side);
        if (yard.InLine)
        {
            int id = yard.Id; string sys = yard.System!;
            ctx.Emit((i, _) => new YardWaiting(i, t, id, sys, side));
        }
    }

    // rule: m2-mines-refineries-pools §Asking for raw / serving a line (the refined line works the same: first in first out,
    // a suspended yard keeps its place and is skipped). Called whenever the side's refined grew and when a yard asks.
    private void ServeRefinedLine(int t, Side side)
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
        string dest = p.Destination;
        if (p.Units.Count == 0)
        {
            // the next queued order becomes the current one (B5b §7); none left: the project ends
            if (p.Next is { Count: > 0 } next) { p.Units = next[0].Units; p.Destination = next[0].Destination; next.RemoveAt(0); if (next.Count == 0) p.Next = null; }
            else C.Remove(p);
        }
        var obj = E.Objects[id];
        int deliveryId = 0;
        if (dest != p.System) deliveryId = Depart(t, p, obj, dest); // the finished unit's own destination (the next queued order may have another)
        else Appear(obj, p.System);
        string cls = obj.Class, yard = p.System; var side = p.Side;
        ctx.Emit((i, _) => new UnitCompleted(i, t, id, cls, side, yard, dest, deliveryId));
        // The delivery events follow the completion event.
        if (deliveryId != 0) AnnounceDeparture(ctx, t, C.Deliveries[deliveryId]);
        if (dest == p.System) UnitAppeared?.Invoke(ctx, obj, UnitAppearance.Completed);
    }

    /// <summary>The unit stands where it is meant to: a facility installs (no side of its own), others get a location. rule: m2-construction §Completion and delivery.</summary>
    private void Appear(EconObject obj, string system)
    {
        obj.Completed = true;
        if (obj.Category == ObjectCategory.Facility) obj.Side = null;
        else obj.System = system;
    }

    // ================================================================== deliveries

    private int Depart(int t, Project p, EconObject obj, string dest)
    {
        var row = _eco.Costs.Get(obj.Class);
        var path = _fleets.FindPath(p.System, dest, DeliverySpeed(row));
        if (!path.Ok) throw new InvalidOperationException($"No path {p.System} to {dest} for a delivery that was accepted."); // the lane graph does not change
        var d = new Delivery
        {
            Id = C.NextDeliveryId++, Side = p.Side, Object = obj.Id, From = p.System, To = dest,
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
        Appear(obj, sys);
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
