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
    const int MaxStepsPerFrame = 30;
    const float PanPixelsPerSecond = 700f;
    const int AutosaveEvery = 30;                          // days
    const int MaxLogItems = 400;

    public GameSession S { get; private set; }
    public Galaxy G => S.Data.Galaxy;
    public GalaxyView View => _view;

    GalaxyView _view;
    FleetLayer _fleets;
    Label _dayLabel, _sideLabel, _econLabel, _ordersLabel, _banner, _hoverLabel, _toast;
    Button _playButton;
    readonly List<Button> _speedButtons = new();
    ItemList _log;
    readonly List<Message> _logMessages = new();
    PanelContainer _panel, _fleetListPanel, _reportPanel, _victoryPanel;
    VBoxContainer _pv, _fleetList;
    Label _reportTitle, _reportBody;
    readonly HashSet<int> _hqs = new();

    bool _playing;
    int _speedIndex;
    double _acc;
    bool _panelDirty = true, _fleetListDirty = true;
    double _panelTimer;
    string _pauseNote;
    double _toastTimer;

    // Screenshot options (set by Main from the command line).
    public string ShotPath;
    public int ShotFrames = 30;
    int _frame;

    public void Init(GameSession session) { S = session; }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var theme = new Theme { DefaultFontSize = 15 };
        Theme = theme;
        BuildUi();

        _view.Init(G);
        _view.OwnerOf = i => S.OwnerOf(G.Systems[i].Name);
        _view.IsHq = i => _hqs.Contains(i);
        _view.IsUprising = i => S.Host.Support.IsUprising(G.Systems[i].Name);
        _view.IsBlockaded = i => S.Game.State.Blockades.ContainsKey(G.Systems[i].Name);
        _view.StateChanged += OnViewChanged;
        _view.SystemPicked += OnSystemPicked;
        _view.FleetPicker = _fleets.Pick;
        foreach (var side in new[] { Side.Empire, Side.Alliance })
            if (G.TryFindSystem(GameBoot.CapitalOf(side), out int hq)) _hqs.Add(hq);
        _fleets.Player = S.Player;
        _fleets.Bind(S.Game, G);

        // Start with the map centred on the player's headquarters.
        if (G.TryFindSystem(GameBoot.CapitalOf(S.Player), out int home)) { _view.FocusOn(home, 3f); }
        S.Say($"You lead the {S.NameOf(S.Player)}. Your headquarters is {GameBoot.CapitalOf(S.Player)}. Press Space to start the clock.", MsgKind.Good, GameBoot.CapitalOf(S.Player));
        foreach (var m in S.Messages) AddLogItem(m);
        RefreshTop();
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

    void BuildUi()
    {
        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 0);
        AddChild(vbox);

        // ---- top bar
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", Box(new Color(0.08f, 0.09f, 0.13f), 6));
        vbox.AddChild(bar);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        bar.AddChild(row);

        _dayLabel = new Label { CustomMinimumSize = new Vector2(84, 0) };
        _dayLabel.AddThemeFontSizeOverride("font_size", 20);
        row.AddChild(_dayLabel);
        _sideLabel = new Label();
        row.AddChild(_sideLabel);
        row.AddChild(new VSeparator());
        _econLabel = new Label();
        row.AddChild(_econLabel);
        _ordersLabel = new Label();
        _ordersLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
        row.AddChild(_ordersLabel);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _playButton = Btn("Play", TogglePlay);
        _playButton.CustomMinimumSize = new Vector2(76, 0);
        row.AddChild(_playButton);
        row.AddChild(Btn("+1 day", StepOne, "Advance one day (N)"));
        var group = new ButtonGroup();
        for (int i = 0; i < Speeds.Length; i++)
        {
            int idx = i;
            var b = Btn($"{Speeds[i]}x", () => SetSpeed(idx), "Days per second (keys 1-4)");
            b.ToggleMode = true; b.ButtonGroup = group; b.ButtonPressed = i == 0;
            _speedButtons.Add(b);
            row.AddChild(b);
        }
        row.AddChild(new VSeparator());
        row.AddChild(Btn("Save", SaveGame, "Save to the one save slot"));
        row.AddChild(Btn("Load", () => LoadRequested?.Invoke(GameBoot.SaveSlot), "Load the saved game"));
        row.AddChild(Btn("Menu", () => BackToMenu?.Invoke(), "Back to the start screen (unsaved progress is lost)"));

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
        var lt = new Label { Text = "Messages (click one to go there)" };
        lt.Modulate = new Color(1, 1, 1, 0.55f);
        lt.AddThemeFontSizeOverride("font_size", 12);
        lv.AddChild(lt);
        _log = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(610, 160), AutoHeight = false, FocusMode = FocusModeEnum.None };
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
        var ft = new Label { Text = "Your fleets" };
        ft.Modulate = new Color(1, 1, 1, 0.6f);
        fv.AddChild(ft);
        var fs = new ScrollContainer { CustomMinimumSize = new Vector2(230, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        fs.CustomMinimumSize = new Vector2(230, 150);
        fv.AddChild(fs);
        _fleetList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        fs.AddChild(_fleetList);

        // right-hand panel
        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        _panel.AddThemeStyleboxOverride("panel", Box(new Color(0.07f, 0.08f, 0.12f, 0.96f), 10, new Color(0.4f, 0.45f, 0.6f)));
        _panel.SetAnchorsPreset(LayoutPreset.TopRight);
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

    // ================================================================== clock, loop

    void TogglePlay()
    {
        if (S.Winner != null) return;
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
        if (S.Winner != null) return;
        RunDay();
    }

    /// <summary>One day. Returns false when something asked for a pause.</summary>
    bool RunDay()
    {
        var oc = S.StepDay();
        _retreatPending = null;
        foreach (var m in oc.New) AddLogItem(m);
        if (S.Game.Day % AutosaveEvery == 0) Autosave();
        _panelDirty = true; _fleetListDirty = true;
        if (oc.Winner is Side w) { ShowVictory(w); Pause(); return false; }
        if (oc.Popup != null) ShowReport(oc.Popup);
        if (oc.PauseReason != null)
        {
            Pause(oc.PauseReason switch
            {
                "battle" => "Paused: a battle was fought",
                "captured" => "Paused: a system changed hands",
                "invaded" or "invasion" => "Paused: an invasion is taking place",
                _ => "Paused",
            });
            return false;
        }
        RefreshTop();
        return true;
    }

    void Autosave()
    {
        try { GameBoot.WriteSave(GameBoot.AutoSaveSlot, S.SaveText(), S.Player); }
        catch (Exception e) { GD.PrintErr("Autosave failed: " + e.Message); }
    }

    public void SaveGame()
    {
        try
        {
            GameBoot.WriteSave(GameBoot.SaveSlot, S.SaveText(), S.Player);
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
        _sideLabel.Text = $"  {S.NameOf(S.Player)}";
        _sideLabel.AddThemeColorOverride("font_color", S.Player == Side.Empire ? GalaxyView.EmpireColor : GalaxyView.AllianceColor);
        var eco = S.Host.Economy;
        int load = eco.Load(S.Player), cap = eco.Capacity(S.Player);
        _econLabel.Text = $"Raw {eco.Raw(S.Player)}   Refined {eco.Refined(S.Player)}   Upkeep {load}/{cap}";
        _econLabel.AddThemeColorOverride("font_color", load > cap ? new Color(1f, 0.4f, 0.35f) : Colors.White);
        _econLabel.TooltipText = "Raw: ore waiting for refineries. Refined: what you spend on building. Upkeep: what your ships, troops and buildings cost to keep, against what your planets can support. Over the limit, units get scrapped.";
        int q = S.QueuedOrders;
        _ordersLabel.Text = q > 0 ? $"  {q} order(s) will be carried out when the next day starts" : "";
        _playButton.Text = _playing ? "Pause" : "Play";
        string banner = null;
        if (_moveFleet >= 0) banner = _moveBanner;
        else if (!_playing && _pauseNote != null) banner = _pauseNote;
        _banner.Text = banner ?? "";
        _banner.Visible = banner != null;
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
        if (Input.IsKeyPressed(Key.Left)) pan.X -= 1;
        if (Input.IsKeyPressed(Key.Right)) pan.X += 1;
        if (Input.IsKeyPressed(Key.Up)) pan.Y -= 1;
        if (Input.IsKeyPressed(Key.Down)) pan.Y += 1;
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
        switch (k.Keycode)
        {
            case Key.Space: TogglePlay(); break;
            case Key.N: StepOne(); break;
            case Key.Escape:
                if (_victoryPanel.Visible) return;
                if (_reportPanel.Visible) _reportPanel.Visible = false;
                else if (_moveFleet >= 0) CancelMove();
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

    void AddLogItem(Message m)
    {
        if (_log == null) return;
        _logMessages.Add(m);
        _log.AddItem($"Day {m.Day}: {m.Text}");
        int idx = _log.ItemCount - 1;
        _log.SetItemCustomFgColor(idx, KindColor(m.Kind));
        _log.SetItemTooltipEnabled(idx, false);
        if (_log.ItemCount > MaxLogItems) { _log.RemoveItem(0); _logMessages.RemoveAt(0); }
        Callable.From(() => { var sb = _log.GetVScrollBar(); sb.Value = sb.MaxValue; }).CallDeferred();
    }

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
        _reportBody.Text = m.Detail ?? m.Text;
        _reportPanel.Visible = true;
        if (m.System != null && G.TryFindSystem(m.System, out int idx)) _view.Centre(idx);
    }

    void ShowVictory(Side winner)
    {
        foreach (var c in _victoryPanel.GetChildren()) c.QueueFree();
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        v.AddThemeConstantOverride("separation", 14);
        _victoryPanel.AddChild(v);
        bool won = winner == S.Player;
        var t = new Label { Text = won ? "VICTORY" : "DEFEAT", HorizontalAlignment = HorizontalAlignment.Center };
        t.AddThemeFontSizeOverride("font_size", 48);
        t.AddThemeColorOverride("font_color", won ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.45f, 0.4f));
        v.AddChild(t);
        v.AddChild(new Label { Text = $"The {S.NameOf(winner)} has won the war, on day {S.Game.Day}.", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        v.AddChild(Btn("Back to the start screen", () => BackToMenu?.Invoke()));
        _victoryPanel.Visible = true;
    }
}
