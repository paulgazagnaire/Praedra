using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Research;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Building (user stories E1-E6): the yard queues of a system with progress and days left, Stop with a question that says what is lost, and
/// the build table: pick a kind (Facilities, Ships, Fighters, Troops), set how many, click a row. Each row shows refined cost, upkeep and
/// about how many days one takes, and says plainly when it cannot be built now (not researched, no free slot, no upkeep room). The core
/// still judges every order when the clock moves. rule: m2-construction §Build order, §Build time; m2-maintenance §Overdraft.
/// </summary>
public partial class GameScreen
{
    int _buildQty = 1;
    string _buildSection = "Ships";

    static string FamilyName(YardFamily f) => f switch { YardFamily.Shipyard => "Shipyard", YardFamily.Training => "Training facility", _ => "Construction yard" };

    static readonly string[] FacilityPriority = { "Mine", "Refinery", "Construction Yard", "Orbital Shipyard", "Training Facility" };

    static string SectionOf(ObjectCategory c) => c switch
    {
        ObjectCategory.Facility => "Facilities",
        ObjectCategory.CapitalShip => "Ships",
        ObjectCategory.Fighter => "Fighters",
        _ => "Troops",
    };

    // ================================================================== build time

    /// <summary>Yards of one family here: (standard, advanced) counts.</summary>
    (int Std, int Adv) YardsOf(string system, YardFamily fam)
    {
        var ys = S.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system
            && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == fam).ToList();
        int adv = ys.Count(y => FacilityKinds.TryParse(y.Class, out var k) && Yards.IsAdvanced(k));
        return (ys.Count - adv, adv);
    }

    /// <summary>About how many days the yards of a family here need for <paramref name="points"/> cost points: each yard adds one point per
    /// cycle (standard and advanced cycles differ), if the refined material is there. rule: m2-construction §Build time.</summary>
    int BuildDaysAt(string system, YardFamily fam, int points)
    {
        var p = S.Host.Construction.Params;
        var (std, adv) = YardsOf(system, fam);
        long perDay = (long)std * p.AdvancedCycleDays + (long)adv * p.StandardCycleDays; // scaled by std*adv cycle days
        if (perDay <= 0) return 0;
        return (int)(((long)Math.Max(0, points) * p.StandardCycleDays * p.AdvancedCycleDays + perDay - 1) / perDay);
    }

    int CostOf(string cls) => S.Host.Economy.Costs.TryGet(cls, out var r) ? r.Cost : 0;

    /// <summary>Days left on the unit being built now by a family at a system.</summary>
    internal int DaysLeftOf(string system, YardFamily fam)
    {
        var p = S.Host.Construction.ProjectAt(system, fam);
        if (p == null || p.Units.Count == 0) return 0;
        var cur = S.Host.Economy.Find(p.Units[0]);
        return BuildDaysAt(system, fam, CostOf(cur?.Class ?? "") - p.Progress);
    }

    // ================================================================== stopping a yard (E5)

    void AskStopBuild(string system, YardFamily fam)
    {
        var p = S.Host.Construction.ProjectAt(system, fam);
        if (p == null) return;
        var eco = S.Host.Economy;
        var units = p.AllUnits().Select(u => eco.Find(u)?.Class ?? "?").ToList();
        var cur = eco.Find(p.Units[0]);
        Confirm($"Stop the {FamilyName(fam).ToLowerInvariant()} at {system}?",
            $"These units are not finished and will be removed: {Counts(units)}. The refined already spent on the {cur?.Class ?? "current unit"} ({p.Progress} of {CostOf(cur?.Class ?? "")} points) is lost, nothing is paid back. Units already finished stay. It happens as soon as the clock moves.",
            "Yes, stop it", () => StopBuild(system, fam));
    }

    void StopBuild(string system, YardFamily fam)
    {
        long seq = S.Host.Construction.SubmitStop(Me, system, fam);
        S.Track(seq, $"Stop building at {system}");
        Sent($"stop the {FamilyName(fam).ToLowerInvariant()} work at {system}", system);
    }

    // ================================================================== the build table (E1)

    sealed record BuildRow(CostRow Row, YardFamily Family, bool Usable, string Why, int Days);
    const string NotResearched = "not researched";

    /// <param name="yardAt">The system whose yards build it, when it is not <paramref name="system"/> (a facility delivered from elsewhere).</param>
    /// <param name="onTheWay">Days the delivery travels, added to the build days.</param>
    List<BuildRow> BuildRowsFor(string system, string section, string yardAt = null, int onTheWay = 0)
    {
        var eco = S.Host.Economy; var cons = S.Host.Construction;
        long refined = eco.Refined(Me);
        int cap = eco.Capacity(Me), dealt = Math.Min(eco.Load(Me), cap);
        var rows = eco.Costs.Rows.Where(r => (Me == Side.Hand ? r.Hand : r.Collective) && r.Cost > 0 && SectionOf(r.Category) == section).ToList();
        var list = new List<BuildRow>();
        foreach (var r in rows)
        {
            var fam = Yards.BuiltBy(r.Category);
            string why = null;
            if (!cons.IsResearched(Me, r.Name)) why = NotResearched;
            else if (r.Category == ObjectCategory.Facility && FacilityKinds.TryParse(r.Name, out var kind) && eco.CheckPlace(system, kind) is { } place)
                why = place == "OnePerPlanet" ? "one per planet: there is one" : place == "NotOnRoute" ? "only on a lane" : "no free slot";
            else if (r.Name == Praedra.Core.Harvester.HarvesterModule.ShipClass && S.HasOwnHarvesterAt(system)) why = "one per system: there is one";
            else if ((long)r.Maintenance * _buildQty > cap - dealt) why = "no upkeep room";
            bool usable = why == null;
            if (usable && r.Cost > refined) why = "waits for refined";
            list.Add(new BuildRow(r, fam, usable, why, BuildDaysAt(yardAt ?? system, fam, r.Cost) + onTheWay));
        }
        int Prio(CostRow r) { int i = Array.IndexOf(FacilityPriority, r.Name); return r.Category == ObjectCategory.Facility ? (i >= 0 ? i : 50) : 0; }
        return list.OrderBy(b => b.Usable ? 0 : 1).ThenBy(b => Prio(b.Row)).ThenBy(b => b.Row.Cost).ThenBy(b => b.Row.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>A construction yard that can build for a planet with none: where it is, the trip in days, and about how many days its
    /// work in hand (the project and orders waiting for the clock) keeps it busy.</summary>
    sealed record Supplier(string Yard, int Trip, int Busy);

    /// <summary>
    /// The construction yards that can build for a planet of yours with none: your planets with a finished construction yard that can
    /// deliver there, soonest first (busy days + trip, then by name). rule: m2-construction §Build order (a facility's destination is any
    /// system the side controls), §Completion and delivery. Paul 2026-11-19: "we're missing a way to build infrastructure for other
    /// systems"; he picks the yard ("Let me pick"), the soonest is picked first. Worked out once a day per planet (and when orders change).
    /// </summary>
    List<Supplier> SuppliersFor(string dest)
    {
        var cons = S.Host.Construction; var eco = S.Host.Economy;
        // a cancel and a new order on the same day keep the count: at worst one stale busy figure until the next day, which is fine
        var stamp = (S.Game.Day, S.Game.Queue.Count);
        if (_suppliers.TryGetValue(dest, out var hit) && hit.Stamp == stamp) return hit.List;
        int speed = cons.Params.UnratedDeliverySpeed;
        int Cost(string cls) => eco.Costs.TryGet(cls, out var r) ? r.Cost : 0;
        var list = new List<Supplier>();
        foreach (var sys in eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System != null && o.System != dest)
                     .Select(o => o.System).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!HasYard(sys, YardFamily.Construction) || S.OwnerOf(sys) != Me || eco.IsDestroyed(sys)) continue;
            var path = S.Host.Fleets.FindPath(sys, dest, speed);
            if (!path.Ok) continue;
            // the points still to build: the project's units (less the current one's progress), its queue, then the orders not yet judged
            long points = 0;
            if (cons.ProjectAt(sys, YardFamily.Construction) is { } p)
            {
                foreach (var id in p.Units.Concat(p.Next?.SelectMany(q => q.Units) ?? Enumerable.Empty<int>()))
                    if (eco.Find(id) is { } o) points += Cost(o.Class);
                points = Math.Max(0, points - p.Progress);
            }
            foreach (var o in QueuedBuilds(sys, YardFamily.Construction)) points += (long)Cost(o.Class) * o.Count;
            list.Add(new Supplier(sys, path.TotalTicks, BuildDaysAt(sys, YardFamily.Construction, (int)Math.Min(int.MaxValue, points))));
        }
        list = list.OrderBy(x => x.Busy + x.Trip).ThenBy(x => x.Yard, StringComparer.Ordinal).ToList();
        _suppliers[dest] = (stamp, list);
        return list;
    }

    readonly Dictionary<string, ((int, int) Stamp, List<Supplier> List)> _suppliers = new(StringComparer.Ordinal);
    /// <summary>The yard the player picked to build for a planet (by planet); the soonest when none or when it can no longer.</summary>
    readonly Dictionary<string, string> _supplierPick = new(StringComparer.Ordinal);

    Supplier PickedSupplier(string dest)
    {
        var all = SuppliersFor(dest);
        return _supplierPick.TryGetValue(dest, out var y) && all.Find(x => x.Yard == y) is { } picked ? picked : all.FirstOrDefault();
    }

    List<BuildRow> _shownBuildRows = new(); // the rows the Build tab shows now (read by the playtest)

    /// <summary>What a ship class is, for its tooltip: hull, shields, fighter and troop room, and the special kit (interdictor, bombardment).</summary>
    string ClassTip(string cls)
    {
        if (!Fm.ShipClasses.TryGetValue(cls, out var sc)) return null;
        var parts = new List<string> { $"Hull {sc.Hull}", $"shields {sc.ShieldStrength}" };
        if (sc.FighterCapacity > 0) parts.Add($"carries {sc.FighterCapacity} fighter squadrons");
        if (sc.TroopCapacity > 0) parts.Add($"carries {sc.TroopCapacity} regiments");
        if (sc.Bombardment > 0) parts.Add($"bombardment {sc.Bombardment}");
        if (sc.GravityWellProjector > 0) parts.Add("interdictor: pulls enemy fleets passing its system out of the deep, on a lane or off it");
        return string.Join(", ", parts) + ".";
    }

    /// <summary>Where research stands in the track this build list draws on: the level, and the next thing it unlocks.
    /// rule: m5-research §Facility income, §Mission income, §The level check (points come from finished yards of that kind and from Research missions).</summary>
    /// <summary>The research level and what it unlocks next; <paramref name="greyNote"/>: the build tiles above include grey ones, which carry
    /// no "not researched" line of their own (so four rows fit the Infrastructure window), so this line says what grey means.</summary>
    Label ResearchLine(ResearchKind kind, bool greyNote = false)
    {
        var r = S.Host.Research;
        string track = kind switch { ResearchKind.Ship => "ship design", ResearchKind.Troop => "troop training", _ => "facility design" };
        string text = (greyNote ? "A grey card with no note is not researched yet. " : "") + (r.NextRung(Me, kind) is { } next
            ? $"Research ({track}): level {r.Level(Me, kind)}. Next: {next.Class}, {r.Points(Me, kind)} of {next.Cost} points."
            : $"Research ({track}): level {r.Level(Me, kind)}, everything researched.");
        var l = Add(Ui.Label(text, "Hint"));
        l.TooltipText = Ui.Wrap("Every finished yard of this kind adds a point now and then; a Research mission by a skilled leader adds far more. Each level unlocks the next item in the list.");
        l.MouseFilter = MouseFilterEnum.Stop;
        return l;
    }

    /// <param name="deliverTo">For a facility built by the yards of <paramref name="system"/> for another of your systems (null: here).</param>
    void SubmitBuild(string system, YardFamily fam, CostRow row, int count, string deliverTo = null)
    {
        int n = Math.Max(1, count);
        string dest = row.Category == ObjectCategory.Facility ? deliverTo ?? system : null;
        long seq = S.Host.Construction.SubmitBuild(Me, system, fam, row.Name, n, dest);
        string what = dest != null && dest != system ? $"build {n} x {row.Name} for {dest}, built at {system}" : $"build {n} x {row.Name} at {system}";
        S.Track(seq, char.ToUpper(what[0]) + what[1..]);
        S.Say($"Order sent: {what}.", MsgKind.Info, system);
        AddLogItem(S.Messages[^1], false);
        Toast($"Order given: {what}.");
        OrdersChanged();
        RefreshTop();
    }

    /// <summary>A queued build order makes its yard busy (impeccable critique 2026-11-14), so an order or a Cancel changes what Needs you
    /// lists and the Yards layer's halos at once, not only when the day moves.</summary>
    void OrdersChanged()
    {
        _panelDirty = true; _panelTimer = 0;
        _attentionDirty = true;
        _view.QueueRedraw();
    }
}
