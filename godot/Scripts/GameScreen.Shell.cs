using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The frame of the game screen, after Paul's mockup (2026-10-28): the map fills the window and a few small dark pieces float at its edges.
/// Top left the menu and the map layers; top centre the search and one pill for whatever needs you; top right the goal, the day and the
/// clock; bottom left your stocks and the way home; bottom centre the thing you clicked; bottom right the latest message. Everything longer
/// (a system's details, Build, Fleets, Leaders, Missions, the log, the sector tables) opens as a window on demand and closes with Esc.
/// </summary>
public partial class GameScreen
{
    /// <summary>The margin between the map's edge and the floating pieces.</summary>
    const int Edge = 20;

    // ---- top right: goal, day, clock
    Label _dayLabel, _sideLabel, _rawLabel, _refinedLabel, _upkeepLabel, _upkeepCaption;
    Control _upkeepBar;
    Button _playButton, _pauseButton, _goalButton, _menuButton;
    readonly List<Button> _speedChips = new();

    // ---- map and overlays
    Control _mapHolder;
    Label _banner, _ordersLabel, _hoverLabel, _hqStatusLabel;
    PanelContainer _hoverCard, _orderBar, _legendCard, _pill;
    Label _orderTitle, _orderBody;
    HBoxContainer _orderActions;
    VBoxContainer _toastBox, _topCentre, _topLeft;
    Button _myHqButton, _enemyHqButton, _legendButton, _layersButton;
    Control _bottomLeft;
    PanelContainer _clockCard;

    // ---- the sector tables, a window over the map
    PanelContainer _sectorDock;

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

        _sectorDock = Ui.Hud(Ui.Pad, Ui.Pad - 2);
        _sectorDock.Visible = false;
        _sectorDock.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _sectorDock.GrowVertical = GrowDirection.Begin;
        _sectorDock.OffsetLeft = Edge; _sectorDock.OffsetBottom = -(Edge + 130); _sectorDock.OffsetTop = -(Edge + 130);
        _sectorDock.CustomMinimumSize = new Vector2(1060, 300);
        _mapHolder.AddChild(_sectorDock);

        _mapHolder.AddChild(BuildDrawer());
        BuildModals();
    }

    // ================================================================== the floating pieces

    void BuildMapOverlays()
    {
        // ---- top left: the menu, then the map layers
        _topLeft = Ui.VBox(Ui.Group);
        _topLeft.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _topLeft.MouseFilter = MouseFilterEnum.Ignore;
        _topLeft.SetAnchorsPreset(LayoutPreset.TopLeft);
        _topLeft.Position = new Vector2(Edge, Edge);
        _mapHolder.AddChild(_topLeft);
        _menuButton = Ui.Button("\u2261", ToggleMenu, "Menu: fleets, leaders, missions, goal, sectors, save, load, settings (Esc closes)", "Hud");
        _menuButton.SetMeta(UiTest.FullName, "Menu");
        _menuButton.CustomMinimumSize = new Vector2(46, 46);
        _menuButton.AddThemeFontSizeOverride("font_size", 22);
        _menuButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _topLeft.AddChild(_menuButton);
        BuildMapTools();

        // ---- top centre: the search with the Layers switch, the pill for what needs you, then short notes
        _topCentre = Ui.VBox(Ui.Block);
        _topCentre.MouseFilter = MouseFilterEnum.Ignore;
        _topCentre.SetAnchorsPreset(LayoutPreset.CenterTop);
        _topCentre.GrowHorizontal = GrowDirection.Both;
        _topCentre.OffsetTop = Edge;
        _topCentre.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _mapHolder.AddChild(_topCentre);
        var searchBar = Ui.Hud(10, 4);
        searchBar.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _topCentre.AddChild(searchBar);
        var sr = Ui.HBox(0);
        searchBar.AddChild(sr);
        var lens = new Glyph(Glyph.Kind.Ring, Ui.Text3, 13) { CustomMinimumSize = new Vector2(20, 0) };
        sr.AddChild(lens);
        BuildSearch(sr);
        sr.AddChild(new VSeparator());
        _layersButton = Ui.Button("Layers", ToggleLayers, "Show or hide the map layers (V cycles them)", "Chip");
        _layersButton.ToggleMode = true;
        _layersButton.ButtonPressed = true;
        _layersButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        sr.AddChild(_layersButton);
        BuildSearchResults(_topCentre);
        ((Control)_searchResults).SizeFlagsHorizontal = SizeFlags.ShrinkCenter;

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
        ordersCard.TooltipText = Ui.Wrap("Orders are carried out when the next day begins. Press Play or +1 day. If the game refuses one, the reason appears in Messages.");
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
        _toastBox = Ui.VBox(6);
        _toastBox.MouseFilter = MouseFilterEnum.Ignore;
        _toastBox.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _topCentre.AddChild(_toastBox);

        // ---- top right: the goal, the day, the clock
        var clock = _clockCard = Ui.Hud(4, 4);
        clock.SetAnchorsPreset(LayoutPreset.TopRight);
        clock.GrowHorizontal = GrowDirection.Begin;
        clock.OffsetRight = -Edge; clock.OffsetLeft = -Edge; clock.OffsetTop = Edge;
        _mapHolder.AddChild(clock);
        var cr = Ui.HBox(Ui.Gap);
        clock.AddChild(cr);
        _goalButton = Ui.Button("Goal", ShowGoal, "How you win, and how close each side is (G).", "Chip");
        _goalButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        cr.AddChild(_goalButton);
        cr.AddChild(new VSeparator());
        var dayBox = Ui.VBox(-3);
        dayBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        dayBox.CustomMinimumSize = new Vector2(52, 0);
        dayBox.MouseFilter = MouseFilterEnum.Stop;
        _sideLabel = Ui.Label("DAY", "StatLabel", wrap: false);
        _sideLabel.MouseFilter = MouseFilterEnum.Stop;
        dayBox.AddChild(_sideLabel);
        _dayLabel = Ui.Label("0", "Number", Ui.Text, 18, wrap: false);
        _dayLabel.MouseFilter = MouseFilterEnum.Stop;
        _dayLabel.TooltipText = Ui.Wrap("The game day. Each day every order you gave is carried out, fleets fly, yards build. Press Play to let the days run.");
        dayBox.AddChild(_dayLabel);
        var dm = new MarginContainer();
        dm.AddThemeConstantOverride("margin_left", 8); dm.AddThemeConstantOverride("margin_right", 8);
        dm.AddChild(dayBox);
        cr.AddChild(dm);
        _pauseButton = Ui.Button("||", () => { if (_playing) TogglePlay(); }, "Pause (Space)", "Clock");
        _pauseButton.SetMeta(UiTest.FullName, "Pause");
        _pauseButton.CustomMinimumSize = new Vector2(44, 44);
        cr.AddChild(_pauseButton);
        _playButton = Ui.Button("\u25b6", () => { if (!_playing) TogglePlay(); }, "Play (Space). The clock stops by itself after a battle, a capture or an invasion.", "Clock");
        _playButton.SetMeta(UiTest.FullName, "Play");
        _playButton.CustomMinimumSize = new Vector2(44, 44);
        cr.AddChild(_playButton);
        var step = Ui.Button("+1", StepOne, "Advance exactly one day (N).", "Chip");
        step.SetMeta(UiTest.FullName, "+1 day");
        step.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        cr.AddChild(step);
        var sg = new ButtonGroup();
        for (int i = 0; i < Speeds.Length; i++)
        {
            int k = i;
            var c = Ui.Chip($"{Speeds[i]}x", () => SetSpeed(k), $"{Speeds[i]} days per second (key {i + 1})", i == _speedIndex, sg);
            c.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            _speedChips.Add(c);
            cr.AddChild(c);
        }
        cr.AddChild(Ui.Space(2));

        // ---- bottom left: your stocks, then the way to the headquarters
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
        var stocks = Ui.Hud(14, 9);
        stocks.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        bl.AddChild(stocks);
        var row = Ui.HBox(0);
        stocks.AddChild(row);
        _rawLabel = StatBlock(row, "RAW", "Raw: ore dug by your mines. It waits for your refineries, which turn it into Refined.", 76);
        row.AddChild(new VSeparator());
        _refinedLabel = StatBlock(row, "REFINED", "Refined: the material you spend on ships, troops and buildings. Each yard takes it as it builds.", 84);
        row.AddChild(new VSeparator());
        _upkeepLabel = StatBlock(row, "UPKEEP", "", 104);
        _upkeepCaption = _upkeepLabel.GetParent().GetChild<Label>(0);
        _upkeepBar = Ui.Bar(0, Ui.You, 2);
        _upkeepLabel.GetParent().AddChild(_upkeepBar);
        var nav = Ui.Hud(4, 4);
        nav.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        bl.AddChild(nav);
        var navRow = Ui.HBox(0);
        nav.AddChild(navRow);
        _myHqButton = Ui.Button("\u2192 HQ", () => GoToCapital(S.Player), "Centre the map on your headquarters", "Chip");
        _myHqButton.SetMeta(UiTest.FullName, "My HQ");
        navRow.AddChild(_myHqButton);
        navRow.AddChild(new VSeparator());
        _enemyHqButton = Ui.Button("\u2192 Enemy HQ", () => GoToCapital(S.Enemy), null, "Chip");
        navRow.AddChild(_enemyHqButton);
        navRow.AddChild(new VSeparator());
        navRow.AddChild(Ui.Button("Fit", () => _view.ResetView(), "Show the whole galaxy (Home)", "Chip"));

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

        // ---- bottom right: the latest messages
        BuildMessageCorner();

        // ---- the hover card follows the mouse
        _hoverCard = Ui.Card(new Color("0d0e10", 0.97f), Ui.LineHi, 10, 7);
        _hoverCard.MouseFilter = MouseFilterEnum.Ignore;
        _hoverCard.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _hoverCard.Visible = false;
        _hoverCard.TopLevel = false;
        _hoverLabel = Ui.Label("", null, Ui.Text, Ui.Small, wrap: false);
        _hoverCard.AddChild(_hoverLabel);
        _mapHolder.AddChild(_hoverCard);
    }

    /// <summary>A small capital caption over a number: how the stocks show Raw, Refined and Upkeep.</summary>
    static Label StatBlock(HBoxContainer row, string caption, string tip, int width)
    {
        var box = Ui.VBox(1);
        box.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        box.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        box.CustomMinimumSize = new Vector2(width, 0);
        box.MouseFilter = MouseFilterEnum.Stop;
        box.TooltipText = Ui.Wrap(tip);
        box.AddChild(Ui.Label(caption, "StatLabel", wrap: false));
        var v = Ui.Label("0", "Number", wrap: false);
        v.MouseFilter = MouseFilterEnum.Stop; // the tooltip is the number's too
        v.TooltipText = box.TooltipText;
        box.AddChild(v);
        row.AddChild(box);
        return v;
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
        _upkeepLabel.Text = $"{load} / {cap}";
        bool over = load > cap, near = !over && cap > 0 && load * 10 >= cap * 9;
        var upColor = over ? Ui.Danger : near ? Ui.Warn : Ui.Text;
        _upkeepLabel.AddThemeColorOverride("font_color", upColor);
        _upkeepCaption.AddThemeColorOverride("font_color", over || near ? upColor : Ui.Text3);
        if (_upkeepBar is ProgressBar pb)
        {
            pb.Value = cap <= 0 ? 1000 : Math.Clamp(load * 1000 / Math.Max(1, cap), 0, 1000);
            pb.AddThemeStyleboxOverride("fill", Ui.Box(over ? Ui.Danger : near ? Ui.Warn : Ui.Text3, 1, null, 0, 0, 0));
        }
        string upTip = $"Upkeep: {load} used of a limit of {cap}. Everything you own costs upkeep every day. The limit comes from your mines and refineries (50 for each mine-and-refinery pair). "
            + (over ? "You are OVER the limit: units get scrapped until you are back under it. Build mines and refineries, or scrap ships." : near ? "You are close to the limit: a build order that would pass it is refused." : "");
        _upkeepLabel.TooltipText = Ui.Wrap(upTip);
        ((Control)_upkeepLabel.GetParent()).TooltipText = _upkeepLabel.TooltipText;
        _rawLabel.TooltipText = ((Control)_rawLabel.GetParent()).TooltipText + (raw >= 10_000 ? $" ({raw})" : "");
        _refinedLabel.TooltipText = ((Control)_refinedLabel.GetParent()).TooltipText + (refined >= 10_000 ? $" ({refined})" : "");

        int q = S.QueuedOrders;
        _ordersLabel.Text = q > 0 ? $"{q} {(q == 1 ? "order" : "orders")} waiting for the next day" : "";
        _ordersLabel.GetParent<Control>().Visible = q > 0 && !InMoveMode;
        _playButton.ThemeTypeVariation = _playing ? "ClockOn" : "Clock";
        _pauseButton.ThemeTypeVariation = _playing ? "Clock" : "ClockOn";
        RefreshGoalButton();
        RefreshHq();
        RefreshOrderBar();
        RefreshPill();
        RefreshSelectionCard();
        RefreshMessageCorner();
    }

    /// <summary>The pill under the search: why the clock stopped, or what needs you; nothing when all is quiet.</summary>
    void RefreshPill()
    {
        if (_pill == null) return;
        string text = null; Color dot = Ui.Warn;
        if (InMoveMode) text = null;
        else if (_playing && _runUntilNeeded) { text = "Running until something needs you"; dot = Ui.Good; }
        else if (!_playing && _pauseNote != null) text = _pauseNote;
        else
        {
            int urgent = _attention.Count(a => a.Level == Level.Danger), watch = _attention.Count(a => a.Level == Level.Warn);
            if (urgent + watch > 0) { text = urgent > 0 ? $"{urgent} urgent, {watch} to watch" : $"{Ui.Count(watch, "thing", "things")} to watch"; dot = urgent > 0 ? Ui.Danger : Ui.Warn; }
        }
        _banner.Text = text ?? "";
        var g = (Glyph)_banner.GetParent().GetChild(0);
        g.Color = dot; g.QueueRedraw();
        _pill.Visible = text != null;
    }

    bool InMoveMode => _moveFleet >= 0 || _moveHq || _moveChar >= 0 || _pickMission || _moveFighters != null;

    void ToggleLayers()
    {
        _layersCard.Visible = !_layersCard.Visible;
        _layersButton.SetPressedNoSignal(_layersCard.Visible);
        _layerKeyCard.Visible = _layersCard.Visible && _layer != MapLayer.Owners;
        _view.QueueRedraw();
    }

    /// <summary>Where the map must not put system names: fleet markers, and the boxes on the map (a name under a box can't be read).</summary>
    IEnumerable<Rect2> MapLabelObstacles()
    {
        foreach (var r in _fleets.Obstacles()) yield return r;
        var origin = _view.GlobalPosition;
        foreach (var box in new Control[] { _orderBar, _topCentre, _topLeft, _clockCard, _bottomLeft, _selCard, _msgCorner, _drawer, _sectorDock })
        {
            if (box == null || !box.IsVisibleInTree()) continue;
            var g = box.GetGlobalRect();
            yield return new Rect2(g.Position - origin, g.Size);
        }
    }

    // ================================================================== the hover card

    void ShowHover(string text)
    {
        _hoverLabel.Text = text;
        _hoverCard.Visible = !string.IsNullOrEmpty(text);
        if (!_hoverCard.Visible) return;
        _hoverCard.ResetSize();
        var mouse = _mapHolder.GetLocalMousePosition();
        var size = _hoverCard.GetCombinedMinimumSize();
        var p = mouse + new Vector2(18, 18);
        if (p.X + size.X > _mapHolder.Size.X - 8) p.X = mouse.X - size.X - 14;
        if (p.Y + size.Y > _mapHolder.Size.Y - 8) p.Y = mouse.Y - size.Y - 14;
        _hoverCard.Position = new Vector2(Math.Max(4, p.X), Math.Max(4, p.Y));
    }

    // ================================================================== toasts

    readonly List<(PanelContainer Card, double Left)> _toasts = new();
    const int MaxToasts = 3;

    /// <summary>A short note under the search that goes by itself (order sent, saved, something happened).</summary>
    void Toast(string text, MsgKind kind = MsgKind.Info, string system = null)
    {
        var dot = kind switch
        {
            MsgKind.Bad => Ui.Danger,
            MsgKind.Warn => Ui.Warn,
            MsgKind.Good => Ui.Good,
            MsgKind.Battle => Ui.You,
            _ => Ui.Text3,
        };
        var card = Ui.Hud(12, 7);
        card.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        var h = Ui.HBox(Ui.Block);
        h.MouseFilter = MouseFilterEnum.Ignore;
        h.AddChild(new Glyph(Glyph.Kind.Dot, dot, 8) { CustomMinimumSize = new Vector2(10, 0) });
        var l = Ui.Label(text, null, Ui.Text, Ui.Small);
        l.CustomMinimumSize = new Vector2(Math.Min(380, 8 * text.Length + 10), 0);
        h.AddChild(l);
        card.AddChild(h);
        if (system != null)
        {
            card.MouseFilter = MouseFilterEnum.Stop;
            card.MouseDefaultCursorShape = CursorShape.PointingHand;
            card.TooltipText = "Click to go there";
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
            else _toasts[i] = (card, left);
        }
    }

    // ================================================================== headquarters buttons

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

    /// <summary>The HQ buttons and the HQ line follow what the player's side knows (rule: m5-fog-and-hq §N5 KnownHq).</summary>
    void RefreshHq()
    {
        if (_enemyHqButton == null) return;
        bool known = S.EnemyHqSystem != null;
        _enemyHqButton.SetMeta(UiTest.FullName, known ? "Enemy HQ" : "Enemy HQ: unknown");
        _enemyHqButton.AddThemeColorOverride("font_color", known ? Ui.Text3 : new Color(Ui.Text3, 0.6f));
        _enemyHqButton.TooltipText = Ui.Wrap(known ? "Centre the map on the enemy headquarters: the system you must take to win" : EnemyHqUnknownText);
        var h = S.OwnHq;
        string line = h == null ? null
            : h.Status == Praedra.Core.Fog.HqStatus.InTransit ? HqMovingText(h) + "."
            : S.PendingHqMove != null ? $"HQ move to {S.PendingHqMove} starts when the next day begins."
            : null;
        _hqStatusLabel.Text = line ?? "";
        _hqStatusLabel.GetParent<Control>().Visible = line != null;
    }

    /// <summary>Centre the map on a system and select it (search results, messages, Needs you items).</summary>
    void GoTo(string system, bool select)
    {
        if (system == null || !G.TryFindSystem(system, out int idx)) return;
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
        void Item(Glyph g, string text)
        {
            var h = Ui.HBox(8);
            g.CustomMinimumSize = new Vector2(22, 18);
            h.AddChild(g);
            h.AddChild(Ui.Label(text, null, Ui.Text2, Ui.Small, wrap: false));
            v.AddChild(h);
        }
        Item(new Glyph(Glyph.Kind.Dot, Ui.You, 11), "Your system");
        Item(new Glyph(Glyph.Kind.Dot, Ui.Enemy, 11), "Enemy system (as last seen)");
        Item(new Glyph(Glyph.Kind.Dot, Ui.Neutral, 8), "Nobody's system");
        Item(new Glyph(Glyph.Kind.Ring, new Color(Ui.Neutral, 0.6f), 9), "Never explored");
        Item(new Glyph(Glyph.Kind.Cross, Ui.Danger, 11), "Destroyed by a harvester");
        Item(new Glyph(Glyph.Kind.DoubleRing, Ui.Gold, 14, Ui.You), "Headquarters (tagged with its name)");
        Item(new Glyph(Glyph.Kind.Ring, Ui.Danger, 14), "Uprising: yards and mines stop");
        Item(new Glyph(Glyph.Kind.DashedRing, Ui.Warn, 14), "Blockade: yards and mines stop");
        Item(new Glyph(Glyph.Kind.Triangle, Colors.White, 11, Ui.You), "Your fleet (a number = several here)");
        Item(new Glyph(Glyph.Kind.TriangleDown, Ui.Enemy, 11), "Enemy fleet you see now");
        Item(new Glyph(Glyph.Kind.Bar, new Color(0.72f, 0.75f, 0.80f), 16), "Major route: 3x speed");
        Item(new Glyph(Glyph.Kind.Bar, new Color(Ui.Text3, 0.7f), 16), "Minor route: 1.5x speed");
        v.AddChild(Ui.Label("Off the routes, ships jump through open space at normal speed. A move preview is solid on a route and dashed off it; red rings mark a known trap. Each sector is drawn a little tighter than it is, so the sectors stand apart: the arrival day in a move preview is the real distance.", "Hint", size: Ui.Tiny));
        v.GetChild<Label>(v.GetChildCount() - 1).CustomMinimumSize = new Vector2(250, 0);
        return card;
    }
}
