using System.Reflection;
using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Map;
using Praedra.Core.Support;

namespace Praedra.Core.Fog;

/// <summary>What a side knows about a system, derived on demand, never stored. rule: m5-fog-and-hq §N1.</summary>
public enum KnowledgeLevel { Unknown, Charted, Observed, Held }

/// <summary>The side's current belief about the HQ: the system its picture shows the HQ at, and the stamp of that sighting. rule: §N5 KnownHq.</summary>
public sealed record KnownHqInfo(string System, int Stamp);

/// <summary>
/// Fog of war and the Hearth (M5). Holds settings only; the state lives in <see cref="GameState.Fog"/> (pictures, logs, clues) and
/// <see cref="Victory.ScenarioState.Hq"/> (the HQ). Every write of a picture goes through <see cref="Reveal"/>. Draws random numbers only in
/// the espionage extras (<see cref="EspionageSuccess"/>); reveals and HQ moves draw none. After <see cref="GameSave.Load"/>, attach again.
///
/// Slots (rule: m5-fog-and-hq §F8): 1 MoveHq; 2 HqDeparted (after the fleet departures); 3 HQ arrival (after the fleet arrivals);
/// 3.1 sightings; 5.4 live refresh (after bombardment and assault, after m5-missions' 5.3); 6.8 the sighting log is pruned (after repair).
/// Capture (any slot): <see cref="ControlModule.HolderChanged"/> gives the loser its LossNotice and applies the HQ capture rule (F7).
/// Every loop over systems is in galaxy index order (= ordinal name), sides Hand then Collective, fleets by id. rule: §N9.
/// </summary>
public sealed class FogModule
{
    public const string CauseCaptured = "Captured";
    /// <summary>The HQ world turned from the Collective on support alone, no troops there (B5a; QA-11, 2026-10-18).</summary>
    public const string CauseLostSupport = "LostSupport";
    /// <summary>The HQ world's last Collective regiment left or died and support handed the world over (B5a; QA-11, 2026-10-18).</summary>
    public const string CauseTroopsLeft = "TroopsLeft";
    public const string CauseDestinationLost = "DestinationLost";
    public const string CauseSystemDestroyed = "SystemDestroyed";

    private static readonly Side[] Sides = { Side.Hand, Side.Collective };

    private readonly Game _game;
    private readonly Galaxy _galaxy;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;
    private readonly SupportModule _support;

    public FogParams Params { get; }
    public HqParams HqRules { get; }

    /// <summary>
    /// Hook for m5-missions: does the side have an agent on an active mission at the system (not travelling)? Null = no agents exist yet.
    /// Counts as live access only when <see cref="FogParams.AgentPresenceCounts"/> is on. rule: §F3 3, Open question 6.
    /// </summary>
    public Func<Side, string, bool>? AgentsAt { get; set; }

    // Step-local scratch, set and used inside one Step (a save is made between Steps, so none of this needs saving).
    private readonly HashSet<int> _inLane = new(); // membership test only, never iterated
    private bool _hqAcceptedThisStep;
    private int _stepEventStart;

    // EventsFor caches (events are not saved either)
    private readonly List<GameEvent>[] _eventsFor = { new(), new() };
    private int _eventsScanned;

    private FogModule(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support, FogParams p, HqParams hq)
    {
        _game = game; _galaxy = galaxy; _fleets = fleets; _eco = eco; _support = support; Params = p; HqRules = hq;
    }

    /// <summary>Attach after the fleet, economy, support and control modules (and after repair: it shares slot 6.8), before victory.</summary>
    public static FogModule Attach(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support, ControlModule control,
        FogParams? p = null, HqParams? hq = null)
    {
        hq ??= new HqParams();
        if (hq.LostDestination == HqLostDestination.Return)
            throw new NotSupportedException("HqLostDestination.Return is a switch value only; m5-fog-and-hq §F6 says it is not built in the first pass.");
        if (!hq.IgnoresForcedStop)
            throw new NotSupportedException("HqParams.IgnoresForcedStop = false is not built (m5-fog-and-hq §F6: the HQ is not a fleet).");
        var m = new FogModule(game, galaxy, fleets, eco, support, p ?? new FogParams(), hq);
        game.RegisterOrderHandler<MoveHqOrder>(m.HandleMoveHq);
        game.RegisterHook("2", m.Departures);
        game.RegisterHook("3", m.HqArrival);
        game.RegisterHook("3.1", m.Sightings);
        game.RegisterHook("5.4", m.LiveRefresh);
        game.RegisterHook("6.8", m.PruneLogs);
        control.HolderChanged += m.OnHolderChanged;
        game.AudienceResolver = m.Audience;
        return m;
    }

    private GameState State => _game.State;
    private FogState F => State.Fog;
    private HqState Hq => State.Scenario.Hq;
    private static Side Other(Side s) => s == Side.Hand ? Side.Collective : Side.Hand;

    // ================================================================== setup

    /// <summary>
    /// Setup, day 0 (outside a Step; raises nothing): systems with <c>start_explored_all</c> give both sides Explored and groups A and B, and C
    /// (the facilities) with <see cref="FogParams.SetupRevealsFacilities"/> (Classic = on, B4 §11; DEFAULT off); never D or E, LastLookDay null;
    /// each side gets OwnLive of the systems it holds; the StartFullCopy lists give a FullCopy (The Shoal to the Hand,
    /// only if the galaxy has it). Nothing reveals the HQ system. Call after owners, facilities and fleets are dealt. rule: m5-fog-and-hq §F4.
    /// </summary>
    public void SetupNewGame(IEnumerable<string> startExploredAll)
    {
        int day = _game.Day;
        var all = new SortedSet<string>(startExploredAll.Where(s => _galaxy.TryFindSystem(s, out _)), StringComparer.Ordinal);
        foreach (var s in all) F.StartExploredAll.Add(s);
        var truth = new Truth(this);
        foreach (var side in Sides)
        {
            foreach (var sys in _galaxy.Systems) // index order = ordinal names
            {
                if (all.Contains(sys.Name)) Write(null, truth, side, sys.Name, RevealKind.Setup, day);
                if (_fleets.OwnerOf(sys.Name) == side) Write(null, truth, side, sys.Name, RevealKind.OwnLive, day);
            }
            var list = side == Side.Hand ? Params.StartFullCopyHand : Params.StartFullCopyCollective;
            foreach (var s in list.OrderBy(x => x, StringComparer.Ordinal))
                if (_galaxy.TryFindSystem(s, out _)) Write(null, truth, side, s, RevealKind.FullCopy, day);
        }
    }

    /// <summary>Setup: the HQ stands at <paramref name="system"/> (null = none). Raises no event and no arrival effect. rule: §F5 PlaceHq.</summary>
    public void PlaceHq(string? system)
    {
        if (system != null && !_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        Hq.Place(system);
    }

    // ================================================================== reveal (the one writer)

    /// <summary>
    /// Teach <paramref name="side"/> about <paramref name="system"/> by one reveal kind, from the truth at this moment, stamp K + 1. The one
    /// function every write goes through; m5-missions calls it with FullCopy (espionage: use <see cref="EspionageSuccess"/>) or Recon.
    /// rule: m5-fog-and-hq §F2 (table of kinds).
    /// </summary>
    public void Reveal(StepContext ctx, Side side, string system, RevealKind kind)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        Write(ctx, new Truth(this), side, system, kind, ctx.Day + 1);
    }

    /// <summary>
    /// An espionage success of <paramref name="side"/> on <paramref name="target"/>: FullCopy of the target, then the extra systems, then (if
    /// on) the sector clue. Draws: one for N when its range is above 0, then one per pick; none when the target was unexplored to the side.
    /// rule: m5-fog-and-hq §F2 Espionage success (Traced, 05 10.1); §N6 SectorSuspicion.
    /// </summary>
    public void EspionageSuccess(StepContext ctx, Side side, string target)
    {
        if (!_galaxy.TryFindSystem(target, out var ti)) throw new ArgumentException($"Unknown system '{target}'.");
        int stamp = ctx.Day + 1;
        bool exploredBefore = F.Of(side).Systems.TryGetValue(target, out var before) && before.Explored;
        Side? holder = _fleets.OwnerOf(target);
        bool hqThere = HqAt(target);
        bool capital = target == Params.CapitalSystem && holder == Side.Hand;
        var truth = new Truth(this);
        Write(ctx, truth, side, target, RevealKind.FullCopy, stamp); // 1.

        if (exploredBefore) // 2. no extras when the target was unexplored before the mission
        {
            bool rich = hqThere || capital;
            int spread = hqThere ? Params.EspionageRichSpread : capital ? Params.EspionageCapitalSpread : Params.EspionageOtherSpread;
            int baseN = hqThere ? Params.EspionageRichBase : capital ? Params.EspionageCapitalBase : Params.EspionageOtherBase;
            int n = (spread > 0 ? ctx.Rng.NextInt(0, spread) : 0) + baseN;
            // Pool: every system held by the same side as the target (neutral: every neutral system), not the target, by name (ordinal);
            // not rich: only the explored class (start_explored_all). The choice method is NOT TRACED; this order is the sheet's DEFAULT.
            var pool = _galaxy.Systems.Where(s => s.Index != ti && _fleets.OwnerOf(s.Name) == holder && (rich || F.StartExploredAll.Contains(s.Name)))
                .Select(s => s.Name).ToList();
            int picks = Math.Min(n, pool.Count);
            for (int k = 0; k < picks; k++) // 3.
            {
                int i = ctx.Rng.NextInt(0, pool.Count - 1);
                string extra = pool[i];
                pool.RemoveAt(i);
                Write(ctx, truth, side, extra, RevealKind.FullCopy, stamp);
            }
        }

        // §N6 sector suspicion (Proposal, off by default): an espionage success on a Collective system in the sector that holds the HQ.
        if (Params.SectorSuspicion == SectorSuspicion.OnEspionage && side != Side.Collective && holder == Side.Collective
            && Hq.Status == HqStatus.AtRest && Hq.System != null && _galaxy.Get(Hq.System).Sector == _galaxy.Systems[ti].Sector)
        {
            string sector = _galaxy.Systems[ti].Sector;
            F.Of(side).Clues.Add(new HqSectorClue(sector, stamp));
            ctx.Emit((i, _) => new HqSectorClueGiven(i, stamp, side, sector));
        }
    }

    /// <summary>
    /// m5-missions' MissionDetected(defender, system, day): the defender's log gets an AgentDetected sighting (system and day only; mission kind
    /// and agent are not given). rule: m5-fog-and-hq §N7 3.
    /// </summary>
    public void OnMissionDetected(StepContext ctx, Side defender, string system)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        int stamp = ctx.Day + 1; var agent = Other(defender);
        F.Of(defender).Sightings.Add(new Sighting { Day = stamp, System = system, Kind = SightingKind.AgentDetected, Side = agent });
        ctx.Emit((i, _) => new AgentDetected(i, stamp, defender, system, agent));
    }

    /// <summary>
    /// The harvester hook (slot 6, m5 later): a destroyed system is shown to both sides (kind Destroyed: A and B), and an HQ at rest there is
    /// destroyed with cause SystemDestroyed; an HQ in transit is not. rule: m5-fog-and-hq §F2 Destroyed, §F7 harvester (Traced, 05 10.2).
    /// </summary>
    public void OnSystemDestroyed(StepContext ctx, string system)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        var truth = new Truth(this);
        foreach (var side in Sides) Write(ctx, truth, side, system, RevealKind.Destroyed, ctx.Day + 1);
        if (HqAt(system)) DestroyHq(ctx, CauseSystemDestroyed, system);
    }

    // The groups each kind writes. rule: §F2 (table). Recon with ReconRevealsFacilities off writes the Presence groups only (see build notes, FE6).
    // groundToo: an Engagement after an assault also writes D (regiment counts).
    private void Write(StepContext? ctx, Truth truth, Side side, string sys, RevealKind kind, int stamp, bool groundToo = false)
    {
        bool a, b, c, d, e;
        switch (kind)
        {
            // rule: m5-fog-and-hq §F2 Setup: A and B, plus C (facilities) with SetupRevealsFacilities (Classic = on, DEFAULT off), never D or E
            // (B4 §11: stage 0x18 gives core systems their facilities, 0x539ac0)
            case RevealKind.Setup: a = b = true; c = Params.SetupRevealsFacilities; d = e = false; break;
            case RevealKind.FullCopy: case RevealKind.OwnLive: a = b = c = d = e = true; break;
            case RevealKind.Presence: a = b = e = true; c = d = false; break;
            case RevealKind.Recon: a = b = e = true; c = Params.ReconRevealsFacilities; d = false; break;
            case RevealKind.Engagement: b = e = true; d = groundToo; a = c = false; break;
            case RevealKind.LossNotice: case RevealKind.HeartlandReport: b = true; a = c = d = e = false; break;
            case RevealKind.Destroyed: a = b = true; c = d = e = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var know = F.Of(side);
        var k = know.Touch(sys);
        if (kind == RevealKind.Destroyed) k.Destroyed = true; // rule: m5-harvester §2 effect 6 (every side's view learns the system is destroyed)
        if (a)
        {
            var slots = _eco.SlotsOf(sys);
            k.Populated = slots.Populated; k.Energy = slots.Energy; k.Raw = slots.Raw; k.StampA = stamp;
            if (!k.Explored)
            {
                k.Explored = true;
                ctx?.Emit((i, _) => new SystemExplored(i, stamp, side, sys, kind));
            }
        }
        if (b)
        {
            Side? now = _fleets.OwnerOf(sys);
            Side? was = k.Owner;
            bool seenBefore = k.Explored && k.StampB != null;
            k.Owner = now; k.CollectiveSupport = _eco.SupportOf(sys, Side.Collective); k.StampB = stamp;
            if (seenBefore && was != now) ctx?.Emit((i, _) => new OwnerChangeSeen(i, stamp, side, sys, was, now));
        }
        if (c)
        {
            var fac = new SortedDictionary<string, int>(StringComparer.Ordinal); int queued = 0;
            foreach (var o in truth.Objects(sys))
            {
                if (o.Category != ObjectCategory.Facility) continue;
                if (o.Completed) fac[o.Class] = fac.GetValueOrDefault(o.Class) + 1; else queued++;
            }
            k.Facilities = fac; k.FacilitiesQueued = queued; k.StampC = stamp;
            if (kind != RevealKind.Setup) k.LastLookDay = stamp; // rule: m5-fog-and-hq §F4 (the day-0 picture is not a look: LastLookDay stays null)
            SetHqHere(know, sys, k, stamp);
        }
        else if (kind == RevealKind.Presence && Params.PresenceRevealsHq) SetHqHere(know, sys, k, stamp); // §N6 PresenceRevealsHq (off by default)
        if (d)
        {
            var bySide = new SortedDictionary<int, GroundPicture>();
            foreach (var o in truth.Objects(sys))
            {
                if (o.Category is not (ObjectCategory.Regiment or ObjectCategory.SpecialForce) || !o.Completed || o.Side is not Side s) continue;
                if (!bySide.TryGetValue((int)s, out var g)) bySide[(int)s] = g = new GroundPicture { Side = s };
                g.Units[o.Class] = g.Units.GetValueOrDefault(o.Class) + 1;
            }
            k.Ground = bySide.Values.ToList(); k.StampD = stamp;
            // rule: m5-fog-and-hq §F1 group D: "characters present", written with the regiments (same reveals, same stamp); id order
            var chars = truth.CharactersAt(sys);
            k.Characters = chars.Count > 0 ? chars.Select(c => c.Clone()).ToList() : null;
        }
        if (e)
        {
            k.Fleets = truth.FleetsAt(sys).Where(f => f.Side != side).Select(truth.Picture).ToList();
            k.StampE = stamp; k.LastLookDay = stamp;
        }
    }

    // §N5: a reveal that looks at the HQ writes the truth; one HQ, so finding it clears the side's other flags (SingleHqCleanup).
    private void SetHqHere(SideKnowledge know, string sys, SystemKnowledge k, int stamp)
    {
        bool here = HqAt(sys);
        k.HqHere = here; k.HqStamp = stamp;
        if (here && Params.SingleHqCleanup)
            foreach (var kv in know.Systems) if (kv.Key != sys) kv.Value.HqHere = false; // SortedDictionary: ordinal order; order does not matter here
    }

    private bool HqAt(string sys) => Hq.Status == HqStatus.AtRest && Hq.System == sys;

    // ================================================================== slot 2: departures

    private void Departures(StepContext ctx)
    {
        _stepEventStart = _game.Events.Count;
        // the fleets on a lane after the fleet departures: whoever of them is placed in step 3 was "placed this Step" for slot 3.1
        _inLane.Clear();
        foreach (var f in State.Fleets.Values) if (f.At == null) _inLane.Add(f.Id);

        // rule: §F6 Acceptance 3: HqDeparted at step 2 (Day K), after the fleet departures
        if (_hqAcceptedThisStep && Hq.Status == HqStatus.InTransit)
        {
            var path = new ValueList<string>(Hq.Path.ToList());
            int total = Hq.HopTicks.Sum(); int arrival = ctx.Day + total; string from = Hq.From!;
            ctx.Emit((i, d) => new HqDeparted(i, d, from, path, total, arrival));
        }
        _hqAcceptedThisStep = false;
    }

    // ================================================================== slot 1: MoveHq

    // rule: m5-fog-and-hq §F6 (rejections checked in the table's order; acceptance 1 to 5)
    private OrderResult HandleMoveHq(StepContext ctx, MoveHqOrder o)
    {
        if (o.Side != Side.Collective) return OrderResult.Reject(HqRejectReason.NotCollective);                 // 1
        if (Hq.Status is HqStatus.Destroyed or HqStatus.None) return OrderResult.Reject(HqRejectReason.NoHq); // 2
        if (Hq.Status == HqStatus.InTransit) return OrderResult.Reject(RejectReason.InTransit);               // 3
        if (!_galaxy.TryFindSystem(o.Destination, out _)) return OrderResult.Reject(RejectReason.UnknownSystem); // 4
        string at = Hq.System!;
        if (o.Destination == at) return OrderResult.Reject(RejectReason.AlreadyThere);                        // 5
        if (HqRules.DestinationMustBeHeld && _fleets.OwnerOf(o.Destination) != Side.Collective) return OrderResult.Reject(HqRejectReason.NotHeld); // 6
        var path = _fleets.FindPath(at, o.Destination, HqRules.Speed);
        if (!path.Ok) return OrderResult.Reject(RejectReason.Unreachable);                                    // 7
        if (IsPinned(at)) return OrderResult.Reject(HqRejectReason.Pinned);                                   // 8
        if (HqRules.UsesFacilitySlot && _eco.FreeEnergy(o.Destination) < 1) return OrderResult.Reject(RejectReason.NoSlot); // 9
        var pool = State.Economy.Pool(Side.Collective);
        if (HqRules.MoveCost > 0 && pool.Refined < HqRules.MoveCost) return OrderResult.Reject(HqRejectReason.NoFunds); // cost, DEFAULT 0
        if (HqRules.MoveCost > 0) pool.Refined -= HqRules.MoveCost;

        // Acceptance: the fastest path by travel time at HqSpeed, fixed now; the HQ leaves at once and is on no system. rule: §F6 Acceptance 1-3
        Hq.From = at; Hq.System = null; Hq.Status = HqStatus.InTransit;
        Hq.Path = path.Systems.Select(i => _galaxy.Systems[i].Name).ToList();
        Hq.HopTicks = path.HopTicks.ToList(); Hq.HopIndex = 0;
        Hq.DueDay = ctx.Day + path.HopTicks[0]; // first hop due on day K + hop ticks (as fleets, m1-travel)
        _hqAcceptedThisStep = true;
        var names = new ValueList<string>(Hq.Path.ToList()); int total = path.TotalTicks; int arrival = ctx.Day + total;
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, null, names, total, arrival));
    }

    /// <summary>Queue a MoveHq order. Returns its sequence number.</summary>
    public long SubmitMoveHq(Side side, string destination) => _game.Submit(seq => new MoveHqOrder(seq, side, destination));

    /// <summary>
    /// The departure refusal: the Classic blockade flag of the system (as left by the previous Step's step 5) is on and
    /// Base - ShipWeight x ships - FighterWeight x fighters &lt; Limit. Operands per <see cref="HqParams.DepartureOperands"/>: Blockaders (DEFAULT)
    /// = capital ships and fighter squadrons (flying or aboard) of every fleet of the blockading side at the system. rule: §F6 Departure refusal.
    /// </summary>
    public bool IsPinned(string system)
    {
        if (HqRules.DepartureOperands == HqDepartureOperands.Off) return false;
        if (!State.Blockades.TryGetValue(system, out var blockader)) return false;
        Side counted = HqRules.DepartureOperands == HqDepartureOperands.Blockaders ? blockader : Side.Collective;
        int ships = 0, fighters = 0;
        foreach (var f in _fleets.FleetsAt(system).Where(f => f.Side == counted))
        {
            ships += f.Ships.Count;
            fighters += f.Squadrons.Count + f.Ships.Sum(id => _fleets.UnitsAboard(id, ObjectCategory.Fighter).Count());
        }
        return HqRules.DepartureBase - HqRules.DepartureShipWeight * ships - HqRules.DepartureFighterWeight * fighters < HqRules.DepartureLimit;
    }

    // ================================================================== slot 3: HQ arrival (after the fleet arrivals)

    // rule: §F6 Arrival. The HQ is not a fleet: no forced stop, no stop at intermediate systems (HqIgnoresForcedStop).
    private void HqArrival(StepContext ctx)
    {
        int day = ctx.Day + 1;
        if (Hq.Status != HqStatus.InTransit || Hq.DueDay != day) return;
        string reached = Hq.Path[Hq.HopIndex + 1];
        if (Hq.HopIndex + 1 < Hq.Path.Count - 1)
        {
            Hq.HopIndex++;
            Hq.DueDay = day + Hq.HopTicks[Hq.HopIndex];
            return;
        }
        if (_fleets.OwnerOf(reached) != Side.Collective)
        {
            DestroyHq(ctx, CauseDestinationLost, reached); // HqLostDestination = DestroyOnArrival: no support effect, no HqArrived
            return;
        }
        Hq.Status = HqStatus.AtRest; Hq.System = reached; ClearTransit();
        string sector = _galaxy.Get(reached).Sector;
        ctx.Emit((i, _) => new HqArrived(i, day, reached, sector));
        // Support effect (Traced, 05 10.3, GNPRTB 5127 = -10, threshold 5128 = 101 always true): every populated system of the sector, any
        // holder, through the support module's one entry point with cause HqMove. "Not destroyed": no destroyed systems exist in the core yet.
        if (HqRules.ArrivalSupportDelta != 0 && _galaxy.TryFindSystem(reached, out var ri))
        {
            var sec = _galaxy.Sectors.FirstOrDefault(s => s.Name == sector);
            if (sec != null && !_galaxy.Systems[ri].IsOutsideSectors)
                foreach (var m in sec.Members) // index order = ordinal names
                {
                    string name = _galaxy.Systems[m].Name;
                    if (_eco.SlotsOf(name).Populated) _support.ChangeSupport(ctx, name, Side.Collective, HqRules.ArrivalSupportDelta, SupportCause.HqMove);
                }
        }
    }

    private void ClearTransit() { Hq.From = null; Hq.Path = new(); Hq.HopTicks = new(); Hq.HopIndex = 0; Hq.DueDay = 0; }

    private void DestroyHq(StepContext ctx, string cause, string system)
    {
        int day = ctx.Day + 1;
        Hq.Status = HqStatus.Destroyed; Hq.System = system; Hq.DestroyedCause = cause; Hq.DestroyedDay = day; ClearTransit();
        // Both sides learn it from HqDestroyed (§F7); there is no HQ left to be seen, so no picture may keep showing one.
        foreach (var side in Sides) foreach (var kv in F.Of(side).Systems) kv.Value.HqHere = false;
        ctx.Emit((i, _) => new HqDestroyed(i, day, cause, system));
    }

    // ================================================================== capture: LossNotice and the HQ capture rule

    // rule: §F2 LossNotice (Proposal: the loser is told, B only); §F7 Capture (HqLostOnHolderChange, DEFAULT AnyCause)
    private void OnHolderChanged(CaptureInfo info)
    {
        if (info.Old is Side loser) Write(info.Ctx, new Truth(this), loser, info.System, RevealKind.LossNotice, info.Ctx.Day + 1);
        if (HqAt(info.System) && info.New != Side.Collective) // the Hand took it, or it went neutral (B5a section 5: the HQ cannot serve side 3)
        {
            bool lost = HqRules.LostOnHolderChange switch
            {
                HqLostOnHolderChange.AnyCause => true,
                HqLostOnHolderChange.AssaultOnly => info.Cause == CaptureCause.Assault,
                _ => false,
            };
            // the cause names how the world was lost (QA-11): by support alone, by its last troops leaving, else captured (assault, debug, harvester revolt)
            string cause = info.Cause switch { CaptureCause.Support => CauseLostSupport, CaptureCause.TroopsLeft => CauseTroopsLeft, _ => CauseCaptured };
            if (lost) DestroyHq(info.Ctx, cause, info.System);
        }
    }

    // ================================================================== slot 3.1: sightings

    // rule: §F8 slot 3.1; §F3; §N3. A fleet "placed now" is one that was on a lane after step 2 and is at a system after step 3 (arrived,
    // or passing: it continues in step 4). Classic: passing fleets neither explore nor appear in the log.
    private void Sightings(StepContext ctx)
    {
        int stamp = ctx.Day + 1;
        bool passing = Params.PassingExplores;
        var truth = new Truth(this);
        var placed = new SortedDictionary<string, List<Fleet>>(StringComparer.Ordinal);
        foreach (var f in State.Fleets.Values) // id order
        {
            if (f.At == null || !_inLane.Contains(f.Id)) continue;
            if (f.Transit != null && !passing) continue;
            if (!placed.TryGetValue(f.At, out var l)) placed[f.At] = l = new List<Fleet>();
            l.Add(f);
        }
        foreach (var side in Sides)
            foreach (var (sys, here) in placed)
            {
                var know = F.Of(side);
                bool ownArrived = here.Any(f => f.Side == side);
                IEnumerable<Fleet> seen;
                if (ownArrived)
                {
                    Write(ctx, truth, side, sys, RevealKind.Presence, stamp);
                    seen = truth.FleetsAt(sys).Where(f => f.Side != side && (passing || f.Transit == null));
                }
                else if (HasLiveAccess(side, sys)) seen = here.Where(f => f.Side != side);
                else continue;
                foreach (var f in seen)
                {
                    var pic = truth.Picture(f);
                    know.Sightings.Add(new Sighting { Day = stamp, System = sys, Kind = SightingKind.Fleet, Side = f.Side, Detail = pic });
                    string detail = pic.ToString(); var fs = f.Side;
                    ctx.Emit((i, _) => new FleetSighted(i, stamp, side, sys, fs, detail));
                }
            }
        SensorReports(ctx, stamp);
    }

    // rule: m5-fog-and-hq §N4 (route-only, N-day warning; movement-redesign-draft §C), §F8 slot 3.1: Sensor Station warnings, after the
    // fleet sightings; sides Hand before Collective, then other-side fleets by id, then the station systems in path order.
    // A warning goes to the owner of a finished station at S for an other-side fleet in transit whose remaining path (the systems after
    // its current position, the destination included) contains S, when 0 < (day it reaches S) - today <= SensorWarningDays. One warning
    // per (fleet, station system, reach day): the side remembers the warnings it got until the reach day is over.
    private void SensorReports(StepContext ctx, int stamp)
    {
        if (!Params.SensorsWork) return;
        foreach (var owner in Sides)
        {
            var know = F.Of(owner);
            // rule: §N4 (warning memory): a remembered warning is dropped once its reach day has come (the fleet can no longer be ahead of it).
            var memory = know.SensorWarnings ?? new List<SensorWarning>();
            memory.RemoveAll(x => x.ReachDay <= stamp);
            var stations = SensorStations(owner);
            foreach (var f in State.Fleets.Values) // SortedDictionary: fleet id order
            {
                if (stations.Count == 0) break;
                if (f.Side == owner || f.Transit is not { } t) continue; // rule: §N4 (no report about a side's own fleets)
                // At slot 3.1 a fleet in flight is between Path[HopIndex] and Path[HopIndex + 1] and reaches the latter on DueDay; a fleet placed
                // this Step (passing, Transit still set) stands at Path[HopIndex + 1]. Either way the next system after Path[HopIndex + 1] is reached
                // HopTicks[HopIndex + 1] days after DueDay, and so on: the same sums the fleet module uses (steps 3 and 4).
                // rule: m1-clock-and-orders §Order of work steps 3 and 4.
                int first = f.At == null ? t.HopIndex + 1 : t.HopIndex + 2;
                int reach = t.DueDay;
                for (int j = t.HopIndex + 1; j < t.Path.Count; j++)
                {
                    if (j > t.HopIndex + 1) reach += t.HopTicks[j - 1];
                    if (j < first) continue;
                    string sys = t.Path[j];
                    if (!stations.Contains(sys)) continue;
                    int ahead = reach - stamp;
                    if (ahead <= 0 || ahead > Params.SensorWarningDays) continue; // rule: §N4 (SensorWarningDays, Decided 10)
                    // rule: §N4 (SensorSeesOffRouteArrivals, Proposal DEFAULT true): a trip that ENDS at S after an off-route jump into S.
                    // A fleet that passes S always uses S as a route stop and is seen.
                    bool last = j == t.Path.Count - 1;
                    if (last && !Params.SensorSeesOffRouteArrivals && _galaxy.FindLane(t.Path[j - 1], sys) == null) continue;
                    int fid = f.Id, rd = reach;
                    if (memory.Any(x => x.FleetId == fid && x.System == sys && x.ReachDay == rd)) continue; // one warning per trip
                    memory.Add(new SensorWarning { FleetId = fid, System = sys, ReachDay = rd });
                    // rule: §N4 size class: capital ships of THAT fleet; a squadron-only fleet counts 1.
                    int total = f.Ships.Count > 0 ? f.Ships.Count : f.Squadrons.Count > 0 ? 1 : 0;
                    if (total == 0) continue;
                    var size = total <= Params.SensorFewMax ? SensorSize.Few : total <= Params.SensorSquadronMax ? SensorSize.Squadron : SensorSize.Large;
                    var fleetSide = f.Side;
                    know.Sightings.Add(new Sighting { Day = stamp, System = sys, Kind = SightingKind.SensorArrival, Side = fleetSide, Size = size, ReachDay = rd });
                    ctx.Emit((i, _) => new SensorReport(i, stamp, owner, sys, fleetSide, size, rd));
                }
            }
            // rule: §N9: the saved memory is kept in one canonical order; empty = null (left out of the saved form).
            memory.Sort((a, b) => a.ReachDay != b.ReachDay ? a.ReachDay.CompareTo(b.ReachDay)
                : string.CompareOrdinal(a.System, b.System) != 0 ? string.CompareOrdinal(a.System, b.System) : a.FleetId.CompareTo(b.FleetId));
            know.SensorWarnings = memory.Count > 0 ? memory : null;
        }
    }

    /// <summary>
    /// The systems of the finished Sensor Stations the side owns, on systems not destroyed (blockade and uprising do not matter: sensors
    /// are passive). Lookup only, never iterated. rule: §N4.
    /// </summary>
    private HashSet<string> SensorStations(Side owner)
    {
        var stations = new HashSet<string>(StringComparer.Ordinal);
        string cls = FacilityKinds.ClassName(FacilityKind.SensorStation);
        foreach (var o in State.Economy.Objects.Values)
            if (o.Category == ObjectCategory.Facility && o.Completed && o.Class == cls && o.System != null
                && _eco.OwnerOf(o) == owner && !_eco.IsDestroyed(o.System))
                stations.Add(o.System);
        return stations;
    }

    // ================================================================== slot 5.4: live refresh

    // rule: §F8 slot 5.4; §F3 (live access); §F2 OwnLive, Presence, HeartlandReport, Engagement
    private void LiveRefresh(StepContext ctx)
    {
        int stamp = ctx.Day + 1;
        var truth = new Truth(this);
        // Engagement (Lead, sheet DEFAULT): a battle or an assault this Step writes B and E for both sides; after an assault also D.
        var ev = _game.Events;
        for (int i = _stepEventStart; i < ev.Count; i++)
        {
            string? at = ev[i] switch { BattleEnded be => be.Result.System, Assault.AssaultResolved ar => ar.System, _ => null };
            if (at == null) continue;
            bool assault = ev[i] is Assault.AssaultResolved;
            foreach (var side in Sides) Write(ctx, truth, side, at, RevealKind.Engagement, stamp, assault);
        }
        foreach (var side in Sides)
            foreach (var sys in _galaxy.Systems) // index order = ordinal names
            {
                string n = sys.Name;
                if (_fleets.OwnerOf(n) == side) { Write(ctx, truth, side, n, RevealKind.OwnLive, stamp); F.Of(side).Touch(n).LastLookDay = stamp; continue; }
                if (truth.HasFleet(side, n) || AgentHere(side, n)) { Write(ctx, truth, side, n, RevealKind.Presence, stamp); F.Of(side).Touch(n).LastLookDay = stamp; continue; }
                if (Params.HeartlandReportsInstantly && sys.Group == Params.HeartlandGroup) Write(ctx, truth, side, n, RevealKind.HeartlandReport, stamp);
            }
    }

    // §N3: entries older than ObservedWindowDays are dropped at slot 6 (age above the window).
    private void PruneLogs(StepContext ctx)
    {
        int day = ctx.Day + 1;
        foreach (var side in Sides) F.Of(side).Sightings.RemoveAll(s => day - s.Day > Params.ObservedWindowDays);
    }

    // ================================================================== queries (read API)

    private bool AgentHere(Side side, string sys) => Params.AgentPresenceCounts && AgentsAt != null && AgentsAt(side, sys);

    /// <summary>Live access of kinds 1 to 3 now: the side holds the system, has a fleet placed there, or an agent on a mission there. rule: §F3.</summary>
    public bool HasLiveAccess(Side side, string system) =>
        _fleets.OwnerOf(system) == side || State.Fleets.Values.Any(f => f.Side == side && f.At == system) || AgentHere(side, system);

    /// <summary>Does the side see the system live today (the UI's "lit" systems)? Same as <see cref="HasLiveAccess"/>.</summary>
    public bool Sees(Side side, string system) => HasLiveAccess(side, system);

    /// <summary>Is the system explored in the side's picture?</summary>
    public bool IsExplored(Side side, string system) => F.Of(side).Systems.TryGetValue(system, out var k) && k.Explored;

    /// <summary>The knowledge level today. Classic: Unknown, Charted or Held only. rule: §N1.</summary>
    public KnowledgeLevel Level(Side side, string system)
    {
        if (!F.Of(side).Systems.TryGetValue(system, out var k) || !k.Explored) return KnowledgeLevel.Unknown;
        if (HasLiveAccess(side, system)) return KnowledgeLevel.Held;
        if (Params.Ruleset == FogRuleset.New && k.LastLookDay is int l && _game.Day - l <= Params.ObservedWindowDays) return KnowledgeLevel.Observed;
        return KnowledgeLevel.Charted;
    }

    /// <summary>The side's picture of a system with ages (null when unexplored: an unexplored picture shows nothing). rule: §N2, §N8 Systems.</summary>
    public SystemPicture? Picture(Side side, string system) =>
        F.Of(side).Systems.TryGetValue(system, out var k) && k.Explored ? new SystemPicture(system, k, _game.Day, Params.Ruleset == FogRuleset.New) : null;

    /// <summary>
    /// Every character in the group D of the side's explored pictures, with the D stamp: systems in ordinal order, then id order. Stale entries
    /// stay (the picture keeps the last list). rule: m5-fog-and-hq §F1 group D, §N8 Systems.
    /// </summary>
    public IReadOnlyList<(string System, CharacterPicture Character, int Stamp)> CharactersSeen(Side side)
    {
        var list = new List<(string, CharacterPicture, int)>();
        foreach (var kv in F.Of(side).Systems)
            if (kv.Value.Explored && kv.Value.StampD is int st && kv.Value.Characters is { } cs)
                foreach (var c in cs) list.Add((kv.Key, c, st));
        return list;
    }

    /// <summary>
    /// The other side's fleets the side sees now: group E of every system it has live access to (class counts, no fleet ids), systems in
    /// ordinal order. rule: §F3, §N8 (no enemy fleet ids).
    /// </summary>
    public IReadOnlyList<(string System, FleetPicture Fleet)> VisibleFleets(Side side)
    {
        var list = new List<(string, FleetPicture)>();
        foreach (var kv in F.Of(side).Systems)
            if (kv.Value.Explored && kv.Value.Fleets.Count > 0 && HasLiveAccess(side, kv.Key))
                foreach (var fp in kv.Value.Fleets) list.Add((kv.Key, fp));
        return list;
    }

    /// <summary>
    /// The side's belief about the HQ: for the Collective the truth (its own object; null in transit or destroyed); for the Hand the system its
    /// picture shows HqHere at, with the stamp of that group C write, possibly out of date. rule: §N5 KnownHq.
    /// </summary>
    public KnownHqInfo? KnownHq(Side side)
    {
        if (side == Side.Collective) return Hq.Status == HqStatus.AtRest ? new KnownHqInfo(Hq.System!, _game.Day) : null;
        foreach (var kv in F.Of(side).Systems)
            if (kv.Value.Explored && kv.Value.HqHere) return new KnownHqInfo(kv.Key, kv.Value.HqStamp ?? 0);
        return null;
    }

    /// <summary>
    /// The stamp of the side's last group D (ground) write at a system, explored or not; null when never written. Stamps only grow (every write
    /// is stamped with the day it is made). Read by the AI memory's save (QA-1) to know which old bombardments a look-back can still reach.
    /// </summary>
    public int? GroundStamp(Side side, string system) => F.Of(side).Systems.TryGetValue(system, out var k) ? k.StampD : null;

    /// <summary>The side's sighting log from a day on. rule: §N3.</summary>
    public IReadOnlyList<Sighting> Sightings(Side side, int sinceDay = 0) => F.Of(side).Sightings.Where(s => s.Day >= sinceDay).ToList();

    /// <summary>HQ sector clues of the side (empty unless SectorSuspicion is on). rule: §N6.</summary>
    public IReadOnlyList<HqSectorClue> Clues(Side side) => F.Of(side).Clues;

    /// <summary>The one view the AI may read for a side. rule: §N8.</summary>
    public KnowledgeView ViewFor(Side side) => new(this, side, _game);

    /// <summary>
    /// The events the side may see, in emission order (the filtered feed of §N8; the unfiltered <see cref="Game.Events"/> stays for tests,
    /// tools and the debug view).
    /// </summary>
    public IReadOnlyList<GameEvent> EventsFor(Side side)
    {
        var ev = _game.Events;
        for (; _eventsScanned < ev.Count; _eventsScanned++)
        {
            int mask = _game.AudienceOf(_eventsScanned);
            foreach (var s in Sides) if ((mask & Victory.ScenarioState.Bit(s)) != 0) _eventsFor[(int)s].Add(ev[_eventsScanned]);
        }
        return _eventsFor[(int)side];
    }

    /// <summary>A hash of the side's knowledge (pictures, logs, clues) and the day: equal hashes = the side knows the same. rule: §N9, tests FN1, FN2.</summary>
    public string KnowledgeHash(Side side)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Day = _game.Day, Knowledge = F.Of(side), Hq = KnownHq(side) });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    // ================================================================== event audience

    // rule: §N8 Event audience. DEFAULT: the side of the event's subject, plus the other side if it has live access (kinds 1 to 3, not the
    // Heartland report) to the event's system at that moment. Exceptions: HqDeparted, HqArrived and an HqMove support change go to the Collective
    // only (§N7 1: the Hand must never be told "the HQ arrived"); HqDestroyed, GameWon and DominanceChanged go to both; SystemCaptured to winner, loser and any
    // side with live access. The fog's own per-viewer events go to their viewer. The subject and the system are read from the event's
    // fields named Side, Holder, Owner or Fleet (the fleet's side) and System; an event with neither goes to both (Contradictions 11: the
    // other sheets' events have no audience entry yet).
    private int Audience(GameEvent e, Order? order)
    {
        const int Emp = 1, All = 2, Both = 3;
        int Bit(Side s) => s == Side.Hand ? Emp : All;
        switch (e)
        {
            case HqDeparted or HqArrived: return All;
            case SupportChanged sc when sc.Cause == SupportCause.HqMove: return All;
            case HqDestroyed or Victory.GameWon or Victory.DominanceChanged: return Both;
            case Harvester.IHarvesterEvent: return Both; // galaxy notices: "New harvester Deployed", "Planet Destroyed", ... (m5-harvester §1, §2 effect 6)
            case SystemExplored x: return Bit(x.Viewer);
            case OwnerChangeSeen x: return Bit(x.Viewer);
            case FleetSighted x: return Bit(x.Viewer);
            case SensorReport x: return Bit(x.Viewer); // rule: §N4 S2 (for the station's owner only)
            case HqSectorClueGiven x: return Bit(x.Viewer);
            case AgentDetected x: return Bit(x.Viewer);
            case OrderAccepted or OrderRejected: return order != null ? Bit(order.Side) : Both;
            case SystemCaptured c: return (c.From is Side f ? Bit(f) : 0) | (c.To is Side t ? Bit(t) : 0) | LiveMask(c.System);
            case BattleEnded: return Both; // a battle needs both sides there: both took part
        }
        var acc = Accessors.Get(e.GetType());
        int mask = 0;
        if (acc.Side?.GetValue(e) is Side s) mask |= Bit(s);
        else if (acc.Fleet?.GetValue(e) is int fid && State.Fleets.TryGetValue(fid, out var fl)) mask |= Bit(fl.Side);
        else if (order != null) mask |= Bit(order.Side);
        string? sys = acc.System?.GetValue(e) as string;
        if (sys != null) mask |= LiveMask(sys);
        return mask == 0 ? Both : mask;
    }

    private int LiveMask(string system)
    {
        int m = 0;
        if (HasLiveAccess(Side.Hand, system)) m |= 1;
        if (HasLiveAccess(Side.Collective, system)) m |= 2;
        return m;
    }

    private sealed record EventAccessors(PropertyInfo? Side, PropertyInfo? Fleet, PropertyInfo? System);

    private static class Accessors
    {
        private static readonly Dictionary<Type, EventAccessors> Cache = new(); // lookup only, never iterated
        private static readonly object Lock = new();
        public static EventAccessors Get(Type t)
        {
            lock (Lock)
            {
                if (Cache.TryGetValue(t, out var a)) return a;
                PropertyInfo? P(string name, params Type[] types) { var p = t.GetProperty(name); return p != null && types.Contains(p.PropertyType) ? p : null; }
                var side = P("Side", typeof(Side), typeof(Side?)) ?? P("Holder", typeof(Side), typeof(Side?)) ?? P("Owner", typeof(Side), typeof(Side?));
                a = new EventAccessors(side, P("Fleet", typeof(int)), P("System", typeof(string)));
                Cache[t] = a;
                return a;
            }
        }
    }

    // ================================================================== truth index (built per call; lookups only)

    /// <summary>One pass over fleets and objects, indexed by system, for the writers. Dictionaries are lookups only, never iterated.</summary>
    private sealed class Truth
    {
        private readonly FogModule _m;
        private Dictionary<string, List<Fleet>>? _fleets;
        private Dictionary<string, List<EconObject>>? _objects;
        private Dictionary<int, List<EconObject>>? _aboard;
        public Truth(FogModule m) { _m = m; }

        public IReadOnlyList<Fleet> FleetsAt(string sys)
        {
            if (_fleets == null)
            {
                _fleets = new Dictionary<string, List<Fleet>>(StringComparer.Ordinal);
                foreach (var f in _m.State.Fleets.Values) // id order
                    if (f.At != null) { if (!_fleets.TryGetValue(f.At, out var l)) _fleets[f.At] = l = new(); l.Add(f); }
            }
            return _fleets.TryGetValue(sys, out var r) ? r : (IReadOnlyList<Fleet>)Array.Empty<Fleet>();
        }

        public bool HasFleet(Side side, string sys) => FleetsAt(sys).Any(f => f.Side == side);

        public IReadOnlyList<EconObject> Objects(string sys)
        {
            if (_objects == null)
            {
                _objects = new Dictionary<string, List<EconObject>>(StringComparer.Ordinal);
                foreach (var o in _m.State.Economy.Objects.Values) // id order
                    if (o.System != null && o.AboardShip == 0) { if (!_objects.TryGetValue(o.System, out var l)) _objects[o.System] = l = new(); l.Add(o); }
            }
            return _objects.TryGetValue(sys, out var r) ? r : (IReadOnlyList<EconObject>)Array.Empty<EconObject>();
        }

        private Dictionary<string, List<CharacterPicture>>? _chars;

        /// <summary>
        /// The characters on a system for group D, id order: free or injured ones standing there (not riding a fleet, not travelling, not on a
        /// mission: a mission team is hidden, m5-missions detection), and prisoners held on the planet (not aboard a ship). Dead ones are not
        /// present. rule: m5-fog-and-hq §F1 group D.
        /// </summary>
        public IReadOnlyList<CharacterPicture> CharactersAt(string sys)
        {
            if (_chars == null)
            {
                _chars = new Dictionary<string, List<CharacterPicture>>(StringComparer.Ordinal);
                var onMission = new HashSet<int>(); // membership test only, never iterated
                foreach (var m in _m.State.Missions.Missions.Values)
                {
                    foreach (var id in m.AllCharacters) onMission.Add(id);
                    if (m.Captives != null) foreach (var id in m.Captives) onMission.Add(id);
                }
                foreach (var c in _m.State.Characters.Characters.Values) // id order
                {
                    if (!c.Placed) continue;
                    string? at = c.Status switch
                    {
                        Praedra.Core.Characters.CharacterStatus.Captured => c.HeldAboard == 0 ? c.HeldAt : null,
                        Praedra.Core.Characters.CharacterStatus.Free or Praedra.Core.Characters.CharacterStatus.Injured
                            => c.AttachedFleet == 0 && c.Transit == null && !onMission.Contains(c.Id) ? c.System : null,
                        _ => null,
                    };
                    if (at == null) continue;
                    if (!_chars.TryGetValue(at, out var l)) _chars[at] = l = new();
                    l.Add(new CharacterPicture { Id = c.Id, Side = c.Side, Status = c.Status });
                }
            }
            return _chars.TryGetValue(sys, out var r) ? r : (IReadOnlyList<CharacterPicture>)Array.Empty<CharacterPicture>();
        }

        public FleetPicture Picture(Fleet f)
        {
            if (_aboard == null)
            {
                _aboard = new Dictionary<int, List<EconObject>>();
                foreach (var o in _m.State.Economy.Objects.Values)
                    if (o.Category == ObjectCategory.Fighter && o.AboardShip != 0) { if (!_aboard.TryGetValue(o.AboardShip, out var l)) _aboard[o.AboardShip] = l = new(); l.Add(o); }
            }
            var objs = _m.State.Economy.Objects;
            var p = new FleetPicture { Side = f.Side };
            foreach (var id in f.Ships)
            {
                if (objs.TryGetValue(id, out var o)) p.Ships[o.Class] = p.Ships.GetValueOrDefault(o.Class) + 1;
                if (_aboard.TryGetValue(id, out var sq)) foreach (var s in sq) p.Squadrons[s.Class] = p.Squadrons.GetValueOrDefault(s.Class) + 1;
            }
            foreach (var id in f.Squadrons) if (objs.TryGetValue(id, out var o)) p.Squadrons[o.Class] = p.Squadrons.GetValueOrDefault(o.Class) + 1;
            return p;
        }
    }
}
