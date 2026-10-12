using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;
using Rebellion.Core.Clock;
using Rebellion.Core.Map;
using Rebellion.Core.Travel;

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
    Game _game;
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

        _galaxy = LoadGalaxy();
        _game = new Game(ViewerSeed);

        if (smoke)
        {
            for (int i = 0; i < 10; i++) _game.Step();
            GD.Print($"OK {_galaxy.Systems.Count} systems, {_galaxy.Lanes.Count} lanes, day {_game.Day}");
            GetTree().Quit();
            return;
        }

        BuildUi();
        _view.Init(_galaxy);
        _view.StateChanged += RefreshPanel;
        RefreshDay();
        RefreshPanel();
        ApplyArgs(args);
    }

    /// <summary>Reads the design tables shipped as data/*.txt and builds the core's Galaxy.</summary>
    static Galaxy LoadGalaxy()
    {
        string Read(string csvName)
        {
            string path = "res://data/" + csvName.Replace(".csv", ".txt");
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f == null) throw new InvalidOperationException($"Cannot open {path}: {FileAccess.GetOpenError()}");
            return f.GetAsText();
        }
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
                case "--days" when i + 1 < args.Length:
                    for (int d = int.Parse(args[i + 1]); d > 0; d--) _game.Step();
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
        var hint = new Label { Text = "wheel/+/-: zoom   drag/arrows: pan   click: pin system" };
        hint.Modulate = new Color(1, 1, 1, 0.6f);
        row.AddChild(hint);

        // map
        _view = new GalaxyView { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _view.FocusMode = FocusModeEnum.None;
        vbox.AddChild(_view);
        _fleets = new FleetLayer { Name = "FleetLayer" };
        _view.AddChild(_fleets);

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
        _game.Step();   // the only thing that advances the game
        RefreshDay();
    }

    void RefreshDay() => _dayLabel.Text = $"Day {_game.Day}";

    public override void _Process(double delta)
    {
        if (_view == null) return;

        if (_playing)
        {
            _acc += delta * Speeds[_speedIndex];
            int steps = 0;
            while (_acc >= 1.0 && steps < MaxStepsPerFrame) { _game.Step(); _acc -= 1.0; steps++; }
            if (_acc > MaxStepsPerFrame) _acc = 0; // drop the backlog instead of spiralling
            if (steps > 0) RefreshDay();
        }

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

    void RefreshPanel()
    {
        if (_panel == null) return;
        int i = _view.Selected >= 0 ? _view.Selected : _view.Hovered;
        if (i < 0)
        {
            _panelTitle.Text = "Galaxy";
            _panelBody.Text = $"{_galaxy.Systems.Count} systems, {_galaxy.Sectors.Count} sectors, {_galaxy.Lanes.Count} lanes\n\n" +
                "Hover a system for details.\nClick to pin it.\n\n" +
                "Lanes: gold thick = route, blue medium = official, faint thin = generated.";
            _panelScroll.CustomMinimumSize = new Vector2(310, 190);
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
