using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;

namespace Praedra.Core.AI;

// The Hand AI and the harvester. rule: m5-harvester §6 (Computer opponent), switch SimpleAiParams.UseHarvester (DEFAULT true).
// 1 build one at a time, with headroom, at the system with the most shipyards; 2 never scrap it first (PlanUpkeepCrisis); 3 wait for an escort of at least E; 4 go only to the Collective
// HQ the AI knows (KnownEnemyHq: with fog that is the side's own picture) and fire on the day it stands there with no enemy fleet left;
// if the HQ moves, go to the newly known one; 5 the Collective never builds one. Everything here is read through the same helpers as the rest
// of the AI (fog view when UseFog is on), so it never peeks at the true HQ.
public sealed partial class SimpleAi
{
    private Fleet? _dsFleet;       // the idle harvester fleet of this review (lowest id), null when none
    private string? _dsWaitAt;     // where it waits for an escort (held system), null when nobody should be called to it

    private bool HarvesterOn => _p.UseHarvester && _side == Side.Hand;

    private static bool IsHarvesterShip(EconObject o) => o.Category == ObjectCategory.CapitalShip && o.Class == EconomyModule.HarvesterClass;

    private bool HasHarvesterShip(Fleet f) =>
        f.Ships.Any(id => _h.Economy.Find(id) is { Completed: true } o && IsHarvesterShip(o));

    // ---- 1. build. rule: m5-harvester §6.1
    private void PlanHarvesterBuild()
    {
        if (!HarvesterOn) return;
        const string cls = EconomyModule.HarvesterClass;
        var eco = _h.Economy;
        if (!Available(cls)) return;
        // one at a time: none alive (in a fleet, in delivery) or queued. A queued unit is already an object of the side.
        if (eco.Objects.Any(o => o.Class == cls && eco.OwnerOf(o) == _side)) return;
        var row = eco.Costs.Get(cls);
        if (eco.Refined(_side) < row.Cost) return;
        // headroom after adding its upkeep stays at least the margin of the capacity
        if (eco.Free(_side) - _plannedLoad - row.Maintenance < eco.Capacity(_side) * _p.HarvesterHeadroomMarginPercent / 100) return;
        // DEFAULT (lead, 2026-10-13, build time per m2-construction: ceil(cost / yards) x 4): the system with the most completed shipyards,
        // ties: fewest lane steps to the capital, then system name
        var capital = Capital();
        string? best = null; int bestSteps = int.MaxValue, bestYards = -1;
        foreach (var y in YardSystems(YardFamily.Shipyard)) // ordinal order
        {
            if (!CanOrderAt(y, YardFamily.Shipyard) || Destroyed(y)) continue;
            int steps = 0;
            if (capital != null && y != capital)
            {
                var path = _h.Fleets.FindPath(y, capital, UnratedSpeed);
                if (!path.Ok) continue;
                steps = path.Systems.Count - 1;
            }
            int yards = YardsDoneAt(y, YardFamily.Shipyard);
            if (yards > bestYards || (yards == bestYards && steps < bestSteps)) { bestYards = yards; bestSteps = steps; best = y; }
        }
        if (best == null) return;
        long seq = _h.Construction.SubmitBuild(_side, best, YardFamily.Shipyard, cls, 1, null);
        Note(seq, "Build", $"1 x {cls} at {best}");
        _busy.Add((best, YardFamily.Shipyard));
        _plannedLoad += row.Maintenance;
    }

    // ---- 3. escort strength: the fleet's ships other than harvesters, against REF (the scale of FleetModule.StrengthOf)
    private double EscortStr(Fleet f)
    {
        var units = new List<Praedra.Core.Battle.Combatant>();
        foreach (var id in f.Ships)
        {
            var o = _h.Economy.Find(id); var c = o == null ? null : _h.Fleets.ClassOf(o);
            if (o == null || c == null || IsHarvesterShip(o)) continue;
            double ratio = c.Hull > 0 ? Math.Min(1.0, (double)o.Hull / c.Hull) : 1.0;
            units.Add(_h.Fleets.Strength.Unit(c, ratio, o.Hits is { Count: > 1 } ? o.Hits[1] : 0));
        }
        return _h.Fleets.Strength.Of(units, RefUnits());
    }

    /// <summary>The escort strength asked: HarvesterEscortStrength, else E (the economic blockade threshold). rule: m5-harvester §6.3.</summary>
    private double EscortNeeded() => _p.HarvesterEscortStrength ?? _h.Fleets.Blockade.EconomicStrength(_h.Fleets.ShipClasses, _h.Fleets.Strength);

    /// <summary>The idle fleets that hold a harvester (the military review leaves them to this file). Sets the escort call-up point.</summary>
    private HashSet<int> PrepareHarvester(List<Fleet> idle)
    {
        var set = new HashSet<int>();
        _dsFleet = null; _dsWaitAt = null;
        if (!HarvesterOn) return set;
        foreach (var f in idle.OrderBy(f => f.Id))
            if (HasHarvesterShip(f)) { set.Add(f.Id); _dsFleet ??= f; }
        var d = _dsFleet;
        if (d?.At != null && Held(d.At) && KnownEnemyHq != null && KnownEnemyHq != d.At && EscortStr(d) < EscortNeeded()) _dsWaitAt = d.At;
        return set;
    }

    // At a system the side holds: the harvester fleet waits for its escort; with HQ known, strike fleets here join it (the escort travels in the
    // same fleet). Returns the strike fleets that stay free for the normal review.
    private List<Fleet> HarvesterHome(List<Fleet> dsHere, List<Fleet> strike)
    {
        var d = dsHere[0];
        string? hq = KnownEnemyHq;
        if (hq == null || Destroyed(hq)) return strike; // no HQ known: it stays home, the army plays as usual
        var rest = new List<Fleet>();
        double escort = EscortStr(d);
        var joining = new List<Fleet>();
        foreach (var f in strike) { if (Str(f) > 0) { joining.Add(f); escort += Str(f); } else rest.Add(f); }
        if (escort < EscortNeeded()) return strike; // not enough yet: nobody is taken from the army until it can make the escort
        foreach (var f in joining) Merge(f, d);
        Move(d, hq);
        return rest;
    }

    // At the rally system with the harvester waiting elsewhere: the strike fleets walk to it.
    private List<Fleet> HarvesterCall(string at, List<Fleet> strike)
    {
        if (_dsWaitAt == null || at == _dsWaitAt || at != (Rally() ?? Capital())) return strike;
        var rest = new List<Fleet>();
        foreach (var f in strike)
            if (Str(f) > 0 && Move(f, _dsWaitAt)) continue; else rest.Add(f);
        return rest;
    }

    // At a system the side does not hold. At the HQ with no enemy fleet left: Destroy System. Elsewhere: on to the HQ now known, or home.
    private void HarvesterAway(Fleet d)
    {
        string at = d.At!; string? hq = KnownEnemyHq;
        if (hq == at)
        {
            if (EnemyFleetsAt(at).Count == 0 && !_h.Harvester.IsDestroyed(at))
            {
                long seq = _h.Harvester.SubmitDestroySystem(_side, d.Id);
                Note(seq, "DestroySystem", $"fleet {d.Id} destroys {at}");
            }
            return; // enemy fleets left: the battle takes its course
        }
        if (hq != null && !Destroyed(hq) && Move(d, hq)) return; // the HQ moved: re-target
        var home = Capital();
        if (home != null) Move(d, home);
    }
}
