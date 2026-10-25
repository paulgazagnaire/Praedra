using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Defenses;
using Praedra.Core.Fleets;

namespace Praedra.Core.AI;

/// <summary>The simple AI's military planning: threats, movement, task forces, piles and targets (moved unchanged from SimpleAi.cs).</summary>
public sealed partial class SimpleAi
{
    // ================================================================== military

    private Dictionary<string, List<ArmedForce>>? _threats;
    private HashSet<string>? _lost;

    /// <summary>Held systems with enemy fleets standing on them or flying toward them (a stop on their path), by system. Fleets in id order.</summary>
    private Dictionary<string, List<ArmedForce>> Threats()
    {
        if (_threats != null) return _threats;
        var d = new Dictionary<string, List<ArmedForce>>(StringComparer.Ordinal);
        void Add(string sys, ArmedForce f) { if (!d.TryGetValue(sys, out var l)) d[sys] = l = new List<ArmedForce>(); l.Add(f); }
        if (_view != null)
        {
            // Fog: only the enemy fleets the side sees standing on its own systems (a fleet on a lane is invisible). rule: m5-fog-and-hq §F3
            foreach (var (sys, fp) in _view.VisibleFleets())
                if (fp.Side == Enemy && fp.ShipCount > 0 && Held(sys)) Add(sys, ForceOf(sys, fp));
            return _threats = d;
        }
        foreach (var f in S.Fleets.Values.Where(f => f.Side == Enemy && f.Ships.Count > 0).OrderBy(f => f.Id))
        {
            if (f.At != null) { if (Held(f.At)) Add(f.At, ForceOf(f)); }
            else if (f.Transit != null)
                for (int i = f.Transit.HopIndex + 1; i < f.Transit.Path.Count; i++)
                    if (Held(f.Transit.Path[i])) Add(f.Transit.Path[i], ForceOf(f));
        }
        return _threats = d;
    }

    /// <summary>An enemy force's strength against REF (capital ships only), as <see cref="Str(Fleet)"/>.</summary>
    private double Str(ArmedForce x) => x.FleetId is int id && S.Fleets.TryGetValue(id, out var f) ? Str(f) : _h.Fleets.Strength.Of(x.Units, RefUnits());

    private Dictionary<string, int>? _lostBattles;
    /// <summary>
    /// Systems where this side was annihilated in a battle lately, with the day. The measure of strength is only a guess, so a fight that
    /// went wrong makes the AI ask for much better odds there for a while (never repeat a suicide). With RetreatCountsAsLoss a battle its fleets
    /// left by the morale retreat (m4-battle-classic) counts too: the measure called the fight good and the battle disagreed.
    /// </summary>
    private Dictionary<string, int> LostBattles()
    {
        if (_lostBattles != null) return _lostBattles;
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in NotesSince(AiNoteKind.Battle, Today - _p.LossMemoryDays))
        {
            if (d.ContainsKey(b.System!)) continue;
            if (b.Own == BattleOutcome.Annihilated || (_p.RetreatCountsAsLoss && b.Own == BattleOutcome.Retreated && OwnArmedIn(b))) d[b.System!] = b.Day;
        }
        return _lostBattles = d;
    }

    /// <summary>
    /// Did this side bring an armed capital ship to the battle that ended at ev[end]? Read from the battle's BattleStarted (same day, same system)
    /// and the side's OWN units only. An unarmed ship (a scout) always retreats from armed enemies (m4-battle-classic morale), which says nothing
    /// about the odds of a strike there. True when the start is not in the feed.
    /// </summary>
    private bool OwnArmedIn(AiNote battle) => battle.Armed == null || battle.Armed.Any(IsArmedCapital); // noted: units armed then; a ship lost since no longer counts

    /// <summary>The odds (percent) asked before fighting at a system: the base, times <see cref="SimpleAiParams.LossOddsFactor"/> where the side was annihilated (or bounced, see LostBattles) lately.</summary>
    private double OddsAt(string system, int basePercent) => LostBattles().ContainsKey(system) ? basePercent * _p.LossOddsFactor : basePercent;

    private Dictionary<string, int>? _runners, _bounced;
    /// <summary>Regiments and squadrons already named in a load order this review (orders run at the next Step, so the picture still shows them on the ground).</summary>
    private readonly HashSet<int> _loading = new(); // lookup only

    /// <summary>
    /// Battles of the last RunnerMemoryDays where the other side retreated (rule: m4-retreat-damage-repair §1, the morale retreat of
    /// m4-battle-classic) and this side held the field, counted by system. Read from the side's own event feed (BattleEnded goes to both
    /// sides, m5-fog-and-hq §N8; read through the AI memory, which a save carries): a fleet that only retreats is remembered for RunnerMemoryDays.
    /// </summary>
    private Dictionary<string, int> Runners()
    {
        if (_runners != null) return _runners;
        ScanRetreats();
        return _runners!;
    }

    /// <summary>Battles of the last RunnerMemoryDays where this side retreated with an armed capital ship, counted by system (own event feed).</summary>
    private Dictionary<string, int> Bounced()
    {
        if (_bounced != null) return _bounced;
        ScanRetreats();
        return _bounced!;
    }

    private void ScanRetreats()
    {
        _runners = new Dictionary<string, int>(StringComparer.Ordinal); // lookup only
        _bounced = new Dictionary<string, int>(StringComparer.Ordinal); // lookup only
        foreach (var b in NotesSince(AiNoteKind.Battle, Today - _p.RunnerMemoryDays))
        {
            string sys = b.System!;
            if (b.Other == BattleOutcome.Retreated && b.Own is not (BattleOutcome.Retreated or BattleOutcome.Annihilated)) _runners[sys] = _runners.GetValueOrDefault(sys) + 1;
            if (b.Own == BattleOutcome.Retreated && OwnArmedIn(b)) _bounced[sys] = _bounced.GetValueOrDefault(sys) + 1;
        }
    }

    /// <summary>
    /// True when enemy fleets retreated from this side at the system or at a system one lane from it at least RunnerRetreats times lately: a
    /// retreating fleet jumps to a neighbour (m4-retreat-damage-repair §3), so hunting it there only starts the next jump. Its fleets there
    /// are not hunted for their own sake (the system itself stays a target for an assault).
    /// </summary>
    private bool EnemyRunsNear(string system)
    {
        var r = Runners();
        if (r.Count == 0 || _p.RunnerRetreats <= 0) return false;
        int n = r.GetValueOrDefault(system);
        var g = _h.Data.Galaxy;
        if (g.TryFindSystem(system, out var i))
            foreach (var nb in g.NearOf(i)) n += r.GetValueOrDefault(g.Systems[nb].Name);
        return n >= _p.RunnerRetreats;
    }

    /// <summary>Systems the side lost to the enemy lately (retake targets).</summary>
    private HashSet<string> LostSystems()
    {
        if (_lost != null) return _lost;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in NotesSince(AiNoteKind.SystemLost, Today - _p.RetakeDays)) set.Add(c.System!);
        return _lost = set;
    }

    /// <summary>
    /// Own power as a percent of the other force's: CombatStrength(own, enemy) over CombatStrength(enemy, own), shields full (m4-battle-new §12).
    /// A huge number when the enemy cannot hurt us, 0 when we cannot hurt it, 100 when nobody can.
    /// </summary>
    private double Duel(IEnumerable<Fleet> own, IEnumerable<ArmedForce> enemy)
    {
        var o = own.SelectMany(ShipUnits).ToList();
        var e = enemy.SelectMany(x => x.Units).ToList();
        if (e.Count == 0) return 1e9;
        if (o.Count == 0) return 0;
        double a = _h.Fleets.Strength.Of(o, e), b = _h.Fleets.Strength.Of(e, o);
        if (b <= 0) return a > 0 ? 1e9 : 100;
        return a * 100 / b;
    }

    private string? CriticalSystem() => _side == Side.Hand ? Praedra.Core.Control.ControlModule.CapitalSystem : _h.HqSystem; // the system whose loss ends the game for the side
    /// <summary>
    /// The home guard stands at the system whose loss ends the game for the side, when the side holds it. New warships are only sent there
    /// when it has a shipyard (so the guard can be topped up); otherwise the guard is whatever fleet already stands there.
    /// </summary>
    private string? GuardSystem() { var c = CriticalSystem(); return c != null && Held(c) ? c : null; }
    // The Hand only strikes an HQ it has been told about (KnownEnemyHq); no peeking at the true HQ (economy-ai D4: the AI plays fair with fog of war).
    private string? WinTarget() => _side == Side.Hand ? KnownEnemyHq : Praedra.Core.Control.ControlModule.CapitalSystem;

    private bool Pending(Fleet f) => _h.Bombardment.HasPending(f.Id) || _h.Assault.HasPending(f.Id);

    private int _offensives = -1;
    /// <summary>
    /// Offensives under way (MaxOffensives): armed own fleets flying to, or standing on, a system the side does not hold; counted once per day,
    /// plus the strikes launched today. Own state only.
    /// </summary>
    private int Offensives()
    {
        if (_offensives >= 0) return _offensives;
        int n = 0;
        foreach (var f in S.Fleets.Values.Where(f => f.Side == _side && f.Ships.Count > 0).OrderBy(f => f.Id))
        {
            string? dest = f.At ?? (f.Transit is { } t ? t.Path[^1] : null);
            if (dest != null && !Held(dest) && Str(f) > 0) n++;
        }
        return _offensives = n;
    }

    /// <summary>Marks a Move note whose order is the first leg of a way around; the trip's goal follows it.</summary>
    public const string OnTheWayTo = "on the way to ";

    private bool Move(Fleet f, string dest)
    {
        if (f.At == null || dest == f.At || f.Transit != null) return false;
        int speed = _h.Fleets.SpeedOf(f);
        var path = _h.Fleets.FindPath(f.At, dest, speed);
        if (speed <= 0 || !path.Ok) return false;
        if (_p.AvoidBlockedPaths && PathBlockedAt(f, path) is { } blocked)
        {
            // the way would end in a retreat: go around (no stop at a blocked system), one leg at a time; on arrival the next review plans
            // again. Without a way around the fleet stays. Proposal (lead 2026-10-16; the AI uses the router, movement-redesign-draft §D).
            var g = _h.Data.Galaxy; var avoid = new List<int>();
            for (int tries = 0; tries < 16 && blocked != null; tries++)
            {
                if (blocked == dest || !g.TryFindSystem(blocked, out int bi)) return false;
                avoid.Add(bi);
                path = _h.Fleets.FindPathAvoiding(g.Get(f.At).Index, g.Get(dest).Index, speed, avoid);
                if (!path.Ok) return false;
                blocked = PathBlockedAt(f, path);
            }
            if (blocked != null) return false;
            if (_p.DetourWithWaypoints)
            {
                // the whole way around in one order: the stops of the avoiding path become waypoints, used only when the order flies exactly
                // that path (a stretch between two stops could otherwise cut back through an avoided system). rule: m1-travel §Path rule (3).
                var stops = path.Systems.Skip(1).Take(path.Systems.Count - 2).Select(i => g.Systems[i].Name).ToList();
                var via = _h.Fleets.PlanPath(f, dest, stops);
                if (via.Ok && via.Systems.SequenceEqual(path.Systems))
                {
                    long ws = _h.Fleets.SubmitMove(_side, f.Id, dest, stops);
                    Note(ws, "Move", $"fleet {f.Id} {f.At} -> {dest} via {string.Join(", ", stops)}");
                    return true;
                }
            }
            string first = g.Systems[path.Systems[1]].Name;
            if (first != dest && !Held(first) && _p.DetourToHeldStop && DetourLeg(f, path, speed) is { } leg2) first = leg2;
            if (first != dest)
            {
                var leg = _h.Fleets.FindPath(f.At, first, speed);
                if (!leg.Ok || PathBlockedAt(f, leg) != null) return false;
                // the order is only the first leg of the way around: the note names the trip's goal, so a reader of the orders (a trace, a
                // test counting offensives) tells a stop on the way (a relief of a held system may stop on a neutral one) from a target
                long legSeq = _h.Fleets.SubmitMove(_side, f.Id, first);
                Note(legSeq, "Move", $"fleet {f.Id} {f.At} -> {first} {OnTheWayTo}{dest}");
                return true;
            }
        }
        long seq = _h.Fleets.SubmitMove(_side, f.Id, dest);
        Note(seq, "Move", $"fleet {f.Id} {f.At} -> {dest}");
        return true;
    }

    /// <summary>
    /// DetourToHeldStop: the leg of a detour that keeps the fleet's purpose. The AI keeps no memory between days, so a fleet that stops at a
    /// system it does not hold plans there as at a target (DecideAtTarget): with no business there it leaves for the rally system and the
    /// trip it was on is lost. Measured on the v5 map (Collective vs passive Hand, seed 1): a strike pile fetching troops went around a blocked
    /// stop to a Hand system, turned back to the rally, and did that again for 200 days with its troop holds empty. So the leg ends at
    /// the first stop of the way around that the side holds (there DecideOwn plans the rest of the trip again), when the direct trip there is
    /// clear. Null (no such stop) = keep the first stop, as the AI of before.
    /// </summary>
    private string? DetourLeg(Fleet f, Praedra.Core.Travel.PathResult around, int speed)
    {
        var g = _h.Data.Galaxy;
        string dest = g.Systems[around.Systems[^1]].Name;
        for (int k = 2; k < around.Systems.Count - 1; k++)
        {
            string n = g.Systems[around.Systems[k]].Name;
            if (!Held(n)) continue;
            var leg = _h.Fleets.FindPath(f.At!, n, speed);
            if (leg.Ok && PathBlockedAt(f, leg) == null) return n;
        }
        return null;
    }

    /// <summary>
    /// The first stop of the trip where it would end in a retreat, or null: a stop (the destination included) where this side's fleets retreated
    /// within RunnerMemoryDays (own event feed), or a stop on the way where enemy fleets the side sees now outmatch the fleets of the side at the
    /// start (RetreatOddsPercent). rule: m1-forced-stop-and-blockade (an enemy fleet that triggers a forced stop halts a passing one; the order is cancelled), then the
    /// morale retreat of m4-battle-classic sends the fleet back: the same order again only repeats the bounce. The destination's fleets are
    /// weighed by the caller (PickTarget, PickRelief).
    /// </summary>
    private string? PathBlockedAt(Fleet f, Praedra.Core.Travel.PathResult path)
    {
        var g = _h.Data.Galaxy;
        List<Fleet>? own = null;
        for (int k = 1; k < path.Systems.Count; k++)
        {
            string n = g.Systems[path.Systems[k]].Name;
            if (BouncedOff(n)) return n;
            if (k == path.Systems.Count - 1) break;
            // only the enemy fleets that would pull this fleet out of hyperspace there (the game's forced-stop trigger; DEFAULT GravityWellShip
            // since 2026-10-16: an ordinary fleet does not stop a passing one, so the AI no longer refuses trips past it)
            var enemies = EnemyFleetsAt(n);
            if (_view != null && _p.FogAvoidEnemyStops && IsEnemyHeld(n) && _view.Level(n) is not (Praedra.Core.Fog.KnowledgeLevel.Held or Praedra.Core.Fog.KnowledgeLevel.Observed))
                return n; // an enemy system whose fleets the side cannot see now: it may hide a gravity well
            if (enemies.Count == 0) continue;
            // pulled out by a gravity well, the fleet then fights every enemy fleet there
            if (_h.Fleets.Trigger == ForcedStopTrigger.GravityWellShip && !enemies.Any(e => e.Gravity)) continue;
            own ??= _h.Fleets.FleetsAt(f.At!).Where(x => x.Side == _side).ToList();
            if (Duel(own, enemies) < _p.RetreatOddsPercent) return n;
        }
        return null;
    }

    /// <summary>The side's fleets retreated from a battle at the system within RunnerMemoryDays: no trip goes there for now (AvoidBlockedPaths).</summary>
    private bool BouncedOff(string system) => _p.AvoidBlockedPaths && Bounced().ContainsKey(system);

    private sealed class Pile
    {
        public required Fleet Leader { get; init; }
        public required List<Fleet> Members { get; init; }
        public double Strength; public int Aboard, FreeSlots, Berths; public double HullPct = 100;
    }

    private void PlanMilitary()
    {
        var idle = S.Fleets.Values.Where(f => f.Side == _side && f.Transit == null && f.At != null && f.Ships.Count > 0 && !Pending(f)).ToList();
        if (_view != null) { var scouts = PlanScouts(idle); idle = idle.Where(f => !scouts.Contains(f.Id)).ToList(); } // fog: scouts go their own way
        var cap = GuardSystem();
        var dsFleets = PrepareHarvester(idle);
        foreach (var g in idle.GroupBy(f => f.At!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = g.OrderBy(f => f.Id).ToList();
            var dsHere = list.Where(f => dsFleets.Contains(f.Id)).ToList();
            if (dsHere.Count > 0) list = list.Where(f => !dsFleets.Contains(f.Id)).ToList();
            if (!Held(g.Key))
            {
                foreach (var d in dsHere) HarvesterAway(d);
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
                    if (acc < _p.HomeDefenceStrength && Str(f) > 0) { home.Add(f); acc += Str(f); } else strike.Add(f);
                }
                for (int i = 1; i < home.Count; i++) Merge(home[i], home[0]);
                if (home.Count == 1 && _p.SplitHomeSurplus) SplitHomeSurplus(home[0], acc);
            }
            if (dsHere.Count > 0) { strike = HarvesterHome(dsHere, strike); }
            else strike = HarvesterCall(g.Key, strike);
            if (strike.Count == 0) continue;
            var forces = TaskForces(strike);
            _tfClaims = forces.Count > 1 ? Claims() : null;
            foreach (var tf in forces)
            {
                var leader = tf[0];
                for (int i = 1; i < tf.Count; i++) Merge(tf[i], leader);
                DecideOwn(MakePile(leader, tf));
            }
            _tfClaims = null;
        }
    }

    /// <summary>Task forces: systems other piles of one group may not pick (lookup only); null when the group formed one pile.</summary>
    private HashSet<string>? _tfClaims;

    /// <summary>Task forces: the systems not held that own armed fleets are flying to (another task force already took them on).</summary>
    private HashSet<string> Claims()
    {
        var c = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in S.Fleets.Values.Where(f => f.Side == _side && f.Transit != null && f.Ships.Count > 0))
            if (!Held(f.Transit!.Path[^1]) && Str(f) > 0) c.Add(f.Transit.Path[^1]);
        return c;
    }

    /// <summary>
    /// TaskForces: the strike fleets idle on one held system as piles (fleet id order within each). One pile (all of them, as before) unless the
    /// switch is on and their strength holds at least two task forces (TaskForceStrength). Then, armed fleets strongest first (ties by id), each
    /// pile takes fleets until it reaches TaskForceStrength and the next starts, up to TaskForceMax; a pile left short joins the weakest full one;
    /// unarmed fleets join the pile with the fewest regiments aboard. A fleet that alone holds two task forces splits off ships worth one (its
    /// highest ship ids, keeping the rest at least one task force): the new fleet is a pile of its own from the next review.
    /// </summary>
    private List<List<Fleet>> TaskForces(List<Fleet> strike)
    {
        var one = new List<List<Fleet>> { strike };
        if (!_p.TaskForces || _p.TaskForceMax < 2) return one;
        double tfs = TaskForceStrength();
        if (tfs <= 0 || strike.Sum(Str) < 2 * tfs) return one;
        var big = strike.Where(f => Str(f) >= 2 * tfs && f.Ships.Count >= 2).OrderByDescending(Str).ThenBy(f => f.Id).FirstOrDefault();
        if (big != null) SplitTaskForce(big, tfs);
        var piles = new List<List<Fleet>>(); var cur = new List<Fleet>(); double acc = 0;
        foreach (var f in strike.Where(f => Str(f) > 0).OrderByDescending(Str).ThenBy(f => f.Id))
        {
            cur.Add(f); acc += Str(f);
            if (acc >= tfs && piles.Count < _p.TaskForceMax - 1) { piles.Add(cur); cur = new List<Fleet>(); acc = 0; }
        }
        if (cur.Count > 0)
        {
            if (piles.Count == 0 || acc >= tfs) piles.Add(cur);
            else piles.OrderBy(pl => pl.Sum(Str)).First().AddRange(cur);
        }
        if (piles.Count < 2) return one;
        foreach (var f in strike.Where(f => Str(f) <= 0).OrderBy(f => f.Id))
            piles.Select((pl, k) => (pl, k)).OrderBy(t => t.pl.Sum(x => _h.Troops.RegimentsAboard(x).Count)).ThenBy(t => t.k).First().pl.Add(f);
        return piles.Select(pl => pl.OrderBy(f => f.Id).ToList()).ToList();
    }

    /// <summary>Task forces: split ships worth at least one task force off a fleet that holds two (highest ship ids first).</summary>
    private void SplitTaskForce(Fleet f, double tfs)
    {
        var cls = f.Ships.Select(id => (Id: id, S: ClassStrength(_h.Economy.Find(id)!.Class))).ToList();
        double total = cls.Sum(c => c.S), strength = Str(f);
        if (total <= 0) return;
        var off = new List<int>(); double got = 0;
        foreach (var (id, cs) in cls.Where(c => c.S > 0).OrderByDescending(c => c.Id))
        {
            if (got >= tfs) break;
            double share = strength * cs / total;
            if (strength - got - share < tfs) continue;
            off.Add(id); got += share;
        }
        if (got < tfs || off.Count == 0 || off.Count == f.Ships.Count) return;
        off.Sort();
        long seq = _h.Fleets.SubmitSplitFleet(_side, f.Id, off);
        Note(seq, "Split", $"{off.Count} ships off fleet {f.Id} at {f.At} (task force)");
    }

    /// <summary>
    /// Task forces: the least strength of one task force, TaskForceMarginPercent of the strongest enemy fleet the side knows of (the picture's
    /// fleets with fog, older sightings fading by TaskForceFadePercent; every enemy fleet without), and at least LaunchStrength (cached per day).
    /// </summary>
    private double TaskForceStrength() => _tfStrength ??= Math.Max(_p.LaunchStrength, StrongestEnemyFleet() * _p.TaskForceMarginPercent / 100.0);
    private double? _tfStrength;

    /// <summary>
    /// The home guard is whole fleets, so new warships delivered to it while it was weak stay there for good once it is strong (seen on the
    /// v5 galaxy: a Collective HQ guard of strength 119 sat still for 300 days while the strike pile never reached LaunchStrength). When one
    /// guard fleet is at least LaunchStrength above HomeDefenceStrength, its newest armed ships are split off (one order) while the guard keeps
    /// HomeDefenceStrength; the split fleet is a strike fleet from the next review. A ship's share of the fleet strength is in proportion to its
    /// class strength. DEFAULT (lead 2026-10-16, a bug fix of the home-defence rule above).
    /// </summary>
    private void SplitHomeSurplus(Fleet guard, double strength)
    {
        if (strength - _p.HomeDefenceStrength < _p.LaunchStrength || guard.Ships.Count < 2) return;
        var cls = guard.Ships.Select(id => (Id: id, S: ClassStrength(_h.Economy.Find(id)!.Class))).ToList();
        double total = cls.Sum(c => c.S);
        if (total <= 0) return;
        var off = new List<int>(); double left = strength;
        foreach (var (id, cs) in cls.Where(c => c.S > 0).OrderByDescending(c => c.Id))
        {
            double share = strength * cs / total;
            if (left - share < _p.HomeDefenceStrength) continue;
            off.Add(id); left -= share;
        }
        if (off.Count == 0 || off.Count == guard.Ships.Count || strength - left < _p.LaunchStrength) return;
        off.Sort();
        long seq = _h.Fleets.SubmitSplitFleet(_side, guard.Id, off);
        Note(seq, "Split", $"{off.Count} ships off the home guard {guard.Id} at {guard.At}");
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

        // enemy fleets on this very system: fight on unless clearly outmatched (m4-retreat-damage-repair: a fleet may leave)
        if (Threats().TryGetValue(at, out var here) && here.Any(x => x.At == at))
        {
            var friends = _h.Fleets.FleetsAt(at).Where(x => x.Side == _side);
            var enemiesHere = here.Where(x => x.At == at).ToList();
            if (Duel(friends, enemiesHere) < _p.RetreatOddsPercent)
            {
                var safe = YardSystems(YardFamily.Shipyard).Where(sy => sy != at && !Threats().ContainsKey(sy)).OrderBy(sy => Ticks(at, sy, 100)).ThenBy(sy => sy, StringComparer.Ordinal).FirstOrDefault() ?? rally;
                if (safe != null && safe != at) { foreach (var m in p.Members) Move(m, safe); return; }
            }
            return;
        }

        // regiments waiting here above the garrison, and fighters based here
        int aboard = p.Aboard;
        var spare = SpareRegimentIds(at);
        int take = Math.Min(spare.Count, p.FreeSlots);
        // two fleets at one system in one review would name the same regiments; the second order could only bounce (AlreadyAboard), so it is not sent
        if (take > 0 && spare.Take(take).Any(_loading.Contains)) take = 0;
        if (take > 0)
        {
            _loading.UnionWith(spare.Take(take));
            long seq = _h.Troops.SubmitLoad(_side, f.Id, spare.Take(take));
            Note(seq, "LoadRegiments", $"{take} onto fleet {f.Id} at {at}");
            aboard += take;
        }
        int bt = p.Berths;
        if (bt > 0)
        {
            var sq = _h.Fleets.BasedSquadrons(at, _side).Select(o => o.Id).Take(bt).ToList();
            if (sq.Count > 0 && !sq.Any(_loading.Contains)) // the same for squadrons (SquadronNotBased)
            {
                _loading.UnionWith(sq);
                long seq = _h.Fleets.SubmitLoadSquadrons(_side, f.Id, sq);
                Note(seq, "LoadSquadrons", $"{sq.Count} onto fleet {f.Id} at {at}");
            }
        }

        bool damaged = p.HullPct < _p.RepairBelowPercent;
        // defence first: a threatened system we can relieve beats repairs
        var relief = PickRelief(p, at);
        if (relief != null && Move(f, relief)) return;
        if (damaged)
        {
            if (!HasShipyard(at) && rally != null) Move(f, rally); // repair happens at a shipyard
            return;
        }
        bool strong = p.Strength >= _p.LaunchStrength;
        if (strong)
        {
            // short of troops: fetch spare regiments waiting on another held system (the rally system may have moved since they were ordered)
            bool Fetch()
            {
                if (p.FreeSlots - take <= 0) return false;
                string? pick = null; long bestPick = 0; int speed = _h.Fleets.SpeedOf(f);
                foreach (var hs in HeldSystems())
                {
                    if (hs == at || Threats().ContainsKey(hs)) continue;
                    int n = SpareRegimentsAt(hs); if (n == 0) continue;
                    int tk = Ticks(at, hs, speed); if (tk == int.MaxValue) continue;
                    long sc = Math.Min(n, p.FreeSlots) * 1000L / (tk + 20); if (sc > bestPick) { bestPick = sc; pick = hs; }
                }
                return pick != null && Move(f, pick);
            }
            // the system that wins the game, believed (nearly) undefended and within reach: load the regiments it needs first, then go for it
            if (aboard < WinTroopsWanted(p, at) && Fetch()) return;
            bool limited = _p.MaxOffensives != int.MaxValue;
            var t = !limited || Offensives() < _p.MaxOffensives ? PickTarget(p, aboard, at) : null;
            if (t != null && Move(f, t)) { if (limited) _offensives++; return; }
            if (aboard < _p.MinLaunchRegiments && Fetch()) return;
        }
        if (rally != null && at != rally) Move(f, rally); // gather (and reload) at the rally system
    }

    /// <summary>
    /// Regiments the pile should carry before it strikes the system that wins the game, or 0 when no such strike is on: the win target is
    /// known and not held, the pile is at least WinStrikeStrength, it can reach it, an assault is allowed there as far as the side knows, its
    /// fleets there (as believed) are beaten at the attack odds, the pile did not bounce off it lately, the regiments believed there are at
    /// most WinRushDefenders, and the regiments needed fit aboard. (The hunt for fleets that only retreat must not keep an empty strike pile
    /// off an open capital: m6 chase fix.)
    /// </summary>
    private int WinTroopsWanted(Pile p, string from)
    {
        string? win = WinTarget();
        if (win == null || win == from || Held(win) || p.Strength < _p.WinStrikeStrength || BouncedOff(win)) return 0;
        int speed = _h.Fleets.SpeedOf(p.Leader);
        if (speed <= 0 || Ticks(from, win, speed) == int.MaxValue) return 0;
        if (AssaultForbiddenSeen(win) || !SlotsSeen(win).Populated) return 0;
        var enemies = TargetFleetsAt(win);
        if (enemies.Count > 0 && Duel(p.Members, enemies) < TargetOdds(win)) return 0;
        int d = DefendersAt(win);
        if (d > _p.WinRushDefenders) return 0;
        int need = Need(_h.Bombardment.Power(p.Leader) > 0 ? d / 2 : d);
        return need <= p.Aboard + p.FreeSlots ? need : 0;
    }

    /// <summary>A held system with enemy fleets on it or on the way that the pile (with the fleets already there) beats; the critical system first, then the nearest.</summary>
    private string? PickRelief(Pile p, string from)
    {
        int speed = _h.Fleets.SpeedOf(p.Leader);
        if (speed <= 0) return null;
        string? best = null; long bestScore = long.MinValue;
        string? crit = CriticalSystem();
        foreach (var kv in Threats().OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string s = kv.Key;
            if (s == from || BouncedOff(s)) continue;
            int ticks = Ticks(from, s, speed);
            if (ticks == int.MaxValue) continue;
            var friends = _h.Fleets.FleetsAt(s).Where(x => x.Side == _side).Concat(p.Members);
            if (Duel(friends, kv.Value) < OddsAt(s, _p.DefendOddsPercent)) continue;
            long score = 1_000_000L / (ticks + 10) * (s == crit ? 3 : 1);
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    /// <summary>Days RecentBombardments looks back (the AI's own tuning since the bombardment AI was written, not a rule of the game).</summary>
    private const int RecentBombardmentDays = 30;

    private int RecentBombardments(string system) => NotesSince(AiNoteKind.Bombardment, Today - RecentBombardmentDays).Count(b => b.System == system);

    // A fleet at a system it does not hold: fight on, bombard, assault, or go elsewhere.
    private void DecideAtTarget(Fleet f)
    {
        string at = f.At!;
        var rally = Rally() ?? Capital();
        var mineHere = _h.Fleets.FleetsAt(at).Where(x => x.Side == _side).ToList();
        var enemies = EnemyFleetsAt(at);
        if (enemies.Count > 0)
        {
            if (Duel(mineHere, enemies) < _p.RetreatOddsPercent && rally != null) Move(f, rally); // too strong for us: leave
            return; // else the battle takes its course
        }
        int aboard = _h.Troops.RegimentsAboard(f).Count;
        int d = DefendersAt(at);
        bool canShell = _h.Bombardment.Power(f) > 0;
        if (AssaultForbiddenSeen(at)) { Leave(f); return; } // rule: m3-planetary-defenses (assault forbidden, 2+ shield generators)
        if (_view != null && !SlotsSeen(at).Populated) { Leave(f); return; } // fog: the target was picked unseen; an empty world cannot be assaulted
        // rule: m3-assault §Order and preconditions: a fleet has one order at a time, so shell one day and land the next.
        bool win = at == WinTarget();
        // fog: the shelling cannot be seen to work, so even the winning system is shelled a bounded number of times
        int maxShell = _view != null && win ? _p.FogWinBombardmentsPer30Days : _p.MaxBombardmentsPer30Days;
        // NoBlindStrikes (QA-22): a Military strike with nothing to hit wastes every hit and still rolls collateral against the bomber (B7 §5.3)
        bool targetSeen = !_p.NoBlindStrikes || MilitaryTargetSeen(at);
        if (d > 0 && _h.Bombardment.Power(f) > 0 && targetSeen && ((win && _view == null) || RecentBombardments(at) < maxShell))
        {
            long seq = _h.Bombardment.SubmitBombard(_side, f.Id, at, BombardCategory.Military);
            Note(seq, "Bombard", $"fleet {f.Id} shells {at}");
            return;
        }
        // rule: ai tuning (m6 stall fix). PickTarget chose this system on Need(d / 2) (the shelling halves the defenders); the landing here asked for
        // Need(d) again. With fog the believed d never falls without own kills (the garrison seen is a guess or stale; an empty or small garrison
        // gives none), so the fleet shelled four times, left, and came back again and again, never landing. Shelled and armed: ask what the pick asked.
        // NoBlindStrikes: no target seen to shell, so land on what PickTarget asked, as after a shelling
        bool shelled = _p.ShelledLandingNeed && canShell && (RecentBombardments(at) > 0 || !targetSeen);
        if (aboard >= 1 && aboard >= Need(shelled ? d / 2 : d))
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
        var p = MakePile(f, new List<Fleet> { f });
        var t = PickRelief(p, f.At!) ?? PickTarget(p, p.Aboard, f.At!);
        if (t != null && Move(f, t)) return;
        if (rally != null) Move(f, rally);
    }

    /// <summary>FinishWhenAheadRatio: the side holds at least that many times the systems it knows the enemy to hold (cached per day).</summary>
    private bool Finishing()
    {
        if (_p.FinishWhenAheadRatio <= 0) return false;
        if (_finishing is bool b) return b;
        int enemy = EnemySystems().Count();
        return (bool)(_finishing = enemy > 0 && HeldSystems().Count() >= _p.FinishWhenAheadRatio * enemy);
    }
    private bool? _finishing;

    private int[] Hops()
    {
        if (_hops != null) return _hops;
        var g = _h.Data.Galaxy; var dist = new int[g.Systems.Count]; Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        foreach (var s in HeldSystems()) if (g.TryFindSystem(s, out var i)) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (var n in g.NearOf(c)) if (dist[n] == int.MaxValue) { dist[n] = dist[c] + 1; q.Enqueue(n); }
        }
        return _hops = dist;
    }

    /// <summary>
    /// Best system to send a pile to: an enemy fleet it beats (fleets first: they are what makes the other targets safe), an enemy or neutral
    /// system near the border with a weak defence (lost systems score double), or the system that wins the game when the pile is strong enough;
    /// with fog and no enemy system on the picture (BlindHunt, QA-19), also a system the side has not looked at for long.
    /// </summary>
    private string? PickTarget(Pile pile, int aboard, string from)
    {
        var f = pile.Leader; double strength = pile.Strength;
        var g = _h.Data.Galaxy; var hops = Hops();
        int speed = _h.Fleets.SpeedOf(f);
        if (speed <= 0) return null;
        bool canBombard = _h.Bombardment.Power(f) > 0;
        string? win = WinTarget();
        var lost = LostSystems();
        var scored = new List<(string Sys, long Score)>();
        bool search = HqSearchOn && strength >= _p.WinStrikeStrength;
        bool finishing = Finishing();
        bool blind = BlindOn();
        foreach (var sys in g.Systems)
        {
            string s = sys.Name;
            if (s == from || Held(s) || BouncedOff(s) || Destroyed(s)) continue;
            bool isWin = s == win && strength >= _p.WinStrikeStrength;
            if (_tfClaims != null && !isWin && _tfClaims.Contains(s)) continue; // task forces: another pile of the group took it on
            // fog, blind hunt (QA-19): the picture shows no enemy system, so a system not looked at for long is worth a visit
            int blindValue = blind ? BlindTargetValue(s, hops[sys.Index]) : 0;
            bool isBlind = blindValue > 0;
            // fog: systems the picture shows enemy-held reach farther; the Hand's HQ candidates (belief map) from any distance
            bool seenEnemy = _view != null && IsEnemyHeld(s);
            int reach = seenEnemy ? Math.Max(_p.TargetHops, _p.FogEnemyTargetHops) : _p.TargetHops;
            int belief = search && seenEnemy ? Belief(s) : 0;
            bool isSearch = belief > 0;
            if (hops[sys.Index] > reach + 1 && !isWin && !isSearch && !isBlind) continue;
            int ticks = _view != null ? TicksCached(from, s, speed) : Ticks(from, s, speed);
            if (ticks == int.MaxValue) continue;
            var enemies = TargetFleetsAt(s);
            double e = 0;
            if (enemies.Count > 0)
            {
                if (Duel(pile.Members, enemies) < TargetOdds(s)) continue;
                e = enemies.Sum(Str);
            }
            long score = 0;
            // the fleet itself: hunt it when the system is near the border and the pile is armed enough, unless it only runs from us (it
            // retreated from this side here or next door lately: the chase would go on with no losses on either side)
            if (e > 0 && ticks <= _p.HuntTicks && hops[sys.Index] <= _p.TargetHops && !EnemyRunsNear(s)) score = 3000L * (long)(e * 100 + 20) / (ticks + 20);
            // the system: needs regiments
            if (hops[sys.Index] <= reach || isWin || isSearch || isBlind)
            {
                var slots = SlotsSeen(s);
                bool ok = slots.Populated && !AssaultForbiddenSeen(s) && aboard >= 1;
                if (ok)
                {
                    int d = DefendersAt(s);
                    ok = aboard >= Need(canBombard ? d / 2 : d);
                    if (ok)
                    {
                        long value = 10 + slots.Energy + 2 * slots.Raw;
                        if (IsEnemyHeld(s)) value += 10 + 2 * FacilitiesAt(s);
                        if (finishing && IsEnemyHeld(s)) value = value * _p.FinishValuePercent / 100;
                        if (lost.Contains(s)) value *= 2;
                        if (s == KnownEnemyHq) value += 200;
                        if (isWin) value += 400;
                        if (isSearch) value += _p.SearchValue * belief / Math.Max(1, BeliefMax);
                        value += blindValue;
                        score = Math.Max(score, value * 1000 / (ticks + 20));
                    }
                }
            }
            if (score > 0) scored.Add((s, score));
        }
        if (scored.Count == 0) return null;
        string pick;
        // difficulty: pick noise (ai/09 §6), a random one of the three best targets
        if (_p.PickNoisePercent > 0 && _rng.NextInt(1, 100) <= _p.PickNoisePercent)
        {
            var top3 = scored.OrderByDescending(t => t.Score).ThenBy(t => t.Sys, StringComparer.Ordinal).Take(3).ToList();
            pick = top3[_rng.NextInt(0, top3.Count - 1)].Sys;
        }
        else
        {
            long best = scored.Max(t => t.Score);
            // tie-break (private generator): any target within 5% of the best
            var top = scored.Where(t => t.Score * 100 >= best * 95).OrderBy(t => t.Sys, StringComparer.Ordinal).ToList();
            pick = top[_rng.NextInt(0, top.Count - 1)].Sys;
        }
        _tfClaims?.Add(pick);
        return pick;
    }
}
