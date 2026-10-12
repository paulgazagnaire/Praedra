using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;

namespace Rebellion.Core.Repair;

/// <summary>
/// Strategic repair, day slot 6.8 (after autoscrap 6.7). Holds data and a hook only, never game state (hull and hits live on the economy
/// objects). Draws ZERO random numbers, costs nothing. Attach after the fleets, economy and construction.
/// rule: m4-retreat-damage-repair §5 Strategic repair (Traced-medium 0x501c50; location and cadence DEFAULT).
/// </summary>
public sealed class RepairModule
{
    private readonly Game _game;
    private readonly EconomyModule _eco;
    private readonly FleetModule _fleets;

    public RepairRules Rules { get; }

    /// <summary>
    /// Has this ship (object id) taken part in a battle in this Step? A repaired ship must not have (DEFAULT, so a fleet cannot fight and heal
    /// on the same day). The battle package wires it; until then nobody fights. rule: m4-retreat-damage-repair §5 (third condition), RT21.
    /// </summary>
    public Func<int, bool> FoughtThisStep { get; set; } = _ => false;

    private RepairModule(Game game, EconomyModule eco, FleetModule fleets, RepairRules rules)
    { _game = game; _eco = eco; _fleets = fleets; Rules = rules; }

    /// <summary>Register slot 6.8. rule: m4-retreat-damage-repair §5 (new Day Step slot 6.8, after autoscrap 6.7).</summary>
    public static RepairModule Attach(Game game, EconomyModule eco, FleetModule fleets, RepairRules rules)
    {
        var m = new RepairModule(game, eco, fleets, rules);
        game.RegisterHook("6.8", m.Repair);
        return m;
    }

    private EconomyState E => _game.State.Economy;

    /// <summary>A completed shipyard (production family 0x28, Orbital or Advanced) on a system the side holds. rule: m4-retreat-damage-repair §5 (where).</summary>
    public bool HasOwnYard(string system, Side side)
    {
        if (_fleets.OwnerOf(system) != side) return false;
        return E.Objects.Values.Any(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system
            && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == YardFamily.Shipyard);
    }

    // rule: m4-retreat-damage-repair §5: side Alliance then Empire, ship id ascending
    private void Repair(StepContext ctx)
    {
        int t = ctx.Day + 1;
        if (t % Rules.RepairCadenceDays != 0) return; // rule: m4-retreat-damage-repair §5 (RepairCadenceDays)

        // ship id -> the system of its fleet, for ships in a fleet at a system (not in transit)
        var at = new Dictionary<int, string>();
        foreach (var f in _game.State.Fleets.Values)
            if (f.At != null && f.Transit == null)
                foreach (var id in f.Ships) at[id] = f.At;

        var yardCache = new Dictionary<(string, Side), bool>();
        foreach (var side in new[] { Side.Alliance, Side.Empire })
        {
            foreach (var ship in E.Objects.Values) // id-sorted registry
            {
                if (ship.Category != ObjectCategory.CapitalShip || ship.Side != side || ship.Hits == null) continue; // squadrons and non-ships: skipped
                if (!at.TryGetValue(ship.Id, out var sys)) continue;
                var cls = _fleets.ClassOf(ship);
                if (cls == null) continue;
                int max = cls.Hull;
                if (ship.Hull >= max && ship.Hits.All(h => h == 0)) continue; // whole: skipped
                if (Rules.RepairNeedsYard)
                {
                    if (!yardCache.TryGetValue((sys, side), out var has)) yardCache[(sys, side)] = has = HasOwnYard(sys, side);
                    if (!has) continue;
                }
                if (FoughtThisStep(ship.Id)) continue;

                // hull = min(hull_max, hull + ceil(hull_max / divisor)); each counter above 0 falls by RepairHitsPerCall
                int step = (max + Rules.RepairHullDivisor - 1) / Rules.RepairHullDivisor;
                ship.Hull = Math.Min(max, ship.Hull + step);
                for (int i = 0; i < ship.Hits.Count; i++) ship.Hits[i] = Math.Max(0, ship.Hits[i] - Rules.RepairHitsPerCall);
                int id = ship.Id, hull = ship.Hull, left = ship.Hits.Sum();
                ctx.Emit((i, d) => new ShipRepaired(i, d, id, hull, left));
            }
        }
    }
}
