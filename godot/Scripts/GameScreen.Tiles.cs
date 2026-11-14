using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The pieces of the management windows (Paul, 2026-11-03: "Right now, the interface is not very usable or visual", with Classic's windows as
/// the example): tabs, unit tiles with a picture, identical units stacked as one tile with a count, and a right-click menu. Screens only.
/// </summary>
public partial class GameScreen
{
    const int TileW = 92;
    const int TileH = 96;

    // ================================================================== tabs

    /// <summary>
    /// Tabs: a row of buttons and one page each. Every page is built (so every drop place exists), only the chosen one shows. While a unit is
    /// dragged over a tab, the tab opens, so a drop can reach a page that was hidden. A system's tabs sit in the window's head, a fleet's
    /// over its detail column.
    /// </summary>
    void Tabs(MgWin w, VBoxContainer into, params (string Name, int? Count, Action Build)[] tabs)
    {
        if (w.Tab == null || !tabs.Any(t => t.Name == w.Tab)) w.Tab = tabs[0].Name;
        var bar = new HFlowContainer();   // wraps rather than widening a narrow column
        bar.AddThemeConstantOverride("h_separation", 10); bar.AddThemeConstantOverride("v_separation", 4); // Paul's "styles" frame: 10 px apart
        var group = new ButtonGroup();
        var prev = _into;
        foreach (var (name, count, build) in tabs)
        {
            string tabName = name;
            var b = Ui.Chip(count is int c && c > 0 ? $"{name} {c}" : name, () => { w.Tab = tabName; Fit(w); }, null, name == w.Tab, group);
            b.SetMeta(UiTest.FullName, $"Tab {w.Key} {name}");
            b.ThemeTypeVariation = "TabChip";
            b.FocusMode = FocusModeEnum.None;
            // spring-loaded: hovering a dragged unit over a tab opens it
            b.SetDragForwarding(new Callable(),
                Callable.From((Func<Vector2, Variant, bool>)((_, _) =>
                {
                    if (w.Tab != tabName) { w.Tab = tabName; Callable.From(() => { if (IsInstanceValid(w.Panel)) Fit(w); }).CallDeferred(); }
                    return false;
                })), new Callable());
            bar.AddChild(b);
            var page = Ui.VBox(Ui.Block);
            page.SetMeta("page", name);
            into.AddChild(page);
            w.Pages[name] = (page, b);
            _into = page;
            try { build(); } finally { _into = prev; }
        }
        if (into == w.Body) w.Head.AddChild(bar);
        else { into.AddChild(bar); into.MoveChild(bar, 0); }
        foreach (var (name, (page, _)) in w.Pages) page.Visible = name == w.Tab;
    }

    /// <summary>A small caption in capitals, with an optional count on the right.</summary>
    static Control Caption(string word, string right, string tip = null)
    {
        var h = Ui.HBox(6);
        var l = Ui.Label(word.ToUpperInvariant(), null, Ui.Text3, Ui.Tiny, wrap: false);
        l.AddThemeFontOverride("font", Ui.Caps);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.ClipText = true;
        h.AddChild(l);
        if (right != null) { var r = Ui.Label(right, null, Ui.Text3, Ui.Tiny, wrap: false); r.AddThemeFontOverride("font", Ui.Num); h.AddChild(r); }
        if (tip != null) { h.TooltipText = Ui.Wrap(tip); h.MouseFilter = MouseFilterEnum.Pass; }
        return h;
    }

    // ================================================================== tiles

    /// <summary>The picture kind of a unit or building.</summary>
    static UnitIcon.Kind KindOf(ObjectCategory c, string cls) => c switch
    {
        ObjectCategory.CapitalShip => UnitIcon.Kind.Ship,
        ObjectCategory.Fighter => UnitIcon.Kind.Fighter,
        ObjectCategory.Regiment => UnitIcon.Kind.Regiment,
        ObjectCategory.SpecialForce => UnitIcon.Kind.Agent,
        _ => UnitIcon.ForFacility(cls),
    };

    /// <summary>A ship class's size (0 small to 3 huge) from the ship stats (combat-ship-stats.csv size_class).</summary>
    int SizeOfClass(string cls) => cls != null && Fm.Strength.Data.Classes.TryGetValue(cls, out var cc) ? Math.Clamp((int)cc.Size - 1, 0, 3) : 1;

    /// <summary>
    /// A unit as a tile: its picture (or a leader's initials in a ring), its name, a short line under it, and "×N" when it stands for N
    /// identical units. Dim when it can't move; faded when it is a drop shown before the day applies it.
    /// </summary>
    PanelContainer Tile(UnitIcon.Kind? kind, bool hand, int size4, string name, string sub, int count, Color accent, bool ghost = false, string avatar = null)
    {
        bool art = kind != null && avatar == null && UnitArt.Has(name); // a painted picture of the class (art/units/units.json)
        var p = art ? new ArtTile { Cls = name, TooltipText = name } : new PanelContainer();
        p.MouseFilter = MouseFilterEnum.Pass; p.CustomMinimumSize = new Vector2(TileW, TileH);
        // no fill, a thin line: faint blue round what is yours and can move, #3A3A3A round the rest (Paul 2026-11-14)
        var edge = accent == Ui.You ? new Color(Ui.You, ghost ? 0.22f : 0.3f) : Ui.WinLine;
        p.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0, 0, 0, 0), 0, edge, 1, 6, 6));
        var v = Ui.VBox(2);
        v.MouseFilter = MouseFilterEnum.Ignore;
        p.AddChild(v);
        var holder = new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(TileW - 12, 40) };
        if (avatar != null)
        {
            var ring = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(36, 36) };
            // a plain round line in the tile's colour, no fill (Paul 2026-11-14: no fills; portraits stay round)
            ring.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0, 0, 0, 0), 18, accent, 1, 0, 0));
            var bl = Ui.Label(avatar, null, accent, 12, wrap: false);
            bl.AddThemeFontOverride("font", Ui.SemiBold);
            bl.HorizontalAlignment = HorizontalAlignment.Center; bl.VerticalAlignment = VerticalAlignment.Center;
            ring.AddChild(bl);
            var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            center.AddChild(ring);
            holder.AddChild(center);
        }
        else if (art && accent == Ui.You)
        {
            var pic = UnitArt.Rect(UnitArt.Thumb(name));
            pic.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            pic.OffsetLeft = 2; pic.OffsetRight = -2; pic.OffsetTop = 0; pic.OffsetBottom = 0;
            holder.AddChild(pic);
        }
        // a unit that can't move: its drawn icon in the tile's solid colour, not a see-through painting (Paul 2026-11-14); the hover card
        // still shows the painting
        else if (kind is UnitIcon.Kind k)
        {
            var icon = UnitIcon.For(k, hand, accent, size4, Vector2.Zero);
            icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            icon.OffsetLeft = 6; icon.OffsetRight = -6; icon.OffsetTop = 2; icon.OffsetBottom = -2;
            holder.AddChild(icon);
        }
        if (count > 1)
        {
            // the count: plain #909EBD words, no chip (Paul 2026-11-14)
            var badge = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
            badge.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            var bl = Ui.Label($"×{count}", null, Ui.Text, Ui.Tiny, wrap: false);
            bl.AddThemeFontOverride("font", Ui.NumBold);
            badge.AddChild(bl);
            badge.AnchorLeft = 1; badge.AnchorRight = 1;
            badge.GrowHorizontal = GrowDirection.Begin;
            badge.OffsetLeft = 0; badge.OffsetRight = 2; badge.OffsetTop = -2;
            holder.AddChild(badge);
        }
        v.AddChild(holder);
        var t = Ui.Label(name, null, Ui.Text, Ui.Small, wrap: true);
        t.AddThemeFontOverride("font", Ui.Medium);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.MaxLinesVisible = 2;
        t.CustomMinimumSize = new Vector2(TileW - 12, 0);
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(t);
        if (sub != null)
        {
            var s = Ui.Label(sub, null, ghost ? Pending : Ui.Text3, Ui.Tiny, wrap: false);
            s.HorizontalAlignment = HorizontalAlignment.Center;
            s.ClipText = true; s.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            s.CustomMinimumSize = new Vector2(TileW - 12, 0);
            v.AddChild(s);
        }
        if (ghost) p.Modulate = new Color(1, 1, 1, 0.62f);
        return p;
    }

    /// <summary>The tile of whatever a drag carries (for the drag preview and for drops shown before the day applies them).</summary>
    PanelContainer DragTile(Drag d, string sub, int count, bool ghost)
    {
        bool hand = Me == Side.Hand;
        switch (d.Kind)
        {
            case 'L':
                string name = S.CharName(d.Id);
                return Tile(null, hand, 0, name, sub, count, Ui.You, ghost, Initials(name));
            case 'M':
                return Tile(UnitIcon.Kind.Ship, hand, 2, ThingName(d), sub ?? "whole fleet", 1, Ui.You, ghost);
            case 'F':
                return Tile(UnitIcon.Kind.Agent, hand, 1, S.SpecialForceName(d.Id), sub, count, Ui.You, ghost);
            default:
                var o = S.Host.Economy.Find(d.Id);
                return Tile(o == null ? UnitIcon.Kind.Regiment : KindOf(o.Category, o.Class), hand, SizeOfClass(o?.Class), o?.Class ?? "unit", sub, count, Ui.You, ghost);
        }
    }

    /// <summary>
    /// Units as tiles, identical ones (same class, same state, both movable or both not) stacked as one tile with a count. A drag takes one
    /// unit; Shift takes the stack. A right-click offers to scrap. Units already dropped elsewhere today are left out (they show there).
    /// </summary>
    void UnitTiles(HFlowContainer flow, IEnumerable<(Drag? D, EconObject O, string Sub)> units, string system)
    {
        var list = units.Where(u => !(u.D is { } d && _moved.ContainsKey(d.Key)) && !_scrapped.Contains(u.O.Id)).ToList();
        foreach (var g in list.GroupBy(u => (u.O.Class, u.Sub, Can: u.D != null)))
        {
            var objs = g.Select(u => u.O).ToList();
            var drags = g.Where(u => u.D != null).Select(u => u.D.Value).ToList();
            string cls = g.Key.Class;
            int n = objs.Count;
            var tile = Tile(KindOf(objs[0].Category, cls), Me == Side.Hand, SizeOfClass(cls), cls, g.Key.Sub, n, g.Key.Can ? Ui.You : Ui.Text3);   // yours but not movable now: the readable grey, not the greyed-out one
            string tip = (ClassTip(cls) is string ct ? ct + "\n" : "") + (n > 1 ? $"{n} {cls}. " : "");
            if (drags.Count > 0)
            {
                tile.SetMeta(UiTest.FullName, drags[0].Key);
                MakeDraggable(tile, drags, cls);
                tip += n > 1 ? $"Drag to move one; hold Shift as you start the drag to move all {n}." : "Drag it to another window.";
            }
            var scrap = objs.Where(o => o.Side == Me && Scrappable(o)).ToList();
            if (scrap.Count > 0 && system != null)
            {
                tip += "\nRight-click to scrap.";
                var first = scrap[0];
                var items = new List<(string, Action)> { ("Scrap one...", () => AskScrap(new[] { first }, $"one {cls}", system)) };
                if (scrap.Count > 1) items.Add(($"Scrap all {scrap.Count}...", () => AskScrap(scrap, $"{scrap.Count} x {cls}", system)));
                RightClickMenu(tile, items.ToArray());
            }
            tile.TooltipText = Ui.Wrap(tip.Trim());
            flow.AddChild(tile);
        }
    }

    /// <summary>A small menu on right-click (Classic's windows had one on every unit).</summary>
    void RightClickMenu(Control c, params (string Text, Action Do)[] items)
    {
        c.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }) return;
            var pm = new PopupMenu();
            for (int i = 0; i < items.Length; i++) pm.AddItem(items[i].Text, i);
            pm.IdPressed += id => items[(int)id].Do();
            pm.PopupHide += () => pm.QueueFree();
            AddChild(pm);
            pm.Popup(new Rect2I((Vector2I)GetViewport().GetMousePosition(), Vector2I.Zero));
            c.AcceptEvent();
        };
    }

    void MgRefreshSoon() { _mgDirty = true; _mgTimer = 0; }
}

/// <summary>Slots as dots: filled for used, hollow for free (the sector tiles draw them the same way).</summary>
public partial class SlotDots : Control
{
    public int Used, Total;
    public Color Ink = Ui.You;

    public SlotDots() { MouseFilter = MouseFilterEnum.Ignore; }

    public static SlotDots Of(int used, int total, Color ink)
    {
        var d = new SlotDots { Used = used, Total = total, Ink = ink };
        d.CustomMinimumSize = new Vector2(Math.Max(1, total) * 12, 12);
        return d;
    }

    public override void _Draw()
    {
        for (int i = 0; i < Total; i++)
        {
            var c = new Vector2(6 + i * 12, Size.Y / 2);
            if (i < Used) DrawCircle(c, 4f, Ink);
            else DrawArc(c, 3.6f, 0, Mathf.Tau, 16, new Color(Ink, 0.7f), 1.2f, true);
        }
    }
}
