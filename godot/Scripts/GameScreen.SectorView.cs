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
    Control _secLayer;
    PanelContainer _secWin;
    Label _secTitle;
    Control _secLegend;
    ScrollContainer _secScroll;
    GridContainer _secGrid;
    MarginContainer _secGridPad;
    List<string> _secOrder;
    string _secName;
    bool _secDirty, _secLeft;
    double _secTimer;
    readonly Dictionary<string, PanelContainer> _secCards = new(StringComparer.Ordinal);

    const int TileWidth = SystemTile.W;   // a tile as Paul's Paper frame "system-info" draws it: 96 x 133
    const int TileHeight = SystemTile.H;
    const int CardGap = 10;
    const int GridPad = 16;           // the margin round the tiles
    const int TopClear = 72;          // the top bar of the screen (goal, day, clock) stays uncovered
    const int BottomClear = 16;       // windows reach down to the foot of the screen, with the same gap as at the side
    const int MonthDays = 30;         // output is shown per 30 days, a span a player can picture

    internal bool SectorViewOpen => _secLayer?.Visible == true;
    internal string SectorViewName => _secName;

    // ================================================================== the frame

    Control BuildSectorView()
    {
        _secOrder = G.Sectors.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        // no veil: the window takes half the screen and the map stays live beside it (a click on another sector opens that one)
        _secLayer = new Control { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _secLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _secWin = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        // no shadow under the window (Paul 2026-11-11: "Remove the shadows under the windows entirely")
        _secWin.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Chrome, 14, new Color(1, 1, 1, 0.09f), 1, 0, 0));
        _secLayer.AddChild(_secWin);
        _secLayer.Resized += () => LayoutSectorView();

        var v = Ui.VBox(0);
        _secWin.AddChild(v);

        // ---- the head: arrows to step through the sectors, the name, close. No region and sector-number line above the name and no
        // who-holds-the-sector bar under it (Paul 2026-11-11: "Remove the top section", "remove the sector-wide loyalty bar").
        var headPad = new MarginContainer();
        foreach (var (k, n) in new[] { ("margin_left", 20), ("margin_right", 14), ("margin_top", 10), ("margin_bottom", 4) }) headPad.AddThemeConstantOverride(k, n);
        v.AddChild(headPad);
        var head = Ui.HBox(Ui.Pad);
        headPad.AddChild(head);
        var titleRow = Ui.HBox(8);
        titleRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleRow.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        titleRow.AddChild(SecArrow("‹", -1, "The sector before (Left arrow)"));
        _secTitle = Ui.Label("", "Title", Ui.Text, 22, wrap: false);
        _secTitle.AddThemeFontOverride("font", Ui.SemiBold);
        titleRow.AddChild(_secTitle);
        titleRow.AddChild(SecArrow("›", 1, "The sector after (Right arrow)"));
        head.AddChild(titleRow);
        var close = Ui.Button("✕", CloseSectorView, "Close (S or Esc)", "Clock");
        close.SetMeta(UiTest.FullName, "Close the sector window");
        close.CustomMinimumSize = new Vector2(32, 32);
        close.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(close);

        // ---- the body: the cards; the system's own window floats over their right side when one is open
        var body = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        v.AddChild(body);
        _secScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _secScroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        body.AddChild(_secScroll);
        var gridPad = _secGridPad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (k, n) in new[] { ("margin_left", GridPad), ("margin_right", GridPad), ("margin_top", 14), ("margin_bottom", 10) }) gridPad.AddThemeConstantOverride(k, n);
        _secScroll.AddChild(gridPad);
        _secGrid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        _secGrid.AddThemeConstantOverride("h_separation", CardGap); _secGrid.AddThemeConstantOverride("v_separation", CardGap);
        gridPad.AddChild(_secGrid);

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
        // wraps onto a second line in a narrow (three-column) window
        var h = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("h_separation", 14); h.AddThemeConstantOverride("v_separation", 4);
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
        // the key, drawn with the tile's own pixels (Paul's "system-info" frame): a bit of each gauge, the loyalty bar, the four icons
        static PixelMark Steps(Color on, Color free) => new(new Vector2(5, 15), ci =>
        {
            for (int i = 0; i < 4; i++) ci.DrawRect(new Rect2(0, i * 4, 5, 3), i == 0 ? SystemTile.Empty : i == 1 ? free : on);
        });
        static PixelMark Icon(string[] map, Color c) => new(new Vector2(16, 16), ci => SystemTile.DrawIcon(ci, map, Vector2.Zero, c));
        Item(Steps(SystemTile.RawUsed, SystemTile.RawFree), "RESOURCES");
        Item(Steps(SystemTile.EnergyUsed, SystemTile.EnergyFree), "ENERGY");
        Item(new PixelMark(new Vector2(2, 15), ci => { ci.DrawRect(new Rect2(0, 0, 2, 5), new Color(SystemTile.Of(S.Enemy), 0.4f)); ci.DrawRect(new Rect2(0, 5, 2, 10), SystemTile.Of(Me)); }), "LOYALTY");
        Item(Icon(SystemTile.Infrastructure, SystemTile.Of(Me)), "BUILDINGS");
        Item(Icon(SystemTile.Fleet, SystemTile.Of(Me)), "FLEETS");
        Item(Icon(SystemTile.Military, SystemTile.Of(Me)), "TROOPS");
        Item(Icon(Me == Side.Hand ? SystemTile.Hand : SystemTile.Braid, SystemTile.Of(Me)), "HOLDER");
        h.TooltipText = Ui.Wrap("Gauges fill from the bottom: bright steps are in use, dark ones free, grey ones slots the system lacks. Resources are the raw slots and the mines on them; energy the building slots. Loyalty: yours from the bottom, the enemy's faded on top. Green is the Hand, blue the Collective. Icons are lit when there is something there, in the colour of whose it is; red troops = short of the garrison, amber buildings = a yard stands idle. A red ring = uprising or harvester. Point at a tile for its numbers.");
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

    /// <summary>The window is two window columns wide (Paul 2026-11-11: "The sector view window can take 2 collumns so we have space between
    /// the systems"), on the side away from the sector so the sector stays in sight on the map, at most half the screen. The tiles keep their
    /// square layout (nine systems make 3 x 3) and spread out evenly over the width. It runs from under the top bar down to the foot of the
    /// screen (Paul 2026-11-11: "filling all of the available vertical space"), the tiles scrolling inside it when they need more.</summary>
    void LayoutSectorView(int count = -1)
    {
        if (_secWin == null) return;
        var vp = _secLayer.Size;
        int n = Math.Max(1, count >= 0 ? count : _secCards.Count);
        float w = Math.Min(2 * MgWidth + SlotGap, Math.Max(vp.X / 2 - 24, 2 * (TileWidth + CardGap) + 2 * GridPad));
        w = Math.Min(w, vp.X - 32);
        int fit = Math.Clamp((int)((w - 2 * GridPad + CardGap) / (TileWidth + CardGap)), 1, 6);
        int cols = Math.Min(fit, Math.Max(2, (int)Math.Ceiling(Math.Sqrt(n))));
        _secGrid.Columns = cols;
        // the same space between the tiles as on either side of them (the 4 is the scroll bar's room)
        int gap = Math.Max(CardGap, (int)((w - 4 - cols * TileWidth) / (cols + 1)));
        _secGrid.AddThemeConstantOverride("h_separation", gap);
        _secGrid.AddThemeConstantOverride("v_separation", Math.Min(gap, 2 * GridPad));
        _secGridPad.AddThemeConstantOverride("margin_left", gap); _secGridPad.AddThemeConstantOverride("margin_right", gap);
        // below the top bar (the clock and the goal stay in sight), down to the foot of the screen
        float h = vp.Y - TopClear - BottomClear;
        w = Math.Max(w, _secWin.GetCombinedMinimumSize().X); // never wider on screen than placed: the right edge stays on screen
        // on the left, the first two window columns; on the right, the last two, so the other windows line up beside it
        var slots = _mgLayer != null ? Slots() : new List<Vector2>();
        float x = !_secLeft && slots.Count >= 2 && slots[^2].X + w <= vp.X - 8 ? slots[^2].X : _secLeft ? 16 : vp.X - w - 16;
        _secWin.Position = new Vector2(x, TopClear);
        _secWin.Size = new Vector2(w, h);
        _secWin.CustomMinimumSize = new Vector2(w, h);
    }

    /// <summary>Whether the open sector lies on the right half of the screen (then the window goes left).</summary>
    bool SectorOnRight()
    {
        var sec = G.Sectors.FirstOrDefault(x => x.Name == _secName);
        if (sec == null || sec.Members.Count == 0) return false;
        float x = sec.Members.Average(m => _view.SystemScreen(m).X);
        return x > _view.Size.X / 2;
    }

    /// <summary>Move the map so the open sector sits in the middle of the half the window leaves free (QA-71: the map beside the window
    /// must show the sector, also when it was off screen or under the window).</summary>
    void FrameSector()
    {
        var sec = G.Sectors.FirstOrDefault(x => x.Name == _secName);
        if (sec == null) return;
        var win = _secWin.GetGlobalRect();
        var view = _view.GetGlobalRect();
        float l = _secLeft ? win.End.X : view.Position.X, r = _secLeft ? view.End.X : win.Position.X;
        var free = new Rect2(l - view.Position.X, TopClear, r - l, view.Size.Y - TopClear - 90); // under the top bar, above the bottom pieces
        _view.FrameIn(sec.Members, free);
        OnViewChanged();
    }

    // ================================================================== open, close, step

    /// <summary>A click on a sector of the map (or S): open its window.</summary>
    internal void OpenSector(string sector)
    {
        if (sector == null || InMoveMode || !_secOrder.Contains(sector)) return;
        _secName = sector;
        bool opening = !_secLayer.Visible;
        if (opening)
        {
            _secLayer.Modulate = new Color(1, 1, 1, 0);
            _secLayer.CreateTween().TweenProperty(_secLayer, "modulate:a", 1f, 0.14f);
        }
        _secLayer.Visible = true;
        _hoverCard.Visible = false;
        if (opening) _secLeft = SectorOnRight(); // chosen once per opening: stepping sectors or panning never makes the window jump
        // QA-73: on the left the window covers your stocks and the HQ / Fit buttons; hide them rather than leave their edges peeking out
        _bottomLeft.Visible = !_secLeft;
        _view.MarkSector(sector);
        RebuildSectorView();
        FrameSector();
        _secScroll.ScrollVertical = 0;
    }

    void CloseSectorView()
    {
        if (_secLayer == null || !_secLayer.Visible) return;
        _secLayer.Visible = false;
        _bottomLeft.Visible = true;
        _view.MarkSector(null);
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
        var systems = sec.Members.Select(m => G.Systems[m].Name)
            .OrderBy(n => n == S.MyHqSystem ? 0 : S.OwnerOf(n) == Me ? 1 : S.KnownOwnerOf(n).Owner is Side ? 2 : S.Picture(n) != null ? 3 : 4)
            .ThenBy(n => n, StringComparer.Ordinal).ToList();

        // ---- head
        _secTitle.Text = _secName;

        // ---- tiles
        _secDirty = false;
        Ui.Clear(_secGrid);
        _secCards.Clear();
        foreach (var n in systems) _secGrid.AddChild(_secCards[n] = SystemCardTile(n));
        LayoutSectorView();
    }
}
