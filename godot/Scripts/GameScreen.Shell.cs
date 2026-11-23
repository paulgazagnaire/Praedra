using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The frame of the game screen, after Paul's mockup (2026-10-28): the map fills the window and a few small dark pieces float at its edges.
/// Across the top the bar (Paul's "ui-update-nov-15" frame, GameScreen.TopBar.cs): menu, goal, search, layers, your stocks, the day and the
/// clock. Under it, centred, one pill for whatever needs you; bottom left the map legend when asked for; bottom centre the thing you
/// clicked; bottom right the short notes, the one place for news (map declutter, Paul's All A 2026-11-18). Everything longer (a system's
/// details, Build, Fleets, Leaders, Missions, the log, a sector) opens as a window on demand and closes with Esc.
/// </summary>
public partial class GameScreen
{
    /// <summary>The margin between the map's edge and the floating pieces.</summary>
    const int Edge = 20;

    // ---- the top bar: goal, stocks, day, clock (GameScreen.TopBar.cs)
    Label _dayLabel, _sideLabel, _rawLabel, _refinedLabel, _upkeepLabel, _upkeepCaption;
    Button _playButton, _goalButton, _menuButton;
    readonly List<Button> _speedChips = new();

    // ---- map and overlays
    Control _mapHolder;
    Label _banner, _ordersLabel, _hoverLabel, _hqStatusLabel;
    PanelContainer _hoverCard, _orderBar, _legendCard, _pill;
    Label _orderTitle, _orderBody;
    HBoxContainer _orderActions;
    VBoxContainer _toastBox, _topCentre, _topLeft;
    Control _noteLayer;
    Button _myHqMenu, _enemyHqMenu, _legendButton, _layersButton;
    Control _bottomLeft;

    void BuildUi()
    {
        var bg = new ColorRect { Color = Ui.MapBg, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        _mapHolder = new Control { ClipContents = true, MouseFilter = MouseFilterEnum.Pass };
        _mapHolder.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_mapHolder);
        _view = new GalaxyView { FocusMode = FocusModeEnum.None };
        _view.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _mapHolder.AddChild(_view);
        _fleets = new FleetLayer { Name = "FleetLayer" };
        _view.AddChild(_fleets);
        BuildMapOverlays();

        _mapHolder.AddChild(BuildDrawer());
        AddChild(BuildSectorView()); // over the map and the window, under the dialogs
        AddChild(BuildMgmtLayer());  // the fleet, ground and infrastructure windows float over the sector window
        AddChild(BuildTopBar());     // the top bar over the windows (they start under it), with what it opens: search results, the layers
        AddChild(_noteLayer);        // the short notes under the top bar, over the windows and under the dialogs (story W9)
        BuildModals();
    }

    // ================================================================== the floating pieces

    void BuildMapOverlays()
    {
        // ---- top left, under the bar: the key of the map layer shown (the layers list itself hangs from the bar's Layers box)
        _topLeft = Ui.VBox(Ui.Block);
        _topLeft.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _topLeft.MouseFilter = MouseFilterEnum.Ignore;
        _topLeft.SetAnchorsPreset(LayoutPreset.TopLeft);
        _topLeft.Position = new Vector2(BarSide, TopClear);
        _mapHolder.AddChild(_topLeft);

        // ---- top centre, under the bar: the pill for what needs you, then short notes
        _topCentre = Ui.VBox(Ui.Block);
        _topCentre.MouseFilter = MouseFilterEnum.Ignore;
        _topCentre.SetAnchorsPreset(LayoutPreset.CenterTop);
        _topCentre.GrowHorizontal = GrowDirection.Both;
        _topCentre.OffsetTop = TopClear;
        _topCentre.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _mapHolder.AddChild(_topCentre);
        _topCentre.SortChildren += HideCoveredNotes;   // checked once the notes are placed, before they are drawn

        // the pill: why the clock stopped, or how many things need you; a click opens Needs you
        _pill = Ui.Hud(14, 7);
        _pill.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _pill.MouseFilter = MouseFilterEnum.Stop;
        _pill.MouseDefaultCursorShape = CursorShape.PointingHand;
        _pill.TooltipText = "Open Needs you (Q)";
        _pill.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) ShowTab(DrawerTab.Needs); };
        var ph = Ui.HBox(Ui.Block);
        ph.MouseFilter = MouseFilterEnum.Ignore;
        ph.AddChild(new Glyph(Glyph.Kind.Dot, Ui.Warn, 8) { CustomMinimumSize = new Vector2(10, 0) });
        _banner = Ui.Label("", null, Ui.Text, Ui.Small, wrap: false);
        _banner.AddThemeFontOverride("font", Ui.Mono);
        ph.AddChild(_banner);
        _pill.AddChild(ph);
        _pill.Visible = false;
        _topCentre.AddChild(_pill);
        var ordersCard = Ui.Hud(12, 5);
        ordersCard.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        ordersCard.MouseFilter = MouseFilterEnum.Stop;
        ordersCard.TooltipText = Ui.Wrap("Your orders take effect as soon as the clock moves (Play, or N for one day): a fleet leaves at once and arrives on the day shown. Until then each order has a Cancel button. If the game refuses one, the reason appears in the Log.");
        _ordersLabel = Ui.Label("", null, Ui.Warn, Ui.Small, wrap: false);
        ordersCard.AddChild(_ordersLabel);
        ordersCard.Visible = false;
        _topCentre.AddChild(ordersCard);
        var hqPill = Ui.Hud(12, 5);
        hqPill.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        hqPill.Visible = false;
        _hqStatusLabel = Ui.Label("", null, Ui.Gold, Ui.Small, wrap: false);
        hqPill.AddChild(_hqStatusLabel);
        _topCentre.AddChild(hqPill);
        // the short notes: one place for news, the bottom-right corner (Paul 2026-11-18, pick 7: nothing pops up in the middle of the
        // map), in a layer of their own that the tree puts over the windows: an order's note or a red warning goes by in seconds and must be
        // seen, and clicked, while you work in them (story W9). Tree order, not ZIndex: ZIndex only drew a note over a window, and its
        // click still went to the window (QA-104). The newest note is at the bottom.
        _toastBox = Ui.VBox(6);
        _toastBox.MouseFilter = MouseFilterEnum.Ignore;
        _toastBox.SetAnchorsPreset(LayoutPreset.BottomRight);
        _toastBox.GrowHorizontal = GrowDirection.Begin; _toastBox.GrowVertical = GrowDirection.Begin;
        _toastBox.OffsetRight = -Edge; _toastBox.OffsetBottom = -Edge; _toastBox.OffsetLeft = -Edge; _toastBox.OffsetTop = -Edge;
        _noteLayer = new Control { MouseFilter = MouseFilterEnum.Ignore, Name = "Notes" };
        _noteLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _noteLayer.AddChild(_toastBox);

        // ---- bottom left: the map legend, when asked for (Layers > Legend). The HQ buttons are gone (Paul 2026-11-18, pick 10): H centres
        // the map on your HQ, and both HQs are in the menu.
        var bl = Ui.VBox(Ui.Block);
        bl.SetAnchorsPreset(LayoutPreset.BottomLeft);
        bl.GrowVertical = GrowDirection.Begin;
        bl.OffsetLeft = Edge; bl.OffsetBottom = -Edge; bl.OffsetTop = -Edge;
        bl.MouseFilter = MouseFilterEnum.Ignore;
        bl.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _mapHolder.AddChild(bl);
        _bottomLeft = bl;
        _legendCard = BuildLegend();
        _legendCard.Visible = false;
        _legendCard.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        bl.AddChild(_legendCard);
        // no Fit button: the Home key shows the whole galaxy (Paul 2026-11-14)

        // ---- bottom centre: what you clicked, or the order bar of a move
        BuildSelectionCard();
        _orderBar = Ui.Hud(14, 10);
        _orderBar.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _orderBar.GrowHorizontal = GrowDirection.Both; _orderBar.GrowVertical = GrowDirection.Begin;
        _orderBar.OffsetBottom = -Edge; _orderBar.OffsetTop = -Edge;
        _orderBar.CustomMinimumSize = new Vector2(560, 0);
        _orderBar.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _orderBar.Visible = false;
        _mapHolder.AddChild(_orderBar);
        var ov = Ui.VBox(6);
        _orderBar.AddChild(ov);
        _orderTitle = Ui.Label("", "Subtitle");
        _orderTitle.CustomMinimumSize = new Vector2(530, 0);
        ov.AddChild(_orderTitle);
        _orderBody = Ui.Label("", "Dim");
        _orderBody.CustomMinimumSize = new Vector2(530, 0);
        ov.AddChild(_orderBody);
        _orderActions = Ui.HBox(6);
        ov.AddChild(_orderActions);

        // ---- the hover card: over a dot, after a short rest, one line above the pointer (pick 8)
        _hoverCard = Ui.Card(new Color("0d0e10", 0.97f), Ui.LineHi, 10, 7);
        _hoverCard.MouseFilter = MouseFilterEnum.Ignore;
        _hoverCard.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _hoverCard.Visible = false;
        _hoverCard.TopLevel = false;
        _hoverLabel = Ui.Label("", null, Ui.Text, Ui.Small, wrap: false);
        _hoverCard.AddChild(_hoverLabel);
        _mapHolder.AddChild(_hoverCard);
    }

    /// <summary>12400 reads 12.4k: the stocks stay short, the exact number is in the tooltip.</summary>
    static string Short(int n) => n >= 100_000 ? $"{n / 1000}k" : n >= 10_000 ? $"{n / 1000.0:0.0}k" : n.ToString();

    void RefreshTop()
    {
        if (_dayLabel == null) return;
        _dayLabel.Text = S.Game.Day.ToString();
        _sideLabel.TooltipText = Ui.Wrap($"You play the {S.NameOf(S.Player)} against the computer ({S.Level}). Space battle rules: {GameBoot.RulesName(S.Rules)}.");
        var eco = S.Host.Economy;
        int load = eco.Load(S.Player), cap = eco.Capacity(S.Player);
        int raw = eco.Raw(S.Player), refined = eco.Refined(S.Player);
        _rawLabel.Text = Short(raw);
        _refinedLabel.Text = Short(refined);
        _upkeepLabel.Text = $"{load}/{cap}";
        bool over = load > cap, near = !over && cap > 0 && load * 10 >= cap * 9;
        // near or over the limit, the number and its name turn amber or red; the frame has no bar under it, so the colour is the warning
        _upkeepLabel.AddThemeColorOverride("font_color", over ? Ui.Danger : near ? Ui.Warn : BarValue);
        _upkeepCaption.AddThemeColorOverride("font_color", over ? Ui.Danger : near ? Ui.Warn : BarLabel);
        string upTip = $"Upkeep: {load} used of a limit of {cap}. Everything you own costs upkeep every day. The limit comes from your mines and refineries (50 for each mine-and-refinery pair). "
            + (over ? "You are OVER the limit: units get scrapped until you are back under it. Build mines and refineries, or scrap ships." : near ? "You are close to the limit: a build order that would pass it is refused." : "");
        SetStockTip(_upkeepCell, _upkeepLabel, upTip);
        SetStockTip(_rawCell, _rawLabel, RawTip + (raw >= 10_000 ? $" ({raw})" : ""));
        SetStockTip(_refinedCell, _refinedLabel, RefinedTip + (refined >= 10_000 ? $" ({refined})" : ""));

        int q = S.QueuedOrders;
        _ordersLabel.Text = q > 0 ? $"{q} {(q == 1 ? "order" : "orders")} given, cancel until Play" : "";
        _ordersLabel.GetParent<Control>().Visible = q > 0 && !InMoveMode;
        _playButton.Text = _playing ? "||" : "\u25b6";
        SetBarButtonColour(_playButton, _playing ? BarValue : BarLabel);   // lit while the clock runs
        _playButton.SetMeta(UiTest.FullName, _playing ? "Pause" : "Play");
        RefreshGoalButton();
        RefreshHq();
        RefreshOrderBar();
        RefreshPill();
        RefreshSelectionCard();
    }

    /// <summary>The pill's note before the first day. While a yard at your headquarters stands idle it says that first: giving the yards
    /// work is the game's first job, and pressing Play with them idle wastes days (impeccable critique 2026-11-14).</summary>
    const string StartNote = "Press Play (or Space) to start the clock";

    string StartNoteText() =>
        S.MyHqSystem is string home && IdleYardFamilies(home).Count > 0 ? $"Yards idle at {home}: build, then Play (Space)" : StartNote;

    /// <summary>The pill under the top bar: why the clock stopped, or what needs you; nothing when all is quiet.</summary>
    void RefreshPill()
    {
        if (_pill == null) return;
        string text = null; Color dot = Ui.Warn;
        if (InMoveMode) text = null;
        else if (_playing && _runUntilNeeded) { text = "Running until something needs you"; dot = Ui.Good; }
        else if (!_playing && _pauseNote != null) text = _pauseNote == StartNote ? StartNoteText() : _pauseNote;
        else
        {
            int urgent = _attention.Count(a => a.Level == Level.Danger), watch = _attention.Count(a => a.Level == Level.Warn);
            if (urgent + watch > 0) { text = NeedsCount(urgent, watch); dot = urgent > 0 ? Ui.Danger : Ui.Warn; }
        }
        _banner.Text = text ?? "";
        var g = (Glyph)_banner.GetParent().GetChild(0);
        g.Color = dot; g.QueueRedraw();
        _pill.Visible = text != null;
    }

    /// <summary>The pill's count, only the parts that are not zero: "1 urgent", not "1 urgent, 0 to watch" (map declutter 2026-11-18).</summary>
    internal static string NeedsCount(int urgent, int watch) =>
        urgent == 0 ? $"{Ui.Count(watch, "thing", "things")} to watch" : watch == 0 ? $"{urgent} urgent" : $"{urgent} urgent, {watch} to watch";

    internal bool InMoveMode => _moveFleet >= 0 || _moveHq || _moveChar >= 0 || _pickMission || _moveFighters != null;

    void ToggleLayers()
    {
        // one drop-down at a time: the layers list hangs next to the search, and the search results are wider than the search box,
        // so the list would cover the end of each result and take its click
        if (!_layersCard.Visible && _searchResults.Visible) CloseSearch();
        _layersCard.Visible = !_layersCard.Visible;
        _layersButton.SetPressedNoSignal(_layersCard.Visible);
        PlaceDrops();
        _view.QueueRedraw();
    }

    /// <summary>Where the map must not put system names: fleet markers and their words, and the boxes on the map (a name under a box can't
    /// be read).</summary>
    IEnumerable<Rect2> MapLabelObstacles() => _fleets.Obstacles().Concat(MapBoxes());

    /// <summary>The boxes over the map, in its coordinates: no word on the map goes under one.</summary>
    IEnumerable<Rect2> MapBoxes()
    {
        var origin = _view.GlobalPosition;
        foreach (var box in new Control[] { _orderBar, _topLeft, _barStrip, _barLeft, _barRight, _searchResults, _layersCard, _bottomLeft, _selCard, _drawer })
        {
            if (box == null || !box.IsVisibleInTree()) continue;
            var g = box.GetGlobalRect();
            yield return new Rect2(g.Position - origin, g.Size);
        }
        // the pills under the bar one by one: a pill a window hides (HideCoveredNotes) leaves the names under it free
        if (_topCentre != null && _topCentre.IsVisibleInTree())
            foreach (var c in _topCentre.GetChildren().OfType<Control>())
            {
                if (!c.IsVisibleInTree() || c.Modulate.A <= 0) continue;
                var g = c.GetGlobalRect();
                yield return new Rect2(g.Position - origin, g.Size);
            }
        // the short notes in the corner, in their own layer
        if (_toastBox != null && _toastBox.GetChildCount() > 0 && _toastBox.IsVisibleInTree())
        {
            var g = _toastBox.GetGlobalRect();
            yield return new Rect2(g.Position - origin, g.Size);
        }
    }

    // ================================================================== the hover card

    /// <summary>How long the mouse rests on a dot before its card shows: crossing the map no longer flashes a card at every dot.</summary>
    const double HoverRest = 0.4;
    double _hoverWait;
    int _hoverAt = -1;

    /// <summary>The card for the dot under the mouse (null: none). Its words are set at once; the card itself shows after a short rest
    /// on the same dot (Paul 2026-11-18, pick 8).</summary>
    void ShowHover(string text, int system = -1)
    {
        _hoverLabel.Text = text ?? "";
        if (string.IsNullOrEmpty(text)) { _hoverCard.Visible = false; _hoverAt = -1; return; }
        if (system != _hoverAt) { _hoverCard.Visible = false; _hoverWait = HoverRest; _hoverAt = system; return; }
        if (_hoverCard.Visible) PlaceHover();
    }

    void TickHover(double delta)
    {
        if (_hoverAt < 0 || _hoverCard.Visible || string.IsNullOrEmpty(_hoverLabel.Text)) return;
        _hoverWait -= delta;
        if (_hoverWait > 0) return;
        _hoverCard.Visible = true;
        PlaceHover();
    }

    /// <summary>Above the pointer and to its right, so it never covers the names the pointing brings in (they sit beside the dots);
    /// below it at the top edge, to the left at the right edge.</summary>
    void PlaceHover()
    {
        _hoverCard.ResetSize();
        var mouse = _mapHolder.GetLocalMousePosition();
        var size = _hoverCard.GetCombinedMinimumSize();
        var p = mouse + new Vector2(12, -size.Y - 14);
        if (p.Y < 4) p.Y = mouse.Y + 22;
        if (p.X + size.X > _mapHolder.Size.X - 8) p.X = mouse.X - size.X - 12;
        _hoverCard.Position = new Vector2(Math.Max(4, p.X), Math.Max(4, p.Y));
    }

    // ================================================================== toasts

    readonly List<(PanelContainer Card, double Left)> _toasts = new();
    const int MaxToasts = 3;
    /// <summary>A note is one line at most this wide; a longer one ends in "..." and says it all in its tooltip (and in the Log).</summary>
    const float NoteMaxWidth = 520f;
    /// <summary>A note fades out over its last moments instead of vanishing.</summary>
    const double NoteFade = 0.6;

    /// <summary>The notes keep to the bottom-right corner; with the side window open there they move to its left, so they never cover it.</summary>
    void PlaceNotes()
    {
        if (_toastBox == null) return;
        float right = -Edge - (_drawer != null && _drawer.Visible ? DrawerWidth + Ui.Block : 0);
        if (_toastBox.OffsetRight != right) { _toastBox.OffsetRight = right; _toastBox.OffsetLeft = right; }
    }

    /// <summary>A short one-line note in the bottom-right corner that fades by itself (an answer to your order, red news, saved).</summary>
    void Toast(string text, MsgKind kind = MsgKind.Info, string system = null)
    {
        var dot = Ui.KindColor(kind, Ui.Text3);
        var card = Ui.Hud(12, 7);
        card.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        var h = Ui.HBox(Ui.Block);
        h.MouseFilter = MouseFilterEnum.Ignore;
        h.AddChild(new Glyph(Glyph.Kind.Dot, dot, 8) { CustomMinimumSize = new Vector2(10, 0) });
        var l = Ui.Label(text, null, Ui.Text, Ui.Small, wrap: false);
        if (l.GetCombinedMinimumSize().X > NoteMaxWidth)
        {
            l.ClipText = true;
            l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            l.CustomMinimumSize = new Vector2(NoteMaxWidth, 0);
            card.TooltipText = Ui.Wrap(text);
        }
        h.AddChild(l);
        card.AddChild(h);
        if (system != null)
        {
            card.MouseFilter = MouseFilterEnum.Stop;
            card.MouseDefaultCursorShape = CursorShape.PointingHand;
            card.TooltipText = string.IsNullOrEmpty(card.TooltipText) ? "Click to go there" : card.TooltipText + "\nClick to go there";
            card.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) GoTo(system, true); };
        }
        else card.MouseFilter = MouseFilterEnum.Ignore;
        _toastBox.AddChild(card);
        _toasts.Add((card, kind is MsgKind.Bad or MsgKind.Warn ? 6.0 : 3.5));
        while (_toasts.Count > MaxToasts) { _toasts[0].Card.QueueFree(); _toasts.RemoveAt(0); }
    }

    void TickToasts(double delta)
    {
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var (card, left) = _toasts[i];
            left -= delta;
            if (left <= 0) { card.QueueFree(); _toasts.RemoveAt(i); }
            else
            {
                _toasts[i] = (card, left);
                if (left < NoteFade) card.Modulate = new Color(1, 1, 1, (float)(left / NoteFade));
            }
        }
        PlaceNotes();
    }

    // ================================================================== headquarters: H, Shift+H and the menu

    void GoToCapital(Side side)
    {
        string sys = side == S.Player ? S.MyHqSystem : S.EnemyHqSystem;
        if (sys == null)
        {
            if (side == S.Player && S.OwnHq is { Status: Praedra.Core.Fog.HqStatus.InTransit } h)
            {
                Toast(HqMovingText(h));
                if (G.TryFindSystem(h.Path[^1], out int dest)) _view.Centre(dest);
            }
            else if (side == S.Player) Toast("You have no headquarters any more.", MsgKind.Bad);
            else Toast(EnemyHqUnknownText, MsgKind.Warn);
            return;
        }
        GoTo(sys, true);
    }

    const string EnemyHqUnknownText = "The Hearth location is unknown. Your picture of the galaxy has never shown it: explore Collective systems, or send spies, to find it.";

    static string HqMovingText(Praedra.Core.Fog.HqState h) => $"HQ moving to {h.Path[^1]}, arrives day {GameSession.HqArrivalDay(h)}";

    /// <summary>The menu's HQ entries and the HQ line follow what the player's side knows (rule: m5-fog-and-hq §N5 KnownHq): the enemy
    /// entry is greyed, and says why, until your picture has shown its HQ.</summary>
    void RefreshHq()
    {
        if (_enemyHqMenu == null) return;
        bool known = S.EnemyHqSystem != null;
        _enemyHqMenu.SetMeta(UiTest.FullName, known ? "Go to enemy HQ" : "Go to enemy HQ: unknown");
        _enemyHqMenu.AddThemeColorOverride("font_color", known ? Ui.Text : new Color(Ui.Text3, 0.6f));
        _enemyHqMenu.AddThemeColorOverride("font_hover_color", known ? Ui.Text : new Color(Ui.Text3, 0.6f));
        _enemyHqMenu.TooltipText = Ui.Wrap(known ? "Centre the map on the enemy headquarters: the system you must take to win (Shift+H)" : EnemyHqUnknownText);
        var h = S.OwnHq;
        string line = h == null ? null
            : h.Status == Praedra.Core.Fog.HqStatus.InTransit ? HqMovingText(h) + "."
            : S.PendingHqMove != null ? $"HQ move to {S.PendingHqMove} starts as soon as the clock moves."
            : null;
        _hqStatusLabel.Text = line ?? "";
        _hqStatusLabel.GetParent<Control>().Visible = line != null;
    }

    /// <summary>Centre the map on a system and select it (search results, messages, Needs you items).</summary>
    void GoTo(string system, bool select)
    {
        if (system == null || !G.TryFindSystem(system, out int idx)) return;
        CloseSectorView();
        _view.Centre(idx);
        if (select) { _view.Select(idx); ShowTab(DrawerTab.Selected); }
    }

    // ================================================================== routes, legend

    void CycleLanes() => SetRoutesShown(!_view.ShowRoutes);

    internal void SetRoutesShown(bool on)
    {
        _view.ShowRoutes = on;
        if (_routesChip != null) _routesChip.SetPressedNoSignal(on);
    }

    internal void SetDeliveriesShown(bool on)
    {
        UiSettings.ShowDeliveries = on; UiSettings.Save();
        _fleets.ShowDeliveries = on;
        if (_deliveriesChip != null) _deliveriesChip.SetPressedNoSignal(on);
    }

    void ToggleLegend()
    {
        _legendCard.Visible = !_legendCard.Visible;
        _legendButton.SetPressedNoSignal(_legendCard.Visible);
        _view.QueueRedraw();
    }

    PanelContainer BuildLegend()
    {
        var card = Ui.Card(new Color(Ui.Chrome, 0.96f), Ui.Line, 12, 10);
        card.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        var v = Ui.VBox(3);
        card.AddChild(v);
        v.AddChild(Ui.Section("Map legend"));
        void Item(Control g, string text)
        {
            var h = Ui.HBox(8);
            if (g is Glyph) g.CustomMinimumSize = new Vector2(22, 18);
            var slot = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(22, 18) };
            slot.AddChild(g);
            h.AddChild(slot);
            h.AddChild(Ui.Label(text, null, Ui.Text2, Ui.Small, wrap: false));
            v.AddChild(h);
        }
        // the map's own pixel icons, as the map draws them (Paul 2026-11-23: no placeholder pictures anywhere; the map icons are #359-#361)
        Control Map(string key, Color ink) => IconPic.Of(PixelIconData.Map[key], 1, ink);
        Item(new Glyph(Glyph.Kind.Dot, Ui.You, 11), "Your system");
        Item(new Glyph(Glyph.Kind.Dot, Ui.Enemy, 11), "Enemy system (as last seen)");
        Item(new Glyph(Glyph.Kind.Dot, Ui.Neutral, 8), "Nobody's system");
        Item(new Glyph(Glyph.Kind.Ring, new Color(Ui.Neutral, 0.6f), 9), "Never explored");
        Item(new Glyph(Glyph.Kind.Cross, Ui.Danger, 11), "Destroyed by a harvester");
        Item(new Glyph(Glyph.Kind.DoubleRing, Colors.White, 14, Ui.You), "Headquarters: a white ring round the dot");   // as the map draws it
        Item(new Glyph(Glyph.Kind.Ring, Ui.Danger, 14), "Uprising: yards and mines stop");
        Item(new Glyph(Glyph.Kind.DashedRing, Ui.Warn, 14), "Blockade: yards and mines stop");
        Item(Map("fleet-defensive", Ui.You), "Your fleet, Defensive (it fights)");
        Item(Map("fleet-elusive", Ui.You), "Your fleet, Elusive (it slips away)");
        Item(Map("fleet-pair-defensive", Ui.You), "Two of your fleets here: two marks");
        Item(Map("fleet-group-3", Ui.You), "More of your fleets here: their number");
        Item(Map("fleet-unknown", Ui.Enemy), "Enemy fleet you see now");
        Item(Map("mover-leader", Ui.You), "Your leader travelling alone");
        Item(Map("mover-team", Ui.You), "Your mission team travelling");
        if (S.Player == Side.Collective) Item(Map("mover-hearth", Ui.Gold), "Your HQ on the move");
        v.AddChild(Ui.Label("On the way from the yard (Layers > Deliveries):", null, Ui.Text3, Ui.Small, wrap: false));
        Item(Map("mover-regiment", Ui.You), "Troops");
        Item(Map("mover-building", Ui.You), "A building");
        Item(Map("mover-ship", Ui.You), "A ship");
        Item(Map("mover-fighter", Ui.You), "Fighters");
        Item(new Glyph(Glyph.Kind.Bar, new Color(0.72f, 0.75f, 0.80f), 16), "Major route: 3x speed");
        Item(new Glyph(Glyph.Kind.Bar, new Color(Ui.Text3, 0.7f), 16), "Minor route: 1.5x speed");
        v.AddChild(Ui.Label("Off the routes, ships jump through open space at normal speed. A move preview is solid on a route and dashed off it; red rings mark a known trap. Each sector is drawn a little tighter than it is, so the sectors stand apart: the arrival day in a move preview is the real distance.", "Hint", size: Ui.Tiny));
        v.GetChild<Label>(v.GetChildCount() - 1).CustomMinimumSize = new Vector2(250, 0);
        return card;
    }
}
