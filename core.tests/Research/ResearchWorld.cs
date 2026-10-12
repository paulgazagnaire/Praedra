using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Research;
using Rebellion.Core.Tests.Construction;
using Rebellion.Core.Tests.Economy;
using Rebellion.Core.Tests.Fleets;

namespace Rebellion.Core.Tests.Research;

/// <summary>
/// A game on the real galaxy with fleets, economy, construction and research attached (GameHost's order and wiring for these four), incidents
/// off and autoscrap out of reach. Stub generator unless a seed is given. Research is not armed until <see cref="Arm"/> is called.
/// </summary>
internal sealed class ResearchWorld
{
    public static readonly Lazy<ResearchData> RealResearch =
        new(() => ResearchData.Load(TestData.InterimDir, TestData.DesignDir, BuildWorld.RealCosts.Value));

    public StubRng? Stub { get; }
    public Game Game { get; private set; }
    public FleetModule Fleets { get; private set; } = null!;
    public EconomyModule Eco { get; private set; } = null!;
    public ConstructionModule Build { get; private set; } = null!;
    public ResearchModule Research { get; private set; } = null!;
    private readonly ResearchParams? _p;
    private readonly ResearchData? _data;

    public ResearchWorld(ResearchParams? p = null, ResearchData? data = null) : this(new StubRng(), p, data) { }
    public ResearchWorld(ulong seed, ResearchParams? p = null) : this(new GameRng(seed), p, null) { }

    private ResearchWorld(GameRng rng, ResearchParams? p, ResearchData? data)
    {
        Stub = rng as StubRng; _p = p; _data = data;
        Game = new Game(rng);
        Attach();
    }

    private void Attach()
    {
        Fleets = FleetModule.Attach(Game, TestData.RealGalaxy, World.Ships.Value, World.Blockade);
        Eco = EconomyModule.Attach(Game, TestData.RealGalaxy, EcoWorld.Slots.Value, BuildWorld.RealCosts.Value, Fleets,
            new EconomyParams { ScrapPeriod = 1_000_000 });
        Eco.IncidentsEnabled = false;
        Build = ConstructionModule.Attach(Game, Fleets, Eco);
        Research = ResearchModule.Attach(Game, Eco, Fleets, _data ?? RealResearch.Value, _p);
        Build.ResearchOrderReached = Research.OrderReached;
    }

    /// <summary>Save, load and attach again (the save holds state only).</summary>
    public void Reload()
    {
        Game = GameSave.Load(GameSave.Save(Game));
        Attach();
    }

    public void Arm(int? systems = null) => Research.SetupNewGame(systems);
    public void Run(int steps) { for (int i = 0; i < steps; i++) Game.Step(); }
    public void RunTo(int day) { while (Game.Day < day) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();

    public void Hold(string system, Side side) => Fleets.SetOwner(system, side);
    public int Fac(string system, FacilityKind kind, bool completed = true) => Eco.AddFacility(system, kind, completed);

    /// <summary>Run one Step whose only extra work is <paramref name="work"/> at the end of slot 6.9 (after the research tick): how m5-missions will call in.</summary>
    public void StepWith(Action<StepContext> work)
    {
        bool done = false;
        Game.RegisterHook("6.9", ctx => { if (!done) { done = true; work(ctx); } });
        Game.Step();
    }

    /// <summary>One AddMissionPoints call inside a Step; returns the levels gained.</summary>
    public int Add(Side side, ResearchKind kind, int total)
    {
        int gained = 0;
        StepWith(ctx => gained = Research.AddMissionPoints(ctx, side, kind, total));
        return gained;
    }

    public string? TryOrder(Side side, string system, YardFamily fam, string cls, int count = 1, string? dest = null)
    {
        long s = Build.SubmitBuild(side, system, fam, cls, count, dest);
        Game.Step();
        return Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == s)?.Reason;
    }
}
