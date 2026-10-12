using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;
using Rebellion.Core.Tests.Economy;
using Rebellion.Core.Tests.Fleets;
using Rebellion.Core.Tests.Support;

namespace Rebellion.Core.Tests.Construction;

/// <summary>
/// A game on the real galaxy with fleets, economy and construction attached. Autoscrap is pushed out of reach (a huge scrap period)
/// so the tests of the construction sheet are not disturbed by overdrafts; incidents are off. Stub generator unless a seed is given.
/// </summary>
internal sealed class BuildWorld
{
    public static readonly Lazy<CostData> RealCosts = new(() => CostData.Load(Path.Combine(TestData.InterimDir, CostData.FileName)));

    /// <summary>The real costs file with every maintenance set to 0 (for tests whose numbers assume load 0, like the pool sheet's P1 fixture).</summary>
    public static CostData ZeroMaintenance()
    {
        var lines = File.ReadAllLines(Path.Combine(TestData.InterimDir, CostData.FileName));
        var o = new List<string> { lines[0] };
        foreach (var l in lines.Skip(1).Where(x => x.Length > 0))
        {
            var f = l.Split(',');
            f[6] = "0"; // maint
            o.Add(string.Join(",", f));
        }
        return CostData.Parse(string.Join("\n", o), "zero-maintenance-costs.csv");
    }

    public StubRng? Stub { get; }
    public Game Game { get; private set; } = null!;
    public FleetModule Fleets { get; private set; } = null!;
    public EconomyModule Eco { get; private set; } = null!;
    public ConstructionModule Build { get; private set; } = null!;

    public SupportModule? Support { get; private set; }
    private readonly bool _withSupport, _wireFleets;

    /// <summary><paramref name="wireFleets"/>: finished and delivered ships join the spare fleet, as GameHost wires it (m3-fleets). Off by default so the construction sheet's tests see no fleets appear.</summary>
    public BuildWorld(CostData? costs = null, bool withSupport = false, bool wireFleets = false) : this(new StubRng(), costs, withSupport, wireFleets) { }
    public BuildWorld(ulong seed, CostData? costs = null) : this(new GameRng(seed), costs) { }

    private BuildWorld(GameRng rng, CostData? costs, bool withSupport = false, bool wireFleets = false)
    {
        _withSupport = withSupport; _wireFleets = wireFleets;
        Stub = rng as StubRng;
        Game = new Game(rng);
        Attach(costs);
    }

    private void Attach(CostData? costs)
    {
        Fleets = FleetModule.Attach(Game, TestData.RealGalaxy, World.Ships.Value, World.Blockade);
        Eco = EconomyModule.Attach(Game, TestData.RealGalaxy, EcoWorld.Slots.Value, costs ?? RealCosts.Value, Fleets,
            new EconomyParams { ScrapPeriod = 1_000_000 });
        Eco.IncidentsEnabled = false;
        Support = _withSupport ? SupportModule.Attach(Game, TestData.RealGalaxy, Eco, Fleets, SupWorld.Weights.Value) : null;
        Build = ConstructionModule.Attach(Game, Fleets, Eco, support: Support);
        if (_wireFleets)
            Build.UnitAppeared += (ctx, o, how) =>
                Fleets.OnUnitAppeared(ctx, o, how == UnitAppearance.Delivered ? ShipJoinCause.Delivered : ShipJoinCause.Completed);
    }

    /// <summary>Save, load and attach the modules again to the loaded game (the save holds state only).</summary>
    public void Reload()
    {
        var costs = Eco.Costs;
        Game = GameSave.Load(GameSave.Save(Game));
        Attach(costs);
    }

    public GameState State => Game.State;
    public EconomyState E => Game.State.Economy;
    public ConstructionState C => Game.State.Construction;

    public void Run(int steps) { for (int i = 0; i < steps; i++) Game.Step(); }
    public void RunTo(int day) { while (Game.Day < day) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();

    public void Hold(string system, Side side, int supportOfHolder = 100)
    {
        Fleets.SetOwner(system, side);
        Eco.SetAllianceSupport(system, side == Side.Alliance ? supportOfHolder : 100 - supportOfHolder);
    }

    public int Yard(string system, FacilityKind kind, int? id = null) => Eco.AddFacility(system, kind, true, id);
    public void Refined(Side side, int n) => Eco.SetPools(side, Eco.Raw(side), n);

    public long Order(Side side, string system, YardFamily fam, string cls, int count, string? dest = null) =>
        Build.SubmitBuild(side, system, fam, cls, count, dest);

    /// <summary>The reason an order was rejected in the event list, or null if it was accepted.</summary>
    public string? RejectionOf(long sequence) =>
        Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == sequence)?.Reason;

    /// <summary>Step 0 convenience: submit, run one Step, return the rejection reason (null = accepted).</summary>
    public string? TryOrder(Side side, string system, YardFamily fam, string cls, int count, string? dest = null)
    {
        long s = Order(side, system, fam, cls, count, dest);
        Run(1);
        return RejectionOf(s);
    }

    public int Scrap(Side side, int objectId)
    {
        Game.Submit(seq => new ScrapObjectOrder(seq, side, objectId));
        return objectId;
    }

    public EconObject Obj(int id) => Eco.Find(id) ?? throw new InvalidOperationException($"No object {id}");
}
