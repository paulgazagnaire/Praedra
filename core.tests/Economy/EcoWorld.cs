using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Tests.Fleets;

namespace Rebellion.Core.Tests.Economy;

/// <summary>
/// A test generator that returns queued values (the rule sheets' "stub draws"). Every request is recorded as (min, max).
/// Returns 0 unless a value was queued. A queued value outside the requested range throws, which catches wrong ranges.
/// </summary>
internal sealed class StubRng : GameRng
{
    private readonly Queue<int> _any = new();
    private readonly Dictionary<(int, int), Queue<int>> _byRange = new(); // lookup only
    public List<(int Min, int Max)> Requests { get; } = new();

    public StubRng() : base(1) { }

    /// <summary>Values returned in order to the next requests, whatever their range.</summary>
    public StubRng Queue(params int[] values) { foreach (var v in values) _any.Enqueue(v); return this; }

    /// <summary>Values returned to the next requests of exactly this range (used when other draws interleave).</summary>
    public StubRng Expect(int min, int max, params int[] values)
    {
        if (!_byRange.TryGetValue((min, max), out var q)) _byRange[(min, max)] = q = new Queue<int>();
        foreach (var v in values) q.Enqueue(v);
        return this;
    }

    public override int NextInt(int minInclusive, int maxInclusive)
    {
        Requests.Add((minInclusive, maxInclusive));
        int v = 0;
        if (_byRange.TryGetValue((minInclusive, maxInclusive), out var q) && q.Count > 0) v = q.Dequeue();
        else if (_any.Count > 0) v = _any.Dequeue();
        if (v < minInclusive || v > maxInclusive)
            throw new InvalidOperationException($"Stub draw {v} is outside the requested range {minInclusive}..{maxInclusive}.");
        return v;
    }

    public int Count(int min, int max) => Requests.Count(r => r.Min == min && r.Max == max);
}

/// <summary>A game on the real galaxy with fleets and economy attached, plus short helpers.</summary>
internal sealed class EcoWorld
{
    public static readonly Lazy<SlotValues> Slots = new(() => SlotValues.Load(Path.Combine(TestData.DesignDir, SlotValues.File), TestData.RealGalaxy));
    public static readonly Lazy<CostData> Costs = new(() => CostData.Load(TestData.OriginalDir, TestData.DesignDir));

    public StubRng? Stub { get; }
    public Game Game { get; }
    public FleetModule Fleets { get; }
    public EconomyModule Eco { get; }

    /// <summary>
    /// The real costs with the maintenance of every capital ship and fighter set to 0. Fleet ships are registry objects and pay upkeep
    /// (m3-fleets), so the pool sheet's tests, whose numbers assume the load of the regiments and facilities only (and a ship that is
    /// not autoscrapped), build their fleets in a world that uses this table.
    /// </summary>
    public static readonly Lazy<CostData> FreeShipCosts = new(() =>
    {
        var lines = TestData.MergedText(CostData.FileName).Split('\n');
        var o = new List<string> { lines[0] };
        foreach (var l in lines.Skip(1).Where(x => x.Length > 0))
        {
            var f = l.Split(',');
            if (f[0] is "capital ship" or "fighter") f[6] = "0"; // maint
            o.Add(string.Join(",", f));
        }
        return CostData.Parse(string.Join("\n", o), "free-ship-costs.csv");
    });

    /// <summary>Stub generator, incidents off (the pool and maintenance sheets' tests do not use incidents). <paramref name="freeShips"/>: ships and fighters cost no upkeep.</summary>
    public EcoWorld(bool incidents = false, EconomyParams? p = null, bool freeShips = false) : this(new StubRng(), incidents, p, freeShips) { }

    public EcoWorld(ulong seed, bool incidents = false, EconomyParams? p = null) : this(new GameRng(seed), incidents, p, false) { }

    private EcoWorld(GameRng rng, bool incidents, EconomyParams? p, bool freeShips)
    {
        Stub = rng as StubRng;
        Game = new Game(rng);
        Fleets = FleetModule.Attach(Game, TestData.RealGalaxy, World.Ships.Value, World.Blockade);
        Eco = EconomyModule.Attach(Game, TestData.RealGalaxy, Slots.Value, freeShips ? FreeShipCosts.Value : Costs.Value, Fleets, p);
        Eco.IncidentsEnabled = incidents;
    }

    public GameState State => Game.State;
    public EconomyState E => Game.State.Economy;

    public void Run(int steps) { for (int i = 0; i < steps; i++) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();

    /// <summary>Own a system and set the owner's support (Empire or Alliance value as seen by the owner).</summary>
    public void Hold(string system, Side side, int supportOfHolder = 100)
    {
        Fleets.SetOwner(system, side);
        Eco.SetAllianceSupport(system, side == Side.Alliance ? supportOfHolder : 100 - supportOfHolder);
    }

    public int Mine(string system, bool completed = true, int? id = null) => Eco.AddFacility(system, FacilityKind.Mine, completed, id);
    public int Refinery(string system, bool completed = true, int? id = null) => Eco.AddFacility(system, FacilityKind.Refinery, completed, id);

    /// <summary>Run a callback inside the next Step (hook 6.8, after the economy), to call methods that need a StepContext.</summary>
    public void InStep(Action<StepContext> a)
    {
        bool done = false;
        Game.RegisterHook("6.8", ctx => { if (!done) { done = true; a(ctx); } });
    }

    public static int IndexOf(string system) => TestData.RealGalaxy.TryFindSystem(system, out var i) ? i : throw new ArgumentException(system);
}
