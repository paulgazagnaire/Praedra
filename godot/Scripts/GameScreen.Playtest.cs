using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>Finds and clicks real buttons in a node tree (used by the headless --playtest mode).</summary>
public static class UiTest
{
    public static T Find<T>(Node root, string prefix) where T : BaseButton
    {
        foreach (var c in root.GetChildren())
        {
            if (c is T b && b.IsVisibleInTree() && TextOf(b).StartsWith(prefix, StringComparison.Ordinal)) return b;
            var r = Find<T>(c, prefix);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>Meta key for a short button's full words ("Move..." on a leader's card is "Move Nobody..."); the finder matches either.</summary>
    public const string FullName = "full_name";

    static string TextOf(BaseButton b) => b.HasMeta(FullName) ? b.GetMeta(FullName).AsString() : b switch { Button x => x.Text ?? "", _ => "" };

    public static bool Has(Node root, string prefix) => Find<Button>(root, prefix) != null;

    /// <summary>Click the first visible button starting with <paramref name="prefix"/>: sets the toggle state like a click would, then raises Pressed.
    /// Returns false if there is none or it is disabled (a real click would do nothing).</summary>
    public static bool Click(Node root, string prefix)
    {
        var b = Find<Button>(root, prefix);
        if (b == null || b.Disabled) return false;
        if (b.ToggleMode && !b.ButtonPressed) b.SetPressedNoSignal(true);
        b.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    public static IEnumerable<string> Texts(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Button b && b.IsVisibleInTree()) yield return TextOf(b);
            foreach (var t in Texts(c)) yield return t;
        }
    }
}

/// <summary>Test hooks used by the headless --playtest mode: they click the real buttons of the real panels (the same Pressed signal a mouse click raises).</summary>
public partial class GameScreen
{
    /// <summary>Where a mouse can click now: only the dialog on top while one is open (the veil blocks the rest), else the whole screen.</summary>
    Node ClickRoot => _modalStack.Count > 0 ? _modalStack[^1] : this;
    internal bool Has(string prefix) => UiTest.Has(ClickRoot, prefix) || (_menuPopup.Visible && UiTest.Has(_menuPopup, prefix));
    internal bool Enabled(string prefix) => (UiTest.Find<Button>(MenuFirst, prefix) ?? UiTest.Find<Button>(ClickRoot, prefix)) is { Disabled: false };
    internal bool Click(string prefix) => (_menuPopup.Visible && _modalStack.Count == 0 && UiTest.Click(_menuPopup, prefix)) || UiTest.Click(ClickRoot, prefix);

    /// <summary>What a player does for a window that is not open: open the menu and click it there (the menu closes itself).
    /// Kept apart from Click so a button dropped from the screen still fails its check.</summary>
    internal bool ClickViaMenu(string prefix)
    {
        if (_modalStack.Count > 0 || _menuPopup.Visible) return false;
        ToggleMenu();
        if (UiTest.Click(_menuPopup, prefix)) return true;
        ToggleMenu();
        return false;
    }
    Node MenuFirst => _menuPopup.Visible && _modalStack.Count == 0 ? _menuPopup : ClickRoot;
    internal IEnumerable<string> ButtonTexts() => UiTest.Texts(ClickRoot);

    /// <summary>What the frame loop does for a dirty panel, at once.</summary>
    internal void PlayRefresh()
    {
        _panelDirty = false; _fleetListDirty = false; _attentionDirty = false;
        RefreshAttention(); RebuildPanel(); RefreshTop();
    }

    internal bool PlayDay() { var r = RunDay(); PlayRefresh(); return r; }
    internal bool HowToVisible => _howPanel.Visible;
    internal bool CharactersOpen => _drawer.Visible && _tab == DrawerTab.Leaders;
    internal bool MissionsOpen => _drawer.Visible && _tab == DrawerTab.Missions && _plan == null;
    internal bool PlannerOpen => _plan != null;
    internal string TabName => _tab.ToString();
    internal void OpenTab(string name) { if (Enum.TryParse<DrawerTab>(name, out var t)) ShowTab(t); PlayRefresh(); }
    internal (System.Collections.Generic.List<int> Chars, System.Collections.Generic.List<int> Sfs) PlanTeam => (_plan?.Chars.ToList() ?? new(), _plan?.Sfs.ToList() ?? new());
    /// <summary>Tick or untick a team member in the mission planner (the CheckBox's toggle, as a click would).</summary>
    internal bool SetTeam(string prefix, bool on)
    {
        foreach (var c in AllNodes(_pv)) if (c is CheckBox cb && cb.Text.StartsWith(prefix, StringComparison.Ordinal)) { cb.ButtonPressed = on; return true; }
        return false;
    }
    internal void PressKey(Key k, bool ctrl = false) { var e = new InputEventKey { Keycode = k, Pressed = true, CtrlPressed = ctrl }; _UnhandledKeyInput(e); }
    internal bool ConfirmVisible => _confirmPanel.Visible;
    internal string ConfirmTitle => _confirmTitle.Text;
    internal string ConfirmText => _confirmBody.Text;
    internal bool ReportVisible => _reportPanel.Visible;
    internal string ReportText => _reportTitle.Text + "\n" + _reportBody.Text;
    internal bool VictoryVisible => _victoryPanel.Visible;
    internal string VictoryText => LabelsIn(_victoryPanel);
    internal bool GoalVisible => _goalPanel.Visible;
    internal string GoalText => LabelsIn(_goalPanel);
    internal string GoalButtonText => _goalButton.GetMeta(UiTest.FullName).AsString();
    internal bool SavesVisible => _savePanel.Visible;
    internal bool SettingsVisible => _settingsPanel.Visible;
    internal bool MenuVisible => _menuPopup.Visible;
    internal bool AnyModal => OverlayOpen;
    internal bool Playing => _playing;
    internal int SpeedValue => Speeds[_speedIndex];
    internal string Banner => InMoveMode ? _moveBanner ?? "" : _banner.GetParent<Control>().Visible ? _banner.Text : "";
    internal string TopText => $"Day {_dayLabel.Text} | {_rawLabel.Text} | {_refinedLabel.Text} | {_upkeepLabel.Text} | {(_ordersLabel.IsVisibleInTree() ? _ordersLabel.Text : "")}";
    internal string[] TopTips => new[] { _rawLabel.TooltipText, _refinedLabel.TooltipText, _upkeepLabel.TooltipText, _dayLabel.TooltipText };
    internal string PanelText() => LabelsIn(_pv);
    static string LabelsIn(Node n) => string.Join("\n", AllNodes(n).OfType<Label>().Where(l => l.IsVisibleInTree() || !l.IsInsideTree()).Select(l => l.Text));
    internal void PickSystem(string name) { if (G.TryFindSystem(name, out int i)) OnSystemPicked(i); }
    internal void Frame(double seconds) => _Process(seconds);
    /// <summary>One frame with no time passing: runs what a click left for the next frame (true, to chain in a check).</summary>
    internal bool Step0() { _Process(0); return true; }
    internal int LogRows => _log.ItemCount;
    internal string ToastText => string.Join("\n", _toasts.SelectMany(t => AllNodes(t.Card).OfType<Label>()).Select(l => l.Text));
    internal int UnreadMessages => _unread;
    internal string HqStatusText => _hqStatusLabel.GetParent<Control>().Visible ? _hqStatusLabel.Text : "";
    internal IReadOnlyList<string> AttentionTitles => _attention.Select(a => a.Title).ToList();
    internal string OrderBarText => _orderBar.Visible ? _orderTitle.Text + "\n" + _orderBody.Text : "";
    /// <summary>The systems the sector window shows, in card order.</summary>
    internal IReadOnlyList<string> SectorViewCards => _secCards.Keys.ToList();
    /// <summary>The words on one card (owner, numbers, links), for the playtest.</summary>
    internal string SectorCardText(string system) => _secCards.TryGetValue(system, out var c) ? LabelsIn(c) : null;
    internal string SectorDetailText => SectorViewFocus == null ? "" : LabelsIn(_secDetailHead) + "\n" + LabelsIn(_secDetailBody);
    internal string SectorCardTip(string system) => _secCards.TryGetValue(system, out var c) ? c.TooltipText : null;
    internal bool SectorOnLeft => _secWin != null && _secWin.Position.X < _secLayer.Size.X / 4;
    internal float SectorWindowWidth => _secWin?.Size.X ?? 0;
    internal float ScreenWidth => _secLayer?.Size.X ?? 0;
    internal string LayerName => _layer.ToString();
    internal Praedra.Core.Clock.Side Player => S.Player;
    internal GalaxyView.MapMark? MarkOf(string system) => G.TryFindSystem(system, out int i) ? LayerMarkOf(i) : null;
    internal void SetSearch(string text) { _search.Text = text; RefreshSearch(); }
    internal IReadOnlyList<string> SearchHits => _hits.Select(h => h.Text).ToList();
    internal void SubmitSearch() { if (_hits.Count > 0) PickHit(0); }
    internal int SelectedSystemIndex => _view.Selected;
    /// <summary>The rows of the Build tab now: name, can it be ordered here, and the reason shown when not (or the warning when it can).</summary>
    internal IReadOnlyList<(string Name, bool Usable, string Why)> BuildRowsShown => _tab == DrawerTab.Selected && _sysTab == SysTab.Build ? _shownBuildRows.Select(b => (b.Row.Name, b.Usable, b.Why)).ToList() : new();
    internal string SelectedSystemName => _view.Selected >= 0 ? G.Systems[_view.Selected].Name : null;
    /// <summary>The tags drawn on the map (system, text), as the map asks for them.</summary>
    internal IEnumerable<(string System, string Text)> MapTags() =>
        Enumerable.Range(0, G.Systems.Count).Select(i => (G.Systems[i].Name, CapitalTagOf(i)?.Text)).Where(t => t.Text != null);
    internal IReadOnlyList<string> EnemyMarkerSystems() => _fleets.EnemyMarkerSystems();
    internal IReadOnlyList<int> DrawnFleetIds() => _fleets.DrawnFleetIds();
    internal bool DrawnDestroyed(string system) => G.TryFindSystem(system, out int i) && _view.DrawsDestroyed(i);
    internal bool DrawnUnknown(string system) => G.TryFindSystem(system, out int i) && _view.IsUnknown(i);
    internal string HoverTextFor(string system) { if (!G.TryFindSystem(system, out int i)) return ""; _view.ShotHover(i); OnViewChanged(); return _hoverLabel.Text; }
    internal bool SetCheck(string prefix, bool on)
    {
        foreach (var c in AllNodes(ClickRoot == this ? _pv : ClickRoot))
            if (c is CheckButton or CheckBox && c is Button cb && cb.Text.StartsWith(prefix, StringComparison.Ordinal)) { cb.ButtonPressed = on; return true; }
        return false;
    }
    internal bool TickShip(int index)
    {
        var boxes = AllNodes(_pv).OfType<CheckBox>().ToList();
        if (index >= boxes.Count) return false;
        boxes[index].ButtonPressed = true; return true;
    }
    static IEnumerable<Node> AllNodes(Node n) { foreach (var c in n.GetChildren()) { yield return c; foreach (var d in AllNodes(c)) yield return d; } }

    /// <summary>Click the newest log row for which <paramref name="pred"/> holds (the row click handler, as a mouse would).</summary>
    internal bool ClickLog(Func<Message, bool> pred)
    {
        for (int i = _logMessages.Count - 1; i >= 0; i--)
            if (pred(_logMessages[i])) { OnLogClicked(i, Vector2.Zero, 1); return true; }
        return false;
    }
    internal void SetFilter(string name) { var b = _filterButtons.FirstOrDefault(x => x.Text == name); if (b != null) { b.SetPressedNoSignal(true); b.EmitSignal(BaseButton.SignalName.Pressed); } }
}

public partial class GameScreen
{
    // Screenshot helpers (command-line options of the exporter, see README).
    public void ShotShowHowTo() => ShowHowTo();
    /// <summary>Screenshot helper: open a drawer tab (Needs, Selected, Fleets, Leaders, Missions, Log) or a system sub-tab (Overview, Build, Forces, Fleets here).</summary>
    public void ShotOpenSection(string name)
    {
        if (Enum.TryParse<DrawerTab>(name, out var t)) ShowTab(t);
        else if (Enum.TryParse<SysTab>(name.Replace(" here", "").Replace("Fleets", "Fleets"), out var st)) { _sysTab = st; ShowTab(DrawerTab.Selected); }
        _panelDirty = true; _panelTimer = 0;
    }
    public int SelectedFleetId => _view.SelectedFleet;
    public void ShotMove(string to) { if (_view.SelectedFleet >= 0 && S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var f)) { StartMove(f); PickSystem(to); } }
    /// <summary>Screenshot helper: the selected fleet's move preview to a system, as if the mouse were over it (underscores stand for spaces).</summary>
    public void ShotPreview(string to)
    {
        if (_view.SelectedFleet < 0 || !S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var f) || !G.TryFindSystem(to.Replace('_', ' '), out int i)) return;
        StartMove(f); _view.ShotHover(i); OnViewChanged();
    }
    /// <summary>Playtest: the move banner for a fleet hovering a destination, then the move is cancelled.</summary>
    internal string PreviewBannerFor(int fleetId, string to)
    {
        SelectFleet(fleetId, false);
        ShotPreview(to);
        string text = _moveBanner + (_view.PathPreview is { Count: > 1 } ? "" : " [no line drawn]");
        CancelMove();
        return text;
    }
    internal bool RoutesShown => _view.ShowRoutes;

    /// <summary>Playtest: plan a fleet move with shift-click waypoints, hover the destination, and report the banner and what the map draws.
    /// With <paramref name="confirm"/> the plain click sends the order; otherwise the move is cancelled.</summary>
    internal (string Banner, bool Red, IReadOnlyList<string> Traps, IReadOnlyList<string> Path) PlanWithWaypoints(int fleetId, IReadOnlyList<string> waypoints, string to, bool confirm)
    {
        SelectFleet(fleetId, false);
        if (!S.Game.State.Fleets.TryGetValue(fleetId, out var f)) return ("", false, Array.Empty<string>(), Array.Empty<string>());
        StartMove(f);
        foreach (var w in waypoints) if (G.TryFindSystem(w, out int wi)) AddWaypoint(wi);
        if (!G.TryFindSystem(to, out int ti)) { CancelMove(); return ("", false, Array.Empty<string>(), Array.Empty<string>()); }
        _view.ShotHover(ti); OnViewChanged();
        var result = (_moveBanner, _moveWarn,
            (IReadOnlyList<string>)(_view.PathTraps?.Select(i => G.Systems[i].Name).ToList() ?? new List<string>()),
            (IReadOnlyList<string>)(_view.PathPreview?.Select(i => G.Systems[i].Name).ToList() ?? new List<string>()));
        if (confirm) OnSystemPicked(ti); else CancelMove();
        return result;
    }

    /// <summary>Screenshot helper: plant a seen enemy interdictor on the selected fleet's path to a system (the system is handed to the player so the
    /// picture is live), then show the preview. Underscores stand for spaces.</summary>
    public void ShotTrap(string to)
    {
        to = to.Replace('_', ' ');
        if (_view.SelectedFleet < 0 || !S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var f)) return;
        var plan = S.Host.PlanMove(Me, f.Id, to);
        if (!plan.Ok || plan.Path.Count < 3) return;
        string trap = plan.Path[plan.Path.Count / 2];
        S.OpponentIdle = true; // keep the planted ship where it is
        Fm.SetOwner(trap, Me);
        Fm.AddFleet(Me == Praedra.Core.Clock.Side.Hand ? Praedra.Core.Clock.Side.Collective : Praedra.Core.Clock.Side.Hand, "Net", new[] { "Subpoena" }, trap);
        StepOne(); // the side's picture is taken in the day step
        ShotPreview(to);
    }

    /// <summary>Screenshot helper: shift-click a waypoint in the move being planned (underscores stand for spaces).</summary>
    /// <summary>"auto" picks the system nearest the first known trap that steers the hovered move around every trap.</summary>
    public void ShotWaypoint(string name)
    {
        if (name == "auto")
        {
            if (_view.PathTraps is not { Count: > 0 } traps || _view.Hovered < 0 || !S.Game.State.Fleets.TryGetValue(_moveFleet, out var f)) return;
            string to = G.Systems[_view.Hovered].Name;
            var tp = new Vector2(G.Systems[traps[0]].MapX, G.Systems[traps[0]].MapY);
            var pick = G.Systems.Where(x => x.Name != f.At && x.Name != to)
                .OrderBy(x => tp.DistanceSquaredTo(new Vector2(x.MapX, x.MapY))).ThenBy(x => x.Name, StringComparer.Ordinal)
                .FirstOrDefault(x => S.Host.PlanMove(Me, f.Id, to, _waypoints.Append(x.Name).ToList()) is { Ok: true, Red: false });
            if (pick != null) AddWaypoint(pick.Index);
            return;
        }
        if (G.TryFindSystem(name.Replace('_', ' '), out int i)) AddWaypoint(i);
    }

    /// <summary>Screenshot helper: centre and zoom the map on a system without selecting it (underscores stand for spaces).</summary>
    public void ShotLookAt(string name, float zoom) { if (G.TryFindSystem(name.Replace('_', ' '), out int i)) _view.FocusOn(i, zoom); }
    public void ShotFilter(string name) => SetFilter(name);
    public void ShotCharacters() { ShowTab(DrawerTab.Leaders); PlayRefresh(); }
    public void ShotMissions() { ShowTab(DrawerTab.Missions); PlayRefresh(); }
    public void ShotSectorHover(string name) { _view.ShotHoverSector(name.Replace('_', ' ')); OnViewChanged(); }
    internal string HoverCardText => _hoverCard.Visible ? _hoverLabel.Text : "";
    internal void ClickSector(string name) => _view.ShotClickSector(name);
    public void ShotCloseWindow() => CloseDrawer();
    public void ShotOpenMenu() => Callable.From(() => { if (!_menuPopup.Visible) ToggleMenu(); }).CallDeferred();
    internal bool WindowOpen => _drawer.Visible;
    internal bool SelectionCardOpen => _selCard.Visible;
    internal string SelectionCardText => _selCard.Visible ? LabelsIn(_selCard) : "";
    internal string PillText => _pill.Visible ? _banner.Text : "";
    internal string MessageCornerText => _msgCorner.Visible && _msgCard.Visible ? LabelsIn(_msgCard) : "";
    /// <summary>The sector the mouse finds when it points at this system's spot on the map (the real hit test).</summary>
    internal string SectorUnder(string system) =>
        G.TryFindSystem(system, out int i) ? _view.SectorName(_view.SectorAt(_view.SystemScreen(i))) : null;
    internal string SectorUnderScreen(Vector2 p) => _view.SectorName(_view.SectorAt(p));
    public void ShotLayer(string name) { if (Enum.TryParse<MapLayer>(name, out var l)) SetLayer(l); }
    public void ShotSectors() { if (!SectorViewOpen) ToggleSectorView(); }
    public void ShotSector(string name) => OpenSector(name.Replace('_', ' '));
    public void ShotSectorPane(string system, string pane) => OpenSystemPane(system.Replace('_', ' '), Enum.TryParse(pane, true, out SecPane p) ? p : SecPane.Buildings);
    public void ShotGoal() => ShowGoal();
    public void ShotClick(string prefix) { PlayRefresh(); if (!Click(prefix)) GD.PrintErr("no button " + prefix); PlayRefresh(); }
}
