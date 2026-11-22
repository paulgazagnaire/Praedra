using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The window on the right (it was a fixed drawer until Paul's mockup of 2026-10-28): closed until you ask for something longer than the
/// floating pieces show (a system's details, Fleets, Leaders, Missions, the log, Needs you), with tabs to step between those, and closed
/// again with its cross or Esc. A click on the map only fills the selection card; the window follows the selection while it is open.
/// The helpers here (Line, Heading, Act, Card) are what every tab builds with, so the tabs look alike. It has the windows' look (Paul
/// 2026-11-14, impeccable critique): their surface and edge, their tabs (words with a 3 px line under the chosen one), × pinned right,
/// outlined cards and buttons, Space Grotesk and the four greys.
/// </summary>
public partial class GameScreen
{
    enum DrawerTab { Needs, Selected, Fleets, Leaders, Missions, Log }

    DrawerTab _tab = DrawerTab.Needs;
    readonly Dictionary<DrawerTab, Button> _tabButtons = new();
    HBoxContainer _tabRow;
    ScrollContainer _scroll;
    VBoxContainer _pv;          // the content of the current tab (the Log tab has its own)
    VBoxContainer _into;        // where Line/Act/Heading add: _pv, or the card being filled
    Control _logHost;
    const int DrawerWidth = 440;

    Side Me => S.Player;
    FleetModule Fm => S.Host.Fleets;

    PanelContainer _drawer;

    Control BuildDrawer()
    {
        using var look = Ui.Window();
        var drawer = _drawer = Ui.Hud(0, 0);
        drawer.Theme = Ui.WindowTheme;
        drawer.Visible = false;
        drawer.SetAnchorsPreset(LayoutPreset.RightWide);
        drawer.GrowHorizontal = GrowDirection.Begin;
        drawer.OffsetRight = -Edge; drawer.OffsetLeft = -Edge - DrawerWidth;
        drawer.OffsetTop = TopClear; drawer.OffsetBottom = -Edge;
        var v = Ui.VBox(0);
        drawer.AddChild(v);

        // the tab row on the window's 16 px margin, with no line under it: the chosen tab's underline is the line (Paul's "styles" frame)
        var tabsBox = Pad(WinPad, WinPad, WinPad, 0);
        v.AddChild(tabsBox);
        var tabs = Ui.HBox(10);
        _tabRow = tabs;
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
            var b = Ui.Button(name, () => ShowTab(t), tip, "TabChip");   // drawn like a window's tabs
            b.ToggleMode = true; b.ButtonGroup = group; b.ButtonPressed = tab == _tab;
            _tabButtons[tab] = b;
            tabs.AddChild(b);
        }
        var close = Ui.Button("×", CloseDrawer, "Close (Esc)", "Clock");   // a window's ×, pushed to the right edge
        close.SetMeta(UiTest.FullName, "Close window");
        close.CustomMinimumSize = new Vector2(WinButton, WinButton);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        close.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.ShrinkEnd;
        tabs.AddChild(close);

        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        v.AddChild(_scroll);
        var margin = Pad(WinPad, WinPad, WinGap, WinPad);   // 24 px under the tabs, 16 at the sides, as in a window
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.AddChild(margin);
        _pv = Ui.VBox(Ui.Block);
        _pv.CustomMinimumSize = new Vector2(DrawerWidth - 2 * WinPad - 2 - 4, 0);
        margin.AddChild(_pv);

        _logHost = BuildLogTab();
        _logHost.Visible = false;
        v.AddChild(_logHost);
        return drawer;
    }

    void CloseDrawer()
    {
        if (_plan != null && !_pickMission) _plan = null;
        _drawer.Visible = false;
        RefreshTop();
    }

    void ShowTab(DrawerTab t)
    {
        if (_plan != null && t != DrawerTab.Missions && !_pickMission) _plan = null; // leaving the planner closes it
        CloseSectorView(); // the window opens on the map, not under the sector window
        _tab = t;
        bool opening = !_drawer.Visible;
        _drawer.Visible = true;
        foreach (var (k, b) in _tabButtons) b.SetPressedNoSignal(k == t);
        _scroll.Visible = t != DrawerTab.Log;
        _logHost.Visible = t == DrawerTab.Log;
        if (t == DrawerTab.Log) { ScrollLogToEnd(); _unread = 0; }
        _panelDirty = true; _panelTimer = 0;
        RefreshTabTitles();
        if (opening) RefreshTop();
    }

    void RefreshTabTitles()
    {
        // the count is what wants a decision (red and yellow); chances to do better never make the tab shout
        int n = _attention.Count(a => a.Level != Level.Info);
        var b = _tabButtons[DrawerTab.Needs];
        b.Text = n == 0 ? "Needs you" : $"Needs you {n}";
        bool urgent = _attention.Any(a => a.Level == Level.Danger);
        // the window's tab greys when nothing needs you; red or amber words when something does
        b.AddThemeColorOverride("font_color", n == 0 ? Ui.WinText : urgent ? Ui.Danger : Ui.Warn);
        b.AddThemeColorOverride("font_hover_color", n == 0 ? Ui.WinActive : urgent ? new Color("ff9a9a") : new Color("ffe08a"));
        b.AddThemeColorOverride("font_pressed_color", n == 0 ? Ui.WinActive : urgent ? new Color("ff9a9a") : new Color("ffe08a"));
        b.AddThemeColorOverride("font_hover_pressed_color", n == 0 ? Ui.WinActive : urgent ? new Color("ff9a9a") : new Color("ffe08a"));
        int f = S.Game.State.Fleets.Values.Count(x => x.Side == Me);
        _tabButtons[DrawerTab.Fleets].Text = $"Fleets {f}";
        _tabButtons[DrawerTab.Log].Text = _unread == 0 ? "Log" : _unread > 99 ? "Log 99+" : $"Log {_unread}";
        // tabs 10 px apart as in Paul's "styles" frame, closer when the counts make the row long: the row never widens the panel
        // (review 2026-11-14: "Needs you 3 ... Fleets 6 ... Log 12" needed 421 px of the 408)
        var font = Ui.Grotesk ?? ThemeDB.FallbackFont;
        float words = _tabButtons.Values.Sum(t => font.GetStringSize(t.Text, HorizontalAlignment.Left, -1, 13).X + 1);
        int sep = Math.Clamp((int)((DrawerWidth - 2 * WinPad - WinButton - words) / _tabButtons.Count), 4, 10);
        _tabRow.AddThemeConstantOverride("separation", sep);
    }

    // ================================================================== the panel switch

    void RebuildPanel()
    {
        using var look = Ui.Window();   // the tabs are built in the windows' look
        if (_tab == DrawerTab.Log) { RefreshTabTitles(); RefreshSectorView(); RefreshMgmt(); return; } // the sector window shows orders given in it whatever tab is open
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
        RefreshSectorView(); RefreshMgmt(); // an order given in a window shows there too
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
        // the panel list already puts Block above and below this spacer, so Group - 2*Block makes the gap before a section exactly Group
        // in a window the first part of a page sits on the window's 24 px margin, with no gap of its own (Paul 2026-11-14)
        if (!Ui.InWindow || (_into ?? _pv).GetChildCount() > 0) Add(Ui.Space(Ui.Group - Ui.Block * 2));
        var l = Add(Ui.Section(text));
        if (tip != null) { l.TooltipText = Ui.Wrap(tip); l.MouseFilter = MouseFilterEnum.Stop; l.MouseDefaultCursorShape = CursorShape.Help; }
    }

    Button Act(string text, Action a, string tip = null, bool disabled = false, string variation = null) => Add(Ui.Action(text, a, tip, disabled, variation));

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

    /// <summary>The panel title: the name in the windows' pixel capitals (32 px, #BABABA), a line under it, and tags on the right.</summary>
    void Title(string name, string sub, params (string Text, Color Color)[] tags)
    {
        var h = Ui.HBox(8);
        string upper = name.ToUpperInvariant();
        var t = Ui.Label(name, null, Ui.WinText, Ui.NameSmall, wrap: false);
        t.Uppercase = true;   // drawn in capitals; the text keeps its case
        t.AddThemeFontOverride("font", Ui.NameFont(Ui.NameSmall, upper));
        t.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        t.ClipText = true;
        t.SizeFlagsHorizontal = SizeFlags.ExpandFill;
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

    static Color Dim => Ui.Text3;   // a property: inside a window it gives the window's grey
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
            ("S", "sector window"), ("V", "next map layer"), ("L", "routes on / off"), ("/", "search"), ("G", "goal"), ("Ctrl/Cmd + S", "quick save"),
            ("Wheel, + -", "zoom"), ("Drag, arrows", "pan"), ("Home", "whole map"), ("Esc", "cancel / deselect"),
        })
            Stat(k, what);
    }

    // ================================================================== the Fleets tab (C5)

    /// <summary>One short line for the fleet list: where it is, where it is going and when it gets there.</summary>
    string StatusOf(Fleet f)
    {
        if (f.Transit is { } t && f.At == null) return $"moving to {t.Path[^1]}, arrives day {GameSession.ArrivalDay(t)}";
        if (S.PlannedMoveOf(f.Id) is { } pm) return $"at {f.At}; leaving for {pm.To}, arrives day {pm.ArrivalDay}";
        if (S.PendingOf(f.Id).Any()) return $"at {f.At}; order given";
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
        // what you'd open the fleet to learn (backlog F3, F4, 2026-11-22): its damaged ships, its troops out of its room, and whether it
        // is Elusive; each part only when it says something
        var eco = S.Host.Economy;
        int damaged = f.Ships.Select(eco.Find).Count(o => o != null && Fm.ClassOf(o) is { } sc && o.Hull < sc.Hull);
        int regs = S.Host.Troops.RegimentsAboard(f).Count, room = Fm.FreeTroopSlots(f) + regs;
        bool elusive = IsElusive(f);
        string tip = $"{f.Name}: {StatusOf(f)}. Strength {Fm.StrengthOf(f):0}."
            + (damaged > 0 ? $" {damaged} of {Ui.Count(f.Ships.Count, "ship", "ships")} damaged." : "")
            + (room > 0 ? $" Troops {regs} of {room}." : "")
            + (elusive ? " Elusive: it pulls back when an enemy fleet arrives." : "") + " Click to select it.";
        var b = Ui.Button($"{f.Name}\n{StatusOf(f)}", () => SelectFleet(fl.Id, true), tip, "Row");
        b.Alignment = HorizontalAlignment.Left;
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        b.ClipText = true;
        // the right-hand words, one or two lines, centred in the row's height so they never spill out of it at a bigger text size
        var corner = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        corner.AddThemeConstantOverride("separation", 0);
        corner.AnchorLeft = corner.AnchorRight = 1; corner.AnchorTop = 0; corner.AnchorBottom = 1;
        corner.OffsetLeft = -220; corner.OffsetRight = -12; corner.OffsetTop = 4; corner.OffsetBottom = -4;
        b.AddChild(corner);
        HBoxContainer Corner()
        {
            var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
            h.AddThemeConstantOverride("separation", 0);
            corner.AddChild(h);
            return h;
        }
        void Part(HBoxContainer h, string words, Color ink) => h.AddChild(Ui.Label(words, null, ink, Ui.Small, wrap: false));
        // line 1: ships (the damaged ones in amber), then strength
        var top = Corner();
        Part(top, Ui.Count(f.Ships.Count, "ship", "ships"), Ui.Text3);
        if (damaged > 0) Part(top, $", {damaged} damaged", Ui.Warn);
        Part(top, $"   {Fm.StrengthOf(f):0}", Ui.Text3);
        // line 2: Elusive, then troops aboard out of room (when it has transports)
        if (elusive || room > 0)
        {
            var low = Corner();
            if (elusive) Part(low, "Elusive", Look.Muted);
            if (elusive && room > 0) Part(low, "   ", Ui.Text3);
            if (room > 0) Part(low, $"troops {regs}/{room}", Ui.Text3);
        }
        // the selected fleet: the window look's "chosen" line (#BABABA), kept under the mouse; no fill, the row's own padding
        // (ui-inspo review 2026-11-14: one ladder everywhere, rest line, #676767 under the mouse, #BABABA chosen)
        if (_view.SelectedFleet == f.Id)
        {
            var sel = Ui.Box(new Color(0, 0, 0, 0), 0, Ui.WinActive, 1, 8, 3);
            b.AddThemeStyleboxOverride("normal", sel); b.AddThemeStyleboxOverride("hover", sel);
        }
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
