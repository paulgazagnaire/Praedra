using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Setup;

namespace Rebellion.Core.AI;

/// <summary>One order the AI queued today (for logs and tests).</summary>
public sealed record SubmittedOrder(long Sequence, string Kind, string Detail);

/// <summary>
/// A simple computer opponent: a decent economy bot with a thin military (Classic-style). Call <see cref="PlanDay"/> once per day BEFORE
/// <c>Game.Step()</c>; it queues orders through the modules' Submit methods, exactly the orders a player can give. It never changes
/// state itself, and it keeps no memory between days: everything is re-derived from the game state, so a save needs nothing extra.
///
/// Randomness: the AI never draws from the game's generator (that would change the game's random stream). Its few tie-breaks use a
/// private <see cref="GameRng"/> seeded from (game day, side), so the same game state gives the same orders.
///
/// VISIBILITY: until the fog-of-war rules exist the AI sees everything (enemy fleets, garrisons, the enemy's systems). This is a DEFAULT
/// that must go: the owner decided the AI plays fair once fog exists. Every look at the enemy goes through <see cref="EnemyFleetsAt"/>,
/// <see cref="DefendersAt"/> and <see cref="EnemySystems"/>, so fog can be put in those three places.
/// </summary>
public sealed class SimpleAi
{
    private readonly GameHost _h;
    private readonly Side _side;
    private readonly SimpleAiParams _p;
    private readonly List<SubmittedOrder> _submitted = new();

    // per-day scratch (rebuilt every PlanDay)
    private int _plannedLoad;
    private readonly HashSet<(string, YardFamily)> _busy = new();
    private readonly Dictionary<string, int> _reservedEnergy = new(StringComparer.Ordinal); // lookup only
    private readonly Dictionary<string, int> _reservedRaw = new(StringComparer.Ordinal);    // lookup only
    private readonly Dictionary<int, double> _strength = new();                              // lookup only
    private string? _capital, _rally; private bool _capitalDone, _rallyDone;
    private int[]? _hops;
    private GameRng _rng = new(0);

    public SimpleAi(GameHost host, Side side, SimpleAiParams? p = null) { _h = host; _side = side; _p = p ?? new SimpleAiParams(); }

    public Side Side => _side;
    public SimpleAiParams Params => _p;
    /// <summary>The enemy HQ, when the caller knows it (the Empire hunts it first). Null = unknown. No HQ exists in the core yet.</summary>
    public string? KnownEnemyHq { get; set; }
    /// <summary>The orders queued by the last <see cref="PlanDay"/>.</summary>
    public IReadOnlyList<SubmittedOrder> Submitted => _submitted;

    private Side Enemy => _side == Side.Empire ? Side.Alliance : Side.Empire;
    private GameState S => _h.Game.State;
    private int Today => _h.Game.Day;

    // ================================================================== entry

    public void PlanDay()
    {
        _submitted.Clear(); _plannedLoad = 0; _busy.Clear(); _reservedEnergy.Clear(); _reservedRaw.Clear(); _strength.Clear();
        _capitalDone = _rallyDone = false; _hops = null;
        int day = Today, phase = (int)_side;
        _rng = new GameRng((ulong)day * 2 + (ulong)_side + 0x5A1D, 0xA1);
        if ((day + phase) % _p.EconomyEvery == 0) PlanEconomy();
        if ((day + phase) % _p.ProductionEvery == 0) PlanProduction();
        if ((day + phase) % _p.MilitaryEvery == 0) PlanMilitary();
    }

    private void Note(long seq, string kind, string detail) => _submitted.Add(new SubmittedOrder(seq, kind, detail));

    // ================================================================== what the AI may see (fog goes here)

    /// <summary>Enemy fleets standing at a system. DEFAULT: sees all.</summary>
    private List<Fleet> EnemyFleetsAt(string system) => _h.Fleets.FleetsAt(system).Where(f => f.Side != _side).ToList();
    /// <summary>Regiments of any other side standing on a system. DEFAULT: sees all.</summary>
    private int DefendersAt(string system) => _h.Troops.RegimentsOnSystem(system, _side).Count;
    /// <summary>Systems the other side holds. DEFAULT: sees all.</summary>
    private IEnumerable<string> EnemySystems() => S.SystemOwners.Where(kv => kv.Value == Enemy).Select(kv => kv.Key);

    // ================================================================== helpers

    private bool Held(string s) => _h.Fleets.OwnerOf(s) == _side;
    private IEnumerable<string> HeldSystems() => S.SystemOwners.Where(kv => kv.Value == _side).Select(kv => kv.Key); // sorted ordinal
    private int UnratedSpeed => _h.Construction.Params.UnratedDeliverySpeed;
    private int Budget => _h.Economy.Free(_side) - _plannedLoad - _p.MaintenanceMargin;
    private int Maint(string cls) => _h.Economy.Costs.Get(cls).Maintenance;
    private bool Suspended(string sys) => _h.Support.IsUprising(sys) || _h.Fleets.IsEconomicBlockade(sys, _side);

    private int Ticks(string a, string b, int speed)
    {
        if (a == b) return 0;
        var r = _h.Fleets.FindPath(a, b, speed);
        return r.Ok ? r.TotalTicks : int.MaxValue;
    }

    private double Str(Fleet f)
    {
        if (!_strength.TryGetValue(f.Id, out var v)) _strength[f.Id] = v = _h.Fleets.StrengthOf(f);
        return v;
    }

    private bool Available(string cls)
    {
        if (!_h.Economy.Costs.TryGet(cls, out var row) || row.Cost <= 0) return false;
        if (!(_side == Side.Empire ? row.Empire : row.Alliance)) return false;
        return _h.Construction.IsResearched(_side, cls);
    }

    private static bool IsYardClass(EconObject o, YardFamily fam) =>
        o.Category == ObjectCategory.Facility && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == fam;

    /// <summary>Held systems with a finished, working yard of the family, ordinal order.</summary>
    private List<string> YardSystems(YardFamily fam) =>
        _h.Economy.Objects.Where(o => o.Completed && IsYardClass(o, fam) && o.System != null && Held(o.System) && !Suspended(o.System))
            .Select(o => o.System!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>Yards of the family owned by the side, queued ones included.</summary>
    private int CountYards(YardFamily fam) => _h.Economy.Objects.Count(o => IsYardClass(o, fam) && _h.Economy.OwnerOf(o) == _side);

    private bool CanOrderAt(string system, YardFamily fam) => !_busy.Contains((system, fam)) && _h.Construction.ProjectAt(system, fam) == null;

    private string? Capital()
    {
        if (_capitalDone) return _capital;
        _capitalDone = true;
        if (_side == Side.Empire && Held(Rebellion.Core.Control.ControlModule.CapitalSystem)) return _capital = Rebellion.Core.Control.ControlModule.CapitalSystem;
        // No HQ in the core yet: the held system with the most finished facilities (DEFAULT).
        _capital = HeldSystems().Select(s => (s, n: _h.Economy.Objects.Count(o => o.Completed && o.Category == ObjectCategory.Facility && o.System == s)))
            .Where(t => t.n > 0).OrderByDescending(t => t.n).ThenBy(t => t.s, StringComparer.Ordinal).Select(t => t.s).FirstOrDefault();
        return _capital;
    }

    /// <summary>The staging system: a held shipyard system (not the capital if another exists) nearest the enemy's core. New warships gather here.</summary>
    private string? Rally()
    {
        if (_rallyDone) return _rally;
        _rallyDone = true;
        var yards = YardSystems(YardFamily.Shipyard);
        var cap = Capital();
        var cands = yards.Where(s => s != cap).ToList();
        if (cands.Count == 0) cands = yards;
        if (cands.Count == 0) return _rally = cap;
        string? anchor = KnownEnemyHq;
        anchor ??= EnemySystems().Select(s => (s, n: _h.Economy.Objects.Count(o => o.Completed && o.Category == ObjectCategory.Facility && o.System == s)))
            .OrderByDescending(t => t.n).ThenBy(t => t.s, StringComparer.Ordinal).Select(t => t.s).FirstOrDefault();
        if (anchor == null) return _rally = cands[0];
        _rally = cands.OrderBy(s => Ticks(s, anchor, 100)).ThenBy(s => s, StringComparer.Ordinal).First();
        return _rally;
    }

    // ================================================================== economy

    private void PlanEconomy()
    {
        var eco = _h.Economy;
        foreach (var y in YardSystems(YardFamily.Construction))
        {
            if (!CanOrderAt(y, YardFamily.Construction)) continue;
            var choice = ChooseFacility(y);
            if (choice == null) continue;
            var (kind, dest) = choice.Value;
            string cls = FacilityKinds.ClassName(kind);
            int m = Maint(cls);
            if (m > 0 && Budget < m) continue;
            long seq = _h.Construction.SubmitBuild(_side, y, YardFamily.Construction, cls, 1, dest);
            Note(seq, "Build", $"{cls} at {dest} from {y}");
            _busy.Add((y, YardFamily.Construction));
            _plannedLoad += m;
            _reservedEnergy[dest] = _reservedEnergy.GetValueOrDefault(dest) + 1;
            if (kind == FacilityKind.Mine) _reservedRaw[dest] = _reservedRaw.GetValueOrDefault(dest) + 1;
        }
    }

    private (FacilityKind, string)? ChooseFacility(string from)
    {
        var eco = _h.Economy;
        var order = new List<FacilityKind>();
        int refined = eco.Refined(_side);
        int ships = CountYards(YardFamily.Shipyard), train = CountYards(YardFamily.Training);
        if (ships == 0) order.Add(FacilityKind.OrbitalShipyard);
        if (train == 0) order.Add(FacilityKind.TrainingFacility);
        int mines = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Mine" && eco.OwnerOf(o) == _side);
        int refs = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Refinery" && eco.OwnerOf(o) == _side);
        // rule: m2-maintenance §Capacity (capacity = 50 x min(mines, refineries)): keep the two balanced.
        if (mines <= refs) { order.Add(FacilityKind.Mine); order.Add(FacilityKind.Refinery); }
        else { order.Add(FacilityKind.Refinery); order.Add(FacilityKind.Mine); }
        if (refined >= _p.ExtraYardRefinedReserve)
        {
            if (ships < _p.DesiredShipyards) order.Add(FacilityKind.OrbitalShipyard);
            if (train < _p.DesiredTrainingFacilities) order.Add(FacilityKind.TrainingFacility);
        }
        foreach (var kind in order)
        {
            if (!Available(FacilityKinds.ClassName(kind))) continue;
            int m = Maint(FacilityKinds.ClassName(kind));
            if (m > 0 && Budget < m) continue;
            var site = PickSite(from, kind);
            if (site != null) return (kind, site);
        }
        return null;
    }

    private string? PickSite(string from, FacilityKind kind)
    {
        var eco = _h.Economy;
        bool mine = kind == FacilityKind.Mine;
        string? best = null; int bestScore = int.MinValue;
        foreach (var s in HeldSystems())
        {
            var slots = eco.SlotsOf(s);
            if (!slots.Populated || _h.Support.IsUprising(s)) continue;
            if (eco.FreeEnergy(s) - _reservedEnergy.GetValueOrDefault(s) < 1) continue;
            if (mine && eco.FreeRaw(s) - _reservedRaw.GetValueOrDefault(s) < 1) continue;
            int ticks = Ticks(from, s, UnratedSpeed);
            if (ticks == int.MaxValue) continue;
            int score = slots.Energy * 2 + (mine ? slots.Raw * 3 : 0) + eco.SupportOf(s, _side) / 10 - ticks / 20;
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    // ================================================================== production

    private void PlanProduction()
    {
        PlanGarrison();
        PlanInvasionRegiments();
        PlanShips();
    }

    private string? TrainingYardNear(string dest)
    {
        string? best = null; int bt = int.MaxValue;
        foreach (var t in YardSystems(YardFamily.Training))
        {
            if (!CanOrderAt(t, YardFamily.Training)) continue;
            int ticks = Ticks(t, dest, UnratedSpeed);
            if (ticks < bt) { bt = ticks; best = t; }
        }
        return best;
    }

    private void OrderRegiments(string yard, string cls, int count, string dest)
    {
        long seq = _h.Construction.SubmitBuild(_side, yard, YardFamily.Training, cls, count, dest);
        Note(seq, "Build", $"{count} x {cls} at {yard} for {dest}");
        _busy.Add((yard, YardFamily.Training));
        _plannedLoad += count * Maint(cls);
    }

    /// <summary>Regiments queued per destination (units of projects not yet finished).</summary>
    private Dictionary<string, int> PendingRegiments()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pr in S.Construction.AllProjects())
            if (pr.Side == _side && pr.Family == YardFamily.Training)
                d[pr.Destination] = d.GetValueOrDefault(pr.Destination) + pr.Units.Count;
        return d;
    }

    // rule: m2-uprisings-and-garrison §Required garrison N: keep N regiments on every system where the support is low.
    private void PlanGarrison()
    {
        string cls = _p.GarrisonRegiment(_side);
        if (!Available(cls)) return;
        var pending = PendingRegiments();
        foreach (var s in HeldSystems())
        {
            int need = _h.Support.RequiredGarrison(s);
            if (need == 0) continue;
            int deficit = need - _h.Support.RegimentCount(s) - pending.GetValueOrDefault(s);
            if (deficit <= 0) continue;
            int count = Math.Min(deficit, _p.GarrisonBatch);
            if (Budget < count * Maint(cls)) continue;
            var yard = TrainingYardNear(s);
            if (yard == null) return;
            OrderRegiments(yard, cls, count, s);
        }
    }

    private void PlanInvasionRegiments()
    {
        var rally = Rally();
        string cls = _p.InvasionRegiment(_side);
        if (rally == null || !Available(cls)) return;
        int have = _h.Economy.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == _side && o.AboardShip != 0) +SpareRegimentsAt(rally);
        have += PendingRegiments().GetValueOrDefault(rally);
        if (have >= _p.InvasionRegimentTarget) return;
        int count = Math.Min(_p.InvasionRegimentTarget - have, _p.GarrisonBatch);
        if (Budget < count * Maint(cls)) return;
        var yard = TrainingYardNear(rally);
        if (yard != null) OrderRegiments(yard, cls, count, rally);
    }

    /// <summary>Landed regiments of the side on a system above what its garrison needs.</summary>
    private List<int> SpareRegimentIds(string system)
    {
        var ids = _h.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == _side && o.System == system && o.AboardShip == 0)
            .Select(o => o.Id).ToList();
        int keep = Held(system) ? _h.Support.RequiredGarrison(system) : 0;
        return ids.Count > keep ? ids.Skip(keep).ToList() : new List<int>();
    }
    private int SpareRegimentsAt(string system) => SpareRegimentIds(system).Count;

    private void PlanShips()
    {
        var eco = _h.Economy;
        var rally = Rally(); var cap = Capital();
        int troopCap = 0, berths = 0, fighters = 0;
        foreach (var o in eco.Objects)
        {
            if (eco.OwnerOf(o) != _side) continue;
            if (o.Category == ObjectCategory.CapitalShip && _h.Fleets.ShipClasses.TryGetValue(o.Class, out var sc)) { troopCap += sc.TroopCapacity; berths += sc.FighterCapacity; }
            else if (o.Category == ObjectCategory.Fighter) fighters++;
        }
        double home = 0;
        if (cap != null) foreach (var f in _h.Fleets.FleetsAt(cap)) if (f.Side == _side) home += Str(f);
        bool homeWeak = home * 100 < _p.HomeDefenceStrength;

        foreach (var y in YardSystems(YardFamily.Shipyard))
        {
            if (!CanOrderAt(y, YardFamily.Shipyard)) continue;
            string? dest = homeWeak && cap != null ? cap : rally;
            if (dest != null && dest != y && Ticks(y, dest, UnratedSpeed) == int.MaxValue) dest = null;
            string? destArg = dest == y ? null : dest;

            string? cls = null; int count = 1;
            string transport = _p.Transport(_side);
            if (troopCap < _p.TroopCapacityTarget && Available(transport))
            {
                cls = transport;
                int per = Math.Max(1, _h.Fleets.ShipClasses[transport].TroopCapacity);
                count = Math.Min(2, (_p.TroopCapacityTarget - troopCap + per - 1) / per);
            }
            else if (berths - fighters >= 2 && Available(_p.Fighter(_side)))
            {
                cls = _p.Fighter(_side);
                count = Math.Min(berths - fighters, _p.FightersPerOrder);
            }
            else
            {
                foreach (var c in _p.HeavyWarships(_side).Concat(_p.Warships(_side)))
                {
                    if (!Available(c)) continue;
                    int cost = eco.Costs.Get(c).Cost;
                    bool heavy = cost > _p.HeavyShipCost;
                    if (heavy && eco.Refined(_side) < cost * 2) continue;
                    if (Budget < Maint(c)) continue;
                    cls = c; break;
                }
            }
            if (cls == null) continue;
            while (count > 1 && Budget < count * Maint(cls)) count--;
            if (Budget < count * Maint(cls)) continue;
            long seq = _h.Construction.SubmitBuild(_side, y, YardFamily.Shipyard, cls, count, destArg);
            Note(seq, "Build", $"{count} x {cls} at {y} for {dest ?? y}");
            _busy.Add((y, YardFamily.Shipyard));
            _plannedLoad += count * Maint(cls);
            var cr = eco.Costs.Get(cls);
            if (cr.Category == ObjectCategory.CapitalShip) troopCap += count * _h.Fleets.ShipClasses[cls].TroopCapacity;
        }
    }

    // ================================================================== military

    private bool Pending(Fleet f) => _h.Bombardment.HasPending(f.Id) || _h.Assault.HasPending(f.Id);

    private bool Move(Fleet f, string dest)
    {
        if (f.At == null || dest == f.At || f.Transit != null) return false;
        int speed = _h.Fleets.SpeedOf(f);
        if (speed <= 0 || !_h.Fleets.FindPath(f.At, dest, speed).Ok) return false;
        long seq = _h.Fleets.SubmitMove(_side, f.Id, dest);
        Note(seq, "Move", $"fleet {f.Id} {f.At} -> {dest}");
        return true;
    }

    private sealed class Pile
    {
        public required Fleet Leader { get; init; }
        public required List<Fleet> Members { get; init; }
        public double Strength; public int Aboard, FreeSlots, Berths; public double HullPct = 100;
    }

    private void PlanMilitary()
    {
        var idle = S.Fleets.Values.Where(f => f.Side == _side && f.Transit == null && f.At != null && f.Ships.Count > 0 && !Pending(f)).ToList();
        var cap = Capital();
        foreach (var g in idle.GroupBy(f => f.At!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = g.OrderBy(f => f.Id).ToList();
            if (!Held(g.Key))
            {
                foreach (var f in list) DecideAtTarget(f);
                continue;
            }
            var strike = list;
            if (g.Key == cap)
            {
                // The home defence: the lowest ids up to the home strength stay and merge; the rest are strike fleets.
                double acc = 0; var home = new List<Fleet>(); strike = new List<Fleet>();
                foreach (var f in list)
                {
                    if (acc * 100 < _p.HomeDefenceStrength) { home.Add(f); acc += Str(f); } else strike.Add(f);
                }
                for (int i = 1; i < home.Count; i++) Merge(home[i], home[0]);
            }
            if (strike.Count == 0) continue;
            var leader = strike[0];
            for (int i = 1; i < strike.Count; i++) Merge(strike[i], leader);
            DecideOwn(MakePile(leader, strike));
        }
    }

    private void Merge(Fleet from, Fleet into)
    {
        long seq = _h.Fleets.SubmitMergeFleets(_side, from.Id, into.Id);
        Note(seq, "Merge", $"fleet {from.Id} into {into.Id}");
    }

    private Pile MakePile(Fleet leader, List<Fleet> members)
    {
        var p = new Pile { Leader = leader, Members = members };
        double hullSum = 0; int ships = 0;
        foreach (var f in members)
        {
            p.Strength += Str(f);
            p.Aboard += _h.Troops.RegimentsAboard(f).Count;
            p.FreeSlots += _h.Fleets.FreeTroopSlots(f);
            p.Berths += _h.Fleets.FreeBerths(f);
            foreach (var id in f.Ships)
            {
                var o = _h.Economy.Find(id)!; var c = _h.Fleets.ClassOf(o)!;
                hullSum += c.Hull > 0 ? Math.Min(100.0, 100.0 * o.Hull / c.Hull) : 100; ships++;
            }
        }
        if (ships > 0) p.HullPct = hullSum / ships;
        return p;
    }

    private int Need(int defenders) =>
        defenders == 0 ? 1 : Math.Max(_p.MinLaunchRegiments, (defenders * _p.RegimentsPerDefenderPercent + 99) / 100);

    private bool HasShipyard(string system) => YardSystems(YardFamily.Shipyard).Contains(system);

    // A strike fleet at a system the side holds: take on regiments and fighters, then leave, wait, or go to the rally system.
    private void DecideOwn(Pile p)
    {
        var f = p.Leader; string at = f.At!;
        var rally = Rally() ?? Capital();

        // regiments waiting here above the garrison, and fighters based here
        int aboard = p.Aboard;
        var spare = SpareRegimentIds(at);
        int take = Math.Min(spare.Count, p.FreeSlots);
        if (take > 0)
        {
            long seq = _h.Troops.SubmitLoad(_side, f.Id, spare.Take(take));
            Note(seq, "LoadRegiments", $"{take} onto fleet {f.Id} at {at}");
            aboard += take;
        }
        int bt = p.Berths;
        if (bt > 0)
        {
            var sq = _h.Fleets.BasedSquadrons(at, _side).Select(o => o.Id).Take(bt).ToList();
            if (sq.Count > 0)
            {
                long seq = _h.Fleets.SubmitLoadSquadrons(_side, f.Id, sq);
                Note(seq, "LoadSquadrons", $"{sq.Count} onto fleet {f.Id} at {at}");
            }
        }

        bool damaged = p.HullPct < _p.RepairBelowPercent;
        if (damaged)
        {
            if (!HasShipyard(at) && rally != null) Move(f, rally); // repair happens at a shipyard
            return;
        }
        bool strong = p.Strength * 100 >= _p.LaunchStrength;
        if (strong)
        {
            // relief of a held system first, then an attack (which needs regiments)
            var t = PickTarget(f, p.Strength, aboard, at);
            if (t != null && Move(f, t)) return;
        }
        if (rally != null && at != rally) Move(f, rally); // gather (and reload) at the rally system
    }

    private int RecentBombardments(string system)
    {
        int n = 0, since = Today - 30;
        var ev = _h.Game.Events;
        for (int i = ev.Count - 1; i >= 0 && ev[i].Day >= since; i--)
            if (ev[i] is BombardmentResolved b && b.System == system) n++;
        return n;
    }

    // A fleet at a system it does not hold: fight on, bombard, assault, or go elsewhere.
    private void DecideAtTarget(Fleet f)
    {
        string at = f.At!;
        var rally = Rally() ?? Capital();
        double mine = _h.Fleets.FleetsAt(at).Where(x => x.Side == _side).Sum(Str);
        var enemies = EnemyFleetsAt(at);
        if (enemies.Count > 0)
        {
            double e = enemies.Sum(Str);
            if (e > 0 && mine * 100 < e * _p.AttackOddsPercent && rally != null) Move(f, rally); // too strong for us: leave
            return; // else the battle takes its course
        }
        int aboard = _h.Troops.RegimentsAboard(f).Count;
        int d = DefendersAt(at);
        if (_h.Defenses.AssaultForbidden(at)) { Leave(f); return; } // rule: m3-planetary-defenses (assault forbidden, 2+ shield generators)
        // rule: m3-assault §Order and preconditions: a fleet has one order at a time, so shell one day and land the next.
        if (d > 0 && _h.Bombardment.Power(f) > 0 && RecentBombardments(at) < _p.MaxBombardmentsPer30Days)
        {
            long seq = _h.Bombardment.SubmitBombard(_side, f.Id, at, BombardCategory.Military);
            Note(seq, "Bombard", $"fleet {f.Id} shells {at}");
            return;
        }
        if (aboard >= 1 && aboard >= Need(d))
        {
            long seq = _h.Assault.SubmitAssault(_side, f.Id, at);
            Note(seq, "Assault", $"fleet {f.Id} lands on {at}");
            return;
        }
        Leave(f);
    }

    private void Leave(Fleet f)
    {
        var rally = Rally() ?? Capital();
        double s = Str(f); int aboard = _h.Troops.RegimentsAboard(f).Count;
        var t = PickTarget(f, s, aboard, f.At!);
        if (t != null && Move(f, t)) return;
        if (rally != null) Move(f, rally);
    }

    private int[] Hops()
    {
        if (_hops != null) return _hops;
        var g = _h.Data.Galaxy; var dist = new int[g.Systems.Count]; Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        foreach (var s in HeldSystems()) if (g.TryFindSystem(s, out var i)) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (var n in g.NeighboursOf(c)) if (dist[n.System] == int.MaxValue) { dist[n.System] = dist[c] + 1; q.Enqueue(n.System); }
        }
        return _hops = dist;
    }

    /// <summary>Best system to send a fleet to: relief of a held system under enemy fleets, or an enemy or neutral system near the border with a weak defence.</summary>
    private string? PickTarget(Fleet f, double strength, int aboard, string from)
    {
        var g = _h.Data.Galaxy; var hops = Hops();
        int speed = _h.Fleets.SpeedOf(f);
        if (speed <= 0) return null;
        bool canBombard = _h.Bombardment.Power(f) > 0;
        var scored = new List<(string Sys, long Score)>();
        foreach (var sys in g.Systems)
        {
            string s = sys.Name;
            if (s == from) continue;
            var enemies = EnemyFleetsAt(s);
            double e = enemies.Sum(Str);
            if (e > 0 && strength * 100 < e * _p.AttackOddsPercent) continue;
            int ticks = Ticks(from, s, speed);
            if (ticks == int.MaxValue) continue;
            long score;
            if (Held(s))
            {
                if (e <= 0) continue;
                score = 5_000_000L / (ticks + 20); // relief: an enemy fleet sits on our system
            }
            else
            {
                if (hops[sys.Index] > _p.TargetHops) continue;
                var slots = _h.Economy.SlotsOf(s);
                if (!slots.Populated) continue;
                if (_h.Defenses.AssaultForbidden(s)) continue;
                int d = DefendersAt(s);
                if (aboard < Need(canBombard ? d / 2 : d) || aboard < 1) continue;
                long value = 10 + slots.Energy + 2 * slots.Raw;
                if (_h.Fleets.OwnerOf(s) == Enemy) value += 10 + 2 * _h.Economy.Objects.Count(o => o.Completed && o.Category == ObjectCategory.Facility && o.System == s);
                if (s == KnownEnemyHq) value += 200;
                score = value * 1000 / (ticks + 20);
            }
            scored.Add((s, score));
        }
        if (scored.Count == 0) return null;
        long best = scored.Max(t => t.Score);
        // tie-break (private generator): any target within 5% of the best
        var top = scored.Where(t => t.Score * 100 >= best * 95).OrderBy(t => t.Sys, StringComparer.Ordinal).ToList();
        return top[_rng.NextInt(0, top.Count - 1)].Sys;
    }
}
