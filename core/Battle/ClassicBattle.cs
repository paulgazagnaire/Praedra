using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Battle;

/// <summary>
/// One unit of a Classic battle: a capital ship or a squadron, a plain copy of the registry object's battle state. The battle never touches
/// the registry; <see cref="BattleModule"/> builds the units and writes the result back. Hull and shield are doubles (DEFAULT, m4-battle-classic
/// Open question 10). rule: m4-battle-classic §2 (units), §7.
/// </summary>
public sealed class BattleUnit
{
    /// <summary>Position in the force: the unit id of the sheet (Alliance first, then Empire; ships by id, then squadrons by id).</summary>
    public int Index { get; set; }
    /// <summary>The registry object id (0 for a synthetic unit).</summary>
    public int ObjectId { get; set; }
    public Side Side { get; set; }
    public ShipClass Class { get; set; } = null!;
    public bool IsCapital { get; set; }
    public double Hull { get; set; }
    /// <summary>Class hull of a ship; the squadron hull constant of a squadron.</summary>
    public double HullMax { get; set; }
    public double Shield { get; set; }
    /// <summary>The six counters (shield generator, weapons, tractor, sub-light, hyperdrive, damage control). Squadrons have none.</summary>
    public int[] Hits { get; } = new int[ObjectRegistry.HitCounters];
    /// <summary>Squadron aboard a ship: that ship's object id; 0 = based (or its carrier died: the berth is lost).</summary>
    public int Carrier { get; set; }
    /// <summary>Still in the battle: not destroyed and not gone.</summary>
    public bool Alive { get; set; } = true;
    /// <summary>The side retreated and this unit jumped (it survived).</summary>
    public bool Left { get; set; }
    /// <summary>Retreat: a squadron that found a free berth on this ship (object id), or 0.</summary>
    public int NewCarrier { get; set; }
    /// <summary>Retreat: a squadron with a hyperdrive that jumped on its own (it needs no berth, and is based at the destination).</summary>
    public bool JumpedAlone { get; set; }
    /// <summary>Why the unit was destroyed (set with <see cref="Alive"/> = false and <see cref="Left"/> = false).</summary>
    public UnitLossReason? Loss { get; set; }
    public int LostAt { get; set; }
    public bool Destroyed => !Alive && !Left;

    /// <summary>Hull ratio; 0 for a unit without hull.</summary>
    public double HullRatio => HullMax <= 0 ? 0 : Hull / HullMax;
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

/// <summary>The result of one Classic battle. Index 0 of each pair is the Alliance, 1 the Empire (the sheet's side order).</summary>
public sealed class ClassicBattleResult
{
    public int Seconds { get; init; }
    public BattleOutcome AllianceOutcome { get; init; }
    public BattleOutcome EmpireOutcome { get; init; }
    public int AllianceLosses { get; init; }
    public int EmpireLosses { get; init; }
    public int AllianceSurvivors { get; init; }
    public int EmpireSurvivors { get; init; }
    public bool AllianceRetreated { get; init; }
    public bool EmpireRetreated { get; init; }
    /// <summary>Numbers the battle generator gave out (the game generator is used once, for the seed).</summary>
    public long Draws { get; init; }
    public IReadOnlyList<BattleStep> Steps { get; init; } = Array.Empty<BattleStep>();

    public BattleOutcome OutcomeOf(Side s) => s == Side.Alliance ? AllianceOutcome : EmpireOutcome;
    public bool Retreated(Side s) => s == Side.Alliance ? AllianceRetreated : EmpireRetreated;
}

/// <summary>
/// The damage formulas of the Classic battle: pure functions of class data and hull ratio, so tests (BC1 to BC4) and the importer-era data can
/// check them one by one. rule: m4-battle-classic §4, §5.
/// </summary>
public static class ClassicRates
{
    /// <summary>
    /// The best arc of a capital ship against a capital or a squadron: largest f, first in fore, aft, port, starboard on a tie. Returns (f, N);
    /// N = 0 when no arc has f above 0. rule: m4-battle-classic §4.
    /// </summary>
    public static (double F, int N) BestArc(BattleParams p, ShipClass c, bool vsCapital)
    {
        double bestF = 0; int bestN = 0;
        var arcs = c.GunArcs;
        for (int a = 0; a < ShipArcs.Count; a++)
        {
            double f = ArcF(p, arcs[a], vsCapital);
            if (f > bestF) { bestF = f; bestN = arcs[a].Total; }
        }
        return bestF > 0 && bestN > 0 ? (bestF, bestN) : (0, 0);
    }

    private static double ArcF(BattleParams p, ArcCounts a, bool vsCapital) => vsCapital
        ? a.Laser * p.LaserVsCapital + a.Ion * p.IonVsCapital + a.Turbolaser * p.TurbolaserVsCapital
        : a.Laser * p.LaserVsSquadron + a.Ion * p.IonVsSquadron + a.Turbolaser * p.TurbolaserVsSquadron;

    /// <summary>1 - 0.25 x hits, floor 0. rule: m4-battle-classic §7 (effects in the loop).</summary>
    public static double PenaltyFactor(BattleParams p, int hits) => Math.Max(0.0, 1.0 - p.HitPenalty * hits);

    /// <summary>
    /// Rate of a capital ship against a capital ship: weapon_recharge_rate x 3.75 x C x (f / N) x hull ratio x weapon factor. Gun counts cancel (Traced, 0x5b7780).
    /// rule: m4-battle-classic §4.
    /// </summary>
    public static double CapitalVsCapital(BattleParams p, ShipClass c, double hullRatio = 1.0, int weaponHits = 0)
    {
        var (f, n) = BestArc(p, c, true);
        if (n == 0) return 0;
        return c.WeaponRechargeRate * (p.WeaponMultiplier * p.FamilyCoefficient) * (f / n) * hullRatio * PenaltyFactor(p, weaponHits);
    }

    /// <summary>
    /// Rate of a capital ship against a squadron: as above times max(maneuverability(u) / maneuverability(v), 0.1). rule: m4-battle-classic §4.
    /// </summary>
    public static double CapitalVsSquadron(BattleParams p, ShipClass c, ShipClass target, double hullRatio = 1.0, int weaponHits = 0)
    {
        var (f, n) = BestArc(p, c, false);
        if (n == 0) return 0;
        double ratio = Math.Max((double)c.Maneuverability / Math.Max(target.Maneuverability, 1), p.ManeuverFloor);
        return c.WeaponRechargeRate * (p.WeaponMultiplier * p.FamilyCoefficient) * (f / n) * hullRatio * PenaltyFactor(p, weaponHits) * ratio;
    }

    /// <summary>
    /// Rate of a squadron: R x hull ratio x (C x f + torpedo term) / N over its fore arc; the torpedo term only against a capital ship at shield 0.
    /// Squadron weapon hits do not exist (DEFAULT). rule: m4-battle-classic §4, §9.
    /// </summary>
    public static double Squadron(BattleParams p, ShipClass c, bool targetIsCapital, bool targetShieldZero, double hullRatio = 1.0)
    {
        var fore = c.GunArcs.Fore;
        int n = fore.Total;
        if (n == 0) return 0;
        double f = ArcF(p, fore, targetIsCapital);
        double torp = targetIsCapital && targetShieldZero ? c.Torpedoes * p.TorpedoTerm : 0;
        return p.SquadronRate * hullRatio * (p.FamilyCoefficient * f + torp) / n;
    }

    /// <summary>Static rate of a class against a capital ship (full health, shield up, no torpedoes): the F of the target priority and the strength term. rule: m4-battle-classic §5, §8.</summary>
    public static double ClassRateVsCapital(BattleParams p, ShipClass c) =>
        c.IsCapital ? CapitalVsCapital(p, c) : Squadron(p, c, true, false);

    /// <summary>Shield recharge per second of a capital ship: shield_recharge_rate x (1 - 0.25 x hits) x hull ratio, floor 0; of a squadron 0.3 x hull ratio. rule: m4-battle-classic §3.5.</summary>
    public static double Recharge(BattleParams p, ShipClass c, double hullRatio, int shieldHits) =>
        c.IsCapital ? Math.Max(0.0, c.ShieldRechargeRate * PenaltyFactor(p, shieldHits) * hullRatio) : Math.Max(0.0, p.SquadronShieldRecharge * hullRatio);

    /// <summary>The shield cap: shield_strength x hull ratio. rule: m4-battle-classic §3.5.</summary>
    public static double ShieldCap(ShipClass c, double hullRatio) => c.ShieldStrength * hullRatio;

    /// <summary>Installed hyperdrive components: one for each non-zero value of hyperdrive and hyperdrive_if_damaged. rule: m4-retreat-damage-repair §1 (Lead).</summary>
    public static int HyperdriveComponents(ShipClass c) => (c.Hyperdrive > 0 ? 1 : 0) + (c.HyperdriveIfDamaged > 0 ? 1 : 0);

    /// <summary>True when a side with this strength against that one is flagged: own is 0, or own / enemy is below the threshold. rule: m4-battle-classic §8.4.</summary>
    public static bool MoraleFlagged(BattleParams p, double own, double enemy) => own <= 0 || (enemy > 0 && own / enemy < p.RetreatThreshold);
}

/// <summary>
/// The Classic space battle (the traced 1998 Simulate), one second at a time, on plain units and its own generator. Nothing moves; the
/// retreat destination is decided by the caller (it only says whether each side has one). rule: m4-battle-classic (the whole sheet);
/// m4-retreat-damage-repair §1, §2.
/// </summary>
public sealed class ClassicBattle
{
    private readonly BattleParams _p;
    private readonly List<BattleUnit> _units;
    private readonly GameRng _rng;
    private readonly bool[] _hasDestination = new bool[2];   // by (int)Side
    private readonly OnContactMode[] _alert = new OnContactMode[2];
    private readonly List<BattleStep> _steps = new();
    private readonly bool[] _retreated = new bool[2];
    private bool _refusalReported;

    // Side order of the sheet: Alliance before Empire.
    private static readonly Side[] Order = { Side.Alliance, Side.Empire };
    private static int I(Side s) => (int)s;
    private static Side Other(Side s) => s == Side.Alliance ? Side.Empire : Side.Alliance;

    // per-second tables, indexed by unit index
    private double[] _rCap = Array.Empty<double>(), _rTorp = Array.Empty<double>(), _rSq = Array.Empty<double>();
    private double[] _recharge = Array.Empty<double>(), _f = Array.Empty<double>();
    private bool[] _shieldZero = Array.Empty<bool>();

    /// <param name="units">All units, Alliance first, each side ships by id then squadrons by id; <see cref="BattleUnit.Index"/> must equal the position.</param>
    /// <param name="hasDestination">Per side (indexed by <c>(int)Side</c>): does a valid retreat system exist? rule: m4-retreat-damage-repair §1 (5).</param>
    /// <param name="alert">Per side: OnContact. rule: m4-battle-classic §1.3.</param>
    public ClassicBattle(BattleParams p, IReadOnlyList<BattleUnit> units, GameRng rng, bool[] hasDestination, OnContactMode[]? alert = null)
    {
        _p = p; _units = units.ToList(); _rng = rng;
        for (int i = 0; i < _units.Count; i++)
            if (_units[i].Index != i) throw new ArgumentException("Unit.Index must equal the unit's position.");
        _hasDestination[0] = hasDestination[0]; _hasDestination[1] = hasDestination[1];
        if (alert != null) { _alert[0] = alert[0]; _alert[1] = alert[1]; }
    }

    public IReadOnlyList<BattleUnit> Units => _units;

    // ---------------------------------------------------------------- helpers over the live state

    private IEnumerable<BattleUnit> AliveOf(Side s) => _units.Where(u => u.Alive && u.Side == s);
    private bool HasUnits(Side s) => _units.Any(u => u.Alive && u.Side == s);

    /// <summary>A working hyperdrive. rule: m4-retreat-damage-repair §1 (3).</summary>
    private bool WorkingHyperdrive(BattleUnit u) => u.IsCapital
        ? u.Class.Hyperdrive > 0 && ClassicRates.HyperdriveComponents(u.Class) - u.Hits[ObjectRegistry.HyperdriveCounter] > 0
        : u.Class.Hyperdrive > 0;

    private bool IsDeathStar(BattleUnit u) => u.IsCapital && u.Class.Id == _p.DeathStarClassId;

    /// <summary>The test CanRetreat: null = the side can; otherwise the first failing reason (checked in the order 2, 3, 5). rule: m4-retreat-damage-repair §1.</summary>
    private RetreatRefusal? CanRetreat(Side side)
    {
        if (_retreated[I(side)]) return RetreatRefusal.NoDestination; // 1. already ended (never asked in practice)
        // 2. no live enemy capital ship with a gravity well projector
        if (AliveOf(Other(side)).Any(u => u.IsCapital && u.Class.GravityWellProjector > 0)) return RetreatRefusal.GravityWell;
        // 3 and 4. a working hyperdrive, or a live Death Star of the Empire side
        bool able = AliveOf(side).Any(WorkingHyperdrive) || (side == Side.Empire && AliveOf(side).Any(IsDeathStar));
        if (!able) return RetreatRefusal.NoHyperdrive;
        // 5. a destination exists
        if (!_hasDestination[I(side)]) return RetreatRefusal.NoDestination;
        return null;
    }

    /// <summary>Strength of a side: the sum of its units' rate against a capital ship (shield up, no torpedo) at current hull and weapon hits. rule: m4-battle-classic §8.2.</summary>
    public double Strength(Side side)
    {
        double sum = 0;
        foreach (var u in AliveOf(side)) sum += RateVsCapital(u);
        return sum;
    }

    private double RateVsCapital(BattleUnit u) => u.IsCapital
        ? ClassicRates.CapitalVsCapital(_p, u.Class, u.HullRatio, u.Hits[1])
        : ClassicRates.Squadron(_p, u.Class, true, false, u.HullRatio);

    // ---------------------------------------------------------------- the battle

    /// <summary>Run the battle to its end and return the result. The units keep their final state.</summary>
    public ClassicBattleResult Run()
    {
        int second = 0;
        bool ended = false;

        // rule: m4-battle-classic §1.3 (alert): a side set to Retreat leaves at second 0 if it can; the larger side id goes first.
        foreach (var side in new[] { Side.Alliance, Side.Empire })
        {
            if (_alert[I(side)] != OnContactMode.Retreat || !HasUnits(side) || !HasUnits(Other(side))) continue;
            var refusal = CanRetreat(side);
            if (refusal is { } r) { Refuse(side, r); continue; }
            ExecuteRetreat(side, 0);
            ended = true;
        }

        double[] low = new double[2]; int[] since = new int[2];
        while (!ended)
        {
            second++;
            Rates();
            var hits = Assign();
            Fire(hits);
            Remove(second);
            Recharge();

            // 1. annihilation
            if (!HasUnits(Side.Alliance) || !HasUnits(Side.Empire)) break;
            // 2. strengths
            double[] str = { Strength(Side.Empire), Strength(Side.Alliance) }; // by (int)Side
            // 3. stalemate timers
            for (int s = 0; s < 2; s++)
            {
                if (second == 1 || str[s] < low[s]) { low[s] = str[s]; since[s] = 0; }
                else since[s]++;
            }
            bool guard = second >= _p.SafetyGuardSeconds;
            if ((since[0] >= _p.StalemateSeconds && since[1] >= _p.StalemateSeconds) || guard)
            {
                ended = ResolveStalemate(str, second);
                if (ended) break;
            }
            // 4. morale
            var flagged = Order.Where(s => ClassicRates.MoraleFlagged(_p, str[I(s)], str[I(Other(s))])).ToList();
            if (flagged.Count > 0)
            {
                // Abilities are read once, before either side moves (DEFAULT: nothing traced for two sides flagged in one second).
                var can = flagged.Select(s => CanRetreat(s)).ToList();
                for (int k = 0; k < flagged.Count; k++)
                {
                    if (can[k] is { } r) { Refuse(flagged[k], r); continue; }
                    ExecuteRetreat(flagged[k], second);
                    ended = true;
                }
                if (ended) break;
            }
        }
        return Finish(second);
    }

    private void Refuse(Side side, RetreatRefusal reason)
    {
        if (_refusalReported) return;
        _refusalReported = true; // once per battle, at the first refusal. rule: m4-retreat-damage-repair §1
        _steps.Add(new RetreatRefusedStep(side, reason));
    }

    // ---------------------------------------------------------------- one second: rates

    private void Rates()
    {
        int n = _units.Count;
        if (_rCap.Length != n)
        {
            _rCap = new double[n]; _rTorp = new double[n]; _rSq = new double[n]; _recharge = new double[n]; _f = new double[n]; _shieldZero = new bool[n];
        }
        foreach (var u in _units)
        {
            int i = u.Index;
            _f[i] = 0; _rCap[i] = _rTorp[i] = _rSq[i] = 0; _recharge[i] = 0;
            _shieldZero[i] = u.Shield <= 0;
            if (!u.Alive) continue;
            double hr = u.HullRatio;
            if (u.IsCapital)
            {
                int wh = u.Hits[1];
                _rCap[i] = _rTorp[i] = ClassicRates.CapitalVsCapital(_p, u.Class, hr, wh);
                _f[i] = ClassicRates.CapitalVsCapital(_p, u.Class);
            }
            else
            {
                _rCap[i] = ClassicRates.Squadron(_p, u.Class, true, false, hr);
                _rTorp[i] = ClassicRates.Squadron(_p, u.Class, true, true, hr);
                _rSq[i] = ClassicRates.Squadron(_p, u.Class, false, false, hr);
                _f[i] = ClassicRates.Squadron(_p, u.Class, true, false);
            }
            _recharge[i] = ClassicRates.Recharge(_p, u.Class, hr, u.IsCapital ? u.Hits[0] : 0);
        }
        // capital-against-squadron rates depend on the target class: computed per pair in Rate()
    }

    /// <summary>rate(u, v) from the start-of-second state. rule: m4-battle-classic §3.1, §4.</summary>
    private double Rate(BattleUnit u, BattleUnit v)
    {
        if (v.IsCapital) return _shieldZero[v.Index] ? _rTorp[u.Index] : _rCap[u.Index];
        return u.IsCapital ? ClassicRates.CapitalVsSquadron(_p, u.Class, v.Class, u.HullRatio, u.Hits[1]) : _rSq[u.Index];
    }

    // ---------------------------------------------------------------- one second: targets

    private readonly record struct Shot(BattleUnit Attacker, BattleUnit Target, double Damage);

    private List<Shot> Assign()
    {
        var shots = new List<Shot>();
        foreach (var side in Order)
        {
            var mine = AliveOf(side).ToList();
            var foes = AliveOf(Other(side)).ToList();
            if (mine.Count == 0 || foes.Count == 0) continue;
            var caps = foes.Where(v => v.IsCapital).OrderByDescending(Value).ThenBy(v => v.Index).ToList();
            var sqs = foes.Where(v => !v.IsCapital).OrderByDescending(Value).ThenBy(v => v.Index).ToList();
            var capFirst = caps.Concat(sqs).ToList();
            var fightFirst = sqs.Concat(caps).ToList();

            var sideShots = new List<Shot>();
            if (_p.TaskForces == ClassicTaskForces.PerSide)
            {
                // One task force: the first target (capitals-first list) that any member can hurt; one hit with the members' summed rate.
                foreach (var v in capFirst)
                {
                    double total = mine.Sum(u => Rate(u, v));
                    if (total > 0) { sideShots.Add(new Shot(mine[0], v, total)); break; }
                }
            }
            else
            {
                // A capital ship with a laser cannon in any arc, and every squadron, use the fighters-first list (DEFAULT reading).
                var poolA = mine.Where(u => !FightersFirst(u)).ToList();
                var poolB = mine.Where(FightersFirst).ToList();
                AssignPool(poolA, capFirst, sideShots);
                AssignPool(poolB, fightFirst, sideShots);
            }
            shots.AddRange(sideShots.OrderBy(s => s.Attacker.Index)); // task forces by unit id
        }
        return shots;
    }

    private static bool FightersFirst(BattleUnit u) => !u.IsCapital || u.Class.GunArcs.Fore.Laser > 0 || u.Class.GunArcs.Aft.Laser > 0
        || u.Class.GunArcs.Port.Laser > 0 || u.Class.GunArcs.Starboard.Laser > 0;

    /// <summary>The target priority: 15 x F - 0.01 x shield ratio - 0.01 x hull ratio. rule: m4-battle-classic §5.</summary>
    private double Value(BattleUnit v)
    {
        double shieldRatio = v.Class.ShieldStrength > 0 ? v.Shield / v.Class.ShieldStrength : 0;
        return _p.PriorityFirepowerWeight * _f[v.Index] - _p.PriorityShieldWeight * shieldRatio - _p.PriorityHullWeight * v.HullRatio;
    }

    private void AssignPool(List<BattleUnit> pool, List<BattleUnit> list, List<Shot> shots)
    {
        if (pool.Count == 0 || list.Count == 0) return;
        var free = new List<BattleUnit>(pool);
        if (_p.Tactic == BattleTactic.Surround)
        {
            // Surround: every task force goes at the first target it can hurt. rule: m4-battle-classic §6
            foreach (var f in free)
                foreach (var v in list)
                {
                    double r = Rate(f, v);
                    if (r > 0) { shots.Add(new Shot(f, v, r)); break; }
                }
            return;
        }
        // Stand Off. rule: m4-battle-classic §6 (Traced-medium 0x5ec740)
        foreach (var v in list)
        {
            if (free.Count == 0) break;
            var committed = new List<(BattleUnit U, double R)>();
            double total = 0;
            foreach (var f in free)
            {
                double r = Rate(f, v);
                if (r <= 0) continue; // a task force that cannot hurt this target is skipped and stays free
                committed.Add((f, r)); total += r;
                double net = total - _recharge[v.Index];
                if (net > 0 && (v.Hull * _p.EffectiveHullFactor + v.Shield) / net <= _p.KillTimeWindow) break;
            }
            foreach (var (u, r) in committed) { shots.Add(new Shot(u, v, r)); free.Remove(u); }
        }
        // leftovers each join the first target (in list order) they can hurt
        foreach (var f in free)
            foreach (var v in list)
            {
                double r = Rate(f, v);
                if (r > 0) { shots.Add(new Shot(f, v, r)); break; }
            }
    }

    // ---------------------------------------------------------------- one second: fire, remove, recharge

    private void Fire(List<Shot> shots)
    {
        foreach (var s in shots)
        {
            if (s.Target.Hull <= 0) continue; // already destroyed in this second: nothing, no draw
            ApplyHit(s.Target, s.Damage);
        }
    }

    /// <summary>Apply one hit. rule: m4-battle-classic §7, §11 (draw order: shield-generator draw, then hull draw).</summary>
    private void ApplyHit(BattleUnit v, double d)
    {
        double shieldBefore = v.Shield;
        double hullLost;
        double removed = 0;
        if (shieldBefore > 0)
        {
            removed = Math.Min(d, shieldBefore);
            if (d > shieldBefore)
            {
                v.Shield = 0;
                hullLost = Math.Max(1.0, d - shieldBefore); // minimum of 1 at the crossing
            }
            else { v.Shield = shieldBefore - removed; hullLost = 0; }
        }
        else hullLost = d;

        if (v.IsCapital && _p.SubsystemRolls)
        {
            if (shieldBefore > 0)
            {
                double strength = v.Class.ShieldStrength;
                double pct = Math.Max(_p.ShieldGeneratorMinPercent, strength > 0 ? 100.0 * removed / strength : 0);
                int r = _rng.NextInt(0, _p.RollSides - 1);
                if (r < pct) Bump(v, 0);
            }
            v.Hull -= hullLost;
            if (hullLost > 0 && v.Hull > 0)
            {
                double score = _rng.NextInt(0, _p.RollSides - 1) + 100.0 * hullLost / v.HullMax;
                if (score > _p.SubsystemScoreAbove)
                {
                    int idx = score <= _p.ShieldGeneratorUpTo ? 0 : score <= _p.WeaponsUpTo ? 1 : score <= _p.TractorUpTo ? 2 : score <= _p.SubLightUpTo ? 3 : ObjectRegistry.HyperdriveCounter;
                    Bump(v, idx);
                }
            }
        }
        else v.Hull -= hullLost;
    }

    /// <summary>One more hit on a counter; a counter at its cap stays (the draw is still used). The cap applies to the whole stored counter (DEFAULT). rule: m4-battle-classic §7 (3).</summary>
    private void Bump(BattleUnit v, int idx)
    {
        int cap = idx == ObjectRegistry.HyperdriveCounter ? ClassicRates.HyperdriveComponents(v.Class) : _p.SubsystemCap;
        if (v.Hits[idx] < cap) v.Hits[idx]++;
    }

    /// <summary>Removal: every unit at hull 0 or below is destroyed, in unit order. A destroyed carrier's squadrons lose their berth. rule: m4-battle-classic §3.4, §Defaults.</summary>
    private void Remove(int second)
    {
        foreach (var u in _units)
        {
            if (!u.Alive || u.Hull > 0) continue;
            Kill(u, second, UnitLossReason.Combat);
        }
    }

    private void Kill(BattleUnit u, int second, UnitLossReason why)
    {
        u.Alive = false; u.Loss = why; u.LostAt = second;
        _steps.Add(new UnitDestroyedStep(u, second, why));
        if (u.IsCapital)
            foreach (var q in _units)
                if (q.Alive && !q.IsCapital && q.Carrier == u.ObjectId && u.ObjectId != 0) q.Carrier = 0;
    }

    /// <summary>Shield recharge of every survivor: shield = min(cap, shield + recharge). rule: m4-battle-classic §3.5.</summary>
    private void Recharge()
    {
        foreach (var u in _units)
        {
            if (!u.Alive) continue;
            double hr = u.HullRatio;
            double rec = ClassicRates.Recharge(_p, u.Class, hr, u.IsCapital ? u.Hits[0] : 0);
            u.Shield = Math.Min(ClassicRates.ShieldCap(u.Class, hr), u.Shield + rec);
        }
    }

    // ---------------------------------------------------------------- stalemate and retreat

    /// <summary>
    /// The stalemate: the side with the lower strength is flagged (both if equal); a flagged side that can retreat does, one that cannot is
    /// destroyed (reason Stalemate). With DeathStarStalemate on, a side facing a live Death Star is the flagged one and is destroyed. rule: m4-battle-classic §8.3;
    /// m4-retreat-damage-repair §2. Returns true when the battle ended.
    /// </summary>
    private bool ResolveStalemate(double[] str, int second)
    {
        var flagged = new List<Side>();
        if (_p.DeathStarStalemate)
        {
            foreach (var s in Order)
                if (AliveOf(Other(s)).Any(IsDeathStar)) flagged.Add(s);
        }
        bool dsRule = flagged.Count > 0;
        if (!dsRule)
        {
            double a = str[I(Side.Alliance)], e = str[I(Side.Empire)];
            if (a <= e) flagged.Add(Side.Alliance);
            if (e <= a) flagged.Add(Side.Empire);
        }
        var can = flagged.Select(s => dsRule ? RetreatRefusal.NoHyperdrive : CanRetreat(s)).ToList();
        bool any = false;
        for (int k = 0; k < flagged.Count; k++)
        {
            if (can[k] is { } r)
            {
                if (!dsRule) Refuse(flagged[k], r);
                foreach (var u in AliveOf(flagged[k]).ToList()) Kill(u, second, UnitLossReason.Stalemate);
            }
            else ExecuteRetreat(flagged[k], second);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Retreat execution, one pass, units in unit order: capital ships with a working hyperdrive leave, the others are destroyed; then squadrons
    /// with a hyperdrive jump alone, the rest keep their berth or take a free one, else are destroyed. rule: m4-retreat-damage-repair §2.
    /// </summary>
    private void ExecuteRetreat(Side side, int second)
    {
        _retreated[I(side)] = true;
        var mine = AliveOf(side).ToList();
        var leaving = new List<BattleUnit>();
        foreach (var u in mine.Where(x => x.IsCapital))
        {
            // Death Star exception: it jumps regardless of its hit counters (Lead, DEFAULT). rule: m4-retreat-damage-repair §1 (4)
            if (WorkingHyperdrive(u) || (side == Side.Empire && IsDeathStar(u))) { u.Alive = false; u.Left = true; leaving.Add(u); }
            else Kill(u, second, UnitLossReason.NoHyperdrive);
        }
        // free berths: fighter_capacity minus the squadrons already aboard that ship
        var free = new Dictionary<int, int>(); // lookup only; iterated through `leaving`
        foreach (var c in leaving)
            free[c.Index] = c.Class.FighterCapacity - mine.Count(q => !q.IsCapital && q.Carrier == c.ObjectId && c.ObjectId != 0 && q.Alive);
        foreach (var q in mine.Where(x => !x.IsCapital))
        {
            bool aboardLeaving = q.Carrier != 0 && leaving.Any(c => c.ObjectId == q.Carrier);
            if (aboardLeaving) { q.Alive = false; q.Left = true; continue; } // keeps its own berth (and goes with its carrier)
            if (q.Class.Hyperdrive > 0) { q.Alive = false; q.Left = true; q.JumpedAlone = true; q.Carrier = 0; continue; }
            var berth = leaving.FirstOrDefault(c => free[c.Index] > 0);
            if (berth != null) { free[berth.Index]--; q.Alive = false; q.Left = true; q.NewCarrier = berth.ObjectId; q.Carrier = berth.ObjectId; continue; }
            Kill(q, second, UnitLossReason.NoBerth);
        }
        _steps.Add(new SideRetreatedStep(side, second));
    }

    // ---------------------------------------------------------------- result

    private ClassicBattleResult Finish(int second)
    {
        bool aliveA = HasUnits(Side.Alliance), aliveE = HasUnits(Side.Empire);
        BattleOutcome Outcome(Side s, bool alive, bool otherAlive) =>
            alive && !otherAlive ? BattleOutcome.Victorious
            : !alive && otherAlive ? (_retreated[I(s)] ? BattleOutcome.Retreated : BattleOutcome.Annihilated)
            : BattleOutcome.Indecisive;
        int Lost(Side s) => _units.Count(u => u.Side == s && u.Destroyed);
        int Survived(Side s) => _units.Count(u => u.Side == s && !u.Destroyed);
        return new ClassicBattleResult
        {
            Seconds = second,
            AllianceOutcome = Outcome(Side.Alliance, aliveA, aliveE),
            EmpireOutcome = Outcome(Side.Empire, aliveE, aliveA),
            AllianceLosses = Lost(Side.Alliance), EmpireLosses = Lost(Side.Empire),
            AllianceSurvivors = Survived(Side.Alliance), EmpireSurvivors = Survived(Side.Empire),
            AllianceRetreated = _retreated[I(Side.Alliance)], EmpireRetreated = _retreated[I(Side.Empire)],
            Draws = _rng.Draws,
            Steps = _steps,
        };
    }
}
