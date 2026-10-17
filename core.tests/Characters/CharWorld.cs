using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;
using Rebellion.Core.Tests.Economy;

namespace Rebellion.Core.Tests.Characters;

/// <summary>
/// A full <see cref="GameHost"/> (every module, real data unless given) on a stub generator, empty galaxy state (no owners), with helpers to
/// create characters at their base values (variance draws answered 0), place them, add fleets and run work inside a Step.
/// </summary>
internal sealed class CharWorld
{
    public const string Yavin = "Yavin", Hoth = "Hoth", Coruscant = "Coruscant";
    public const string Corvette = "Corellian Corvette", Isd = "Imperial Star Destroyer", MonCal = "Mon Calamari Cruiser";
    // record ids (data/original/characters.csv)
    public const int MonMothma = 576, Leia = 577, Luke = 578, Han = 579, Palpatine = 640, Vader = 641;
    public const int Ackbar = 832, Wedge = 833, Lando = 834, Chewbacca = 835, Dodonna = 836, Madine = 837, Afyon = 839, Drayson = 840, Borsk = 841, Tura = 842;
    public const int Piett = 898, Veers = 899;

    public StubRng? Stub { get; }
    public GameHost Host { get; private set; }
    public Game Game => Host.Game;
    public CharacterModule Ch => Host.Characters;
    private readonly GameSettings _settings;
    private readonly GameData? _data;

    public CharWorld(CharacterParams? p = null, GameData? data = null, ulong? seed = null, GameSettings? settings = null)
    {
        // Incidents off and autoscrap out of reach (an empty galaxy has no capacity, so every ship would be scrapped).
        _settings = (settings ?? new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true, Economy = new Rebellion.Core.Economy.EconomyParams { ScrapPeriod = 1_000_000 } }) with { Characters = p };
        _data = data;
        if (seed is ulong s) Host = GameHost.New(s, _settings, data);
        else { Stub = new StubRng(); Host = GameHost.New(Stub, _settings, data); }
    }

    /// <summary>Create a roster character at its base values (no draw from the game generator) and place it at a system.</summary>
    public CharacterRecord Add(int id, string system)
    {
        var c = Ch.Create(Ch.Data.Get(id), new StubRng());
        Ch.Place(id, system);
        return c;
    }

    public CharacterRecord C(int id) => Ch.Get(id)!;

    public int Fleet(Side side, string system, string cls = Corvette, int n = 1) =>
        Host.Fleets.AddFleet(side, $"F{Game.State.Fleets.Count + 1}", Enumerable.Repeat(cls, n), system);

    public void Hold(string system, Side side) => Host.Fleets.SetOwner(system, side);

    public long Submit(Func<long, Order> make) => Game.Submit(make);

    /// <summary>The rejection reason of order <paramref name="seq"/> after a Step, or null if accepted.</summary>
    public string? Reason(long seq) => Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == seq)?.Reason;

    /// <summary>Submit one order, Step once, return its rejection reason (null = accepted).</summary>
    public string? Try(Func<long, Order> make)
    {
        long seq = Submit(make);
        Game.Step();
        return Reason(seq);
    }

    /// <summary>Run one Step whose only extra work is <paramref name="work"/> at the end of <paramref name="slot"/>.</summary>
    public void StepWith(string slot, Action<StepContext> work)
    {
        bool done = false;
        Game.RegisterHook(slot, ctx => { if (!done) { done = true; work(ctx); } });
        Game.Step();
    }

    public void Run(int steps) { for (int i = 0; i < steps; i++) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();

    /// <summary>Save, load and attach again (same settings and data).</summary>
    public void Reload() => Host = GameHost.Load(Host.Save(), _settings, _data);
}
