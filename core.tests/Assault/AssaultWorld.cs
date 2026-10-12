using Rebellion.Core.Assault;
using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Tests.Defenses;
using Rebellion.Core.Tests.Economy;
using Rebellion.Core.Troops;

namespace Rebellion.Core.Tests.Assault;

/// <summary>
/// A <see cref="BombWorld"/> (fleets, economy, support, defenses, bombardment, real files) with the troop, control and assault modules
/// attached. Stub generator unless <c>stub: false</c>. <see cref="Sys"/> is "Denon". Regiments "aboard" are placed on the first ship of the
/// fleet without a capacity check (the troop orders are tested elsewhere).
/// </summary>
internal sealed class AssaultWorld
{
    public const string Sys = BombWorld.Sys;
    public const string Reb = "Alliance Army Regiment", Imp = "Imperial Army Regiment";
    public const string Isd = "Imperial Star Destroyer", Neb = "Nebulon-B Frigate";

    public static readonly Lazy<TroopData> Troops = new(() => TroopData.Load(TestData.InterimDir, TestData.DesignDir));

    public BombWorld B { get; }
    public TroopModule Troop { get; }
    public ControlModule Control { get; }
    public AssaultModule Assault { get; }
    public Game Game => B.Game;
    public EconomyModule Eco => B.Eco;
    public StubRng Stub => B.Stub;

    public AssaultWorld(bool stub = true, ulong seed = 1, AssaultParams? p = null)
    {
        B = new BombWorld(stub, seed);
        Troop = TroopModule.Attach(Game, B.Fleets, Troops.Value);
        Control = ControlModule.Attach(Game, TestData.RealGalaxy, B.Fleets, Eco, B.S.Sup);
        Assault = AssaultModule.Attach(Game, Eco, B.Fleets, Troop, Control, B.Def, B.Bomb, p);
        // mark the stub's draw count just before slot 5.2 runs the assault and just after it (the economy and support draw in other slots)
        if (stub)
        {
            Game.RegisterHook("5.1", _ => _from = Stub.Requests.Count);
            Game.RegisterHook("5.2", _ => _to = Stub.Requests.Count);
        }
    }

    private int _from, _to;

    /// <summary>The ranges of the draws made by slot 5.2 in the last Step (stub worlds only).</summary>
    public List<(int Min, int Max)> AssaultDraws => Stub.Requests.Skip(_from).Take(_to - _from).Select(r => (r.Min, r.Max)).ToList();

    public void Hold(Side side, int support = 100, string system = Sys) => B.Hold(side, support, system);

    /// <summary>A fleet of one capital ship class, <paramref name="n"/> ships, at the system.</summary>
    public int Fleet(Side side, string ship = Isd, int n = 1, string system = Sys) => B.Fleet(side, system, ship, n);

    /// <summary>Put <paramref name="n"/> new regiments aboard the first ship of the fleet; returns their ids (ascending).</summary>
    public List<int> Aboard(int fleet, string cls, int n = 1)
    {
        var f = Game.State.Fleets[fleet];
        var ids = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int id = Eco.AddObject(f.Side, cls, null);
            var o = Eco.Find(id)!;
            o.AboardShip = f.Ships[0]; o.System = null;
            ids.Add(id);
        }
        return ids;
    }

    public List<int> Defenders(Side side, string cls, int n, string system = Sys) =>
        Enumerable.Range(0, n).Select(_ => Eco.AddObject(side, cls, system)).ToList();

    public long Submit(Side side, int fleet, string system = Sys) => Assault.SubmitAssault(side, fleet, system);

    /// <summary>Submit, run one Step, return the rejection reason (null = accepted).</summary>
    public string? Try(Side side, int fleet, string system = Sys)
    {
        long seq = Submit(side, fleet, system);
        Run(1);
        return B.RejectionOf(seq);
    }

    public void Run(int n = 1) => B.Run(n);
    public List<T> Events<T>() where T : GameEvent => B.Events<T>();

    /// <summary>The newest AssaultResolved event (scanned from the end, so long loops stay cheap).</summary>
    public AssaultResolved LastResolved()
    {
        for (int i = Game.Events.Count - 1; i >= 0; i--) if (Game.Events[i] is AssaultResolved r) return r;
        throw new InvalidOperationException("no AssaultResolved event");
    }

    /// <summary>Remove every regiment and give the system back to <paramref name="holder"/> (between trials of a statistical test).</summary>
    public void ResetRegiments(Side? holder, string system = Sys)
    {
        foreach (var id in Eco.Objects.Where(o => o.Category == ObjectCategory.Regiment).Select(o => o.Id).ToList()) Eco.RemoveObject(id);
        if (holder is Side s) B.Fleets.SetOwner(system, s);
    }
}
