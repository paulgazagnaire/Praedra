using Rebellion.Core.Control;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;

namespace Rebellion.Core.Clock;

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
    /// <summary>Uprising flags, support timers and the Death Star sector flag. rule: m2-popular-support, m2-uprisings-and-garrison.</summary>
    public SupportState Support { get; init; } = new();

    /// <summary>Control settings stored per game (scorched earth). rule: m2-control-and-capture §Scorched earth.</summary>
    public ControlState Control { get; init; } = new();

    /// <summary>OnContact settings and the battle-day list. rule: m4-battle-classic §1.3; m4-retreat-damage-repair §5.</summary>
    public Rebellion.Core.Battle.BattleState Battle { get; init; } = new();

    /// <summary>HQ system, explored flags, victory result. rule: setup-victory/04, 09.</summary>
    public Rebellion.Core.Victory.ScenarioState Scenario { get; init; } = new();

    /// <summary>Each side's picture of the galaxy, sighting logs and clues. rule: m5-fog-and-hq §F1, §N9.</summary>
    public Rebellion.Core.Fog.FogState Fog { get; init; } = new();

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
    public void Emit(Func<int, int, GameEvent> make) => _game.EmitEvent(make);
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
        "5.4", // Live refresh of each side's picture (M5 fog; 5.3 is mission resolution, m5-missions). rule: m5-fog-and-hq §F8, Lead notes
        "6.1", "6.2", "6.3", "6.4", "6.5", "6.6", "6.7", "6.8", // Day-end hooks (empty in M1)
        "6.11", // Victory check: MUST stay the last day-end slot (research 6.9 and characters 6.10 will be added before it). rule: m5-setup-and-victory §Game over
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
    /// Decides who may see an event, when it is raised: bit 1 = Empire, bit 2 = Alliance (as Victory.ScenarioState.Bit). Given the
    /// event and the order being processed in step 1 (null outside it). Null = everyone sees everything. The fog module sets it.
    /// rule: m5-fog-and-hq §N8 (event audience: "every event gets an audience when it is raised").
    /// </summary>
    public Func<GameEvent, Order?, int>? AudienceResolver { get; set; }

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

    /// <summary>Advance exactly one day. rule: §The clock</summary>
    public void Step()
    {
        if (IsOver) return; // rule: m5-setup-and-victory §Game over: no slot runs, Day does not change, no event
        var ctx = new StepContext(this, Day);
        foreach (var slot in WorkList)
        {
            if (slot == "1") ProcessOrders(ctx);
            else if (slot == "7") Day = ctx.Day + 1;
            else if (_hooks.TryGetValue(slot, out var list)) foreach (var h in list) h(ctx);
        }
    }

    // rule: m1-clock-and-orders §Order of work, step 1 (sorted by sequence; a rejection never changes state)
    private void ProcessOrders(StepContext ctx)
    {
        var batch = _queue.OrderBy(o => o.Sequence).ToList();
        _queue.Clear();
        _markersChanged.Clear();
        foreach (var order in batch)
        {
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
    internal void EmitEvent(Func<int, int, GameEvent> make)
    {
        var e = make(_events.Count, Day);
        _events.Add(e);
        _audience.Add(AudienceResolver?.Invoke(e, _currentOrder) ?? 3);
    }
}
