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
/// it builds and a progress bar, what can be built as picture tiles (click one to order it), and the buildings as tiles, one per building (right-click
/// to scrap). The same orders as the drawer's build table (user stories E1-E6); the core judges them when the clock moves.
/// rule: m2-construction §Build order, §Build time; m2-maintenance §Overdraft, §Scrapping and refunds.
/// </summary>
public partial class GameScreen
{
    void BuildTab(string n, float width)
    {
        var eco = S.Host.Economy;
        bool hand = Me == Side.Hand;

        // two columns (Paul 2026-11-14: two columns, no scrolling): what stands on the planet on the left (what stops it, the yards, the
        // slots, the buildings), what it can build on the right
        var cols = Ui.HBox(WinGap);
        var left = Ui.VBox(Ui.Block); left.CustomMinimumSize = new Vector2(InfraLeft, 0);
        var right = Ui.VBox(Ui.Block); right.CustomMinimumSize = new Vector2(Math.Max(InfraRight, width - WinGap - InfraLeft), 0);
        cols.AddChild(left); cols.AddChild(right);
        Add(cols);
        var page = _into;
        _into = left;

        // what stops the planet, first
        int mines = eco.OfKindAt(n, FacilityKind.Mine), refs = eco.OfKindAt(n, FacilityKind.Refinery);
        int smug = mines + refs > 0 ? eco.DiversionPercent(n, Me) : 0;
        if (smug > 0) Line($"Smugglers may take a cycle's output {smug}% of the time. More support, or forces here, scare them off.", Ui.Warn, Ui.Small);
        if (S.Game.State.Blockades.ContainsKey(n)) Line("Blockaded: the mines and yards here stop until the enemy fleet is driven off.", Bad, Ui.Small);
        if (S.Host.Support.IsUprising(n)) Line("Uprising: the mines and yards here stop until it ends.", Bad, Ui.Small);

        // the yards, a card each
        var fams = YardFamiliesAt(n).ToList();
        Section("Yards");   // the cards below are the count
        if (fams.Count == 0) Line("No yard here. A Construction yard builds facilities, a Shipyard ships and fighters, a Training facility troops.", Ui.Text3, Ui.Small);
        else
        {
            var yards = new HFlowContainer();
            yards.AddThemeConstantOverride("h_separation", 8); yards.AddThemeConstantOverride("v_separation", 8);
            foreach (var fam in fams) yards.AddChild(YardCard(n, fam));
            Add(yards);
        }

        // the planet: slots, what it gives, its buildings
        Section("Planet");
        // slots and what the planet gives
        var slots = eco.SlotsOf(n);
        var strip = Ui.VBox(8);
        strip.AddChild(SlotStrip("Energy slots", eco.EnergyUsed(n), slots.Energy, "Every building takes one energy slot."));
        strip.AddChild(SlotStrip("Raw material slots", eco.RawUsed(n), eco.EffectiveRaw(n), "A mine also needs a raw material slot. A planet never has more usable raw slots than energy slots."));
        // one line each, in the drawer's words, at the slot lines' size (ui-inspo review 2026-11-14: the one sentence wrapped mid-phrase)
        string givesTip = Ui.Wrap("Each mine digs one raw material every cycle; each refinery turns one raw into one refined. The cycle is longer when support is low. "
            + $"Every mine and refinery also adds {eco.Params.CapacityPerFacility} to your upkeep room (with as many of the other kind).");
        var gives = Ui.VBox(3);
        foreach (var words in new[] {
            "Mines  " + (mines == 0 ? "none" : $"{mines} · about +{RawPerMonth(n)} raw / 30 days"),
            "Refineries  " + (refs == 0 ? "none" : $"{refs} · about +{RefinedPerMonth(n)} refined / 30 days") })
        {
            var l = Ui.Label(words, null, Ui.Text2, Ui.Tiny, wrap: false);
            l.TooltipText = givesTip;
            l.MouseFilter = MouseFilterEnum.Stop;
            gives.AddChild(l);
        }
        strip.AddChild(gives);
        Add(strip);
        // the buildings, one tile each, grouped by kind
        var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == n).ToList();
        Section("Buildings", here.Count(o => o.Completed).ToString());
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
        UnitTiles(flow, here.Where(o => o.Completed).OrderBy(o => Array.IndexOf(FacilityPriority, o.Class) is int i && i >= 0 ? i : 50).ThenBy(o => o.Class, StringComparer.Ordinal)
            .Select(o => ((Drag?)null, o, (string)null)), n);
        foreach (var o in here.Where(o => !o.Completed).OrderBy(o => o.Class, StringComparer.Ordinal).ThenBy(o => o.Id))
            flow.AddChild(Tile(UnitIcon.ForFacility(o.Class), hand, 1, o.Class, "being built", 1, Ui.You, ghost: true));   // one tile per building (Paul 2026-11-14)
        if (here.Count == 0) flow.AddChild(EmptySlot("None"));
        Add(flow);
        if (Me == Side.Collective && n == S.MyHqSystem) HqSection(n);
        GroundLink(n);   // under the planet, not under both columns: the build column needs the height

        _into = right;
        // what can be built here, as tiles; "How many" sits on the caption's line, so four rows of tiles fit under it
        var head = (BoxContainer)Caption("Build", null);
        Add(head);
        if (_buildSection is "Ships" or "Fighters" && !HasYard(n, YardFamily.Shipyard) || _buildSection == "Troops" && !HasYard(n, YardFamily.Training)) _buildSection = "Facilities";
        var chips = new HFlowContainer();   // wraps rather than widening the column
        chips.AddThemeConstantOverride("h_separation", 10); chips.AddThemeConstantOverride("v_separation", 4);
        Container top = chips;
        var group = new ButtonGroup();
        foreach (var section in new[] { "Facilities", "Ships", "Fighters", "Troops" })
        {
            var fam = section switch { "Facilities" => YardFamily.Construction, "Troops" => YardFamily.Training, _ => YardFamily.Shipyard };
            string sec = section;
            var b = Ui.Chip(section, () => { _buildSection = sec; MgRefreshSoon(); },
                HasYard(n, fam) ? $"Built by the {FamilyName(fam).ToLowerInvariant()} here" : $"Needs a {FamilyName(fam).ToLowerInvariant()} on this planet", _buildSection == section, group);
            b.FocusMode = FocusModeEnum.None;
            b.ThemeTypeVariation = "TabChip";   // drawn exactly like the window's tabs (Paul 2026-11-14)
            // greyed out with no yard: Ships, Fighters, Troops can't be picked (no hover, no underline; the tooltip says why). Facilities
            // stays clickable: it is the page shown when there is no yard, and it says so
            if (!HasYard(n, fam))
            {
                if (section == "Facilities") b.AddThemeColorOverride("font_color", Ui.Dimmed);
                else { b.Disabled = true; b.MouseDefaultCursorShape = CursorShape.Arrow; }
            }
            top.AddChild(b);
        }
        Add(chips);
        top = head;
        top.AddChild(Ui.Label("How many", null, Ui.Text3, Ui.Tiny, wrap: false));
        var less = Ui.Button("−", () => { _buildQty = Math.Max(1, _buildQty - 1); MgRefreshSoon(); }, "One fewer", "Step");
        less.CustomMinimumSize = new Vector2(24, 24);
        top.AddChild(less);
        var ql = Ui.Label(_buildQty.ToString(), null, Ui.Text, Ui.Small, wrap: false);
        ql.AddThemeFontOverride("font", Ui.Num);
        ql.CustomMinimumSize = new Vector2(22, 0); ql.HorizontalAlignment = HorizontalAlignment.Center;
        top.AddChild(ql);
        var more = Ui.Button("+", () => { _buildQty = Math.Min(20, _buildQty + 1); MgRefreshSoon(); }, "One more (up to 20)", "Step");
        more.CustomMinimumSize = new Vector2(24, 24);
        top.AddChild(more);

        var famSel = _buildSection switch { "Facilities" => YardFamily.Construction, "Troops" => YardFamily.Training, _ => YardFamily.Shipyard };
        if (!HasYard(n, famSel))
            Line($"This planet has no {FamilyName(famSel).ToLowerInvariant()}. {(famSel == YardFamily.Construction ? "Build one from a planet that already has a construction yard." : "Build one under Facilities first.")}", Ui.Text2, Ui.Small);
        else
        {
            var rows = BuildRowsFor(n, _buildSection);
            _shownBuildRows = rows;
            var grid = new HFlowContainer();
            grid.AddThemeConstantOverride("h_separation", 6); grid.AddThemeConstantOverride("v_separation", 6);
            // QA-89: cost and days are never clipped ("584 ref" read as "84"): on one line when every tile of the tab has room for both,
            // else a line each, the same in the whole tab
            var font = Ui.Grotesk ?? ThemeDB.FallbackFont;
            bool oneLine = rows.All(b => font.GetStringSize(CostDays(b), HorizontalAlignment.Left, -1, Ui.Tiny).X <= TileW - 8 - 2);
            foreach (var b in rows) grid.AddChild(OptionTile(n, b, hand, oneLine));
            Add(grid);
        }

        ResearchLine(_buildSection switch { "Facilities" => ResearchKind.Facility, "Troops" => ResearchKind.Troop, _ => ResearchKind.Ship },
            HasYard(n, famSel) && _shownBuildRows.Any(b => b.Why == NotResearched));
        _into = page;
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

    /// <summary>A yard as a card: its picture, what it is building with a progress bar and the days left, or "Idle". The build orders
    /// still waiting for the clock are listed under it, each with Cancel (impeccable critique 2026-11-14: a build click showed nothing here).</summary>
    Control YardCard(string n, YardFamily fam)
    {
        var eco = S.Host.Economy;
        var p = S.Host.Construction.ProjectAt(n, fam);
        var queued = QueuedBuilds(n, fam);
        bool building = p is { Units.Count: > 0 }, idle = !building && queued.Count == 0;
        var card = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(148, 0) };   // two to a row in the planet column (InfraLeft)
        card.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0, 0, 0, 0), 0, idle ? new Color(Ui.Warn, 0.5f) : new Color(Ui.You, 0.3f), 1, 8, 8));
        card.SetMeta(UiTest.FullName, $"Yard {n} {fam}");
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
        var name = Ui.Label(fam switch { YardFamily.Construction => "Construction", YardFamily.Training => "Training", _ => FamilyName(fam) }, null, Ui.Text, Ui.Small, wrap: false);
        name.AddThemeFontOverride("font", Ui.SemiBold);
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(name);
        if (idle)
        {
            v.AddChild(Ui.Label("Idle", null, Ui.Warn, Ui.Tiny, wrap: false));
            card.TooltipText = Ui.Wrap($"The {FamilyName(fam).ToLowerInvariant()} builds nothing. Pick what it should build under Build.");
            return card;
        }
        if (!building)
        {
            foreach (var o in queued) v.AddChild(QueuedLine(o));
            card.TooltipText = Ui.Wrap($"The {FamilyName(fam).ToLowerInvariant()} starts on {(queued.Count == 1 ? "this order" : "these orders")} as soon as the clock moves. Until then Cancel takes an order back.");
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
        var stop = Ui.Link("Stop...", () => AskStopBuild(n, f2), "Stops all work at this yard: asks first and says what is lost.");
        stop.AddThemeFontSizeOverride("font_size", Ui.Tiny);
        foot.AddChild(stop);
        v.AddChild(foot);
        foreach (var o in queued) v.AddChild(QueuedLine(o));
        card.TooltipText = Ui.Wrap($"{FamilyName(fam)} is building {Counts(all)}. {curName}: {p.Progress} of {cost} points." + (all.Count > 1 ? $" The whole queue: about {totalLeft} days." : "")
            + " Each yard adds one point per cycle while it has refined material." + (queued.Count > 0 ? " Orders waiting for the clock join the end of the queue; until then Cancel takes one back." : ""));
        return card;
    }

    /// <summary>A build order waiting for the clock, inside its yard card: "Ordered: 2 x Bond", then Cancel on its own line (the card is narrow).</summary>
    Control QueuedLine(Praedra.Core.Clock.BuildOrder o)
    {
        var box = Ui.VBox(0);
        var l = Ui.Label($"Ordered: {o.Count} x {o.Class}", null, Pending, Ui.Tiny, wrap: false);
        l.ClipText = true; l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        box.AddChild(l);
        string what = S.WhatOf(o.Sequence) ?? $"Build {o.Count} x {o.Class} at {o.System}";
        var c = Ui.Link("Cancel", () => CancelOrder(o.Sequence), $"Take back: {what}. Possible until the clock moves.");
        c.AddThemeFontSizeOverride("font_size", Ui.Tiny);
        c.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        c.SetMeta(UiTest.FullName, $"Cancel: {what}");
        box.AddChild(c);
        return box;
    }

    /// <summary>A thing the yards here can build, as a picture tile with its refined cost and days. Click: order as many as "How many".</summary>
    static string CostDays(BuildRow b) => $"{b.Row.Cost} ref · {b.Days} d";

    Control OptionTile(string n, BuildRow b, bool hand, bool oneLine)
    {
        var r = b.Row;
        string tip =
            $"{r.Name}: costs {r.Cost} refined each and {r.Maintenance} upkeep each, about {b.Days} days per unit here once the refined is there." +
            (b.Why != null ? $"\n{char.ToUpperInvariant(b.Why[0])}{b.Why[1..]}." : "") + (ClassTip(r.Name) is string ct ? "\n" + ct : "") + (b.Usable ? $"\nClick to order {_buildQty}." : "");
        Action order = () => { SubmitBuild(n, b.Family, r, _buildQty); MgRefreshSoon(); };
        Button btn;
        if (UnitArt.Has(r.Name)) // the painted picture as the hover card too (art/units/units.json)
        {
            btn = new ArtButton { Cls = r.Name, FocusMode = FocusModeEnum.None, Disabled = !b.Usable, MouseDefaultCursorShape = CursorShape.PointingHand,
                TooltipText = Ui.Wrap(tip), ThemeTypeVariation = "Flat" };
            btn.Pressed += order;
        }
        else btn = Ui.Button("", order, tip, "Flat", !b.Usable);
        btn.SetMeta(UiTest.FullName, $"Build {r.Name}");
        btn.CustomMinimumSize = new Vector2(TileW, TileH);
        btn.FocusMode = FocusModeEnum.None;
        var clear = new Color(0, 0, 0, 0);
        // a choice is a plain card like every other (Paul 2026-11-14): grey edge, lighter on hover; blue stays for the units you own
        btn.AddThemeStyleboxOverride("normal", Ui.Box(clear, 0, Ui.WinLine, 1, 0, 0));
        btn.AddThemeStyleboxOverride("hover", Ui.Box(clear, 0, Ui.WinText, 1, 0, 0));
        btn.AddThemeStyleboxOverride("pressed", Ui.Box(clear, 0, Ui.WinActive, 1, 0, 0));
        btn.AddThemeStyleboxOverride("disabled", Ui.Box(clear, 0, Ui.WinLine, 1, 0, 0));
        var ink = b.Usable ? Ui.You : Ui.Dimmed;
        var v = Ui.VBox(2);
        v.MouseFilter = MouseFilterEnum.Ignore;
        v.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        v.OffsetLeft = 4; v.OffsetRight = -4; v.OffsetTop = 6; v.OffsetBottom = -4;
        // as tall as its words need (a name or a reason may take two lines), not a fixed 146 px, so four rows of tiles fit the window; the
        // row then takes its tallest tile's height
        v.MinimumSizeChanged += () => btn.CustomMinimumSize = new Vector2(TileW, Math.Max(TileH, v.GetCombinedMinimumSize().Y + 10));
        // a painted picture of the class (art/units/units.json); what you can't build yet gets its drawn icon in solid grey instead of a
        // see-through painting (Paul 2026-11-14); the hover card still shows the painting
        if (UnitArt.Has(r.Name) && b.Usable)
        {
            var pic = UnitArt.Rect(UnitArt.Thumb(r.Name));
            pic.CustomMinimumSize = new Vector2(0, 40);
            v.AddChild(pic);
        }
        else v.AddChild(UnitIcon.For(KindOf(r.Category, r.Name), hand, ink, SizeOfClass(r.Name), new Vector2(0, 36)));
        var t = Ui.Label(KeepNumeral(r.Name), null, b.Usable ? Ui.Text : Ui.Dimmed, Ui.Small, wrap: true);
        t.AddThemeFontOverride("font", Ui.Medium);
        t.AutowrapMode = TextServer.AutowrapMode.Word;   // breaks between words only: never "Constructio / n"
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.MaxLinesVisible = 2;
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(t);
        foreach (var line in oneLine ? new[] { CostDays(b) } : new[] { $"{r.Cost} ref", $"{b.Days} d" })
        {
            var c = Ui.Label(line, null, b.Usable ? Ui.Text2 : Ui.Dimmed, Ui.Tiny, wrap: false);
            c.HorizontalAlignment = HorizontalAlignment.Center;
            v.AddChild(c);
        }
        // grey says "not researched" (the research line under the tiles says so once), so those tiles carry no reason line: four rows fit
        if (b.Why != null && b.Why != NotResearched)
        {
            // wraps to two lines so "waits for refined" or "no upkeep room" reads in full (QA round 126)
            var why = Ui.Label(b.Why, null, b.Usable ? Ui.Warn : Ui.Dimmed, Ui.Tiny, wrap: true);
            why.HorizontalAlignment = HorizontalAlignment.Center;
            why.MaxLinesVisible = 2; why.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            v.AddChild(why);
        }
        btn.AddChild(v);
        return btn;
    }
}
