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
/// Your planet's Infrastructure window, from Paul's Paper design (2026-11-18, "CHARTER: INFRASTRUCTURE") and his answers (2026-11-19).
/// The head stays put: the name with the window buttons; the energy and raw material gauges with the way to the Ground and Fleet windows;
/// a strip of cells, one per kind of building (the three yards; behind the arrows the mines, refineries and defences). Under it, and
/// scrolling: the open cell's buildings, one tile each (right-click to scrap), and what can be built of it, as cards (click to order
/// "How many"). The same orders as the drawer's build table (user stories E1-E6); the core judges them when the clock moves.
/// rule: m2-construction §Build order, §Build time; m2-maintenance §Overdraft, §Scrapping and refunds; m2-slots-and-resources §Occupied slots.
/// </summary>
public partial class GameScreen
{
    /// <summary>The cells of the strip: the three yards, then (behind the arrows, Paul 2026-11-19) mines, refineries and defences.</summary>
    public enum InfraTab { Construction, Shipyard, Training, Mines, Refineries, Defences }

    const int CellsPerPage = 3;
    const int ArrowColumn = 32;
    const int CardHeight = 128;
    static readonly Vector2 BuildingTileSize = new(114, 56);

    static InfraTab TabOfSection(string section) => section switch { "Facilities" => InfraTab.Construction, "Troops" => InfraTab.Training, _ => InfraTab.Shipyard };

    static string SectionOfFamily(YardFamily f) => f switch { YardFamily.Construction => "Facilities", YardFamily.Training => "Troops", _ => "Ships" };

    static string CellName(InfraTab t) => t switch
    {
        InfraTab.Construction => "Construction yards", InfraTab.Shipyard => "Orbital shipyards", InfraTab.Training => "Training facilities",
        InfraTab.Mines => "Mines", InfraTab.Refineries => "Refineries", _ => "Defences",
    };

    /// <summary>The yards a cell stands for, or null (mines, refineries and defences are built by the construction yards).</summary>
    static YardFamily? FamilyOfTab(InfraTab t) => t switch
    {
        InfraTab.Construction => YardFamily.Construction, InfraTab.Shipyard => YardFamily.Shipyard, InfraTab.Training => YardFamily.Training, _ => null,
    };

    static UnitIcon.Kind IconOfTab(InfraTab t) => t switch
    {
        InfraTab.Construction => UnitIcon.Kind.Construction, InfraTab.Shipyard => UnitIcon.Kind.Shipyard, InfraTab.Training => UnitIcon.Kind.Training,
        InfraTab.Mines => UnitIcon.Kind.Mine, InfraTab.Refineries => UnitIcon.Kind.Refinery, _ => UnitIcon.Kind.Shield,
    };

    /// <summary>Is a facility class one of a cell's buildings? Defences are every building that is not a yard, a mine or a refinery.</summary>
    static bool InTab(string cls, InfraTab t)
    {
        if (!FacilityKinds.TryParse(cls, out var k)) return t == InfraTab.Defences;
        return t switch
        {
            InfraTab.Mines => k == FacilityKind.Mine,
            InfraTab.Refineries => k == FacilityKind.Refinery,
            InfraTab.Defences => !FacilityKinds.IsYard(k) && k != FacilityKind.Mine && k != FacilityKind.Refinery,
            _ => FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == FamilyOfTab(t),
        };
    }

    void InfraOwnWin(MgWin w)
    {
        string n = w.System;
        var tab = w.Shows ??= TabOfSection(_buildSection);
        InfraHead(w, n);
        if (w.Min) return;
        InfraBuildings(n, tab);
        InfraBuild(w, n, tab);
        if (Me == Side.Collective && n == S.MyHqSystem) HqSection(n);
    }

    // ================================================================== the head: name, gauges, links, the strip

    void InfraHead(MgWin w, string n)
    {
        var eco = S.Host.Economy;
        var head = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        var top = Pad(24, 24, 32, w.Min ? 24 : 40);
        head.AddChild(top);
        var parts = Boxed(new VBoxContainer(), 32);
        top.AddChild(parts);

        // the name, "CHARTER: INFRASTRUCTURE", BitPotion 48 px (32 px when too long), and the window buttons
        var row = Boxed(new HBoxContainer(), NameGap);
        string upper = $"{n}: Infrastructure".ToUpperInvariant();
        float room = WidthOf(w) - 2 - 48 - NameGap - 3 * WinButton - 2 * 4;
        int size = Ui.NameSize(upper, room);
        var name = Look.Words(upper, size, Look.Text);
        name.AddThemeFontOverride("font", Ui.NameFont(size, upper));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.AddChild(name);
        row.AddChild(WinButtons(w));
        parts.AddChild(row);
        if (!w.Min)
        {
            var mid = Boxed(new VBoxContainer(), 12);
            mid.AddChild(InfraGauges(n));
            // what stops the planet (Paul 2026-11-19: kept, small): one line each, only when it applies
            int mines = eco.OfKindAt(n, FacilityKind.Mine), refs = eco.OfKindAt(n, FacilityKind.Refinery);
            int smug = mines + refs > 0 ? eco.DiversionPercent(n, Me) : 0;
            var warns = new List<(string, Color)>();
            if (S.Game.State.Blockades.ContainsKey(n)) warns.Add(("Blockaded: the mines and yards here stop until the enemy fleet is driven off.", Ui.Danger));
            if (S.Host.Support.IsUprising(n)) warns.Add(("Uprising: the mines and yards here stop until it ends.", Ui.Danger));
            if (smug > 0) warns.Add(($"Smugglers may take a cycle's output {smug}% of the time. More support, or forces here, scare them off.", Ui.Warn));
            foreach (var (words, ink) in warns)
            {
                var l = Look.Words(words, 12, ink);
                l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                mid.AddChild(l);
            }
            parts.AddChild(mid);
        }
        w.Head.AddChild(head);
        if (!w.Min) w.Head.AddChild(YardStrip(w, n));
    }

    static T Boxed<T>(T box, int gap) where T : BoxContainer
    {
        box.MouseFilter = MouseFilterEnum.Ignore;
        box.AddThemeConstantOverride("separation", gap);
        return box;
    }

    /// <summary>The two slot gauges, a line between them, and on the right the way to the planet's Ground window and its fleet.</summary>
    Control InfraGauges(string n)
    {
        var eco = S.Host.Economy;
        var slots = eco.SlotsOf(n);
        var built = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == n && o.Completed).ToList();
        int eBuilt = built.Count, eUsed = eco.EnergyUsed(n);
        int rBuilt = built.Count(o => FacilityKinds.TryParse(o.Class, out var k) && k == FacilityKind.Mine), rUsed = eco.RawUsed(n), rTotal = eco.EffectiveRaw(n);
        int mines = eco.OfKindAt(n, FacilityKind.Mine), refs = eco.OfKindAt(n, FacilityKind.Refinery);
        string Ordered(int used, int b) => used > b ? $", {used - b} ordered or being built" : "";

        var row = Boxed(new HBoxContainer(), 24);
        var gauges = Boxed(new HBoxContainer(), 24);
        gauges.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        gauges.AddChild(Gauge("System energy", eBuilt, eUsed, slots.Energy, Look.Accent, Look.AccentDeep,
            $"System energy: {eUsed} of {slots.Energy} slots taken ({eBuilt} built{Ordered(eUsed, eBuilt)}), {Math.Max(0, slots.Energy - eUsed)} free. "
            + "Every building takes one energy slot.\nBright: built. Mid: ordered or being built. Dark: free."));
        gauges.AddChild(new ColorRect { Color = Look.Divider, CustomMinimumSize = new Vector2(1, 36), MouseFilter = MouseFilterEnum.Ignore });
        gauges.AddChild(Gauge("Raw material", rBuilt, rUsed, rTotal, Look.Raw, Look.RawDeep,
            $"Raw material: {rUsed} of {rTotal} raw slots taken by mines ({rBuilt} built{Ordered(rUsed, rBuilt)}), {Math.Max(0, rTotal - rUsed)} free. A mine needs a raw slot as well as an energy slot. "
            + (slots.Raw > slots.Energy ? $"This planet has {slots.Raw} raw slots but only {slots.Energy} energy slots, so only {rTotal} can be used. " : "A planet never uses more raw slots than it has energy slots. ")
            + $"\nMines: {(mines == 0 ? "none" : $"{mines}, about +{RawPerMonth(n)} raw every 30 days")}. Refineries: {(refs == 0 ? "none" : $"{refs}, about +{RefinedPerMonth(n)} refined every 30 days")}. "
            + "Each mine digs one raw material every cycle; each refinery turns one raw into one refined. The cycle is longer when support is low. "
            + $"Every mine and refinery also adds {eco.Params.CapacityPerFacility} to your upkeep room (with as many of the other kind)."));
        row.AddChild(gauges);
        row.AddChild(Ui.Grow());

        var links = Boxed(new HBoxContainer(), 16);
        links.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        links.AddChild(OutlinedButton(PixelIconData.Tile["troops"], "Ground and military", $"Ground and military at {n}", () => OpenMgmt(MgKind.Ground, n),
            $"The Ground window of {n}: leaders, troops and fighters on the planet, and the fleets in orbit."));
        var fleets = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n && f.Transit == null).OrderBy(f => f.Id).ToList();
        links.AddChild(OutlinedButton(PixelIconData.Tile["fleets"], "Fleet", $"Fleet at {n}",
            () => { if (fleets.Count == 1) OpenMgmt(MgKind.Fleet, fleet: fleets[0].Id); else OpenMgmt(MgKind.Ground, n, tab: "Fleets"); },
            fleets.Count == 0 ? $"No fleet of yours is in orbit at {n}." : fleets.Count == 1 ? $"Opens {fleets[0].Name}'s window."
                : $"{fleets.Count} fleets of yours are in orbit ({string.Join(", ", fleets.Select(f => f.Name))}): opens the list of them.",
            fleets.Count == 0));
        row.AddChild(links);
        return row;
    }

    static Control Gauge(string label, int built, int used, int total, Color bright, Color mid, string tip)
    {
        var v = Boxed(new VBoxContainer(), 4);
        v.MouseFilter = MouseFilterEnum.Stop;
        v.TooltipText = Ui.Wrap(tip);
        var h = Boxed(new HBoxContainer(), 16);
        h.AddChild(Look.Words(label, 12, Look.Text, true));
        h.AddChild(Ui.Grow());
        var num = Look.Words($"{used}/{total}", 10, Look.Dim);
        num.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(num);
        v.AddChild(h);
        v.AddChild(SlotSquares.Of(built, used, total, bright, mid));
        return v;
    }

    /// <summary>A button outlined in the design's line colour: a 16 px icon and its words, 8 px inside.</summary>
    static Button OutlinedButton(string[] icon, string words, string key, Action act, string tip, bool disabled = false)
    {
        var b = new Button { FocusMode = FocusModeEnum.None, Disabled = disabled, TooltipText = Ui.Wrap(tip),
            MouseDefaultCursorShape = disabled ? CursorShape.Arrow : CursorShape.PointingHand };
        var clear = new Color(0, 0, 0, 0);
        foreach (var (state, line) in new[] { ("normal", Look.Line), ("hover", Look.Dim), ("pressed", Look.Accent), ("hover_pressed", Look.Accent), ("disabled", Look.Line) })
            b.AddThemeStyleboxOverride(state, Look.Box(clear, line, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var ink = disabled ? Look.Dim : Look.Text;
        var h = Boxed(new HBoxContainer(), 8);
        var i = IconPic.Of(icon, 1, ink, disabled);
        i.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(i);
        h.AddChild(Look.Words(words, 12, ink, true));
        Fill(b, h, 8, 8);
        b.Pressed += act;
        b.SetMeta(UiTest.FullName, key);
        return b;
    }

    /// <summary>Puts <paramref name="content"/> inside a button with a margin, and keeps the button at least as big as it.</summary>
    static void Fill(Button b, Control content, int padX, int padY, Vector2? least = null)
    {
        var m = Pad(padX, padX, padY, padY);
        m.AddChild(content);
        m.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        b.AddChild(m);
        void Grow() { var c = m.GetCombinedMinimumSize(); b.CustomMinimumSize = least is Vector2 l ? new Vector2(Math.Max(l.X, c.X), Math.Max(l.Y, c.Y)) : c; }
        m.MinimumSizeChanged += Grow;
        Grow();
    }

    // ================================================================== the strip of cells

    /// <summary>Three cells a page and the arrows: › shows the mines, refineries and defences, ‹ the yards again (Paul 2026-11-19, 4B). The
    /// open cell is the lighter one, joined to the part under it.</summary>
    Control YardStrip(MgWin w, string n)
    {
        var strip = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var lines = Look.Box(Look.Line);
        lines.ContentMarginTop = 1;   // the line above the cells
        strip.AddThemeStyleboxOverride("panel", lines);
        var h = Boxed(new HBoxContainer(), 1);   // the line between cells
        strip.AddChild(h);
        foreach (var t in Enum.GetValues<InfraTab>().Skip(w.Page * CellsPerPage).Take(CellsPerPage)) h.AddChild(YardCell(w, n, t));
        var arrows = Boxed(new VBoxContainer(), 1);
        arrows.CustomMinimumSize = new Vector2(ArrowColumn, 0);
        arrows.AddChild(ArrowButton(true, w.Page == 0, () => TurnStrip(w, 1), "Mines, refineries and defences", $"Strip next {n}"));
        arrows.AddChild(ArrowButton(false, w.Page == 1, () => TurnStrip(w, 0), "The yards", $"Strip back {n}"));
        h.AddChild(arrows);
        return strip;
    }

    /// <summary>The other page of the strip; its first cell opens unless the open one is on it (back on the yards: the yard last opened).</summary>
    void TurnStrip(MgWin w, int page)
    {
        w.Page = page;
        var on = Enum.GetValues<InfraTab>().Skip(page * CellsPerPage).Take(CellsPerPage).ToList();
        if (w.Shows is not InfraTab t || !on.Contains(t)) w.Shows = page == 0 ? TabOfSection(_buildSection) : on[0];
        MgRefreshSoon();
    }

    static Button ArrowButton(bool right, bool usable, Action act, string tip, string key)
    {
        var b = new Button { FocusMode = FocusModeEnum.None, Disabled = !usable, TooltipText = tip, SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseDefaultCursorShape = usable ? CursorShape.PointingHand : CursorShape.Arrow };
        b.AddThemeStyleboxOverride("normal", Look.Box(Look.Bg));
        b.AddThemeStyleboxOverride("hover", Look.Box(Look.Band));
        b.AddThemeStyleboxOverride("pressed", Look.Box(Look.Band));
        b.AddThemeStyleboxOverride("hover_pressed", Look.Box(Look.Band));
        b.AddThemeStyleboxOverride("disabled", Look.Box(Look.Bg));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var c = new Chevron { Right = right, Ink = usable ? Look.Text : Look.Dim, CustomMinimumSize = new Vector2(24, 24) };
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.AddChild(c);
        Fill(b, center, 8, 8);
        b.Pressed += act;
        b.SetMeta(UiTest.FullName, key);
        return b;
    }

    /// <summary>
    /// A cell of the strip: an icon, the kind's name and how many stand here; under it what they are doing and a 2 px bar. A yard says
    /// "Building:" and how many days are left, and the name of what it builds while the mouse is on it (Paul 2026-11-19); "Ordered:" when
    /// orders wait for the clock; "Idle"; or "No infrastructure". Click: open it. Right-click a yard: stop its work or cancel an order.
    /// </summary>
    Control YardCell(MgWin w, string n, InfraTab t)
    {
        var eco = S.Host.Economy;
        bool open = w.Shows == t;
        var cell = new HoverWordsButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseDefaultCursorShape = CursorShape.PointingHand };
        cell.SetMeta(UiTest.FullName, $"Yard {n} {t}");
        var bg = open ? Look.Band : Look.Bg;
        cell.AddThemeStyleboxOverride("normal", Look.Box(bg));
        cell.AddThemeStyleboxOverride("hover", Look.Box(open ? Look.Band : new Color("17171a")));
        cell.AddThemeStyleboxOverride("pressed", Look.Box(Look.Band));
        cell.AddThemeStyleboxOverride("hover_pressed", Look.Box(Look.Band));
        cell.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        var v = Boxed(new VBoxContainer(), 16);
        var top = Boxed(new HBoxContainer(), 8);
        var icon = IconPic.Of(PixelIcons.Building(IconOfTab(t)) ?? PixelIcons.Building(UnitIcon.Kind.Building), 1, Look.Text);
        icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(icon);
        // the name is never cut ("Construction yards" is the longest): its cell takes the room it needs, the cells share the rest
        var name = Look.Words(CellName(t), 16, Look.Text, true);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(name);
        var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == n && InTab(o.Class, t)).ToList();
        int count = here.Count(o => o.Completed);
        top.AddChild(Look.Words(count.ToString(), 16, count == 0 ? Look.Soft : Look.Dim));
        v.AddChild(top);

        // what they are doing
        string left = "No infrastructure", right = "-", hover = null, tip;
        Color ink = Look.Dim;
        float share = 0;
        bool track = false;   // the bar's blue track (something under way) or the grey one
        var menu = new List<(string, Action)>();
        if (FamilyOfTab(t) is YardFamily fam)
        {
            var p = S.Host.Construction.ProjectAt(n, fam);
            var queued = QueuedBuilds(n, fam);
            string fname = FamilyName(fam).ToLowerInvariant();
            if (p is { Units.Count: > 0 })
            {
                var cur = eco.Find(p.Units[0]);
                string curName = cur?.Class ?? "?";
                int cost = CostOf(curName);
                int daysLeft = BuildDaysAt(n, fam, cost - p.Progress);
                var all = p.AllUnits().Select(u => eco.Find(u)?.Class ?? "?").ToList();
                int totalLeft = BuildDaysAt(n, fam, all.Sum(CostOf) - p.Progress);
                (left, right, hover, ink, track) = ("Building:", daysLeft <= 0 ? "done next cycle" : $"{daysLeft} {Plural(daysLeft, "day", "days")} left", curName, Look.Text, true);
                share = cost <= 0 ? 0 : (float)p.Progress / cost;
                tip = $"The {fname} is building {Counts(all)}. {curName}: {p.Progress} of {cost} points." + (all.Count > 1 ? $" The whole queue: about {totalLeft} days." : "")
                    + " Each yard adds one point per cycle while it has refined material.";
                var f2 = fam;
                menu.Add(("Stop building...", () => AskStopBuild(n, f2)));
            }
            else if (queued.Count > 0)
            {
                (left, right, hover, ink, track) = ("Ordered:", "next day", string.Join(", ", queued.Select(o => o.Count > 1 ? $"{o.Count} {o.Class}" : o.Class)), Look.Text, true);
                tip = $"The {fname} starts on {(queued.Count == 1 ? "this order" : "these orders")} as soon as the clock moves.";
            }
            else if (count > 0)
            {
                (left, ink) = ("Idle", Look.Dim);
                tip = $"The {fname} builds nothing. Pick what it should build under Build.";
            }
            else tip = $"No {fname} here. " + (fam == YardFamily.Construction ? "Build one from a planet that already has a construction yard." : "Build one with this planet's construction yards.");
            foreach (var o in queued)
            {
                string what = S.WhatOf(o.Sequence) ?? $"Build {o.Count} x {o.Class} at {o.System}";
                long seq = o.Sequence;
                menu.Add(($"Cancel: {what}", () => CancelOrder(seq)));
                tip += $"\nOrdered: {o.Count} x {o.Class}.";
            }
            tip += "\nClick: what it can build." + (menu.Count > 0 ? " Right-click: " + (p is { Units.Count: > 0 } ? "stop its work" + (queued.Count > 0 ? " or cancel an order." : ".") : "cancel an order.") : "");
        }
        else
        {
            int coming = here.Count - count;
            string kinds = t == InfraTab.Mines ? "mines" : t == InfraTab.Refineries ? "refineries" : "defences";
            if (count > 0)
            {
                ink = Look.Text;
                left = t switch
                {
                    InfraTab.Mines => $"+{RawPerMonth(n)} raw / 30 days",
                    InfraTab.Refineries => $"+{RefinedPerMonth(n)} refined / 30 days",
                    _ => Counts(here.Where(o => o.Completed).Select(o => o.Class)),
                };
            }
            else left = "None";
            if (coming > 0) right = $"{coming} on the way";
            tip = t switch
            {
                InfraTab.Mines => "Each mine digs one raw material every cycle, about the numbers shown. The cycle is longer when support is low.",
                InfraTab.Refineries => "Each refinery turns one raw material into one refined every cycle, about the numbers shown.",
                _ => "Shields and batteries fight for the planet when it is attacked; a Sensor Station watches the routes around it.",
            } + $"\nClick: the {kinds} here (right-click one to scrap it), and what the construction yards can build of them.";
        }
        cell.TooltipText = Ui.Wrap(tip);
        if (menu.Count > 0) RightClickMenu(cell, menu.ToArray());

        var st = Boxed(new VBoxContainer(), 4);
        var line = Boxed(new HBoxContainer(), 8);
        var l = Look.Words(left, 12, ink, true);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.ClipText = true; l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        line.AddChild(l);
        line.AddChild(Look.Words(right, 12, ink));
        st.AddChild(line);
        st.AddChild(new ThinBar { Share = share, Fill = Look.Accent, Track = track ? new Color(Look.AccentDeep, 0.5f) : new Color(Look.Dim, 0.4f), CustomMinimumSize = new Vector2(0, 2) });
        v.AddChild(st);
        if (hover != null) { cell.Target = l; cell.Normal = left; cell.Hover = hover; }
        Fill(cell, v, 16, 16);
        cell.Pressed += () =>
        {
            w.Shows = t;
            if (FamilyOfTab(t) is YardFamily f) _buildSection = SectionOfFamily(f);   // the drawer's build table follows the yard last opened
            MgRefreshSoon();
        };
        return cell;
    }

    // ================================================================== under the strip: the buildings and what to build

    void InfraBuildings(string n, InfraTab tab)
    {
        var eco = S.Host.Economy;
        var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == n && InTab(o.Class, tab) && !_scrapped.Contains(o.Id)).ToList();
        var done = here.Where(o => o.Completed).OrderBy(o => Array.IndexOf(FacilityPriority, o.Class) is int i && i >= 0 ? i : 50)
            .ThenBy(o => o.Class, StringComparer.Ordinal).ThenBy(o => o.Id).ToList();
        var coming = here.Where(o => !o.Completed).OrderBy(o => o.Class, StringComparer.Ordinal).ThenBy(o => o.Id).ToList();
        var sec = Boxed(new VBoxContainer(), 16);
        sec.AddChild(Heading2("Buildings", done.Count.ToString()));
        if (here.Count == 0) sec.AddChild(Look.Words($"No {CellName(tab).ToLowerInvariant()} here.", 12, Look.Dim));
        else
        {
            var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
            flow.AddThemeConstantOverride("h_separation", 8); flow.AddThemeConstantOverride("v_separation", 8);
            foreach (var o in done) flow.AddChild(BuildingTile(o, n, done.Where(x => x.Class == o.Class && Scrappable(x)).ToList()));
            foreach (var o in coming) flow.AddChild(BuildingTile(o, n, null));
            sec.AddChild(flow);
        }
        Add(sec);
    }

    /// <summary>A heading of the part under the strip: the word, and a count in the dim grey 16 px to its right.</summary>
    static Control Heading2(string word, string count = null)
    {
        var h = Boxed(new HBoxContainer(), 16);
        h.AddChild(Look.Words(word, 16, Look.Text, true));
        if (count != null) h.AddChild(Look.Words(count, 16, count == "0" ? Look.Soft : Look.Dim));
        return h;
    }

    /// <summary>One building: its icon in an outlined tile, the name on hover (Paul's design). Right-click to scrap; one being built is faded.</summary>
    Control BuildingTile(EconObject o, string n, List<EconObject> same)
    {
        bool ghost = !o.Completed;
        var b = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = BuildingTileSize };
        var clear = new Color(0, 0, 0, 0);
        b.AddThemeStyleboxOverride("normal", Look.Box(clear, Look.Edge, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("hover", Look.Box(clear, Look.Dim, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("pressed", Look.Box(clear, Look.Dim, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("hover_pressed", Look.Box(clear, Look.Dim, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.SetMeta(UiTest.FullName, $"Building {o.Class} {o.Id}");
        var grid = PixelIcons.Building(UnitIcon.ForFacility(o.Class)) ?? PixelIcons.Building(UnitIcon.Kind.Building);
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        center.AddChild(IconPic.Of(grid, 2, Look.Text));
        b.AddChild(center);
        string tip = o.Class + (ghost ? ": being built" : "") + (ClassTip(o.Class) is string ct ? "\n" + ct : "");
        if (!ghost && Scrappable(o))
        {
            tip += "\nRight-click to scrap.";
            var items = new List<(string, Action)> { ("Scrap this one...", () => AskScrap(new[] { o }, $"one {o.Class}", n)) };
            if (same is { Count: > 1 }) items.Add(($"Scrap all {same.Count} {o.Class}...", () => AskScrap(same, $"{same.Count} x {o.Class}", n)));
            RightClickMenu(b, items.ToArray());
        }
        b.TooltipText = Ui.Wrap(tip);
        if (ghost) b.Modulate = new Color(1, 1, 1, 0.45f);
        return b;
    }

    /// <summary>What the open cell's yards can build, four cards a row; "How many" on the heading's line; the research line under the cards.</summary>
    void InfraBuild(MgWin w, string n, InfraTab tab)
    {
        var fam = FamilyOfTab(tab) ?? YardFamily.Construction;
        var sec = Boxed(new VBoxContainer(), 8);
        var head = Boxed(new HBoxContainer(), 8);
        var title = Heading2("Build");
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        var hm = Look.Words("How many", 12, Look.Soft);
        hm.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(hm);
        head.AddChild(StepButton("−", "One fewer", () => { _buildQty = Math.Max(1, _buildQty - 1); MgRefreshSoon(); }));
        var ql = Look.Words(_buildQty.ToString(), 12, Look.Text);
        ql.CustomMinimumSize = new Vector2(18, 0); ql.HorizontalAlignment = HorizontalAlignment.Center; ql.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(ql);
        head.AddChild(StepButton("+", "One more (up to 20)", () => { _buildQty = Math.Min(20, _buildQty + 1); MgRefreshSoon(); }));
        sec.AddChild(head);

        // a planet with no construction yard gets its buildings from your nearest one, delivered (Paul 2026-11-19)
        var supply = fam == YardFamily.Construction && !HasYard(n, fam) ? SupplierFor(n) : null;
        string yardAt = supply?.Yard;
        List<BuildRow> rows = tab switch
        {
            InfraTab.Construction => BuildRowsFor(n, "Facilities", yardAt, supply?.Days ?? 0),
            InfraTab.Training => BuildRowsFor(n, "Troops"),
            // ships, then fighters, both from the shipyards; what can be ordered first (OrderBy keeps the order within each)
            InfraTab.Shipyard => BuildRowsFor(n, "Ships").Concat(BuildRowsFor(n, "Fighters")).OrderBy(b => b.Usable ? 0 : 1).ToList(),
            _ => BuildRowsFor(n, "Facilities", yardAt, supply?.Days ?? 0).Where(b => InTab(b.Row.Name, tab)).ToList(),
        };
        bool canBuild = HasYard(n, fam) || yardAt != null;
        _shownBuildRows = canBuild ? rows : new();   // the cards shown (read by the playtest)
        if (yardAt != null)
        {
            var from = Look.Words($"No construction yard here: {yardAt} builds it and sends it here ({supply.Value.Days} days on the way, counted in the days below).", 12, Look.Soft);
            from.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            sec.AddChild(from);
        }
        if (!canBuild)
        {
            var why = Look.Words($"This planet has no {FamilyName(fam).ToLowerInvariant()}. "
                + (fam == YardFamily.Construction ? "None of your construction yards can reach it." : "Build one under Construction yards first."), 12, Look.Soft);
            why.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            sec.AddChild(why);
        }
        else if (rows.Count > 0)
        {
            var grid = new GridContainer { Columns = 4, MouseFilter = MouseFilterEnum.Ignore };
            grid.AddThemeConstantOverride("h_separation", 1); grid.AddThemeConstantOverride("v_separation", 1);
            float cardW = MathF.Floor((BodyWidth(w) - 3) / 4f);
            bool hand = Me == Side.Hand;
            foreach (var b in rows) grid.AddChild(BuildCard(n, b, hand, cardW, yardAt));
            sec.AddChild(grid);
        }
        var prev = _into;
        _into = sec;
        try
        {
            var research = ResearchLine(tab switch { InfraTab.Shipyard => ResearchKind.Ship, InfraTab.Training => ResearchKind.Troop, _ => ResearchKind.Facility },
                HasYard(n, fam) && rows.Any(b => b.Why == NotResearched));
            research.AddThemeColorOverride("font_color", Look.Soft);
            research.AddThemeFontSizeOverride("font_size", 12);
            research.AddThemeFontOverride("font", Ui.Grotesk);
        }
        finally { _into = prev; }
        Add(sec);
    }

    static Button StepButton(string glyph, string tip, Action act)
    {
        var b = new Button { Text = glyph, FocusMode = FocusModeEnum.None, TooltipText = tip, CustomMinimumSize = new Vector2(24, 24),
            MouseDefaultCursorShape = CursorShape.PointingHand };
        var clear = new Color(0, 0, 0, 0);
        b.AddThemeStyleboxOverride("normal", Look.Box(clear, Look.Line, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("hover", Look.Box(clear, Look.Dim, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("pressed", Look.Box(clear, Look.Accent, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("hover_pressed", Look.Box(clear, Look.Accent, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeFontOverride("font", Ui.Grotesk);
        b.AddThemeFontSizeOverride("font_size", 14);
        foreach (var c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, Look.Text);
        b.Pressed += act;
        return b;
    }

    /// <summary>
    /// A thing the yards here can build, as a card (Paul's design): its icon, its name, and at the foot its refined cost and the days one
    /// takes here. Grey when it can't be ordered; the reason, when it is not "not researched", in a line of its own. Click: order "How many".
    /// </summary>
    Control BuildCard(string n, BuildRow b, bool hand, float width, string yardAt = null)
    {
        var r = b.Row;
        string tip =
            $"{r.Name}: costs {r.Cost} refined each and {r.Maintenance} upkeep each, about {b.Days} days per unit {(yardAt != null ? $"(built at {yardAt}, then sent here)" : "here")} once the refined is there." +
            (b.Why != null ? $"\n{char.ToUpperInvariant(b.Why[0])}{b.Why[1..]}." : "") + (ClassTip(r.Name) is string ct ? "\n" + ct : "") + (b.Usable ? $"\nClick to order {_buildQty}." : "");
        // the hover card: what it is, its role, its stats and its picture (Paul 2026-11-19); the plain tip stays as the fallback text
        var kind = KindOf(r.Category, r.Name);
        var grid = PixelIcons.Unit(kind, hand, SizeOfClass(r.Name)) ?? PixelIcons.Building(kind) ?? PixelIcons.Building(UnitIcon.Kind.Building);
        var stats = ClassStats(r, b.Days, out string extra);
        string hoverFoot = string.Join("\n", new[] { extra, b.Why != null ? $"{char.ToUpperInvariant(b.Why[0])}{b.Why[1..]}." : null, b.Usable ? $"Click to order {_buildQty}." : null }.Where(x => x != null));
        var btn = new InfoButton { Make = () => UnitInfo.Card(r.Name, KeepNumeral(r.Name), grid, b.Usable ? Ui.You : Look.Text, stats, hoverFoot) };
        btn.FocusMode = FocusModeEnum.None;
        btn.Disabled = !b.Usable;
        btn.MouseDefaultCursorShape = b.Usable ? CursorShape.PointingHand : CursorShape.Arrow;
        btn.TooltipText = Ui.Wrap(tip);
        btn.SetMeta(UiTest.FullName, $"Build {r.Name}");
        btn.AddThemeStyleboxOverride("normal", Look.Box(Look.Bg));
        btn.AddThemeStyleboxOverride("hover", Look.Box(Look.Line));
        btn.AddThemeStyleboxOverride("pressed", Look.Box(Look.Empty));
        btn.AddThemeStyleboxOverride("hover_pressed", Look.Box(Look.Empty));
        btn.AddThemeStyleboxOverride("disabled", Look.Box(Look.Bg));
        btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var ink = b.Usable ? Look.Text : Look.Dim;

        var v = Boxed(new VBoxContainer(), 4);
        v.AddChild(IconPic.Of(grid, 2, ink, !b.Usable));
        var name = Look.Words(KeepNumeral(r.Name), 12, ink, true);
        name.AutowrapMode = TextServer.AutowrapMode.Word;   // between words only: never "Constructio / n"
        name.MaxLinesVisible = 2;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(name);
        v.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        // grey says "not researched" (the line under the cards says so once); any other reason gets its line
        if (b.Why != null && b.Why != NotResearched)
        {
            var why = Look.Words(b.Why, 12, b.Usable ? Ui.Warn : Look.Soft);
            why.AutowrapMode = TextServer.AutowrapMode.Word;
            why.MaxLinesVisible = 2; why.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            v.AddChild(why);
        }
        var foot = Boxed(new HBoxContainer(), 16);
        foot.AddChild(Amount(r.Cost.ToString(), PixelIconData.Bar["refined"], ink, !b.Usable));
        var sep = new ColorRect { Color = ink, CustomMinimumSize = new Vector2(1, 8), MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        foot.AddChild(sep);
        foot.AddChild(Amount(b.Days.ToString(), PixelIconData.Bar["days"], ink, !b.Usable));
        v.AddChild(foot);
        Fill(btn, v, 16, 16, new Vector2(width, CardHeight));
        btn.Pressed += () => { if (yardAt != null) SubmitBuild(yardAt, b.Family, r, _buildQty, n); else SubmitBuild(n, b.Family, r, _buildQty); MgRefreshSoon(); };
        return btn;
    }

    /// <summary>A number and its 16 px icon, 4 px apart: "20 ◇" (refined), "80 ⧗" (days).</summary>
    static Control Amount(string number, string[] icon, Color ink, bool flat)
    {
        var h = Boxed(new HBoxContainer(), 4);
        h.AddChild(Look.Words(number, 12, ink));
        var i = IconPic.Of(icon, 1, ink, flat);
        i.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(i);
        return h;
    }
}

/// <summary>A 2 px bar: the part done in the fill colour, the rest in the track colour.</summary>
public partial class ThinBar : Control
{
    public float Share;
    public Color Fill, Track;

    public ThinBar() { MouseFilter = MouseFilterEnum.Ignore; }

    public override void _Draw()
    {
        float done = Mathf.Round(Size.X * Mathf.Clamp(Share, 0, 1));
        if (done > 0) DrawRect(new Rect2(0, 0, done, Size.Y), Fill);
        if (done < Size.X) DrawRect(new Rect2(done, 0, Size.X - done, Size.Y), Track);
    }
}

/// <summary>The strip's › and ‹: a thin chevron, 7.5 by 15 px, as in Paul's design.</summary>
public partial class Chevron : Control
{
    public bool Right = true;
    public Color Ink = Look.Text;

    public Chevron() { MouseFilter = MouseFilterEnum.Ignore; }

    public override void _Draw()
    {
        var c = Size / 2;
        float dx = Right ? 3.75f : -3.75f;
        DrawPolyline(new[] { c + new Vector2(-dx, -7.5f), c + new Vector2(dx, 0), c + new Vector2(-dx, 7.5f) }, Ink, 1.5f, true);
    }
}
