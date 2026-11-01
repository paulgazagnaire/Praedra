using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The sector window (Paul, 2026-10-31; made simple 2026-11-01): a click on a sector of the map opens it on half the screen, beside the
/// map, as one small tile per system: its planet as the Planet Maker draws it, its name, and lines of marks with no numbers (loyalty as one
/// line, energy and raw slots as dots, troops and defences, fleets, a red ring for an uprising). A click on a tile opens the system's own
/// window (Buildings, Fleets, Forces) over the tiles, built with the same helpers as the right-hand window, so every order goes through the
/// same code. Systems you don't hold show only your side's picture of them; one never looked at shows its planet and name only. Screens
/// only: no rule lives here; every number comes from the core.
/// </summary>
public partial class GameScreen
{
    public enum SecPane { Buildings, Fleets, Forces }

    Control _secLayer;
    PanelContainer _secWin;
    Label _secTitle, _secSub;
    Control _secOwnBar, _secLegend;
    ScrollContainer _secScroll;
    GridContainer _secGrid;
    PanelContainer _secDetail;
    VBoxContainer _secDetailHead, _secDetailBody;
    ScrollContainer _secDetailScroll;
    List<string> _secOrder;
    string _secName, _secFocus;
    SecPane _secPane = SecPane.Buildings;
    bool _secDirty;
    double _secTimer;
    readonly Dictionary<string, PanelContainer> _secCards = new(StringComparer.Ordinal);

    const int TileWidth = 152;        // a tile: the planet (128 dots, one pixel each) with a margin
    const int TileHeight = 182;       // every tile the same height, so the board reads as one whatever each shows
    const int CardGap = 10;
    const int GridPad = 16;           // the margin round the tiles
    const int HeadHeight = 76;        // the window's head and the bar under it
    const int LegendHeight = 30;      // the key to the marks at its foot
    const int TopClear = 72;          // the top bar of the screen (goal, day, clock) stays uncovered
    const int DetailWidth = 480;      // the system's own window at its narrowest
    const int MonthDays = 30;         // output is shown per 30 days, a span a player can picture

    internal bool SectorViewOpen => _secLayer?.Visible == true;
    internal string SectorViewName => _secName;
    internal string SectorViewFocus => _secDetail?.Visible == true ? _secFocus : null;
    internal SecPane SectorViewPane => _secPane;

    // ================================================================== the frame

    Control BuildSectorView()
    {
        _secOrder = G.Sectors.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        // no veil: the window takes half the screen and the map stays live beside it (a click on another sector opens that one)
        _secLayer = new Control { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _secLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _secWin = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var wb = Ui.Box(Ui.Chrome, 14, new Color(1, 1, 1, 0.09f), 1, 0, 0);
        wb.ShadowColor = new Color(0, 0, 0, 0.55f); wb.ShadowSize = 28;
        _secWin.AddThemeStyleboxOverride("panel", wb);
        _secLayer.AddChild(_secWin);
        _secLayer.Resized += () => LayoutSectorView();

        var v = Ui.VBox(0);
        _secWin.AddChild(v);

        // ---- the head: the region, arrows to step through the sectors, the name, close
        var headPad = new MarginContainer();
        foreach (var (k, n) in new[] { ("margin_left", 20), ("margin_right", 14), ("margin_top", 10), ("margin_bottom", 8) }) headPad.AddThemeConstantOverride(k, n);
        v.AddChild(headPad);
        var head = Ui.HBox(Ui.Pad);
        headPad.AddChild(head);
        var names = Ui.VBox(2);
        names.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _secSub = Ui.Label("", null, Ui.Text3, Ui.Tiny, wrap: false);
        _secSub.AddThemeFontOverride("font", Ui.Caps);
        names.AddChild(_secSub);
        var titleRow = Ui.HBox(8);
        titleRow.AddChild(SecArrow("‹", -1, "The sector before (Left arrow)"));
        _secTitle = Ui.Label("", "Title", Ui.Text, 22, wrap: false);
        _secTitle.AddThemeFontOverride("font", Ui.SemiBold);
        titleRow.AddChild(_secTitle);
        titleRow.AddChild(SecArrow("›", 1, "The sector after (Right arrow)"));
        names.AddChild(titleRow);
        head.AddChild(names);
        var close = Ui.Button("✕", CloseSectorView, "Close (S or Esc)", "Clock");
        close.SetMeta(UiTest.FullName, "Close the sector window");
        close.CustomMinimumSize = new Vector2(32, 32);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        head.AddChild(close);

        // ---- who holds the sector, as one bar: blue yours, orange the enemy, grey nobody, dark unknown
        var barPad = new MarginContainer();
        barPad.AddThemeConstantOverride("margin_left", 20); barPad.AddThemeConstantOverride("margin_right", 20); barPad.AddThemeConstantOverride("margin_bottom", 2);
        _secOwnBar = Ui.HBox(2);
        _secOwnBar.CustomMinimumSize = new Vector2(0, 3);
        barPad.AddChild(_secOwnBar);
        v.AddChild(barPad);

        // ---- the body: the cards; the system's own window floats over their right side when one is open
        var body = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        v.AddChild(body);
        _secScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _secScroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        body.AddChild(_secScroll);
        var gridPad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (k, n) in new[] { ("margin_left", GridPad), ("margin_right", GridPad), ("margin_top", 14), ("margin_bottom", 10) }) gridPad.AddThemeConstantOverride(k, n);
        _secScroll.AddChild(gridPad);
        _secGrid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        _secGrid.AddThemeConstantOverride("h_separation", CardGap); _secGrid.AddThemeConstantOverride("v_separation", CardGap);
        gridPad.AddChild(_secGrid);

        _secDetail = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _secDetail.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var db = Ui.Box(Ui.Panel, 12, new Color(1, 1, 1, 0.12f), 1, 0, 0);
        db.ShadowColor = new Color(0, 0, 0, 0.6f); db.ShadowSize = 20;
        _secDetail.AddThemeStyleboxOverride("panel", db);
        body.AddChild(_secDetail);
        var dv = Ui.VBox(0);
        _secDetail.AddChild(dv);
        _secDetailHead = Ui.VBox(Ui.Block);
        var dhPad = new MarginContainer();
        foreach (var (k, n) in new[] { ("margin_left", 20), ("margin_right", 16), ("margin_top", 16), ("margin_bottom", 12) }) dhPad.AddThemeConstantOverride(k, n);
        dhPad.AddChild(_secDetailHead);
        dv.AddChild(dhPad);
        _secDetailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        dv.AddChild(_secDetailScroll);
        var dbPad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (k, n) in new[] { ("margin_left", 20), ("margin_right", 20), ("margin_top", 4), ("margin_bottom", 20) }) dbPad.AddThemeConstantOverride(k, n);
        _secDetailScroll.AddChild(dbPad);
        _secDetailBody = Ui.VBox(Ui.Block);
        _secDetailBody.CustomMinimumSize = new Vector2(DetailWidth - 44, 0);
        dbPad.AddChild(_secDetailBody);

        // ---- what the marks mean, one quiet line at the foot
        var legPad = new MarginContainer();
        foreach (var (k, n) in new[] { ("margin_left", 20), ("margin_right", 20), ("margin_top", 2), ("margin_bottom", 12) }) legPad.AddThemeConstantOverride(k, n);
        v.AddChild(legPad);
        _secLegend = Legend();
        legPad.AddChild(_secLegend);
        return _secLayer;
    }

    /// <summary>The key to the marks: loyalty, energy, raw, troops, defences, fleets, trouble.</summary>
    Control Legend()
    {
        var h = Ui.HBox(14);
        h.Alignment = BoxContainer.AlignmentMode.Center;
        h.MouseFilter = MouseFilterEnum.Ignore;
        void Item(Control mark, string word)
        {
            var it = Ui.HBox(5);
            it.MouseFilter = MouseFilterEnum.Ignore;
            mark.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            it.AddChild(mark);
            var l = Ui.Label(word, null, Ui.Text3, 10, wrap: false);
            l.AddThemeFontOverride("font", Ui.Caps);
            l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            it.AddChild(l);
            h.AddChild(it);
        }
        var line = new LoyaltyLine { CustomMinimumSize = new Vector2(22, 4) };
        line.Set(60, Ui.You, Ui.Enemy);
        Item(line, "LOYALTY");
        Item(new SectorMarks().Add(SectorMarks.Shape.Dot, EnergyColor, true).Add(SectorMarks.Shape.Dot, EnergyColor, false), "ENERGY");
        Item(new SectorMarks().Add(SectorMarks.Shape.Dot, Ui.Gold, true).Add(SectorMarks.Shape.Dot, Ui.Gold, false), "RAW");
        Item(new SectorMarks().Add(SectorMarks.Shape.Square, Ui.You, true).Add(SectorMarks.Shape.Diamond, Ui.Text2, true), "TROOPS · DEFENCES");
        Item(new SectorMarks { Side = 8 }.Add(SectorMarks.Shape.Triangle, Ui.You, true).Add(SectorMarks.Shape.Triangle, Ui.Enemy, true), "FLEETS");
        var ring = new Panel { CustomMinimumSize = new Vector2(10, 10), MouseFilter = MouseFilterEnum.Ignore };
        ring.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0, 0, 0, 0), 5, Ui.Danger, 2, 0, 0));
        Item(ring, "UPRISING");
        h.TooltipText = Ui.Wrap("Filled marks are in use or there, hollow ones free. Energy dots are building slots, raw dots the slots mines dig; a hollow red square is a regiment missing to keep order. Point at a tile for its numbers.");
        h.MouseFilter = MouseFilterEnum.Pass;
        return h;
    }

    Button SecArrow(string text, int dir, string tip)
    {
        var b = Ui.Button(text, () => StepSector(dir), tip, "Clock");
        b.SetMeta(UiTest.FullName, dir < 0 ? "Sector before" : "Sector after");
        b.AddThemeFontSizeOverride("font_size", 22);
        b.CustomMinimumSize = new Vector2(34, 34);
        b.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return b;
    }

    /// <summary>The window takes half the screen, on the side away from the sector so the sector stays in sight on the map. The tiles fill
    /// as many columns as fit; the window is as tall as its rows need, up to the screen.</summary>
    void LayoutSectorView(int count = -1)
    {
        if (_secWin == null) return;
        var vp = _secLayer.Size;
        int n = Math.Max(1, count >= 0 ? count : _secCards.Count);
        float w = Math.Max(Math.Min(vp.X / 2 - 24, 900), Math.Min(vp.X - 32, 2 * (TileWidth + CardGap) + 2 * GridPad));
        int cols = Math.Clamp((int)((w - 2 * GridPad + CardGap) / (TileWidth + CardGap)), 1, 6);
        int rows = (n + cols - 1) / cols;
        _secGrid.Columns = cols;
        float content = HeadHeight + 14 + rows * TileHeight + (rows - 1) * CardGap + 10 + LegendHeight;
        // below the top bar (the clock and the goal stay in sight), as tall as the tiles need
        float h = Math.Min(vp.Y - TopClear - 12, Math.Max(content, _secDetail.Visible ? 560 : 0));
        bool left = SectorOnRight();
        _secWin.Position = new Vector2(left ? 16 : vp.X - w - 16, TopClear);
        _secWin.Size = new Vector2(w, h);
        _secWin.CustomMinimumSize = new Vector2(w, h);
        PlaceSystemPane();
    }

    /// <summary>Whether the open sector lies on the right half of the screen (then the window goes left).</summary>
    bool SectorOnRight()
    {
        var sec = G.Sectors.FirstOrDefault(x => x.Name == _secName);
        if (sec == null || sec.Members.Count == 0) return false;
        float x = sec.Members.Average(m => _view.SystemScreen(m).X);
        return x > _view.Size.X / 2;
    }

    /// <summary>The system's own window covers the tiles; closing it shows them again.</summary>
    void PlaceSystemPane()
    {
        if (_secDetail == null || !_secDetail.Visible) return;
        _secDetail.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _secDetail.OffsetLeft = 10; _secDetail.OffsetRight = -10; _secDetail.OffsetTop = 6; _secDetail.OffsetBottom = -4;
        _secDetailBody.CustomMinimumSize = new Vector2(Math.Max(DetailWidth - 44, _secWin.Size.X - 20 - 42), 0);
    }

    // ================================================================== open, close, step

    /// <summary>A click on a sector of the map (or S): open its window.</summary>
    internal void OpenSector(string sector)
    {
        if (sector == null || InMoveMode || !_secOrder.Contains(sector)) return;
        if (_secName != sector) { _secDetail.Visible = false; _secFocus = null; }
        _secName = sector;
        if (!_secLayer.Visible)
        {
            _secLayer.Modulate = new Color(1, 1, 1, 0);
            _secLayer.CreateTween().TweenProperty(_secLayer, "modulate:a", 1f, 0.14f);
        }
        _secLayer.Visible = true;
        _hoverCard.Visible = false;
        LayoutSectorView();
        RebuildSectorView();
        _secScroll.ScrollVertical = 0;
    }

    void CloseSectorView()
    {
        if (_secLayer == null || !_secLayer.Visible) return;
        _secLayer.Visible = false;
        _secDetail.Visible = false;
        _secFocus = null;
    }

    /// <summary>S: open the sector of the selected system (or of the headquarters), or close the window.</summary>
    void ToggleSectorView()
    {
        if (SectorViewOpen) { CloseSectorView(); return; }
        string at = _view.Selected >= 0 ? G.Systems[_view.Selected].Name : S.MyHqSystem ?? GameBoot.HandCapital;
        OpenSector(SectorOf(at) ?? _secOrder.FirstOrDefault());
    }

    void StepSector(int dir)
    {
        if (_secName == null) return;
        int i = _secOrder.IndexOf(_secName);
        OpenSector(_secOrder[((i + dir) % _secOrder.Count + _secOrder.Count) % _secOrder.Count]);
    }

    internal string SectorOf(string system) =>
        G.TryFindSystem(system, out int i) && !G.Systems[i].IsOutsideSectors ? G.Systems[i].Sector : null;

    /// <summary>Open a system's own window beside the cards, on one of its three panes.</summary>
    internal void OpenSystemPane(string system, SecPane pane)
    {
        _secFocus = system; _secPane = pane;
        _secDetail.Visible = true;
        PlaceSystemPane();
        _secDetailScroll.ScrollVertical = 0;
        RebuildSectorView();
        // look the card up when it runs: a rebuild in between replaces it, and the old one is no longer under the scroll
        Callable.From(() => { if (_secCards.TryGetValue(system, out var card) && IsInstanceValid(card) && _secScroll.IsAncestorOf(card)) _secScroll.EnsureControlVisible(card); }).CallDeferred();
    }

    void CloseSystemPane()
    {
        _secDetail.Visible = false; _secFocus = null;
        RebuildSectorView();
    }

    Action _secLater;
    /// <summary>Run after the input event being handled: a click on a card or a link rebuilds that card, which must not happen inside its own
    /// event. The next frame runs it.</summary>
    void Later(Action a) => _secLater = a;

    /// <summary>Something changed (a new day, an order): rebuild the window soon, at most a few times a second while the clock runs.</summary>
    void RefreshSectorView() { if (SectorViewOpen) _secDirty = true; }

    void TickSectorView(double delta)
    {
        if (_secLater is { } later) { _secLater = null; later(); }
        if (!SectorViewOpen) return;
        if (InMoveMode) { CloseSectorView(); return; } // a move picks its target on the map
        _secTimer -= delta;
        // not while a mouse button is held: a click pressed before a new day would land on a card that no longer exists
        if (_secDirty && _secTimer <= 0 && !Input.IsMouseButtonPressed(MouseButton.Left) && !Input.IsMouseButtonPressed(MouseButton.Right))
        { _secDirty = false; _secTimer = 0.25; RebuildSectorView(); }
    }

    // ================================================================== the content

    void RebuildSectorView()
    {
        var sec = G.Sectors.FirstOrDefault(s => s.Name == _secName);
        if (sec == null) return;
        float keep = _secDetailScroll.ScrollVertical;
        var systems = sec.Members.Select(m => G.Systems[m].Name)
            .OrderBy(n => n == S.MyHqSystem ? 0 : S.OwnerOf(n) == Me ? 1 : S.KnownOwnerOf(n).Owner is Side ? 2 : S.Picture(n) != null ? 3 : 4)
            .ThenBy(n => n, StringComparer.Ordinal).ToList();

        // ---- head
        string region = sec.Group switch { "heartland" => "Heartland", "midreach" => "Midreach", "farreach" => "Farreach", var gname => gname };
        _secSub.Text = $"{region.ToUpperInvariant()}  ·  SECTOR {_secOrder.IndexOf(_secName) + 1} OF {_secOrder.Count}";
        _secTitle.Text = _secName;
        SectorOwnBar(systems);

        // ---- tiles
        _secDirty = false;
        Ui.Clear(_secGrid);
        _secCards.Clear();
        foreach (var n in systems) _secGrid.AddChild(_secCards[n] = SystemCardTile(n));
        LayoutSectorView();
        _secScroll.Visible = !_secDetail.Visible;

        // ---- the system's own window
        Ui.Clear(_secDetailHead);
        Ui.Clear(_secDetailBody);
        if (_secDetail.Visible && _secFocus != null)
        {
            DetailHead(_secFocus);
            var prev = _into;
            _into = _secDetailBody;
            try
            {
                switch (_secPane)
                {
                    case SecPane.Fleets: PaneFleets(_secFocus); break;
                    case SecPane.Forces: PaneForces(_secFocus); break;
                    default: PaneBuildings(_secFocus); break;
                }
            }
            finally { _into = prev; }
            Callable.From(() => _secDetailScroll.ScrollVertical = (int)keep).CallDeferred();
        }
    }

    /// <summary>Who holds the sector, as one bar under the head: blue yours, orange the enemy, grey nobody, dark not known.</summary>
    void SectorOwnBar(List<string> systems)
    {
        Ui.Clear(_secOwnBar);
        int mine = 0, enemy = 0, nobody = 0, unknown = 0, dead = 0;
        foreach (var n in systems)
        {
            if (S.IsDestroyed(n)) { dead++; continue; }
            var (known, owner) = S.KnownOwnerOf(n);
            if (S.OwnerOf(n) == Me) mine++;
            else if (S.Picture(n) == null || !known) unknown++;
            else if (owner == null) nobody++;
            else enemy++;
        }
        void Seg(int count, Color c) { if (count <= 0) return; var r = new ColorRect { Color = c, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = count, MouseFilter = MouseFilterEnum.Ignore }; _secOwnBar.AddChild(r); }
        Seg(mine, Ui.You); Seg(enemy, Ui.Enemy); Seg(nobody, new Color(Ui.Neutral, 0.55f)); Seg(unknown, Ui.LineHi); Seg(dead, new Color(Ui.Danger, 0.45f));
        _secOwnBar.TooltipText = $"Who holds the sector: {mine} yours (blue), {enemy} the enemy's (orange), {nobody} nobody's (grey), {unknown} not known (dark){(dead > 0 ? $", {dead} harvested (red)" : "")}.";
        _secOwnBar.MouseFilter = MouseFilterEnum.Pass;
    }
}
