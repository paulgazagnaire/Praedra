namespace Rebellion.Core.Clock;

/// <summary>Game state. Placeholder content until fleets and planets arrive; collections are sorted for canonical output.</summary>
public sealed class GameState
{
    public SortedDictionary<string, int> Markers { get; init; } = new(StringComparer.Ordinal);
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
    /// <summary>Append an event; the factory receives (index, day).</summary>
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
        "4",   // Forced stops
        "5",   // Contacts and blockade flags
        "5.1", // Bombardments (M3)
        "5.2", // Assaults (M3)
        "6.1", "6.2", "6.3", "6.4", "6.5", "6.6", "6.7", "6.8", // Day-end hooks (empty in M1)
        "7",   // Day becomes K + 1
    };

    private readonly Dictionary<string, List<Action<StepContext>>> _hooks = new();
    private readonly List<Order> _queue = new();
    private readonly List<GameEvent> _events = new();
    private long _nextSequence;

    public Game(ulong seed) : this(new GameState(), 0, new GameRng(seed), Array.Empty<Order>(), 1) { }

    internal Game(GameState state, int day, GameRng rng, IEnumerable<Order> queue, long nextSequence)
    {
        State = state; Day = day; Rng = rng; _queue.AddRange(queue); _nextSequence = nextSequence;
    }

    public GameState State { get; }
    /// <summary>Starts at 0 (DEFAULT). rule: m1-clock-and-orders §The clock</summary>
    public int Day { get; private set; }
    public GameRng Rng { get; }
    public IReadOnlyList<Order> Queue => _queue;
    public long NextSequence => _nextSequence;
    /// <summary>Append-only. Not part of a save.</summary>
    public IReadOnlyList<GameEvent> Events => _events;

    /// <summary>Add a hook to a work-list slot (steps 2-5.2 and 6.x). Hooks of one slot run in registration order.</summary>
    public void RegisterHook(string slot, Action<StepContext> hook)
    {
        if (!WorkList.Contains(slot) || slot is "1" or "7") throw new ArgumentException($"No hook slot '{slot}'");
        if (!_hooks.TryGetValue(slot, out var list)) _hooks[slot] = list = new();
        list.Add(hook);
    }

    /// <summary>Queue an order; the core numbers it. It is validated later, in step 1. rule: §Orders in</summary>
    public long Submit(Func<long, Order> make)
    {
        long seq = _nextSequence++;
        _queue.Add(make(seq));
        return seq;
    }

    public long Submit(Side side, string marker, int value) => Submit(seq => new SetMarkerOrder(seq, side, marker, value));

    /// <summary>Advance exactly one day. rule: §The clock</summary>
    public void Step()
    {
        var ctx = new StepContext(this, Day);
        foreach (var slot in WorkList)
        {
            if (slot == "1") ProcessOrders();
            else if (slot == "7") Day = ctx.Day + 1;
            else if (_hooks.TryGetValue(slot, out var list)) foreach (var h in list) h(ctx);
        }
    }

    // rule: m1-clock-and-orders §Order of work, step 1 (sorted by sequence; a rejection never changes state)
    private void ProcessOrders()
    {
        var batch = _queue.OrderBy(o => o.Sequence).ToList();
        _queue.Clear();
        var changed = new HashSet<string>(StringComparer.Ordinal); // membership test only, never iterated
        foreach (var order in batch)
        {
            string? reason = order switch
            {
                SetMarkerOrder { Value: < 0 } => RejectReason.NegativeValue,
                SetMarkerOrder m when changed.Contains(m.Marker) => RejectReason.AlreadyChanged,
                _ => null,
            };
            if (reason != null)
            {
                EmitEvent((i, d) => new OrderRejected(i, d, order.Sequence, reason));
                continue;
            }
            var set = (SetMarkerOrder)order;
            State.Markers[set.Marker] = set.Value;
            changed.Add(set.Marker);
            EmitEvent((i, d) => new OrderAccepted(i, d, order.Sequence));
        }
    }

    // Events carry Day = K, the day at the start of the Step, because Day only changes in step 7.
    internal void EmitEvent(Func<int, int, GameEvent> make) => _events.Add(make(_events.Count, Day));
}
