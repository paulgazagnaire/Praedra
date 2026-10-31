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
/// The sector window (Paul, 2026-10-31): a click on a sector of the map opens every system of that sector as a card, with the planet the
/// Planet Maker draws for it and how the system is doing at a glance (who holds it, support, energy slots, mines and what they dig, garrison,
/// what the yards build, fleets and leaders there). Each card has three links that open the system's own window beside the cards:
/// Buildings (mines, refineries, yards, defences: build and scrap), Fleets (in orbit, yours and the enemy's) and Forces (troops, fighters,
/// leaders, missions). That window is built with the same helpers as the right-hand window (Line, Act, Card, BuildMenu, ScrapRows...), so
/// every order goes through the same code. Systems you don't hold show only your side's picture of them, with its age. Screens only: no rule
/// lives here; every number comes from the core.
/// </summary>
public partial class GameScreen
{
    public enum SecPane { Buildings, Fleets, Forces }

    Control _secLayer;
    PanelContainer _secWin;
    Label _secTitle, _secSub;
    HBoxContainer _secTotals;
    Control _secOwnBar;
    SectorMiniMap _secMini;
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

    const int CardMinWidth = 430;     // a card: the planet on the left, the numbers on the right
    const int CardArt = 150;          // the width of the planet band; the planet is drawn at two screen pixels per dot and cropped
    const int CardGap = 14;
    const int CardMinHeight = 228;    // every card the same height, so the grid reads as one board whatever each one shows
    const int DetailWidth = 480;      // the system's own window, floating over the right of the cards
    const int MonthDays = 30;         // output is shown per 30 days, a span a player can picture

    internal bool SectorViewOpen => _secLayer?.Visible == true;
    internal string SectorViewName => _secName;
    internal string SectorViewFocus => _secDetail?.Visible == true ? _secFocus : null;
    internal SecPane SectorViewPane => _secPane;

    // ================================================================== the frame

    Control BuildSectorView()
    {
        _secOrder = G.Sectors.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        _secLayer = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _secLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var veil = new ColorRect { Color = new Color(0.01f, 0.012f, 0.02f, 0.72f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        veil.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) CloseSectorView(); };
        _secLayer.AddChild(veil);

        _secWin = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var wb = Ui.Box(Ui.Chrome, 14, new Color(1, 1, 1, 0.09f), 1, 0, 0);
        wb.ShadowColor = new Color(0, 0, 0, 0.55f); wb.ShadowSize = 28;
        _secWin.AddThemeStyleboxOverride("panel", wb);
        _secLayer.AddChild(_secWin);
        _secLayer.Resized += LayoutSectorView;

        var v = Ui.VBox(0);
        _secWin.AddChild(v);

        // ---- the head: arrows to step through the sectors, the name, the totals, close
        var headPad = new MarginContainer();
        foreach (var (k, n) in new[] { ("margin_left", 24), ("margin_right", 18), ("margin_top", 14), ("margin_bottom", 12) }) headPad.AddThemeConstantOverride(k, n);
        v.AddChild(headPad);
        var head = Ui.HBox(Ui.Pad);
        headPad.AddChild(head);
        var names = Ui.VBox(2);
        names.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        names.CustomMinimumSize = new Vector2(250, 0);
        _secSub = Ui.Label("", null, Ui.Text3, Ui.Tiny, wrap: false);
        _secSub.AddThemeFontOverride("font", Ui.Caps);
        names.AddChild(_secSub);
        var titleRow = Ui.HBox(10);
        titleRow.AddChild(SecArrow("‹", -1, "The sector before (Left arrow)"));
        _secTitle = Ui.Label("", "Title", Ui.Text, 26, wrap: false);
        _secTitle.AddThemeFontOverride("font", Ui.SemiBold);
        titleRow.AddChild(_secTitle);
        titleRow.AddChild(SecArrow("›", 1, "The sector after (Right arrow)"));
        names.AddChild(titleRow);
        head.AddChild(names);
        head.AddChild(Ui.Grow());
        _secMini = new SectorMiniMap { CustomMinimumSize = new Vector2(260, 56), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _secMini.Picked = n => OpenSystemPane(n, _secPane);
        head.AddChild(_secMini);
        head.AddChild(Ui.Grow());
        _secTotals = Ui.HBox(26);
        _secTotals.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(_secTotals);
        var close = Ui.Button("✕", CloseSectorView, "Close (S or Esc)", "Clock");
        close.SetMeta(UiTest.FullName, "Close the sector window");
        close.CustomMinimumSize = new Vector2(34, 34);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        head.AddChild(close);

        // ---- who holds the sector, as one bar: blue yours, orange the enemy, grey nobody, dark unknown
        var barPad = new MarginContainer();
        barPad.AddThemeConstantOverride("margin_left", 24); barPad.AddThemeConstantOverride("margin_right", 24); barPad.AddThemeConstantOverride("margin_bottom", 2);
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
        foreach (var (k, n) in new[] { ("margin_left", 22), ("margin_right", 22), ("margin_top", 16), ("margin_bottom", 20) }) gridPad.AddThemeConstantOverride(k, n);
        _secScroll.AddChild(gridPad);
        _secGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _secGrid.AddThemeConstantOverride("h_separation", CardGap); _secGrid.AddThemeConstantOverride("v_separation", CardGap);
        gridPad.AddChild(_secGrid);

        _secDetail = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _secDetail.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        _secDetail.OffsetTop = 6; _secDetail.OffsetBottom = -12;
        var db = Ui.Box(Ui.Panel, 12, new Color(1, 1, 1, 0.12f), 1, 0, 0);
        db.ShadowColor = new Color(0, 0, 0, 0.6f); db.ShadowSize = 24; db.ShadowOffset = new Vector2(-6, 0);
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
        return _secLayer;
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

    /// <summary>The window fills the screen less a margin. The cards take as few rows as the width allows, spread evenly (nine systems
    /// make three rows of three on a laptop, two rows of five on a wide screen), so a whole sector is seen without scrolling.</summary>
    void LayoutSectorView()
    {
        if (_secWin == null) return;
        var vp = _secLayer.Size;
        float w = Math.Min(vp.X - 48, 2000), h = Math.Min(vp.Y - 40, 1100);
        _secWin.Position = new Vector2((vp.X - w) / 2, (vp.Y - h) / 2);
        _secWin.Size = new Vector2(w, h);
        _secWin.CustomMinimumSize = new Vector2(w, h);
        int n = Math.Max(1, _secCards.Count);
        int fit = Math.Clamp((int)((w - 44 + CardGap) / (CardMinWidth + CardGap)), 1, 5);
        int rows = (n + fit - 1) / fit;
        _secGrid.Columns = Math.Max(1, (n + rows - 1) / rows);
        PlaceSystemPane();
    }

    /// <summary>The system window takes the place of one column of cards: the right one, or the left one when the card it shows is
    /// on the right, so the card stays in sight beside its window. On a narrow screen (one or two columns) it keeps its own width.</summary>
    void PlaceSystemPane()
    {
        if (_secDetail == null || !_secDetail.Visible) return;
        int cols = _secGrid.Columns;
        float inner = _secWin.Size.X - 44;
        float colW = (inner - (cols - 1) * CardGap) / cols;
        float w = cols >= 3 ? colW + 10 : Math.Min(DetailWidth, _secWin.Size.X - 24);
        int idx = _secFocus != null ? _secCards.Keys.ToList().IndexOf(_secFocus) : -1;
        bool left = cols >= 2 && idx >= 0 && idx % cols == cols - 1;
        _secDetailBody.CustomMinimumSize = new Vector2(w - 42, 0);
        _secDetail.CustomMinimumSize = Vector2.Zero;
        _secDetail.SetAnchorsPreset(left ? LayoutPreset.LeftWide : LayoutPreset.RightWide, true);
        if (left) { _secDetail.OffsetLeft = 17; _secDetail.OffsetRight = 17 + w; }
        else { _secDetail.OffsetLeft = -17 - w; _secDetail.OffsetRight = -17; }
        _secDetail.OffsetTop = 6; _secDetail.OffsetBottom = -12;
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
        if (_secCards.TryGetValue(system, out var card)) Callable.From(() => { if (IsInstanceValid(card)) _secScroll.EnsureControlVisible(card); }).CallDeferred();
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
        if (_secDirty && _secTimer <= 0) { _secDirty = false; _secTimer = 0.25; RebuildSectorView(); }
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
        SectorTotals(systems);

        // ---- the little map: where each system lies in the sector, in its owner's colour
        _secMini.Dots = sec.Members.Select(m => G.Systems[m]).Select(x =>
        {
            bool dead = S.IsDestroyed(x.Name), mine = !dead && S.OwnerOf(x.Name) == Me;
            var (known, owner) = S.KnownOwnerOf(x.Name);
            bool seen = mine || S.Picture(x.Name) != null;
            var c = dead ? Ui.Danger : mine ? Ui.You : !seen || !known ? Ui.Text3 : Ui.OwnerColor(owner, Me);
            return new SectorMiniMap.Dot(x.Name, new Vector2(x.MapX, x.MapY), c, x.Name == S.MyHqSystem || x.Name == S.EnemyHqSystem, seen);
        }).ToList();
        _secMini.Focus = _secDetail.Visible ? _secFocus : null;
        _secMini.QueueRedraw();

        // ---- cards
        Ui.Clear(_secGrid);
        _secCards.Clear();
        foreach (var n in systems) _secGrid.AddChild(_secCards[n] = SystemCardTile(n));
        LayoutSectorView();

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

    /// <summary>The sector at a glance, on the right of the head: what you hold, what it gives, what is wrong.</summary>
    void SectorTotals(List<string> systems)
    {
        Ui.Clear(_secTotals);
        Ui.Clear(_secOwnBar);
        var eco = S.Host.Economy;
        int mine = 0, enemy = 0, nobody = 0, unknown = 0, dead = 0, supSum = 0, eUsed = 0, eAll = 0, raw = 0, shortTroops = 0, idle = 0, myFleets = 0, theirFleets = 0;
        foreach (var n in systems)
        {
            if (S.IsDestroyed(n)) { dead++; continue; }
            var (known, owner) = S.KnownOwnerOf(n);
            if (S.OwnerOf(n) == Me)
            {
                mine++;
                supSum += eco.SupportOf(n, Me);
                eUsed += eco.EnergyUsed(n); eAll += eco.SlotsOf(n).Energy;
                raw += RawPerMonth(n);
                int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
                if (have < need) shortTroops += need - have;
                idle += IdleYards(n);
            }
            else if (S.Picture(n) == null || !known) unknown++;
            else if (owner == null) nobody++;
            else enemy++;
            myFleets += S.Game.State.Fleets.Values.Count(f => f.Side == Me && f.At == n);
            theirFleets += S.Sees(n) ? S.VisibleEnemyFleetsAt(n).Count : 0;
        }
        void Seg(int count, Color c) { if (count <= 0) return; var r = new ColorRect { Color = c, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = count, MouseFilter = MouseFilterEnum.Ignore }; _secOwnBar.AddChild(r); }
        Seg(mine, Ui.You); Seg(enemy, Ui.Enemy); Seg(nobody, new Color(Ui.Neutral, 0.55f)); Seg(unknown, Ui.LineHi); Seg(dead, new Color(Ui.Danger, 0.45f));
        _secOwnBar.TooltipText = "Who holds the sector: blue yours, orange the enemy, grey nobody, dark not known, red destroyed.";

        Total("YOURS", $"{mine}/{systems.Count}", mine > 0 ? Ui.You : Ui.Text2, "Systems you hold in this sector, of all its systems.");
        if (enemy > 0) Total("ENEMY", enemy.ToString(), Ui.Enemy, "Systems you know the enemy holds here.");
        if (unknown > 0) Total("UNKNOWN", unknown.ToString(), Ui.Text3, "Systems you have never looked at, or whose owner you don't know.");
        if (mine > 0)
        {
            Total("SUPPORT", $"{supSum / mine}%", supSum / mine < 40 ? Ui.Enemy : supSum / mine >= 60 ? Ui.You : Ui.Text, "Average support for you over the systems you hold here.");
            Total("ENERGY FREE", $"{eAll - eUsed}/{eAll}", Ui.Text, "Energy: building slots. Free slots of all slots, over your systems here. Every building takes one.");
            Total("RAW / 30 D", $"+{raw}", raw > 0 ? Ui.Good : Ui.Text3, "About how much raw material the mines of your systems here dig every 30 days, at today's support.");
        }
        if (shortTroops > 0) Total("TROOPS SHORT", shortTroops.ToString(), Ui.Warn, "Regiments missing over your systems here: low support needs troops to keep order.");
        if (idle > 0) Total("IDLE YARDS", idle.ToString(), Ui.Warn, "Yards in your systems here with nothing to build.");
        Total("FLEETS", theirFleets > 0 ? $"{myFleets} · {theirFleets}" : myFleets.ToString(), theirFleets > 0 ? Ui.Enemy : myFleets > 0 ? Ui.You : Ui.Text3,
            theirFleets > 0 ? $"Your fleets here: {myFleets}. Enemy fleets you see now: {theirFleets}." : "Your fleets in this sector.");
    }

    void Total(string key, string value, Color color, string tip)
    {
        var b = Ui.VBox(1);
        b.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        b.MouseFilter = MouseFilterEnum.Stop;
        b.TooltipText = Ui.Wrap(tip);
        var k = Ui.Label(key, "StatLabel", wrap: false);
        k.HorizontalAlignment = HorizontalAlignment.Right;
        b.AddChild(k);
        var v = Ui.Label(value, null, color, 20, wrap: false);
        v.AddThemeFontOverride("font", Ui.NumBold);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        b.AddChild(v);
        _secTotals.AddChild(b);
    }
}
