using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.DeathStar;
using Rebellion.Core.Economy;
using Rebellion.Core.Map;
using Rebellion.Core.Setup;
using Rebellion.Core.Tests.Economy;

namespace Rebellion.Core.Tests.DeathStar;

/// <summary>
/// A full <see cref="GameHost"/> on a small map for the Death Star tests (m5-death-star §Tests). Sector SA has six systems on a line, A1 to A6
/// (lanes A1-A2 ... A5-A6); sector SB has B1, B2, B3 (lanes A1-B1, B1-B2, B2-B3). All populated, 9 energy and 3 raw. No owners unless set; every
/// support value is the data default until set. Incidents off, autoscrap out of reach, debug orders on. A stub generator unless a seed is given.
/// </summary>
internal sealed class DsWorld
{
    public const string A1 = "A1", A2 = "A2", A3 = "A3", A4 = "A4", A5 = "A5", A6 = "A6", B1 = "B1", B2 = "B2", B3 = "B3";
    public const string DeathStarClass = "Death Star", Isd = "Imperial Star Destroyer", Corvette = "Corellian Corvette";
    // character record ids (data/original/characters.csv): four Alliance minors and two majors
    public const int Luke = 578, Vader = 641, Drayson = 840, Borsk = 841, Tura = 842, Derlin = 843;
    public const string Sa = "SA", Sb = "SB";

    private static readonly Lazy<GameData> _data = new(() =>
    {
        var planets = "name,sector,group,map_x,map_y\n" +
                      "A1,SA,core,0,0\nA2,SA,core,40,0\nA3,SA,core,80,0\nA4,SA,core,120,0\nA5,SA,core,160,0\nA6,SA,core,200,0\n" +
                      "B1,SB,core,0,40\nB2,SB,core,40,40\nB3,SB,core,80,40\n";
        var lanes = "from,to,kind,routes\nA1,A2,generated,\nA2,A3,generated,\nA3,A4,generated,\nA4,A5,generated,\nA5,A6,generated,\n" +
                    "A1,B1,generated,\nB1,B2,generated,\nB2,B3,generated,\n";
        var g = GalaxyLoader.Load(planets, lanes, TestData.Tiers);
        var slots = "name,sector,group,energy,raw,populated\n" + string.Join("\n", g.Systems.Select(s => $"{s.Name},{s.Sector},{s.Group},9,3,1"));
        return GameData.Default with { Galaxy = g, Slots = SlotValues.Parse(slots, g) };
    });

    public static GameData Data => _data.Value;

    public StubRng? Stub { get; }
    public GameHost Host { get; private set; }
    public Game Game => Host.Game;
    public Rebellion.Core.DeathStar.DeathStarModule Ds => Host.DeathStar;
    private readonly GameSettings _settings;

    public DsWorld(DeathStarParams? ds = null, CharacterParams? ch = null, ulong? seed = null, GameSettings? settings = null)
    {
        _settings = (settings ?? new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true, Economy = new EconomyParams { ScrapPeriod = 1_000_000 } })
            with { DeathStar = ds, Characters = ch };
        if (seed is ulong s) Host = GameHost.New(s, _settings, Data);
        else { Stub = new StubRng(); Host = GameHost.New(Stub, _settings, Data); }
    }

    /// <summary>Owner and Alliance support of a system.</summary>
    public void Hold(string system, Side? side, int allianceSupport = 50)
    {
        Host.Fleets.SetOwner(system, side);
        Host.Economy.SetAllianceSupport(system, allianceSupport);
    }

    public int Fleet(Side side, string system, params string[] classes) =>
        Host.Fleets.AddFleet(side, $"F{Game.State.Fleets.Count + 1}", classes, system);

    public int DeathStar(Side side, string system, params string[] extra) =>
        Fleet(side, system, new[] { DeathStarClass }.Concat(extra).ToArray());

    /// <summary>A roster character at base values (no game draw), placed at a system.</summary>
    public CharacterRecord Person(int id, string system)
    {
        var c = Host.Characters.Create(Host.Characters.Data.Get(id), new StubRng());
        Host.Characters.Place(id, system);
        return c;
    }

    public CharacterRecord C(int id) => Host.Characters.Get(id)!;

    public string? Reason(long seq) => Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == seq)?.Reason;

    /// <summary>Submit a Destroy System order, Step once, return its rejection reason (null = accepted).</summary>
    public string? Destroy(Side side, int fleet)
    {
        long seq = Ds.SubmitDestroySystem(side, fleet);
        Game.Step();
        return Reason(seq);
    }

    public void Run(int steps) { for (int i = 0; i < steps; i++) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();
    public int Alliance(string system) => Host.Economy.SupportOf(system, Side.Alliance);
    public bool Exists(int objectId) => Host.Economy.Find(objectId) != null;
    public bool FleetExists(int fleetId) => Game.State.Fleets.ContainsKey(fleetId);
    public int ShipOf(int fleetId) => Game.State.Fleets[fleetId].Ships[0];

    /// <summary>Save, load and attach again (same settings and data).</summary>
    public void Reload() => Host = GameHost.Load(Host.Save(), _settings, Data);
}
