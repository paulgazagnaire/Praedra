using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;

namespace Praedra.Core.Repair;

/// <summary>
/// Strategic repair, day slot 6.8 (after autoscrap 6.7): Classic's per-unit repair timer (rule event 0x380, capital ship 0x501c50, squadron
/// 0x503530). A damaged unit that is not moving has a repair turn every 30 days, every 2 days with FastRepair (at a system of its side with a usable
/// own shipyard that is not suspended by a blockade or an uprising). Holds data and hooks only; the timer lives on the economy objects
/// (<see cref="EconObject.RepairMode"/>, <see cref="EconObject.RepairDue"/>). Free. The damage-control rolls use the game generator.
/// Attach after the fleets, economy and construction. rule: m4-retreat-damage-repair §5 Strategic repair (Traced, B3 §15).
/// </summary>
public sealed class RepairModule
{
    private readonly Game _game;
    private readonly EconomyModule _eco;
    private readonly FleetModule _fleets;

    public RepairRules Rules { get; }

    /// <summary>
    /// True when the system's yards are suspended (blockade or uprising): FastRepair is then off. The host wires the construction module's test; until
    /// then nothing is suspended. rule: m4-retreat-damage-repair §5 (FastRepair), Open question 8.
    /// </summary>
    public Func<string, bool> YardSuspended { get; set; } = _ => false;

    private RepairModule(Game game, EconomyModule eco, FleetModule fleets, RepairRules rules)
    { _game = game; _eco = eco; _fleets = fleets; Rules = rules; }

    /// <summary>Register slot 6.8. rule: m4-retreat-damage-repair §5 (Day Step slot 6.8, after autoscrap 6.7).</summary>
    public static RepairModule Attach(Game game, EconomyModule eco, FleetModule fleets, RepairRules rules)
    {
        var m = new RepairModule(game, eco, fleets, rules);
        game.RegisterHook("6.8", m.Repair);
        return m;
    }

    private EconomyState E => _game.State.Economy;

    /// <summary>A completed shipyard (production family 0x28, Orbital or Advanced) on a system the side holds. rule: m4-retreat-damage-repair §5 (FastRepair).</summary>
    public bool HasOwnYard(string system, Side side)
    {
        if (_fleets.OwnerOf(system) != side) return false;
        return E.Objects.Values.Any(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system
            && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == YardFamily.Shipyard);
    }

    /// <summary>
    /// The flag CombatUnitFastRepair of a system for a side: its own system with a usable own shipyard-family facility that is not suspended.
    /// rule: m4-retreat-damage-repair §5.
    /// </summary>
    public bool FastRepair(string system, Side side) => HasOwnYard(system, side) && !YardSuspended(system);

    /// <summary>Repair period in days. rule: m4-retreat-damage-repair §5 (GNPRTB 2052 / 2053, squadrons 4608 / 4609).</summary>
    public int Period(bool capital, bool fast) => capital
        ? (fast ? Rules.FastRepairPeriodDays : Rules.RepairPeriodDays)
        : (fast ? Rules.SquadronFastRepairPeriodDays : Rules.SquadronRepairPeriodDays);

    // rule: m4-retreat-damage-repair §5: side Alliance then Empire, object id ascending; per unit the damage-control roll, then the pick
    private void Repair(StepContext ctx)
    {
        int k = ctx.Day, t = ctx.Day + 1;

        // where each unit stands still: ships of fleets at a system (not in transit), squadrons aboard them or in such a fleet, based squadrons
        var at = new Dictionary<int, string>(); // lookup only
        foreach (var f in _game.State.Fleets.Values)
            if (f.At != null && f.Transit == null)
            {
                foreach (var id in f.Ships) at[id] = f.At;
                foreach (var id in f.Squadrons) at[id] = f.At;
            }
        var moving = new HashSet<int>(); // lookup only: units in a travelling fleet
        foreach (var f in _game.State.Fleets.Values)
            if (f.At == null || f.Transit != null) { foreach (var id in f.Ships) moving.Add(id); foreach (var id in f.Squadrons) moving.Add(id); }

        var fastCache = new Dictionary<(string, Side), bool>(); // lookup only
        foreach (var side in new[] { Side.Alliance, Side.Empire })
        {
            foreach (var o in E.Objects.Values.ToList()) // id-sorted registry
            {
                bool capital = o.Category == ObjectCategory.CapitalShip;
                if ((!capital && o.Category != ObjectCategory.Fighter) || o.Side != side || !o.Completed) continue;
                var cls = _fleets.ClassOf(o);
                if (cls == null) continue;
                bool damaged = capital ? (o.Hits != null && (o.Hull < cls.Hull || o.Hits.Any(h => h > 0))) : o.CraftLost > 0;
                string? sys = Place(o, at, moving);
                bool fast = false;
                if (damaged && sys != null)
                {
                    if (!fastCache.TryGetValue((sys, side), out fast)) fastCache[(sys, side)] = fast = FastRepair(sys, side);
                }
                if (!damaged || sys == null || (Rules.RepairNeedsYard && !fast)) { o.RepairMode = 0; o.RepairDue = 0; continue; }
                int mode = fast ? 2 : 1;
                // any change of the period or of the damaged / usable state restarts the timer from that day (DEFAULT: the state seen in Step K is day K's)
                if (o.RepairMode != mode) { o.RepairMode = mode; o.RepairDue = k + Period(capital, fast); }
                if (t < o.RepairDue) continue;
                if (capital) ShipTurn(ctx, t, o, cls.Hull, cls.DamageControl); else SquadronTurn(ctx, t, o);
                bool still = capital ? (o.Hull < cls.Hull || o.Hits!.Any(h => h > 0)) : o.CraftLost > 0;
                if (still) o.RepairDue = t + Period(capital, fast);
                else { o.RepairMode = 0; o.RepairDue = 0; }
            }
        }
    }

    /// <summary>The system a unit stands at without moving, or null (moving, in transit, or nowhere). rule: m4-retreat-damage-repair §5 (usable).</summary>
    private string? Place(EconObject o, Dictionary<int, string> at, HashSet<int> moving)
    {
        if (moving.Contains(o.Id)) return null;
        if (at.TryGetValue(o.Id, out var s)) return s;
        if (o.Category == ObjectCategory.Fighter)
        {
            if (o.AboardShip != 0) return moving.Contains(o.AboardShip) ? null : at.TryGetValue(o.AboardShip, out var c) ? c : null;
            return o.System;
        }
        return null;
    }

    /// <summary>
    /// A capital ship's turn (0x501c50): hull damage falls by trunc(class hull / 10); then, with any hits, rand(0..99) &lt; damage_control fixes one hit,
    /// picked by rand(0..total - 1) (no draw when total is 1) walking shield gen, weapons, tractor, sub-light, primary and backup hyperdrive.
    /// rule: m4-retreat-damage-repair §5.
    /// </summary>
    private void ShipTurn(StepContext ctx, int t, EconObject ship, int max, int damageControl)
    {
        ship.Hull = Math.Min(max, ship.Hull + max / Rules.RepairHullDivisor);
        var hits = ship.Hits!;
        int total = hits.Sum();
        if (total > 0 && ctx.Rng.NextInt(0, 99) < damageControl)
        {
            int r = total == 1 ? 0 : ctx.Rng.NextInt(0, total - 1);
            for (int i = 0; i < hits.Count; i++)
            {
                if (r < hits[i]) { hits[i]--; break; }
                r -= hits[i];
            }
        }
        int id = ship.Id, hull = ship.Hull, left = hits.Sum();
        ctx.Emit((i, _) => new ShipRepaired(i, t, id, hull, left));
    }

    /// <summary>A squadron's turn (0x503530): one craft back. rule: m4-retreat-damage-repair §5.</summary>
    private void SquadronTurn(StepContext ctx, int t, EconObject sq)
    {
        sq.CraftLost = Math.Max(0, sq.CraftLost - 1);
        int id = sq.Id, lost = sq.CraftLost;
        ctx.Emit((i, _) => new SquadronRepaired(i, t, id, lost));
    }
}
