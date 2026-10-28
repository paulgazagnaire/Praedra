using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The drawer on the right: one fixed column with tabs (Needs you, Selected, Fleets, Leaders, Missions, Log). It never covers the map, it
/// keeps its place while the map moves, and a click on the map shows the clicked thing in Selected. The helpers here (Line, Heading, Act,
/// Card) are what every tab builds with, so the tabs look alike.
/// </summary>
public partial class GameScreen
{
    enum DrawerTab { Needs, Selected, Fleets, Leaders, Missions, Log }

    DrawerTab _tab = DrawerTab.Needs;
    readonly Dictionary<DrawerTab, Button> _tabButtons = new();
    ScrollContainer _scroll;
    VBoxContainer _pv;          // the content of the current tab (the Log tab has its own)
    VBoxContainer _into;        // where Line/Act/Heading add: _pv, or the card being filled
    Control _logHost;
    const int DrawerWidth = 440;

    Side Me => S.Player;
    FleetModule Fm => S.Host.Fleets;

    Control BuildDrawer()
    {
        var drawer = new PanelContainer { CustomMinimumSize = new Vector2(DrawerWidth, 0), SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Stop };
        var st = Ui.Box(Ui.Chrome, 0, null, 0, 0, 0);
        st.BorderColor = Ui.Line; st.BorderWidthLeft = 1;
        drawer.AddThemeStyleboxOverride("panel", st);
        var v = Ui.VBox(0);
        drawer.AddChild(v);

        var tabsBox = new PanelContainer();
        var ts = Ui.Box(Ui.Chrome, 0, null, 0, 8, 2);
        ts.BorderColor = Ui.Line; ts.BorderWidthBottom = 1;
        tabsBox.AddThemeStyleboxOverride("panel", ts);
        v.AddChild(tabsBox);
        var tabs = Ui.HBox(0);
        tabsBox.AddChild(tabs);
        var group = new ButtonGroup();
        foreach (var (tab, name, tip) in new[]
        {
            (DrawerTab.Needs, "Needs you", "Everything that needs a decision from you, worst first (Q)"),
            (DrawerTab.Selected, "Selected", "The system or fleet you clicked on the map"),
            (DrawerTab.Fleets, "Fleets", "All your fleets: where they are and where they go (F)"),
            (DrawerTab.Leaders, "Leaders", "Your leaders, and the enemy leaders you have seen (C)"),
            (DrawerTab.Missions, "Missions", "Your agents' missions (M)"),
            (DrawerTab.Log, "Log", "Every message, by kind (E)"),
        })
        {
            var t = tab;
            var b = Ui.Button(name, () => ShowTab(t), tip, "Tab");
            b.ToggleMode = true; b.ButtonGroup = group; b.ButtonPressed = tab == _tab;
            b.AddThemeStyleboxOverride("normal", TabBox(false)); // a slimmer tab than the theme's, six must fit
            b.AddThemeStyleboxOverride("hover", TabBox(false, Ui.LineHi));
            b.AddThemeStyleboxOverride("pressed", TabBox(true));
            b.AddThemeStyleboxOverride("hover_pressed", TabBox(true));
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.AddThemeFontSizeOverride("font_size", 13); // six tabs need air between them
            _tabButtons[tab] = b;
            tabs.AddChild(b);
        }

        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        v.AddChild(_scroll);
        var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_left", Ui.Pad + 4); margin.AddThemeConstantOverride("margin_right", Ui.Pad + 4);
        margin.AddThemeConstantOverride("margin_top", Ui.Pad + 2); margin.AddThemeConstantOverride("margin_bottom", Ui.Group);
        _scroll.AddChild(margin);
        _pv = Ui.VBox(Ui.Block);
        _pv.CustomMinimumSize = new Vector2(DrawerWidth - 2 * (Ui.Pad + 4) - 4, 0);
        margin.AddChild(_pv);

        _logHost = BuildLogTab();
        _logHost.Visible = false;
        v.AddChild(_logHost);
        return drawer;
    }

    static StyleBoxFlat TabBox(bool on, Color? hover = null)
    {
        var s = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = on ? Ui.You : hover ?? new Color(0, 0, 0, 0) };
        s.BorderWidthBottom = 3;
        s.ContentMarginLeft = 4; s.ContentMarginRight = 4; s.ContentMarginTop = 9; s.ContentMarginBottom = 7;
        return s;
    }

    void ShowTab(DrawerTab t)
    {
        if (_plan != null && t != DrawerTab.Missions && !_pickMission) _plan = null; // leaving the planner closes it
        _tab = t;
        foreach (var (k, b) in _tabButtons) b.SetPressedNoSignal(k == t);
        _scroll.Visible = t != DrawerTab.Log;
        _logHost.Visible = t == DrawerTab.Log;
        if (t == DrawerTab.Log) { ScrollLogToEnd(); _unread = 0; }
        _panelDirty = true; _panelTimer = 0;
        RefreshTabTitles();
    }

    void RefreshTabTitles()
    {
        // the count is what wants a decision (red and yellow); chances to do better never make the tab shout
        int n = _attention.Count(a => a.Level != Level.Info);
        var b = _tabButtons[DrawerTab.Needs];
        b.Text = n == 0 ? "Needs you" : $"Needs you {n}";
        bool urgent = _attention.Any(a => a.Level == Level.Danger);
        b.AddThemeColorOverride("font_color", n == 0 ? Ui.Text3 : urgent ? Ui.Danger : Ui.Warn);
        b.AddThemeColorOverride("font_hover_color", n == 0 ? Ui.Text : urgent ? new Color("ff9a9a") : new Color("ffe08a"));
        b.AddThemeColorOverride("font_pressed_color", n == 0 ? Ui.Text : urgent ? new Color("ff9a9a") : new Color("ffe08a"));
        int f = S.Game.State.Fleets.Values.Count(x => x.Side == Me);
        _tabButtons[DrawerTab.Fleets].Text = $"Fleets {f}";
        _tabButtons[DrawerTab.Log].Text = _unread == 0 ? "Log" : $"Log {_unread}";
    }

    // ================================================================== the panel switch

    void RebuildPanel()
    {
        if (_tab == DrawerTab.Log) { RefreshTabTitles(); return; }
        float scroll = _scroll.ScrollVertical;
        string key = PanelKey();
        ClearPanel();
        switch (_tab)
        {
            case DrawerTab.Needs: NeedsPanel(); break;
            case DrawerTab.Fleets: FleetsPanel(); break;
            case DrawerTab.Leaders: CharactersPanel(); break;
            case DrawerTab.Missions: if (_plan != null) MissionPlanner(); else MissionsPanel(); break;
            default:
                // only own fleets can be selected: enemy fleets are known by what was seen, not by id (rule: m5-fog-and-hq §N8)
                if (_view.SelectedFleet >= 0 && S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var fleet) && fleet.Side == Me) FleetPanel(fleet);
                else
                {
                    if (_view.SelectedFleet >= 0) _view.SelectedFleet = -1;
                    if (_view.Selected >= 0) SystemPanel(_view.Selected);
                    else HelpPanel();
                }
                break;
        }
        RefreshTabTitles();
        // the same thing rebuilt (a new day) keeps its scroll; a new thing starts at the top
        if (key == _lastPanelKey) Callable.From(() => _scroll.ScrollVertical = (int)scroll).CallDeferred();
        else _scroll.ScrollVertical = 0;
        _lastPanelKey = key;
    }

    string _lastPanelKey;
    string PanelKey() => $"{_tab}|{_view.Selected}|{_view.SelectedFleet}|{_sysTab}|{_plan?.Home}";

    void ClearPanel() { Ui.Clear(_pv); _into = null; }

    // ================================================================== small helpers every tab uses

    T Add<T>(T c) where T : Control { (_into ?? _pv).AddChild(c); return c; }

    Label Line(string text, Color? color = null, int size = 0)
    {
        var l = Ui.Label(text, null, color, size);
        return Add(l);
    }

    /// <summary>A muted explanation line.</summary>
    Label Note(string text) => Add(Ui.Label(text, "Hint"));

    /// <summary>A section head: a clear gap above it (a new group starts), then the name in small capitals.</summary>
    void Heading(string text, string tip = null)
    {
        Add(Ui.Space(Ui.Group - Ui.Block * 2));
        var l = Add(Ui.Section(text));
        if (tip != null) { l.TooltipText = Ui.Wrap(tip); l.MouseFilter = MouseFilterEnum.Stop; l.MouseDefaultCursorShape = CursorShape.Help; }
    }

    Button Act(string text, Action a, string tip = null, bool disabled = false, string variation = null) => Add(Ui.Action(text, a, tip, disabled, variation));

    /// <summary>A row of buttons that share the width.</summary>
    HBoxContainer Buttons(params Button[] buttons)
    {
        var h = Ui.HBox(6);
        foreach (var b in buttons) { b.SizeFlagsHorizontal = SizeFlags.ExpandFill; b.CustomMinimumSize = new Vector2(0, 36); h.AddChild(b); }
        return Add(h);
    }

    /// <summary>Lines added inside <c>using (Card(...))</c> go into a framed card.</summary>
    IDisposable Card(Color? bg = null, Color? border = null, int gap = 6)
    {
        var card = Ui.Card(bg ?? Ui.Panel, border ?? Ui.Line, Ui.Pad, 8);
        Add(card);
        var v = Ui.VBox(gap);
        card.AddChild(v);
        var prev = _into;
        _into = v;
        return new Restore(() => _into = prev);
    }

    sealed class Restore : IDisposable
    {
        readonly Action _a;
        public Restore(Action a) => _a = a;
        public void Dispose() => _a();
    }

    /// <summary>The panel title: a big name, a line under it, and tags on the right.</summary>
    void Title(string name, string sub, params (string Text, Color Color)[] tags)
    {
        var h = Ui.HBox(8);
        var t = Ui.Label(name, "Title");
        h.AddChild(t);
        foreach (var (text, color) in tags) h.AddChild(Ui.Tag(text, color));
        Add(h);
        if (sub != null) Add(Ui.Label(sub, "Dim", size: Ui.Small));
    }

    /// <summary>A key / value row with an optional explanation on hover.</summary>
    HBoxContainer Stat(string key, string value, Color? color = null, string tip = null) => Add(Ui.Row(key, value, color, tip));

    string Counts(IEnumerable<string> names)
    {
        var parts = names.GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} x {g.Key}").ToList();
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    static string Plural(int n, string one, string many) => n == 1 ? one : many;

    static readonly Color Dim = Ui.Text3;
    static readonly Color Bad = Ui.Danger;
    static readonly Color Good = Ui.Good;
    static readonly Color Pending = Ui.Warn;

    // ================================================================== Selected, with nothing selected

    void HelpPanel()
    {
        Title("Nothing selected", "Click a system or one of your fleets on the map.");
        using (Card())
        {
            Line("Click a system to see it, and to build there if it is yours.", Ui.Text2, Ui.Small);
            Line("Click one of your fleets (blue triangles) to give it orders. A number on a triangle means several fleets are there: Tab steps through them.", Ui.Text2, Ui.Small);
            Line("Needs you (Q) lists what wants a decision, worst first. Press Play (Space) and the game stops by itself when it matters.", Ui.Text2, Ui.Small);
        }
        Heading("Keys");
        foreach (var (k, what) in new[]
        {
            ("Space", "play / pause"), ("N", "one day"), ("1 - 4", "speed"), ("Q  F  C  M  E", "Needs you, Fleets, Leaders, Missions, Log"),
            ("S", "sector windows"), ("V", "next map layer"), ("L", "routes on / off"), ("/", "search"), ("G", "goal"), ("Ctrl/Cmd + S", "quick save"),
            ("Wheel, + -", "zoom"), ("Drag, arrows", "pan"), ("Esc", "cancel / deselect"),
        })
            Stat(k, what);
    }

    // ================================================================== the Fleets tab (C5)

    /// <summary>One short line for the fleet list: where it is, where it is going and when it gets there.</summary>
    string StatusOf(Fleet f)
    {
        if (f.Transit is { } t && f.At == null) return $"moving to {t.Path[^1]}, arrives day {GameSession.ArrivalDay(t)}";
        if (S.PlannedMoveOf(f.Id) is { } pm) return $"at {f.At}; moving to {pm.To}, arrives day {pm.ArrivalDay}";
        if (S.PendingOf(f.Id).Any()) return $"at {f.At}; order waiting";
        return $"at {f.At}";
    }

    void FleetsPanel()
    {
        var mine = S.Game.State.Fleets.Values.Where(f => f.Side == Me).ToList();
        Title("Your fleets", mine.Count == 0 ? "You have no fleet." : $"{Ui.Count(mine.Count, "fleet", "fleets")}, {Ui.Count(mine.Sum(f => f.Ships.Count), "ship", "ships")}. Click one to select it.");
        bool AtEnemy(Fleet f) => f.At != null && (S.OwnerOf(f.At) != Me || S.VisibleEnemyFleetsAt(f.At).Count > 0);
        var groups = new[]
        {
            ("Facing the enemy", mine.Where(f => f.At != null && S.VisibleEnemyFleetsAt(f.At).Count > 0).ToList()),
            ("At enemy or neutral systems", mine.Where(f => f.At != null && S.VisibleEnemyFleetsAt(f.At).Count == 0 && S.OwnerOf(f.At) != Me).ToList()),
            ("Moving", mine.Where(f => f.At == null || S.PlannedMoveOf(f.Id) != null).Where(f => !AtEnemy(f) || f.At == null).ToList()),
            ("At your systems", mine.Where(f => f.At != null && !AtEnemy(f) && S.PlannedMoveOf(f.Id) == null).ToList()),
        };
        foreach (var (head, list) in groups)
        {
            if (list.Count == 0) continue;
            Heading($"{head} ({list.Count})");
            foreach (var f in list.OrderBy(f => f.At ?? f.Transit?.Path[^1], StringComparer.Ordinal).ThenBy(f => f.Id)) FleetRow(f);
        }
    }

    void FleetRow(Fleet f)
    {
        var fl = f;
        var b = Ui.Button($"{f.Name}\n{StatusOf(f)}", () => SelectFleet(fl.Id, true), $"{f.Name}: {StatusOf(f)}. Strength {Fm.StrengthOf(f):0}. Click to select it.", "Row");
        b.Alignment = HorizontalAlignment.Left;
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        b.ClipText = true;
        var stats = Ui.Label($"{f.Ships.Count} ships   {Fm.StrengthOf(f):0}", null, Ui.Text3, Ui.Small, wrap: false);
        stats.HorizontalAlignment = HorizontalAlignment.Right;
        stats.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
        stats.OffsetLeft = -120; stats.OffsetRight = -12; stats.OffsetTop = 9;
        b.AddChild(stats);
        if (_view.SelectedFleet == f.Id) b.AddThemeStyleboxOverride("normal", Ui.Box(Ui.YouDeep, 6, Ui.You, 1, 12, 8));
        Add(b);
    }

    internal IEnumerable<string> FleetListTexts() =>
        S.Game.State.Fleets.Values.Where(f => f.Side == Me).Select(f => $"{f.Name}\n{StatusOf(f)}");

    public void SelectFleet(int id, bool centre)
    {
        if (!S.Game.State.Fleets.TryGetValue(id, out var f) || f.Side != Me) return;
        _view.SelectedFleet = id;
        string sys = f.At ?? f.Transit?.Path[f.Transit.HopIndex + 1];
        if (centre && sys != null && G.TryFindSystem(sys, out int idx)) _view.Centre(idx);
        _tab = DrawerTab.Selected;
        ShowTab(DrawerTab.Selected);
        OnViewChanged();
        _panelDirty = true; _panelTimer = 0;
    }

    public void SelectSystem(string name)
    {
        if (G.TryFindSystem(name, out int idx)) { _view.Select(idx); OnViewChanged(); ShowTab(DrawerTab.Selected); }
    }

    /// <summary>Tab / Shift-Tab: the next of the player's fleets at the selected fleet's system.</summary>
    void CycleFleetHere(int dir)
    {
        if (!S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var f) || f.At == null) return;
        var here = S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.At == f.At).OrderBy(x => x.Id).ToList();
        if (here.Count < 2) return;
        int i = here.FindIndex(x => x.Id == f.Id);
        SelectFleet(here[((i + dir) % here.Count + here.Count) % here.Count].Id, false);
    }
}
