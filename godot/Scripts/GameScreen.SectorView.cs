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
        var wb = Ui.Box(new Color("0c0c0e"), 14, new Color(1, 1, 1, 0.09f), 1, 0, 0);
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
        var db = Ui.Box(new Color("101114"), 12, new Color(1, 1, 1, 0.12f), 1, 0, 0);
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
        var k = Ui.Label(key, null, Ui.Text3, 11, wrap: false);
        k.AddThemeFontOverride("font", Ui.Caps);
        k.HorizontalAlignment = HorizontalAlignment.Right;
        b.AddChild(k);
        var v = Ui.Label(value, null, color, 20, wrap: false);
        v.AddThemeFontOverride("font", Ui.NumBold);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        b.AddChild(v);
        _secTotals.AddChild(b);
    }

    // ================================================================== one card

    PanelContainer SystemCardTile(string n)
    {
        bool dead = S.IsDestroyed(n);
        bool mine = !dead && S.OwnerOf(n) == Me;
        var pic = S.Picture(n);
        bool explored = mine || pic != null;
        var (known, owner) = S.KnownOwnerOf(n);
        Side? holder = mine ? Me : dead ? null : owner;
        Color oc = dead ? Ui.Danger : !explored || (!known && !mine) ? Ui.Text3 : Ui.OwnerColor(holder, Me);
        bool focused = _secDetail.Visible && _secFocus == n;

        var card = new PanelContainer { CustomMinimumSize = new Vector2(CardMinWidth, CardMinHeight), SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        card.SetMeta(UiTest.FullName, "Sector card " + n);
        StyleBoxFlat Style(bool hover) => Ui.Box(hover ? new Color("17181c") : CardBg, 12, focused ? oc : hover ? new Color(1, 1, 1, 0.24f) : new Color(1, 1, 1, 0.07f), focused ? 2 : 1, 0, 0);
        card.AddThemeStyleboxOverride("panel", Style(false));
        // a card rebuilt under a still mouse (a new day) keeps its hover look
        Callable.From(() => { if (IsInstanceValid(card) && card.IsVisibleInTree() && card.GetGlobalRect().HasPoint(card.GetGlobalMousePosition())) card.AddThemeStyleboxOverride("panel", Style(true)); }).CallDeferred();
        card.MouseEntered += () => { card.AddThemeStyleboxOverride("panel", Style(true)); _secMini.Hot = n; _secMini.QueueRedraw(); };
        card.MouseExited += () => { card.AddThemeStyleboxOverride("panel", Style(false)); if (_secMini.Hot == n) { _secMini.Hot = null; _secMini.QueueRedraw(); } };
        card.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
            // the card is rebuilt by what the click does: do it after this input event is over
            if (mb.DoubleClick) { Later(() => ShowOnMap(n)); return; }
            Later(() => { if (focused) CloseSystemPane(); else OpenSystemPane(n, _secPane); });
        };
        card.TooltipText = Ui.Wrap($"{n}: click for its window (buildings, fleets, forces). Double-click to show it on the map.");
        var h = Ui.HBox(0);
        h.MouseFilter = MouseFilterEnum.Ignore;
        card.AddChild(h);

        // ---- the planet band on the left: the planet large and cropped, the owner's colour as a thin line down its edge
        var art = new Control { CustomMinimumSize = new Vector2(CardArt, 0), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 0.5f };
        h.AddChild(art);
        var sky = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        var skyBox = new StyleBoxFlat { BgColor = new Color("040405") };
        skyBox.CornerRadiusTopLeft = 11; skyBox.CornerRadiusBottomLeft = 11;
        sky.AddThemeStyleboxOverride("panel", skyBox);
        sky.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        art.AddChild(sky);
        if (PlanetArt.Of(n) is Texture2D tex)
        {
            float px = PlanetArt.Dots * 2; // two screen pixels per dot: the dither stays crisp
            var tr = new TextureRect { Texture = tex, TextureFilter = TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
            tr.SetAnchorsPreset(LayoutPreset.Center);
            tr.OffsetLeft = -px / 2; tr.OffsetRight = px / 2; tr.OffsetTop = -px / 2; tr.OffsetBottom = px / 2;
            if (dead) tr.Modulate = new Color(0.6f, 0.22f, 0.2f, 0.5f);
            else if (!explored) tr.Modulate = new Color(0.26f, 0.27f, 0.3f, 1f);
            art.AddChild(tr);
        }
        var fade = new TextureRect { Texture = FadeTexture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        fade.SetAnchorsAndOffsetsPreset(LayoutPreset.RightWide);
        fade.OffsetLeft = -40;
        art.AddChild(fade);
        var stripe = new ColorRect { Color = new Color(oc, mine ? 0.95f : 0.6f), CustomMinimumSize = new Vector2(2, 0), MouseFilter = MouseFilterEnum.Ignore };
        h.AddChild(stripe);

        // ---- the right side: name, owner, the numbers, the three links
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        foreach (var (k, px) in new[] { ("margin_left", 14), ("margin_right", 14), ("margin_top", 11), ("margin_bottom", 11) }) pad.AddThemeConstantOverride(k, px);
        h.AddChild(pad);
        var v = Ui.VBox(6);
        v.MouseFilter = MouseFilterEnum.Ignore;
        pad.AddChild(v);

        var nameRow = Ui.HBox(8);
        nameRow.MouseFilter = MouseFilterEnum.Ignore;
        var nm = Ui.Label(n, null, Ui.Text, 17, wrap: false);
        nm.AddThemeFontOverride("font", Ui.SemiBold);
        nm.ClipText = true;
        nm.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameRow.AddChild(nm);
        string ownWord = dead ? "DESTROYED" : mine ? (n == S.MyHqSystem ? (Me == Side.Hand ? "CAPITAL" : "HQ") : "YOURS") : !explored ? "UNEXPLORED"
            : !known ? "OWNER ?" : owner == null ? "NOBODY'S" : n == S.EnemyHqSystem ? (Me == Side.Hand ? "THEIR HQ" : "THEIR CAPITAL") : "ENEMY";
        nameRow.AddChild(Ui.Tag(ownWord, mine && n == S.MyHqSystem ? Ui.Gold : oc));
        v.AddChild(nameRow);
        var sub = Ui.HBox(6);
        sub.MouseFilter = MouseFilterEnum.Ignore;
        var kl = Ui.Label((PlanetArt.KindOf(n) ?? "").ToUpperInvariant(), null, Ui.Text3, 10, wrap: false);
        kl.AddThemeFontOverride("font", Ui.Caps);
        kl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        kl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        sub.AddChild(kl);
        foreach (var (word, c, tip) in Alarms(n, mine)) { var t = Ui.Tag(word, c, true); t.TooltipText = Ui.Wrap(tip); t.MouseFilter = MouseFilterEnum.Pass; sub.AddChild(t); }
        v.AddChild(sub);
        v.AddChild(Ui.Space(1));

        var st = Ui.VBox(6);
        st.MouseFilter = MouseFilterEnum.Ignore;
        st.SizeFlagsVertical = SizeFlags.ExpandFill;
        v.AddChild(st);
        if (dead) DeadStats(st, n);
        else if (mine) OwnStats(st, n);
        else if (!explored) { var q = Quiet("Never looked at."); q.TooltipText = Ui.Wrap("Send a fleet, or a spy on an Espionage mission, to see who holds it and what is there."); q.MouseFilter = MouseFilterEnum.Pass; st.AddChild(q); }
        else ForeignStats(st, n, pic);

        var links = Ui.HBox(6);
        links.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(links);
        var (bCount, bTip, bColor) = BuildingsLink(n, mine, pic, dead);
        var (fCount, fTip, fColor) = FleetsLink(n, pic);
        var (mCount, mTip, mColor) = ForcesLink(n, mine, pic, dead);
        links.AddChild(CardLink(n, SecPane.Buildings, "BUILDINGS", bCount, bColor, bTip, focused));
        links.AddChild(CardLink(n, SecPane.Fleets, "FLEETS", fCount, fColor, fTip, focused));
        links.AddChild(CardLink(n, SecPane.Forces, "FORCES", mCount, mColor, mTip, focused));
        return card;
    }

    static readonly Color CardBg = new("121316");
    static Texture2D _fade;
    /// <summary>A fade from clear to the card's ground at the right edge of the planet band, so the planet melts into the card.</summary>
    static Texture2D FadeTexture => _fade ??= new GradientTexture2D
    {
        Gradient = new Gradient { Colors = new[] { new Color("121316", 0f), new Color("121316", 1f) }, Offsets = new[] { 0f, 1f } },
        FillFrom = new Vector2(0, 0), FillTo = new Vector2(1, 0), Width = 64, Height = 4,
    };

    Label Quiet(string text)
    {
        var l = Ui.Label(text, null, Ui.Text3, Ui.Small);
        return l;
    }

    /// <summary>The words that must jump out of a card: uprising, blockade, an enemy fleet, a harvester.</summary>
    List<(string, Color, string)> Alarms(string n, bool mine)
    {
        var a = new List<(string, Color, string)>();
        if (S.IsDestroyed(n)) return a;
        bool sees = S.Sees(n);
        if (sees && S.VisibleEnemyFleetsAt(n).Count > 0) a.Add(("ENEMY FLEET", Ui.Danger, $"Enemy fleet here now: {string.Join("; ", S.VisibleEnemyFleetsAt(n))}"));
        if (sees && S.Host.Support.IsUprising(n)) a.Add(("UPRISING", Ui.Danger, "The people are in revolt: yards and mines stop until it ends."));
        if (sees && S.Game.State.Blockades.TryGetValue(n, out var b)) a.Add(("BLOCKADE", b == Me ? Ui.You : Ui.Warn, b == Me ? "Your fleet blockades it: its mines and yards stop." : "Blockaded by the enemy: mines and yards here stop."));
        if (!mine && S.Picture(n)?.HarvesterQueued is { Value: true }) a.Add(("HARVESTER", Ui.Danger, "A harvester was being built here when you last looked."));
        return a;
    }

    void OwnStats(VBoxContainer st, string n)
    {
        var eco = S.Host.Economy;
        int sup = eco.SupportOf(n, Me);
        Color sc = sup < 40 ? Ui.Enemy : sup >= 60 ? Ui.You : Ui.Text;
        st.AddChild(Meter("SUPPORT", $"{sup}%", sc, sup / 100f, sc, $"How much the people here back you (the {S.NameOf(S.Enemy)} has {100 - sup}%). Low support slows mines and yards, needs troops, and below a point the planet may go over."));
        var slots = eco.SlotsOf(n);
        int used = eco.EnergyUsed(n);
        st.AddChild(Pips("ENERGY", used, slots.Energy, Ui.You, $"Energy: the building slots of this system. {slots.Energy - used} free of {slots.Energy}; every building takes one."));
        int mines = eco.OfKindAt(n, FacilityKind.Mine), rawSlots = eco.EffectiveRaw(n);
        st.AddChild(Pips("RAW", mines, rawSlots, Ui.Gold, $"Raw material slots, where mines can dig: {mines} mines on {rawSlots} slots. A mine takes one raw slot and one energy slot."));
        int raw = RawPerMonth(n), refs = eco.OfKindAt(n, FacilityKind.Refinery), refined = RefinedPerMonth(n);
        int smug = mines + refs > 0 ? eco.DiversionPercent(n, Me) : 0;
        st.AddChild(Pair("OUTPUT / 30 D", raw == 0 && refined == 0 ? "–" : $"+{raw} raw  +{refined} refined", smug > 0 ? Ui.Warn : raw + refined > 0 ? Ui.Good : Ui.Text3,
            $"Every 30 days at today's support, about: {raw} raw dug by {Ui.Count(mines, "mine", "mines")}, {refined} refined made by {Ui.Count(refs, "refinery", "refineries")} (when there is raw to refine). Both go to your side's pools."
            + (smug > 0 ? $" Smugglers may take a cycle's output {smug}% of the time here: troops, fighters and ships on the planet scare them off." : "")));
        int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
        var two = Ui.HBox(18);
        two.MouseFilter = MouseFilterEnum.Ignore;
        var g = Pair("TROOPS", need == 0 ? $"{have}" : $"{have}/{need}", have < need ? Ui.Warn : Ui.Text,
            need == 0 ? $"{Ui.Count(have, "regiment", "regiments")} here; none needed at this support." : $"{have} regiments of the {need} needed to keep order. Below 60% support a system needs one regiment for every 10 points under 60.");
        g.SizeFlagsHorizontal = SizeFlags.ExpandFill; g.SizeFlagsStretchRatio = 0.7f;
        two.AddChild(g);
        var (what, wc) = YardsWords(n);
        var y = Pair("YARDS", what, wc, "What the yards here build now, and how many days the current one still needs.");
        y.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        two.AddChild(y);
        st.AddChild(two);
    }

    void ForeignStats(VBoxContainer st, string n, SystemPicture pic)
    {
        bool live = S.Sees(n);
        int? last = S.LastSeenDay(n);
        int age = last is int l ? S.Game.Day - l : int.MaxValue;
        string when = live ? "seen live now" : last is int ld ? $"last seen day {ld} ({Ui.DaysAgo(age)})" : "not seen since the war began";
        var seen = Ui.Label(when.ToUpperInvariant(), null, live ? Ui.Good : age > 10 ? Ui.Warn : Ui.Text3, 11, wrap: false);
        seen.AddThemeFontOverride("font", Ui.Caps);
        st.AddChild(seen);
        if (pic.Owner is { } po)
        {
            int s = Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport;
            st.AddChild(Meter("SUPPORT FOR YOU", $"{s}%", Ui.Text2, s / 100f, new Color(Ui.Text2, 0.7f), $"What the people thought of you on day {po.Stamp}. A Diplomacy mission raises it."));
        }
        else st.AddChild(Pair("SUPPORT FOR YOU", "?", Ui.Text3, "Not known."));
        if (pic.Status is { } ss) st.AddChild(Pair("SLOTS", $"{ss.Value.Energy} energy · {ss.Value.Raw} raw", Ui.Text2, $"Seen day {ss.Stamp}. {(ss.Value.Populated ? "Populated." : "Nobody lives here.")}"));
        if (pic.Facilities is { } fac) st.AddChild(Pair("BUILDINGS", fac.Value.Values.Sum() == 0 ? "none" : fac.Value.Values.Sum().ToString(), Ui.Text2,
            fac.Value.Count == 0 ? $"None seen (day {fac.Stamp})." : $"Seen day {fac.Stamp}: " + string.Join(", ", fac.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Value} x {kv.Key}"))));
        else st.AddChild(Pair("BUILDINGS", "?", Ui.Text3, "Nobody has looked at the ground yet."));
        if (pic.Ground is { } gr)
        {
            int t = gr.Value.Where(g => g.Side != Me).Sum(g => g.Total);
            st.AddChild(Pair("TROOPS", t.ToString(), t > 0 ? Ui.Enemy : Ui.Text2, $"Regiments of others seen on the ground, day {gr.Stamp}."));
        }
        else st.AddChild(Pair("TROOPS", "?", Ui.Text3, "Not known."));
    }

    void DeadStats(VBoxContainer st, string n)
    {
        string d = S.DestroyedDay(n) is int day ? $"on day {day}" : "";
        st.AddChild(Ui.Label($"Harvested {d}. Nothing is left: no people, no buildings, no troops. Fleets can still pass and stop here.".Replace("  ", " "), null, new Color("ffb3b3"), Ui.Small));
    }

    /// <summary>A key, a value and a bar under them.</summary>
    Control Meter(string key, string value, Color valueColor, float share, Color fill, string tip)
    {
        var v = Ui.VBox(5);
        v.AddChild(Pair(key, value, valueColor, tip));
        v.AddChild(Ui.Bar(share, fill, 4, new Color(1, 1, 1, 0.06f)));
        v.MouseFilter = MouseFilterEnum.Ignore;
        return v;
    }

    /// <summary>A key on the left, a value on the right, both one line.</summary>
    HBoxContainer Pair(string key, string value, Color valueColor, string tip)
    {
        var h = Ui.HBox(8);
        h.MouseFilter = MouseFilterEnum.Pass;
        h.TooltipText = Ui.Wrap(tip);
        var k = Ui.Label(key, null, Ui.Text3, 11, wrap: false);
        k.AddThemeFontOverride("font", Ui.Caps);
        k.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        k.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        k.ClipText = true;
        h.AddChild(k);
        var val = Ui.Label(value, null, valueColor, 13, wrap: false);
        val.AddThemeFontOverride("font", Ui.NumBold);
        val.HorizontalAlignment = HorizontalAlignment.Right;
        h.AddChild(val);
        return h;
    }

    /// <summary>Slots as a row of small blocks: filled for used, hollow for free (up to 15 slots, the slot cap).</summary>
    Control Pips(string key, int used, int total, Color c, string tip)
    {
        var h = Ui.HBox(8);
        h.MouseFilter = MouseFilterEnum.Pass;
        h.TooltipText = Ui.Wrap(tip);
        var k = Ui.Label(key, null, Ui.Text3, 11, wrap: false);
        k.AddThemeFontOverride("font", Ui.Caps);
        k.CustomMinimumSize = new Vector2(52, 0);
        k.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(k);
        var pips = Ui.HBox(2);
        pips.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        pips.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        pips.MouseFilter = MouseFilterEnum.Ignore;
        for (int i = 0; i < Math.Min(total, 15); i++)
        {
            var p = new Panel { CustomMinimumSize = new Vector2(5, 10), MouseFilter = MouseFilterEnum.Ignore };
            p.AddThemeStyleboxOverride("panel", i < used ? Ui.Box(c, 1, null, 0, 0, 0) : Ui.Box(new Color(1, 1, 1, 0.09f), 1, null, 0, 0, 0));
            pips.AddChild(p);
        }
        h.AddChild(pips);
        int free = Math.Max(0, total - used);
        var val = Ui.Label(total == 0 ? "none" : $"{free} free / {total}", null, total == 0 || free == 0 ? Ui.Text3 : Ui.Text, 13, wrap: false);
        val.AddThemeFontOverride("font", Ui.NumBold);
        h.AddChild(val);
        return h;
    }

    Button CardLink(string n, SecPane pane, string word, string count, Color color, string tip, bool focused)
    {
        bool on = focused && _secPane == pane;
        var b = new Button { FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 28) };
        b.TooltipText = Ui.Wrap(tip);
        b.SetMeta(UiTest.FullName, $"{n} {word.ToLowerInvariant()}");
        b.AddThemeStyleboxOverride("normal", Ui.Box(on ? new Color(color, 0.16f) : new Color(1, 1, 1, 0.025f), 8, on ? color : new Color(1, 1, 1, 0.07f), 1, 4, 4));
        b.AddThemeStyleboxOverride("hover", Ui.Box(new Color(color, 0.12f), 8, new Color(color, 0.7f), 1, 4, 4));
        b.AddThemeStyleboxOverride("pressed", Ui.Box(new Color(color, 0.2f), 8, color, 1, 4, 4));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.Pressed += () => Later(() => { if (on) CloseSystemPane(); else OpenSystemPane(n, pane); });
        var row = Ui.HBox(6);
        row.MouseFilter = MouseFilterEnum.Ignore;
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        b.AddChild(row);
        var w = Ui.Label(word, null, on ? Ui.Text : Ui.Text2, 10, wrap: false);
        w.AddThemeFontOverride("font", Ui.Caps);
        w.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(w);
        var c = Ui.Label(count, null, color, 13, wrap: false);
        c.AddThemeFontOverride("font", Ui.NumBold);
        c.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(c);
        return b;
    }

    (string, string, Color) BuildingsLink(string n, bool mine, SystemPicture pic, bool dead)
    {
        if (dead) return ("–", "Nothing is left here.", Ui.Text3);
        if (mine)
        {
            int c = S.Host.Economy.Objects.Count(o => o.Category == ObjectCategory.Facility && o.System == n && o.Completed);
            int idle = IdleYards(n);
            return (c.ToString(), $"{Ui.Count(c, "building", "buildings")} here{(idle > 0 ? $", {Ui.Count(idle, "idle yard", "idle yards")}" : "")}. Click to build, scrap and see what each one does.", idle > 0 ? Ui.Warn : Ui.Text);
        }
        if (pic?.Facilities is { } f) return (f.Value.Values.Sum().ToString(), $"Buildings seen on day {f.Stamp}. Click for the list.", Ui.Text2);
        return ("?", "Not known.", Ui.Text3);
    }

    (string, string, Color) FleetsLink(string n, SystemPicture pic)
    {
        int own = S.Game.State.Fleets.Values.Count(f => f.Side == Me && f.At == n);
        int enemy = S.Sees(n) ? S.VisibleEnemyFleetsAt(n).Count : 0;
        int seen = !S.Sees(n) && pic?.Fleets is { } pf ? pf.Value.Count : 0;
        if (enemy > 0) return (own > 0 ? $"{own}·{enemy}" : enemy.ToString(), $"Your fleets here: {own}. Enemy fleets here now: {enemy}. Click to see them.", Ui.Danger);
        if (own > 0) return (own.ToString(), $"{Ui.Count(own, "fleet", "fleets")} of yours in orbit. Click to see and command them.", Ui.You);
        if (seen > 0) return (seen.ToString(), $"Enemy fleets seen here when you last looked: {seen}.", Ui.Enemy);
        return ("0", S.Sees(n) ? "No fleet in orbit." : "None of yours; you don't see enemy fleets here.", Ui.Text3);
    }

    (string, string, Color) ForcesLink(string n, bool mine, SystemPicture pic, bool dead)
    {
        int leaders = S.MyCharacters.Count(c => c.Status != Praedra.Core.Characters.CharacterStatus.Dead && Cm.LocationOf(c) == n);
        if (dead) return (leaders > 0 ? leaders.ToString() : "–", "Nothing is left here.", Ui.Text3);
        if (mine)
        {
            int regs = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n), fighters = FightersBasedAt(n).Count;
            int total = regs + fighters + leaders;
            return (total.ToString(), $"{Ui.Count(regs, "regiment", "regiments")}, {Ui.Count(fighters, "fighter squadron", "fighter squadrons")} based here, {Ui.Count(leaders, "leader", "leaders")}. Click to train troops and send missions.",
                regs < need ? Ui.Warn : total > 0 ? Ui.Text : Ui.Text3);
        }
        int theirs = pic?.Ground is { } gr ? gr.Value.Where(g => g.Side != Me).Sum(g => g.Total) : -1;
        string count = leaders > 0 ? $"{leaders}" : theirs >= 0 ? theirs.ToString() : "?";
        return (count, leaders > 0 ? $"{Ui.Count(leaders, "leader", "leaders")} of yours here." : theirs >= 0 ? $"Troops of others seen: {theirs}." : "Not known.", leaders > 0 ? Ui.You : theirs > 0 ? Ui.Enemy : Ui.Text3);
    }

    // ================================================================== numbers the cards share

    /// <summary>Yards here with nothing to build.</summary>
    int IdleYards(string n) => new[] { Praedra.Core.Construction.YardFamily.Shipyard, Praedra.Core.Construction.YardFamily.Construction, Praedra.Core.Construction.YardFamily.Training }
        .Count(f => HasYard(n, f) && S.Host.Construction.ProjectAt(n, f) is not { Units.Count: > 0 });

    (string, Color) YardsWords(string n)
    {
        var eco = S.Host.Economy;
        var parts = new List<string>();
        bool idle = false;
        foreach (var fam in new[] { Praedra.Core.Construction.YardFamily.Shipyard, Praedra.Core.Construction.YardFamily.Construction, Praedra.Core.Construction.YardFamily.Training })
        {
            if (!HasYard(n, fam)) continue;
            var p = S.Host.Construction.ProjectAt(n, fam);
            if (p == null || p.Units.Count == 0) { idle = true; continue; }
            parts.Add($"{eco.Find(p.Units[0])?.Class ?? "?"} {DaysLeftOf(n, fam)}d");
        }
        if (parts.Count == 0) return idle ? ("idle", Ui.Warn) : ("none", Ui.Text3);
        return (string.Join(", ", parts) + (idle ? " +idle" : ""), idle ? Ui.Warn : Ui.Text);
    }

    /// <summary>About how much raw the mines here dig in 30 days at today's support: one raw a cycle each, the cycle as the core times it
    /// (rule: m2-mines-refineries-pools §Cycle length). A stopped mine (blockade, uprising) digs nothing.</summary>
    int RawPerMonth(string n) => PerMonth(n, FacilityKind.Mine);

    /// <summary>The same for refineries: one refined a cycle each, when the side has raw to refine.</summary>
    int RefinedPerMonth(string n) => PerMonth(n, FacilityKind.Refinery);

    int PerMonth(string n, FacilityKind kind)
    {
        var eco = S.Host.Economy;
        var p = eco.Params;
        string cls = FacilityKinds.ClassName(kind);
        int sup = Math.Clamp(eco.SupportOf(n, Me), p.SupportFloor, p.SupportCeiling);
        double sum = 0;
        foreach (var o in eco.Objects)
        {
            if (o.Category != ObjectCategory.Facility || !o.Completed || o.System != n || o.Class != cls || eco.IsSuspended(o)) continue;
            int d = Math.Max(1, EconomyModule.CycleDays(p, sup, eco.ShareOf(o.Id)));
            sum += (double)MonthDays / d;
        }
        return (int)Math.Round(sum);
    }

    void ShowOnMap(string n)
    {
        CloseSectorView();
        if (!G.TryFindSystem(n, out int idx)) return;
        _view.Centre(idx);
        _view.Select(idx);
        OnViewChanged();
    }

    // ================================================================== the system's own window

    void DetailHead(string n)
    {
        bool mine = S.OwnerOf(n) == Me;
        var row = Ui.HBox(Ui.Pad);
        _secDetailHead.AddChild(row);
        if (PlanetArt.Of(n) is Texture2D tex)
        {
            var frame = new PanelContainer { CustomMinimumSize = new Vector2(64, 64), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
            frame.AddThemeStyleboxOverride("panel", Ui.Box(new Color("050506"), 8, new Color(1, 1, 1, 0.08f), 1, 0, 0));
            var tr = new TextureRect { Texture = tex, TextureFilter = TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore };
            frame.AddChild(tr);
            row.AddChild(frame);
        }
        var names = Ui.VBox(2);
        names.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var t = Ui.Label(n, "Title", Ui.Text, 22, wrap: false);
        t.ClipText = true;
        names.AddChild(t);
        var (known, owner) = S.KnownOwnerOf(n);
        string who = S.IsDestroyed(n) ? "Destroyed" : mine ? $"Yours · {PlanetArt.KindOf(n) ?? "system"}" : S.Picture(n) == null ? "Unexplored" : !known ? "Owner unknown" : owner == null ? "Nobody's" : $"Held by the {S.NameOf(owner.Value)}";
        names.AddChild(Ui.Label(who, null, S.IsDestroyed(n) ? Ui.Danger : Ui.OwnerColor(mine ? Me : owner, Me), Ui.Small, wrap: false));
        row.AddChild(names);
        row.AddChild(Ui.Grow());
        var map = Ui.Button("Map", () => ShowOnMap(n), "Close this window and show the system on the map (double-click a card does the same)", "Chip");
        map.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(map);
        var close = Ui.Button("✕", CloseSystemPane, "Close the system window", "Clock");
        close.SetMeta(UiTest.FullName, "Close the system window");
        close.CustomMinimumSize = new Vector2(30, 30);
        close.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(close);

        var tabs = Ui.HBox(4);
        var group = new ButtonGroup();
        foreach (var (pane, word) in new[] { (SecPane.Buildings, "Buildings"), (SecPane.Fleets, "Fleets"), (SecPane.Forces, "Forces") })
        {
            var p = pane;
            var b = Ui.Chip(word, () => Later(() => { _secPane = p; _secDetailScroll.ScrollVertical = 0; RebuildSectorView(); }), null, _secPane == pane, group);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.CustomMinimumSize = new Vector2(0, 34);
            tabs.AddChild(b);
        }
        _secDetailHead.AddChild(Ui.Space(2));
        _secDetailHead.AddChild(tabs);
    }

    void PaneBuildings(string n)
    {
        if (S.IsDestroyed(n)) { Note("The planet and everything on it are gone. Nothing can be built here."); return; }
        if (S.OwnerOf(n) != Me)
        {
            var pic = S.Picture(n);
            if (pic == null) { Note("Unexplored: you don't know what is built here. Send a fleet or a spy to look."); return; }
            Heading("Buildings seen");
            if (pic.Facilities is { } fac)
            {
                if (fac.Value.Count == 0) Line($"none (seen day {fac.Stamp})", Dim);
                foreach (var kv in fac.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Stat(kv.Key, kv.Value.ToString());
                Note($"As seen on day {fac.Stamp}. It may have changed since.");
            }
            else Line("unknown (nobody has looked at the ground yet)", Dim);
            if (pic.Status is { } ss) { Heading("Slots"); Stat("Energy slots", ss.Value.Energy.ToString()); Stat("Raw material slots", ss.Value.Raw.ToString()); }
            Note("You can only build on systems you hold.");
            return;
        }
        var eco = S.Host.Economy;
        // what the planet gives: the mines dig raw, the refineries turn raw into refined; both add room for upkeep
        using (Card())
        {
            int mines = eco.OfKindAt(n, FacilityKind.Mine), refs = eco.OfKindAt(n, FacilityKind.Refinery);
            int sup = Math.Clamp(eco.SupportOf(n, Me), eco.Params.SupportFloor, eco.Params.SupportCeiling);
            Stat("Mines", mines == 0 ? "none" : $"{mines}   about +{RawPerMonth(n)} raw / 30 days", RawPerMonth(n) > 0 ? Ui.Good : null,
                "Each mine digs one raw material every cycle. The cycle is longer when support is low and when the mine carries a big share of your upkeep.");
            Stat("Refineries", refs == 0 ? "none" : $"{refs}   about +{RefinedPerMonth(n)} refined / 30 days", RefinedPerMonth(n) > 0 ? Ui.Good : null,
                "Each refinery turns one raw material into one refined every cycle, when your side has raw in store. Refined pays for everything you build.");
            Note($"At {sup}% support a fresh cycle here takes {EconomyModule.CycleDays(eco.Params, sup, 0)} days. Every mine and refinery also adds {eco.Params.CapacityPerFacility} to your upkeep room (with as many of the other kind).");
            int smug = mines + refs > 0 ? eco.DiversionPercent(n, Me) : 0;
            if (smug > 0) Line($"Smugglers may take a cycle's output {smug}% of the time. More support, or troops, fighters and ships here, scare them off.", Ui.Warn, Ui.Small);
            if (S.Game.State.Blockades.ContainsKey(n)) Line("Blockaded: the mines and yards here stop until the enemy fleet is driven off.", Bad, Ui.Small);
            if (S.Host.Support.IsUprising(n)) Line("Uprising: the mines and yards here stop until it ends.", Bad, Ui.Small);
        }
        var slots = eco.SlotsOf(n);
        Stat("Energy slots", $"{eco.EnergyUsed(n)} used of {slots.Energy}, {eco.FreeEnergy(n)} free", null, "Every building takes one energy slot.");
        Stat("Raw material slots", $"{eco.RawUsed(n)} used of {eco.EffectiveRaw(n)}, {eco.FreeRaw(n)} free", null, "A mine also needs a raw material slot. A planet never has more usable raw slots than energy slots.");
        Heading("Being built here");
        BuildQueues(n);
        Heading("Buildings");
        var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == n).ToList();
        if (here.Count(o => o.Completed) == 0) Line("none", Dim);
        else ScrapRows(here.Where(o => o.Completed), n);
        var building = here.Where(o => !o.Completed).ToList();
        if (building.Count > 0) Line($"Under construction: {Counts(building.Select(o => o.Class))}", Dim, Ui.Small);
        Heading("Build");
        // open the build table on what this planet can build: no shipyard here means start on Facilities
        if (_buildSection is "Ships" or "Fighters" && !HasYard(n, Praedra.Core.Construction.YardFamily.Shipyard)
            || _buildSection == "Troops" && !HasYard(n, Praedra.Core.Construction.YardFamily.Training)) _buildSection = "Facilities";
        BuildMenu(n);
        if (Me == Side.Collective && n == S.MyHqSystem) HqSection(n);
    }

    void PaneFleets(string n)
    {
        var pic = S.Picture(n);
        bool live = S.Sees(n) || S.OwnerOf(n) == Me;
        var own = S.Game.State.Fleets.Values.Where(f => f.At == n && f.Side == Me).OrderBy(f => f.Id).ToList();
        Heading(own.Count == 0 ? "Your fleets" : $"Your fleets ({own.Count})");
        if (own.Count == 0) Line("None in orbit.", Dim);
        foreach (var f in own) OrbitFleetCard(f);
        var heading = live ? "Enemy fleets here now" : "Enemy fleets";
        Heading(heading);
        if (live)
        {
            var enemy = S.VisibleEnemyFleetsAt(n);
            if (enemy.Count == 0) Line("None.", Dim);
            foreach (var e in enemy)
                using (Card(Ui.DangerDeep, new Color(Ui.Danger, 0.5f)))
                {
                    Line($"{S.NameOf(e.Side)}: {Ui.Count(e.ShipCount, "ship", "ships")}, {Ui.Count(e.SquadronCount, "squadron", "squadrons")}", Ui.Text);
                    Line(e.ToString(), Ui.Text2, Ui.Small);
                }
        }
        else if (pic?.Fleets is { } seen)
        {
            if (seen.Value.Count == 0) Line($"None seen on day {seen.Stamp}.", Dim);
            foreach (var e in seen.Value) Line($"{S.NameOf(e.Side)}, seen day {seen.Stamp}: {e}", Ui.Text2, Ui.Small);
            Note("You have no eyes here now: enemy fleets may have come or gone since.");
        }
        else Note("You have no eyes here: send a fleet, or build a Sensor Station on a route nearby.");
    }

    /// <summary>One of your fleets in orbit: what it is made of, its strength and troops aboard, and a button to command it.</summary>
    void OrbitFleetCard(Fleet f)
    {
        var eco = S.Host.Economy;
        using (Card(new Color(Ui.YouDeep, 0.55f), new Color(Ui.You, 0.35f)))
        {
            var h = Ui.HBox(8);
            var name = Ui.Label(f.Name, null, Ui.Text, Ui.Body, wrap: false);
            name.AddThemeFontOverride("font", Ui.SemiBold);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            h.AddChild(name);
            var str = Ui.Label($"strength {Fm.StrengthOf(f):0}", null, Ui.Text2, Ui.Small, wrap: false);
            str.AddThemeFontOverride("font", Ui.Num);
            h.AddChild(str);
            Add(h);
            Line(Counts(f.Ships.Select(id => eco.Find(id)?.Class ?? "?")), Ui.Text2, Ui.Small);
            if (f.Squadrons.Count > 0) Line("Fighters: " + Counts(f.Squadrons.Select(id => eco.Find(id)?.Class ?? "?")), Ui.Text2, Ui.Small);
            var aboard = S.Host.Troops.RegimentsAboard(f);
            if (aboard.Count > 0) Line("Troops aboard: " + Counts(aboard.Select(a => a.Regiment.Class)), Ui.Text2, Ui.Small);
            if (S.PlannedMoveOf(f.Id) is { } pm) Line($"Leaving for {pm.To}, arrives day {pm.ArrivalDay}", Pending, Ui.Small);
            var fid = f.Id;
            var b = Ui.Button("Command this fleet", () => { CloseSectorView(); SelectFleet(fid, true); }, "Close the sector window and select the fleet: move, attack, bombard, invade, load troops.", "Primary");
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.CustomMinimumSize = new Vector2(0, 34);
            Add(b);
        }
    }

    void PaneForces(string n)
    {
        if (S.IsDestroyed(n)) { Note("Nothing is left here."); CharactersAtSystem(n, false); return; }
        var pic = S.Picture(n);
        if (S.OwnerOf(n) == Me)
        {
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            using (Card(have < need ? Ui.WarnDeep : Ui.Panel, have < need ? new Color(Ui.Warn, 0.5f) : Ui.Line))
            {
                Stat("Garrison", need == 0 ? $"{Ui.Count(have, "regiment", "regiments")}, none needed" : $"{have} of {need} regiments needed", have < need ? Ui.Warn : Ui.Text,
                    "Below 60% support a system needs troops to stay calm: one regiment for every 10 points under 60. Too few and it revolts: its yards and mines stop.");
                if (have < need) GarrisonFix(n, need - have);
            }
            RegimentsHere(n, true, pic);
            FightersHere(n);
            CharactersAtSystem(n, true);
            SpecialForcesAtSystem(n);
        }
        else
        {
            if (pic == null) Note("Unexplored: you don't know who is on the ground here.");
            else { RegimentsHere(n, false, pic); LeadersSeenAt(pic); }
            CharactersAtSystem(n, false);
        }
        if (CanSendFrom(n)) { Add(Ui.Space(4)); Act($"Send a mission from {n}...", () => { CloseSectorView(); OpenPlanner(n); }, "Pick a team standing here (characters and special forces), the kind of mission and the target."); }
    }
}
