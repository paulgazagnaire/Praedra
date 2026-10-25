using Praedra.Core.Control;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Support;

namespace Praedra.Core.Clock;

/// <summary>
/// Game state. Everything is a sorted collection (ordinal for names, ascending for ids) so the saved and hashed form is
/// canonical. rule: m1-clock-and-orders §Seeded random numbers rule 10.
/// </summary>
public sealed class GameState
{
    public SortedDictionary<string, int> Markers { get; init; } = new(StringComparer.Ordinal);

    /// <summary>All fleets by id (ids come from the economy's shared object counter, <see cref="EconomyState.NextObjectId"/>). rule: m3-fleets §What a fleet is; positions and in-transit state live on the fleet.</summary>
    public SortedDictionary<int, Fleet> Fleets { get; init; } = new();
    /// <summary>
    /// Owner of each system that has one; a system not listed is neutral (DEFAULT). M1 has no ownership data yet, so
    /// setup and tests set it. rule: m1-forced-stop-and-blockade §Rule (Classic) (system owner).
    /// </summary>
    public SortedDictionary<string, Side> SystemOwners { get; init; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The Classic blockade flag per system: present = on, value = the side that blockades. Derived from fleets and
    /// owners, but kept so step 5 can raise BlockadeStarted and BlockadeLifted when it changes.
    /// </summary>
    public SortedDictionary<string, Side> Blockades { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Economy state: objects, slot changes, support, pools, timers. rule: m2-slots-and-resources, m2-mines-refineries-pools,
    /// m2-maintenance. Saved and hashed like everything else.
    /// </summary>
    public EconomyState Economy { get; init; } = new();

    /// <summary>Construction state: projects, deliveries, refined line, research grants. rule: m2-construction.</summary>
    public ConstructionState Construction { get; init; } = new();
    /// <summary>Uprising flags, support timers and the harvester sector flag. rule: m2-popular-support, m2-uprisings-and-garrison.</summary>
    public SupportState Support { get; init; } = new();

    /// <summary>Control settings stored per game (scorched earth). rule: m2-control-and-capture §Scorched earth.</summary>
    public ControlState Control { get; init; } = new();

    /// <summary>OnContact settings and the battle-day list. rule: m4-battle-classic §1.3; m4-retreat-damage-repair §5.</summary>
    public Praedra.Core.Battle.BattleState Battle { get; init; } = new();

    /// <summary>HQ system, explored flags, victory result. rule: setup-victory/04, 09.</summary>
    public Praedra.Core.Victory.ScenarioState Scenario { get; init; } = new();

    /// <summary>Each side's picture of the galaxy, sighting logs and clues. rule: m5-fog-and-hq §F1, §N9.</summary>
    public Praedra.Core.Fog.FogState Fog { get; init; } = new();
    /// <summary>Research points, levels, exhausted flags and tick timers per side; the two percents. rule: m5-research §State per side.</summary>
    public Praedra.Core.Research.ResearchState Research { get; init; } = new();
    /// <summary>Every character (stats, Force, status, post, place, trip) and the system General table. rule: m5-characters-and-force §The character object.</summary>
    public Praedra.Core.Characters.CharacterState Characters { get; init; } = new();
    /// <summary>Every running mission group (team, decoys, trip, timer). rule: m5-missions §Orders, §Rule (Classic).</summary>
    public Praedra.Core.Missions.MissionState Missions { get; init; } = new();
    /// <summary>Destroyed systems and the strikes per sector. rule: m5-death-star §Interfaces.</summary>
    public Praedra.Core.Harvester.HarvesterState Harvester { get; init; } = new();
    /// <summary>Per-system loss tallies of the loyalty triggers (B2c). rule: m5-characters-and-force §Loyalty and betrayal.</summary>
    public Praedra.Core.Characters.LoyaltyState Loyalty { get; init; } = new();

    /// <summary>Copy another state's content into this one (used by Load, which needs ordinal-comparer dictionaries).</summary>
    internal void CopyFrom(GameState other)
    {
        foreach (var kv in other.Markers) Markers[kv.Key] = kv.Value;
        foreach (var kv in other.Fleets) Fleets[kv.Key] = kv.Value;
        foreach (var kv in other.SystemOwners) SystemOwners[kv.Key] = kv.Value;
        foreach (var kv in other.Blockades) Blockades[kv.Key] = kv.Value;
        Economy.CopyFrom(other.Economy);
        Construction.CopyFrom(other.Construction);
        Support.CopyFrom(other.Support);
        Control.CopyFrom(other.Control);
        Battle.CopyFrom(other.Battle);
        Scenario.CopyFrom(other.Scenario);
        Fog.CopyFrom(other.Fog);
        Research.CopyFrom(other.Research);
        Characters.CopyFrom(other.Characters);
        Missions.CopyFrom(other.Missions);
        Harvester.CopyFrom(other.Harvester);
        if (other.Loyalty != null) Loyalty.CopyFrom(other.Loyalty);
    }
}

/// <summary>Outcome of validating one order in step 1: a rejection reason, or the event to raise on acceptance.</summary>
public sealed record OrderResult(string? Reason, Func<int, int, GameEvent>? Accepted)
{
    public static OrderResult Reject(string reason) => new(reason, null);
    public static OrderResult Accept(Func<int, int, GameEvent> accepted) => new(null, accepted);
}

/// <summary>What a hook may touch during a Step. Only Step() hands this out.</summary>
public sealed class StepContext
{
    private readonly Game _game;
    internal StepContext(Game g, int day) { _game = g; Day = day; }
    /// <summary>K: the value of Day at the start of the Step.</summary>
    public int Day { get; }
    public GameState State => _game.State;
    public GameRng Rng => _game.Rng;
    /// <summary>Append an event; the factory receives (index, day) where day is K.</summary>
    public void Emit(Func<int, int, GameEvent> make) => _game.EmitEvent(make, this);
}

/// <summary>
/// The game loop. One Step() is one game day; only Step() mutates state.
/// rule: m1-clock-and-orders §The clock, §Order of work inside one Step
/// </summary>
public sealed class Game
{
    /// <summary>The numbered work list, in execution order. Later packages register hooks on these ids.</summary>
    public static readonly IReadOnlyList<string> WorkList = new[]
    {
        "1",   // Orders
        "2",   // Departures
        "3",   // Arrivals
        "3.1", // Sightings (M5 fog). rule: m5-fog-and-hq §F8
        "4",   // Forced stops
        "5",   // Contacts and blockade flags
        "5.1", // Bombardments (M3)
        "5.2", // Assaults (M3)
        "5.3", // Mission resolution (M5). rule: m5-missions §Where it runs in the day Step (Lead notes: after assault 5.2, before the fog refresh 5.4)
        "5.4", // Live refresh of each side's picture (M5 fog; 5.3 is mission resolution, m5-missions). rule: m5-fog-and-hq §F8, Lead notes
        "5.6", // harvester sector flag, once per day after battles and missions, before support at 6.1. rule: m5-death-star §3
        "6.1", "6.2", "6.3", "6.4", "6.5", "6.6", "6.7", "6.8", // Day-end hooks (empty in M1)
        "6.9", // Research tick, after strategic repair (6.8). rule: m5-research §Where it runs in the day Step
        "6.10", // Character day-end: injuries heal, escape rolls. rule: m5-characters-and-force §Where it runs in the day Step
        "6.11", // Victory check: MUST stay the last day-end slot. rule: m5-setup-and-victory §Game over
        "7",   // Day becomes K + 1
    };

    private readonly Dictionary<string, List<Action<StepContext>>> _hooks = new();
    // Looked up by order type, never iterated.
    private readonly Dictionary<Type, Func<StepContext, Order, OrderResult>> _handlers = new();
    private readonly HashSet<string> _markersChanged = new(StringComparer.Ordinal); // membership test only, never iterated
    private readonly List<Order> _queue = new();
    private readonly List<GameEvent> _events = new();
    private long _nextSequence;
    private readonly List<int> _audience = new();
    private Order? _currentOrder;

    public Game(ulong seed) : this(new GameState(), 0, new GameRng(seed), Array.Empty<Order>(), 1) { }

    /// <summary>Start a game on a given generator (tests pass a stub that returns queued draws; the game never makes its own).</summary>
    public Game(GameRng rng) : this(new GameState(), 0, rng, Array.Empty<Order>(), 1) { }

    internal Game(GameState state, int day, GameRng rng, IEnumerable<Order> queue, long nextSequence)
    {
        State = state; Day = day; Rng = rng; _queue.AddRange(queue); _nextSequence = nextSequence;
        RegisterOrderHandler<SetMarkerOrder>(HandleSetMarker);
    }

    public GameState State { get; }
    /// <summary>A winner is set: Step does nothing and orders are rejected GameOver.</summary>
    public bool IsOver => State.Scenario.Winner != null;
    /// <summary>Starts at 0 (DEFAULT). rule: m1-clock-and-orders §The clock</summary>
    public int Day { get; private set; }
    public GameRng Rng { get; }
    public IReadOnlyList<Order> Queue => _queue;
    public long NextSequence => _nextSequence;
    /// <summary>Append-only. Not part of a save.</summary>
    public IReadOnlyList<GameEvent> Events => _events;

    /// <summary>
    /// Decides who may see an event, when it is raised: bit 1 = Hand, bit 2 = Collective (as Victory.ScenarioState.Bit). Given the
    /// event and the order being processed in step 1 (null outside it). Null = everyone sees everything. The fog module sets it.
    /// rule: m5-fog-and-hq §N8 (event audience: "every event gets an audience when it is raised").
    /// </summary>
    public Func<GameEvent, Order?, int>? AudienceResolver { get; set; }

    private readonly List<Action<StepContext, GameEvent>> _observers = new(); // called in registration order (attach order: deterministic)

    /// <summary>
    /// Register a rule that reacts to an event at the moment it is emitted inside a Step (same Step, same place in the draw order). Events
    /// emitted outside a Step (setup) reach no observer. The observer may change state, draw and emit; its own events are observed too.
    /// rule: m5-characters-and-force §Loyalty (B2c: Classic's rule events 0x31b, 0x300, 0x305 and the battle end call their handlers at once).
    /// </summary>
    public void ObserveEvents(Action<StepContext, GameEvent> observer) => _observers.Add(observer);

    /// <summary>The audience mask of event <paramref name="index"/> (3 = both sides). Not part of a save, like the events.</summary>
    public int AudienceOf(int index) => _audience[index];

    /// <summary>Add a hook to a work-list slot (steps 2-5.2 and 6.x). Hooks of one slot run in registration order.</summary>
    public void RegisterHook(string slot, Action<StepContext> hook)
    {
        if (!WorkList.Contains(slot) || slot is "1" or "7") throw new ArgumentException($"No hook slot '{slot}'");
        if (!_hooks.TryGetValue(slot, out var list)) _hooks[slot] = list = new();
        list.Add(hook);
    }

    /// <summary>
    /// Register the step-1 validator and handler for one order type. The handler checks the order against the state at
    /// that moment and either rejects it (changing nothing) or applies it and returns the event to raise.
    /// rule: m1-clock-and-orders §Order of work, step 1; §Orders in (rejection never changes any state).
    /// </summary>
    public void RegisterOrderHandler<TOrder>(Func<StepContext, TOrder, OrderResult> handler) where TOrder : Order
    {
        if (!_handlers.TryAdd(typeof(TOrder), (ctx, o) => handler(ctx, (TOrder)o)))
            throw new ArgumentException($"A handler for {typeof(TOrder).Name} is already registered");
    }

    /// <summary>Queue an order; the core numbers it. It is validated later, in step 1. rule: §Orders in</summary>
    public long Submit(Func<long, Order> make)
    {
        if (IsOver) // rule: m5-setup-and-victory §Game over: every order is rejected GameOver
        {
            long rejected = _nextSequence++;
            var order = make(rejected);
            _currentOrder = order;
            EmitEvent((i, d) => new OrderRejected(i, d, order.Sequence, RejectReason.GameOver));
            _currentOrder = null;
            return rejected;
        }
        long seq = _nextSequence++;
        _queue.Add(make(seq));
        return seq;
    }

    public long Submit(Side side, string marker, int value) => Submit(seq => new SetMarkerOrder(seq, side, marker, value));

    private readonly List<Action<StepContext>> _stepBegin = new(); // registration order
    private readonly List<Action<StepContext>> _settle = new();    // registration order

    /// <summary>
    /// Register a pair of callbacks for work that the original queues as a rule event and runs later the same day: <paramref name="begin"/> runs at
    /// the start of every Step (before the orders), <paramref name="settle"/> after the orders of step 1 and after every slot that has hooks.
    /// The control module uses it to see changes in the regiments standing on each system. rule: m2-control-and-capture §When the rule runs
    /// (B5a section 2: the regiment hook's recompute is queued, same day).
    /// </summary>
    public void RegisterSettle(Action<StepContext> begin, Action<StepContext> settle)
    {
        _stepBegin.Add(begin);
        _settle.Add(settle);
    }

    private readonly List<Action<StepContext>> _tickStart = new(); // registration order

    /// <summary>
    /// Register a callback for the start of each rule "tick" of the classic: before each player order is resolved, and once at the start of the
    /// day's rule pass (after the orders of step 1 and their settle). The control module resets its per-tick limiter there. Nothing is saved: a save
    /// is made between Steps, and every Step starts a fresh tick. rule: m2-control-and-capture §After the flip (B5a section 6, the limiter
    /// <c>0x518040</c>, RE lead correction 2026-10-17: per tick, not per day).
    /// </summary>
    public void RegisterTickStart(Action<StepContext> start) => _tickStart.Add(start);

    private void TickStart(StepContext ctx)
    {
        foreach (var t in _tickStart) t(ctx);
    }

    /// <summary>Advance exactly one day. rule: §The clock</summary>
    public void Step()
    {
        if (IsOver) return; // rule: m5-setup-and-victory §Game over: no slot runs, Day does not change, no event
        var ctx = new StepContext(this, Day);
        foreach (var b in _stepBegin) b(ctx);
        foreach (var slot in WorkList)
        {
            if (slot == "1") { ProcessOrders(ctx); Settle(ctx); TickStart(ctx); }
            else if (slot == "7") Day = ctx.Day + 1;
            else if (_hooks.TryGetValue(slot, out var list)) { foreach (var h in list) h(ctx); Settle(ctx); }
        }
    }

    private void Settle(StepContext ctx)
    {
        foreach (var s in _settle) s(ctx);
    }

    // rule: m1-clock-and-orders §Order of work, step 1 (sorted by sequence; a rejection never changes state)
    private void ProcessOrders(StepContext ctx)
    {
        var batch = _queue.OrderBy(o => o.Sequence).ToList();
        _queue.Clear();
        _markersChanged.Clear();
        foreach (var order in batch)
        {
            TickStart(ctx);
            _currentOrder = order;
            var result = _handlers.TryGetValue(order.GetType(), out var handler)
                ? handler(ctx, order)
                : OrderResult.Reject(RejectReason.UnknownOrderType);
            if (result.Reason != null)
            {
                string reason = result.Reason;
                EmitEvent((i, d) => new OrderRejected(i, d, order.Sequence, reason));
                continue;
            }
            EmitEvent(result.Accepted!);
        }
        _currentOrder = null;
    }

    private OrderResult HandleSetMarker(StepContext ctx, SetMarkerOrder m)
    {
        if (m.Value < 0) return OrderResult.Reject(RejectReason.NegativeValue);
        if (_markersChanged.Contains(m.Marker)) return OrderResult.Reject(RejectReason.AlreadyChanged);
        State.Markers[m.Marker] = m.Value;
        _markersChanged.Add(m.Marker);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, m.Sequence));
    }

    // Events carry Day = K, the day at the start of the Step, because Day only changes in step 7.
    internal void EmitEvent(Func<int, int, GameEvent> make, StepContext? ctx = null)
    {
        var e = make(_events.Count, Day);
        _events.Add(e);
        _audience.Add(AudienceResolver?.Invoke(e, _currentOrder) ?? 3);
        if (ctx != null) foreach (var o in _observers) o(ctx, e);
    }
}
