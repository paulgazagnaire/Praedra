using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Battle;

/// <summary>
/// Battle state kept in the game state (saved and hashed): each side's standing OnContact answer, and the objects that took part in a battle
/// in the Step of <see cref="FoughtDay"/>. rule: m4-battle-classic §1.3 (OnContact is per-side game state); m4-retreat-damage-repair §5 (no repair on a battle day).
/// </summary>
public sealed class BattleState
{
    public int EmpireOnContact { get; set; }
    public int AllianceOnContact { get; set; }
    /// <summary>The Step (K) whose battles fill <see cref="Fought"/>; -1 = none yet.</summary>
    public int FoughtDay { get; set; } = -1;
    /// <summary>Object ids (ships and squadrons) that took part in a battle in Step <see cref="FoughtDay"/>, ascending. Stale after that Step: the query checks the day.</summary>
    public List<int> Fought { get; set; } = new();

    public OnContactMode OnContact(Side s) => (OnContactMode)(s == Side.Empire ? EmpireOnContact : AllianceOnContact);

    internal void CopyFrom(BattleState o)
    {
        EmpireOnContact = o.EmpireOnContact; AllianceOnContact = o.AllianceOnContact;
        FoughtDay = o.FoughtDay; Fought = new List<int>(o.Fought);
    }
}

/// <summary>
/// The Classic space battle in the day Step: when both sides are present at a system, step 5 (after the fleet contacts, before bombardment 5.1 and
/// assault 5.2) resolves it in full with <see cref="ClassicBattle"/>, writes hull and counters back, removes the dead, and moves a retreating side
/// (retreat, stalemate, what persists). Holds settings only; state is <see cref="BattleState"/>. The only draw from the game generator is one
/// seed per battle, taken in system-name order; the battle runs on its own <see cref="GameRng"/>.
/// rule: m4-battle-classic (whole sheet); m4-retreat-damage-repair §1 to §4.
/// </summary>
public sealed class BattleModule
{
    private readonly Game _game;
    private readonly Galaxy _galaxy;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;

    public BattleParams Params { get; }

    private BattleModule(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, BattleParams p)
    {
        _game = game; _galaxy = galaxy; _fleets = fleets; _eco = eco; Params = p;
    }

    /// <summary>Register the SetOnContact handler and the step 5 hook. Attach after <see cref="FleetModule.Attach"/> (its contact and blockade hook runs first in slot 5).</summary>
    public static BattleModule Attach(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, BattleParams? p = null)
    {
        var m = new BattleModule(game, galaxy, fleets, eco, p ?? new BattleParams());
        game.RegisterOrderHandler<SetOnContactOrder>(m.HandleSetOnContact);
        game.RegisterHook("5", m.Resolve);
        return m;
    }

    private BattleState B => _game.State.Battle;
    private GameState State => _game.State;
    private EconomyState E => _game.State.Economy;

    /// <summary>The side's standing answer to the Battle Alert (the AI side is always Simulate). rule: m4-battle-classic §1.3.</summary>
    public OnContactMode OnContactOf(Side side) => IsAi(side) ? OnContactMode.Simulate : B.OnContact(side);

    private bool IsAi(Side s) => s == Side.Empire ? Params.EmpireIsAi : Params.AllianceIsAi;

    public long SubmitSetOnContact(Side side, OnContactMode mode) => _game.Submit(seq => new SetOnContactOrder(seq, side, mode));

    /// <summary>
    /// True when this ship or squadron took part in a battle in the Step being run (the repair slot 6.8 reads it: no repair on a battle day).
    /// False between Steps (the day has moved on), so it is false at every save point and the list it reads is saved state.
    /// rule: m4-retreat-damage-repair §5 (not in a battle this Step).
    /// </summary>
    public bool FoughtThisStep(int objectId) => B.FoughtDay == _game.Day && B.Fought.BinarySearch(objectId) >= 0;

    // rule: m4-battle-classic §1.3 (SetOnContact {side, mode}); the AI side stays Simulate
    private OrderResult HandleSetOnContact(StepContext ctx, SetOnContactOrder o)
    {
        if (!Enum.IsDefined(o.Mode)) return OrderResult.Reject(RejectReason.BadValue);
        if (IsAi(o.Side)) return OrderResult.Reject(BattleReject.AiSide);
        if (o.Side == Side.Empire) B.EmpireOnContact = (int)o.Mode; else B.AllianceOnContact = (int)o.Mode;
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // ================================================================== presence

    private bool Present(string system, Side side) =>
        State.Fleets.Values.Any(f => f.At == system && f.Side == side) || _fleets.BasedSquadrons(system, side).Any();

    private sealed class Force
    {
        public string System = "";
        public ulong Seed;
        public List<BattleUnit> Units = new();
        public HashSet<int> Fleets = new(); // lookup only
    }

    // ================================================================== the hook (step 5)

    private void Resolve(StepContext ctx)
    {
        int day = ctx.Day + 1;
        B.FoughtDay = ctx.Day; B.Fought = new List<int>();

        // rule: m4-battle-classic §1.2 (systems in ordinal name order; one battle per system per day)
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in State.Fleets.Values) if (f.At != null) names.Add(f.At);
        foreach (var o in E.Objects.Values) if (o.Category == ObjectCategory.Fighter && o.Completed && o.System != null && o.AboardShip == 0) names.Add(o.System);
        var systems = names.Where(s => Present(s, Side.Alliance) && Present(s, Side.Empire)).ToList();
        if (systems.Count == 0) return;

        // rule: m4-battle-classic §1.4 (one draw per battle, in system-name order, also when none is needed). Forces are fixed now, so a fleet
        // that retreats into another battle's system this Step is not in that battle (it meets the enemy next Step).
        var forces = new List<Force>();
        foreach (var sys in systems)
        {
            uint seed = unchecked((uint)ctx.Rng.NextInt(int.MinValue, int.MaxValue)); // exactly one native draw
            forces.Add(BuildForce(sys, seed));
        }

        var touched = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var force in forces) RunBattle(ctx, day, force, touched);
        foreach (var sys in touched) _fleets.RefreshBlockadeFlag(ctx, sys, day); // rule: m4-battle-classic §1.2 (blockade flags recomputed after the battle)
    }

    private Force BuildForce(string sys, ulong seed)
    {
        var force = new Force { System = sys, Seed = seed };
        foreach (var side in new[] { Side.Alliance, Side.Empire })
        {
            var fleets = State.Fleets.Values.Where(f => f.At == sys && f.Side == side).ToList();
            var ships = fleets.SelectMany(f => f.Ships).OrderBy(i => i).ToList();
            var shipSet = new HashSet<int>(ships);
            foreach (var f in fleets) force.Fleets.Add(f.Id);
            var squads = new SortedSet<int>(fleets.SelectMany(f => f.Squadrons));
            foreach (var o in E.Objects.Values)
                if (o.Category == ObjectCategory.Fighter && o.Completed && o.Side == side &&
                    ((o.AboardShip != 0 && shipSet.Contains(o.AboardShip)) || (o.AboardShip == 0 && o.System == sys))) squads.Add(o.Id);
            foreach (var id in ships) force.Units.Add(MakeUnit(force.Units.Count, E.Objects[id], side, true));
            foreach (var id in squads) force.Units.Add(MakeUnit(force.Units.Count, E.Objects[id], side, false));
        }
        return force;
    }

    // rule: m4-battle-classic §2 (start state: hull and counters as stored; shield = cap; squadron whole)
    private BattleUnit MakeUnit(int index, EconObject o, Side side, bool capital)
    {
        var cls = _fleets.ClassOf(o) ?? throw new InvalidOperationException($"No ship class '{o.Class}'.");
        var u = new BattleUnit { Index = index, ObjectId = o.Id, Side = side, Class = cls, IsCapital = capital };
        if (capital)
        {
            u.HullMax = cls.Hull; u.Hull = Math.Min(o.Hull, cls.Hull);
            if (o.Hits != null) for (int i = 0; i < u.Hits.Length && i < o.Hits.Count; i++) u.Hits[i] = o.Hits[i];
            u.Shield = ClassicRates.ShieldCap(cls, u.HullRatio);
        }
        else
        {
            u.HullMax = u.Hull = Params.SquadronHull;
            u.Shield = cls.ShieldStrength;
            u.Carrier = o.AboardShip;
        }
        return u;
    }

    // ================================================================== one battle

    private void RunBattle(StepContext ctx, int day, Force force, SortedSet<string> touched)
    {
        string sys = force.System;
        var alliance = force.Units.Where(u => u.Side == Side.Alliance).Select(u => u.ObjectId).ToList();
        var empire = force.Units.Where(u => u.Side == Side.Empire).Select(u => u.ObjectId).ToList();
        ctx.Emit((i, _) => new BattleStarted(i, day, sys, new ValueList<int>(alliance), new ValueList<int>(empire)));
        var alert = new OnContactMode[2];
        foreach (var s in new[] { Side.Alliance, Side.Empire })
        {
            var mode = OnContactOf(s);
            alert[(int)s] = mode;
            ctx.Emit((i, _) => new BattleChoice(i, day, sys, s, mode));
        }

        // retreat destinations, fixed before any unit moves. rule: m4-retreat-damage-repair §3
        var dest = new Dictionary<Side, SortedDictionary<string, int>>();
        foreach (var s in new[] { Side.Alliance, Side.Empire }) dest[s] = ValidRetreatSystems(sys, s);
        var has = new bool[2];
        foreach (var s in dest.Keys) has[(int)s] = dest[s].Count > 0;

        var battle = new ClassicBattle(Params, force.Units, new GameRng(force.Seed), has, alert);
        var result = battle.Run();

        // damage written back to every surviving ship. rule: m4-battle-classic §10 (stored hull = max(1, round half up); counters as they stand)
        foreach (var u in force.Units)
        {
            if (!u.IsCapital || u.Destroyed) continue;
            var o = E.Objects[u.ObjectId];
            o.Hull = (int)Math.Min(u.HullMax, Math.Max(1.0, Math.Floor(u.Hull + 0.5)));
            o.Hits = new List<int>(u.Hits);
        }

        var left = new Dictionary<Side, bool> { [Side.Alliance] = false, [Side.Empire] = false };
        foreach (var step in result.Steps)
        {
            switch (step)
            {
                case UnitDestroyedStep d: DestroyUnit(ctx, day, sys, d); break;
                case RetreatRefusedStep r: { var side = r.Side; var why = r.Reason; ctx.Emit((i, _) => new RetreatRefused(i, day, sys, side, why)); break; }
                case SideRetreatedStep r: left[r.Side] = true; break;
            }
        }

        // relocation of retreating sides: sets were computed above, before anything moved
        touched.Add(sys);
        foreach (var side in new[] { Side.Alliance, Side.Empire })
            if (left[side]) Relocate(ctx, day, force, side, dest[side], touched);

        foreach (var u in force.Units) B.Fought.Add(u.ObjectId);
        B.Fought.Sort();

        var res = new BattleResult(sys, result.Seconds, result.AllianceOutcome, result.EmpireOutcome,
            result.AllianceLosses, result.EmpireLosses, result.AllianceSurvivors, result.EmpireSurvivors);
        ctx.Emit((i, _) => new BattleEnded(i, day, res));
    }

    /// <summary>A destroyed unit leaves the registry, its fleet (dissolved if empty) and, for a ship, takes its regiments with it. rule: m4-battle-classic §10; m3-fleets Q6.</summary>
    private void DestroyUnit(StepContext ctx, int day, string sys, UnitDestroyedStep d)
    {
        int id = d.Unit.ObjectId, sec = d.Second; var why = d.Reason;
        if (!E.Objects.TryGetValue(id, out var obj)) return;
        if (d.Unit.IsCapital)
            foreach (var reg in E.Objects.Values.Where(r => r.AboardShip == id && r.Category == ObjectCategory.Regiment).OrderBy(r => r.Id).ToList())
            {
                int rid = reg.Id;
                ctx.Emit((i, _) => new UnitDestroyed(i, day, sys, rid, sec, UnitLossReason.ShipLost));
                _eco.DestroyUnit(ctx, day, reg);
            }
        ctx.Emit((i, _) => new UnitDestroyed(i, day, sys, id, sec, why));
        _eco.DestroyUnit(ctx, day, obj);
    }

    // ================================================================== retreat destinations and relocation (m4-retreat-damage-repair §3)

    /// <summary>
    /// The valid retreat systems of a side with the distance used to rank them: joined to the battle system by a lane (LaneAdjacent) or any
    /// reachable system (Nearest); owned by the side or neutral; no enemy fleet and no enemy based squadron there. Empty for NoRetreat.
    /// </summary>
    private SortedDictionary<string, int> ValidRetreatSystems(string sys, Side side)
    {
        var set = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (Params.RetreatMode == RetreatMode.NoRetreat || !_galaxy.TryFindSystem(sys, out int from)) return set;
        var dist = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (Params.RetreatMode == RetreatMode.LaneAdjacent)
            foreach (var nb in _galaxy.NeighboursOf(from)) dist[_galaxy.Systems[nb.System].Name] = nb.Lane.BaseDistance;
        else
            dist = PathDistances(from);
        var enemy = side == Side.Alliance ? Side.Empire : Side.Alliance;
        foreach (var (name, d) in dist)
        {
            if (name == sys) continue;
            if (State.SystemOwners.TryGetValue(name, out var owner) && owner != side) continue;
            if (Present(name, enemy)) continue;
            set[name] = d;
        }
        return set;
    }

    /// <summary>Shortest lane path distance (sum of lane base distances) from a system to every system. rule: m4-retreat-damage-repair §3 (Nearest).</summary>
    private SortedDictionary<string, int> PathDistances(int from)
    {
        int n = _galaxy.Systems.Count;
        var best = new long[n]; Array.Fill(best, long.MaxValue);
        best[from] = 0;
        var queue = new PriorityQueue<int, (long, int)>();
        queue.Enqueue(from, (0, from));
        while (queue.TryDequeue(out var u, out var pri))
        {
            if (pri.Item1 != best[u]) continue;
            foreach (var nb in _galaxy.NeighboursOf(u))
            {
                long t = best[u] + nb.Lane.BaseDistance;
                if (t < best[nb.System]) { best[nb.System] = t; queue.Enqueue(nb.System, (t, nb.System)); }
            }
        }
        var r = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++) if (best[i] != long.MaxValue && i != from) r[_galaxy.Systems[i].Name] = (int)Math.Min(best[i], int.MaxValue);
        return r;
    }

    /// <summary>Previous system if valid (LaneAdjacent), else the smallest distance, ties by ordinal name. rule: m4-retreat-damage-repair §3.</summary>
    private string Pick(SortedDictionary<string, int> set, string? previous)
    {
        if (Params.RetreatMode == RetreatMode.LaneAdjacent && previous != null && set.ContainsKey(previous)) return previous;
        string best = ""; int bd = int.MaxValue;
        foreach (var (name, d) in set) // ordinal name order, strict < keeps the smaller name on a tie
            if (d < bd) { bd = d; best = name; }
        return best;
    }

    private void Relocate(StepContext ctx, int day, Force force, Side side, SortedDictionary<string, int> set, SortedSet<string> touched)
    {
        string from = force.System;
        if (set.Count == 0) return;
        foreach (var fid in force.Fleets.OrderBy(i => i))
        {
            if (!State.Fleets.TryGetValue(fid, out var f) || f.Side != side || f.At != from) continue;
            string to = Pick(set, f.PreviousSystem);
            f.At = to; f.Transit = null; f.Spare = false; f.PreviousSystem = from; // order cancelled, no forced stop, no travel
            touched.Add(to);
            int fleetId = f.Id;
            var ships = new ValueList<int>(f.Ships.ToList());
            var squads = new ValueList<int>(E.Objects.Values.Where(o => o.Category == ObjectCategory.Fighter && o.AboardShip != 0 && f.Ships.Contains(o.AboardShip)).Select(o => o.Id).ToList());
            ctx.Emit((i, _) => new FleetRetreated(i, day, fleetId, from, to, ships, squads));
        }
        // squadrons that found a berth ride that ship; lone jumpers are based at the destination (smallest-distance rule)
        var alone = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var u in force.Units.Where(u => u.Side == side && u.Left && !u.IsCapital))
        {
            if (!E.Objects.TryGetValue(u.ObjectId, out var o)) continue;
            if (u.NewCarrier != 0) { o.AboardShip = u.NewCarrier; o.System = null; continue; }
            if (!u.JumpedAlone) continue;
            string to = Pick(set, null);
            o.AboardShip = 0; o.System = to;
            if (!alone.TryGetValue(to, out var l)) alone[to] = l = new List<int>();
            l.Add(o.Id);
        }
        foreach (var (to, ids) in alone)
        {
            touched.Add(to);
            var v = new ValueList<int>(ids.OrderBy(i => i).ToList());
            ctx.Emit((i, _) => new SquadronsRelocated(i, day, v, to));
        }
    }
}
