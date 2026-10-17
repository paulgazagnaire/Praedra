using Rebellion.Core.Clock;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;
using Rebellion.Core.Tests.Economy;
using Rebellion.Core.Tests.Support;

namespace Rebellion.Core.Tests.Defenses;

/// <summary>
/// A support world (fleets, economy, support, real files) with the defense and bombardment modules attached. Autoscrap is out of reach
/// and incidents are off. Stub generator unless <c>stub: false</c> (then the seeded game generator). <see cref="Sys"/> is the system
/// most tests bombard; <see cref="Hold"/> makes it Alliance (or Empire) with the holder's support at 100 (so N = 0 and no uprising).
/// </summary>
internal sealed class BombWorld
{
    public const string Sys = "Denon";
    public const string Other = "Bespin";
    public const string Imp = "Coruscant";

    public static readonly Lazy<DefenseData> RealData = new(() => DefenseData.Load(TestData.OriginalDir, TestData.DesignDir));

    public SupWorld S { get; }
    public DefenseModule Def { get; }
    public BombardmentModule Bomb { get; }
    public Game Game => S.Game;
    public EconomyModule Eco => S.W.Eco;
    public FleetModule Fleets => S.W.Fleets;
    public StubRng Stub => S.Stub;

    public BombWorld(bool stub = true, ulong seed = 1, DefenseData? data = null, DefenseParams? dp = null)
    {
        S = new SupWorld(GarrisonRuleset.Classic, stub, seed);
        Def = DefenseModule.Attach(Eco, Fleets, data ?? RealData.Value, dp);
        Bomb = BombardmentModule.Attach(Game, Eco, Fleets, S.Sup, Def);
    }

    /// <summary>The real defense data with one text edit applied to the bombardment-defense file (e.g. another regiment defense).</summary>
    public static DefenseData DataWith(Func<string, string> editBombardFile)
    {
        string dir = TestData.OriginalDir;
        return DefenseData.Parse(File.ReadAllText(Path.Combine(dir, DefenseData.DefensesFile)), DefenseData.DefensesFile,
            editBombardFile(TestData.MergedText(DefenseData.BombardmentFile)), "test-bombardment-defense.csv");
    }

    /// <summary>Data where every Army regiment (both sides) has bombardment defense <paramref name="d"/>.</summary>
    public static DefenseData ArmyDefense(int d) => DataWith(t => string.Join("\n", t.Split('\n').Select(l =>
        l.StartsWith("troop,Alliance Army Regiment,") || l.StartsWith("troop,Imperial Army Regiment,")
            ? l.Split(',')[0] + "," + l.Split(',')[1] + "," + d + "," + l.Split(',')[3] : l)));

    public void Hold(Side side, int support = 100, string system = Sys) => S.Hold(system, side, support);

    public int Fleet(Side side, string system, params string[] ships) => Fleets.AddFleet(side, $"{side} {Game.State.Fleets.Count}", ships, system);

    /// <summary>A fleet of <paramref name="count"/> ships of one class.</summary>
    public int Fleet(Side side, string system, string ship, int count) => Fleet(side, system, Enumerable.Repeat(ship, count).ToArray());

    public int Facility(FacilityKind kind, bool completed = true, string system = Sys) => Eco.AddFacility(system, kind, completed);
    public int Regiment(Side side, string cls, string system = Sys) => Eco.AddObject(side, cls, system);

    public long Bombard(Side side, int fleet, string system, BombardCategory cat) => Bomb.SubmitBombard(side, fleet, system, cat);

    /// <summary>Submit, run one Step, return the rejection reason (null = accepted).</summary>
    public string? TryBombard(Side side, int fleet, string system, BombardCategory cat)
    {
        long seq = Bombard(side, fleet, system, cat);
        Run(1);
        return RejectionOf(seq);
    }

    public string? RejectionOf(long seq) => Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == seq)?.Reason;

    public void Run(int n = 1) { for (int i = 0; i < n; i++) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();

    /// <summary>Stub draws as (t, r) pairs, in order.</summary>
    public void Dice(params (int T, int R)[] hits) { foreach (var (t, r) in hits) Stub.Queue(t, r); }

    public static readonly string Army = "Alliance Army Regiment";
    public static readonly string Isd = "Imperial Star Destroyer";
}
