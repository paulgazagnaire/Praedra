using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;
using Rebellion.Core.Clock;
using Rebellion.Core.Fleets;
using Rebellion.Core.Ships;
using Rebellion.Core.Map;
using Rebellion.Core.Travel;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>
/// Debug viewer root: top bar (day, play/pause, speed, step), the galaxy map, and the info panel.
/// All rules live in Rebellion.Core; this only loads the data, calls Game.Step(), and draws.
/// </summary>
public partial class Main : Control
{
    // View-only constants (no rules). Seed is fixed so every run of the viewer is the same game.
    const ulong ViewerSeed = 1998UL;
    static readonly int[] Speeds = { 1, 2, 5, 10 };       // days per second
    const int MaxStepsPerFrame = 30;                       // keeps the window responsive at 10x on a slow frame
    const int ReferenceSpeed = TravelFormula.DefaultSpeed; // hop days shown in the panel are for speed 100
    const float PanPixelsPerSecond = 700f;

    Galaxy _galaxy;
    IReadOnlyDictionary<string, ShipClass> _ships;
    BlockadeData _blockade;
    Game _game;
    FleetModule _fm;
    const int LogLines = 12;
    readonly List<string> _log = new();
    int _logPrinted;
    string _lastContact;
    Label _logLabel;
    PanelContainer _logPanel;
    GalaxyView _view;
    FleetLayer _fleets;
    Label _dayLabel, _speedLabel;
    Button _playButton;
    readonly List<Button> _speedButtons = new();
    PanelContainer _panel;
    Label _panelTitle, _panelBody;
    ScrollContainer _panelScroll;

    bool _playing;
    int _speedIndex;
    double _acc;

    string _shotPath;
    int _shotFrames = 30;
    int _frame;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        bool smoke = args.Contains("--smoke") || DisplayServer.GetName() == "headless";

        LoadData();
        StartDemo();

        if (smoke)
        {
            for (int i = 0; i < 40; i++) _game.Step();
            GD.Print($"OK {_galaxy.Systems.Count} systems, {_galaxy.Lanes.Count} lanes, {_game.State.Fleets.Count} fleets, day {_game.Day}");
            foreach (var f in _game.State.Fleets.Values)
                GD.Print($"  {f.Name} ({f.Side}): {Where(f)}");
            for (; _logPrinted < _game.Events.Count; _logPrinted++) { var l = EventLine(_game.Events[_logPrinted]); if (l != null) GD.Print("  " + l); }
            GetTree().Quit();
            return;
        }

        BuildUi();
        _view.Init(_galaxy);
        _view.StateChanged += RefreshPanel;
        _view.FleetPicker = _fleets.Pick;
        _fleets.Bind(_game, _galaxy);
        RefreshDay();
        RefreshPanel();
        ApplyArgs(args);
    }

    /// <summary>(Re)creates the game with the fixed seed and the demo fleets. Used at start and by R.</summary>
    void StartDemo()
    {
        _game = new Game(ViewerSeed);
        _fm = DemoScenario.Build(_game, _galaxy, _ships, _blockade);
        _log.Clear(); _logPrinted = 0; _lastContact = null;
        _playing = false; _acc = 0;
        if (_playButton != null) _playButton.Text = "Play (Space)";
        if (_fleets != null) { _fleets.Bind(_game, _galaxy); _view.SelectedFleet = -1; RefreshLog(); RefreshDay(); RefreshPanel(); }
    }

    /// <summary>Reads the data tables shipped as data/*.txt: the galaxy, ship classes and blockade settings.</summary>
    void LoadData()
    {
        string Read(string csvName)
        {
            string path = "res://data/" + csvName.Replace(".csv", ".txt");
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f == null) throw new InvalidOperationException($"Cannot open {path}: {FileAccess.GetOpenError()}");
            return f.GetAsText();
        }
        _galaxy = LoadGalaxy(Read);
        var ships = new Dictionary<string, ShipClass>(StringComparer.Ordinal);
        foreach (var c in ShipData.Parse(Read(ShipData.CapitalShipsFile), ShipData.CapitalShipsFile, true)) ships[c.Name] = c;
        foreach (var c in ShipData.Parse(Read(ShipData.FightersFile), ShipData.FightersFile, false)) ships[c.Name] = c;
        _ships = ships;
        _blockade = BlockadeData.Parse(Read(BlockadeData.FileName), BlockadeData.FileName);
    }

    static Galaxy LoadGalaxy(Func<string, string> Read)
    {
        var tiers = LaneTiers.Parse(Read(GalaxyLoader.TiersFile), GalaxyLoader.TiersFile);
        var g = GalaxyLoader.Load(Read(GalaxyLoader.PlanetsFile), Read(GalaxyLoader.LanesFile), tiers);
        ExpectedGalaxyCounts.Parse(Read(GalaxyLoader.CountsFile)).Check(g);
        return g;
    }

    void ApplyArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screenshot" when i + 1 < args.Length:
                    _shotPath = args[i + 1];
                    if (i + 2 < args.Length && int.TryParse(args[i + 2], out int fr)) _shotFrames = fr;
                    break;
                case "--zoom" when i + 1 < args.Length:
                    _view.SetZoom(float.Parse(args[i + 1], CultureInfo.InvariantCulture));
                    break;
                case "--focus" when i + 1 < args.Length:
                    if (_galaxy.TryFindSystem(args[i + 1], out int idx))
                    {
                        float z = 4f;
                        if (i + 2 < args.Length && float.TryParse(args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float zz)) z = zz;
                        _view.FocusOn(idx, z);
                        _view.Select(idx);
                    }
                    break;
                case "--fleet" when i + 1 < args.Length:
                    foreach (var f in _game.State.Fleets.Values) if (f.Name == args[i + 1]) _view.SelectedFleet = f.Id;
                    RefreshPanel();
                    break;
                case "--days" when i + 1 < args.Length:
                    for (int d = int.Parse(args[i + 1]); d > 0; d--) AfterStep(_game);
                    RefreshDay();
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- UI

    void BuildUi()
    {
        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 0);
        AddChild(vbox);

        // top bar
        var bar = new PanelContainer();
        vbox.AddChild(bar);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        bar.AddChild(row);

        _dayLabel = new Label();
        _dayLabel.AddThemeFontSizeOverride("font_size", 22);
        _dayLabel.CustomMinimumSize = new Vector2(130, 0);
        row.AddChild(_dayLabel);

        _playButton = MakeButton("Play (Space)", TogglePlay);
        _playButton.CustomMinimumSize = new Vector2(120, 0);
        row.AddChild(_playButton);

        row.AddChild(new Label { Text = "Speed:" });
        var group = new ButtonGroup();
        for (int i = 0; i < Speeds.Length; i++)
        {
            int idx = i;
            var b = MakeButton($"{Speeds[i]}x", () => SetSpeed(idx));
            b.ToggleMode = true;
            b.ButtonGroup = group;
            b.ButtonPressed = i == 0;
            _speedButtons.Add(b);
            row.AddChild(b);
        }
        _speedLabel = new Label { Text = "days per second" };
        _speedLabel.Modulate = new Color(1, 1, 1, 0.6f);
        row.AddChild(_speedLabel);

        row.AddChild(MakeButton("Step 1 day (N)", StepOne));
        row.AddChild(MakeButton("Reset view (Home)", () => _view.ResetView()));

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(spacer);
        var hint = new Label { Text = "wheel/+/-: zoom   drag/arrows: pan   click: system or fleet   R: restart" };
        hint.Modulate = new Color(1, 1, 1, 0.6f);
        row.AddChild(hint);

        // map
        _view = new GalaxyView { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _view.FocusMode = FocusModeEnum.None;
        vbox.AddChild(_view);
        _fleets = new FleetLayer { Name = "FleetLayer" };
        _view.AddChild(_fleets);

        // event log (bottom left, over the map; ignores the mouse)
        _logPanel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _logPanel.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _logPanel.OffsetLeft = 10; _logPanel.OffsetBottom = -10;
        _logPanel.GrowVertical = GrowDirection.Begin;
        var logStyle = new StyleBoxFlat { BgColor = new Color(0.05f, 0.06f, 0.09f, 0.82f) };
        logStyle.SetContentMarginAll(8);
        _logPanel.AddThemeStyleboxOverride("panel", logStyle);
        AddChild(_logPanel);
        _logLabel = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _logLabel.AddThemeFontSizeOverride("font_size", 14);
        _logPanel.AddChild(_logLabel);

        // info panel (top right, over the map)
        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        _panel.SetAnchorsPreset(LayoutPreset.TopRight);
        _panel.OffsetLeft = -340; _panel.OffsetRight = -10; _panel.OffsetTop = 56;
        _panel.GrowHorizontal = GrowDirection.Begin;
        var style = new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.12f, 0.96f), BorderColor = new Color(0.4f, 0.45f, 0.6f), CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4 };
        style.SetBorderWidthAll(1); style.SetContentMarginAll(10);
        _panel.AddThemeStyleboxOverride("panel", style);
        AddChild(_panel);
        var pv = new VBoxContainer();
        _panel.AddChild(pv);
        _panelTitle = new Label();
        _panelTitle.AddThemeFontSizeOverride("font_size", 20);
        pv.AddChild(_panelTitle);
        _panelScroll = new ScrollContainer { CustomMinimumSize = new Vector2(310, 0) };
        _panelScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        pv.AddChild(_panelScroll);
        _panelBody = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _panelScroll.AddChild(_panelBody);
    }

    Button MakeButton(string text, Action onPressed)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None };
        b.Pressed += onPressed;
        return b;
    }

    // ---------------------------------------------------------------- clock controls

    void TogglePlay()
    {
        _playing = !_playing;
        _playButton.Text = _playing ? "Pause (Space)" : "Play (Space)";
    }

    void SetSpeed(int idx)
    {
        _speedIndex = Math.Clamp(idx, 0, Speeds.Length - 1);
        for (int i = 0; i < _speedButtons.Count; i++) _speedButtons[i].SetPressedNoSignal(i == _speedIndex);
    }

    void StepOne()
    {
        AfterStep(_game);   // the only thing that advances the game is Game.Step()
        RefreshDay();
    }

    void AfterStep(Game g)
    {
        g.Step();
        for (; _logPrinted < g.Events.Count; _logPrinted++)
        {
            var line = EventLine(g.Events[_logPrinted]);
            if (line != null) _log.Add(line);
        }
        if (_logLabel != null) RefreshLog();
    }

    void RefreshDay()
    {
        _dayLabel.Text = $"Day {_game.Day}";
        if (_view.SelectedFleet >= 0) RefreshPanel();
    }

    void RefreshLog()
    {
        _logLabel.Text = string.Join("\n", _log.Skip(Math.Max(0, _log.Count - LogLines)));
        _logPanel.Visible = _log.Count > 0;
    }

    // ---- event log wording: same style as the command-line runner's day log (sim/Program.cs) ----

    string Describe(int id) => _game.State.Fleets.TryGetValue(id, out var f) ? Describe(f) : $"fleet {id}";
    static string Describe(Fleet f) => $"{(f.Side == Side.Empire ? "Imperial" : "Rebel")} fleet {f.Name}";
    string Describe(IEnumerable<int> ids) => string.Join(" and ", ids.Select(Describe));

    string EventLine(GameEvent e)
    {
        string text = e switch
        {
            OrderAccepted { Fleet: int f } a =>
                $"{Describe(f)} is ordered {a.Path!.Items[0]} -> {a.Path.Items[^1]}; {a.TotalTicks} days, due day {a.ArrivalDay}",
            OrderAccepted => null,
            OrderRejected r => $"order #{r.Sequence} rejected ({r.Reason})",
            FleetDeparted d => $"{Describe(d.Fleet)} leaves {d.From} (first hop {d.FirstHopTicks} days)",
            FleetPassed p => $"{Describe(p.Fleet)} passes {p.System}",
            FleetArrived a => $"{Describe(a.Fleet)} arrives at {a.System}",
            ForcedStop x => $"{Describe(x.Fleet)} is stopped at {x.System} by {Describe(x.Blockers.Items)}",
            Contact c => ContactLine(c),
            BlockadeStarted b => $"{b.Blockader} fleet blockades {b.System}",
            BlockadeLifted b => $"blockade of {b.System} by the {b.Blockader} ends",
            _ => null,
        };
        return text == null ? null : $"Day {e.Day}: {text}";
    }

    // A Contact is raised every day while both sides are present; the log shows it once until the fleets change.
    string ContactLine(Contact c)
    {
        string key = $"{c.System}|{c.EmpireFleets}|{c.AllianceFleets}";
        if (key == _lastContact) return null;
        _lastContact = key;
        return $"contact at {c.System}: {Describe(c.AllianceFleets.Items)} meets {Describe(c.EmpireFleets.Items)}";
    }

    string Where(Fleet f) => f.Transit is { } t && f.At == null
        ? $"in transit {t.Path[t.HopIndex]} -> {t.Path[t.HopIndex + 1]}, next stop day {t.DueDay}, destination {t.Path[^1]}"
        : $"at {f.At}";

    public override void _Process(double delta)
    {
        if (_view == null) return;

        if (_playing)
        {
            _acc += delta * Speeds[_speedIndex];
            int steps = 0;
            while (_acc >= 1.0 && steps < MaxStepsPerFrame) { AfterStep(_game); _acc -= 1.0; steps++; }
            if (_acc > MaxStepsPerFrame) _acc = 0; // drop the backlog instead of spiralling
            if (steps > 0) RefreshDay();
        }

        _fleets.DayFraction = _playing ? (float)Math.Min(_acc, 0.999) : 0f;

        var pan = Vector2.Zero;
        if (Input.IsKeyPressed(Key.Left)) pan.X -= 1;
        if (Input.IsKeyPressed(Key.Right)) pan.X += 1;
        if (Input.IsKeyPressed(Key.Up)) pan.Y -= 1;
        if (Input.IsKeyPressed(Key.Down)) pan.Y += 1;
        if (pan != Vector2.Zero) _view.PanPixels(pan * PanPixelsPerSecond * (float)delta);

        if (_shotPath != null && ++_frame >= _shotFrames)
        {
            GetViewport().GetTexture().GetImage().SavePng(_shotPath);
            GD.Print("Saved screenshot " + _shotPath);
            _shotPath = null;
            GetTree().Quit();
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k) return;
        switch (k.Keycode)
        {
            case Key.Space: TogglePlay(); break;
            case Key.N: StepOne(); break;
            case Key.R: StartDemo(); break;
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

    // ---------------------------------------------------------------- info panel

    void ShowFleet(Fleet f)
    {
        var sb = new StringBuilder();
        sb.Append($"{(f.Side == Side.Empire ? "Empire" : "Alliance")} fleet (pinned)\n");
        var all = f.Ships.Concat(f.Squadrons).GroupBy(x => x).Select(g => $"{g.Count()} x {g.Key}");
        sb.Append($"Ships: {string.Join(", ", all)}\n");
        sb.Append($"Speed: {_fm.SpeedOf(f)}\n");
        if (f.Transit is { } t && f.At == null)
        {
            int finalDay = t.DueDay;
            for (int h = t.HopIndex + 1; h < t.HopTicks.Count; h++) finalDay += t.HopTicks[h];
            sb.Append($"In transit {t.Path[t.HopIndex]} -> {t.Path[t.HopIndex + 1]}, arrives day {t.DueDay}\n");
            sb.Append($"Destination: {t.Path[^1]} (day {finalDay})");
        }
        else sb.Append($"At {f.At}\nNo destination");
        _panelTitle.Text = f.Name;
        _panelBody.Text = sb.ToString();
        _panelScroll.CustomMinimumSize = new Vector2(310, 130);
    }

    void RefreshPanel()
    {
        if (_panel == null) return;
        if (_view.SelectedFleet >= 0 && _game.State.Fleets.TryGetValue(_view.SelectedFleet, out var fleet))
        {
            ShowFleet(fleet);
            return;
        }
        int i = _view.Selected >= 0 ? _view.Selected : _view.Hovered;
        if (i < 0)
        {
            _panelTitle.Text = "Galaxy";
            _panelBody.Text = $"{_galaxy.Systems.Count} systems, {_galaxy.Sectors.Count} sectors, {_galaxy.Lanes.Count} lanes\n\n" +
                "Hover a system for details.\nClick to pin it.\n\n" +
                "Lanes: gold thick = route, blue medium = official, faint thin = generated.";
            _panelScroll.CustomMinimumSize = new Vector2(310, 240);
            return;
        }
        var s = _galaxy.Systems[i];
        var nbs = _galaxy.NeighboursOf(i);
        var sb = new StringBuilder();
        sb.Append(_view.Selected == i ? "(pinned)\n" : "");
        sb.Append($"Sector: {(s.IsOutsideSectors ? "none (outside sectors)" : s.Sector)}\n");
        sb.Append($"Coordinates: {s.MapX}, {s.MapY}\n");
        sb.Append($"Lanes: {nbs.Count}\n");
        if (nbs.Count > 0) sb.Append($"\nNeighbours (days at speed {ReferenceSpeed}):\n");
        foreach (var nb in nbs)
        {
            int days = TravelFormula.HopTicks(nb.Lane, ReferenceSpeed); // core formula, never re-implemented here
            string tier = nb.Lane.Tier switch { 1 => "route", 2 => "official", _ => "generated" };
            sb.Append($"  {_galaxy.Systems[nb.System].Name}: {days}d  ({tier})\n");
        }
        _panelTitle.Text = s.Name;
        _panelBody.Text = sb.ToString().TrimEnd('\n');
        _panelScroll.CustomMinimumSize = new Vector2(310, Math.Min(560, 20 + 26 * (nbs.Count + 6)));
    }
}
