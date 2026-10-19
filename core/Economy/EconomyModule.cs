using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using Praedra.Core.Map;

namespace Praedra.Core.Economy;

/// <summary>
/// The economy base: per-system slots and facilities, the side-wide raw and refined pools, the mine and refinery cycle
/// (slot 6.5), resource and disaster incidents (6.4), maintenance and autoscrap (6.7), the Classic way (B5b). Registers itself on a
/// <see cref="Game"/> like <see cref="FleetModule"/>; holds data and settings only, never game state (that lives in
/// <see cref="GameState.Economy"/>). After <see cref="GameSave.Load"/>, attach again to the loaded game.
/// rule: m2-slots-and-resources, m2-mines-refineries-pools, m2-maintenance.
/// </summary>
public sealed class EconomyModule
{
    /// <summary>Ship class of the Death Star (name in the costs file; used for the smuggling exception).</summary>
    public const string DeathStarClass = "Death Star";

    private readonly Game _game;
    private readonly Galaxy _galaxy;
    private readonly SlotValues _slots;
    private readonly FleetModule _fleets;
    // Blockade answers for the systems asked about during one hook (fleets do not move inside a hook). Lookup only.
    private readonly Dictionary<string, bool> _blockCache = new(StringComparer.Ordinal);

    public CostData Costs { get; }
    public EconomyParams Params { get; }

    /// <summary>Blockade stops mines and refineries. Decided yes (Paul 2026-10-12), default on. rule: m2-mines-refineries-pools §Blockade and uprising.</summary>
    public bool BlockadeStopsMining { get; set; } = true;
    /// <summary>An uprising stops mines and refineries exactly like a blockade. Classic = on (B5b §3, [EXE-own] 0x50bbb0); DEFAULT on (Dev lead 2026-10-18). rule: m2-mines-refineries-pools §Blockade and uprising.</summary>
    public bool UprisingStopsMining { get; set; } = true;
    /// <summary>
    /// Suspended mines and refineries leave the capacity sums. Classic = on (B5b §3, [EXE-own] 0x52fff0); DEFAULT off: a blockaded or rebelling mine
    /// or refinery keeps counting, so a blockade cannot set off the autoscrap. rule: m2-maintenance §Capacity (Blockade pause, 2026-10-18; Paul's
    /// economy/02 blockade rule is the DEFAULT, Classic is the switch).
    /// </summary>
    public bool SuspendedLeaveCapacity { get; set; }
    /// <summary>
    /// A suspended mine's or refinery's running cycle: Pause (DEFAULT) keeps it and resumes it for the same side, Abandon (Classic, B5b §3) loses it.
    /// rule: m2-mines-refineries-pools §Blockade and uprising (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch).
    /// </summary>
    public StopMode MineStopMode { get; set; } = StopMode.Pause;
    /// <summary>Armed (DEFAULT, Classic) or FreeRunning autoscrap timer. rule: m2-maintenance §Overdraft and autoscrap (B5b §6).</summary>
    public AutoscrapTimer AutoscrapTimer { get; set; } = AutoscrapTimer.Armed;
    /// <summary>
    /// Is the system in uprising? The flag belongs to the uprising sheet; GameHost wires it to <c>SupportModule.IsUprising</c>. Unwired = no.
    /// rule: m2-mines-refineries-pools §Blockade and uprising (B5b §3).
    /// </summary>
    public Func<string, bool> IsInUprising { get; set; } = _ => false;
    /// <summary>Both incident timers run. DEFAULT on. Off: neither is armed, no draws. rule: m2-slots-and-resources §Do incidents run on pinned values.</summary>
    public bool IncidentsEnabled { get; set; } = true;
    public AutoscrapMode AutoscrapMode { get; set; } = AutoscrapMode.Classic;
    public SlotSource SlotSource { get; set; } = SlotSource.Pinned;
    /// <summary>
    /// Is the system in a Death Star sector (Empire regiments count double for smuggling)? The flag belongs to the support
    /// sheet (m2-popular-support §Death Star sector); M2 sets nothing, so the default answers no.
    /// </summary>
    public Func<string, bool> IsDeathStarSector { get; set; } = _ => false;
    /// <summary>
    /// Raised after a side's refined pool grew (a refinery cycle ended or a scrap refund). The construction package serves the
    /// refined line (yards waiting) from here. rule: m2-mines-refineries-pools §Where it runs, step 2.
    /// </summary>
    public event Action<StepContext, Side>? RefinedPoolGrew;
    /// <summary>
    /// Raised after an object was scrapped or destroyed (not for setup removals); the int is the event day. The construction package
    /// cancels the project of a lost yard from here. rule: m2-construction §Scrapping (scrapping the last yard cancels the project).
    /// </summary>
    public event Action<StepContext, EconObject, int>? ObjectRemoved;
    /// <summary>
    /// Raised by <see cref="DestroyUnit"/> (a ship, squadron or regiment destroyed in a battle or by the Death Star), before <see cref="ObjectRemoved"/>;
    /// not for scrapping. The Death Star module listens to report the loss of a Death Star. rule: m5-death-star §4, §5.
    /// </summary>
    public event Action<StepContext, EconObject, int>? UnitLost;

    private EconomyModule(Game game, Galaxy galaxy, SlotValues slots, CostData costs, FleetModule fleets, EconomyParams p)
    {
        _game = game; _galaxy = galaxy; _slots = slots; Costs = costs; _fleets = fleets; Params = p;
    }

    /// <summary>
    /// Register the ScrapObject handler and the hooks 6.4, 6.5 and 6.7 on <paramref name="game"/>. Attach after
    /// <see cref="FleetModule.Attach"/> (the economy reads fleets, owners and the blockade test from it).
    /// </summary>
    public static EconomyModule Attach(Game game, Galaxy galaxy, SlotValues slots, CostData costs, FleetModule fleets, EconomyParams? p = null)
    {
        foreach (var k in FacilityKinds.All)
            if (!costs.TryGet(FacilityKinds.ClassName(k), out var row) || row.Category != ObjectCategory.Facility)
                throw new ArgumentException($"The costs file has no facility row for '{FacilityKinds.ClassName(k)}'.");
        var m = new EconomyModule(game, galaxy, slots, costs, fleets, p ?? new EconomyParams());
        game.RegisterOrderHandler<ScrapObjectOrder>(m.HandleScrap);
        game.RegisterHook("6.4", m.ResourceAndDisasterTimers);
        game.RegisterHook("6.5", m.MinesAndRefineries);
        game.RegisterHook("6.7", m.Autoscrap);
        return m;
    }

    private EconomyState E => _game.State.Economy;

    // ================================================================== setup (between Steps; not orders, no events)

    /// <summary>
    /// Setup only: place a facility. A completed facility is installed; an incomplete one is queued or in delivery (counts for
    /// load and reserves its slot). Does not check slots (tests need over-full systems); use <see cref="CheckPlace"/> for that.
    /// </summary>
    public int AddFacility(string system, FacilityKind kind, bool completed = true, int? id = null)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        return Add(new EconObject { Category = ObjectCategory.Facility, Class = FacilityKinds.ClassName(kind), System = system, Completed = completed }, id);
    }

    /// <summary>Setup only: add a ship, fighter squadron, regiment or special force of a side (a regiment or special force may stand on a system).</summary>
    public int AddObject(Side side, string className, string? system = null, bool completed = true, int? id = null)
    {
        var row = Costs.Get(className);
        if (row.Category == ObjectCategory.Facility) throw new ArgumentException("Use AddFacility for facilities.");
        if (system != null && !_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        return Add(new EconObject { Category = row.Category, Class = className, Side = side, System = system, Completed = completed }, id);
    }

    private int Add(EconObject o, int? id)
    {
        // rule: m3-ships-and-fighters §The ship object (one registry, one counter; a ship starts at class hull with six zero counters)
        _fleets.ShipClasses.TryGetValue(o.Class, out var shipClass);
        return ObjectRegistry.Add(E, o, id, shipClass);
    }

    /// <summary>
    /// Remove an object with no refund and no event: cancelling queued work (m2-maintenance M8) or test setup. Its maintenance
    /// and slot leave at once. rule: m2-maintenance §Scrapping and refunds (a cancelled queued unit gives back maintenance, no refined).
    /// </summary>
    public void RemoveObject(int id)
    {
        if (!E.Objects.TryGetValue(id, out var o)) return;
        Remove(o);
        _fleets.ObjectRemoved(null, 0, o); // silent: a fleet ship leaves its fleet, units aboard land (m3-fleets §Carrying rules)
    }

    /// <summary>Setup only: slot values of a system (replaces the file values). rule: m2-slots-and-resources §Slot values.</summary>
    public void SetSlots(string system, int energy, int raw)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        if (energy < 0 || raw < 0) throw new ArgumentException("Slots cannot be negative.");
        E.Slots[system] = new SlotState { Energy = energy, Raw = raw, Populated = SlotsOf(system).Populated };
    }

    /// <summary>
    /// The Death Star destroyed the system: no energy and no raw slots, not populated (the stored slot values are replaced). The facilities and units
    /// on it are removed by the Death Star module. rule: m5-death-star §2 effect 5 (no energy or raw slots, no production).
    /// </summary>
    public void ClearSlotsForDestroyedSystem(string system)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        E.Slots[system] = new SlotState { Energy = 0, Raw = 0, Populated = false };
    }

    /// <summary>Setup only: Alliance support of a system, 0..100 (Empire support is 100 minus it). Read-only for the economy afterwards.</summary>
    public void SetAllianceSupport(string system, int allianceSupport)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        if (allianceSupport is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(allianceSupport));
        E.AllianceSupport[system] = allianceSupport;
    }

    /// <summary>Setup only: a side's stockpile.</summary>
    public void SetPools(Side side, int raw, int refined)
    {
        if (raw < 0 || refined < 0) throw new ArgumentException("Pools cannot be negative.");
        E.Pool(side).Raw = raw; E.Pool(side).Refined = refined;
    }

    /// <summary>
    /// New-game setup for the economy (M5 calls it after dealing the galaxy): the starting stockpile, then the rolled slots if
    /// <see cref="SlotSource"/> is Rolled (systems in ordinal name order), then, if incidents are on, the two timers armed
    /// (resource first, then disaster, one interval draw each). The order of the three parts is a DEFAULT.
    /// rule: m2-slots-and-resources §Where it runs and what it draws (New game setup row); m2-mines-refineries-pools §Starting stockpile.
    /// </summary>
    public void SetupNewGame()
    {
        SetPools(Side.Empire, Params.StartRaw, Params.StartRefined);
        SetPools(Side.Alliance, Params.StartRaw, Params.StartRefined);
        if (SlotSource == SlotSource.Rolled)
            foreach (var s in _galaxy.Systems) // ordinal name order
                E.Slots[s.Name] = SlotRoller.Roll(_game.Rng, s.Group == "core", Params);
        if (IncidentsEnabled)
        {
            int t = _game.Day + 1;
            E.ResourceDue = t + Params.ResourceIntervalMin + _game.Rng.NextInt(0, Params.ResourceIntervalSpread);
            E.DisasterDue = t + Params.DisasterIntervalMin + _game.Rng.NextInt(0, Params.DisasterIntervalSpread);
        }
    }

    // ================================================================== queries

    public int Raw(Side s) => E.Pool(s).Raw;
    public int Refined(Side s) => E.Pool(s).Refined;
    public IReadOnlyList<int> RawLine(Side s) => E.Pool(s).RawLine;
    public IReadOnlyCollection<EconObject> Objects => E.Objects.Values;
    public EconObject? Find(int id) => E.Objects.TryGetValue(id, out var o) ? o : null;

    /// <summary>Slot values now: the file's, unless an incident or setup changed them.</summary>
    public SlotState SlotsOf(string system)
    {
        if (E.Slots.TryGetValue(system, out var s)) return new SlotState { Energy = s.Energy, Raw = s.Raw, Populated = s.Populated };
        var v = _slots.Of(IndexOf(system));
        return new SlotState { Energy = v.Energy, Raw = v.Raw, Populated = v.Populated };
    }

    /// <summary>raw_eff = min(raw, energy). rule: m2-slots-and-resources §Effective raw (DEFAULT, Lead).</summary>
    public int EffectiveRaw(string system) { var s = SlotsOf(system); return Math.Min(s.Raw, s.Energy); }

    /// <summary>energy_used: installed facilities plus facilities in delivery or queued for the system. rule: §Occupied slots.</summary>
    public int EnergyUsed(string system) => E.Objects.Values.Count(o => o.Category == ObjectCategory.Facility && o.System == system);

    /// <summary>raw_used: installed mines plus mines in delivery or queued for the system.</summary>
    public int RawUsed(string system) => E.Objects.Values.Count(o => IsClass(o, FacilityKind.Mine) && o.System == system);

    /// <summary>Facilities of <paramref name="kind"/> on the system: finished, queued or in delivery.</summary>
    public int OfKindAt(string system, FacilityKind kind) => E.Objects.Values.Count(o => IsClass(o, kind) && o.System == system);

    public int FreeEnergy(string system) => SlotsOf(system).Energy - EnergyUsed(system);
    public int FreeRaw(string system) => EffectiveRaw(system) - RawUsed(system);

    /// <summary>
    /// Can another facility of this kind be ordered for the system? Null = yes, else "NoSlot". Construction calls this when it
    /// accepts a build order. rule: m2-slots-and-resources §Occupied slots ("A facility can only be ordered for a system with a free energy slot (and a free raw slot for a mine)").
    /// </summary>
    public string? CheckPlace(string system, FacilityKind kind, int count = 1)
    {
        // rule: m5-fog-and-hq §N4 (Decided, Paul 2026-10-16): a Sensor Station only on a system on a hyperspace route.
        if (FacilityKinds.RouteOnly(kind) && !(_galaxy.TryFindSystem(system, out int at) && _galaxy.OnRoute(at))) return "NotOnRoute";
        // rule: m5-fog-and-hq §N4 (economy/15 §2): one per planet; counts finished, queued and in-delivery stations.
        if (FacilityKinds.OnePerPlanet(kind) && (count > 1 || OfKindAt(system, kind) > 0)) return "OnePerPlanet";
        if (FreeEnergy(system) < count) return "NoSlot";
        if (kind == FacilityKind.Mine && FreeRaw(system) < count) return "NoSlot";
        return null;
    }

    /// <summary>Has the Death Star destroyed the system? A destroyed system has no slots, no production and no support value. rule: m5-death-star §2 effect 5.</summary>
    public bool IsDestroyed(string system) => _game.State.DeathStar.IsDestroyed(system);

    /// <summary>Support of a side in a system. Alliance share is stored; Empire = 100 - it. rule: m2-popular-support §What it stores. A destroyed system has no support value: 0 for both sides (rule: m5-death-star §2 effect 5).</summary>
    public int SupportOf(string system, Side side)
    {
        if (IsDestroyed(system)) return 0;
        int a = E.AllianceSupport.TryGetValue(system, out var v) ? v : Params.DefaultAllianceSupport;
        return side == Side.Alliance ? a : 100 - a;
    }

    // A facility queued or in delivery carries its ordering side (m2-construction); an installed one has none and follows its system.
    public Side? OwnerOf(EconObject o) => o.Side ?? (o.Category == ObjectCategory.Facility && o.System != null ? _fleets.OwnerOf(o.System) : null);

    /// <summary>
    /// capacity = min(50 x mines, 50 x refineries) over the completed ones on systems the side controls that are not suspended (a blockade, or an
    /// uprising with <see cref="UprisingStopsMining"/>): with <see cref="SuspendedLeaveCapacity"/> on (Classic) they leave the sums at once.
    /// rule: m2-maintenance §Capacity (B5b §3, [EXE-own] 0x52fff0).
    /// </summary>
    public int Capacity(Side side)
    {
        int mines = 0, refs = 0;
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal); // lookup only
        foreach (var o in E.Objects.Values)
        {
            if (!o.Completed || OwnerOf(o) != side) continue;
            bool mine = IsClass(o, FacilityKind.Mine);
            if (!mine && !IsClass(o, FacilityKind.Refinery)) continue;
            if (SuspendedLeaveCapacity && SuspendedAt(o.System!, side, memo)) continue;
            if (mine) mines++; else refs++;
        }
        return Params.CapacityPerFacility * Math.Min(mines, refs);
    }

    /// <summary>Sum of maintenance over every existing object of the side (queued, in delivery and completed). rule: m2-maintenance §Load.</summary>
    public int Load(Side side)
    {
        int sum = 0;
        foreach (var o in E.Objects.Values)
            if (OwnerOf(o) == side) sum += Costs.Get(o.Class).Maintenance;
        return sum;
    }

    /// <summary>capacity - load, may be negative. rule: m2-maintenance §Load (free).</summary>
    public int Free(Side side) => Capacity(side) - Load(side);

    /// <summary>
    /// This facility's dealt slice of its side's maintenance load (0 for a stopped one). Dealt incrementally per class at slot 6.5
    /// (<see cref="DealShares"/>). rule: m2-mines-refineries-pools §Load share dealing (B5b §3).
    /// </summary>
    public int ShareOf(int facilityId) => E.Objects[facilityId].Share;

    /// <summary>
    /// Incremental dealing ([EXE-own] 0x52f6b0 mines, 0x52f8f0 refineries): per side and class, only the difference between the target
    /// min(load, 50 x working facilities) and what is already dealt is handed out or taken back, one unit at a time; existing shares are kept,
    /// so a facility that starts while the load does not change keeps share 0. A unit goes to the working facility with the lowest share (ties:
    /// lowest id), capped at 50; one is taken back from the highest share (ties: highest id). The exact pass order of Classic ("about its
    /// proportional part plus one") is read as this (DEFAULT, B5b §3; it gives the batch's B5b-6 numbers). Each class carries the whole load.
    /// </summary>
    public void DealShares()
    {
        foreach (var side in new[] { Side.Empire, Side.Alliance })
        {
            int load = Load(side);
            foreach (var kind in new[] { FacilityKind.Mine, FacilityKind.Refinery })
            {
                var working = Cyclers(kind).Where(o => o.WorkSide == side).ToList(); // id order
                int cap = Params.CapacityPerFacility;
                int target = Math.Min(load, cap * working.Count), dealt = working.Sum(o => o.Share);
                while (dealt < target)
                {
                    EconObject? pick = null;
                    foreach (var o in working) if (o.Share < cap && (pick == null || o.Share < pick.Share)) pick = o;
                    pick!.Share++; dealt++;
                }
                while (dealt > target)
                {
                    EconObject? pick = null;
                    foreach (var o in working) if (o.Share > 0 && (pick == null || o.Share >= pick.Share)) pick = o;
                    pick!.Share--; dealt--;
                }
            }
        }
    }

    /// <summary>
    /// Cycle length in whole days: k = max(1, 50 x GNPRTB4096 / 100); eff = GNPRTB7763 x 100 / max(support, 1);
    /// d = (ceil(share / k) + rate) x eff / 100, every division rounding down. rule: m2-mines-refineries-pools §Cycle length.
    /// </summary>
    public static int CycleDays(EconomyParams p, int support, int share)
    {
        int k = Math.Max(1, p.CapacityPerFacility * p.LoadBrakeDivisor / 100);
        int eff = p.SlowdownNumerator * 100 / Math.Max(support, 1);
        return ((share + k - 1) / k + p.ProcessingRate) * eff / 100;
    }

    /// <summary>
    /// The smuggling chance v in percent for a cycle that ends at a system held by <paramref name="controller"/>:
    /// clamp(tier(support) - 10 x ships - 5 x fighters - 2 x regiments, 0, 100); 0 if the controller's Death Star is in a fleet
    /// there. rule: m2-mines-refineries-pools §Diversion (smuggling).
    /// </summary>
    public int DiversionPercent(string system, Side controller)
    {
        int support = SupportOf(system, controller);
        int tier = 0;
        foreach (var (max, pct) in Params.SmugglingTiers) if (support <= max) { tier = pct; break; }
        int ships = 0, fighters = 0, regiments = 0;
        foreach (var f in _fleets.FleetsAt(system))
        {
            if (f.Side != controller) continue;
            // rule: m3-fleets §Carrying rules (aboard units are not "on the system"); only ships count from a fleet.
            foreach (var id in f.Ships)
            {
                if (E.Objects.TryGetValue(id, out var ship) && ship.Class == DeathStarClass) return 0;
                ships++;
            }
        }
        foreach (var o in E.Objects.Values)
        {
            if (!o.Completed || o.Side != controller || o.System != system || o.AboardShip != 0) continue;
            // rule: m2-mines-refineries-pools §Diversion: fighter squadrons and regiments of that side ON the system (aboard ones do not count)
            if (o.Category == ObjectCategory.Regiment) regiments++;
            else if (o.Category == ObjectCategory.Fighter) fighters++;
        }
        int mult = controller == Side.Empire && IsDeathStarSector(system) ? Params.DeathStarRegimentMultiplier : 1;
        int v = tier - Params.ShipPresence * ships - Params.FighterPresence * fighters - Params.RegimentPresence * mult * regiments;
        return Math.Clamp(v, 0, 100);
    }

    /// <summary>
    /// Is a mine or refinery suspended: its system under the blockade that stops it, or in uprising (with <see cref="UprisingStopsMining"/>)?
    /// rule: m2-mines-refineries-pools §Blockade and uprising (B5b §3, [EXE-own] 0x50bbb0).
    /// </summary>
    public bool IsSuspended(EconObject f) => IsSuspended(f, new Dictionary<string, bool>(StringComparer.Ordinal));

    /// <summary>The same with a per-hook memo (slot 6.5 clears it each Step). Lookup only, never iterated.</summary>
    private bool IsSuspended(EconObject f, Dictionary<string, bool> memo)
    {
        if (f.System == null || _fleets.OwnerOf(f.System) is not Side side) return false;
        return SuspendedAt(f.System, side, memo);
    }

    private bool SuspendedAt(string system, Side holder, Dictionary<string, bool> memo)
    {
        if (memo.TryGetValue(system, out var b)) return b;
        b = (BlockadeStopsMining && _fleets.IsEconomicBlockade(system, holder)) || (UprisingStopsMining && IsInUprising(system));
        memo[system] = b;
        return b;
    }

    private int IndexOf(string system) =>
        _galaxy.TryFindSystem(system, out var i) ? i : throw new ArgumentException($"Unknown system '{system}'.");

    private static bool IsClass(EconObject o, FacilityKind k) => o.Category == ObjectCategory.Facility && o.Class == FacilityKinds.ClassName(k);

    private static Side Other(Side s) => s == Side.Empire ? Side.Alliance : Side.Empire;

    // ================================================================== removal, destruction, scrapping

    private void Remove(EconObject o)
    {
        E.Objects.Remove(o.Id);
        E.Empire.RawLine.Remove(o.Id);
        E.Alliance.RawLine.Remove(o.Id);
    }

    /// <summary>
    /// A ship, squadron or regiment destroyed in a battle: leaves the registry at once, no refund, no scrap event (the battle raises UnitDestroyed);
    /// its fleet drops it (and dissolves if empty) and units aboard land. rule: m4-battle-classic §10.
    /// </summary>
    internal void DestroyUnit(StepContext ctx, int day, EconObject o)
    {
        Remove(o);
        UnitLost?.Invoke(ctx, o, day);
        _fleets.ObjectRemoved(ctx, day, o);
        ObjectRemoved?.Invoke(ctx, o, day);
    }

    internal void Destroy(StepContext ctx, int day, EconObject f, DestroyReason reason)
    {
        // rule: m2-slots-and-resources §Over-capacity destruction (frees its maintenance, cancels any cycle, no refund)
        Remove(f);
        string sys = f.System!; int id = f.Id;
        ctx.Emit((i, _) => new FacilityDestroyed(i, day, id, sys, reason));
        ObjectRemoved?.Invoke(ctx, f, day);
    }

    private List<EconObject> Installed(string system) =>
        E.Objects.Values.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system).ToList();

    /// <summary>
    /// Over-capacity destruction, to run whenever a system's energy or raw changed: energy check first, then raw check; each
    /// pick is rand(0..n-1) over the candidates sorted by facility id. rule: m2-slots-and-resources §Over-capacity destruction
    /// (the pick is DEFAULT). M3 (assault, bombardment) calls it too.
    /// </summary>
    public void EnforceCapacity(StepContext ctx, string system)
    {
        int day = ctx.Day + 1;
        var s = SlotsOf(system);
        while (true) // 1. energy
        {
            var inst = Installed(system);
            if (inst.Count <= s.Energy) break;
            Destroy(ctx, day, inst[ctx.Rng.NextInt(0, inst.Count - 1)], DestroyReason.OverEnergy);
        }
        int rawEff = Math.Min(s.Raw, s.Energy);
        while (true) // 2. raw
        {
            var mines = Installed(system).Where(o => IsClass(o, FacilityKind.Mine)).ToList();
            if (mines.Count <= rawEff) break;
            Destroy(ctx, day, mines[ctx.Rng.NextInt(0, mines.Count - 1)], DestroyReason.OverRaw);
        }
    }

    /// <summary>Scrap one completed object: removed at once, side's refined +floor(cost / 2). rule: m2-maintenance §Scrapping and refunds.</summary>
    internal void Scrap(StepContext ctx, int day, EconObject o, Side side, string reason)
    {
        int refund = Costs.Get(o.Class).Cost / Params.RefundDivisor;
        Remove(o);
        E.Pool(side).Refined += refund;
        int id = o.Id; string cls = o.Class;
        ctx.Emit((i, _) => new ObjectScrapped(i, day, id, cls, side, reason, refund));
        _fleets.ObjectRemoved(ctx, day, o); // rule: m3-fleets §What a fleet is invariant 1; §Carrying rules (scrapping a loaded ship)
        ObjectRemoved?.Invoke(ctx, o, day);
        if (refund > 0) RefinedPoolGrew?.Invoke(ctx, side);
    }

    // rule: m2-construction §Scrapping (ScrapObject, step 1): any completed installed object of the side, except the ones Classic answers code 23
    // ("cannot be scrapped": the Rebel HQ, characters and special forces; the HQ and characters are not registry objects here, so they are
    // UnknownObject). No check reads blockade, uprising or who holds the system (B5a section 8, handler 0x579860).
    private OrderResult HandleScrap(StepContext ctx, ScrapObjectOrder o)
    {
        if (!E.Objects.TryGetValue(o.ObjectId, out var obj)) return OrderResult.Reject(RejectReason.UnknownObject);
        if (OwnerOf(obj) != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (!obj.Completed) return OrderResult.Reject(RejectReason.NotCompleted);
        if (obj.Category == ObjectCategory.SpecialForce) return OrderResult.Reject(RejectReason.CannotScrap); // B5a decision 3: refused in both rulesets
        Scrap(ctx, ctx.Day, obj, o.Side, "Order");
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // ================================================================== 6.4 incident timers

    // rule: m2-slots-and-resources §Where it runs and what it draws (6.4.a resource, then 6.4.b disaster)
    private void ResourceAndDisasterTimers(StepContext ctx)
    {
        if (!IncidentsEnabled) return;
        int t = ctx.Day + 1;
        if (E.ResourceDue != 0 && E.ResourceDue == t) FireResource(ctx, t);
        if (E.DisasterDue != 0 && E.DisasterDue == t) FireDisaster(ctx, t);
    }

    // rule: m2-slots-and-resources §Resource incident steps 1 to 5
    private void FireResource(StepContext ctx, int t)
    {
        var rng = ctx.Rng;
        string sys = _galaxy.Systems[rng.NextInt(0, _galaxy.Systems.Count - 1)].Name; // ordinal name order (DEFAULT: every system)
        int roll = rng.NextInt(0, 99);
        var s = SlotsOf(sys);
        var change = SlotChange.None;
        int before, after;
        bool hasMine = Installed(sys).Any(o => IsClass(o, FacilityKind.Mine));
        bool hasFacility = Installed(sys).Count > 0;
        int cap = Params.SlotCap;
        // rule: m5-death-star §2 effect 5 (a destroyed system has no energy or raw slots): the draws are made, nothing changes
        if (IsDestroyed(sys)) { before = after = 0; }
        else
        switch (roll / 25) // four equal bands: raw -1, energy -1, raw +1, energy +1
        {
            case 0:
                before = s.Raw; if (hasMine && s.Raw > 0) { s.Raw--; change = SlotChange.RawMinus; } after = s.Raw; break;
            case 1:
                before = s.Energy; if (hasFacility && s.Energy > 0) { s.Energy--; change = SlotChange.EnergyMinus; } after = s.Energy; break;
            case 2:
                before = s.Raw; if (s.Raw < cap && s.Raw < s.Energy) { s.Raw++; change = SlotChange.RawPlus; } after = s.Raw; break;
            default:
                before = s.Energy; if (s.Energy < cap) { s.Energy++; change = SlotChange.EnergyPlus; } after = s.Energy; break;
        }
        if (change != SlotChange.None) E.Slots[sys] = s;
        EnforceCapacity(ctx, sys);
        ctx.Emit((i, _) => new ResourceIncident(i, t, sys, change, before, after));
        E.ResourceDue = t + Params.ResourceIntervalMin + rng.NextInt(0, Params.ResourceIntervalSpread);
    }

    // rule: m2-slots-and-resources §Disaster incident (effects Lead, DEFAULT: facilities only, energy and raw unchanged)
    private void FireDisaster(StepContext ctx, int t)
    {
        var rng = ctx.Rng;
        string sys = _galaxy.Systems[rng.NextInt(0, _galaxy.Systems.Count - 1)].Name;
        var inst = Installed(sys); // id order, whatever the side (neutral included)
        var hit = new List<EconObject>();
        foreach (var f in inst)
            if (rng.NextInt(0, 99) < Params.DisasterDestroyPercent) hit.Add(f);
        foreach (var f in hit) Destroy(ctx, t, f, DestroyReason.Disaster);
        var ids = new ValueList<int>(hit.Select(f => f.Id).ToList());
        int rolled = inst.Count;
        ctx.Emit((i, _) => new Disaster(i, t, sys, ids, rolled));
        E.DisasterDue = t + Params.DisasterIntervalMin + rng.NextInt(0, Params.DisasterIntervalSpread);
    }

    // ================================================================== 6.5 mines, then refineries

    /// <summary>The side a mine or refinery should work for now: completed, on a held system, not suspended; else null (IDLE). rule: B5b §1 (Active, 0x55aa80).</summary>
    private Side? WantedSide(EconObject f) => f.Completed && OwnerOf(f) is Side s && !IsSuspended(f, _blockCache) ? s : null;

    private IEnumerable<EconObject> Cyclers(FacilityKind kind) =>
        E.Objects.Values.Where(o => IsClass(o, kind)); // id ascending

    // rule: m2-mines-refineries-pools §Where it runs, slot 6.5 (global facility id inside each class). The Classic game runs these in one timer queue;
    // the slots are kept (DEFAULT, B5b §9).
    private void MinesAndRefineries(StepContext ctx)
    {
        int t = ctx.Day + 1;
        _blockCache.Clear();
        NormalizeLines();

        // 1. Stops and restarts (mines, then refineries, id order): a facility that is no longer active for the side it worked for stops at once,
        //    its running cycle abandoned with no point (a refinery's raw already taken is lost), share 0, out of the raw line; an active IDLE one
        //    goes to WAITING on a startup cycle (first start, end of a blockade or uprising, side change). rule: B5b §1, §2, §3 (0x50bbb0, 0x55b010, 0x53b1d0)
        //    With MineStopMode Pause (DEFAULT) a suspended facility keeps its running cycle (PausedFor): frozen while suspended, resumed with no
        //    startup cycle when it is active again for the same side; any other change drops it. rule: m2-mines-refineries-pools §Blockade and
        //    uprising (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch)
        var starting = new List<EconObject>();
        foreach (var kind in new[] { FacilityKind.Mine, FacilityKind.Refinery })
            foreach (var f in Cyclers(kind).ToList())
            {
                var want = WantedSide(f);
                if (f.WorkSide != null && f.WorkSide != want) Stop(ctx, t, f);
                if (f.PausedFor is Side held)
                {
                    if (want == held) { f.WorkSide = held; f.PausedFor = null; continue; } // resumes where it left off
                    if (want == null && f.Completed && OwnerOf(f) == held) { f.Due++; continue; } // still suspended: frozen one more day
                    f.PausedFor = null; f.Due = 0; // the system changed hands or the facility is gone from use: the paused cycle is lost
                }
                if (f.WorkSide == null && want is Side w)
                {
                    f.WorkSide = w;
                    if (f.Due != 0 || f.InLine) continue; // already running (a setup or a save from before B5b): adopted as it is
                    f.Startup = true;
                    starting.Add(f);
                }
            }
        // 2. The load is dealt to the working facilities. rule: B5b §3
        DealShares();
        // 3. Mines due, 4. refineries due
        foreach (var m in Cyclers(FacilityKind.Mine).Where(o => o.Due == t).ToList())
            FinishCycle(ctx, t, m, raw: true);
        foreach (var r in Cyclers(FacilityKind.Refinery).Where(o => o.Due == t).ToList())
            FinishCycle(ctx, t, r, raw: false);
        // 5. The restarted ones go to WAITING: a mine starts at once, a refinery asks for raw (mines first, id order).
        foreach (var f in starting)
            if (IsClass(f, FacilityKind.Mine)) StartCycle(ctx, t, f); else AskForRaw(ctx, t, f);
    }

    /// <summary>
    /// IDLE: the running cycle is abandoned (no point), the share drops to 0, the facility leaves its raw line. rule: B5b §3. A suspended one under
    /// <see cref="MineStopMode"/> Pause keeps its running cycle instead (frozen, PausedFor = its side). rule: m2-mines-refineries-pools §Blockade and
    /// uprising (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch).
    /// </summary>
    private void Stop(StepContext ctx, int t, EconObject f)
    {
        var side = f.WorkSide!.Value;
        int id = f.Id; string sys = f.System!;
        string reason = f.Completed && OwnerOf(f) == side ? "Suspended" : "Released";
        ctx.Emit((i, _) => new FacilityStopped(i, t, id, sys, side, reason));
        if (reason == "Suspended" && MineStopMode == StopMode.Pause && f.Due != 0) f.PausedFor = side; // frozen from today (the caller moves Due on)
        else { f.Due = 0; f.Startup = false; }
        f.Share = 0; f.WorkSide = null;
        if (f.InLine) { E.Empire.RawLine.Remove(f.Id); E.Alliance.RawLine.Remove(f.Id); f.InLine = false; }
    }

    /// <summary>Entries that are gone, not completed, or no longer working for the line's side leave the line. rule: m2-mines-refineries-pools §Side change (B5b §3: the old place is lost).</summary>
    private void NormalizeLines()
    {
        foreach (var side in new[] { Side.Alliance, Side.Empire })
        {
            var line = E.Pool(side).RawLine;
            for (int i = 0; i < line.Count;)
            {
                E.Objects.TryGetValue(line[i], out var f);
                if (f == null || !f.Completed || OwnerOf(f) != side) { if (f != null) f.InLine = false; line.RemoveAt(i); continue; }
                i++;
            }
        }
    }

    // rule: m2-mines-refineries-pools §Where it runs, 6.5 steps 1 and 2; §Diversion (B5b §4: one draw when v > 0, none at 0)
    private void FinishCycle(StepContext ctx, int t, EconObject f, bool raw)
    {
        f.Due = 0;
        if (f.WorkSide is not Side ctl || !f.Completed) return; // a stopped facility has no cycle

        var pool = ctl;
        bool diverted = false;
        int v = DiversionPercent(f.System!, ctl);
        if (v > 0 && ctx.Rng.NextInt(0, 99) < v) { pool = Other(ctl); diverted = true; } // no draw when v = 0 (DEFAULT)

        if (raw) E.Pool(pool).Raw++; else E.Pool(pool).Refined++;
        int id = f.Id; string sys = f.System!;
        ctx.Emit((i, _) => new ResourceProduced(i, t, id, sys, raw ? "Raw" : "Refined", ctl, pool, diverted, v));
        f.Startup = false; // DONE -> WAITING clears the startup flag (B5b §2)

        if (raw)
        {
            ServeRawLine(ctx, t, pool);
            StartCycle(ctx, t, f);
        }
        else
        {
            RefinedPoolGrew?.Invoke(ctx, pool);
            AskForRaw(ctx, t, f);
        }
    }

    // rule: m2-mines-refineries-pools §Cycle length, First cycle (B5b §2: a startup cycle on every restart; one draw when it starts, DEFAULT)
    private void StartCycle(StepContext ctx, int t, EconObject f)
    {
        var owner = f.WorkSide!.Value;
        int support = Math.Clamp(SupportOf(f.System!, owner), Params.SupportFloor, Params.SupportCeiling); // read when the cycle starts
        int d = CycleDays(Params, support, f.Share);
        if (f.Startup)
        {
            d = ctx.Rng.NextInt(0, d) + d / 2; // startup cycle: rand(0..d) + d / 2
            int id = f.Id, days = d; string sys = f.System!;
            ctx.Emit((i, _) => new StartupCycle(i, t, id, sys, owner, days));
        }
        f.FirstDone = true;
        f.Due = t + d;
    }

    // rule: m2-mines-refineries-pools §Asking for raw / serving a line (B5b §1: a refinery entering WAITING takes raw at once if raw > 0, even if
    // others wait; otherwise it joins the first-in first-out line)
    private void AskForRaw(StepContext ctx, int t, EconObject r)
    {
        var side = r.WorkSide!.Value;
        var pool = E.Pool(side);
        if (pool.Raw > 0) { pool.Raw--; StartCycle(ctx, t, r); return; }
        pool.RawLine.Add(r.Id);
        r.InLine = true;
        int id = r.Id; string sys = r.System!;
        ctx.Emit((i, _) => new RefineryWaiting(i, t, id, sys, side));
    }

    // rule: m2-mines-refineries-pools §Asking for raw / serving a line (served whenever raw rises above 0; a stopped refinery has left the line)
    private void ServeRawLine(StepContext ctx, int t, Side side)
    {
        var pool = E.Pool(side);
        while (pool.RawLine.Count > 0 && pool.Raw > 0)
        {
            var f = E.Objects[pool.RawLine[0]];
            pool.RawLine.RemoveAt(0);
            f.InLine = false;
            if (f.WorkSide != side) continue;
            pool.Raw--;
            StartCycle(ctx, t, f);
        }
    }

    // ================================================================== 6.7 autoscrap

    // rule: m2-maintenance §Overdraft and autoscrap; §Where it runs (6.7.a Alliance, 6.7.b Empire). Armed timer (Classic, B5b §6, 0x5329f0): armed when
    // the overdraft starts, first firing 10 days later, then every 10 days while over; dropped when it ends.
    private void Autoscrap(StepContext ctx)
    {
        int t = ctx.Day + 1;
        foreach (var side in new[] { Side.Alliance, Side.Empire })
        {
            // A victim picked while in transit is scrapped once it has arrived (B5b §6, 0x4fb310).
            foreach (var o in E.Objects.Values.Where(o => o.ScrapPending && OwnerOf(o) == side).ToList())
                if (!InTransit(o)) Scrap(ctx, t, o, side, "Autoscrap");
            var pool = E.Pool(side);
            if (AutoscrapTimer == AutoscrapTimer.FreeRunning)
            {
                if (pool.ScrapDue == 0) pool.ScrapDue = Params.ScrapPeriod; // free-running, first due at hook time 10 (the rule before B5b)
                if (pool.ScrapDue != t) continue;
                AutoscrapFiring(ctx, t, side);
                pool.ScrapDue = t + Params.ScrapPeriod;
                continue;
            }
            bool over = Load(side) > Capacity(side);
            if (!over) { pool.ScrapDue = 0; continue; }                          // the pending firing is dropped
            if (pool.ScrapDue == 0) { pool.ScrapDue = t + Params.ScrapPeriod; continue; } // the overdraft starts: armed
            if (pool.ScrapDue != t) continue;
            AutoscrapFiring(ctx, t, side);
            pool.ScrapDue = t + Params.ScrapPeriod;
        }
    }

    /// <summary>A capital ship or squadron whose fleet is on a lane (Completed and en route). rule: m2-maintenance §Overdraft step 2 (B5b §6).</summary>
    private bool InTransit(EconObject o)
    {
        if (o.Category == ObjectCategory.CapitalShip) return _fleets.FleetOfShip(o.Id) is { At: null };
        if (o.Category != ObjectCategory.Fighter) return false;
        if (o.AboardShip != 0) return _fleets.FleetOfShip(o.AboardShip) is { At: null };
        return _game.State.Fleets.Values.Any(f => f.At == null && f.Squadrons.Contains(o.Id));
    }

    private void AutoscrapFiring(StepContext ctx, int t, Side side)
    {
        if (Load(side) <= Capacity(side)) return; // nothing happens, no draw
        // eligible: completed (in transit included), maintenance not 0, never a mine or refinery, sorted by object id (Objects is id-sorted).
        // A victim already pending can be picked again (a wasted firing, B5b §6).
        var eligible = E.Objects.Values
            .Where(o => o.Completed && OwnerOf(o) == side && Costs.Get(o.Class).Maintenance != 0
                        && !IsClass(o, FacilityKind.Mine) && !IsClass(o, FacilityKind.Refinery))
            .ToList();
        if (eligible.Count == 0) return;

        EconObject pick;
        if (AutoscrapMode == AutoscrapMode.Classic)
        {
            pick = eligible[ctx.Rng.NextInt(0, eligible.Count - 1)]; // one uniform pick, one object per firing
        }
        else
        {
            // Smart (Proposal, no draw): not yards; if nothing else, the yards after a warning. Groups: regiments and special
            // forces, fighters, capital ships, other facilities; inside a group lowest refined cost, then lowest id.
            var cands = eligible.Where(o => !(o.Category == ObjectCategory.Facility && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k))).ToList();
            bool yardsOnly = cands.Count == 0;
            if (yardsOnly) cands = eligible;
            pick = cands.OrderBy(o => yardsOnly ? 0 : SmartGroup(o)).ThenBy(o => Costs.Get(o.Class).Cost).ThenBy(o => o.Id).First();
            if (yardsOnly) { int id = pick.Id; ctx.Emit((i, _) => new AutoscrapWarning(i, t, side, id)); }
        }
        // rule: B5b §6: destroyed at once only if usable; a victim in transit is marked and scrapped on arrival (with the FreeRunning rule: at once, as before)
        if (AutoscrapTimer == AutoscrapTimer.Armed && InTransit(pick)) { pick.ScrapPending = true; return; }
        Scrap(ctx, t, pick, side, "Autoscrap");
    }

    private static int SmartGroup(EconObject o) => o.Category switch
    {
        ObjectCategory.Regiment or ObjectCategory.SpecialForce => 0,
        ObjectCategory.Fighter => 1,
        ObjectCategory.CapitalShip => 2,
        _ => 3,
    };
}
