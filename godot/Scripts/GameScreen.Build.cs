using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Construction;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Building (user stories E1-E6): the yard queues of a system with progress and days left, Stop with a question that says what is lost, and
/// the build table: pick a kind (Facilities, Ships, Fighters, Troops), set how many, click a row. Each row shows refined cost, upkeep and
/// about how many days one takes, and says plainly when it cannot be built now (not researched, no free slot, no upkeep room). The core
/// still judges every order when the next day starts. rule: m2-construction §Build order, §Build time; m2-maintenance §Overdraft.
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

    // ================================================================== the queues (E2, E3, E5)

    void BuildQueues(string name)
    {
        var eco = S.Host.Economy;
        var fams = new[] { YardFamily.Shipyard, YardFamily.Construction, YardFamily.Training }.Where(f => HasYard(name, f)).ToList();
        if (fams.Count == 0) { Note("No yard here. A Construction yard builds facilities, a Shipyard ships and fighters, a Training facility troops. Build a Construction yard from a planet that has one."); return; }
        // idle yards first, in one quiet card: a row each with its button (an idle yard is a chance, not an alarm)
        var idle = fams.Where(f => S.Host.Construction.ProjectAt(name, f) is not { Units.Count: > 0 }).ToList();
        if (idle.Count > 0)
            using (Card(Ui.Panel, new Color(Ui.Warn, 0.45f)))
            {
                foreach (var fam in idle)
                {
                    var h = Ui.HBox(8);
                    h.AddChild(new Glyph(Glyph.Kind.Dot, Ui.Warn, 9));
                    h.AddChild(Ui.Label($"{FamilyName(fam)}: idle", "Strong", new Color("ffe7a8"), wrap: false));
                    h.AddChild(Ui.Grow());
                    string sec = fam switch { YardFamily.Shipyard => "Ships", YardFamily.Training => "Troops", _ => "Facilities" };
                    var b = Ui.Button("Choose what to build", () => { _sysTab = SysTab.Build; _buildSection = sec; _panelDirty = true; _panelTimer = 0; }, "Opens the build table for this yard");
                    b.CustomMinimumSize = new Vector2(0, 30);
                    h.AddChild(b);
                    Add(h);
                }
            }
        foreach (var fam in fams.Except(idle))
        {
            var p = S.Host.Construction.ProjectAt(name, fam);
            var cur = eco.Find(p.Units[0]);
            string curName = cur?.Class ?? "?";
            int cost = CostOf(curName);
            int left = BuildDaysAt(name, fam, cost - p.Progress);
            var all = p.AllUnits().Select(u => eco.Find(u)?.Class ?? "?").ToList();
            int totalLeft = BuildDaysAt(name, fam, all.Sum(CostOf) - p.Progress);
            using (Card())
            {
                var h = Ui.HBox(8);
                h.AddChild(Ui.Label(FamilyName(fam), "Strong", wrap: false));
                h.AddChild(Ui.Grow());
                var f2 = fam;
                var stop = Ui.Button("Stop...", () => AskStopBuild(name, f2), "Stops all work at this yard: asks first and says what is lost.", "DangerButton");
                stop.CustomMinimumSize = new Vector2(0, 28);
                h.AddChild(stop);
                Add(h);
                Line($"{FamilyName(fam)} is building {Counts(all)}", Ui.Text2, Ui.Small);
                Add(Ui.Bar(cost <= 0 ? 0 : (float)p.Progress / cost, Ui.You, 7));
                Stat($"{curName}: {p.Progress} of {cost}", left <= 0 ? "done next cycle" : $"about {left} {Plural(left, "day", "days")} left", null,
                    "Each yard adds one point per cycle while it has refined material; the numbers assume it does. Yards wait in line for refined.");
                if (all.Count > 1) Note($"The whole queue: about {totalLeft} days. New orders join the end of the queue.");
            }
        }
    }

    void AskStopBuild(string system, YardFamily fam)
    {
        var p = S.Host.Construction.ProjectAt(system, fam);
        if (p == null) return;
        var eco = S.Host.Economy;
        var units = p.AllUnits().Select(u => eco.Find(u)?.Class ?? "?").ToList();
        var cur = eco.Find(p.Units[0]);
        Confirm($"Stop the {FamilyName(fam).ToLowerInvariant()} at {system}?",
            $"These units are not finished and will be removed: {Counts(units)}. The refined already spent on the {cur?.Class ?? "current unit"} ({p.Progress} of {CostOf(cur?.Class ?? "")} points) is lost, nothing is paid back. Units already finished stay. It happens when the next day starts.",
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

    List<BuildRow> BuildRowsFor(string system, string section)
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
            if (!cons.IsResearched(Me, r.Name)) why = "not researched";
            else if (r.Category == ObjectCategory.Facility && FacilityKinds.TryParse(r.Name, out var kind) && eco.CheckPlace(system, kind) is { } place)
                why = place == "OnePerPlanet" ? "one per planet: there is one" : place == "NotOnRoute" ? "only on a route system" : "no free slot";
            else if (r.Name == Praedra.Core.Harvester.HarvesterModule.ShipClass && S.HasOwnHarvesterAt(system)) why = "one per system: there is one";
            else if ((long)r.Maintenance * _buildQty > cap - dealt) why = "no upkeep room";
            bool usable = why == null;
            if (usable && r.Cost > refined) why = "waits for refined";
            list.Add(new BuildRow(r, fam, usable, why, BuildDaysAt(system, fam, r.Cost)));
        }
        int Prio(CostRow r) { int i = Array.IndexOf(FacilityPriority, r.Name); return r.Category == ObjectCategory.Facility ? (i >= 0 ? i : 50) : 0; }
        return list.OrderBy(b => b.Usable ? 0 : 1).ThenBy(b => Prio(b.Row)).ThenBy(b => b.Row.Cost).ThenBy(b => b.Row.Name, StringComparer.Ordinal).ToList();
    }

    List<BuildRow> _shownBuildRows = new(); // the rows the Build tab shows now (read by the playtest)

    void BuildMenu(string system)
    {
        // kind chips, each saying which yard it needs
        var chips = Ui.HBox(4);
        var group = new ButtonGroup();
        foreach (var section in new[] { "Facilities", "Ships", "Fighters", "Troops" })
        {
            var fam = section switch { "Facilities" => YardFamily.Construction, "Troops" => YardFamily.Training, _ => YardFamily.Shipyard };
            string sec = section;
            var b = Ui.Chip(section, () => { _buildSection = sec; _panelDirty = true; _panelTimer = 0; },
                HasYard(system, fam) ? $"Built by the {FamilyName(fam).ToLowerInvariant()} here" : $"Needs a {FamilyName(fam).ToLowerInvariant()} on this planet", _buildSection == section, group);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (!HasYard(system, fam)) b.AddThemeColorOverride("font_color", Ui.Text3);
            chips.AddChild(b);
        }
        Add(chips);

        var famSel = _buildSection switch { "Facilities" => YardFamily.Construction, "Troops" => YardFamily.Training, _ => YardFamily.Shipyard };
        if (!HasYard(system, famSel))
        {
            using (Card())
                Line($"This planet has no {FamilyName(famSel).ToLowerInvariant()}. {(famSel == YardFamily.Construction ? "Build one from a planet that already has a construction yard." : "Build one under Facilities first.")}", Ui.Text2, Ui.Small);
            return;
        }
        if (S.Host.Construction.ProjectAt(system, famSel) is { Units.Count: > 0 })
            Note($"The {FamilyName(famSel).ToLowerInvariant()} is busy: a new order joins the end of its queue (see Overview).");

        // how many
        var qty = Ui.HBox(6);
        qty.AddChild(Ui.Label("How many", "Dim", wrap: false));
        qty.AddChild(Ui.Grow());
        qty.AddChild(Ui.Button("-", () => { _buildQty = Math.Max(1, _buildQty - 1); _panelDirty = true; _panelTimer = 0; }, "One fewer", "Chip"));
        qty.GetChild<Button>(qty.GetChildCount() - 1).CustomMinimumSize = new Vector2(32, 30);
        var ql = Ui.Label(_buildQty.ToString(), "Number", wrap: false);
        ql.CustomMinimumSize = new Vector2(28, 0); ql.HorizontalAlignment = HorizontalAlignment.Center;
        qty.AddChild(ql);
        qty.AddChild(Ui.Button("+", () => { _buildQty = Math.Min(20, _buildQty + 1); _panelDirty = true; _panelTimer = 0; }, "One more (up to 20)", "Chip"));
        qty.GetChild<Button>(qty.GetChildCount() - 1).CustomMinimumSize = new Vector2(32, 30);
        Add(qty);

        // the table
        var head = Ui.HBox(0);
        head.AddChild(Ui.Label(_buildSection.ToUpperInvariant(), "StatLabel", wrap: false));
        head.AddChild(Ui.Grow());
        foreach (var (h, w) in new[] { ("REFINED", 62), ("UPKEEP", 56), ("DAYS", 46) })
        {
            var l = Ui.Label(h, "StatLabel", wrap: false);
            l.CustomMinimumSize = new Vector2(w, 0); l.HorizontalAlignment = HorizontalAlignment.Right;
            head.AddChild(l);
        }
        head.AddChild(Ui.Space(0));
        head.GetChild<Control>(head.GetChildCount() - 1).CustomMinimumSize = new Vector2(8, 0);
        Add(head);

        var rows = BuildRowsFor(system, _buildSection);
        _shownBuildRows = rows;
        bool shownLocked = false;
        foreach (var b in rows)
        {
            if (!b.Usable && !shownLocked) { shownLocked = true; Add(Ui.Space(4)); Add(Ui.Label("Not possible here now", "StatLabel", wrap: false)); }
            BuildRowButton(system, b);
        }
        Note($"Click a row to order {_buildQty}. Days are for one unit with the yards here.");
    }

    void BuildRowButton(string system, BuildRow b)
    {
        var r = b.Row;
        var btn = Ui.Button(r.Name, () => SubmitBuild(system, b.Family, r, _buildQty),
            $"{r.Name}: costs {r.Cost} refined each and {r.Maintenance} upkeep each, about {b.Days} days per unit here once the refined is there." +
            (b.Why != null ? $"\n{char.ToUpperInvariant(b.Why[0])}{b.Why[1..]}." : "") + (ClassTip(r.Name) is string ct ? "\n" + ct : ""), "Flat", !b.Usable);
        btn.Alignment = HorizontalAlignment.Left;
        btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        btn.CustomMinimumSize = new Vector2(0, 34);
        var cols = Ui.HBox(0);
        cols.MouseFilter = MouseFilterEnum.Ignore;
        cols.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        cols.OffsetRight = -8;
        cols.AddChild(Ui.Grow());
        if (b.Why != null)
        {
            var why = Ui.Label(b.Why, null, b.Usable ? Ui.Warn : Ui.Text3, Ui.Tiny, wrap: false);
            why.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            cols.AddChild(why);
            cols.AddChild(Ui.Space(0));
            cols.GetChild<Control>(cols.GetChildCount() - 1).CustomMinimumSize = new Vector2(8, 0);
        }
        foreach (var (text, w) in new[] { (r.Cost.ToString(), 62), (r.Maintenance.ToString(), 56), ($"~{b.Days}", 46) })
        {
            var l = Ui.Label(text, null, b.Usable ? Ui.Text : Ui.Text3, Ui.Small, wrap: false);
            l.AddThemeFontOverride("font", Ui.Num);
            l.CustomMinimumSize = new Vector2(w, 0); l.HorizontalAlignment = HorizontalAlignment.Right;
            l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            cols.AddChild(l);
        }
        btn.AddChild(cols);
        Add(btn);
    }

    /// <summary>What a ship class is, for its tooltip: hull, shields, fighter and troop room, and the special kit (interdictor, bombardment).</summary>
    string ClassTip(string cls)
    {
        if (!Fm.ShipClasses.TryGetValue(cls, out var sc)) return null;
        var parts = new List<string> { $"Hull {sc.Hull}", $"shields {sc.ShieldStrength}" };
        if (sc.FighterCapacity > 0) parts.Add($"carries {sc.FighterCapacity} fighter squadrons");
        if (sc.TroopCapacity > 0) parts.Add($"carries {sc.TroopCapacity} regiments");
        if (sc.Bombardment > 0) parts.Add($"bombardment {sc.Bombardment}");
        if (sc.GravityWellProjector > 0) parts.Add("interdictor: pulls enemy fleets out of hyperspace");
        return string.Join(", ", parts) + ".";
    }

    void SubmitBuild(string system, YardFamily fam, CostRow row, int count)
    {
        int n = Math.Max(1, count);
        string dest = row.Category == ObjectCategory.Facility ? system : null;
        long seq = S.Host.Construction.SubmitBuild(Me, system, fam, row.Name, n, dest);
        string what = $"build {n} x {row.Name} at {system}";
        S.Track(seq, char.ToUpper(what[0]) + what[1..]);
        S.Say($"Order sent: {what}.", MsgKind.Info, system);
        AddLogItem(S.Messages[^1], false);
        Toast($"Order sent: {what}. It starts next day.");
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }
}
