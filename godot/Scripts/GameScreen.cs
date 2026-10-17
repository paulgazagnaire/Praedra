using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Rebellion.Core.Clock;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>
/// The game screen: top bar, galaxy map, system and fleet panels, message log. All rules live in the core; this reads the state through the
/// <see cref="GameSession"/>, submits orders and calls the session's StepDay (the only thing that advances the game).
/// </summary>
public partial class GameScreen : Control
{
    public event Action BackToMenu;
    public event Action<string> LoadRequested;

    static readonly int[] Speeds = { 1, 2, 5, 10 };       // days per second
    const int DefaultSpeedIndex = 2;                       // 5x when the player presses Play
    const int MaxStepsPerFrame = 30;
    const float PanPixelsPerSecond = 700f;
    const int AutosaveEvery = 30;                          // days
    const int MaxLogItems = 400;

    public GameSession S { get; private set; }
    public Galaxy G => S.Data.Galaxy;
    public GalaxyView View => _view;

    GalaxyView _view;
    FleetLayer _fleets;
    Label _dayLabel, _sideLabel, _rawLabel, _refinedLabel, _upkeepLabel, _ordersLabel, _banner, _hoverLabel, _toast;
    Button _playButton;
    Button _lanesButton;
    readonly List<Button> _speedButtons = new();
    ItemList _log;
    readonly List<Message> _allMessages = new();      // everything (capped), oldest first
    readonly List<Message> _logMessages = new();      // what the list shows under the current filter
    enum LogFilter { All, Battles, Mine }
    LogFilter _logFilter = LogFilter.All;
    readonly List<Button> _filterButtons = new();
    PanelContainer _panel, _fleetListPanel, _reportPanel, _victoryPanel, _confirmPanel, _howPanel;
    VBoxContainer _pv, _fleetList;
    Label _reportTitle, _reportBody, _confirmTitle, _confirmBody;
    Button _confirmOk;
    Action _confirmAction;
    Button _myHqButton, _enemyHqButton;
    Label _hqStatusLabel;
    bool _showHowToOnStart;

    bool _playing;
    int _speedIndex = DefaultSpeedIndex;
    double _acc;
    bool _panelDirty = true, _fleetListDirty = true;
    double _panelTimer;
    string _pauseNote;
    double _toastTimer;

    // Screenshot options (set by Main from the command line).
    public string ShotPath;
    public int ShotFrames = 30;
    int _frame;

    /// <param name="newGame">true for a fresh game from the start screen (the very first one shows the How to play overlay).</param>
    public void Init(GameSession session, bool newGame = false) { S = session; _showHowToOnStart = newGame; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var theme = new Theme { DefaultFontSize = 15 };
        Theme = theme;
        BuildUi();

        _view.Init(G);
        // Fog of war: owners and states of systems that are not yours come from your side's picture; unexplored systems are dim; uprisings
        // and blockades show only where you see live. rule: m5-fog-and-hq §F3, §N2, §N8
        _view.OwnerOf = i => S.KnownOwnerOf(G.Systems[i].Name).Owner;
        _view.IsUnknown = i => { string n = G.Systems[i].Name; return !S.Explored(n) && S.OwnerOf(n) != S.Player; };
        _view.IsDestroyed = i => S.IsDestroyed(G.Systems[i].Name); // rule: m5-death-star §2 effect 6 (the side's own picture knows)
        _view.IsHq = i => HqSideAt(i) != null;
        _view.CapitalTag = CapitalTagOf;
        _view.IsUprising = i => { string n = G.Systems[i].Name; return S.Sees(n) && S.Host.Support.IsUprising(n); };
        _view.IsBlockaded = i => { string n = G.Systems[i].Name; return S.Sees(n) && S.Game.State.Blockades.ContainsKey(n); };
        _view.StateChanged += OnViewChanged;
        _view.SystemPicked += OnSystemPicked;
        _view.FleetPicker = _fleets.Pick;
        _view.EnemyMarkerPicker = _fleets.PickEnemySystem;
        _fleets.Player = S.Player;
        _view.Player = S.Player;
        _view.LabelObstacles = MapLabelObstacles;
        _fleets.Planned = () => S.PlannedMoves;
        _fleets.EnemyFleets = () => S.Fog.VisibleFleets(S.Player);
        _fleets.CharacterRoutes = CharacterRoutes;
        _fleets.HqRoute = () => S.OwnHq is { Status: Rebellion.Core.Fog.HqStatus.InTransit } h ? (h.Path, h.HopIndex, GameSession.HqArrivalDay(h)) : null;
        _fleets.Bind(S.Game, G);

        // Start with the map centred on the player's headquarters.
        string home = S.MyHqSystem ?? (S.OwnHq is { Status: Rebellion.Core.Fog.HqStatus.InTransit } th ? th.Path[^1] : GameBoot.EmpireCapital);
        if (G.TryFindSystem(home, out int homeIdx)) { _view.FocusOn(homeIdx, 2.4f); }
        S.Say($"You lead the {S.NameOf(S.Player)}. Your headquarters is {home}. Press Play (or Space) to start the clock; the How to play button explains the rest.", MsgKind.Good, home);
        foreach (var m in S.Messages) AddLogItem(m);
        if (S.Game.Day == 0) _pauseNote = "Press Play (or Space) to start the clock";
        RefreshTop();
        if (_showHowToOnStart && GameBoot.HowToUnseen()) ShowHowTo();
    }

    /// <summary>Whose headquarters the player knows at a system: their own always, the enemy's only when known (the Empire: KnownHq).</summary>
    Side? HqSideAt(int i)
    {
        string name = G.Systems[i].Name;
        if (name == S.MyHqSystem) return S.Player;
        if (name == S.EnemyHqSystem) return S.Enemy;
        return null;
    }

    /// <summary>Where the map must not put system names: fleet markers, and the boxes floating over the map (a name under a box can't be read).</summary>
    IEnumerable<Rect2> MapLabelObstacles()
    {
        foreach (var r in _fleets.Obstacles()) yield return r;
        var origin = _view.GlobalPosition;
        var map = _view.GetGlobalRect();
        foreach (var c in GetChildren())
            if (c is Control box && box.IsVisibleInTree() && box.GetGlobalRect() is var g && g.Intersects(map) && !g.Encloses(map))
                yield return new Rect2(g.Position - origin, g.Size);
    }

    (string Text, Color Color)? CapitalTagOf(int i)
    {
        if (HqSideAt(i) is not Side side) return null;
        if (side == S.Player) return (side == Side.Empire ? "YOUR CAPITAL" : "YOUR HQ", new Color(1f, 0.95f, 0.5f));
        return (side == Side.Empire ? "ENEMY CAPITAL" : "REBEL HQ (TARGET)", new Color(1f, 0.45f, 0.4f));
    }

    // ================================================================== UI skeleton

    static StyleBoxFlat Box(Color bg, int margin = 8, Color? border = null)
    {
        var st = new StyleBoxFlat { BgColor = bg };
        st.SetContentMarginAll(margin);
        if (border is Color b) { st.BorderColor = b; st.SetBorderWidthAll(1); }
        st.SetCornerRadiusAll(3);
        return st;
    }

    Label Stat(string tip, HBoxContainer row, int minWidth = 0)
    {
        var l = new Label { MouseFilter = MouseFilterEnum.Stop, TooltipText = tip, CustomMinimumSize = new Vector2(minWidth, 0) };
        row.AddChild(l);
        return l;
    }

    void BuildUi()
    {
        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 0);
        AddChild(vbox);

        // ---- top bar
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", Box(new Color(0.08f, 0.09f, 0.13f), 6));
        vbox.AddChild(bar);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        bar.AddChild(row);

        _dayLabel = Stat("The game day. The game lasts as long as it takes: capture the enemy capital to win.", row, 84);
        _dayLabel.AddThemeFontSizeOverride("font_size", 20);
        _sideLabel = Stat("Your side.", row);
        row.AddChild(new VSeparator());
        _rawLabel = Stat("Raw: ore dug by your mines. It waits for your refineries.", row, 70);
        _refinedLabel = Stat("Refined: the material you spend on building ships, troops and buildings.", row, 100);
        _upkeepLabel = Stat("Upkeep, used / limit. Everything you own costs upkeep. The limit comes from your mines and refineries (50 for each mine-and-refinery pair). Over the limit, units get scrapped.", row, 100);
        _ordersLabel = Stat("Orders you gave that start when the next day begins (press Play or +1 day).", row);
        _ordersLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _playButton = Btn("Play", TogglePlay, "Start or stop the clock (Space)");
        _playButton.CustomMinimumSize = new Vector2(76, 0);
        row.AddChild(_playButton);
        row.AddChild(Btn("+1 day", StepOne, "Advance one day (N)"));
        var group = new ButtonGroup();
        for (int i = 0; i < Speeds.Length; i++)
        {
            int idx = i;
            var b = Btn($"{Speeds[i]}x", () => SetSpeed(idx), $"{Speeds[i]} days per second (key {i + 1})");
            b.ToggleMode = true; b.ButtonGroup = group; b.ButtonPressed = i == DefaultSpeedIndex;
            _speedButtons.Add(b);
            row.AddChild(b);
        }
        row.AddChild(new VSeparator());
        _charButton = Btn("Characters", ToggleCharacters, "Your leaders: where they are, their posts and skills (C)");
        _charButton.ToggleMode = true;
        row.AddChild(_charButton);
        _missionButton = Btn("Missions", ToggleMissions, "Your missions: team, target, phase and expected day; cancel one (M)");
        _missionButton.ToggleMode = true;
        row.AddChild(_missionButton);
        _lanesButton = Btn("Routes: on", CycleLanes, "Show or hide the hyperspace routes on the map (L)");
        row.AddChild(_lanesButton);
        row.AddChild(Btn("How to play", ToggleHowTo, "Short instructions (F1)"));
        row.AddChild(Btn("Save", SaveGame, "Save to the one save slot"));
        row.AddChild(Btn("Load", AskLoad, "Load the saved game (asks first)"));
        row.AddChild(Btn("Menu", AskMenu, "Back to the start screen (asks first)"));

        // ---- map and everything floating over it
        _view = new GalaxyView { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        vbox.AddChild(_view);
        _fleets = new FleetLayer { Name = "FleetLayer" };
        _view.AddChild(_fleets);

        // message log (bottom left)
        var logBox = new PanelContainer();
        logBox.AddThemeStyleboxOverride("panel", Box(new Color(0.04f, 0.05f, 0.08f, 0.85f), 6, new Color(0.3f, 0.34f, 0.45f)));
        logBox.SetAnchorsPreset(LayoutPreset.BottomLeft);
        logBox.OffsetLeft = 10; logBox.OffsetBottom = -10; logBox.OffsetRight = 640; logBox.OffsetTop = -210;
        logBox.GrowVertical = GrowDirection.Begin;
        AddChild(logBox);
        var lv = new VBoxContainer();
        logBox.AddChild(lv);
        var lh = new HBoxContainer();
        lh.AddThemeConstantOverride("separation", 4);
        lv.AddChild(lh);
        var lt = new Label { Text = "Messages (click one to go there)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        lt.Modulate = new Color(1, 1, 1, 0.55f);
        lt.AddThemeFontSizeOverride("font_size", 12);
        lh.AddChild(lt);
        var fgroup = new ButtonGroup();
        foreach (var (text, f, tip) in new[] { ("All", LogFilter.All, "Show every message"), ("Battles", LogFilter.Battles, "Only fights: battles, bombardments, invasions, captures"), ("My systems", LogFilter.Mine, "Only things that happen at your own systems") })
        {
            var ff = f;
            var fb = Btn(text, () => SetLogFilter(ff), tip);
            fb.ToggleMode = true; fb.ButtonGroup = fgroup; fb.ButtonPressed = f == _logFilter;
            fb.AddThemeFontSizeOverride("font_size", 12);
            _filterButtons.Add(fb);
            lh.AddChild(fb);
        }
        _log = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(610, 150), AutoHeight = false, FocusMode = FocusModeEnum.None };
        _log.AddThemeFontSizeOverride("font_size", 13);
        _log.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        _log.AddThemeColorOverride("guide_color", new Color(0, 0, 0, 0));
        _log.ItemClicked += OnLogClicked;
        lv.AddChild(_log);

        // fleets list (top left)
        _fleetListPanel = new PanelContainer();
        _fleetListPanel.AddThemeStyleboxOverride("panel", Box(new Color(0.05f, 0.06f, 0.09f, 0.88f), 6, new Color(0.3f, 0.34f, 0.45f)));
        _fleetListPanel.SetAnchorsPreset(LayoutPreset.TopLeft);
        _fleetListPanel.OffsetLeft = 10; _fleetListPanel.OffsetTop = 56;
        AddChild(_fleetListPanel);
        var fv = new VBoxContainer();
        _fleetListPanel.AddChild(fv);
        var go = new HBoxContainer();
        go.AddThemeConstantOverride("separation", 4);
        fv.AddChild(go);
        var ft = new Label { Text = "Your fleets", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ft.Modulate = new Color(1, 1, 1, 0.6f);
        go.AddChild(ft);
        _myHqButton = Btn("My HQ", () => GoToCapital(S.Player), "Centre the map on your headquarters");
        go.AddChild(_myHqButton);
        _enemyHqButton = Btn("Enemy HQ", () => GoToCapital(S.Enemy), "Centre the map on the enemy headquarters: the system you must take to win");
        go.AddChild(_enemyHqButton);
        var fs = new ScrollContainer { CustomMinimumSize = new Vector2(300, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        fs.CustomMinimumSize = new Vector2(300, 170);
        _hqStatusLabel = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0) };
        _hqStatusLabel.AddThemeFontSizeOverride("font_size", 13);
        _hqStatusLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.5f));
        fv.AddChild(_hqStatusLabel);
        fv.AddChild(fs);
        _fleetList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        fs.AddChild(_fleetList);

        // right-hand panel
        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        _panel.AddThemeStyleboxOverride("panel", Box(new Color(0.07f, 0.08f, 0.12f, 0.96f), 10, new Color(0.4f, 0.45f, 0.6f)));
        _panel.AnchorLeft = 1; _panel.AnchorRight = 1; _panel.AnchorTop = 0; _panel.AnchorBottom = 1;
        _panel.OffsetLeft = -400; _panel.OffsetRight = -10; _panel.OffsetTop = 56; _panel.OffsetBottom = -10;
        _panel.GrowHorizontal = GrowDirection.Begin;
        AddChild(_panel);
        var sc = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _panel.AddChild(sc);
        _pv = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(372, 0) };
        _pv.AddThemeConstantOverride("separation", 4);
        sc.AddChild(_pv);

        // banner (top centre): pause reason, move-order hints
        _banner = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        _banner.AddThemeFontSizeOverride("font_size", 18);
        _banner.AddThemeStyleboxOverride("normal", Box(new Color(0.1f, 0.1f, 0.16f, 0.92f), 8, new Color(1f, 0.85f, 0.35f)));
        _banner.SetAnchorsPreset(LayoutPreset.CenterTop);
        _banner.OffsetTop = 60; _banner.GrowHorizontal = GrowDirection.Both;
        _banner.Visible = false;
        AddChild(_banner);

        // hover label (bottom centre)
        _hoverLabel = new Label { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        _hoverLabel.AddThemeStyleboxOverride("normal", Box(new Color(0.04f, 0.05f, 0.08f, 0.85f), 5));
        _hoverLabel.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _hoverLabel.OffsetBottom = -12; _hoverLabel.GrowVertical = GrowDirection.Begin; _hoverLabel.GrowHorizontal = GrowDirection.Both;
        _hoverLabel.Visible = false;
        AddChild(_hoverLabel);

        // toast (a short confirmation under the banner)
        _toast = new Label { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        _toast.AddThemeStyleboxOverride("normal", Box(new Color(0.1f, 0.2f, 0.12f, 0.92f), 7, new Color(0.5f, 0.9f, 0.5f)));
        _toast.SetAnchorsPreset(LayoutPreset.CenterTop);
        _toast.OffsetTop = 110; _toast.GrowHorizontal = GrowDirection.Both;
        _toast.Visible = false;
        AddChild(_toast);

        // report popup (centre)
        _reportPanel = new PanelContainer { Visible = false };
        _reportPanel.AddThemeStyleboxOverride("panel", Box(new Color(0.08f, 0.09f, 0.14f, 0.98f), 16, new Color(1f, 0.85f, 0.35f)));
        _reportPanel.SetAnchorsPreset(LayoutPreset.Center);
        _reportPanel.GrowHorizontal = GrowDirection.Both; _reportPanel.GrowVertical = GrowDirection.Both;
        AddChild(_reportPanel);
        var rv = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        rv.AddThemeConstantOverride("separation", 8);
        _reportPanel.AddChild(rv);
        _reportTitle = new Label();
        _reportTitle.AddThemeFontSizeOverride("font_size", 22);
        rv.AddChild(_reportTitle);
        _reportBody = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(520, 0) };
        rv.AddChild(_reportBody);
        var rb = Btn("Close (Esc)", () => _reportPanel.Visible = false);
        rb.CustomMinimumSize = new Vector2(0, 36);
        rv.AddChild(rb);

        BuildHowTo();
        BuildConfirm();

        // victory screen
        _victoryPanel = new PanelContainer { Visible = false };
        _victoryPanel.AddThemeStyleboxOverride("panel", Box(new Color(0.05f, 0.06f, 0.1f, 0.98f), 30, new Color(1f, 0.85f, 0.35f)));
        _victoryPanel.SetAnchorsPreset(LayoutPreset.Center);
        _victoryPanel.GrowHorizontal = GrowDirection.Both; _victoryPanel.GrowVertical = GrowDirection.Both;
        AddChild(_victoryPanel);
    }

    static Button Btn(string text, Action onPressed, string tooltip = null)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None };
        if (tooltip != null) b.TooltipText = tooltip;
        b.Pressed += onPressed;
        return b;
    }

    // ================================================================== how to play, confirm dialog

    static readonly string[] HowToLines =
    {
        "GOAL: Empire - capture the Rebel headquarters system (first you must find it). Alliance - capture Coruscant. (Your HQ, and the enemy HQ once you know where it is, are tagged on the map; the My HQ / Enemy HQ buttons jump there.) You also win if the other side loses every system.",
        "TIME: press Play (or Space) to run the clock, again to pause. Speed buttons or keys 1-4 (Play starts at 5x). N or +1 day moves one day. It pauses by itself after a battle, a capture or an invasion.",
        "ORDERS: everything you order starts when the next day begins. After giving orders, press Play or +1 day.",
        "MOVE A FLEET: click one of your fleets on the map (white outline) or its name in the Your fleets list, press Move..., then click the destination. The white line shows the route and the arrival day.",
        "INVADE: 1) Select a fleet that is at one of your planets which has regiments, and press Load troops (the fleet needs troop room: transports and some warships have it).",
        "   2) Move the fleet to the enemy or neutral system. Enemy fleets there must be beaten first (they fight you automatically when you arrive).",
        "   3) Optional: press a Bombard button to weaken defences and shields. 4) Press Invade. Land more regiments than the defenders have.",
        "BATTLES happen by themselves when fleets of both sides are at one system. The game pauses and shows a report; click a battle line in Messages to see it again.",
        "BUILD: click one of your planets. The Build menu has Facilities, Ships, Fighters and Troops (click a heading to open it). Ships need a Shipyard there, troops a Training facility, buildings a Construction yard and a free slot.",
        "MONEY: Raw is ore from mines. Refineries turn it into Refined, which is what you spend. Upkeep (used / limit) is the running cost of everything you own; over the limit, units get scrapped. More mines and refineries raise the limit.",
        "ORDER: a planet needs enough regiments to keep the people calm. Too few and it revolts (red ring): yards and mines there stop.",
        "MAP: wheel or + / - zoom, drag or arrow keys pan, Home resets, Esc cancels. Orange = Alliance, green = Empire, grey = nobody. Ships can jump anywhere, but the drawn hyperspace routes are faster: bright thick lines are major routes (3x speed), thin lines minor routes (1.5x). Routes (L) shows or hides them. When you move a fleet the preview is solid where it rides a route and dashed where it jumps through open space. It turns red when it passes a system where you saw an enemy interdictor, which can pull the fleet out of hyperspace there: the game never detours on its own, so Shift-click systems to add waypoints and steer around (or through) it; Backspace drops the last waypoint.",
        "FOG OF WAR: you see live only systems you hold or have a fleet at. Other systems show what you saw last (the panel says \"last seen day N\"); dim hollow dots are unexplored. Enemy fleets appear only where you can see them. The Empire does not know where the Rebel HQ is until it finds it.",
        "CHARACTERS: press Characters (or C) for your leaders: where they are, their post, status and skills. Leaders give bonuses: a fleet's Admiral boosts its bombardment, a fleet's General its regiments in an invasion, a system's General the defenders there.",
        "   Select a fleet to attach a character standing at the same system (as Admiral, Commander, General or passenger) or to detach one; select one of your systems to name or release its General. Move <name>... sends a character alone: the route and arrival day show before you confirm.",
        "MISSIONS: press Send NAME on a mission... in the Characters list (or Send a mission from... on a system panel), tick the team (characters and special forces standing together), pick the kind and click the target: the route and arrival day show first. The Missions list (M) shows each team's phase and expected day, and can cancel one.",
        "   Worth it now: Espionage (see what an enemy system holds) and Diplomacy (win a neutral system over before you take it). Sabotage is very hard with the current interim tables: the team is usually caught.",
        "REBEL HQ (Alliance): click your HQ system and press Move HQ..., then click one of your systems. The HQ travels for some days (it cannot be attacked on the way); a big enemy blockade pins it in place.",
        "DEATH STAR: the Empire can build it like any capital ship at a shipyard (one per system; it is huge and very slow). Select its fleet at a system and press Destroy System: the planet and everything on it are gone for good, enemy ships there die, and the rest of the sector turns against the Empire (a second strike in the same sector turns the whole sector to the Alliance). Destroyed systems stay on the map as dark grey crosses; fleets can still pass through them.",
        "   To stop it (Alliance): beat its fleet in battle (it is an ordinary ship in the fight), or send a Death Star Sabotage mission to the system where it stands (the Missions planner offers it once an agent with the skills is free). Watch the messages for \"New Death Star deployed\".",
        "SAVING: Save / Load in the top bar (one slot). The game also saves itself every 30 days; Continue on the start screen loads that.",
    };

    void BuildHowTo()
    {
        _howPanel = new PanelContainer { Visible = false };
        _howPanel.AddThemeStyleboxOverride("panel", Box(new Color(0.06f, 0.07f, 0.11f, 0.99f), 18, new Color(1f, 0.85f, 0.35f)));
        _howPanel.SetAnchorsPreset(LayoutPreset.Center);
        _howPanel.GrowHorizontal = GrowDirection.Both; _howPanel.GrowVertical = GrowDirection.Both;
        AddChild(_howPanel);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) };
        v.AddThemeConstantOverride("separation", 8);
        _howPanel.AddChild(v);
        var t = new Label { Text = "How to play" };
        t.AddThemeFontSizeOverride("font_size", 26);
        t.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.35f));
        v.AddChild(t);
        var sc = new ScrollContainer { CustomMinimumSize = new Vector2(760, 440), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        v.AddChild(sc);
        var lines = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        lines.AddThemeConstantOverride("separation", 6);
        sc.AddChild(lines);
        lines.AddChild(new Label { Text = "BATTLE RULES (this game): " + GameBoot.RulesName(S.Rules) + (S.Rules == Rebellion.Core.Battle.BattleRuleset.New ? ". Ships fight gun by gun, so the weapons they carry decide the fight." : ". Battles play as in the 1998 game."), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(730, 0) });
        foreach (var line in HowToLines)
            lines.AddChild(new Label { Text = line, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(730, 0) });
        var ok = Btn("Got it - let's play", CloseHowTo);
        ok.CustomMinimumSize = new Vector2(0, 40);
        v.AddChild(ok);
    }

    void ShowHowTo() { _howPanel.Visible = true; _playing = false; RefreshTop(); }

    void CloseHowTo()
    {
        _howPanel.Visible = false;
        if (_showHowToOnStart) { _showHowToOnStart = false; GameBoot.MarkHowToSeen(); }
    }

    void CycleLanes() => SetRoutesShown(!_view.ShowRoutes);

    internal void SetRoutesShown(bool on)
    {
        _view.ShowRoutes = on;
        _lanesButton.Text = on ? "Routes: on" : "Routes: off";
    }

    void ToggleHowTo() { if (_howPanel.Visible) CloseHowTo(); else ShowHowTo(); }

    void BuildConfirm()
    {
        _confirmPanel = new PanelContainer { Visible = false };
        _confirmPanel.AddThemeStyleboxOverride("panel", Box(new Color(0.08f, 0.09f, 0.14f, 0.99f), 16, new Color(1f, 0.6f, 0.35f)));
        _confirmPanel.SetAnchorsPreset(LayoutPreset.Center);
        _confirmPanel.GrowHorizontal = GrowDirection.Both; _confirmPanel.GrowVertical = GrowDirection.Both;
        AddChild(_confirmPanel);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
        v.AddThemeConstantOverride("separation", 10);
        _confirmPanel.AddChild(v);
        _confirmTitle = new Label();
        _confirmTitle.AddThemeFontSizeOverride("font_size", 22);
        v.AddChild(_confirmTitle);
        _confirmBody = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 0) };
        v.AddChild(_confirmBody);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        v.AddChild(row);
        _confirmOk = Btn("OK", () => { var a = _confirmAction; CloseConfirm(); a?.Invoke(); });
        _confirmOk.SizeFlagsHorizontal = SizeFlags.ExpandFill; _confirmOk.CustomMinimumSize = new Vector2(0, 38);
        row.AddChild(_confirmOk);
        var cancel = Btn("Cancel", CloseConfirm);
        cancel.SizeFlagsHorizontal = SizeFlags.ExpandFill; cancel.CustomMinimumSize = new Vector2(0, 38);
        row.AddChild(cancel);
    }

    void Confirm(string title, string body, string okText, Action ok)
    {
        _confirmTitle.Text = title; _confirmBody.Text = body; _confirmOk.Text = okText; _confirmAction = ok;
        _confirmPanel.Visible = true;
        _playing = false; RefreshTop();
    }

    void CloseConfirm() { _confirmPanel.Visible = false; _confirmAction = null; }

    void AskLoad()
    {
        if (!GameBoot.SaveExists(GameBoot.SaveSlot)) { Toast("There is no saved game yet. Press Save first."); return; }
        Confirm("Load the saved game?", $"Your current game (day {S.Game.Day}) will be replaced by the saved one. Anything since your last save is lost.", "Yes, load it", () => LoadRequested?.Invoke(GameBoot.SaveSlot));
    }

    void AskMenu() =>
        Confirm("Back to the start screen?", "The current game will be closed. Anything since your last save (or the autosave every 30 days) is lost.", "Yes, leave", () => BackToMenu?.Invoke());

    void GoToCapital(Side side)
    {
        string sys = side == S.Player ? S.MyHqSystem : S.EnemyHqSystem;
        if (sys == null)
        {
            if (side == S.Player && S.OwnHq is { Status: Rebellion.Core.Fog.HqStatus.InTransit } h)
            {
                Toast(HqMovingText(h));
                if (G.TryFindSystem(h.Path[^1], out int dest)) _view.Centre(dest);
            }
            else if (side == S.Player) Toast("You have no headquarters any more.");
            else Toast(EnemyHqUnknownText);
            return;
        }
        if (!G.TryFindSystem(sys, out int idx)) return;
        _view.Centre(idx); _view.Select(idx);
    }

    const string EnemyHqUnknownText = "The Rebel HQ location is unknown. Your picture of the galaxy has never shown it: explore Rebel systems to find it.";

    static string HqMovingText(Rebellion.Core.Fog.HqState h) => $"HQ moving to {h.Path[^1]}, arrives day {GameSession.HqArrivalDay(h)}";

    /// <summary>The HQ buttons and the HQ line follow what the player's side knows (rule: m5-fog-and-hq §N5 KnownHq).</summary>
    void RefreshHq()
    {
        if (_enemyHqButton == null) return;
        bool known = S.EnemyHqSystem != null;
        _enemyHqButton.Text = known ? "Enemy HQ" : "Enemy HQ: unknown";
        _enemyHqButton.TooltipText = known ? "Centre the map on the enemy headquarters: the system you must take to win" : EnemyHqUnknownText;
        var h = S.OwnHq;
        string line = h == null ? null
            : h.Status == Rebellion.Core.Fog.HqStatus.InTransit ? HqMovingText(h) + "."
            : S.PendingHqMove != null ? $"HQ move to {S.PendingHqMove} starts when the next day begins."
            : null;
        _hqStatusLabel.Text = line ?? "";
        _hqStatusLabel.Visible = line != null;
    }

    bool OverlayOpen => _howPanel.Visible || _confirmPanel.Visible;

    // ================================================================== clock, loop

    void TogglePlay()
    {
        if (S.Winner != null || OverlayOpen) return;
        _playing = !_playing;
        if (_playing) { _pauseNote = null; _reportPanel.Visible = false; }
        RefreshTop();
    }

    void Pause(string note = null)
    {
        _playing = false;
        if (note != null) _pauseNote = note;
        RefreshTop();
    }

    void SetSpeed(int idx)
    {
        _speedIndex = Math.Clamp(idx, 0, Speeds.Length - 1);
        for (int i = 0; i < _speedButtons.Count; i++) _speedButtons[i].SetPressedNoSignal(i == _speedIndex);
    }

    public void StepOne()
    {
        if (S.Winner != null || OverlayOpen) return;
        RunDay();
    }

    /// <summary>One day. Returns false when something asked for a pause.</summary>
    bool RunDay()
    {
        var oc = S.StepDay();
        _retreatPending = null;
        _pauseNote = null;
        foreach (var m in oc.New) AddLogItem(m);
        if (S.Game.Day % AutosaveEvery == 0) Autosave();
        _panelDirty = true; _fleetListDirty = true;
        _view.QueueRedraw(); // owners, blockades and uprisings may have changed
        if (oc.Winner is Side w) { ShowVictory(w); Pause(); return false; }
        if (oc.Popup != null) ShowReport(oc.Popup);
        if (oc.PauseReason != null)
        {
            Pause(oc.PauseReason switch
            {
                "battle" => "Paused: a battle was fought",
                "captured" => "Paused: a system changed hands",
                "invaded" or "invasion" => "Paused: an invasion is taking place",
                "destroyed" => "Paused: the Death Star destroyed a system",
                _ => "Paused",
            });
            return false;
        }
        RefreshTop();
        return true;
    }

    void Autosave()
    {
        try { GameBoot.WriteSave(GameBoot.AutoSaveSlot, S.SaveText(), S.Player, S.Level); }
        catch (Exception e) { GD.PrintErr("Autosave failed: " + e.Message); }
    }

    public void SaveGame()
    {
        try
        {
            GameBoot.WriteSave(GameBoot.SaveSlot, S.SaveText(), S.Player, S.Level);
            Toast($"Game saved (day {S.Game.Day}).");
        }
        catch (Exception e) { Toast("Save failed: " + e.Message); }
    }

    void Toast(string text)
    {
        _toast.Text = text; _toast.Visible = true; _toastTimer = 3.0;
    }

    void RefreshTop()
    {
        _dayLabel.Text = $"Day {S.Game.Day}";
        _sideLabel.Text = $"  {S.NameOf(S.Player)} - {(S.Rules == Rebellion.Core.Battle.BattleRuleset.New ? "New" : "Classic")} battles";
        _sideLabel.TooltipText = "Your side. Space battle rules: " + GameBoot.RulesName(S.Rules) + " (chosen at the start of the game).";
        _sideLabel.AddThemeColorOverride("font_color", S.Player == Side.Empire ? GalaxyView.EmpireColor : GalaxyView.AllianceColor);
        var eco = S.Host.Economy;
        int load = eco.Load(S.Player), cap = eco.Capacity(S.Player);
        _rawLabel.Text = $"Raw {eco.Raw(S.Player)}";
        _refinedLabel.Text = $"Refined {eco.Refined(S.Player)}";
        _upkeepLabel.Text = $"Upkeep {load}/{cap}";
        _upkeepLabel.AddThemeColorOverride("font_color", load > cap ? new Color(1f, 0.4f, 0.35f) : Colors.White);
        int q = S.QueuedOrders;
        _ordersLabel.Text = q > 0 ? $"{q} {(q == 1 ? "order" : "orders")} waiting" : "";
        _playButton.Text = _playing ? "Pause" : "Play";
        RefreshHq();
        string banner = null;
        if (_moveFleet >= 0 || _moveHq || _moveChar >= 0 || _pickMission) banner = _moveBanner;
        else if (!_playing && _pauseNote != null) banner = _pauseNote;
        _banner.Text = banner ?? "";
        _banner.Visible = banner != null;
        _banner.AddThemeColorOverride("font_color", _moveFleet >= 0 && _moveWarn ? new Color(1f, 0.55f, 0.5f) : Colors.White);
    }

    public override void _Process(double delta)
    {
        if (_view == null) return;

        if (_playing)
        {
            _acc += delta * Speeds[_speedIndex];
            int steps = 0;
            while (_acc >= 1.0 && steps < MaxStepsPerFrame && _playing) { _acc -= 1.0; steps++; if (!RunDay()) break; }
            if (_acc > MaxStepsPerFrame || !_playing) _acc = 0;
        }
        _fleets.DayFraction = _playing ? (float)Math.Min(_acc, 0.999) : 0f;

        if (_toast.Visible) { _toastTimer -= delta; if (_toastTimer <= 0) _toast.Visible = false; }

        var pan = Vector2.Zero;
        if (!OverlayOpen)
        {
            if (Input.IsKeyPressed(Key.Left)) pan.X -= 1;
            if (Input.IsKeyPressed(Key.Right)) pan.X += 1;
            if (Input.IsKeyPressed(Key.Up)) pan.Y -= 1;
            if (Input.IsKeyPressed(Key.Down)) pan.Y += 1;
        }
        if (pan != Vector2.Zero) _view.PanPixels(pan * PanPixelsPerSecond * (float)delta);

        _panelTimer -= delta;
        if (_panelDirty && _panelTimer <= 0) { _panelDirty = false; _panelTimer = 0.2; RebuildPanel(); RefreshTop(); }
        if (_fleetListDirty) { _fleetListDirty = false; RebuildFleetList(); }

        if (ShotPath != null && ++_frame >= ShotFrames)
        {
            GetViewport().GetTexture().GetImage().SavePng(ShotPath);
            GD.Print("Saved screenshot " + ShotPath);
            ShotPath = null;
            GetTree().Quit();
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k) return;
        if (_victoryPanel.Visible && k.Keycode != Key.Escape) return;
        if (OverlayOpen && k.Keycode != Key.Escape && k.Keycode != Key.F1) return;
        switch (k.Keycode)
        {
            case Key.F1: ToggleHowTo(); break;
            case Key.Space: TogglePlay(); break;
            case Key.N: StepOne(); break;
            case Key.C: ToggleCharacters(); break;
            case Key.M: ToggleMissions(); break;
            case Key.L: CycleLanes(); break;
            case Key.Backspace when _moveFleet >= 0: DropWaypoint(); break;
            case Key.Escape:
                if (_victoryPanel.Visible) return;
                if (_confirmPanel.Visible) CloseConfirm();
                else if (_howPanel.Visible) CloseHowTo();
                else if (_reportPanel.Visible) _reportPanel.Visible = false;
                else if (_moveFleet >= 0 || _moveHq || _moveChar >= 0 || _pickMission) CancelMove();
                else if (_plan != null) ClosePlanner();
                else if (_missionTab) ToggleMissions();
                else if (_charTab) ToggleCharacters();
                else { _view.SelectedFleet = -1; _view.Select(-1); }
                break;
            case Key.Home: _view.ResetView(); break;
            case Key.Equal: case Key.Plus: case Key.KpAdd: _view.ZoomCentre(1.25f); break;
            case Key.Minus: case Key.KpSubtract: _view.ZoomCentre(1f / 1.25f); break;
            case Key.Key1: SetSpeed(0); break;
            case Key.Key2: SetSpeed(1); break;
            case Key.Key3: SetSpeed(2); break;
            case Key.Key4: SetSpeed(3); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    // ================================================================== log and reports

    static Color KindColor(MsgKind k) => k switch
    {
        MsgKind.Good => new Color(0.55f, 0.95f, 0.55f),
        MsgKind.Bad => new Color(1f, 0.5f, 0.45f),
        MsgKind.Warn => new Color(1f, 0.85f, 0.4f),
        MsgKind.Battle => new Color(0.6f, 0.8f, 1f),
        _ => new Color(0.85f, 0.87f, 0.92f),
    };

    bool PassesFilter(Message m) => _logFilter switch
    {
        LogFilter.Battles => m.Combat || m.Kind == MsgKind.Battle,
        LogFilter.Mine => m.System == null || S.OwnerOf(m.System) == S.Player,
        _ => true,
    };

    void SetLogFilter(LogFilter f)
    {
        _logFilter = f;
        for (int i = 0; i < _filterButtons.Count; i++) _filterButtons[i].SetPressedNoSignal(i == (int)f);
        _log.Clear(); _logMessages.Clear();
        foreach (var m in _allMessages) if (PassesFilter(m)) AppendLogRow(m);
        ScrollLogToEnd();
    }

    void AddLogItem(Message m)
    {
        if (_log == null) return;
        _allMessages.Add(m);
        if (_allMessages.Count > MaxLogItems) _allMessages.RemoveAt(0);
        if (!PassesFilter(m)) return;
        AppendLogRow(m);
        ScrollLogToEnd();
    }

    void AppendLogRow(Message m)
    {
        _logMessages.Add(m);
        _log.AddItem($"Day {m.Day}: {m.Text}");
        int idx = _log.ItemCount - 1;
        _log.SetItemCustomFgColor(idx, KindColor(m.Kind));
        _log.SetItemTooltipEnabled(idx, false);
        if (_log.ItemCount > MaxLogItems) { _log.RemoveItem(0); _logMessages.RemoveAt(0); }
    }

    void ScrollLogToEnd() => Callable.From(() => { var sb = _log.GetVScrollBar(); sb.Value = sb.MaxValue; }).CallDeferred();

    void OnLogClicked(long index, Vector2 pos, long button)
    {
        if (index < 0 || index >= _logMessages.Count) return;
        var m = _logMessages[(int)index];
        if (m.System != null && G.TryFindSystem(m.System, out int idx)) { _view.Centre(idx); _view.Select(idx); }
        if (m.Detail != null) ShowReport(m);
    }

    void ShowReport(Message m)
    {
        _reportTitle.Text = m.Title ?? "Report";
        _reportBody.Text = m.Detail != null && m.Kind == MsgKind.Battle ? "Battle rules: " + GameBoot.RulesName(S.Rules) + "\n" + m.Detail : m.Detail ?? m.Text;
        _reportPanel.Visible = true;
        if (m.System != null && G.TryFindSystem(m.System, out int idx)) _view.Centre(idx);
    }

    void ShowVictory(Side winner)
    {
        foreach (var c in _victoryPanel.GetChildren()) { _victoryPanel.RemoveChild(c); c.QueueFree(); }
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        v.AddThemeConstantOverride("separation", 14);
        _victoryPanel.AddChild(v);
        bool won = winner == S.Player;
        var t = new Label { Text = won ? "VICTORY" : "DEFEAT", HorizontalAlignment = HorizontalAlignment.Center };
        t.AddThemeFontSizeOverride("font_size", 48);
        t.AddThemeColorOverride("font_color", won ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.45f, 0.4f));
        v.AddChild(t);
        v.AddChild(new Label { Text = $"The {S.NameOf(winner)} has won the war, on day {S.Game.Day}. ({GameBoot.WinReason(S.Host)})", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        v.AddChild(Btn("Look at the map", () => _victoryPanel.Visible = false));
        v.AddChild(Btn("Back to the start screen", () => BackToMenu?.Invoke()));
        _victoryPanel.Visible = true;
    }
}
