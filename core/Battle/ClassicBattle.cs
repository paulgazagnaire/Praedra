using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Ships;

namespace Praedra.Core.Battle;

/// <summary>
/// One unit of a battle: a capital ship or a squadron, a plain copy of the registry object's battle state. The battle never touches
/// the registry; <see cref="BattleModule"/> builds the units and writes the result back. Hull and shield are doubles (DEFAULT, m4-battle-classic
/// Open question 10: the original uses 32-bit floats). rule: m4-battle-classic §2 (units), §5 (start state).
/// </summary>
public sealed class BattleUnit
{
    /// <summary>Position in the force: the unit id of the sheet (Collective first, then Hand; ships by id, then squadrons).</summary>
    public int Index { get; set; }
    /// <summary>The registry object id (0 for a synthetic unit).</summary>
    public int ObjectId { get; set; }
    public Side Side { get; set; }
    public ShipClass Class { get; set; } = null!;
    public bool IsCapital { get; set; }
    public double Hull { get; set; }
    /// <summary>Class hull of a ship; squadron_size x 8 for a squadron (Classic), the squadron hull constant (New).</summary>
    public double HullMax { get; set; }
    public double Shield { get; set; }
    /// <summary>
    /// Classic: the shield cap. A ship's is its class shield and never changes; a squadron's shrinks with its hull ratio every second (it compounds).
    /// Set by <see cref="ClassicBattle"/> at the start. rule: m4-battle-classic §5, §7.
    /// </summary>
    public double ShieldMax { get; set; }
    /// <summary>
    /// The six counters. Classic battle: shield generator, weapons, tractor, sub-light, hyperdrive (primary + backup, see <see cref="BattleModule"/>), and
    /// index 5 unused. Squadrons take the same rolls. rule: m4-battle-classic §6, §7.
    /// </summary>
    public int[] Hits { get; } = new int[ObjectRegistry.HitCounters];
    /// <summary>Squadron aboard a ship: that ship's object id (the recorded carrier); 0 = based.</summary>
    public int Carrier { get; set; }
    /// <summary>Characters and special-forces units aboard (each adds 1 to the target value). rule: m4-battle-classic §5 (Traced, inferred "aboard").</summary>
    public int Passengers { get; set; }
    /// <summary>Classic: a squadron in a hangar (state 1): at the start, a hyperdrive-less squadron of the Hand aboard its carrier; on retreat, one that docked.</summary>
    public bool Docked { get; set; }
    /// <summary>Still in the battle: not destroyed and not gone.</summary>
    public bool Alive { get; set; } = true;
    /// <summary>The side retreated and this unit jumped (it survived).</summary>
    public bool Left { get; set; }
    /// <summary>Retreat: a squadron that docked in (or found a free berth on) this ship (object id), or 0.</summary>
    public int NewCarrier { get; set; }
    /// <summary>Retreat: a squadron with a working hyperdrive that jumped on its own (it needs no berth).</summary>
    public bool JumpedAlone { get; set; }
    /// <summary>Why the unit was destroyed (set with <see cref="Alive"/> = false and <see cref="Left"/> = false).</summary>
    public UnitLossReason? Loss { get; set; }
    public int LostAt { get; set; }
    public bool Destroyed => !Alive && !Left;

    /// <summary>Hull ratio; 0 for a unit without hull.</summary>
    public double HullRatio => HullMax <= 0 ? 0 : Hull / HullMax;

    internal BattleUnit? DockedIn { get; set; }
}

/// <summary>What happened in a battle, in order, for the module to turn into events and registry changes. rule: m4-battle-classic §10.</summary>
public abstract record BattleStep;
/// <summary>A unit was destroyed in second <paramref name="Second"/> (0 = at the alert).</summary>
public sealed record UnitDestroyedStep(BattleUnit Unit, int Second, UnitLossReason Reason) : BattleStep;
/// <summary>The first refusal of a retreat in this battle. rule: m4-retreat-damage-repair §1.</summary>
public sealed record RetreatRefusedStep(Side Side, RetreatRefusal Reason) : BattleStep;
/// <summary>New rules: a ship of a retreating side was held by an enemy tractor beam and stays on the field. rule: m4-retreat-damage-repair §6.</summary>
public sealed record TractorHeldStep(BattleUnit Ship, BattleUnit Holder, int Second) : BattleStep;
/// <summary>A side retreated (its units are marked <see cref="BattleUnit.Left"/>). rule: m4-retreat-damage-repair §2.</summary>
public sealed record SideRetreatedStep(Side Side, int Second) : BattleStep;

/// <summary>The result of one battle.</summary>
public sealed class ClassicBattleResult
{
    public int Seconds { get; init; }
    public BattleOutcome CollectiveOutcome { get; init; }
    public BattleOutcome HandOutcome { get; init; }
    public int CollectiveLosses { get; init; }
    public int HandLosses { get; init; }
    public int CollectiveSurvivors { get; init; }
    public int HandSurvivors { get; init; }
    public bool CollectiveRetreated { get; init; }
    public bool HandRetreated { get; init; }
    /// <summary>Numbers the battle generator gave out.</summary>
    public long Draws { get; init; }
    public IReadOnlyList<BattleStep> Steps { get; init; } = Array.Empty<BattleStep>();

    public BattleOutcome OutcomeOf(Side s) => s == Side.Collective ? CollectiveOutcome : HandOutcome;
    public bool Retreated(Side s) => s == Side.Collective ? CollectiveRetreated : HandRetreated;
}

/// <summary>The dice of one Classic battle: rand(0..n), inclusive; n &lt;= 1 gives 0 and draws nothing. rule: m4-battle-classic §11 (B3 §13).</summary>
public interface IBattleDice
{
    int Rand(int n);
    long Draws { get; }
}

/// <summary>
/// The original's battle generator (Traced, B3 §13, 0x5af5f0 / 0x5f5700): Lehmer x = 41358 x mod (2^31 - 1), seeded 12345678 and stepped 16 times
/// for every battle; rand(0..n) is one step, then x / floor(0x7ffffffe / n). Deterministic, seeded, no clock: a core-owned per-battle generator
/// (CLAUDE.md hard rule 2 note). rule: m4-battle-classic §1.4.
/// </summary>
public sealed class LehmerBattleRng : IBattleDice
{
    private const long Modulus = 0x7fffffff, Multiplier = 41358, Span = 0x7ffffffe;
    private long _x;

    public LehmerBattleRng(int seed, int warmup)
    {
        _x = seed;
        for (int i = 0; i < warmup; i++) Step();
    }

    /// <summary>The generator state.</summary>
    public long State => _x;
    public long Draws { get; private set; }

    private void Step() => _x = Multiplier * _x % Modulus;

    public int Rand(int n)
    {
        if (n <= 1) return 0; // no step
        Step();
        Draws++;
        return (int)(_x / (Span / n));
    }
}

/// <summary>Switch ClassicBattleRng = Game: the battle's own <see cref="GameRng"/>, seeded by one game draw. rule: m4-battle-classic §1.4.</summary>
internal sealed class GameBattleDice : IBattleDice
{
    private readonly GameRng _rng;
    public GameBattleDice(GameRng rng) { _rng = rng; }
    public int Rand(int n) => n <= 1 ? 0 : _rng.NextInt(0, n);
    public long Draws => _rng.Draws;
}

/// <summary>
/// The unit formulas of the Classic battle (B3 §4, §5, §7): pure functions of a unit's class, hull, shield and hit counters.
/// rule: m4-battle-classic §4, §5, §7.
/// </summary>
public static class ClassicRates
{
    /// <summary>Installed hyperdrive components: one for each non-zero value of hyperdrive and hyperdrive_if_damaged. rule: m4-battle-classic §5 (Traced).</summary>
    public static int HyperdriveComponents(ShipClass c) => (c.Hyperdrive != 0 ? 1 : 0) + (c.HyperdriveIfDamaged != 0 ? 1 : 0);

    /// <summary>Craft of a whole squadron: FIGHTSD squadron_size (12 for all eight). rule: m4-battle-classic §5.</summary>
    public static int SquadronSize(BattleParams p, ShipClass c) => c.SquadronSize > 0 ? c.SquadronSize : p.SquadronCraft;

    /// <summary>Squadron hull max = squadron_size x 8.0. rule: m4-battle-classic §5.</summary>
    public static double SquadronHullMax(BattleParams p, ShipClass c) => SquadronSize(p, c) * p.SquadronCraftHull;

    /// <summary>Start shield (and cap): the class shield of a ship, not scaled by hull; shield_strength x squadron_size of a squadron. rule: m4-battle-classic §5.</summary>
    public static double StartShield(BattleParams p, ShipClass c, bool capital) => capital ? c.ShieldStrength : (double)c.ShieldStrength * SquadronSize(p, c);

    /// <summary>The guns of arc a in the battle's scan order fore, starboard, aft, port; a squadron has only the fore arc, per-craft counts x squadron_size. rule: m4-battle-classic §4, §5.</summary>
    public static ArcCounts ArcOf(BattleParams p, BattleUnit u, int a)
    {
        var arcs = u.Class.GunArcs;
        if (!u.IsCapital)
        {
            if (a != 0) return default;
            int n = SquadronSize(p, u.Class);
            return new ArcCounts(arcs.Fore.Laser * n, arcs.Fore.Ion * n, arcs.Fore.Turbolaser * n);
        }
        return a switch { 0 => arcs.Fore, 1 => arcs.Starboard, 2 => arcs.Aft, _ => arcs.Port };
    }

    public const int ArcCount = 4;

    /// <summary>Torpedoes of a squadron: per-craft count x squadron_size. rule: m4-battle-classic §5.</summary>
    public static int Torpedoes(BattleParams p, BattleUnit u) => u.IsCapital ? 0 : u.Class.Torpedoes * SquadronSize(p, u.Class);

    /// <summary>A laser cannon in any arc. rule: m4-battle-classic §6 (pools), §2 (capital pools).</summary>
    public static bool HasLaser(BattleParams p, BattleUnit u)
    {
        for (int a = 0; a < ArcCount; a++) if (ArcOf(p, u, a).Laser > 0) return true;
        return false;
    }

    /// <summary>A gun of any type in any arc. rule: m4-battle-classic §2 (0x5b7d10).</summary>
    public static bool IsArmed(BattleParams p, BattleUnit u)
    {
        for (int a = 0; a < ArcCount; a++) if (ArcOf(p, u, a).Total > 0) return true;
        return false;
    }

    /// <summary>The longest range of the gun types the unit carries. rule: m4-battle-classic §2 (pool order).</summary>
    public static int LongestRange(BattleParams p, BattleUnit u)
    {
        bool l = false, i = false, t = false;
        for (int a = 0; a < ArcCount; a++) { var c = ArcOf(p, u, a); l |= c.Laser > 0; i |= c.Ion > 0; t |= c.Turbolaser > 0; }
        int r = 0;
        if (l) r = Math.Max(r, u.Class.LaserRange);
        if (i) r = Math.Max(r, u.Class.IonRange);
        if (t) r = Math.Max(r, u.Class.TurbolaserRange);
        return r;
    }

    /// <summary>Base rate: a ship max(0, W x (hullRatio - 0.25 x weapons hits)), W = weapon_recharge_rate x 3.75; a squadron hull x 3.751 / 8. rule: m4-battle-classic §4.</summary>
    public static double BaseRate(BattleParams p, BattleUnit u) => u.IsCapital
        ? Math.Max(0.0, u.Class.WeaponRechargeRate * p.WeaponMultiplier * (u.HullRatio - p.HitPenalty * u.Hits[1]))
        : u.Hull * p.SquadronRateFactor / p.SquadronCraftHull;

    /// <summary>Shield recharge per second: a ship max(0, R x (hullRatio - 0.25 x generator hits)); a squadron hull x 0.025 / 8. rule: m4-battle-classic §7.</summary>
    public static double Recharge(BattleParams p, BattleUnit u) => u.IsCapital
        ? Math.Max(0.0, u.Class.ShieldRechargeRate * (u.HullRatio - p.HitPenalty * u.Hits[0]))
        : u.Hull * p.SquadronRechargeFactor / p.SquadronCraftHull;

    /// <summary>Battle speed: sub-light x (1 - 0.25 x sub-light hits), floor 0. rule: m4-battle-classic §7.</summary>
    public static double Speed(BattleParams p, BattleUnit u) =>
        Math.Max(0.0, u.Class.SubLight * (u.IsCapital ? p.CapitalSpeedPerSublight : p.SquadronSpeedPerSublight) * (1 - p.HitPenalty * u.Hits[3]));

    /// <summary>Maneuverability: 1.0 at speed 0; else class maneuverability + 1, capped at 9. rule: m4-battle-classic §4.</summary>
    public static double Maneuver(BattleParams p, BattleUnit u) =>
        Speed(p, u) == 0 ? 1.0 : Math.Min(p.ManeuverCap, u.Class.Maneuverability + p.ManeuverBonus);

    /// <summary>Can jump: hyperdrive cap - hyperdrive hits &gt; 0 and speed &gt; 0 (0x5b1b70). rule: m4-retreat-damage-repair §1.</summary>
    public static bool CanJump(BattleParams p, BattleUnit u) =>
        HyperdriveComponents(u.Class) - u.Hits[ObjectRegistry.HyperdriveCounter] > 0 && Speed(p, u) > 0;

    /// <summary>Shield ratio: current / cap, 0 for a unit with no shield. rule: m4-battle-classic §4 (torpedoes).</summary>
    public static double ShieldRatio(BattleUnit v) => v.ShieldMax > 0 ? v.Shield / v.ShieldMax : 0.0;

    private static double ArcF(BattleParams p, ArcCounts a, bool vsCapital) => p.FamilyCoefficient * (vsCapital
        ? a.Laser * p.LaserVsCapital + a.Ion * p.IonVsCapital + a.Turbolaser * p.TurbolaserVsCapital
        : a.Laser * p.LaserVsSquadron + a.Ion * p.IonVsSquadron + a.Turbolaser * p.TurbolaserVsSquadron);

    /// <summary>
    /// rate(u, v) (0x5b7780). With <paramref name="full"/> false it is the mode-0 rate F against the capital-ship class: no maneuverability, no torpedoes
    /// (the target value and the strength). Best arc strictly greater, scanned fore, starboard, aft, port. A squadron's gun count is trunc(N x hullRatio),
    /// its sum is times hullRatio, and against a capital ship at shield ratio 0 it adds hullRatio x torpedoes x 0.53333336 x hullRatio.
    /// rule: m4-battle-classic §4.
    /// </summary>
    public static double Rate(BattleParams p, BattleUnit u, BattleUnit? v, bool vCapital, bool full = true)
    {
        double b = BaseRate(p, u);
        if (b == 0) return 0;
        int best = -1; double bf = 0;
        for (int a = 0; a < ArcCount; a++)
        {
            double f = ArcF(p, ArcOf(p, u, a), vCapital);
            if (f > bf) { best = a; bf = f; }
        }
        if (best < 0) return 0;
        var c = ArcOf(p, u, best);
        int n = c.Total;
        double hr = u.HullRatio;
        if (!u.IsCapital) n = (int)Math.Truncate(n * hr);
        if (n == 0) return 0;
        double m = 1.0;
        if (full && v != null && !vCapital) m = Math.Max(p.ManeuverFloor, Maneuver(p, u) / Maneuver(p, v));
        double k = p.FamilyCoefficient;
        double s = vCapital
            ? c.Ion * k * p.IonVsCapital + c.Laser * k * p.LaserVsCapital + c.Turbolaser * k * p.TurbolaserVsCapital
            : c.Ion * k * p.IonVsSquadron + c.Laser * k * p.LaserVsSquadron * m + c.Turbolaser * k * p.TurbolaserVsSquadron * m;
        if (!u.IsCapital)
        {
            s *= hr;
            int torps = Torpedoes(p, u);
            if (full && v != null && vCapital && ShieldRatio(v) == 0 && torps > 0) s += hr * (torps * p.TorpedoTerm * hr);
        }
        return s / (n / b);
    }

    /// <summary>F(u): the mode-0 rate against the capital-ship class (live hull and weapon hits). rule: m4-battle-classic §5, §8.</summary>
    public static double F(BattleParams p, BattleUnit u) => Rate(p, u, null, true, full: false);

    /// <summary>The retreat test after canRetreat (0x5d19a0): own strength 0, or own / enemy below the threshold (strict) or 0; never when the enemy's is 0. rule: m4-battle-classic §8.4.</summary>
    public static bool RetreatWanted(double own, double enemy, double threshold)
    {
        if (own == 0) return true;
        if (enemy <= 0) return false;
        double r = own / enemy;
        return r < threshold || r == 0;
    }
}

/// <summary>A task force: units that fire one combined hit a second at one target. rule: m4-battle-classic §2 (B3 §2).</summary>
public sealed class ClassicTaskForce
{
    public int Id { get; init; }
    public Side Side { get; init; }
    public List<BattleUnit> Members { get; init; } = new();
    public BattleUnit? Target { get; set; }
    /// <summary>Members still in the battle and not docked (states 2, 4, 6).</summary>
    public IEnumerable<BattleUnit> Live => Members.Where(m => m.Alive && !m.Docked);
}

/// <summary>
/// The Classic space battle (the traced Classic Simulate, batch B3), one second at a time, on plain units and its own dice. Nothing moves; the retreat
/// destination is decided by the caller (it only says whether each side has one). rule: m4-battle-classic (the whole sheet); m4-retreat-damage-repair §1, §2.
/// </summary>
public sealed class ClassicBattle
{
    private readonly BattleParams _p;
    private readonly List<BattleUnit> _units;
    private readonly IBattleDice _dice;
    private readonly bool[] _hasDestination = new bool[2];   // by (int)Side
    private readonly double[] _threshold = new double[2];
    private readonly List<BattleStep> _steps = new();
    private readonly bool[] _retreated = new bool[2];
    private readonly bool[] _forced = new bool[2];
    private readonly List<ClassicTaskForce>[] _tfs = { new(), new() };
    private readonly List<BattleUnit>[] _squadList = { new(), new() };   // the side's squadron list (+0x70 order): retreat order
    private readonly double[] _low = new double[2];
    private readonly int[] _timer = new int[2];
    private bool _refusalReported;
    private int _second;
    private int _nextTf;

    // Side order of the result and of Simultaneous fire: Collective before Hand.
    private static readonly Side[] Order = { Side.Collective, Side.Hand };
    private static int I(Side s) => (int)s;
    private static Side Other(Side s) => s == Side.Collective ? Side.Hand : Side.Collective;

    /// <param name="units">All units; <see cref="BattleUnit.Index"/> must equal the position. Hull, HullMax and Hits as stored; shields are set full here.</param>
    /// <param name="rng">The battle's GameRng, used only with ClassicBattleRng = Game.</param>
    /// <param name="hasDestination">Per side (indexed by <c>(int)Side</c>): does a valid retreat system exist? rule: m4-retreat-damage-repair §1 (5).</param>
    /// <param name="alert">Per side: Fight or Retreat (the human controller's threshold). Null = no controller: <see cref="BattleParams.RetreatThreshold"/>.</param>
    /// <param name="ai">Per side: the AI controls it.</param>
    /// <param name="aiThreshold">Per side: the AI's threshold computed at the alert (B6a §2); null = <see cref="BattleParams.AiRetreatCap"/>.</param>
    public ClassicBattle(BattleParams p, IReadOnlyList<BattleUnit> units, GameRng? rng, bool[] hasDestination, OnContactMode[]? alert = null, bool[]? ai = null,
        double[]? aiThreshold = null)
    {
        _p = p; _units = units.ToList();
        for (int i = 0; i < _units.Count; i++)
            if (_units[i].Index != i) throw new ArgumentException("Unit.Index must equal the unit's position.");
        _hasDestination[0] = hasDestination[0]; _hasDestination[1] = hasDestination[1];
        // rule: m4-battle-classic §1.4 (ClassicBattleRng): Classic = the fixed per-battle Lehmer generator; Game = the seeded GameRng
        _dice = p.BattleRng == ClassicBattleRng.Classic
            ? new LehmerBattleRng(p.ClassicRngSeed, p.ClassicRngWarmup)
            : new GameBattleDice(rng ?? throw new ArgumentNullException(nameof(rng), "ClassicBattleRng.Game needs the battle's GameRng."));
        // rule: m4-battle-classic §1.3 (B3 §10): the threshold is set per side by its controller before the battle
        foreach (var s in Order)
            _threshold[I(s)] = ai != null && ai[I(s)] ? (aiThreshold?[I(s)] ?? p.AiRetreatCap)
                : alert == null ? p.RetreatThreshold
                : alert[I(s)] == OnContactMode.Retreat ? p.RetreatAlertThreshold : p.FightThreshold;

        // rule: m4-battle-classic §5: shields start full (a ship's class shield, not scaled by hull; a squadron's shield_strength x squadron_size)
        foreach (var u in _units)
        {
            u.ShieldMax = u.Shield = ClassicRates.StartShield(p, u.Class, u.IsCapital);
            if (u.IsCapital) u.Hits[ObjectRegistry.HyperdriveCounter] = Math.Min(u.Hits[ObjectRegistry.HyperdriveCounter], ClassicRates.HyperdriveComponents(u.Class));
        }
        // rule: m4-battle-classic §1 (B3 §1): squadrons of the Hand aboard a carrier in the battle that have no hyperdrive start docked (state 1)
        foreach (var q in _units)
            if (!q.IsCapital && q.Side == Side.Hand && q.Carrier != 0 && q.Class.Hyperdrive == 0)
            {
                var c = _units.FirstOrDefault(x => x.IsCapital && x.Side == q.Side && x.ObjectId == q.Carrier);
                if (c != null) { q.Docked = true; q.DockedIn = c; }
            }
        foreach (var s in Order)
        {
            BuildTaskForces(s);
            foreach (var q in _units) if (q.Side == s && !q.IsCapital && !q.Docked) _squadList[I(s)].Add(q);
        }
        // rule: m4-battle-classic §8.3 (B3 §12): each side's low starts at its strength after setup
        foreach (var s in Order) _low[I(s)] = Strength(s);
    }

    public IReadOnlyList<BattleUnit> Units => _units;
    /// <summary>The side's task forces in firing order (fighter groups first, then capital task forces).</summary>
    public IReadOnlyList<ClassicTaskForce> TaskForcesOf(Side s) => _tfs[I(s)];
    /// <summary>Numbers the battle dice gave out so far.</summary>
    public long Draws => _dice.Draws;
    /// <summary>The retreat threshold of a side. rule: m4-battle-classic §1.3.</summary>
    public double ThresholdOf(Side s) => _threshold[I(s)];

    // ---------------------------------------------------------------- helpers over the live state

    private IEnumerable<BattleUnit> AliveOf(Side s) => _units.Where(u => u.Alive && u.Side == s);
    private bool HasUnits(Side s) => _units.Any(u => u.Alive && u.Side == s);
    private static bool Active(BattleUnit u) => u.Alive && !u.Docked;
    private bool IsDeathStar(BattleUnit u) => u.IsCapital && u.Class.Id == _p.DeathStarClassId;
    private bool DeathStarWorks(Side s) => s == Side.Hand && AliveOf(s).Any(IsDeathStar);

    private double TfRate(ClassicTaskForce tf, BattleUnit v)
    {
        double sum = 0;
        foreach (var m in tf.Live) sum += ClassicRates.Rate(_p, m, v, v.IsCapital);
        return sum;
    }

    // ---------------------------------------------------------------- task forces. rule: m4-battle-classic §2 (B3 §2)

    private ClassicTaskForce NewTf(Side s, List<BattleUnit> members)
    {
        var tf = new ClassicTaskForce { Id = ++_nextTf, Side = s, Members = members };
        _tfs[I(s)].Add(tf);
        return tf;
    }

    private void BuildTaskForces(Side s)
    {
        var mine = _units.Where(u => u.Side == s).ToList();
        var squads = mine.Where(u => !u.IsCapital).ToList();
        var caps = mine.Where(u => u.IsCapital).ToList();
        if (_p.TaskForces == ClassicTaskForces.PerSide)
        {
            if (mine.Count > 0) NewTf(s, squads.Concat(caps).ToList());
            return;
        }
        if (_p.TaskForces == ClassicTaskForces.PerUnit)
        {
            foreach (var u in squads.Concat(caps)) NewTf(s, new List<BattleUnit> { u });
            return;
        }
        // fighter groups (0x5ae330): one per class (class id ascending), then split the largest of 8 or more (its last count >> 1 by unit id) while a slot is free
        var groups = squads.GroupBy(q => q.Class.Id).OrderBy(g => g.Key).Select(g => g.OrderBy(q => q.Index).ToList()).ToList();
        while (groups.Count < _p.MaxFighterGroups && groups.Count > 0)
        {
            var big = groups[0];
            foreach (var g in groups) if (g.Count > big.Count) big = g;
            if (big.Count < _p.FighterGroupSplitAt) break;
            int half = big.Count >> 1;
            groups.Add(big.GetRange(big.Count - half, half));
            big.RemoveRange(big.Count - half, half);
        }
        foreach (var g in groups) NewTf(s, g);
        // capital pools (0x5c81d0), each sorted by speed + 10000 x longest range, highest first, ties by unit id
        List<BattleUnit> Pool(Func<BattleUnit, bool> f) => caps.Where(f)
            .OrderByDescending(u => ClassicRates.Speed(_p, u) + _p.RangeSortWeight * ClassicRates.LongestRange(_p, u)).ThenBy(u => u.Index).ToList();
        var p98 = Pool(u => ClassicRates.IsArmed(_p, u) && !ClassicRates.HasLaser(_p, u));
        var pa0 = Pool(u => ClassicRates.HasLaser(_p, u));
        var pa8 = Pool(u => !ClassicRates.IsArmed(_p, u));
        int n1 = Builder(s, p98, 0, 3);
        int n2 = Builder(s, pa0, n1, n1 + 3);
        Builder(s, pa8, n1 + n2, 7);
    }

    /// <summary>The builder 0x5d1240(pool, lo, hi): about √N task forces, at most hi - lo + 1. rule: m4-battle-classic §2.</summary>
    private int Builder(Side s, List<BattleUnit> pool, int lo, int hi)
    {
        int n = pool.Count;
        if (n == 0) return 0;
        int k = n <= 1 ? 1 : Math.Min(IntSqrt(n), hi);
        k = Math.Min(k, hi - lo + 1);
        if (k <= 0) return 0;
        int per = n / k;
        var tfs = new List<List<BattleUnit>>();
        for (int i = 0; i < n; i++)
        {
            if (i % per == 0 && tfs.Count < k) tfs.Add(new List<BattleUnit> { pool[i] });
            else tfs[^1].Add(pool[i]);
        }
        foreach (var m in tfs) NewTf(s, m);
        return k;
    }

    private static int IntSqrt(int n)
    {
        int r = 0;
        while ((r + 1) * (r + 1) <= n) r++;
        return r;
    }

    // ---------------------------------------------------------------- strength and retreat. rule: m4-battle-classic §8 (B3 §10)

    /// <summary>
    /// Strength (0x5d0ce0): the sum of F over the side's capital ships, except a ship some enemy task force aimed at it out-shoots (its rate alone is at
    /// least the ship's recharge), plus all its squadrons (docked ones too). rule: m4-battle-classic §8.2.
    /// </summary>
    public double Strength(Side side)
    {
        double sum = 0;
        var foe = _tfs[I(Other(side))];
        foreach (var u in _units)
        {
            if (!u.Alive || u.Side != side) continue;
            if (u.IsCapital)
            {
                if (u.Docked) continue;
                double rech = ClassicRates.Recharge(_p, u);
                bool outShot = false;
                foreach (var tf in foe)
                    if (tf.Target == u && tf.Live.Any() && rech <= TfRate(tf, u)) { outShot = true; break; }
                if (outShot) continue;
            }
            sum += ClassicRates.F(_p, u);
        }
        return sum;
    }

    /// <summary>The test canRetreat (0x5d0f50): null = the side can; otherwise the first failing reason. rule: m4-retreat-damage-repair §1.</summary>
    public RetreatRefusal? CanRetreat(Side side)
    {
        if (_retreated[I(side)]) return RetreatRefusal.NoDestination; // already done (never asked in practice)
        // no enemy capital ship in the battle with the gravity-well flag (a static class flag)
        if (AliveOf(Other(side)).Any(u => u.IsCapital && u.Class.GravityWellProjector > 0)) return RetreatRefusal.GravityWell;
        // some own unit can jump, or the Hand has a working Death Star
        bool able = AliveOf(side).Any(u => !u.Docked && ClassicRates.CanJump(_p, u)) || DeathStarWorks(side);
        if (!able) return RetreatRefusal.NoHyperdrive;
        if (!_hasDestination[I(side)]) return RetreatRefusal.NoDestination; // ours: a destination exists (m4-retreat-damage-repair §1 (5))
        return null;
    }

    /// <summary>The retreat test of a side's step (0x5d19a0); a refused retreat is reported once per battle. rule: m4-battle-classic §8.4.</summary>
    private bool WantsRetreat(Side side)
    {
        bool want = _forced[I(side)] || ClassicRates.RetreatWanted(Strength(side), Strength(Other(side)), _threshold[I(side)]);
        if (!want) return false;
        if (CanRetreat(side) is { } r) { Refuse(side, r); return false; }
        return true;
    }

    private void Refuse(Side side, RetreatRefusal reason)
    {
        if (_refusalReported) return;
        _refusalReported = true; // once per battle, at the first refusal. rule: m4-retreat-damage-repair §1
        _steps.Add(new RetreatRefusedStep(side, reason));
    }

    // ---------------------------------------------------------------- targeting. rule: m4-battle-classic §5, §6 (B3 §8, §9)

    private bool Eligible(BattleUnit v) => Active(v) && ClassicRates.BaseRate(_p, v) != 0;

    /// <summary>value(v) = 15 F - 0.01 rechargeRatio - 0.01 hullRatio + 1 per passenger - 0.01 if docked. rule: m4-battle-classic §5.</summary>
    public double Value(BattleUnit v)
    {
        double rr = v.IsCapital ? (v.Class.ShieldRechargeRate != 0 ? ClassicRates.Recharge(_p, v) / v.Class.ShieldRechargeRate : 0.0) : 1.0;
        return _p.PriorityFirepowerWeight * ClassicRates.F(_p, v) - _p.PriorityShieldWeight * rr - _p.PriorityHullWeight * v.HullRatio
            + _p.PriorityPassengerWeight * v.Passengers - (v.Docked ? _p.PriorityDockedPenalty : 0);
    }

    /// <summary>The ranked list (0x5d1a70): eligible enemy units by value (ties lowest unit id), then the ineligible ones in their own value order. rule: m4-battle-classic §5.</summary>
    public List<BattleUnit> Ranked(Side side)
    {
        var cands = _units.Where(u => u.Alive && u.Side == Other(side)).Select(u => (U: u, V: Value(u), E: Eligible(u))).ToList();
        return cands.Where(c => c.E).OrderByDescending(c => c.V).ThenBy(c => c.U.Index)
            .Concat(cands.Where(c => !c.E).OrderByDescending(c => c.V).ThenBy(c => c.U.Index)).Select(c => c.U).ToList();
    }

    /// <summary>The two lists (0x5c9200): L = squadrons then capital ships; T = capital ships, squadrons, capital ships. rule: m4-battle-classic §5.</summary>
    public (List<BattleUnit> L, List<BattleUnit> T) Lists(Side side)
    {
        var rank = Ranked(side);
        var sq = rank.Where(u => !u.IsCapital).ToList();
        var cap = rank.Where(u => u.IsCapital).ToList();
        var l = sq.Concat(cap).ToList();
        var t = cap.Concat(l).ToList();
        return (l, t);
    }

    /// <summary>Kill time (0x5ed170): max(1, shield + 0.65 hull) / (acc - recharge), or -1 when that is not positive. rule: m4-battle-classic §6.</summary>
    private double KillTime(BattleUnit v, double acc)
    {
        double eh = Math.Max(1.0, v.Shield + _p.EffectiveHullFactor * v.Hull);
        double net = acc - ClassicRates.Recharge(_p, v);
        return net <= 0 ? -1.0 : eh / net;
    }

    /// <summary>Sticky target (0x5eda50). rule: m4-battle-classic §6.</summary>
    private bool Keep(Side side, ClassicTaskForce tf)
    {
        if (tf.Members.Any(m => m.Alive && m.Docked)) return true;
        var v = tf.Target;
        if (v == null || !Eligible(v)) return false;
        double t = KillTime(v, TfRate(tf, v));
        if (t != -1 && t < _p.StickyKillTime) return true;
        double acc = 0;
        foreach (var o in _tfs[I(side)]) if (o.Target == v && o.Live.Any()) acc += TfRate(o, v);
        t = KillTime(v, acc);
        return t != -1 && t < _p.StickyKillTime;
    }

    private void Assign(Side side)
    {
        var (l, t) = Lists(side);
        var live = _tfs[I(side)].Where(tf => tf.Live.Any()).ToList();
        var keep = live.Select(tf => Keep(side, tf)).ToList(); // all keep tests before any target is cleared (DEFAULT)
        var poolA = new List<ClassicTaskForce>(); var poolB = new List<ClassicTaskForce>();
        for (int i = 0; i < live.Count; i++)
        {
            if (keep[i]) continue;
            var tf = live[i];
            tf.Target = null;
            (ClassicRates.HasLaser(_p, tf.Live.First()) ? poolB : poolA).Add(tf); // pool by the first member's laser
        }
        if (_p.Tactic == BattleTactic.Surround)
        {
            foreach (var (pool, list) in new[] { (poolA, t), (poolB, l) })
                if (list.Count > 0) foreach (var tf in pool) tf.Target = list.FirstOrDefault(Eligible) ?? list[0];
            return;
        }
        StandOff(poolA, t);
        StandOff(poolB, l);
    }

    /// <summary>Stand Off (0x5ecb90). rule: m4-battle-classic §6 (B3 §9).</summary>
    internal void StandOff(List<ClassicTaskForce> pool, List<BattleUnit> list)
    {
        var free = new List<ClassicTaskForce>(pool);
        foreach (var v in list)
        {
            if (!Eligible(v)) continue;
            if (free.Count == 0) return;
            var keyed = free.Select(tf =>
            {
                double r = TfRate(tf, v), kt = KillTime(v, r);
                return (Key: kt >= 0 && kt <= _p.KillTimeWindow ? kt : _p.StandOffFar - r, Tf: tf, R: r);
            }).OrderBy(x => x.Key).ThenBy(x => x.Tf.Id).ToList();
            if (keyed.Count == 1) { keyed[0].Tf.Target = v; free.Remove(keyed[0].Tf); return; } // one free task force: commit it and end the pass
            double acc = keyed[0].R;
            for (int j = keyed.Count - 1; j >= 1; j--) // from the worst up; the best is counted but never committed this way
            {
                acc += keyed[j].R;
                double kt = KillTime(v, acc);
                if (kt >= 0 && kt <= _p.KillTimeWindow)
                {
                    for (int x = j; x < keyed.Count; x++) { keyed[x].Tf.Target = v; free.Remove(keyed[x].Tf); }
                    break;
                }
            }
        }
        if (free.Count > 0 && list.Count > 0)
        {
            var first = list.FirstOrDefault(Eligible) ?? list[0];
            foreach (var tf in free) tf.Target = first;
        }
    }

    // ---------------------------------------------------------------- hits. rule: m4-battle-classic §6, §7 (B3 §6)

    /// <summary>
    /// Apply one hit of damage d (0x5b54d0): the generator roll on the current shield (rand(0..100) &lt;= max(1, 100 d / s0)), then, when the shield breaks,
    /// hull loss max(d - s0, 1) and the hull roll rand(0..100) - percent of hull lost (drawn also on a kill). No draw at hull 0. rule: m4-battle-classic §6.
    /// </summary>
    internal void ApplyHit(BattleUnit v, double d)
    {
        double h0 = v.Hull;
        if (h0 <= 0) return;
        double s0 = v.Shield;
        v.Shield = s0 - d;
        if (s0 > 0 && _p.SubsystemRolls)
        {
            double pct = Math.Max(_p.ShieldGeneratorMinPercent, 100.0 * d / s0);
            if (_dice.Rand(_p.RollSides) <= pct) Bump(v, 0);
        }
        if (v.Shield >= 0) return;
        double loss = Math.Max(d - s0, 1.0);
        v.Shield = 0;
        v.Hull = Math.Max(0.0, h0 - loss);
        if (!_p.SubsystemRolls) return;
        double score = _dice.Rand(_p.RollSides) - 100.0 * (h0 - v.Hull) / h0;
        if (score <= _p.SubsystemScoreAbove) return;
        int idx = score <= _p.ShieldGeneratorUpTo ? 0 : score <= _p.WeaponsUpTo ? 1 : score <= _p.TractorUpTo ? 2 : score <= _p.SubLightUpTo ? 3 : ObjectRegistry.HyperdriveCounter;
        Bump(v, idx);
    }

    /// <summary>One more hit on a counter: the four plain ones stop at 4, the hyperdrive at its installed components (0x5b1970). rule: m4-battle-classic §7.</summary>
    internal void Bump(BattleUnit v, int idx)
    {
        int cap = idx == ObjectRegistry.HyperdriveCounter ? ClassicRates.HyperdriveComponents(v.Class) : _p.SubsystemCap;
        if (v.Hits[idx] < cap) v.Hits[idx]++;
    }

    private void Kill(BattleUnit u, UnitLossReason why)
    {
        u.Alive = false; u.Left = false; u.Loss = why; u.LostAt = _second;
        _steps.Add(new UnitDestroyedStep(u, _second, why));
        if (u.IsCapital) // everything docked in it dies with it
            foreach (var q in _units)
                if (q.Alive && q.Docked && q.DockedIn == u) Kill(q, UnitLossReason.ShipLost);
    }

    // ---------------------------------------------------------------- one side's step. rule: m4-battle-classic §3 (B3 §3, 0x5d14d0)

    private void Launch(Side side)
    {
        if (side != Side.Hand) return;
        foreach (var q in _units)
            if (q.Side == side && q.Alive && q.Docked && !q.IsCapital) { q.Docked = false; q.DockedIn = null; _squadList[I(side)].Add(q); }
    }

    private void Fire(Side side)
    {
        foreach (var tf in _tfs[I(side)])
        {
            var v = tf.Target;
            if (v == null || !Active(v) || !tf.Live.Any()) continue; // a task force whose target is gone does nothing and draws nothing
            ApplyHit(v, TfRate(tf, v));
            if (v.Hull <= 0) Kill(v, UnitLossReason.Combat);
        }
    }

    /// <summary>Recharge (0x5b6320) of the side's units still in the battle. rule: m4-battle-classic §7.</summary>
    private void Recharge(Side side)
    {
        foreach (var u in _units)
        {
            if (!u.Alive || u.Side != side) continue;
            if (u.IsCapital)
            {
                if (u.Shield < u.ShieldMax) u.Shield = Math.Min(u.ShieldMax, u.Shield + ClassicRates.Recharge(_p, u));
            }
            else
            {
                u.ShieldMax *= u.HullRatio; // the squadron's cap shrinks again every second (it compounds)
                u.Shield = Math.Min(u.Shield, u.ShieldMax);
                u.Shield = Math.Min(u.ShieldMax, u.Shield + ClassicRates.Recharge(_p, u));
            }
        }
    }

    internal void RechargeSide(Side side) => Recharge(side);

    /// <summary>Returns true when the side has no unit left in the battle.</summary>
    private bool Step(Side side)
    {
        if (!HasUnits(side)) return true;
        Launch(side);
        Assign(side);
        Fire(side);
        if (WantsRetreat(side)) ExecuteRetreat(side);
        Recharge(side);
        return !HasUnits(side);
    }

    /// <summary>Switch ClassicFireOrder = Simultaneous: both sides aim and fire from the same state, then retreat tests, then recharge.</summary>
    private (bool E, bool A) StepBoth()
    {
        bool hasE = HasUnits(Side.Hand), hasA = HasUnits(Side.Collective);
        Launch(Side.Hand);
        foreach (var s in Order) if (HasUnits(s)) Assign(s);
        var shots = new List<(BattleUnit V, double D)>();
        foreach (var s in Order)
            foreach (var tf in _tfs[I(s)])
                if (tf.Target is { } v && Active(v) && tf.Live.Any()) shots.Add((v, TfRate(tf, v)));
        foreach (var (v, d) in shots)
        {
            if (!Active(v)) continue;
            ApplyHit(v, d);
            if (v.Hull <= 0) Kill(v, UnitLossReason.Combat);
        }
        var want = Order.Where(s => HasUnits(s) && WantsRetreat(s)).ToList();
        foreach (var s in want) ExecuteRetreat(s);
        foreach (var s in Order) Recharge(s);
        return (!hasE || !HasUnits(Side.Hand), !hasA || !HasUnits(Side.Collective));
    }

    // ---------------------------------------------------------------- retreat execution. rule: m4-retreat-damage-repair §2 (B3 §11)

    private bool HasRoom(BattleUnit ship) =>
        _units.Count(q => q.Alive && q.Docked && q.DockedIn == ship) < ship.Class.FighterCapacity;

    /// <summary>
    /// Squadrons first, in the side's list order: one that can jump leaves; else it docks in its own carrier still in the battle (its hyperdrive is not
    /// checked), else in the first own capital ship with room, else (Hand) in the Death Star; no room: destroyed. Then the Death Star leaves; then
    /// capital ships that can jump leave and the others are destroyed with everything docked in them. rule: m4-retreat-damage-repair §2.
    /// </summary>
    private void ExecuteRetreat(Side side)
    {
        _retreated[I(side)] = true;
        foreach (var q in _squadList[I(side)].ToList())
        {
            if (!q.Alive || q.Docked) continue;
            if (ClassicRates.CanJump(_p, q)) { q.Alive = false; q.Left = true; q.JumpedAlone = true; continue; }
            BattleUnit? ship = null;
            var own = q.Carrier != 0 ? _units.FirstOrDefault(c => c.IsCapital && c.Side == side && c.ObjectId == q.Carrier && Active(c)) : null;
            if (own != null && HasRoom(own)) ship = own;
            ship ??= _units.FirstOrDefault(c => c.IsCapital && c.Side == side && Active(c) && !IsDeathStar(c) && HasRoom(c));
            if (ship == null && side == Side.Hand) ship = _units.FirstOrDefault(c => Active(c) && c.Side == side && IsDeathStar(c) && HasRoom(c));
            if (ship == null) { Kill(q, UnitLossReason.NoBerth); continue; }
            q.Docked = true; q.DockedIn = ship;
        }
        // squadrons docked from the start that never launched (none in practice) stay docked in their carrier
        foreach (var ds in _units.Where(u => u.Side == side && Active(u) && IsDeathStar(u)).ToList()) Leave(ds);
        foreach (var c in _units.Where(u => u.Side == side && u.IsCapital && Active(u)).ToList())
        {
            if (ClassicRates.CanJump(_p, c)) Leave(c);
            else Kill(c, UnitLossReason.NoHyperdrive);
        }
        _steps.Add(new SideRetreatedStep(side, _second));
    }

    private void Leave(BattleUnit c)
    {
        c.Alive = false; c.Left = true;
        foreach (var q in _units)
            if (q.Alive && q.Docked && q.DockedIn == c) { q.Alive = false; q.Left = true; q.NewCarrier = c.ObjectId; }
    }

    // ---------------------------------------------------------------- the battle. rule: m4-battle-classic §3 (B3 §3, §12, 0x5ae9c0)

    /// <summary>Run one second. Returns true when the battle is over.</summary>
    internal bool Tick()
    {
        _second++;
        bool overE, overA;
        if (_p.FireOrder == ClassicFireOrder.HandFirst)
        {
            overE = Step(Side.Hand);   // the Hand's step, then the Collective's
            overA = Step(Side.Collective);
        }
        else (overE, overA) = StepBoth();
        double[] str = new double[2];
        foreach (var s in Order) str[I(s)] = Strength(s);
        foreach (var s in Order)
        {
            if (str[I(s)] < _low[I(s)]) { _low[I(s)] = str[I(s)]; _timer[I(s)] = 0; }
            else _timer[I(s)]++;
        }
        if (overE || overA || !HasUnits(Side.Collective) || !HasUnits(Side.Hand)) return true;
        bool guard = _second >= _p.SafetyGuardSeconds;
        if ((_timer[0] >= _p.StalemateSeconds && _timer[1] >= _p.StalemateSeconds) || guard)
        {
            // rule: m4-battle-classic §8.3; m4-retreat-damage-repair §2 (DeathStarStalemate: the Collective's stalemate strength is -1 while the Hand's Death Star works)
            double a = str[I(Side.Collective)], e = str[I(Side.Hand)];
            if (_p.DeathStarStalemate && DeathStarWorks(Side.Hand)) a = -1;
            var flagged = new List<Side>();
            if (a >= e) flagged.Add(Side.Hand);
            if (a <= e) flagged.Add(Side.Collective);
            var can = flagged.Select(CanRetreat).ToList(); // both read before either changes
            for (int k = 0; k < flagged.Count; k++)
            {
                if (can[k] is null) { _forced[I(flagged[k])] = true; continue; } // retreats in its next step
                Refuse(flagged[k], can[k]!.Value);
                foreach (var u in AliveOf(flagged[k]).ToList()) Kill(u, UnitLossReason.Stalemate);
            }
            if (!HasUnits(Side.Collective) || !HasUnits(Side.Hand)) return true;
            if (guard) // the engineering guard ends the battle now: the forced sides leave
            {
                foreach (var s in Order) if (_forced[I(s)] && HasUnits(s)) ExecuteRetreat(s);
                return true;
            }
        }
        return false;
    }

    /// <summary>Run the battle to its end and return the result. The units keep their final state.</summary>
    public ClassicBattleResult Run()
    {
        if (HasUnits(Side.Collective) && HasUnits(Side.Hand))
            while (!Tick()) { }
        return Finish();
    }

    private ClassicBattleResult Finish()
    {
        bool aliveA = HasUnits(Side.Collective), aliveE = HasUnits(Side.Hand);
        BattleOutcome Outcome(Side s, bool alive, bool otherAlive) =>
            alive && !otherAlive ? BattleOutcome.Victorious
            : !alive && otherAlive ? (_retreated[I(s)] ? BattleOutcome.Retreated : BattleOutcome.Annihilated)
            : BattleOutcome.Indecisive;
        int Lost(Side s) => _units.Count(u => u.Side == s && u.Destroyed);
        int Survived(Side s) => _units.Count(u => u.Side == s && !u.Destroyed);
        return new ClassicBattleResult
        {
            Seconds = _second,
            CollectiveOutcome = Outcome(Side.Collective, aliveA, aliveE),
            HandOutcome = Outcome(Side.Hand, aliveE, aliveA),
            CollectiveLosses = Lost(Side.Collective), HandLosses = Lost(Side.Hand),
            CollectiveSurvivors = Survived(Side.Collective), HandSurvivors = Survived(Side.Hand),
            CollectiveRetreated = _retreated[I(Side.Collective)], HandRetreated = _retreated[I(Side.Hand)],
            Draws = _dice.Draws,
            Steps = _steps,
        };
    }
}
