using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Map;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The game screen. Layout (Paul's mockup, 2026-10-28): the map fills the window, small dark pieces float at its edges, and everything longer
/// (a system's details, Fleets, Leaders, Missions, the log, the sector tables) opens as a window on demand. All rules live in
/// the core; this reads the state through the <see cref="GameSession"/>, submits orders and calls the session's StepDay (the only thing that
/// advances the game). The parts live in the GameScreen.*.cs files: Shell (the frame), Hud (the selection card and the message corner),
/// Drawer (the window), System, Build, Fleet, Move, Attention, Log,
/// Dialogs, Goal, Search, Characters, Missions.
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
    bool _showHowToOnStart;

    bool _playing;
    int _speedIndex = DefaultSpeedIndex;
    double _acc;
    bool _panelDirty = true, _fleetListDirty = true, _attentionDirty = true;
    double _panelTimer;
    string _pauseNote;

    // Screenshot options (set by Main from the command line).
    public string ShotPath;
    public int ShotFrames = 30;
    int _frame;

    /// <param name="newGame">true for a fresh game from the start screen (the very first one shows the How to play overlay).</param>
    public void Init(GameSession session, bool newGame = false) { S = session; _showHowToOnStart = newGame; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Theme = Ui.Theme;
        UiSettings.Load();
        UiSettings.ApplyScale(GetTree().Root);
        BuildUi();

        _view.Init(G);
        // Fog of war: owners and states of systems that are not yours come from your side's picture; unexplored systems are dim; uprisings
        // and blockades show only where you see live. rule: m5-fog-and-hq §F3, §N2, §N8
        _view.OwnerOf = i => S.KnownOwnerOf(G.Systems[i].Name).Owner;
        _view.IsUnknown = i => { string n = G.Systems[i].Name; return !S.Explored(n) && S.OwnerOf(n) != S.Player; };
        _view.IsDestroyed = i => S.IsDestroyed(G.Systems[i].Name); // rule: m5-harvester §2 effect 6 (the side's own picture knows)
        _view.IsHq = i => HqSideAt(i) != null;
        _view.CapitalTag = CapitalTagOf;
        _view.IsUprising = i => { string n = G.Systems[i].Name; return S.Sees(n) && S.Host.Support.IsUprising(n); };
        _view.IsBlockaded = i => { string n = G.Systems[i].Name; return S.Sees(n) && S.Game.State.Blockades.ContainsKey(n); };
        _view.Mark = LayerMarkOf;
        _view.StateChanged += OnViewChanged;
        _view.SystemPicked += OnSystemPicked;
        _view.SectorClicked += OpenSector;
        _view.FleetPicker = _fleets.Pick;
        _view.EnemyMarkerPicker = _fleets.PickEnemySystem;
        _fleets.Player = S.Player;
        _view.Player = S.Player;
        _view.LabelObstacles = MapLabelObstacles;
        _fleets.Planned = () => S.PlannedMoves;
        _fleets.EnemyFleets = () => S.Fog.VisibleFleets(S.Player);
        _fleets.CharacterRoutes = CharacterRoutes;
        _fleets.HqRoute = () => S.OwnHq is { Status: Praedra.Core.Fog.HqStatus.InTransit } h ? (h.Path, h.HopIndex, GameSession.HqArrivalDay(h)) : null;
        _fleets.Bind(S.Game, G);

        // Start with the map centred on the player's headquarters.
        string home = S.MyHqSystem ?? (S.OwnHq is { Status: Praedra.Core.Fog.HqStatus.InTransit } th ? th.Path[^1] : GameBoot.HandCapital);
        if (G.TryFindSystem(home, out int homeIdx)) { _view.FocusOn(homeIdx, 2.4f); }
        S.Say($"You lead the {S.NameOf(S.Player)}. Your headquarters is {home}. Press Play (or Space) to start the clock; How to play (F1) explains the rest.", MsgKind.Good, home);
        foreach (var m in S.Messages) AddLogItem(m, false);
        if (S.Game.Day == 0) _pauseNote = "Press Play (or Space) to start the clock";
        BuildSectorDock();
        RefreshTop();
        if (_showHowToOnStart && GameBoot.HowToUnseen()) ShowHowTo();
    }

    /// <summary>Whose headquarters the player knows at a system: their own always, the enemy's only when known (the Hand: KnownHq).</summary>
    Side? HqSideAt(int i)
    {
        string name = G.Systems[i].Name;
        if (name == S.MyHqSystem) return S.Player;
        if (name == S.EnemyHqSystem) return S.Enemy;
        return null;
    }

    (string Text, Color Color)? CapitalTagOf(int i)
    {
        if (HqSideAt(i) is not Side side) return null;
        if (side == S.Player) return (side == Side.Hand ? "YOUR CAPITAL" : "YOUR HQ", Ui.Gold);
        return (side == Side.Hand ? "ENEMY CAPITAL" : "COLLECTIVE HQ (TARGET)", Ui.Enemy);
    }

    // ================================================================== clock, loop

    void TogglePlay()
    {
        if (S.Winner != null || OverlayOpen) return;
        _playing = !_playing;
        _runUntilNeeded = false;
        if (_playing) { _pauseNote = null; HideModal(_reportPanel); }
        RefreshTop();
    }

    void Pause(string note = null)
    {
        _playing = false;
        _runUntilNeeded = false;
        if (note != null) _pauseNote = note;
        RefreshTop();
    }

    void SetSpeed(int idx)
    {
        _speedIndex = Math.Clamp(idx, 0, Speeds.Length - 1);
        for (int i = 0; i < _speedChips.Count; i++) _speedChips[i].SetPressedNoSignal(i == _speedIndex);
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
        foreach (var m in oc.New) AddLogItem(m, true);
        if (S.Game.Day % AutosaveEvery == 0) Autosave();
        _panelDirty = true; _fleetListDirty = true; _attentionDirty = true;
        _view.QueueRedraw(); // owners, blockades and uprisings may have changed
        RefreshSectorWindows();
        if (oc.Winner is Side w) { ShowVictory(w); Pause(); return false; }

        // The core's own pauses (a battle, a capture, an invasion, a harvest), each of which the Settings can switch off; then the events the
        // player chose to stop on; then "Run until something needs me".
        string why = oc.PauseReason != null && UiSettings.PausesOn(oc.PauseReason) ? oc.PauseReason : null;
        string note = why switch
        {
            "battle" => "Paused: a battle was fought",
            "captured" => "Paused: a system changed hands",
            "invaded" or "invasion" => "Paused: an invasion is taking place",
            "destroyed" => "Paused: the harvester destroyed a system",
            null => null,
            _ => "Paused",
        };
        if (why != null && oc.Popup != null) ShowReport(oc.Popup);
        note ??= ChosenPause(oc.New);
        if (note == null && _runUntilNeeded && NewAttention() is { } item) note = "Paused: " + item;
        if (note != null) { Pause(note); return false; }
        RefreshTop();
        return true;
    }

    void Autosave()
    {
        try { GameBoot.WriteSave(GameBoot.AutoSaveSlot, S.SaveText(), S.Player, S.Level, S.Game.Day); }
        catch (Exception e) { GD.PrintErr("Autosave failed: " + e.Message); }
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

        TickToasts(delta);

        var pan = Vector2.Zero;
        if (!OverlayOpen && GetViewport().GuiGetFocusOwner() is not LineEdit)
        {
            if (Input.IsKeyPressed(Key.Left)) pan.X -= 1;
            if (Input.IsKeyPressed(Key.Right)) pan.X += 1;
            if (Input.IsKeyPressed(Key.Up)) pan.Y -= 1;
            if (Input.IsKeyPressed(Key.Down)) pan.Y += 1;
        }
        if (pan != Vector2.Zero) _view.PanPixels(pan * PanPixelsPerSecond * (float)delta);

        _panelTimer -= delta;
        if (_attentionDirty && _panelTimer <= 0) { _attentionDirty = false; RefreshAttention(); }
        if (_panelDirty && _panelTimer <= 0) { _panelDirty = false; _panelTimer = 0.2; RebuildPanel(); RefreshTop(); }
        if (_fleetListDirty) { _fleetListDirty = false; if (_tab == DrawerTab.Fleets) _panelDirty = true; }

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
        bool cmd = k.CtrlPressed || k.MetaPressed;
        if (cmd && k.Keycode == Key.S) { QuickSave(); GetViewport().SetInputAsHandled(); return; }
        if (OverlayOpen && k.Keycode != Key.Escape && k.Keycode != Key.F1) return;
        switch (k.Keycode)
        {
            case Key.F1: ToggleHowTo(); break;
            case Key.Space: TogglePlay(); break;
            case Key.N: StepOne(); break;
            case Key.Q: ShowTab(DrawerTab.Needs); break;
            case Key.F: ShowTab(DrawerTab.Fleets); break;
            case Key.C: ToggleCharacters(); break;
            case Key.M: ToggleMissions(); break;
            case Key.E: ShowTab(DrawerTab.Log); break;
            case Key.S: ToggleSectorDock(); break;
            case Key.L: CycleLanes(); break;
            case Key.V: NextLayer(); break;
            case Key.G: ShowGoal(); break;
            case Key.Tab when _view.SelectedFleet >= 0: CycleFleetHere(k.ShiftPressed ? -1 : 1); break;
            case Key.Slash: FocusSearch(); break;
            case Key.Backspace when _moveFleet >= 0: DropWaypoint(); break;
            case Key.Escape: Escape(); break;
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

    /// <summary>Esc closes the topmost thing: a dialog, the menu, the search, a move being planned, the planner, the sector tables, the window,
    /// then the selection.</summary>
    void Escape()
    {
        if (_victoryPanel.Visible) return;
        if (_menuPopup.Visible) { ToggleMenu(); return; }
        if (CloseTopModal()) return;
        if (_searchResults.Visible) { CloseSearch(); return; }
        if (_moveFleet >= 0 || _moveHq || _moveChar >= 0 || _pickMission) { CancelMove(); return; }
        if (_plan != null) { ClosePlanner(); return; }
        if (_sectorDock.Visible) { ToggleSectorDock(); return; }
        if (_drawer.Visible) { CloseDrawer(); return; }
        if (_view.SelectedFleet >= 0 || _view.Selected >= 0) { _view.SelectedFleet = -1; _view.Select(-1); return; }
    }
}
