using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The frame of the game screen: the top bar, the map and what sits on it (search, headquarters buttons, map layers, legend, the latest
/// messages, the order bar of a move, the hover card, toasts), the drawer on the right and the dock for the sector windows. Nothing floats
/// over the middle of the map: the drawer has its own column, and the map's corners hold only small, collapsible things.
/// </summary>
public partial class GameScreen
{
    // ---- top bar
    Label _dayLabel, _sideLabel, _rawLabel, _refinedLabel, _upkeepLabel;
    Control _upkeepBar;
    Button _playButton, _goalButton, _sectorsButton, _menuButton;
    Button _speedButton;

    // ---- map and overlays
    Control _mapHolder;
    Label _banner, _ordersLabel, _hoverLabel, _hqStatusLabel;
    PanelContainer _hoverCard, _orderBar, _legendCard;
    Label _orderTitle, _orderBody;
    HBoxContainer _orderActions;
    VBoxContainer _toastBox, _topRight;
    Button _myHqButton, _enemyHqButton, _legendButton;
    HBoxContainer _layerChips;

    // ---- column under the map
    PanelContainer _sectorDock;

    void BuildUi()
    {
        var bg = new ColorRect { Color = Ui.MapBg, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var root = Ui.VBox(0);
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(root);
        root.AddChild(BuildTopBar());

        var body = Ui.HBox(0);
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(body);

        var mapColumn = Ui.VBox(0);
        mapColumn.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(mapColumn);
        _mapHolder = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true, MouseFilter = MouseFilterEnum.Pass };
        mapColumn.AddChild(_mapHolder);
        _view = new GalaxyView { FocusMode = FocusModeEnum.None };
        _view.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _mapHolder.AddChild(_view);
        _fleets = new FleetLayer { Name = "FleetLayer" };
        _view.AddChild(_fleets);
        BuildMapOverlays();

        _sectorDock = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(0, 304) };
        var dockStyle = Ui.Box(Ui.Chrome, 0, null, 0, Ui.Pad, Ui.Pad - 2);
        dockStyle.BorderColor = Ui.Line; dockStyle.BorderWidthTop = 1;
        _sectorDock.AddThemeStyleboxOverride("panel", dockStyle);
        mapColumn.AddChild(_sectorDock);

        body.AddChild(BuildDrawer());
        BuildModals();
    }

    // ================================================================== top bar

    Control BuildTopBar()
    {
        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 54) };
        var st = Ui.Box(Ui.Chrome, 0, null, 0, 14, 5);
        st.BorderColor = Ui.Line; st.BorderWidthBottom = 1;
        bar.AddThemeStyleboxOverride("panel", st);
        var row = Ui.HBox(Ui.Block);
        bar.AddChild(row);

        // side and day
        var dayBox = Ui.VBox(-2);
        dayBox.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        dayBox.CustomMinimumSize = new Vector2(104, 0);
        dayBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _sideLabel = Ui.Label("", "StatLabel", Ui.You, wrap: false);
        _sideLabel.MouseFilter = MouseFilterEnum.Stop;
        dayBox.AddChild(_sideLabel);
        _dayLabel = Ui.Label("Day 0", "Title", wrap: false, size: 22);
        _dayLabel.MouseFilter = MouseFilterEnum.Stop;
        _dayLabel.TooltipText = Ui.Wrap("The game day. Each day every order you gave is carried out, fleets fly, yards build. Press Play to let the days run.");
        dayBox.AddChild(_dayLabel);
        row.AddChild(dayBox);
        row.AddChild(new VSeparator());

        // the clock
        _playButton = Ui.Button("Play", TogglePlay, "Start or stop the clock (Space). It stops by itself after a battle, a capture or an invasion.", "Primary");
        _playButton.CustomMinimumSize = new Vector2(84, 36);
        _playButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(_playButton);
        var step = Ui.Button("+1 day", StepOne, "Advance exactly one day (N).");
        step.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        step.CustomMinimumSize = new Vector2(0, 36);
        row.AddChild(step);
        // one speed button: a click goes to the next speed (keys 1-4 pick one)
        _speedButton = Ui.Button($"{Speeds[DefaultSpeedIndex]}x", () => SetSpeed((_speedIndex + 1) % Speeds.Length),
            $"Days per second: {string.Join(", ", Speeds.Select(x => x + "x"))}. Click for the next speed, or press 1 to 4.", "Chip");
        _speedButton.CustomMinimumSize = new Vector2(48, 36);
        _speedButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(_speedButton);
        row.AddChild(new VSeparator());

        // the three numbers of the economy, each explained by its tooltip
        _rawLabel = StatBlock(row, "RAW", "Raw: ore dug by your mines. It waits for your refineries, which turn it into Refined.", 64);
        _refinedLabel = StatBlock(row, "REFINED", "Refined: the material you spend on ships, troops and buildings. Each yard takes it as it builds.", 76);
        _upkeepLabel = StatBlock(row, "UPKEEP", "", 104);
        _upkeepBar = Ui.Bar(0, Ui.You, 3);
        _upkeepLabel.GetParent().AddChild(_upkeepBar);

        row.AddChild(Ui.Grow());

        _goalButton = Ui.Button("Goal", ShowGoal, "How you win, and how close each side is (G).");
        _goalButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _goalButton.CustomMinimumSize = new Vector2(0, 36);
        row.AddChild(_goalButton);
        _sectorsButton = Ui.Chip("Sectors", ToggleSectorDock, "Sector windows under the map: every system of a sector in one table, with support, troops, slots and what is being built (S).");
        _sectorsButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _sectorsButton.CustomMinimumSize = new Vector2(0, 36);
        row.AddChild(_sectorsButton);
        var help = Ui.Button("?", ToggleHowTo, "How to play (F1)");
        help.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        help.CustomMinimumSize = new Vector2(36, 36);
        row.AddChild(help);
        _menuButton = Ui.Button("Menu", ToggleMenu, "Save, load, settings, how to play, back to the start screen.");
        _menuButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _menuButton.CustomMinimumSize = new Vector2(0, 36);
        row.AddChild(_menuButton);
        return bar;
    }

    /// <summary>A small capital caption over a number: the top bar's way to show Raw, Refined and Upkeep.</summary>
    static Label StatBlock(HBoxContainer row, string caption, string tip, int width)
    {
        var box = Ui.VBox(0);
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

    void RefreshTop()
    {
        if (_dayLabel == null) return;
        _dayLabel.Text = $"Day {S.Game.Day}";
        _sideLabel.Text = (S.Player == Side.Hand ? "THE HAND" : "THE COLLECTIVE") + (S.Rules == Praedra.Core.Battle.BattleRuleset.New ? "" : " · CLASSIC");
        _sideLabel.TooltipText = Ui.Wrap($"You play the {S.NameOf(S.Player)} against the computer ({S.Level}). Space battle rules: {GameBoot.RulesName(S.Rules)}.");
        var eco = S.Host.Economy;
        int load = eco.Load(S.Player), cap = eco.Capacity(S.Player);
        _rawLabel.Text = eco.Raw(S.Player).ToString();
        _refinedLabel.Text = eco.Refined(S.Player).ToString();
        _upkeepLabel.Text = $"{load} / {cap}";
        bool over = load > cap, near = !over && cap > 0 && load * 10 >= cap * 9;
        _upkeepLabel.AddThemeColorOverride("font_color", over ? Ui.Danger : near ? Ui.Warn : Ui.Text);
        if (_upkeepBar is ProgressBar pb)
        {
            pb.Value = cap <= 0 ? 1000 : Math.Clamp(load * 1000 / Math.Max(1, cap), 0, 1000);
            pb.AddThemeStyleboxOverride("fill", Ui.Box(over ? Ui.Danger : near ? Ui.Warn : Ui.You, 2, null, 0, 0, 0));
        }
        string upTip = $"Upkeep: {load} used of a limit of {cap}. Everything you own costs upkeep every day. The limit comes from your mines and refineries (50 for each mine-and-refinery pair). "
            + (over ? "You are OVER the limit: units get scrapped until you are back under it. Build mines and refineries, or scrap ships." : near ? "You are close to the limit: a build order that would pass it is refused." : "");
        _upkeepLabel.TooltipText = Ui.Wrap(upTip);
        ((Control)_upkeepLabel.GetParent()).TooltipText = _upkeepLabel.TooltipText;

        int q = S.QueuedOrders;
        _ordersLabel.Text = q > 0 ? $"{q} {(q == 1 ? "order" : "orders")} waiting for the next day" : "";
        _ordersLabel.GetParent<Control>().Visible = q > 0;
        _playButton.Text = _playing ? (_runUntilNeeded ? "Running..." : "Pause") : "Play";
        _playButton.ThemeTypeVariation = _playing ? "WarnButton" : "Primary";
        RefreshGoalButton();
        RefreshHq();
        RefreshOrderBar();
        string banner = !_playing && _pauseNote != null && !InMoveMode ? _pauseNote : null;
        _banner.Text = banner ?? "";
        _banner.GetParent<Control>().Visible = banner != null;
    }

    bool InMoveMode => _moveFleet >= 0 || _moveHq || _moveChar >= 0 || _pickMission;

    // ================================================================== map overlays

    void BuildMapOverlays()
    {
        // ---- top left: one row (search, the map layer, routes), the layer's key under it
        var tl = Ui.VBox(Ui.Block);
        tl.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        tl.MouseFilter = MouseFilterEnum.Ignore;
        tl.SetAnchorsPreset(LayoutPreset.TopLeft);
        tl.Position = new Vector2(Ui.Pad, Ui.Pad);
        _mapHolder.AddChild(tl);
        var r1 = Ui.HBox(Ui.Block);
        r1.MouseFilter = MouseFilterEnum.Ignore;
        tl.AddChild(r1);
        BuildSearch(r1);
        _layerChips = r1;
        BuildLayerChips();
        var hqPill = Ui.Card(new Color(Ui.Panel, 0.94f), Ui.Gold, 8, 6);
        hqPill.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        hqPill.Visible = false;
        _hqStatusLabel = Ui.Label("", null, Ui.Gold, Ui.Small, wrap: false);
        hqPill.AddChild(_hqStatusLabel);
        tl.AddChild(hqPill);
        BuildSearchResults(tl);

        // ---- top right: pause note, waiting orders, toasts
        _topRight = Ui.VBox(6);
        _topRight.MouseFilter = MouseFilterEnum.Ignore;
        _topRight.SetAnchorsPreset(LayoutPreset.TopRight);
        _topRight.GrowHorizontal = GrowDirection.Begin;
        _topRight.OffsetRight = -Ui.Pad; _topRight.OffsetTop = Ui.Pad; _topRight.OffsetLeft = -Ui.Pad;
        _topRight.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _mapHolder.AddChild(_topRight);
        var bannerCard = Ui.Card(new Color(Ui.Panel, 0.96f), Ui.Warn, 12, 8);
        bannerCard.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        bannerCard.MouseFilter = MouseFilterEnum.Ignore;
        _banner = Ui.Label("", "Strong", Ui.Text, 15, wrap: false);
        bannerCard.AddChild(_banner);
        bannerCard.Visible = false;
        _topRight.AddChild(bannerCard);
        var ordersCard = Ui.Card(new Color(Ui.WarnDeep, 0.95f), new Color(Ui.Warn, 0.6f), 10, 6);
        ordersCard.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        ordersCard.MouseFilter = MouseFilterEnum.Stop;
        ordersCard.TooltipText = Ui.Wrap("Orders are carried out when the next day begins. Press Play or +1 day. If the game refuses one, the reason appears in Messages.");
        _ordersLabel = Ui.Label("", null, new Color("ffe7a8"), Ui.Small, wrap: false);
        ordersCard.AddChild(_ordersLabel);
        ordersCard.Visible = false;
        _topRight.AddChild(ordersCard);
        _toastBox = Ui.VBox(6);
        _toastBox.MouseFilter = MouseFilterEnum.Ignore;
        _toastBox.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _topRight.AddChild(_toastBox);

        // ---- bottom right: zoom and the legend
        var br = Ui.VBox(6);
        br.SetAnchorsPreset(LayoutPreset.BottomRight);
        br.GrowHorizontal = GrowDirection.Begin; br.GrowVertical = GrowDirection.Begin;
        br.OffsetRight = -Ui.Pad; br.OffsetBottom = -Ui.Pad; br.OffsetLeft = -Ui.Pad; br.OffsetTop = -Ui.Pad;
        br.MouseFilter = MouseFilterEnum.Ignore;
        _mapHolder.AddChild(br);
        _legendCard = BuildLegend();
        _legendCard.Visible = false;
        br.AddChild(_legendCard);
        // the places to go and how to read the map, together in one corner (zoom is the wheel, the trackpad or + and -)
        var navRow = Ui.HBox(Ui.Block);
        navRow.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        navRow.MouseFilter = MouseFilterEnum.Ignore;
        br.AddChild(navRow);
        _myHqButton = Ui.Button("My HQ", () => GoToCapital(S.Player), "Centre the map on your headquarters", "Chip");
        navRow.AddChild(_myHqButton);
        _enemyHqButton = Ui.Button("Enemy HQ", () => GoToCapital(S.Enemy), null, "Chip");
        navRow.AddChild(_enemyHqButton);
        navRow.AddChild(Ui.Button("Fit", () => _view.ResetView(), "Show the whole galaxy (Home)", "Chip"));
        _legendButton = Ui.Chip("Legend", ToggleLegend, "What the dots, rings, lines and triangles on the map mean");
        navRow.AddChild(_legendButton);

        // ---- bottom centre: the order bar of a move
        _orderBar = Ui.Card(new Color(Ui.Panel, 0.97f), Ui.LineHi, 14, 10);
        _orderBar.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _orderBar.GrowHorizontal = GrowDirection.Both; _orderBar.GrowVertical = GrowDirection.Begin;
        _orderBar.OffsetBottom = -Ui.Pad; _orderBar.OffsetTop = -Ui.Pad;
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

        // ---- the hover card follows the mouse
        _hoverCard = Ui.Card(new Color("0f141d", 0.96f), Ui.LineHi, 10, 7);
        _hoverCard.MouseFilter = MouseFilterEnum.Ignore;
        _hoverCard.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _hoverCard.Visible = false;
        _hoverCard.TopLevel = false;
        _hoverLabel = Ui.Label("", null, Ui.Text, Ui.Small, wrap: false);
        _hoverCard.AddChild(_hoverLabel);
        _mapHolder.AddChild(_hoverCard);
    }

    /// <summary>Where the map must not put system names: fleet markers, and the boxes on the map (a name under a box can't be read).</summary>
    IEnumerable<Rect2> MapLabelObstacles()
    {
        foreach (var r in _fleets.Obstacles()) yield return r;
        var origin = _view.GlobalPosition;
        foreach (var box in new Control[] { _legendCard, _orderBar, _topRight, _searchResults, _layerChips?.GetParent<Control>(), _myHqButton?.GetParent<Control>() })
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

    /// <summary>A short note at the top right of the map that goes by itself (order sent, saved, something happened).</summary>
    void Toast(string text, MsgKind kind = MsgKind.Info, string system = null)
    {
        var (bg, line) = kind switch
        {
            MsgKind.Bad => (Ui.DangerDeep, Ui.Danger),
            MsgKind.Warn => (Ui.WarnDeep, Ui.Warn),
            MsgKind.Good => (Ui.GoodDeep, Ui.Good),
            MsgKind.Battle => (Ui.YouDeep, Ui.You),
            _ => (Ui.Panel, Ui.LineHi),
        };
        var card = Ui.Card(new Color(bg, 0.97f), line, 10, 6);
        card.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        card.CustomMinimumSize = new Vector2(0, 0);
        var l = Ui.Label(text, null, Ui.Text, Ui.Small);
        l.CustomMinimumSize = new Vector2(Math.Min(380, 9 * text.Length + 10), 0);
        card.AddChild(l);
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
        _enemyHqButton.Text = known ? "Enemy HQ" : "Enemy HQ: unknown";
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
        Item(new Glyph(Glyph.Kind.Bar, new Color("9ecbff"), 16), "Major route: 3x speed");
        Item(new Glyph(Glyph.Kind.Bar, new Color(Ui.Text3, 0.7f), 16), "Minor route: 1.5x speed");
        v.AddChild(Ui.Label("Off the routes, ships jump through open space at normal speed. A move preview is solid on a route and dashed off it; red rings mark a known trap.", "Hint", size: Ui.Tiny));
        v.GetChild<Label>(v.GetChildCount() - 1).CustomMinimumSize = new Vector2(250, 0);
        return card;
    }
}
