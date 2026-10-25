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

    static string TextOf(BaseButton b) => b switch { Button x => x.Text ?? "", _ => "" };

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
            if (c is Button b && b.IsVisibleInTree()) yield return b.Text;
            foreach (var t in Texts(c)) yield return t;
        }
    }
}

/// <summary>Test hooks used by the headless --playtest mode: they click the real buttons of the real panels (the same Pressed signal a mouse click raises).</summary>
public partial class GameScreen
{
    internal bool Has(string prefix) => UiTest.Has(this, prefix);
    internal bool Enabled(string prefix) => UiTest.Find<Button>(this, prefix) is { Disabled: false };
    internal bool Click(string prefix) => UiTest.Click(this, prefix);
    internal IEnumerable<string> ButtonTexts() => UiTest.Texts(this);

    /// <summary>What the frame loop does for a dirty panel, at once.</summary>
    internal void PlayRefresh()
    {
        _panelDirty = false; _fleetListDirty = false;
        RebuildPanel(); RefreshTop(); RebuildFleetList();
    }

    internal bool PlayDay() { var r = RunDay(); PlayRefresh(); return r; }
    internal bool HowToVisible => _howPanel.Visible;
    internal bool CharactersOpen => _charTab;
    internal bool MissionsOpen => _missionTab;
    internal bool PlannerOpen => _plan != null;
    internal (System.Collections.Generic.List<int> Chars, System.Collections.Generic.List<int> Sfs) PlanTeam => (_plan?.Chars.ToList() ?? new(), _plan?.Sfs.ToList() ?? new());
    /// <summary>Tick or untick a team member in the mission planner (the CheckBox's toggle, as a click would).</summary>
    internal bool SetTeam(string prefix, bool on)
    {
        foreach (var c in AllNodes(_pv)) if (c is CheckBox cb && cb.Text.StartsWith(prefix, StringComparison.Ordinal)) { cb.ButtonPressed = on; return true; }
        return false;
    }
    internal void PressKey(Key k) { var e = new InputEventKey { Keycode = k, Pressed = true }; _UnhandledKeyInput(e); }
    internal bool ConfirmVisible => _confirmPanel.Visible;
    internal string ConfirmTitle => _confirmTitle.Text;
    internal string ConfirmText => _confirmBody.Text;
    internal bool ReportVisible => _reportPanel.Visible;
    internal string ReportText => _reportTitle.Text + "\n" + _reportBody.Text;
    internal bool VictoryVisible => _victoryPanel.Visible;
    internal string VictoryText => string.Join("\n", _victoryPanel.GetChildren().SelectMany(c => c.GetChildren().OfType<Label>()).Select(l => l.Text));
    internal bool Playing => _playing;
    internal int SpeedIndex => _speedIndex;
    internal int SpeedValue => Speeds[_speedIndex];
    internal string Banner => _banner.Visible ? _banner.Text : "";
    internal string TopText => $"{_dayLabel.Text} | {_rawLabel.Text} | {_refinedLabel.Text} | {_upkeepLabel.Text} | {_ordersLabel.Text}";
    internal string[] TopTips => new[] { _rawLabel.TooltipText, _refinedLabel.TooltipText, _upkeepLabel.TooltipText, _dayLabel.TooltipText };
    internal string PanelText() => string.Join("\n", _pv.GetChildren().OfType<Label>().Select(l => l.Text));
    internal void PickSystem(string name) { if (G.TryFindSystem(name, out int i)) OnSystemPicked(i); }
    internal void Frame(double seconds) => _Process(seconds);
    internal IEnumerable<string> FleetListTexts() => _fleetList.GetChildren().OfType<Button>().Select(b => b.Text);
    internal int LogRows => _log.ItemCount;
    internal string ToastText => _toast.Visible ? _toast.Text : "";
    internal string HqStatusText => _hqStatusLabel.Visible ? _hqStatusLabel.Text : "";
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
        foreach (var c in AllNodes(_pv)) if (c is CheckButton cb && cb.Text.StartsWith(prefix, StringComparison.Ordinal)) { cb.ButtonPressed = on; return true; }
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
    public void ShotOpenSection(string name) { if (name.StartsWith("-")) _openSections.Remove(name[1..]); else _openSections.Add(name); _panelDirty = true; _panelTimer = 0; }
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
        Fm.AddFleet(Me == Praedra.Core.Clock.Side.Empire ? Praedra.Core.Clock.Side.Alliance : Praedra.Core.Clock.Side.Empire, "Net", new[] { "Subpoena" }, trap);
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
    public void ShotCharacters() { if (!_charTab) ToggleCharacters(); PlayRefresh(); }
    public void ShotMissions() { if (!_missionTab) ToggleMissions(); PlayRefresh(); }
    public void ShotClick(string prefix) { PlayRefresh(); if (!Click(prefix)) GD.PrintErr("no button " + prefix); PlayRefresh(); }
}
