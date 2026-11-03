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
/// A system's Infrastructure window (Paul, 2026-11-03, with Classic's system window as the example): slots as dots, one card per yard with what
/// it builds and a progress bar, what can be built as picture tiles (click one to order it), and the buildings as stacked tiles (right-click
/// to scrap). The same orders as the drawer's build table (user stories E1-E6); the core judges them when the next day starts.
/// rule: m2-construction §Build order, §Build time; m2-maintenance §Overdraft, §Scrapping and refunds.
/// </summary>
public partial class GameScreen
{
    static string SectionOf(YardFamily f) => f switch { YardFamily.Shipyard => "Ships", YardFamily.Training => "Troops", _ => "Facilities" };

    void BuildTab(string n)
    {
        var eco = S.Host.Economy;
        bool hand = Me == Side.Hand;

        // what stops the planet, first
        int mines = eco.OfKindAt(n, FacilityKind.Mine), refs = eco.OfKindAt(n, FacilityKind.Refinery);
        int smug = mines + refs > 0 ? eco.DiversionPercent(n, Me) : 0;
        if (smug > 0) Line($"Smugglers may take a cycle's output {smug}% of the time. More support, or forces here, scare them off.", Ui.Warn, Ui.Small);
        if (S.Game.State.Blockades.ContainsKey(n)) Line("Blockaded: the mines and yards here stop until the enemy fleet is driven off.", Bad, Ui.Small);
        if (S.Host.Support.IsUprising(n)) Line("Uprising: the mines and yards here stop until it ends.", Bad, Ui.Small);

        // the yards, a card each
        var fams = YardFamiliesAt(n).ToList();
        Section("Yards", fams.Count == 0 ? null : fams.Count.ToString());
        if (fams.Count == 0) Line("No yard here. A Construction yard builds facilities, a Shipyard ships and fighters, a Training facility troops.", Ui.Text3, Ui.Small);
        else
        {
            var yards = new HFlowContainer();
            yards.AddThemeConstantOverride("h_separation", 8); yards.AddThemeConstantOverride("v_separation", 8);
            foreach (var fam in fams) yards.AddChild(YardCard(n, fam));
            Add(yards);
        }

        // what can be built here, as tiles
        Section("Build");
        if (_buildSection is "Ships" or "Fighters" && !HasYard(n, YardFamily.Shipyard) || _buildSection == "Troops" && !HasYard(n, YardFamily.Training)) _buildSection = "Facilities";
        var chips = new HFlowContainer();   // wraps rather than widening the column
        chips.AddThemeConstantOverride("h_separation", 2); chips.AddThemeConstantOverride("v_separation", 4);
        Container top = chips;
        var group = new ButtonGroup();
        foreach (var section in new[] { "Facilities", "Ships", "Fighters", "Troops" })
        {
            var fam = section switch { "Facilities" => YardFamily.Construction, "Troops" => YardFamily.Training, _ => YardFamily.Shipyard };
            string sec = section;
            var b = Ui.Chip(section, () => { _buildSection = sec; MgRefreshSoon(); },
                HasYard(n, fam) ? $"Built by the {FamilyName(fam).ToLowerInvariant()} here" : $"Needs a {FamilyName(fam).ToLowerInvariant()} on this planet", _buildSection == section, group);
            b.FocusMode = FocusModeEnum.None;
            if (!HasYard(n, fam)) b.AddThemeColorOverride("font_color", Ui.Text3);
            top.AddChild(b);
        }
        Add(chips);
        top = Ui.HBox(4);
        top.AddChild(Ui.Label("How many", null, Ui.Text3, Ui.Tiny, wrap: false));
        var less = Ui.Button("-", () => { _buildQty = Math.Max(1, _buildQty - 1); MgRefreshSoon(); }, "One fewer", "Chip");
        less.CustomMinimumSize = new Vector2(28, 26);
        top.AddChild(less);
        var ql = Ui.Label(_buildQty.ToString(), null, Ui.Text, Ui.Small, wrap: false);
        ql.AddThemeFontOverride("font", Ui.Num);
        ql.CustomMinimumSize = new Vector2(22, 0); ql.HorizontalAlignment = HorizontalAlignment.Center;
        top.AddChild(ql);
        var more = Ui.Button("+", () => { _buildQty = Math.Min(20, _buildQty + 1); MgRefreshSoon(); }, "One more (up to 20)", "Chip");
        more.CustomMinimumSize = new Vector2(28, 26);
        top.AddChild(more);
        Add(top);

        var famSel = _buildSection switch { "Facilities" => YardFamily.Construction, "Troops" => YardFamily.Training, _ => YardFamily.Shipyard };
        if (!HasYard(n, famSel))
            Line($"This planet has no {FamilyName(famSel).ToLowerInvariant()}. {(famSel == YardFamily.Construction ? "Build one from a planet that already has a construction yard." : "Build one under Facilities first.")}", Ui.Text2, Ui.Small);
        else
        {
            var rows = BuildRowsFor(n, _buildSection);
            _shownBuildRows = rows;
            var grid = new HFlowContainer();
            grid.AddThemeConstantOverride("h_separation", 6); grid.AddThemeConstantOverride("v_separation", 6);
            foreach (var b in rows) grid.AddChild(OptionTile(n, b, hand));
            Add(grid);
        }

        ResearchLine(_buildSection switch { "Facilities" => ResearchKind.Facility, "Troops" => ResearchKind.Troop, _ => ResearchKind.Ship });
        // the planet: slots, what it gives, its buildings
        Section("Planet");
        // slots and what the planet gives
        var slots = eco.SlotsOf(n);
        var strip = Ui.VBox(8);
        strip.AddChild(SlotStrip("Energy slots", eco.EnergyUsed(n), slots.Energy, "Every building takes one energy slot."));
        strip.AddChild(SlotStrip("Raw material slots", eco.RawUsed(n), eco.EffectiveRaw(n), "A mine also needs a raw material slot. A planet never has more usable raw slots than energy slots."));
        Add(strip);
        var gives = Line($"{Ui.Count(mines, "mine", "mines")}: about +{RawPerMonth(n)} raw · {Ui.Count(refs, "refinery", "refineries")}: about +{RefinedPerMonth(n)} refined, every 30 days", Ui.Text2, Ui.Small);
        gives.TooltipText = Ui.Wrap("Each mine digs one raw material every cycle; each refinery turns one raw into one refined. The cycle is longer when support is low. "
            + $"Every mine and refinery also adds {eco.Params.CapacityPerFacility} to your upkeep room (with as many of the other kind).");
        gives.MouseFilter = MouseFilterEnum.Stop;
        // the buildings, stacked by kind
        var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == n).ToList();
        Section("Buildings", here.Count(o => o.Completed).ToString());
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
        UnitTiles(flow, here.Where(o => o.Completed).OrderBy(o => Array.IndexOf(FacilityPriority, o.Class) is int i && i >= 0 ? i : 50).ThenBy(o => o.Class, StringComparer.Ordinal)
            .Select(o => ((Drag?)null, o, (string)null)), n);
        foreach (var g in here.Where(o => !o.Completed).GroupBy(o => o.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
            flow.AddChild(Tile(UnitIcon.ForFacility(g.Key), hand, 1, g.Key, "being built", g.Count(), Ui.You, ghost: true));
        if (here.Count == 0) flow.AddChild(EmptySlot("None"));
        Add(flow);
        if (Me == Side.Collective && n == S.MyHqSystem) HqSection(n);
    }

    Control SlotStrip(string label, int used, int total, string tip)
    {
        var v = Ui.VBox(3);
        v.MouseFilter = MouseFilterEnum.Stop;
        v.TooltipText = Ui.Wrap($"{label}: {used} used of {total}, {Math.Max(0, total - used)} free. {tip}");
        v.AddChild(Ui.Label($"{label}  {used} of {total}", null, Ui.Text2, Ui.Tiny, wrap: false));
        v.AddChild(SlotDots.Of(used, total, Ui.You));
        return v;
    }

    /// <summary>A yard as a card: its picture, what it is building with a progress bar and the days left, or "idle" and a button to choose.</summary>
    Control YardCard(string n, YardFamily fam)
    {
        var eco = S.Host.Economy;
        var p = S.Host.Construction.ProjectAt(n, fam);
        bool idle = p is not { Units.Count: > 0 };
        var card = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(150, 0) };
        card.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel2, 8, idle ? new Color(Ui.Warn, 0.5f) : new Color(Ui.You, 0.3f), 1, 8, 8));
        card.SetMeta(UiTest.FullName, $"Yard {n} {fam}");
        card.MouseDefaultCursorShape = CursorShape.PointingHand;
        string sec = SectionOf(fam);
        card.GuiInput += e => { if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) { _buildSection = sec; MgRefreshSoon(); } };
        var v = Ui.VBox(3);
        v.MouseFilter = MouseFilterEnum.Ignore;
        card.AddChild(v);
        var h = Ui.HBox(8);
        h.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(h);
        var kind = fam switch { YardFamily.Shipyard => UnitIcon.Kind.Shipyard, YardFamily.Training => UnitIcon.Kind.Training, _ => UnitIcon.Kind.Construction };
        var icon = UnitIcon.For(kind, Me == Side.Hand, idle ? Ui.Warn : Ui.You, 1, new Vector2(40, 28));
        icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(icon);
        var name = Ui.Label(fam == YardFamily.Construction ? "Construction" : FamilyName(fam), null, Ui.Text, Ui.Small, wrap: false);
        name.AddThemeFontOverride("font", Ui.SemiBold);
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(name);
        if (idle)
        {
            v.AddChild(Ui.Label("Idle: click to choose", null, Ui.Warn, Ui.Tiny, wrap: false));
            card.TooltipText = Ui.Wrap($"The {FamilyName(fam).ToLowerInvariant()} builds nothing. Click to show what it can build.");
            return card;
        }
        var cur = eco.Find(p.Units[0]);
        string curName = cur?.Class ?? "?";
        int cost = CostOf(curName);
        int left = BuildDaysAt(n, fam, cost - p.Progress);
        var all = p.AllUnits().Select(u => eco.Find(u)?.Class ?? "?").ToList();
        int totalLeft = BuildDaysAt(n, fam, all.Sum(CostOf) - p.Progress);
        var what = Ui.Label(Counts(all), null, Ui.Text2, Ui.Tiny, wrap: false);
        what.ClipText = true; what.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(what);
        v.AddChild(Ui.Bar(cost <= 0 ? 0 : (float)p.Progress / cost, Ui.You, 5));
        var foot = Ui.HBox(6);
        foot.AddChild(Ui.Label(left <= 0 ? "done next cycle" : $"~{left} {Plural(left, "day", "days")} left", null, Ui.Text3, Ui.Tiny, wrap: false));
        foot.AddChild(Ui.Grow());
        var f2 = fam;
        var stop = Ui.Button("Stop...", () => AskStopBuild(n, f2), "Stops all work at this yard: asks first and says what is lost.", "Link");
        stop.AddThemeFontSizeOverride("font_size", Ui.Tiny);
        foot.AddChild(stop);
        v.AddChild(foot);
        card.TooltipText = Ui.Wrap($"{FamilyName(fam)} is building {Counts(all)}. {curName}: {p.Progress} of {cost} points." + (all.Count > 1 ? $" The whole queue: about {totalLeft} days." : "")
            + " Each yard adds one point per cycle while it has refined material. Click to show what it can build.");
        return card;
    }

    /// <summary>A thing the yards here can build, as a picture tile with its refined cost and days. Click: order as many as "How many".</summary>
    Control OptionTile(string n, BuildRow b, bool hand)
    {
        var r = b.Row;
        var btn = Ui.Button("", () => { SubmitBuild(n, b.Family, r, _buildQty); MgRefreshSoon(); },
            $"{r.Name}: costs {r.Cost} refined each and {r.Maintenance} upkeep each, about {b.Days} days per unit here once the refined is there." +
            (b.Why != null ? $"\n{char.ToUpperInvariant(b.Why[0])}{b.Why[1..]}." : "") + (ClassTip(r.Name) is string ct ? "\n" + ct : "") + (b.Usable ? $"\nClick to order {_buildQty}." : ""), "Flat", !b.Usable);
        btn.SetMeta(UiTest.FullName, $"Build {r.Name}");
        btn.CustomMinimumSize = new Vector2(TileW, 132);
        btn.FocusMode = FocusModeEnum.None;
        btn.AddThemeStyleboxOverride("normal", Ui.Box(Ui.Panel2, 8, new Color(Ui.You, 0.3f), 1, 0, 0));
        btn.AddThemeStyleboxOverride("hover", Ui.Box(Ui.Hover, 8, new Color(Ui.You, 0.8f), 1, 0, 0));
        btn.AddThemeStyleboxOverride("pressed", Ui.Box(new Color(Ui.You, 0.18f), 8, Ui.You, 1, 0, 0));
        btn.AddThemeStyleboxOverride("disabled", Ui.Box(new Color(Ui.Panel2, 0.5f), 8, new Color(1, 1, 1, 0.05f), 1, 0, 0));
        var ink = b.Usable ? Ui.You : Ui.Text3;
        var v = Ui.VBox(2);
        v.MouseFilter = MouseFilterEnum.Ignore;
        v.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        v.OffsetLeft = 6; v.OffsetRight = -6; v.OffsetTop = 6; v.OffsetBottom = -4;
        var icon = UnitIcon.For(KindOf(r.Category, r.Name), hand, ink, SizeOfClass(r.Name), new Vector2(0, 36));
        v.AddChild(icon);
        var t = Ui.Label(r.Name, null, b.Usable ? Ui.Text : Ui.Text3, Ui.Small, wrap: true);
        t.AddThemeFontOverride("font", Ui.Medium);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.MaxLinesVisible = 2;
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(t);
        // QA-89: cost and days on their own lines, so a big number is never clipped ("584 ref" read as "84")
        foreach (var line in new[] { $"{r.Cost} ref", $"{b.Days} d" })
        {
            var c = Ui.Label(line, null, b.Usable ? Ui.Text2 : Ui.Text3, Ui.Tiny, wrap: false);
            c.HorizontalAlignment = HorizontalAlignment.Center;
            v.AddChild(c);
        }
        if (b.Why != null)
        {
            var why = Ui.Label(b.Why, null, b.Usable ? Ui.Warn : Ui.Text3, Ui.Tiny, wrap: false);
            why.HorizontalAlignment = HorizontalAlignment.Center;
            why.ClipText = true; why.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            v.AddChild(why);
        }
        btn.AddChild(v);
        return btn;
    }
}
