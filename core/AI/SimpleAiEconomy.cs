using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.AI;

/// <summary>The simple AI's economy, production and upkeep-crisis planning (moved unchanged from SimpleAi.cs).</summary>
public sealed partial class SimpleAi
{
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
        int ships = CountYards(YardFamily.Shipyard), train = CountYards(YardFamily.Training), cons = CountYards(YardFamily.Construction);
        if (ships == 0) order.Add(FacilityKind.OrbitalShipyard);
        if (train == 0) order.Add(FacilityKind.TrainingFacility);
        // rule: m2-construction §Build time: a yard adds 1 point per cycle, so throughput is the number of yards; refined that floats is turned into yards.
        if (refined >= _p.ExtraYardRefinedReserve)
        {
            // the construction yards build everything else: the first ones come before the other yards
            if (cons < _p.EarlyConstructionYards) order.Add(FacilityKind.ConstructionYard);
            var wants = new List<(FacilityKind Kind, int Have, int Want)>
            {
                (FacilityKind.OrbitalShipyard, ships, _p.DesiredShipyards),
                (FacilityKind.TrainingFacility, train, TrainingFacilitiesWanted()),
                (FacilityKind.ConstructionYard, cons, _p.DesiredConstructionYards),
            };
            foreach (var w in wants.Where(w => w.Have < w.Want).OrderBy(w => w.Have * 100 / w.Want).ThenBy(w => (int)w.Kind)) order.Add(w.Kind);
        }
        int mines = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Mine" && eco.OwnerOf(o) == _side);
        int refs = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Refinery" && eco.OwnerOf(o) == _side);
        // rule: m2-maintenance §Capacity (capacity = 50 x min(mines, refineries)): keep the two balanced.
        if (mines <= refs) { order.Add(FacilityKind.Mine); order.Add(FacilityKind.Refinery); }
        else { order.Add(FacilityKind.Refinery); order.Add(FacilityKind.Mine); }
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

    private static bool IsYardKind(FacilityKind k) => k is FacilityKind.OrbitalShipyard or FacilityKind.TrainingFacility or FacilityKind.ConstructionYard;

    private static YardFamily FamilyOfKind(FacilityKind k) => Yards.FamilyOf(k);

    /// <summary>Yards of a family standing or queued on a system (the side's).</summary>
    private int YardsAt(string system, YardFamily fam) => _h.Economy.Objects.Count(o => o.System == system && IsYardClass(o, fam));

    private string? PickSite(string from, FacilityKind kind)
    {
        var eco = _h.Economy;
        bool mine = kind == FacilityKind.Mine, yard = IsYardKind(kind);
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
            if (yard)
            {
                // stack on a system that already has yards of the family (they share a project), up to YardsPerSystem; keep yards off the border
                var fam = FamilyOfKind(kind);
                int here = YardsAt(s, fam) + _reservedYards.GetValueOrDefault((s, fam));
                if (here >= _p.YardsPerSystem) continue;
                if (here > 0) score += 40 * here;
                score -= BorderDanger(s) * 15;
            }
            else score -= YardsAnyAt(s) * 3; // keep room on the yard systems for more yards
            if (score > bestScore) { bestScore = score; best = s; }
        }
        if (best != null && yard) { var fam = FamilyOfKind(kind); _reservedYards[(best, fam)] = _reservedYards.GetValueOrDefault((best, fam)) + 1; }
        return best;
    }

    private int YardsAnyAt(string system) => _h.Economy.Objects.Count(o => o.System == system && o.Category == ObjectCategory.Facility && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k));

    /// <summary>0 when no nearby system (m1-galaxy-data Nearby) is enemy-held, 2 when one is (a yard there is a target).</summary>
    private int BorderDanger(string system)
    {
        var g = _h.Data.Galaxy;
        if (!g.TryFindSystem(system, out var i)) return 0;
        foreach (var n in g.NearOf(i))
            if (IsEnemyHeld(g.Systems[n].Name)) return 2;
        return 0;
    }

    // ================================================================== production

    private void PlanProduction()
    {
        if (_view != null) PlanScoutBuild();
        if (_view != null && _side == Side.Hand) PlanSpeculatorBuild();
        PlanGarrison();
        PlanInvasionRegiments();
        PlanAgentForceBuild(); // after the regiments: a training yard goes to the garrisons first
        PlanHarvesterBuild(); // before the ships: it takes its yard and its budget first (m5-harvester §6.1)
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
            {
                d[pr.Destination] = d.GetValueOrDefault(pr.Destination) + pr.Units.Count;
                foreach (var q in pr.Next ?? new List<QueuedOrder>()) d[q.Destination] = d.GetValueOrDefault(q.Destination) + q.Units.Count; // orders queued behind (B5b)
            }
        return d;
    }

    // rule: m2-uprisings-and-garrison §Required garrison N: keep N regiments on every system where the support is low.
    private void PlanGarrison()
    {
        _garrisonReserve = 0;
        string cls = _p.GarrisonRegiment(_side);
        if (!Available(cls)) return;
        var pending = PendingRegiments();
        var held = HeldSystems();
        bool first = _p.GarrisonsFirst && Finishing(); // far ahead, the side needs its garrisons more than new warships
        // GarrisonsFirst: the worlds in uprising, then the fewest regiments, get the upkeep first (ties: ordinal name)
        if (first) held = held.OrderByDescending(_h.Support.IsUprising).ThenBy(_h.Support.RegimentCount).ThenBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var s in held)
        {
            int need = GarrisonTarget(s);
            if (need == 0) continue;
            int deficit = need - _h.Support.RegimentCount(s) - pending.GetValueOrDefault(s);
            if (deficit <= 0) continue;
            int count = Math.Min(deficit, _p.GarrisonBatch);
            if (first) while (count > 1 && Budget < count * Maint(cls)) count--;
            var yard = Budget < count * Maint(cls) ? null : TrainingYardNear(s);
            if (yard == null)
            {
                if (!first) { if (Budget < count * Maint(cls)) continue; return; } // every training yard is busy
                _garrisonReserve += deficit * Maint(cls); // GarrisonsFirst: unpaid or waiting for a yard, its upkeep is still kept from the ships
                continue;
            }
            OrderRegiments(yard, cls, count, s);
            if (first) _garrisonReserve += (deficit - count) * Maint(cls);
        }
    }

    /// <summary>GarrisonsFirst: upkeep the garrisons still short of their need would take, kept from new ships (set by PlanGarrison).</summary>
    private int _garrisonReserve;
    /// <summary>
    /// Training facilities wanted: DesiredTrainingFacilities, and with GarrisonsFirst while far ahead one more per GarrisonShortPerTrainingFacility
    /// regiments the garrisons are short (queued ones count), up to MaxTrainingFacilities (one facility trains about 10 regiments in 250 days:
    /// m2-construction §Build time).
    /// </summary>
    private int TrainingFacilitiesWanted()
    {
        if (!_p.GarrisonsFirst || !Finishing()) return _p.DesiredTrainingFacilities;
        var pending = PendingRegiments();
        int shortBy = HeldSystems().Sum(s => Math.Max(0, GarrisonTarget(s) - _h.Support.RegimentCount(s) - pending.GetValueOrDefault(s)));
        return Math.Min(Math.Max(_p.DesiredTrainingFacilities, _p.MaxTrainingFacilities), _p.DesiredTrainingFacilities + shortBy / Math.Max(1, _p.GarrisonShortPerTrainingFacility));
    }

    private void PlanInvasionRegiments()
    {
        var rally = Rally();
        string cls = _p.InvasionRegiment(_side);
        if (rally == null || !Available(cls)) return;
        // InvasionBatchesOnly: a lone spare regiment on a far system is not an invasion force (a strike pile does not fly across the map for one)
        int have = _h.Economy.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == _side && o.AboardShip != 0)
                   + HeldSystems().Select(SpareRegimentsAt).Where(n => !_p.InvasionBatchesOnly || n >= _p.MinLaunchRegiments).Sum();
        have += PendingRegiments().GetValueOrDefault(rally);
        if (have >= _p.InvasionRegimentTarget) return;
        int count = Math.Min(_p.InvasionRegimentTarget - have, _p.GarrisonBatch);
        if (Budget < count * Maint(cls)) return;
        var yard = TrainingYardNear(rally);
        if (yard != null) OrderRegiments(yard, cls, count, rally);
    }

    /// <summary>Landed regiments of the side on a system above what its garrison needs.</summary>
    internal List<int> SpareRegimentIds(string system)
    {
        var ids = _h.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == _side && o.System == system && o.AboardShip == 0)
            .Select(o => o.Id).ToList();
        int keep = Held(system) ? GarrisonTarget(system) : 0;
        return ids.Count > keep ? ids.Skip(keep).ToList() : new List<int>();
    }

    // The Classic AI's garrison wish (B6b §3, EXE-own 0x434e4d..0x43507c): below 70 support min((79 - s) / 10, 6), doubled in an uprising (after the cap);
    // the special world's base is 9 (the claimed-world base 3 and the base drift are the Classic AI's, not built here).
    private const int WishBelowSupport = 70, WishTop = 79, WishStep = 10, WishCap = 6, SpecialBase = 9, UprisingFactor = 2;

    /// <summary>
    /// Regiments the AI wants standing on a held world: built toward (PlanGarrison) and never moved off (SpareRegimentIds). With
    /// <see cref="SimpleAiParams.ClassicGarrisonFloor"/> (DEFAULT): max(the rule's need, the Classic wish, 9 on the special world). Off: the rule's need,
    /// <see cref="SimpleAiParams.CriticalGarrison"/> on the critical world, and never the last regiment below 60 support (the B5a guard, Dev lead
    /// 2026-10-17). rule: ai tuning, B6b §3 and §11 (Dev lead 2026-10-18).
    /// </summary>
    /// <summary>
    /// The world the Classic AI's special base (9) applies to: Charter for the Hand; for the Collective the Hearth's world, or while the HQ is on
    /// the way, its destination (its own HQ: the side knows where it goes), so regiments are ordered there when the move starts and the HQ does
    /// not arrive on an unguarded world (QA-10, Dev lead 2026-10-18). rule: ai tuning, B6b §3 ("special" system).
    /// </summary>
    private string? SpecialSystem()
    {
        if (CriticalSystem() is { } c) return c;
        var hq = _h.Game.State.Scenario.Hq;
        return _side == Side.Collective && hq.Status == Praedra.Core.Fog.HqStatus.InTransit && hq.Path.Count > 0 ? hq.Path[^1] : null;
    }

    internal int GarrisonTarget(string system)
    {
        int need = _h.Support.RequiredGarrison(system);
        int s = _h.Economy.SupportOf(system, _side); // its own world: the AI sees it
        if (!_p.ClassicGarrisonFloor)
        {
            if (system == CriticalSystem()) need = Math.Max(need, _p.CriticalGarrison);
            if (s < Praedra.Core.Control.ControlModule.JoinSupport) need = Math.Max(need, 1);
            return need;
        }
        int wish = s < WishBelowSupport ? Math.Min((WishTop - s) / WishStep, WishCap) : 0;
        if (_h.Support.IsUprising(system)) wish *= UprisingFactor;
        int @base = system == SpecialSystem() ? SpecialBase : 0;
        return Math.Max(need, Math.Max(wish, @base));
    }
    private int SpareRegimentsAt(string system) => SpareRegimentIds(system).Count;

    private readonly Dictionary<string, double> _classStrength = new(StringComparer.Ordinal); // lookup only; static data, so it survives days

    /// <summary>Strength of one ship or squadron of the class against the side's reference enemy line (FleetModule's CombatStrength, shields full).</summary>
    private double ClassStrength(string cls)
    {
        if (_classStrength.TryGetValue(cls, out var v)) return v;
        var ships = _h.Fleets.ShipClasses;
        var versus = _p.ReferenceEnemy(_side).Where(ships.ContainsKey).Select(n => ships[n]).ToList();
        v = ships.TryGetValue(cls, out var sc) && versus.Count > 0 ? _h.Fleets.Strength.Of(new[] { sc }, versus) : 0;
        return _classStrength[cls] = v;
    }

    private IEnumerable<string> ShipClassNames(ObjectCategory cat) =>
        _h.Fleets.ShipClasses.Keys.OrderBy(n => n, StringComparer.Ordinal)
            .Where(n => _h.Economy.Costs.TryGet(n, out var r) && r.Category == cat && Available(n));

    /// <summary>The fighter class with the most strength per refined cost plus upkeep. Null when none.</summary>
    private string? BestFighter()
    {
        string? best = null; double bs = 0;
        foreach (var n in ShipClassNames(ObjectCategory.Fighter))
        {
            var r = _h.Economy.Costs.Get(n);
            double sc = ClassStrength(n) / (r.Cost + r.Maintenance);
            if (sc > bs) { bs = sc; best = n; }
        }
        return best;
    }

    private int YardsDoneAt(string system, YardFamily fam) => _h.Economy.Objects.Count(o => o.Completed && o.System == system && IsYardClass(o, fam));

    /// <summary>
    /// The warship that gives the most strength per price (refined cost plus upkeep), counting each fighter berth as one squadron of the best
    /// fighter; only classes the yards of the system finish within MaxBuildDays. Null when nothing fits.
    /// </summary>
    private string? BestWarship(string yardSystem, string? fighter)
    {
        var eco = _h.Economy;
        int yards = Math.Max(1, YardsDoneAt(yardSystem, YardFamily.Shipyard));
        double fighterStr = fighter != null ? ClassStrength(fighter) : 0;
        string? best = null; double bs = 0;
        foreach (var n in ShipClassNames(ObjectCategory.CapitalShip))
        {
            if (n == EconomyModule.HarvesterClass) continue;
            var sc = _h.Fleets.ShipClasses[n]; var r = eco.Costs.Get(n);
            // rule: m2-construction §Build time: ceil(cost / yards) x 4 days per unit (standard yards)
            if ((r.Cost + yards - 1) / yards * 4 > _p.MaxBuildDays) continue;
            if (Budget < r.Maintenance) continue;
            double value = ClassStrength(n) + fighterStr * sc.FighterCapacity * _p.FighterTrustPercent / 100;
            if (value <= 0) continue;
            double score = value / (r.Cost + _p.UpkeepWeight * r.Maintenance); // upkeep is the lasting limit (m2-maintenance), so it weighs more than the one-time cost
            if (score > bs + 1e-9) { bs = score; best = n; }
        }
        return best;
    }

    private void PlanShips()
    {
        var eco = _h.Economy;
        var rally = Rally(); var cap = GuardSystem();
        int troopCap = 0, berths = 0, fighters = 0;
        foreach (var o in eco.Objects)
        {
            if (eco.OwnerOf(o) != _side) continue;
            if (o.Category == ObjectCategory.CapitalShip && _h.Fleets.ShipClasses.TryGetValue(o.Class, out var sc)) { if (o.Class == _p.Transport(_side)) troopCap += sc.TroopCapacity; berths += sc.FighterCapacity; } // only transports count as troop ships: warships carry a few, but stay with the fleet
            else if (o.Category == ObjectCategory.Fighter) fighters++;
        }
        double home = 0;
        if (cap != null) foreach (var f in _h.Fleets.FleetsAt(cap)) if (f.Side == _side) home += Str(f);
        bool homeWeak = home < _p.HomeDefenceStrength;
        string? fighter = BestFighter();
        bool transportOrdered = false;

        foreach (var y in YardSystems(YardFamily.Shipyard))
        {
            if (!CanOrderAt(y, YardFamily.Shipyard)) continue;
            string? dest = rally;
            if (dest != null && dest != y && Ticks(y, dest, UnratedSpeed) == int.MaxValue) dest = null;
            string? destArg = dest == y ? null : dest;

            string? cls = null; int count = 1;
            string transport = _p.Transport(_side);
            if (troopCap < _p.TroopCapacityTarget && Available(transport) && !transportOrdered)
            {
                cls = transport;
                int per = Math.Max(1, _h.Fleets.ShipClasses[transport].TroopCapacity);
                count = Math.Min(2, (_p.TroopCapacityTarget - troopCap + per - 1) / per);
                transportOrdered = true;
            }
            else if (fighter != null && fighters * 100 < berths * _p.FighterFillPercent && berths - fighters >= 1)
            {
                cls = fighter;
                count = Math.Clamp(berths * _p.FighterFillPercent / 100 - fighters, 1, Math.Min(berths - fighters, _p.FightersPerOrder));
            }
            else
            {
                cls = BestWarship(y, fighter);
                if (cls != null)
                {
                    // the home guard is topped up with armed ships (only where it has a shipyard to repair at)
                    if (homeWeak && cap != null && ClassStrength(cls) > 0 && Ticks(y, cap, UnratedSpeed) != int.MaxValue) { dest = cap; destArg = cap == y ? null : cap; }
                    int cost = eco.Costs.Get(cls).Cost;
                    count = Math.Clamp(eco.Refined(_side) / Math.Max(1, cost * 3), 1, _p.MaxShipsPerOrder);
                }
            }
            if (cls == null) continue;
            // rule: m2-construction §Build time: an order keeps the yard busy for count x ceil(cost / yards) x 4 days; cap it so the yard can answer news
            {
                int yardsHere = Math.Max(1, YardsDoneAt(y, YardFamily.Shipyard)); int unitDays = (eco.Costs.Get(cls).Cost + yardsHere - 1) / yardsHere * 4;
                count = Math.Max(1, Math.Min(count, _p.MaxOrderDays / Math.Max(1, unitDays)));
            }
            int budget = Budget - _garrisonReserve; // GarrisonsFirst: the garrisons are paid first
            while (count > 1 && budget < count * Maint(cls)) count--;
            if (budget < count * Maint(cls)) continue;
            long seq = _h.Construction.SubmitBuild(_side, y, YardFamily.Shipyard, cls, count, destArg);
            Note(seq, "Build", $"{count} x {cls} at {y} for {dest ?? y}");
            _busy.Add((y, YardFamily.Shipyard));
            _plannedLoad += count * Maint(cls);
            var cr = eco.Costs.Get(cls);
            if (cr.Category == ObjectCategory.CapitalShip) { troopCap += count * _h.Fleets.ShipClasses[cls].TroopCapacity; berths += count * _h.Fleets.ShipClasses[cls].FighterCapacity; }
            else if (cr.Category == ObjectCategory.Fighter) fighters += count;
        }
    }

    // ================================================================== upkeep crisis

    /// <summary>
    /// rule: m2-maintenance §Overdraft and autoscrap. When the load is above the capacity (a capture took mines or refineries away) the game
    /// would scrap one random finished object every 10 days (a yard as likely as a regiment). The AI scraps its least useful items itself,
    /// right away, enough to be back under the capacity. Yards, mines and refineries are never offered.
    /// </summary>
    private void PlanUpkeepCrisis()
    {
        int over = -_h.Economy.Free(_side);
        if (over <= 0) return;
        var eco = _h.Economy;
        string? fighter = BestFighter();
        double fighterStr = fighter != null ? ClassStrength(fighter) : 0;
        var cands = new List<(double Use, int Id, int Maint)>();
        foreach (var o in eco.Objects.OrderBy(o => o.Id))
        {
            if (!o.Completed || eco.OwnerOf(o) != _side || o.Category == ObjectCategory.Facility || o.Category == ObjectCategory.SpecialForce) continue;
            int m = Maint(o.Class);
            bool keep = _p.UseHarvester && IsHarvesterShip(o); // rule: m5-harvester §6.2: scrapped last, after everything else
            if (m <= 0) continue;
            double use;
            if (o.Category == ObjectCategory.Regiment)
            {
                if (o.AboardShip != 0 || o.System == null || !Held(o.System)) continue;
                if (!SpareRegimentIds(o.System).Contains(o.Id)) continue; // garrison regiments hold the system
                use = -1;
            }
            else
            {
                var f = _h.Fleets.FleetOfShip(o.Id);
                if (o.Category == ObjectCategory.CapitalShip && (f == null || f.Transit != null || Pending(f))) continue;
                if (o.Category == ObjectCategory.Fighter && o.AboardShip != 0 && _h.Fleets.FleetOfShip(o.AboardShip) is { Transit: not null }) continue;
                _h.Fleets.ShipClasses.TryGetValue(o.Class, out var sc);
                double v = ClassStrength(o.Class) + (sc != null ? 6 * sc.TroopCapacity + fighterStr * sc.FighterCapacity * _p.FighterTrustPercent / 100 : 0);
                use = v * 1000 / m;
                if (keep) use += 1e12;
            }
            cands.Add((use, o.Id, m));
        }
        foreach (var c in cands.OrderBy(c => c.Use).ThenBy(c => c.Id))
        {
            if (over <= 0) break;
            long seq = _h.Game.Submit(sq => new ScrapObjectOrder(sq, _side, c.Id));
            Note(seq, "Scrap", $"object {c.Id} (upkeep over capacity)");
            over -= c.Maint;
        }
    }
}
