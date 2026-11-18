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

    static string TextOf(BaseButton b) => b.HasMeta(FullName) ? b.GetMeta(FullName).AsString() : b switch { Button x => x.Text ?? "", LinkButton l => l.Text ?? "", _ => "" };

    public static bool Has(Node root, string prefix) => Find<BaseButton>(root, prefix) != null;

    /// <summary>Click the first visible button starting with <paramref name="prefix"/>: sets the toggle state like a click would, then raises Pressed.
    /// Returns false if there is none or it is disabled (a real click would do nothing).</summary>
    public static bool Click(Node root, string prefix)
    {
        var b = Find<BaseButton>(root, prefix);   // a button or a link (the windows' links, 2026-11-14)
        if (b == null || b.Disabled) return false;
        if (b.ToggleMode && !b.ButtonPressed) b.SetPressedNoSignal(true);
        b.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    public static IEnumerable<string> Texts(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is BaseButton b && (b is Button or LinkButton) && b.IsVisibleInTree()) yield return TextOf(b);
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
    internal bool Enabled(string prefix) => (UiTest.Find<BaseButton>(MenuFirst, prefix) ?? UiTest.Find<BaseButton>(ClickRoot, prefix)) is { Disabled: false };
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
    internal int LogRows => _log.ItemCount;
    internal string ToastText => string.Join("\n", _toasts.SelectMany(t => AllNodes(t.Card).OfType<Label>()).Select(l => l.Text));
    internal string HqStatusText => _hqStatusLabel.GetParent<Control>().Visible ? _hqStatusLabel.Text : "";
    internal IReadOnlyList<string> AttentionTitles => _attention.Select(a => a.Title).ToList();
    internal string OrderBarText => _orderBar.Visible ? _orderTitle.Text + "\n" + _orderBody.Text : "";
    /// <summary>The systems the sector window shows, in card order.</summary>
    internal IReadOnlyList<string> SectorViewCards => _secCards.Keys.ToList();
    /// <summary>The words on one card (owner, numbers, links), for the playtest.</summary>
    // the tile draws its name (Ui/SystemTile.cs): read it from the tile's data, with any labels it still holds
    internal string SectorCardText(string system) => _secCards.TryGetValue(system, out var c)
        ? string.Join("\n", c.GetChildren().OfType<SystemTile>().Select(t => t.D.Name).Append(LabelsIn(c)).Where(x => !string.IsNullOrEmpty(x))) : null;
    /// <summary>What one sector tile draws (colours, name), for the playtest.</summary>
    internal SystemTile.Data SectorTileData(string system) => _secCards.TryGetValue(system, out var c) ? c.GetChildren().OfType<SystemTile>().FirstOrDefault()?.D : null;
    internal string SectorCardTip(string system) => _secCards.TryGetValue(system, out var c) ? c.TooltipText : null;
    internal bool SectorOnLeft => _secWin != null && _secWin.Position.X < _secLayer.Size.X / 4;
    internal float SectorWindowWidth => _secWin?.Size.X ?? 0;
    internal string MarkedSectorName => _view.SectorName(_view.MarkedSector);
    internal bool BottomLeftShown => _bottomLeft.Visible;

    /// <summary>
    /// Paul's top bar (ui-update-nov-15): the stocks, the day and the clock are in it; it and what it opens (search results, the layers
    /// list) are over the windows and under the notes and dialogs; the windows start under it. At the widths the interface sizes leave
    /// (1440 px at 100%, then 110%, 125%, 140%) its two sides fit without meeting: at full width as drawn, narrower by shrinking the
    /// search, then dropping the stocks' names. Null, or what is wrong.
    /// </summary>
    internal string TopBarProblem()
    {
        int Layer(Node n) { while (n != null && n.GetParent() != this) n = n.GetParent(); return n?.GetIndex() ?? -1; }
        bool InBar(Control c) => c != null && c.FindParent("TopBar") == _barLayer;
        if (!InBar(_rawLabel) || !InBar(_refinedLabel) || !InBar(_upkeepLabel) || !InBar(_dayLabel) || !InBar(_playButton) || !InBar(_goalButton) || !InBar(_search))
            return "the stocks, day, clock, goal and search are not all in the top bar";
        if (_speedChips.Count != 3) return $"{_speedChips.Count} speed buttons, not 1x 2x 5x";
        if (!(Layer(_mgLayer) < Layer(_barLayer) && Layer(_barLayer) < Layer(_noteLayer) && Layer(_noteLayer) < Layer(_modalLayer)))
            return "the top bar is not between the windows and the notes";
        if (_searchResults.GetParent() != _barLayer || _layersCard.GetParent() != _barLayer) return "the search results or the layers list do not hang from the top bar";
        if (ClampWin(new Vector2(100, 0), new Vector2(400, 300)).Y <= _searchBox.GetCombinedMinimumSize().Y)
            return "a window dragged to the top would cover the bar's boxes";
        foreach (var l in new[] { _goalCaption, _rawCaption, _refinedCaption, _upkeepCaption, _sideLabel, _dayLabel })
            if (l.GetCombinedMinimumSize().X <= 0) return $"the bar's word \"{l.Text}\" has no width";
        // an open drop-down pushes the short notes below it, and lets a dragged chip through to the window under it
        bool wasOpen = _layersCard.Visible;
        if (!wasOpen) ToggleLayers();
        PlaceNotes();
        float listBottom = _layersCard.GlobalPosition.Y - _noteLayer.GlobalPosition.Y + _layersCard.GetCombinedMinimumSize().Y;
        bool below = _toastBox.OffsetTop >= listBottom;
        SyncNoteLayer(true);
        bool through = _layersCard.MouseBehaviorRecursive == MouseBehaviorRecursiveEnum.Disabled && _searchResults.MouseBehaviorRecursive == MouseBehaviorRecursiveEnum.Disabled;
        SyncNoteLayer(false);
        bool back = _layersCard.MouseBehaviorRecursive != MouseBehaviorRecursiveEnum.Disabled;
        if (!wasOpen) ToggleLayers();
        PlaceNotes();
        if (!below) return $"with the layers list open the notes start at {_toastBox.OffsetTop:0}, over the list (to {listBottom:0})";
        if (!through || !back) return "the drop-downs do not let a dragged chip through (or keep blocking it after)";
        // one drop-down at a time: they hang side by side and the results are wider than the search box
        if (_layersCard.Visible) ToggleLayers();
        ToggleLayers();
        SetSearch("a");
        bool searchClosedLayers = _searchResults.Visible && !_layersCard.Visible;
        ToggleLayers();
        bool layersClosedSearch = _layersCard.Visible && !_searchResults.Visible;
        ToggleLayers();
        CloseSearch();
        if (wasOpen) ToggleLayers();
        PlaceNotes();
        if (!searchClosedLayers || !layersClosedSearch) return "the search results and the layers list can be open together, one over the other";
        // the drop-downs speak the bar's pixel words (Paul 2026-11-15, "Yes A"), and a hit reached with the arrow keys is lit
        foreach (var lb in _layerButtons.Append(_routesChip).Append(_deliveriesChip).Append(_legendButton))
            if (lb.GetThemeFont("font") != BarFont) return $"the layers list's \"{lb.Text}\" is not in the bar's pixel font";
        // a word with é is set on BitPotion's line (the second font's line trimmed to it), so it stands level with its neighbours
        var plainLine = new TextLine(); plainLine.AddString("Charter", BarFont, BarTextSize);
        var wideLine = new TextLine(); wideLine.AddString("Ciénaga", BarFontFor("Ciénaga"), BarTextSize);
        if (BarFontFor("Ciénaga") != BarFontWide || BarFontWide is not FontVariation { Fallbacks.Count: > 0 })
            return "a name with a letter BitPotion lacks (Ciénaga) has no second font for it";
        if (Math.Abs(wideLine.GetLineAscent() - plainLine.GetLineAscent()) > 0.5f || Math.Abs(wideLine.GetLineDescent() - plainLine.GetLineDescent()) > 0.5f)
            return $"a word with é sits on another line ({wideLine.GetLineAscent():0}+{wideLine.GetLineDescent():0} px) than BitPotion's ({plainLine.GetLineAscent():0}+{plainLine.GetLineDescent():0})";
        SetSearch("chart");
        var hit = _searchList.GetChildCount() > 0 ? _searchList.GetChild(0) as Button : null;
        var hitWords = hit?.FindChildren("*", "Label", true, false).OfType<Label>().ToList();
        bool pixel = hitWords is { Count: 2 } && hitWords.All(l => l.GetThemeFont("font") == BarFontWide) && ((FontVariation)BarFontWide).BaseFont == BarFont;
        hit?.GrabFocus();
        bool lit = hitWords is { Count: 2 } && hitWords[1].GetThemeColor("font_color") == BarValue;
        hit?.ReleaseFocus();
        bool unlit = hitWords is { Count: 2 } && hitWords[1].GetThemeColor("font_color") == BarLabel;
        CloseSearch();
        if (wasOpen && !_layersCard.Visible) ToggleLayers();   // the search closed it
        if (!pixel) return "the search results are not in the bar's pixel font";
        if (!lit || !unlit) return "a search hit reached with the arrow keys is not lit, or stays lit after";
        string why = null;
        foreach (float w in new[] { 1440f, 1440f / 1.1f, 1440f / 1.25f, 1440f / 1.4f })
        {
            PlaceBarIn(w);
            float left = _barLeft.GetCombinedMinimumSize().X, right = _barRight.GetCombinedMinimumSize().X;
            if (BarSide + left + BarMinGap + right + BarSide > w + 0.5f) { why = $"at {w:0} px the bar's sides meet ({left:0} + {right:0})"; break; }
            if (w == 1440f && (_barLevel != 0 || _searchBox.CustomMinimumSize.X != SearchWide)) { why = "at 1440 px the bar is not as Paul drew it"; break; }
        }
        PlaceBar();
        return why;
    }

    /// <summary>The dialogs that hold a body, each with a way to open it (a long report for the report).</summary>
    IEnumerable<(string Name, Action Open, Control Panel)> Dialogs() => new (string, Action, Control)[]
    {
        ("How you win", ShowGoal, _goalPanel), ("Load a game", () => ShowSaves(false), _savePanel), ("Save the game", () => ShowSaves(true), _savePanel),
        ("Settings", ShowSettings, _settingsPanel), ("How to play", ShowHowTo, _howPanel), ("Victory", () => ShowVictory(S.Player), _victoryPanel),
        ("Report", () => ShowReport(new Message(S.Game.Day, "A long report", null, MsgKind.Info, "A long report", string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Line {i} of a long report."))) ), _reportPanel),
    };

    /// <summary>
    /// QA-105: at 140% the screen is 1029 x 579 and the Goal dialog was 628 px tall, Load 581. Every dialog must fit that screen less its
    /// margins, its body scrolling for the rest, with the head and the buttons still in it. Null, or what is wrong. Puts the dialogs back as they were.
    /// </summary>
    internal string DialogFitProblem(float screen = 579)
    {
        var open = _modalStack.ToList();
        string why = null;
        foreach (var (name, show, panel) in Dialogs())
        {
            show();
            if (!_dialogBodies.TryGetValue(panel, out var b) || b.Holder.GetParent()?.GetParent() != panel) { why = $"\"{name}\" has no body that can scroll"; break; }
            FitDialogIn(panel, screen);
            float tall = panel.GetCombinedMinimumSize().Y, room = screen - 2 * DialogMargin;
            if (tall > room + 1) { why = $"\"{name}\" is {tall:0} px tall on a {screen:0} px screen (room {room:0})"; break; }
            bool scrolls = b.Body.GetCombinedMinimumSize().Y > b.Holder.CustomMinimumSize.Y;
            int right = (int)b.Pad.GetThemeConstant("margin_right");
            if (scrolls && right >= BarReach) { why = $"\"{name}\" scrolls but its text runs under the bar"; break; }
        }
        foreach (var (_, _, panel) in Dialogs()) HideModal(panel);
        foreach (var p in open) if (!_modalStack.Contains(p)) { _modalStack.Add(p); p.Visible = true; }
        _modalLayer.Visible = _modalStack.Count > 0;
        foreach (var p in _modalStack) FitDialog(p);
        return why;
    }
    internal float ScreenWidth => _secLayer?.Size.X ?? 0;
    internal string LayerName => _layer.ToString();
    internal Praedra.Core.Clock.Side Player => S.Player;
    internal GalaxyView.MapMark? MarkOf(string system) => G.TryFindSystem(system, out int i) ? LayerMarkOf(i) : null;
    internal void SetSearch(string text) { _search.Text = text; RefreshSearch(); }
    internal IReadOnlyList<string> SearchHits => _hits.Select(h => $"{h.Name}   {h.Detail}").ToList();
    internal void SubmitSearch() { if (_hits.Count > 0) PickHit(0); }
    internal int SelectedSystemIndex => _view.Selected;
    /// <summary>The rows of the Build tab now: name, can it be ordered here, and the reason shown when not (or the warning when it can).</summary>
    internal IReadOnlyList<(string Name, bool Usable, string Why)> BuildRowsShown => _tab == DrawerTab.Selected && _sysTab == SysTab.Build ? _shownBuildRows.Select(b => (b.Row.Name, b.Usable, b.Why)).ToList() : new();
    internal string SelectedSystemName => _view.Selected >= 0 ? G.Systems[_view.Selected].Name : null;
    internal IReadOnlyList<string> EnemyMarkerSystems() => _fleets.EnemyMarkerSystems();
    internal IReadOnlyList<int> DrawnFleetIds() => _fleets.DrawnFleetIds();
    internal IReadOnlyList<string> MoverLabels() => _fleets.MoverLabels();
    /// <summary>Press Layers > Deliveries as a click would; returns whether the row is now lit.</summary>
    internal bool PressDeliveries() { _deliveriesChip.EmitSignal(BaseButton.SignalName.Pressed); return _deliveriesChip.ButtonPressed; }
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
    internal void TileClick(string system, string hot) => OpenFromTile(system, hot);

    /// <summary>--window ground|troops|fighters|fleets|infra Charter[:Ships], or --window fleet 3[:Ships]: open a management window (for
    /// infra, the build tab to show).</summary>
    public void ShotWindow(string kind, string what)
    {
        if (kind.Equals("pair", StringComparison.OrdinalIgnoreCase))
        {
            // a leader who can command and a fleet of yours at the same system: open both and hold the leader as if dragged
            var cm = S.Host.Characters;
            var pair = S.Game.State.Fleets.Values.Where(f => f.Side == S.Player && f.At != null && f.Transit == null).OrderBy(f => f.Id)
                .SelectMany(f => S.MyCharacters.Where(c => c.AttachedFleet == 0 && c.Transit == null && cm.LocationOf(c) == f.At && cm.ClassOf(c).Can(Praedra.Core.Characters.CharacterPost.Admiral))
                    .OrderBy(c => c.Id).Select(c => (f, c))).FirstOrDefault();
            if (pair.f == null) { GD.PrintErr("no leader stands with a fleet"); return; }
            OpenMgmt(MgKind.Ground, pair.f.At); OpenMgmt(MgKind.Fleet, fleet: pair.f.Id);
            if (what != "-") MgShowDrag($"L:{pair.c.Id}");
            return;
        }
        if (kind.Equals("fleet", StringComparison.OrdinalIgnoreCase)) 
        {
            // a fleet id, or a system name: then your first fleet standing there
            // "3" or "Charter", optionally ":Ships" for the tab
            string ftab = what.Contains(':') ? what[(what.IndexOf(':') + 1)..] : null;
            if (ftab != null) what = what[..what.IndexOf(':')];
            int id = int.TryParse(what, out int n) ? n : S.Game.State.Fleets.Values.Where(f => f.Side == S.Player && f.At == what.Replace('_', ' ') && f.Transit == null)
                .OrderBy(f => f.Id).Select(f => f.Id).FirstOrDefault();
            if (id > 0) OpenMgmt(MgKind.Fleet, fleet: id, tab: ftab);
            return;
        }
        // ground (Leaders), infra or build, troops, fighters, fleets: a system's window on that tab
        if (what == "HQ") what = S.MyHqSystem ?? what;   // your own headquarters, wherever it stands
        if (kind.ToLowerInvariant() is "infra" or "build")
        {
            // "Charter" or "Charter:Troops": the build tab to show (Facilities, Ships, Fighters, Troops)
            if (what.Contains(':')) { _buildSection = what[(what.IndexOf(':') + 1)..]; what = what[..what.IndexOf(':')]; }
            OpenMgmt(MgKind.Infra, what.Replace('_', ' '));
            return;
        }
        string tab = kind.ToLowerInvariant() switch { "troops" => "Troops", "fighters" => "Fighters", "fleets" => "Fleets", "lore" => "Lore", _ => "Leaders" };
        OpenMgmt(MgKind.Ground, what.Replace('_', ' '), tab: tab);
    }
    public void ShotGoal() => ShowGoal();
    /// <summary>--dialog NAME (screenshots only): opens that dialog ("How you win", "Load a game", "Settings", "Victory", "Report"...).</summary>
    public void ShotDialog(string name)
    {
        foreach (var (n, show, _) in Dialogs()) if (n.StartsWith(name.Replace('_', ' '), StringComparison.OrdinalIgnoreCase)) { show(); return; }
        GD.PrintErr("no dialog " + name);
    }

    /// <summary>--strike SYSTEM (screenshots only): a harvester fleet of yours appears there and destroys it, then one day runs, to show a harvested system.</summary>
    public void ShotStrike(string system)
    {
        system = system.Replace('_', ' ');
        int id = S.Host.Fleets.AddFleet(S.Player, "Strike", new[] { Praedra.Core.Harvester.HarvesterModule.ShipClass }, system);
        S.SubmitDestroySystem(S.Game.State.Fleets[id]);
        StepOne();
        HideModal(_reportPanel);   // the shot shows the window, not the harvest report the strike opens
    }
    public void ShotClick(string prefix) { PlayRefresh(); if (!Click(prefix)) GD.PrintErr("no button " + prefix); PlayRefresh(); }
}
