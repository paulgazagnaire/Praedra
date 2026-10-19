using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Fog;

namespace Rebellion.Core.AI;

/// <summary>
/// The parts of the simple AI that exist only behind the fog (<see cref="SimpleAiParams.UseFog"/>): scouting, estimates from the last-seen
/// picture, the Empire's belief map of the Rebel HQ and the Alliance's risk-based HQ moves. Everything about the other side is read from
/// the side's <see cref="KnowledgeView"/> (m5-fog-and-hq §N8, the AI plays fair: Decided); own state is read as before. Like the rest of the
/// AI it keeps no plans between days: belief, risk and scout roles are re-derived every day from the view and the side's own fleets (the few
/// events it reads back come from the side's <see cref="AiMemory"/>, which a save carries).
/// Guidance: specs/ai/07 §7 (search and hide), ai/12 P7 and D1 (Decided YES). Every threshold is a <see cref="SimpleAiParams"/> DEFAULT.
/// </summary>
public sealed partial class SimpleAi
{
    // per-day scratch (cleared in ClearFogScratch, called from PlanDay)
    private Dictionary<string, int>? _belief; private int _beliefMax;
    private List<Fleet>? _scouts;
    private Dictionary<string, int>? _risk;
    private readonly Dictionary<string, List<Force>> _targetForces = new(StringComparer.Ordinal);   // lookup only
    private readonly Dictionary<(string, string, int), int> _ticks = new();                          // lookup only

    private readonly Dictionary<string, List<DefenderSeen>> _missionDefenders = new(StringComparer.Ordinal); // lookup only

    private void ClearFogScratch()
    {
        _belief = null; _beliefMax = 0; _scouts = null; _risk = null; _targetForces.Clear(); _ticks.Clear(); _missionDefenders.Clear();
    }

    /// <summary>Travel ticks, cached for the day (the pathfinder is pure).</summary>
    private int TicksCached(string a, string b, int speed)
    {
        if (_ticks.TryGetValue((a, b, speed), out var t)) return t;
        return _ticks[(a, b, speed)] = Ticks(a, b, speed);
    }

    private int AgeOf<T>(Seen<T> s) => s.AgeDays ?? Today - s.Stamp;

    /// <summary>Percent asked on top of an estimate from a picture this old: 100 fresh, 100 + FogStaleMarginPercent at StaleDays and older.</summary>
    private int StalePercent(int age) => 100 + _p.FogStaleMarginPercent * Math.Clamp(age, 0, _p.StaleDays) / Math.Max(1, _p.StaleDays);

    private bool Armed(FleetPicture fp) => fp.ShipCount > 0 && ForceOf("", fp) is var x && x.Units.Count > 0 && _h.Fleets.Strength.Of(x.Units, RefUnits()) > 0;

    // ================================================================== estimates from the last-seen picture

    /// <summary>
    /// Enemy fleets the side believes stand at a system, for choosing targets: group E of the picture up to FogFleetMemoryDays old, whatever
    /// the knowledge level (a stale sighting is the best guess; the odds asked grow with its age, <see cref="TargetOdds"/>). No fog: the truth.
    /// </summary>
    private List<Force> TargetFleetsAt(string system)
    {
        if (_view == null) return EnemyFleetsAt(system);
        if (_targetForces.TryGetValue(system, out var cached)) return cached;
        var list = new List<Force>();
        if (_view.Picture(system)?.Fleets is { } e && AgeOf(e) <= _p.FogFleetMemoryDays)
            foreach (var fp in e.Value) if (fp.Side != _side) list.Add(ForceOf(system, fp));
        return _targetForces[system] = list;
    }

    /// <summary>The odds asked to attack fleets at a system: <see cref="OddsAt"/>, and with fog raised by the age of the fleet picture.</summary>
    private double TargetOdds(string system)
    {
        double odds = OddsAt(system, _p.AttackOddsPercent);
        if (_view?.Picture(system)?.Fleets is { } e) odds = odds * StalePercent(AgeOf(e)) / 100;
        return odds;
    }

    /// <summary>Regiments killed by bombardments at each system, by day (the side's own bombardment reports in its event feed).</summary>
    private int KillsSince(string system, int sinceDay)
    {
        int n = 0;
        foreach (var b in NotesSince(AiNoteKind.Bombardment, sinceDay)) if (b.System == system) n += b.Value;
        return n;
    }

    /// <summary>
    /// Fog: regiments of the other side believed on a system. From group D of the picture plus the staleness margin, less the regiments the
    /// side's own bombardments killed there since that look; never seen, or an enemy-held system seen more than FogGroundForgetDays ago: a
    /// guess (larger for the system that wins the game), less the kills of the last FogKillMemoryDays.
    /// </summary>
    private int FogDefendersAt(string system)
    {
        var g = _view!.Picture(system)?.Ground;
        // QA-5: a ground count older than FogGroundForgetDays is no better than a guess (the garrison seen years ago may be long gone)
        if (g != null && _p.FogGroundForgetDays > 0 && AgeOf(g) > _p.FogGroundForgetDays && IsEnemyHeld(system)) g = null;
        if (g != null)
        {
            int d = g.Value.Where(x => x.Side != _side).Sum(x => x.Total);
            d = (d * StalePercent(AgeOf(g)) + 99) / 100;
            return Math.Max(0, d - KillsSince(system, g.Stamp + 1));
        }
        if (!IsEnemyHeld(system)) return 0;
        int guess = system == WinTarget() ? _p.FogUnknownWinDefenders : _p.FogUnknownDefenders;
        return Math.Max(0, guess - KillsSince(system, Today - _p.FogKillMemoryDays));
    }

    /// <summary>Tests: the regiments the AI would assume on a system today (DefendersAt as PlanDay sees it).</summary>
    internal int DefendersEstimate(string system)
    {
        _view = _p.UseFog ? _h.Fog.ViewFor(_side) : null;
        ClearFogScratch();
        return DefendersAt(system);
    }

    // ================================================================== the Empire's belief map of the Rebel HQ (ai/07 §7)

    private bool HqSearchOn => _view != null && _side == Side.Empire && KnownEnemyHq == null;

    /// <summary>HQ belief weight of a system (0 = cannot hold it as far as the Empire knows). Empire with fog only.</summary>
    private int Belief(string system) => Beliefs().TryGetValue(system, out var w) ? w : 0;
    private int BeliefMax { get { Beliefs(); return _beliefMax; } }

    /// <summary>
    /// The Empire's HQ belief map as it stands (fog only; empty otherwise), highest first, ties by name: for logs and tests. Rebuilt from the
    /// view on each call; reads nothing else of the other side.
    /// </summary>
    public IReadOnlyList<(string System, int Weight)> HqBelief()
    {
        _view ??= _p.UseFog ? _h.Fog.ViewFor(_side) : null;
        _belief = null;
        return Beliefs().OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private Dictionary<string, int> Beliefs()
    {
        if (_belief != null) return _belief;
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        _belief = d; _beliefMax = 0;
        if (_view == null || _side != Side.Empire) return d;
        string core = _h.Fog.Params.CoreGroup;
        // Alliance fleet sightings by system (the side's own log of the last ObservedWindowDays). rule: m5-fog-and-hq §N7 2
        var sightings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in _view.Sightings(Today - _view.ObservedWindowDays))
            if (s.Side == Side.Alliance) sightings[s.System] = sightings.GetValueOrDefault(s.System) + 1;
        var clues = new HashSet<string>(_view.HqSectorClues.Select(c => c.Sector), StringComparer.Ordinal);
        var allies = new Dictionary<string, int>(StringComparer.Ordinal); // Alliance-held systems per sector, as the picture shows them
        foreach (var sys in _h.Data.Galaxy.Systems)
            if (_view.Picture(sys.Name)?.Owner?.Value.Owner == Side.Alliance) allies[sys.Sector] = allies.GetValueOrDefault(sys.Sector) + 1;
        foreach (var sys in _h.Data.Galaxy.Systems)
        {
            string s = sys.Name;
            if (Held(s) || Destroyed(s)) continue;
            var pic = _view.Picture(s);
            int w;
            if (pic == null) w = string.Equals(sys.Group, core, StringComparison.OrdinalIgnoreCase) ? 0 : _p.BeliefUnexplored; // §N7 4: only rim starts and Alliance systems
            else
            {
                var owner = pic.Owner;
                if (owner == null) w = _p.BeliefUnexplored;
                else if (owner.Value.Owner == Side.Alliance) w = _p.BeliefAllianceHeld;
                else w = AgeOf(owner) > _p.BeliefOwnerTrustDays ? _p.BeliefStaleOther : 0;
                if (w > 0)
                {
                    if (pic.Fleets is { } e && AgeOf(e) <= _p.FogFleetMemoryDays)
                        w += _p.BeliefPerShipSeen * e.Value.Where(f => f.Side == Side.Alliance).Sum(f => f.ShipCount);
                    if (pic.Facilities is { } c) w += _p.BeliefPerFacilitySeen * c.Value.Values.Sum();
                    if (pic.Ground is { } g) w += _p.BeliefPerRegimentSeen * g.Value.Where(x => x.Side == Side.Alliance).Sum(x => x.Total);
                    w += Math.Min(_p.BeliefSightingCap, _p.BeliefPerSighting * sightings.GetValueOrDefault(s));
                    int sup = owner?.Value.AllianceSupport ?? 0;
                    if (sup > _p.BeliefSupportFloor) w += (sup - _p.BeliefSupportFloor) * (sup - _p.BeliefSupportFloor) / Math.Max(1, _p.BeliefSupportDivisor);
                    int ally = allies.GetValueOrDefault(sys.Sector) - (owner?.Value.Owner == Side.Alliance ? 1 : 0);
                    w += _p.BeliefPerSectorAlly * Math.Max(0, ally);
                    // a group C look that showed no HQ (Yavin's day-0 copy, a captured and lost system) clears it for a while. rule: §N5
                    if (pic.Facilities is { } cc && !pic.HqHere)
                        w = (int)((long)w * Math.Clamp(AgeOf(cc), 0, _p.BeliefClearedDays) / Math.Max(1, _p.BeliefClearedDays));
                }
            }
            if (w > 0 && clues.Contains(sys.Sector)) w *= _p.BeliefSectorClueFactor; // §N6 sector clue (only when that rule is on)
            if (w <= 0) continue;
            d[s] = w;
            if (w > _beliefMax) _beliefMax = w;
        }
        return d;
    }

    // ================================================================== scouting

    private bool IsScoutShape(Fleet f)
    {
        if (f.Side != _side || f.Ships.Count != 1 || f.Squadrons.Count != 0) return false;
        var o = _h.Economy.Find(f.Ships[0]);
        if (o == null || !_p.ScoutClasses(_side).Contains(o.Class)) return false;
        return !_h.Fleets.UnitsAboard(o.Id, ObjectCategory.Regiment).Any() && !_h.Fleets.UnitsAboard(o.Id, ObjectCategory.Fighter).Any();
    }

    /// <summary>Scouts kept: ScoutCount plus the ExplorerScouts on top.</summary>
    private int ScoutsWanted => _p.ScoutCount + _p.ExplorerScouts;

    /// <summary>The side's scouts: scout-shaped fleets, lowest id first, up to ScoutsWanted and the ScoutUpkeepBudget.</summary>
    private List<Fleet> Scouts()
    {
        if (_scouts != null) return _scouts;
        var list = new List<Fleet>(); int upkeep = 0;
        foreach (var f in S.Fleets.Values.Where(IsScoutShape).OrderBy(f => f.Id))
        {
            if (list.Count >= ScoutsWanted) break;
            int m = Maint(_h.Economy.Find(f.Ships[0])!.Class);
            if (upkeep + m > _p.ScoutUpkeepBudget) continue;
            list.Add(f); upkeep += m;
        }
        return _scouts = list;
    }

    private int ScoutUpkeep() => Scouts().Sum(f => Maint(_h.Economy.Find(f.Ships[0])!.Class));

    /// <summary>One more scout of this class fits the count and the budget.</summary>
    private bool WantsScout(string cls) => Scouts().Count < ScoutsWanted && ScoutUpkeep() + Maint(cls) <= _p.ScoutUpkeepBudget;

    /// <summary>Scout-class ships with nothing aboard in other fleets of the side at held systems (they can be split off).</summary>
    private IEnumerable<(Fleet Fleet, int Ship)> SplittableScouts(IEnumerable<Fleet> fleets)
    {
        var classes = _p.ScoutClasses(_side);
        foreach (var f in fleets.OrderBy(f => f.Id))
        {
            if (f.Ships.Count < 2 || f.At == null || !Held(f.At) || IsScoutShape(f)) continue;
            foreach (var id in f.Ships)
            {
                var o = _h.Economy.Find(id);
                if (o == null || !classes.Contains(o.Class)) continue;
                if (_h.Fleets.UnitsAboard(id, ObjectCategory.Regiment).Any() || _h.Fleets.UnitsAboard(id, ObjectCategory.Fighter).Any()) continue;
                yield return (f, id);
                break;
            }
        }
    }

    /// <summary>Production, fog only: order one scout at a free shipyard when the side lacks one and none can be split off a pile.</summary>
    private void PlanScoutBuild()
    {
        if (Scouts().Count >= ScoutsWanted) return;
        var classes = _p.ScoutClasses(_side);
        // scouts already queued (units exist from acceptance: m2-construction §Build order)
        foreach (var pr in S.Construction.AllProjects())
            if (pr.Side == _side && pr.Family == YardFamily.Shipyard && pr.AllUnits().Any(id => _h.Economy.Find(id) is { } o && classes.Contains(o.Class))) return;
        var idle = S.Fleets.Values.Where(f => f.Side == _side && f.Transit == null && f.At != null && !Pending(f));
        if (SplittableScouts(idle).Any()) return;
        string? cls = classes.FirstOrDefault(c => Available(c) && WantsScout(c) && Budget >= Maint(c));
        if (cls == null) return;
        foreach (var y in YardSystems(YardFamily.Shipyard))
        {
            if (!CanOrderAt(y, YardFamily.Shipyard)) continue;
            long seq = _h.Construction.SubmitBuild(_side, y, YardFamily.Shipyard, cls, 1, null);
            Note(seq, "Build", $"1 x {cls} at {y} (scout)");
            _busy.Add((y, YardFamily.Shipyard));
            _plannedLoad += Maint(cls);
            return;
        }
    }

    /// <summary>
    /// Military, fog only: split a scout off a pile when one is missing, then send every idle scout to the system most worth a look per day of
    /// travel. Returns the ids of the scouts (the caller leaves them out of the piles).
    /// </summary>
    private HashSet<int> PlanScouts(List<Fleet> idle)
    {
        var scouts = Scouts();
        var ids = new HashSet<int>(scouts.Select(f => f.Id));
        int extra = 0;
        foreach (var (f, ship) in SplittableScouts(idle.Where(f => !ids.Contains(f.Id))))
        {
            var cls = _h.Economy.Find(ship)!.Class;
            if (scouts.Count + extra >= ScoutsWanted || ScoutUpkeep() + (extra + 1) * Maint(cls) > _p.ScoutUpkeepBudget) break;
            long seq = _h.Fleets.SubmitSplitFleet(_side, f.Id, new[] { ship });
            Note(seq, "Split", $"ship {ship} off fleet {f.Id} at {f.At} (scout)");
            extra++;
        }
        // targets already covered: where scouts are or are flying to
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in scouts)
        {
            if (f.Transit != null) claimed.Add(f.Transit.Path[^1]);
            else if (f.At != null) claimed.Add(f.At);
        }
        int explorers = _p.ExplorerScouts;
        foreach (var f in scouts.OrderBy(f => f.Id))
        {
            bool explorer = explorers-- > 0;
            if (f.Transit != null || f.At == null || Pending(f)) continue;
            var t = (explorer ? ExploreTarget(f, claimed) : null) ?? ScoutTarget(f, claimed);
            if (t != null && Move(f, t)) { claimed.Add(t); }
        }
        return ids;
    }

    /// <summary>ExplorerScouts: the nearest never-explored system (no picture) by travel, not claimed, over a clear trip; null when none.</summary>
    private string? ExploreTarget(Fleet f, HashSet<string> claimed)
    {
        int speed = _h.Fleets.SpeedOf(f);
        if (speed <= 0) return null;
        var cands = new List<(string Sys, int Ticks)>();
        foreach (var sys in _h.Data.Galaxy.Systems)
        {
            string s = sys.Name;
            if (s == f.At || claimed.Contains(s) || Destroyed(s) || _view!.Picture(s) != null) continue;
            int ticks = TicksCached(f.At!, s, speed);
            if (ticks != int.MaxValue) cands.Add((s, ticks));
        }
        int checkedTrips = 0;
        foreach (var (s, _) in cands.OrderBy(c => c.Ticks).ThenBy(c => c.Sys, StringComparer.Ordinal))
        {
            if (checkedTrips++ >= _p.ScoutPathChecks) break;
            if (!_p.AvoidBlockedPaths || ScoutPathClear(f.At!, s, speed)) return s;
        }
        return null;
    }

    private string? ScoutTarget(Fleet f, HashSet<string> claimed)
    {
        int speed = _h.Fleets.SpeedOf(f);
        if (speed <= 0) return null;
        var hops = Hops(); var g = _h.Data.Galaxy;
        var cands = new List<(string Sys, long Score)>();
        foreach (var sys in g.Systems)
        {
            string s = sys.Name;
            if (s == f.At || Held(s) || claimed.Contains(s) || Destroyed(s)) continue;
            int value = ScoutValue(s, hops[sys.Index]);
            if (value <= 0) continue;
            int ticks = TicksCached(f.At!, s, speed);
            if (ticks == int.MaxValue) continue;
            long score = value * 1000L / (ticks + 10);
            cands.Add((s, score));
        }
        // best first (ties: the first system in galaxy order, as before); a trip that would stop at a system where armed enemy ships were seen
        // lately, or where an armed fleet of the side had to retreat lately, is skipped (rule: m1-forced-stop-and-blockade: an armed enemy fleet stops
        // a passing one; the unarmed scout then retreats, and the same trip again only repeats that)
        int checkedTrips = 0;
        foreach (var (s, _) in cands.Select((c, k) => (c, k)).OrderByDescending(t => t.c.Score).ThenBy(t => t.k).Select(t => t.c))
        {
            if (checkedTrips++ >= _p.ScoutPathChecks) break;
            if (!_p.AvoidBlockedPaths || ScoutPathClear(f.At!, s, speed)) return s;
        }
        return null;
    }

    /// <summary>No stop of the trip (the target included) has armed enemy ships seen within ScoutAvoidFleetDays or an armed own fleet retreated lately (BouncedOff).</summary>
    private bool ScoutPathClear(string from, string to, int speed)
    {
        var path = _h.Fleets.FindPath(from, to, speed);
        if (!path.Ok) return false;
        var g = _h.Data.Galaxy;
        for (int k = 1; k < path.Systems.Count; k++)
        {
            string n = g.Systems[path.Systems[k]].Name;
            if (BouncedOff(n)) return false;
            var pic = _view!.Picture(n);
            if (pic?.Fleets is { } e && AgeOf(e) <= _p.ScoutAvoidFleetDays && e.Value.Any(fp => fp.Side != _side && Armed(fp))) return false;
        }
        return true;
    }

    /// <summary>
    /// How much a fresh look at a system is worth: never explored = ScoutUnexploredValue; seen = its age as a share of StaleDays (0 while
    /// Held or Observed), doubled for enemy-held; the Empire adds HQ belief. Systems with armed enemy ships seen lately are avoided, and only
    /// systems within ScoutHops of the side's holdings count (the Empire also looks at HQ candidates farther away).
    /// </summary>
    private int ScoutValue(string s, int hop)
    {
        int belief = HqSearchOn ? Belief(s) : 0;
        if (hop > _p.ScoutHops && belief <= 0) return 0;
        var pic = _view!.Picture(s);
        int stale;
        if (pic == null) stale = 100;
        else
        {
            if (pic.Fleets is { } e && AgeOf(e) <= _p.ScoutAvoidFleetDays && e.Value.Any(fp => fp.Side != _side && Armed(fp))) return 0;
            if (_view.Level(s) is KnowledgeLevel.Held or KnowledgeLevel.Observed) return 0;
            int look = pic.LastLookDay ?? pic.Owner?.Stamp ?? 0;
            // blind hunt (QA-19): no StaleDays ceiling, so the oldest look goes first (nearest first among equals)
            int cap = BlindOn() ? int.MaxValue / 1000 : _p.StaleDays;
            stale = Math.Clamp(Today - look, 0, cap) * 100 / Math.Max(1, _p.StaleDays);
        }
        int v = pic == null ? _p.ScoutUnexploredValue : stale * (IsEnemyHeld(s) ? _p.ScoutEnemyFactor : 1);
        if (belief > 0 && BeliefMax > 0) v += (int)((long)_p.ScoutBeliefValue * belief / BeliefMax * stale / 100);
        return v;
    }

    // ================================================================== the Alliance hides its HQ (ai/07 §7)

    /// <summary>
    /// Risk per system from what the Alliance sees: every Imperial fleet sighting in its log of the last ObservedWindowDays (and every Imperial
    /// fleet it sees standing on its systems) adds HqRiskHere at that system, HqRiskNear one lane away and HqRiskFar two lanes away; every
    /// neighbour the picture shows Imperial adds HqRiskEnemyNeighbour.
    /// </summary>
    private Dictionary<string, int> Risk()
    {
        if (_risk != null) return _risk;
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        var g = _h.Data.Galaxy;
        void Spread(string at)
        {
            if (!g.TryFindSystem(at, out var i0)) return;
            var depth = new Dictionary<int, int> { [i0] = 0 }; var q = new Queue<int>(); q.Enqueue(i0);
            while (q.Count > 0)
            {
                int c = q.Dequeue(); int dc = depth[c];
                string n = g.Systems[c].Name;
                d[n] = d.GetValueOrDefault(n) + (dc == 0 ? _p.HqRiskHere : dc == 1 ? _p.HqRiskNear : _p.HqRiskFar);
                if (dc >= 2) continue;
                foreach (var nb in g.NearOf(c)) if (!depth.ContainsKey(nb)) { depth[nb] = dc + 1; q.Enqueue(nb); }
            }
        }
        foreach (var s in _view!.Sightings(Today - _view.ObservedWindowDays)) if (s.Side == Enemy) Spread(s.System);
        foreach (var (sys, fp) in _view.VisibleFleets()) if (fp.Side == Enemy) Spread(sys);
        foreach (var sys in g.Systems)
        {
            int n = g.NearOf(sys.Index).Count(nb => IsEnemyHeld(g.Systems[nb].Name));
            if (n > 0) d[sys.Name] = d.GetValueOrDefault(sys.Name) + n * _p.HqRiskEnemyNeighbour;
        }
        return _risk = d;
    }

    /// <summary>Lanes from each system to the nearest system the picture shows Imperial (int.MaxValue when none is known).</summary>
    private int[] HopsFromEnemy()
    {
        var g = _h.Data.Galaxy; var dist = new int[g.Systems.Count]; Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        foreach (var s in g.Systems) if (IsEnemyHeld(s.Name)) { dist[s.Index] = 0; q.Enqueue(s.Index); }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (var n in g.NearOf(c)) if (dist[n] == int.MaxValue) { dist[n] = dist[c] + 1; q.Enqueue(n); }
        }
        return dist;
    }

    /// <summary>
    /// Daily, Alliance with fog: move the HQ (MoveHq, m5-fog-and-hq §F6) when the risk at its system reaches HqMoveRisk and the last move is at
    /// least HqMoveCooldownDays old; the destination is a held system with HqMinLanes lanes or more, no enemy fleet seen on it, risk below half
    /// of HqMoveRisk, as far from the Empire as possible, then the least risk, then the shortest trip.
    /// </summary>
    private void PlanHqHide()
    {
        var hq = _view!.KnownHq; // the Alliance's own HQ (its own object, §N5)
        if (hq == null) return;
        string at = hq.System;
        var risk = Risk();
        if (risk.GetValueOrDefault(at) < _p.HqMoveRisk) return;
        if (NotesSince(AiNoteKind.HqMoved, Today - _p.HqMoveCooldownDays).Any()) return; // the HQ moved lately (own feed, kept in the AI memory)
        if (_h.Fog.IsPinned(at)) return; // its own system: the side sees the blockade there (§F6 departure refusal)
        var g = _h.Data.Galaxy; var room = HopsFromEnemy();
        int speed = _h.Fog.HqRules.Speed;
        string? best = null; long bestScore = long.MinValue;
        foreach (var s in HeldSystems())
        {
            if (s == at || !g.TryFindSystem(s, out var i) || g.NearOf(i).Count < _p.HqMinLanes) continue;
            int r = risk.GetValueOrDefault(s);
            if (r * 2 >= _p.HqMoveRisk || Threats().ContainsKey(s) || Suspended(s)) continue;
            int ticks = TicksCached(at, s, speed);
            if (ticks == int.MaxValue) continue;
            long score = Math.Min(room[i], 8) * 1000L - r * 10L - ticks;
            if (score > bestScore) { bestScore = score; best = s; }
        }
        if (best == null) return;
        long seq = _h.Fog.SubmitMoveHq(_side, best);
        Note(seq, "MoveHq", $"HQ {at} -> {best} (risk {risk.GetValueOrDefault(at)})");
    }

    // ================================================================== missions behind the fog (m6 AI: ai/05 §7b, missions/08)

    /// <summary>
    /// Fog: the units a mission at the system would meet (m5-missions §3 Defenders), from the side's picture: every regiment of another side in
    /// group D (with the staleness margin), every ship and squadron of the other side in group E up to FogFleetMemoryDays old. Ground never
    /// seen on a system the picture shows enemy-held, or on a system never explored: FogUnknownDefenders regiments of the enemy's garrison
    /// class. Officers are unknown (espionage 0). Cached for the day.
    /// </summary>
    private List<DefenderSeen> FogMissionDefenders(string system)
    {
        if (_missionDefenders.TryGetValue(system, out var cached)) return cached;
        var list = new List<DefenderSeen>();
        var pic = _view!.Picture(system);
        if (pic?.Ground is { } g)
        {
            int pct = StalePercent(AgeOf(g));
            foreach (var gp in g.Value)
            {
                if (gp.Side == _side) continue;
                foreach (var (cls, n) in gp.Units) // ordinal class order
                {
                    int det = _h.Troops.Data.Regiment(cls)?.Detection ?? 0, k = (n * pct + 99) / 100;
                    for (int i = 0; i < k; i++) list.Add(new DefenderSeen(det, 0));
                }
            }
        }
        else if (!Held(system) && (pic == null || IsEnemyHeld(system)))
        {
            int det = _h.Troops.Data.Regiment(_p.GarrisonRegiment(Enemy))?.Detection ?? 0;
            for (int i = 0; i < _p.FogUnknownDefenders; i++) list.Add(new DefenderSeen(det, 0));
        }
        if (pic?.Fleets is { } e && AgeOf(e) <= _p.FogFleetMemoryDays)
            foreach (var fp in e.Value)
            {
                if (fp.Side == _side) continue;
                foreach (var (cls, n) in fp.Ships.Concat(fp.Squadrons))
                {
                    int det = _h.Fleets.ShipClasses.TryGetValue(cls, out var c) ? c.Detection : 0;
                    for (int i = 0; i < n; i++) list.Add(new DefenderSeen(det, 0));
                }
            }
        return _missionDefenders[system] = list;
    }

    /// <summary>
    /// Fog: enemy characters the side knows to stand at a system, by character id, from two of its own sources within AbductionInfoDays: its
    /// event feed (arrived, detached, made General there, freed there; a later departure, capture, injury or death forgets them; kept only where
    /// the side sees the system today) and the group D characters of its pictures (free ones, stamp in the window; m5-fog-and-hq §F1 group D).
    /// The newest word on each character wins; on the same day the feed wins.
    /// </summary>
    private List<(int Character, string System)> FogEnemyCharacters()
    {
        var last = new Dictionary<int, (string? System, int Day)>(); // lookup only: the latest word on each character
        var notes = _mem.Notes; int since = Today - _p.AbductionInfoDays;
        for (int i = notes.Count - 1; i >= 0 && notes[i].Floor >= since; i--)
            switch (notes[i].Kind)
            {
                case AiNoteKind.EnemyCharacterAt: last.TryAdd(notes[i].Value, (notes[i].System, notes[i].Day)); break; // arrived, detached, made General or freed there
                case AiNoteKind.EnemyCharacterGone: last.TryAdd(notes[i].Value, (null, notes[i].Day)); break;           // departed, captured, injured or killed
            }
        var result = new SortedDictionary<int, string>();
        foreach (var (id, w) in last) if (w.System != null && _view!.Sees(w.System)) result[id] = w.System; // keyed by id: order cannot matter
        // rule: m5-fog-and-hq §F1 group D (characters present): the side's own pictures, systems in ordinal order; the newest stamp per character
        var fromPicture = new SortedDictionary<int, (string System, int Stamp)>();
        foreach (var (sys, c, stamp) in _view!.CharactersSeen())
            if (c.Side == Enemy && c.Status == Rebellion.Core.Characters.CharacterStatus.Free && stamp >= since
                && (!fromPicture.TryGetValue(c.Id, out var had) || stamp > had.Stamp))
                fromPicture[c.Id] = (sys, stamp);
        foreach (var (id, p) in fromPicture)
        {
            if (last.TryGetValue(id, out var w) && w.Day >= p.Stamp) continue; // the feed's word is as new or newer
            result[id] = p.System;
        }
        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ================================================================== the key hunt (KeyHunt)

    /// <summary>
    /// KeyHunt: fog on, the Alliance, ahead (Finishing, or its place term: Coruscant held or destroyed), and a key of its standard victory set
    /// not yet its own prisoner (own state). A dead key is not known here and still hunted: the looks then find nobody.
    /// </summary>
    private bool KeyHuntOn()
    {
        if (!_p.KeyHunt || _view == null || _side != Side.Alliance || HuntedKeys().Count == 0) return false;
        return KeyAhead();
    }

    /// <summary>The side is ahead for its victory keys: Finishing, or for the Alliance its place term (Coruscant held or destroyed).</summary>
    private bool KeyAhead()
    {
        string cor = Rebellion.Core.Victory.VictoryModule.CoruscantName;
        return Finishing() || (_side == Side.Alliance && (Held(cor) || Destroyed(cor)));
    }

    /// <summary>Is the character one of the side's standard victory keys (the characters it must capture to win)?</summary>
    private bool IsVictoryKey(int id) => _h.Victory.Keys.Any(k => k.Standard && k.Side == _side && k.Character == id);

    /// <summary>The side's standard victory keys that are not its own prisoners, by id.</summary>
    private List<int> HuntedKeys() => _h.Victory.Keys.Where(k => k.Standard && k.Side == _side).Select(k => k.Character).Distinct().OrderBy(id => id)
        .Where(id => _h.Characters.Get(id) is not { Status: CharacterStatus.Captured } c || c.Captor != _side).ToList();

    /// <summary>
    /// Key hunt: the systems to look at, best first. First where the side's pictures last showed a hunted key, free, within KeyInfoDays but
    /// older than AbductionInfoDays (a fresher one is already an Abduction target); then every system the picture shows enemy-held whose ground
    /// was not seen within KeyLookDays and where no look was foiled within SpyFoilMemoryDays, by hops from the side's systems, then ordinal name.
    /// Reads the side's pictures only (m5-fog-and-hq §N8; group D carries the characters, §F1).
    /// </summary>
    private List<(string System, bool LastSeen)> KeyHuntTargets()
    {
        var keys = HuntedKeys();
        var list = new List<(string, bool)>(); var have = new HashSet<string>(StringComparer.Ordinal); // membership only
        var last = new SortedDictionary<int, (string System, int Stamp)>();
        foreach (var (sys, c, stamp) in _view!.CharactersSeen()) // systems in ordinal order; the newest stamp per key
            if (keys.Contains(c.Id) && c.Status == CharacterStatus.Free && (!last.TryGetValue(c.Id, out var had) || stamp > had.Stamp))
                last[c.Id] = (sys, stamp);
        foreach (var (_, (sys, stamp)) in last)
            if (stamp >= Today - _p.KeyInfoDays && stamp < Today - _p.AbductionInfoDays && !Held(sys) && !Destroyed(sys) && have.Add(sys)) list.Add((sys, true));
        var hops = Hops(); var g = _h.Data.Galaxy;
        var sweep = new List<(int Hops, string System)>();
        foreach (var s in EnemySystems())
        {
            if (have.Contains(s) || Destroyed(s) || !g.TryFindSystem(s, out int i)) continue;
            var ground = _view.Picture(s)?.Ground;
            if (ground != null && AgeOf(ground) <= _p.KeyLookDays) continue;
            if (FoiledWithin(s, _p.SpyFoilMemoryDays)) continue;
            sweep.Add((hops[i], s));
        }
        foreach (var (_, s) in sweep.OrderBy(x => x.Hops).ThenBy(x => x.System, StringComparer.Ordinal)) list.Add((s, false));
        return list;
    }

    // ================================================================== the blind hunt (BlindHunt, QA-19)

    /// <summary>BlindHunt: fog on and the side's picture shows no system held by the other side (cached per day).</summary>
    private bool BlindOn() => _blind ??= _p.BlindHunt && _view != null && !EnemySystems().Any();
    private bool? _blind;

    /// <summary>Days since the side last looked at the system (the picture's last look, else its owner stamp); never looked = Today + 1.</summary>
    private int LookAge(string s)
    {
        var pic = _view!.Picture(s);
        int? look = pic?.LastLookDay ?? pic?.Owner?.Stamp;
        return look is int d ? Today - d : Today + 1;
    }

    /// <summary>
    /// Blind hunt: pile target value of a system the side last looked at more than BlindStaleDays ago, within ScoutHops of its systems: up to
    /// BlindValue by the age of the look (all of it from BlindAgeDays); 0 otherwise. Reads the side's picture only.
    /// </summary>
    private int BlindTargetValue(string s, int hop)
    {
        if (hop > _p.ScoutHops) return 0;
        int age = LookAge(s);
        if (age <= _p.BlindStaleDays) return 0;
        return (int)((long)_p.BlindValue * Math.Min(age, _p.BlindAgeDays) / Math.Max(1, _p.BlindAgeDays));
    }

    /// <summary>
    /// Blind hunt: where to send spies, best first: systems not held or destroyed, populated as the picture shows them, last looked at more than
    /// BlindStaleDays ago, and no look foiled there within SpyFoilMemoryDays; oldest look first, then by hops from the side's systems, then
    /// ordinal name.
    /// </summary>
    private List<string> BlindLookTargets()
    {
        var hops = Hops(); var list = new List<(int Age, int Hops, string System)>();
        foreach (var sys in _h.Data.Galaxy.Systems)
        {
            string s = sys.Name;
            if (Held(s) || Destroyed(s) || !SlotsSeen(s).Populated) continue;
            int age = LookAge(s);
            if (age <= _p.BlindStaleDays || FoiledWithin(s, _p.SpyFoilMemoryDays)) continue;
            list.Add((age, hops[sys.Index], s));
        }
        return list.OrderByDescending(x => x.Age).ThenBy(x => x.Hops).ThenBy(x => x.System, StringComparer.Ordinal).Select(x => x.System).ToList();
    }

    // ================================================================== task forces (TaskForces, QA-2)

    /// <summary>Task forces: the percent of a fleet sighting's strength that still counts at that age (100 within memoryDays, then fadePercent less per 100 days, never below 0).</summary>
    internal static int FadedPercent(int ageDays, int memoryDays, int fadePercent)
    {
        int beyond = ageDays - memoryDays;
        return beyond <= 0 ? 100 : 100 - (int)Math.Min(100L, (long)Math.Max(0, fadePercent) * beyond / 100);
    }

    /// <summary>
    /// The strength of the strongest enemy fleet the side knows of: without fog every enemy fleet; with fog the fleets of its picture (as
    /// <see cref="Str(Force)"/>), each at its whole strength when seen within FogFleetMemoryDays and older ones fading by TaskForceFadePercent
    /// per 100 days beyond that (QA-20: a fleet out of sight is not a fleet gone; the picture keeps a fleet's last sighting until that system
    /// is looked at again). 0 when none.
    /// </summary>
    private double StrongestEnemyFleet()
    {
        if (_view == null) return S.Fleets.Values.Where(f => f.Side == Enemy && f.Ships.Count > 0).Select(Str).DefaultIfEmpty(0).Max();
        double best = 0;
        foreach (var sys in _h.Data.Galaxy.Systems)
        {
            var e = _view.Picture(sys.Name)?.Fleets;
            if (e == null) continue;
            int pct = FadedPercent(AgeOf(e), _p.FogFleetMemoryDays, _p.TaskForceFadePercent);
            if (pct <= 0) continue;
            foreach (var fp in e.Value) if (fp.Side == Enemy) best = Math.Max(best, Str(ForceOf(sys.Name, fp)) * pct / 100);
        }
        return best;
    }
}
