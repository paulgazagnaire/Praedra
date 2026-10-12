using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Battle;

/// <summary>One line of the optional event log of a New battle (shots, hits, deflections, kills, retreats). rule: m4-battle-new §4 step 8, §13.</summary>
public sealed record BattleLogEntry(int T, string Kind, int Unit, int Other, string Detail);

/// <summary>
/// The New space battle: a second-by-second per-gun fight (m4-battle-new §3 to §11). Batteries fire out of arcs at exact DAT range, every gun rolls a
/// seeded draw, shields soak and deflect, squadrons launch and strafe, sides retreat on the strength measure of §12. Runs on plain
/// <see cref="BattleUnit"/>s and its own generator, exactly like <see cref="ClassicBattle"/>, and returns the same result and steps, so
/// <see cref="BattleModule"/> writes it back the same way. Nothing moves in the galaxy; the caller says only whether each side has a retreat destination.
/// One thread, fixed evaluation order, no hash iteration: the same units and seed give the same battle.
/// rule: m4-battle-new (whole sheet, with its Lead update 2026-10-12); m4-retreat-damage-repair §1 to §4 (shared retreat clauses).
/// </summary>
public sealed class NewBattle
{
    private enum St { Hangar, Active, Jumping, Leaving, Left, Dead }

    private readonly record struct Rec(NU Attacker, WeaponKind Weapon, double Damage);

    private sealed class NU
    {
        public int Id;
        public BattleUnit B = null!;
        public CombatClass C = null!;
        public Side Side;
        public bool Capital;
        public double Hull, HullMax, Shield;
        public int LastHitT = int.MinValue / 2;
        public bool Broadside;
        public int TurnTimer;
        public NU? Target;
        public int CommitUntil;
        public int Offset;
        public int[] Cooldown = Array.Empty<int>();
        public St State;
        public int TorpedoesLeft;
        public int JumpEnd;
        public NU? Carrier;          // squadron: the ship it rides (null = based, or its carrier is gone)
        public bool HadCarrier;
        public int FireFrom;         // squadron: first second it may fire
        public List<Rec> Recs = new();
        public double Assigned;      // saturation scratch
        public double Threat;
        public double HullRatio => HullMax <= 0 ? 0 : Math.Max(0.0, Math.Min(1.0, Hull / HullMax));
        public bool OnField => State == St.Active || State == St.Jumping;
    }

    private readonly BattleParams _p;
    private readonly NewBattleParams _np;
    private readonly CombatModel _m;
    private readonly NewBattleData _data;
    private readonly GameRng _rng;
    private readonly List<NU> _u = new();
    private readonly bool[] _hasDestination = new bool[2];
    private readonly OnContactMode[] _alert = new OnContactMode[2];
    private readonly BattleTactic[] _tactic = new BattleTactic[2];
    private readonly bool[] _decided = new bool[2];      // retreat decided (once per side)
    private readonly List<BattleStep> _steps = new();
    private readonly List<BattleLogEntry>? _log;
    private bool _refusalReported;
    private double _d;                                     // fleet distance
    private Combatant[] _cmb = Array.Empty<Combatant>();
    private int _t;

    private static int I(Side s) => (int)s;
    private static Side Other(Side s) => s == Side.Alliance ? Side.Empire : Side.Alliance;
    private static readonly Side[] Sides = { Side.Alliance, Side.Empire };

    /// <param name="units">All units of both sides. The processing order (the sheet's unit ids) is ascending <see cref="BattleUnit.ObjectId"/> (creation order), ties by list position.</param>
    /// <param name="hasDestination">Per side (by <c>(int)Side</c>): does a valid retreat system exist? rule: m4-retreat-damage-repair §1 (5).</param>
    public NewBattle(BattleParams p, NewBattleData data, IReadOnlyList<BattleUnit> units, GameRng rng, bool[] hasDestination,
        OnContactMode[]? alert = null, bool logEvents = false)
    {
        _p = p; _np = p.New; _data = data; _rng = rng; _m = new CombatModel(data, p.New);
        _hasDestination[0] = hasDestination[0]; _hasDestination[1] = hasDestination[1];
        if (alert != null) { _alert[0] = alert[0]; _alert[1] = alert[1]; }
        _tactic[0] = _tactic[1] = p.Tactic;
        if (logEvents) _log = new List<BattleLogEntry>();

        var order = units.Select((b, i) => (b, i)).OrderBy(x => x.b.ObjectId).ThenBy(x => x.i).ToList();
        foreach (var (b, _) in order)
        {
            var c = data.Class(b.Class.Name);
            var u = new NU { Id = _u.Count, B = b, C = c, Side = b.Side, Capital = b.IsCapital, State = b.IsCapital ? St.Active : St.Hangar };
            u.HullMax = b.HullMax > 0 ? b.HullMax : c.Hull;
            u.Hull = b.IsCapital ? b.Hull : (b.Hull > 0 ? Math.Min(b.Hull, u.HullMax) : u.HullMax); // a squadron is whole unless the caller says otherwise
            u.Offset = b.IsCapital ? OffsetOf(c.Size) : 0;
            u.Cooldown = new int[c.Batteries.Count]; // rule: m4-battle-new §3.4 (all batteries ready at t = 0)
            u.Shield = c.ShieldPool * u.HullRatio;   // rule: m4-battle-new §3.4, §9 (shields start at the hull-scaled cap)
            u.TorpedoesLeft = b.IsCapital ? 0 : c.Torpedoes;
            if (!b.IsCapital && b.Carrier != 0) u.Carrier = _u.FirstOrDefault(x => x.Capital && x.Side == b.Side && x.B.ObjectId == b.Carrier);
            u.HadCarrier = u.Carrier != null;
            _u.Add(u);
        }
        // a carrier listed after its squadron (unusual ids): resolve now
        foreach (var u in _u)
            if (!u.Capital && u.B.Carrier != 0 && u.Carrier == null)
            {
                u.Carrier = _u.FirstOrDefault(x => x.Capital && x.Side == u.Side && x.B.ObjectId == u.B.Carrier);
                u.HadCarrier = u.Carrier != null;
            }
    }

    /// <summary>Set the tactic of one side (DEFAULT: <see cref="BattleParams.Tactic"/> for both). rule: m4-battle-new §3.5.</summary>
    public void SetTactic(Side side, BattleTactic tactic) => _tactic[I(side)] = tactic;

    /// <summary>The event log, or null when logging is off.</summary>
    public IReadOnlyList<BattleLogEntry>? Log => _log;
    public IReadOnlyList<BattleUnit> Units => _u.Select(x => x.B).ToList();
    /// <summary>Fleet distance now (for tests and the viewer).</summary>
    public double Distance => _d;
    /// <summary>The start distance D: the longest weapon range present + 10. Set when <see cref="Run"/> begins. rule: m4-battle-new Lead update 2.</summary>
    public double StartDistance { get; private set; }
    /// <summary>Called at the end of every tick (after step 7) with the tick number: the viewer's and the tests' hook to read the live state. Never changes the battle.</summary>
    public Action<int>? AfterTick { get; set; }
    /// <summary>Live shield of a unit (shields are not written back to the strategic state). rule: m4-battle-new §13.</summary>
    public double ShieldOf(BattleUnit b) => _u.First(x => x.B == b).Shield;
    /// <summary>Live hull of a unit.</summary>
    public double HullOf(BattleUnit b) => _u.First(x => x.B == b).Hull;

    private int OffsetOf(SizeClass s) => s switch
    {
        SizeClass.Huge => _np.OffsetHuge, SizeClass.Large => _np.OffsetLarge, SizeClass.Medium => _np.OffsetMedium, SizeClass.Small => _np.OffsetSmall, _ => 0,
    };

    private void Note(string kind, NU a, NU? b, string detail) => _log?.Add(new BattleLogEntry(_t, kind, a.B.ObjectId, b?.B.ObjectId ?? 0, detail));

    // ================================================================== helpers over the live state

    private static bool Targetable(NU u) => u.State == St.Active || u.State == St.Jumping;
    /// <summary>Live for strength as a target: on the field or still in a hangar (counted as if launched).</summary>
    private static bool LiveTarget(NU u) => u.State == St.Hangar || u.State == St.Active || u.State == St.Jumping;
    /// <summary>Counts as own strength: not jumping, not riding a leaving carrier. rule: m4-battle-new §12.</summary>
    private static bool OwnStrength(NU u) => u.State == St.Hangar || u.State == St.Active;
    private bool AliveShip(NU u) => u.Capital && (u.State == St.Active || u.State == St.Jumping);
    private int ShipsLive(Side s)
    {
        int n = 0;
        foreach (var u in _u) if (u.Side == s && AliveShip(u)) n++;
        return n;
    }

    private int Craft(NU u) => u.Hull <= 0 ? 0 : (int)Math.Ceiling(u.Hull / _np.CraftHull);

    private Combatant Cmb(NU u) => new()
    {
        Class = u.C, HullRatio = u.HullRatio, ShieldFactor = _m.ShieldFactor(u.C, u.Shield), ShieldUp = u.Shield > 0,
        WeaponHits = u.Capital ? u.B.Hits[1] : 0, Craft = u.Capital ? 0 : Craft(u), Torpedoes = u.TorpedoesLeft,
    };

    private bool HyperdriveOk(NU u) => u.Capital
        ? u.C.Hyperdrive > 0 && ClassicRates.HyperdriveComponents(u.B.Class) - u.B.Hits[ObjectRegistry.HyperdriveCounter] > 0
        : u.C.Hyperdrive > 0;

    /// <summary>The test CanRetreat: null = can; else the first failing reason (checked in the order gravity well, hyperdrive, destination). rule: m4-battle-new §11.2; m4-retreat-damage-repair §1.</summary>
    private RetreatRefusal? CanRetreat(Side side)
    {
        if (_u.Any(u => u.Side == Other(side) && AliveShip(u) && u.B.Class.GravityWellProjector > 0)) return RetreatRefusal.GravityWell;
        bool able = _u.Any(u => u.Side == side && (u.State == St.Active || u.State == St.Hangar) && HyperdriveOk(u) && (u.Capital || u.C.Hyperdrive > 0));
        if (!able) return RetreatRefusal.NoHyperdrive;
        if (!_hasDestination[I(side)]) return RetreatRefusal.NoDestination;
        return null;
    }

    private double PairRange(NU a, NU b)
    {
        // rule: m4-battle-new §5: range 5 between a squadron and the unit it is attacking (or that attacks it)
        if ((!a.Capital && a.Target == b) || (!b.Capital && b.Target == a)) return _np.SquadronRange;
        return _d + a.Offset + b.Offset;
    }

    // ================================================================== the battle

    /// <summary>Run the battle to its end and return the result. The units keep their final state.</summary>
    public ClassicBattleResult Run()
    {
        // rule: m4-battle-new §3.1 and the Lead update: D = the longest weapon range present in the battle + 10
        int longest = 0;
        foreach (var u in _u) longest = Math.Max(longest, u.C.LongestRange);
        _d = longest + _np.StartDistanceMargin;
        StartDistance = _d;

        // rule: m4-battle-classic §1.3 (alert), shared: a side set to Retreat leaves at second 0 if it can; when both are set, only the Alliance goes
        _t = 0;
        foreach (var side in Sides)
        {
            if (_alert[I(side)] != OnContactMode.Retreat || ShipsLive(side) == 0 || ShipsLive(Other(side)) == 0 || _decided[I(Other(side))]) continue;
            if (CanRetreat(side) is { } r) Refuse(side, r); else ExecuteRetreat(side);
        }

        var low = new double[] { double.PositiveInfinity, double.PositiveInfinity };
        var lowT = new int[2];
        int cap = _np.TickCap;
        bool ended = false;
        for (_t = 0; ; _t++)
        {
            int t = _t;
            RefreshCombatants();
            Movement(t);                // 1
            RefreshCombatants();
            Facing(t);                  // 2
            SelectTargets(t);           // 3
            Fire(t);                    // 4
            ApplyDamage(t);             // 4b
            CooldownsAndShields(t);     // 5
            SquadronHousekeeping(t);    // 6
            ended = RetreatAndEnd(t, low, lowT);  // 7
            // step 8 (events) is the log written as things happen
            AfterTick?.Invoke(t);
            if (ended || t >= cap + _np.JumpSeconds + 2) break;
        }
        FinishSquadrons();
        return Finish();
    }

    private void Refuse(Side side, RetreatRefusal reason)
    {
        if (_refusalReported) return;
        _refusalReported = true; // once per battle, at the first refusal. rule: m4-retreat-damage-repair §1
        _steps.Add(new RetreatRefusedStep(side, reason));
    }

    private void RefreshCombatants()
    {
        if (_cmb.Length != _u.Count) _cmb = new Combatant[_u.Count];
        foreach (var u in _u) if (u.State != St.Dead && u.State != St.Left) _cmb[u.Id] = Cmb(u);
    }

    // ---------------------------------------------------------------- step 1: movement

    private double Sublight(NU u) => u.C.Sublight * (4 - Math.Min(u.B.Hits[3], 4)) / 4.0;

    private double SlowestSublight(Side s)
    {
        double best = -1;
        foreach (var u in _u)
            if (u.Side == s && u.Capital && u.State == St.Active) { double v = Sublight(u); if (best < 0 || v < best) best = v; }
        return best;
    }

    /// <summary>
    /// StandOff desired distance: from the fore batteries of live capital ships, the weapon type with the largest sum of
    /// guns_active x damage / reload x hull multiplier (tie: longer range), distance = max(0, R - 16). Surround: 0. A side with nothing armed has no
    /// preference (keeps D). rule: m4-battle-new §5 (DEFAULT).
    /// </summary>
    private double DesiredDistance(Side s)
    {
        if (_tactic[I(s)] == BattleTactic.Surround) return 0;
        foreach (int mask in new[] { CombatModel.ForeMask, 15 }) // DEFAULT fallback: a fleet with no fore guns at all uses every arc
        {
            var sum = new double[3]; var range = new int[3];
            foreach (var u in _u)
            {
                if (u.Side != s || !u.Capital || u.State != St.Active) continue;
                var cmb = _cmb[u.Id];
                foreach (var b in u.C.Batteries)
                {
                    if ((mask & (1 << (int)b.Arc)) == 0) continue;
                    int guns = _m.GunsActive(b, cmb);
                    if (guns <= 0) continue;
                    int k = (int)b.Weapon;
                    sum[k] += guns * b.ShotDamage / b.Reload * _data.Weapon(b.Weapon).VsHull;
                    range[k] = Math.Max(range[k], b.Range);
                }
            }
            int best = -1;
            for (int k = 0; k < 3; k++)
            {
                if (sum[k] <= 0) continue;
                if (best < 0 || sum[k] > sum[best] || (sum[k] == sum[best] && range[k] > range[best])) best = k;
            }
            if (best >= 0) return Math.Max(0, range[best] - _np.StandOffSlack);
        }
        return _d;
    }

    private void Movement(int t)
    {
        // squadron launch at t = 5 (a squadron whose carrier died before is already lost, step 6)
        if (t == _np.LaunchSecond)
            foreach (var u in _u)
                if (!u.Capital && u.State == St.Hangar)
                {
                    u.State = St.Active; u.FireFrom = t + _np.FlightSeconds;
                    Note("launch", u, u.Carrier, "");
                }
        double sa = SlowestSublight(Side.Alliance), se = SlowestSublight(Side.Empire);
        if (sa < 0 && se < 0) return;
        Side mover;
        double da = sa >= 0 ? DesiredDistance(Side.Alliance) : 0, de = se >= 0 ? DesiredDistance(Side.Empire) : 0;
        if (sa > se) mover = Side.Alliance;
        else if (se > sa) mover = Side.Empire;
        else mover = da >= de ? Side.Alliance : Side.Empire; // tie: the larger desired distance
        double speed = mover == Side.Alliance ? sa : se, want = mover == Side.Alliance ? da : de;
        double step = _np.ClosingK * speed;
        if (_d > want) _d = Math.Max(want, _d - step);
        else if (_d < want) _d = Math.Min(want, _d + step);
    }

    // ---------------------------------------------------------------- step 2: facing

    private int ActiveMask(NU u, NU? tgt)
    {
        if (!u.Capital) return CombatModel.ForeMask;
        if (u.State == St.Jumping) return CombatModel.AftMask; // DEFAULT: a retreating ship fires its aft batteries only
        if (!u.Broadside || tgt == null) return CombatModel.ForeMask;
        return BestSideMask(u, tgt);
    }

    private int BestSideMask(NU u, NU tgt)
    {
        double range = PairRange(u, tgt);
        double port = _m.ArcsDps(_cmb[u.Id], _cmb[tgt.Id], CombatModel.PortMask, range);
        double star = _m.ArcsDps(_cmb[u.Id], _cmb[tgt.Id], CombatModel.StarboardMask, range);
        return star > port ? CombatModel.StarboardMask : CombatModel.PortMask; // tie: port
    }

    private void Facing(int t)
    {
        foreach (var u in _u)
        {
            if (!u.Capital || u.State != St.Active || u.TurnTimer > 0) continue;
            var tgt = u.Target;
            if (tgt == null || !Targetable(tgt)) continue; // with no target the facing stays
            double range = PairRange(u, tgt);
            double fore = _m.ArcsDps(_cmb[u.Id], _cmb[tgt.Id], CombatModel.ForeMask, range);
            int sideMask = BestSideMask(u, tgt);
            double side = _m.ArcsDps(_cmb[u.Id], _cmb[tgt.Id], sideMask, range);
            bool toggle = u.Broadside ? fore > 0 && fore >= _np.FacingHysteresis * side : side > 0 && side >= _np.FacingHysteresis * fore;
            if (!toggle) continue;
            u.Broadside = !u.Broadside;
            int man = u.C.ManeuverOrOne;
            u.TurnTimer = (_np.TurnTime + man - 1) / man; // ceil(10 / maneuver)
            Note("turn", u, tgt, u.Broadside ? "broadside" : "head-on");
        }
    }

    // ---------------------------------------------------------------- step 3: target selection

    private double DpsRaw(NU u, NU tgt)
    {
        double range = u.Capital ? PairRange(u, tgt) : _np.SquadronRange;
        if (u.Capital && u.State == St.Active && u.Broadside) // the better side (tie: port, same value): one evaluation per side
            return Math.Max(_m.ArcsDps(_cmb[u.Id], _cmb[tgt.Id], CombatModel.PortMask, range), _m.ArcsDps(_cmb[u.Id], _cmb[tgt.Id], CombatModel.StarboardMask, range));
        return _m.ArcsDps(_cmb[u.Id], _cmb[tgt.Id], ActiveMask(u, tgt), range);
    }

    private bool AnyBatteryInRange(NU u, NU tgt)
    {
        if (!u.Capital) return true;
        double range = PairRange(u, tgt);
        int mask = ActiveMask(u, tgt);
        foreach (var b in u.C.Batteries)
            if ((mask & (1 << (int)b.Arc)) != 0 && b.Range >= range && _m.GunsActive(b, _cmb[u.Id]) > 0) return true;
        return false;
    }

    private void SelectTargets(int t)
    {
        // threat[u]: cost-weighted mean over the enemy units of facingDps(u, enemy) (the same function CombatStrength uses). rule: m4-battle-new §7
        var groups = new List<(Combatant Rep, double Cost)>[2];
        var weight = new double[2];
        for (int s = 0; s < 2; s++)
        {
            groups[s] = new List<(Combatant, double)>();
            foreach (var u in _u)
            {
                if (I(u.Side) != s || !LiveTarget(u)) continue;
                weight[s] += u.C.Cost;
                int g = -1;
                for (int i = 0; i < groups[s].Count; i++)
                    if (groups[s][i].Rep.Class.Index == u.C.Index && groups[s][i].Rep.ShieldUp == _cmb[u.Id].ShieldUp) { g = i; break; }
                if (g < 0) groups[s].Add((_cmb[u.Id], u.C.Cost)); else groups[s][g] = (groups[s][g].Rep, groups[s][g].Cost + u.C.Cost);
            }
        }
        foreach (var u in _u)
        {
            u.Threat = 0; u.Assigned = 0;
            if (!LiveTarget(u)) continue;
            int e = 1 - I(u.Side);
            if (weight[e] <= 0) continue;
            double d = 0;
            foreach (var (rep, cost) in groups[e]) d += cost * _m.FacingDps(_cmb[u.Id], rep) / weight[e];
            u.Threat = d;
        }

        // units that keep their target (committed) count towards saturation. rule: m4-battle-new §7 (StandOff)
        var needs = new List<NU>();
        foreach (var u in _u)
        {
            if (!(u.State == St.Active || u.State == St.Jumping) || (!u.Capital && u.State != St.Active)) continue;
            if (u.Target != null && Targetable(u.Target) && t < u.CommitUntil && AnyBatteryInRange(u, u.Target))
            {
                u.Target.Assigned += DpsRaw(u, u.Target);
                continue;
            }
            u.Target = null;
            needs.Add(u);
        }
        foreach (var u in needs) Choose(u, t);
    }

    private void Choose(NU u, int t)
    {
        bool standOff = _tactic[I(u.Side)] == BattleTactic.StandOff;
        // squadrons: dogfight first. rule: m4-battle-new §7
        if (!u.Capital)
        {
            foreach (var e in _u)
                if (e.Side != u.Side && !e.Capital && Targetable(e) && DpsRaw(u, e) > 0) { Assign(u, e, DpsRaw(u, e), t); return; }
        }
        NU? best = null; double bestScore = -1, bestDps = 0;
        NU? bestFree = null; double bestFreeScore = -1, bestFreeDps = 0;
        foreach (var e in _u)
        {
            if (e.Side == u.Side || !Targetable(e)) continue;
            double torp = u.Capital ? 0 : _m.TorpedoExpected(_cmb[u.Id], _cmb[e.Id]);
            if (u.Capital && PairRange(u, e) > u.C.LongestRange) continue; // quick filter: out of reach of every weapon of the class
            if (torp <= 0 && !_m.CanHurt(u.C, _cmb[e.Id])) continue; // quick filter: no battery of the class can ever hurt it now
            double dps = DpsRaw(u, e);
            if (dps <= 0 && torp <= 0) continue; // cannot hurt it now: laser ships ignore shielded capitals and hold fire
            double remaining = e.Hull + e.Shield;
            double expected = dps * _np.ExpectedDamageHorizon + torp;
            double score = e.Threat * Math.Min(1.0, expected / remaining);
            if (score > bestScore) { bestScore = score; best = e; bestDps = dps; }
            bool saturated = e.Assigned * _np.KillWindow >= remaining;
            if (!saturated && score > bestFreeScore) { bestFreeScore = score; bestFree = e; bestFreeDps = dps; }
        }
        if (best == null && u.Capital)
        {
            // DEFAULT fallback: nothing hurtable in the current facing; look at the better facing so a ship with no fore guns can turn to a target
            foreach (var e in _u)
            {
                if (e.Side == u.Side || !Targetable(e) || !_m.CanHurt(u.C, _cmb[e.Id])) continue;
                double range = PairRange(u, e);
                if (range > u.C.LongestRange) continue;
                double dps = Math.Max(_m.ArcsDps(_cmb[u.Id], _cmb[e.Id], CombatModel.ForeMask, range),
                    Math.Max(_m.ArcsDps(_cmb[u.Id], _cmb[e.Id], CombatModel.PortMask, range), _m.ArcsDps(_cmb[u.Id], _cmb[e.Id], CombatModel.StarboardMask, range)));
                if (dps <= 0) continue;
                double score = e.Threat * Math.Min(1.0, dps * _np.ExpectedDamageHorizon / (e.Hull + e.Shield));
                if (score > bestScore) { bestScore = score; best = e; bestDps = dps; }
            }
        }
        if (best == null) return; // holds fire
        var pick = standOff && bestFree != null ? bestFree : best;
        Assign(u, pick, pick == bestFree ? bestFreeDps : bestDps, t);
    }

    private void Assign(NU u, NU target, double dps, int t)
    {
        u.Target = target; u.CommitUntil = t + _np.CommitSeconds;
        target.Assigned += dps;
        Note("target", u, target, "");
    }

    // ---------------------------------------------------------------- step 4: fire (collect)

    private void Fire(int t)
    {
        foreach (var u in _u)
        {
            if (!u.OnField) continue;
            if (!u.Capital && t < u.FireFrom) continue;
            if (u.Capital && u.TurnTimer > 0) continue; // turning: nothing fires
            var tgt = u.Target;
            if (tgt == null || !Targetable(tgt)) continue;
            double range = u.Capital ? PairRange(u, tgt) : _np.SquadronRange;
            int mask = ActiveMask(u, tgt);
            var me = _cmb[u.Id]; var him = _cmb[tgt.Id];
            double hr = u.HullRatio;
            for (int bi = 0; bi < u.C.Batteries.Count; bi++)
            {
                var b = u.C.Batteries[bi];
                if (u.Cooldown[bi] > 0) continue;
                if (u.Capital && (mask & (1 << (int)b.Arc)) == 0) continue;
                int guns = _m.GunsActive(b, me);
                if (guns <= 0 || range > b.Range) continue;
                if (_m.ShotValue(b.Weapon, b.ShotDamage, u.C, him) <= 0) continue; // holds fire, cooldown stays 0
                u.Cooldown[bi] = b.Reload;
                uint thr = _data.Weapon(b.Weapon).Threshold[(int)tgt.C.Size];
                double dmg = b.ShotDamage * hr;
                for (int g = 0; g < guns; g++)
                {
                    bool hit = _rng.NextUInt() < thr; // one draw per gun, deflected shots included
                    if (hit) tgt.Recs.Add(new Rec(u, b.Weapon, dmg));
                    if (_log != null) Note(hit ? "hit" : "miss", u, tgt, b.Weapon.ToString());
                }
            }
            // torpedoes: one salvo, all at the same tick, at the first tick there is a capital target in torpedo range that the salvo can hurt
            if (!u.Capital && u.TorpedoesLeft > 0 && !tgt.C.IsSquadron && range <= u.C.TorpedoRange
                && _m.ShotValue(WeaponKind.Torpedo, u.C.TorpedoDamage, u.C, him) > 0)
            {
                uint thr = _data.Weapon(WeaponKind.Torpedo).Threshold[(int)tgt.C.Size];
                double dmg = u.C.TorpedoDamage * hr;
                int n = u.TorpedoesLeft; u.TorpedoesLeft = 0;
                for (int g = 0; g < n; g++)
                {
                    bool hit = _rng.NextUInt() < thr;
                    if (hit) tgt.Recs.Add(new Rec(u, WeaponKind.Torpedo, dmg));
                    if (_log != null) Note(hit ? "hit" : "miss", u, tgt, "Torpedo");
                }
            }
        }
    }

    // ---------------------------------------------------------------- step 4b: damage applied

    private void ApplyDamage(int t)
    {
        var died = new List<NU>();
        foreach (var v in _u)
        {
            if (v.Recs.Count == 0) continue;
            v.LastHitT = t; // every record, deflected or not. rule: m4-battle-new §8 step 4b.1
            double shieldLost = 0, hullLost = 0;
            if (v.Capital)
            {
                foreach (var r in v.Recs)
                {
                    var row = _data.Weapon(r.Weapon);
                    if (v.Shield > 0)
                    {
                        bool laserBlocked = r.Weapon == WeaponKind.LaserCannon && !_np.LaserPenetratesShield;
                        double x = r.Damage * row.VsShield;
                        if (laserBlocked || x <= v.C.Deflection) { if (_log != null) Note("deflect", r.Attacker, v, r.Weapon.ToString()); continue; }
                        if (x <= v.Shield) { v.Shield -= x; shieldLost += x; }
                        else
                        {
                            double part = (x - v.Shield) / x; // the share of the shot that continues to the hull
                            hullLost += r.Damage * row.VsHull * part;
                            shieldLost += v.Shield; v.Shield = 0;
                        }
                    }
                    else hullLost += r.Damage * row.VsHull;
                }
                v.Hull -= hullLost;
            }
            else
            {
                foreach (var r in v.Recs)
                {
                    var row = _data.Weapon(r.Weapon);
                    double dmg = r.Damage * row.VsFighter;
                    if (row.FighterUsesManeuverRatio) dmg *= _m.ManeuverRatio(r.Attacker.C, v.C);
                    if (dmg <= 0) continue;
                    if (v.Shield > 0) { double take = Math.Min(v.Shield, dmg); v.Shield -= take; shieldLost += take; dmg -= take; }
                    if (dmg > 0) { v.Hull -= dmg; hullLost += dmg; }
                }
            }
            if (_log != null) foreach (var r in v.Recs) Note("damage", r.Attacker, v, r.Weapon.ToString());
            v.Recs.Clear();

            bool dead = v.Hull <= 0;
            // subsystem rolls: capital ships only, once per target per tick, on the totals. rule: m4-battle-new §8 step 4b.6
            if (v.Capital && _p.SubsystemRolls)
            {
                if (shieldLost > 0)
                {
                    double pct = v.C.ShieldPool > 0 ? 100.0 * shieldLost / v.C.ShieldPool : 0;
                    int i = _rng.NextInt(0, _np.ShieldRollSides - 1);
                    if (i < Math.Max(_np.ShieldGeneratorMinPercent, pct)) Bump(v, 0);
                }
                if (hullLost > 0 && !dead)
                {
                    double score = _rng.NextInt(0, _np.HullRollSides - 1) + 100.0 * hullLost / v.HullMax;
                    if (score > _p.SubsystemScoreAbove)
                    {
                        int idx = score <= _p.ShieldGeneratorUpTo ? 0 : score <= _p.WeaponsUpTo ? 1 : score <= _p.TractorUpTo ? 2 : score <= _p.SubLightUpTo ? 3 : ObjectRegistry.HyperdriveCounter;
                        Bump(v, idx);
                    }
                }
            }
            if (dead) died.Add(v);
        }
        foreach (var v in died) Kill(v, UnitLossReason.Combat); // after all records of the tick, in id order
    }

    private void Bump(NU v, int idx)
    {
        int cap = idx == ObjectRegistry.HyperdriveCounter ? ClassicRates.HyperdriveComponents(v.B.Class) : _p.SubsystemCap;
        if (v.B.Hits[idx] < cap) v.B.Hits[idx]++;
    }

    private void Kill(NU u, UnitLossReason why)
    {
        if (u.State == St.Dead || u.State == St.Left) return;
        u.State = St.Dead;
        u.B.Alive = false; u.B.Left = false; u.B.Loss = why; u.B.LostAt = _t; u.B.Hull = u.Hull;
        _steps.Add(new UnitDestroyedStep(u.B, _t, why));
        Note("kill", u, null, why.ToString());
    }

    // ---------------------------------------------------------------- step 5: cooldowns, turn timers, shield regeneration

    private void CooldownsAndShields(int t)
    {
        foreach (var u in _u)
        {
            if (u.State == St.Dead || u.State == St.Left) continue;
            for (int i = 0; i < u.Cooldown.Length; i++) u.Cooldown[i] = Math.Max(0, u.Cooldown[i] - 1);
            u.TurnTimer = Math.Max(0, u.TurnTimer - 1);
            if (u.State == St.Hangar || u.State == St.Leaving) continue;
            if (t - u.LastHitT < _np.ShieldRegenPause) continue; // rule: m4-battle-new §9 (five seconds after the last hit)
            double hr = u.HullRatio, cap = u.C.ShieldPool * hr;
            if (u.Shield >= cap) continue; // above the cap after hull loss: not lowered, it just stops regenerating
            int gh = u.Capital ? Math.Min(u.B.Hits[0], 4) : 0;
            double inc = u.C.ShieldRegen * hr * ((4 - gh) / 4.0);
            u.Shield = Math.Min(cap, u.Shield + Math.Max(0, inc));
        }
    }

    // ---------------------------------------------------------------- step 6: fighters

    private void SquadronHousekeeping(int t)
    {
        foreach (var u in _u)
        {
            if (u.Capital || u.State == St.Dead || u.State == St.Left) continue;
            var c = u.Carrier;
            bool carrierGone = c != null && c.State == St.Dead;
            if (!carrierGone) continue;
            // rule: m4-battle-new §10: a squadron whose carrier died before launch is lost; one riding a leaving carrier that died is lost;
            // a flying one carries on without a carrier (it docks at the end)
            if (u.State == St.Hangar || u.State == St.Leaving) Kill(u, UnitLossReason.NoBerth);
            else { u.Carrier = null; u.B.Carrier = 0; }
        }
    }

    // ---------------------------------------------------------------- step 7: retreat and end checks

    private List<Combatant> OwnList(Side s)
    {
        var l = new List<Combatant>();
        foreach (var u in _u) if (u.Side == s && OwnStrength(u)) l.Add(Cmb(u));
        return l;
    }

    private List<Combatant> VersusList(Side s)
    {
        var l = new List<Combatant>();
        foreach (var u in _u) if (u.Side == s && LiveTarget(u)) l.Add(Cmb(u));
        return l;
    }

    /// <summary>CombatStrength of a side against the other with the live battle state. rule: m4-battle-new §12.</summary>
    public double Strength(Side side) => _m.Strength(OwnList(side), VersusList(Other(side)));

    private bool RetreatAndEnd(int t, double[] low, int[] lowT)
    {
        // jumps complete: a ship leaves five seconds after the call; squadrons riding it go with it
        foreach (var u in _u)
            if (u.Capital && u.State == St.Jumping && t >= u.JumpEnd) Leave(u);
        foreach (var u in _u)
            if (!u.Capital && (u.State == St.Leaving || u.State == St.Jumping))
            {
                bool carrierLeft = u.State == St.Leaving && u.Carrier != null && u.Carrier.State == St.Left;
                if (carrierLeft || (u.State == St.Jumping && t >= u.JumpEnd)) Leave(u);
            }

        double[] str = new double[2];
        foreach (var s in Sides) str[I(s)] = Strength(s);

        // morale retreat. rule: m4-battle-new §11.2 (ratio only when the enemy has strength; own = 0 against enemy > 0 retreats)
        foreach (var s in Sides)
        {
            if (_decided[I(s)] || ShipsLive(s) == 0) continue;
            double own = str[I(s)], enemy = str[I(Other(s))];
            if (!(enemy > 0 && own / enemy < _np.RetreatThreshold)) continue;
            if (CanRetreat(s) is { } r) { Refuse(s, r); continue; }
            ExecuteRetreat(s);
        }

        // stalemate. rule: m4-battle-new §11.4
        for (int s = 0; s < 2; s++)
        {
            double rounded = Math.Floor(str[s] / _np.StalemateRounding + 0.5) * _np.StalemateRounding;
            if (rounded < low[s]) { low[s] = rounded; lowT[s] = t; }
        }
        if ((t - lowT[0] >= _np.StalemateSeconds && t - lowT[1] >= _np.StalemateSeconds) || t >= _np.TickCap)
        {
            var flagged = new List<Side>();
            if (str[I(Side.Alliance)] <= str[I(Side.Empire)]) flagged.Add(Side.Alliance);
            if (str[I(Side.Empire)] <= str[I(Side.Alliance)]) flagged.Add(Side.Empire);
            var can = flagged.Select(s => _decided[I(s)] ? (RetreatRefusal?)RetreatRefusal.NoDestination : CanRetreat(s)).ToList();
            for (int k = 0; k < flagged.Count; k++)
            {
                var s = flagged[k];
                if (_decided[I(s)] || ShipsLive(s) == 0) continue;
                if (can[k] is { } r)
                {
                    Refuse(s, r);
                    foreach (var u in _u.Where(x => x.Side == s && (x.State == St.Active || x.State == St.Hangar || x.State == St.Jumping || x.State == St.Leaving)).ToList())
                        Kill(u, UnitLossReason.Stalemate);
                }
                else ExecuteRetreat(s);
            }
        }

        // end: a side has no live ships (none on the field, none jumping). Squadrons without ships count for nothing. rule: m4-battle-new §11.5
        return ShipsLive(Side.Alliance) == 0 || ShipsLive(Side.Empire) == 0;
    }

    private void Leave(NU u)
    {
        u.State = St.Left; u.B.Alive = false; u.B.Left = true; u.B.Hull = u.Hull;
        Note("left", u, null, "");
    }

    /// <summary>
    /// Retreat executed once per side: ships with a working hyperdrive become jumping (hittable for 5 s, then gone), the others are destroyed; squadrons
    /// riding a leaving ship go with it, one with a hyperdrive jumps alone, the rest take a free berth on a leaving ship (fighter_capacity minus the
    /// squadrons already aboard) in id order, else are destroyed. rule: m4-battle-new §11.3; m4-retreat-damage-repair §2.
    /// </summary>
    private void ExecuteRetreat(Side side)
    {
        _decided[I(side)] = true;
        var leaving = new List<NU>();
        foreach (var u in _u)
        {
            if (u.Side != side || !u.Capital || u.State != St.Active) continue;
            if (HyperdriveOk(u)) { u.State = St.Jumping; u.JumpEnd = _t + _np.JumpSeconds; leaving.Add(u); Note("jump", u, null, ""); }
            else Kill(u, UnitLossReason.NoHyperdrive); // left behind (DEFAULT, open question 8)
        }
        var free = new Dictionary<int, int>(); // lookup only; read through `leaving`
        foreach (var c in leaving)
            free[c.Id] = c.C.FighterSquadrons - _u.Count(q => !q.Capital && q.Side == side && q.Carrier == c && q.State != St.Dead && q.State != St.Left);
        foreach (var q in _u)
        {
            if (q.Side != side || q.Capital || !(q.State == St.Hangar || q.State == St.Active)) continue;
            if (q.Carrier != null && q.Carrier.State == St.Jumping) { q.State = St.Leaving; continue; } // keeps its own berth
            if (q.C.Hyperdrive > 0)
            {
                q.State = St.Jumping; q.JumpEnd = _t + _np.JumpSeconds; q.B.JumpedAlone = true; q.B.Carrier = 0; q.Carrier = null; Note("jump", q, null, "alone");
                continue;
            }
            var berth = leaving.FirstOrDefault(c => free[c.Id] > 0);
            if (berth != null)
            {
                free[berth.Id]--; q.State = St.Leaving; q.Carrier = berth; q.B.NewCarrier = berth.B.ObjectId; q.B.Carrier = berth.B.ObjectId;
                continue;
            }
            Kill(q, UnitLossReason.NoBerth);
        }
        _steps.Add(new SideRetreatedStep(side, _t));
    }

    // ---------------------------------------------------------------- the end: docking and result

    /// <summary>
    /// At the end, squadrons still in the battle: a side with ships keeps each squadron of at least 6 craft (docked on its carrier if alive, else on
    /// the lowest-id friendly carrier with a free berth, else lost; based squadrons stay based); a side with no ships loses them. rule: m4-battle-new §10.
    /// </summary>
    private void FinishSquadrons()
    {
        foreach (var s in Sides)
        {
            bool hasShips = ShipsLive(s) > 0;
            var squads = _u.Where(q => q.Side == s && !q.Capital && (q.State == St.Active || q.State == St.Hangar)).ToList();
            if (!hasShips) { foreach (var q in squads) Kill(q, UnitLossReason.NoBerth); continue; }
            var carriers = _u.Where(c => c.Side == s && AliveShip(c) && c.C.FighterSquadrons > 0).ToList();
            var free = new Dictionary<int, int>(); // lookup only
            foreach (var c in carriers)
                free[c.Id] = c.C.FighterSquadrons - squads.Count(q => q.Carrier == c && Craft(q) >= _np.SquadronSurvivalCraft);
            foreach (var q in squads)
            {
                if (Craft(q) < _np.SquadronSurvivalCraft) { Kill(q, UnitLossReason.Combat); continue; }
                if (q.Carrier != null || !q.HadCarrier) continue; // docks on its own carrier, or was based and stays based
                var c = carriers.FirstOrDefault(x => free[x.Id] > 0);
                if (c == null) { Kill(q, UnitLossReason.NoBerth); continue; }
                free[c.Id]--; q.Carrier = c; q.B.Carrier = c.B.ObjectId; q.B.NewCarrier = c.B.ObjectId;
            }
        }
    }

    private ClassicBattleResult Finish()
    {
        foreach (var u in _u) { u.B.Hull = u.Hull; u.B.Shield = u.Shield; }
        bool aliveA = ShipsLive(Side.Alliance) > 0, aliveE = ShipsLive(Side.Empire) > 0;
        bool retA = _u.Any(u => u.Side == Side.Alliance && u.B.Left), retE = _u.Any(u => u.Side == Side.Empire && u.B.Left);
        BattleOutcome Outcome(bool alive, bool otherAlive, bool retreated) =>
            alive && !otherAlive ? BattleOutcome.Victorious
            : !alive && otherAlive ? (retreated ? BattleOutcome.Retreated : BattleOutcome.Annihilated)
            : BattleOutcome.Indecisive;
        int Lost(Side s) => _u.Count(u => u.Side == s && u.B.Destroyed);
        int Survived(Side s) => _u.Count(u => u.Side == s && !u.B.Destroyed);
        return new ClassicBattleResult
        {
            Seconds = _t,
            AllianceOutcome = Outcome(aliveA, aliveE, retA), EmpireOutcome = Outcome(aliveE, aliveA, retE),
            AllianceLosses = Lost(Side.Alliance), EmpireLosses = Lost(Side.Empire),
            AllianceSurvivors = Survived(Side.Alliance), EmpireSurvivors = Survived(Side.Empire),
            AllianceRetreated = retA, EmpireRetreated = retE,
            Draws = _rng.Draws,
            Steps = _steps,
        };
    }
}
