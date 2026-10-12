using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;

namespace Rebellion.Core.Defenses;

/// <summary>
/// Numbers and switches of the planetary-defenses sheet, with their tags. Data values: tests and setups may override them.
/// rule: m3-planetary-defenses §Numbers, §Defaults for undecided items.
/// </summary>
public sealed record DefenseParams
{
    /// <summary>A facility's shot chance is attack / this, in percent (GNPRTB 0x200 = 5). Traced.</summary>
    public int ShotChanceDivisor { get; init; } = 5;
    /// <summary>
    /// Completed shield generators that forbid an assault (2 or more). Original text; enforcement not traced. 0 switches the gate off
    /// (nothing is ever forbidden). rule: m3-planetary-defenses §What each class does in M3; m3-assault check 4.
    /// </summary>
    public int AssaultGateShields { get; init; } = 2;
    /// <summary>
    /// Does the Death Star Shield count as one of the shields of the gate? DEFAULT no (Open questions 1 and 5; the original UI counts it
    /// separately). rule: m3-planetary-defenses §Defaults for undecided items.
    /// </summary>
    public bool DeathStarShieldCountsForGate { get; init; }
}

/// <summary>One facility's shot in an assault. rule: m3-planetary-defenses §Queries (BatteryShots).</summary>
public sealed record DefenseShot(int Facility, string Class, int Attack, int ChancePercent);

/// <summary>
/// The queries the assault and bombardment sheets call on a system's defense facilities. No draws, no state of its own: everything is read
/// from the economy registry (<see cref="Economy.EconomyState.Objects"/>), so a loaded game needs nothing but to attach this again.
/// Facilities that count are the COMPLETED ones standing on the system; their side is the system's holder (a neutral system has side 3 in
/// the sheet: here a null holder, which is never the attacker). Unfinished facilities do not count, shoot or absorb (DEFAULT).
/// Facilities are visited in facility id order wherever order matters. rule: m3-planetary-defenses §Queries.
/// </summary>
public sealed class DefenseModule
{
    private readonly EconomyModule _eco;
    private readonly FleetModule _fleets;

    public DefenseData Data { get; }
    public DefenseParams Params { get; }

    private DefenseModule(EconomyModule eco, FleetModule fleets, DefenseData data, DefenseParams p)
    {
        _eco = eco; _fleets = fleets; Data = data; Params = p;
    }

    /// <summary>
    /// Create the module. It registers nothing on the game (nothing here runs by itself and no order is its own); GameHost holds it so
    /// the assault and bombardment modules share one instance. rule: m3-planetary-defenses §Where it runs and what it draws.
    /// </summary>
    public static DefenseModule Attach(EconomyModule eco, FleetModule fleets, DefenseData data, DefenseParams? p = null) =>
        new(eco, fleets, data, p ?? new DefenseParams());

    // ================================================================== the queries

    /// <summary>The completed defense facilities of a system in id order, with their class rows.</summary>
    public IEnumerable<(EconObject Facility, DefenseClass Class)> DefensesOf(string system)
    {
        foreach (var o in _eco.Objects) // id order
            if (o.Category == ObjectCategory.Facility && o.Completed && o.System == system && Data.TryGetClass(o.Class, out var c))
                yield return (o, c);
    }

    /// <summary>
    /// Number of completed GenCore generators (family 36). The Death Star Shield (family 37) is not counted. rule: m3-planetary-defenses §Queries
    /// (ShieldGenerators; D2).
    /// </summary>
    public int ShieldGenerators(string system) => DefensesOf(system).Count(d => d.Class.Job == DefenseJob.ShieldGenerator);

    /// <summary>
    /// Sum of the shield strength of every completed defense facility (only GenCore is non-zero): 0, 40, 80, 120, 160. Read once at the start
    /// of each bombardment. rule: m3-planetary-defenses §Queries (ShieldStrength; D3).
    /// </summary>
    public int ShieldStrength(string system) => DefensesOf(system).Sum(d => d.Class.Shield);

    /// <summary>Completed batteries (families 34 and 35: KDY and LNR). For UI counts and the M6 AI; no M3 rule reads it. rule: m3-planetary-defenses §Queries (Batteries; D7).</summary>
    public int Batteries(string system) => DefensesOf(system).Count(d => d.Class.Job == DefenseJob.Battery);

    /// <summary>
    /// Every completed defense facility whose side is not <paramref name="attacker"/>, in facility id order, with its attack and shot chance:
    /// the assault's step 1 list (a shield or Death Star Shield is listed with chance 0, so the draw count is fixed). A neutral system's
    /// facilities are listed too. Empty if the attacker holds the system. rule: m3-planetary-defenses §Queries (BatteryShots; D5, D6, D15).
    /// </summary>
    public IReadOnlyList<DefenseShot> BatteryShots(string system, Side attacker)
    {
        if (_fleets.OwnerOf(system) == attacker) return Array.Empty<DefenseShot>();
        return DefensesOf(system).Select(d => new DefenseShot(d.Facility.Id, d.Class.Name, d.Class.Attack, ChancePercent(d.Class.Attack))).ToList();
    }

    /// <summary>The batteries (families 34 and 35), id order: the set of the bombardment's BatteryReturnFire hook. rule: m3-planetary-defenses §Queries (ReturnFireSet; D8).</summary>
    public IReadOnlyList<int> ReturnFireSet(string system) =>
        DefensesOf(system).Where(d => d.Class.Job == DefenseJob.Battery).Select(d => d.Facility.Id).ToList();

    /// <summary>A facility's shot chance in percent: attack / 5, integer division (2000 gives 400, 0 gives 0). rule: m3-planetary-defenses §Numbers (D4).</summary>
    public int ChancePercent(int attack) => attack / Params.ShotChanceDivisor;

    /// <summary>
    /// Shield generators that count for the assault gate: GenCore, plus the Death Star Shield if the switch says so (DEFAULT no).
    /// rule: m3-planetary-defenses §What each class does in M3.
    /// </summary>
    public int GateShields(string system) =>
        ShieldGenerators(system) + (Params.DeathStarShieldCountsForGate ? DefensesOf(system).Count(d => d.Class.Job == DefenseJob.DeathStarShield) : 0);

    /// <summary>
    /// The assault gate the assault module calls at order validation and again at resolution: true when the system has the required number of
    /// completed shield generators (2 by default), so an assault is refused (<c>FullyShielded</c>). Holder-independent: it forbids both sides.
    /// rule: m3-planetary-defenses §What each class does in M3 (D2, D11); m3-assault check 4 (A10).
    /// </summary>
    public bool AssaultForbidden(string system) => Params.AssaultGateShields > 0 && GateShields(system) >= Params.AssaultGateShields;
}
