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
        drawer.Theme = SibylTheme;
        // Paul 2026-11-24 ("update the style according to the recent tabs that we redesigned, especially the fleet and ground tabs"): the
        // Fleet window's look. The window on the lighter surface with its 1 px edge, the head on the darker one, the strip of tabs, the page.
        var frame = Look.Box(Look.Band, Ui.WinEdge, 1, 1, 1, 1);
        frame.ContentMarginLeft = frame.ContentMarginRight = frame.ContentMarginTop = frame.ContentMarginBottom = 1;
        drawer.AddThemeStyleboxOverride("panel", frame);
        drawer.Visible = false;
        drawer.SetAnchorsPreset(LayoutPreset.RightWide);
        drawer.GrowHorizontal = GrowDirection.Begin;
        drawer.OffsetRight = -Edge; drawer.OffsetLeft = -Edge - DrawerWidth;
        drawer.OffsetTop = TopClear; drawer.OffsetBottom = -Edge;
        var v = Ui.VBox(0);
        drawer.AddChild(v);
        v.AddChild(BuildSibylHead());
        v.AddChild(BuildSibylStrip());

        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        v.AddChild(_scroll);
        var margin = Pad(WinPad, WinPad, WinPad, WinPad);   // the page's 16 px all round, as the Fleet window's
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.AddChild(margin);
        _pv = Ui.VBox(Ui.Block);
        _pv.CustomMinimumSize = new Vector2(PageWidth, 0);
        margin.AddChild(_pv);

        _logHost = BuildLogTab();
        _logHost.Visible = false;
        v.AddChild(_logHost);
        return drawer;
    }

    /// <summary>The page's width: the window less its 1 px edges, the 16 px margins, and 4 px spare for the scroll grabber.</summary>
    const int PageWidth = DrawerWidth - 2 - 2 * WinPad - 4;

    // ================================================================== the head and the strip (the Fleet window's design)

    Label _sibylName;
    HBoxContainer _sibylStatusRow;
    readonly Dictionary<Praedra.Core.AI.SibylJob, CheckBox> _sibylSwitches = new();

    /// <summary>
    /// The head, on the darker surface with 24 px all round as the Fleet window's: the advisor's name in BitPotion 48 (Sibyl for the Hand;
    /// "Advisor" for the Collective, which has no name yet) with × on the right, then her two switches (Paul 2026-11-24: "we're missing the
    /// controls. Add them KISS"): the same tick boxes as in Menu > Settings, the same state.
    /// </summary>
    Control BuildSibylHead()
    {
        var head = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        var pad = Pad(FleetHeadPad, FleetHeadPad, FleetHeadPad, FleetHeadPad);
        head.AddChild(pad);
        var parts = Boxed(new VBoxContainer(), FleetHeadPad);
        pad.AddChild(parts);

        var row = Boxed(new HBoxContainer(), NameGap);
        string word = (AdvisorName ?? "Advisor").ToUpperInvariant();
        var name = Look.Words(word, Ui.NameBig, Look.Text);
        name.AddThemeFontOverride("font", Ui.NameFont(Ui.NameBig, word));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _sibylName = name;
        row.AddChild(name);
        var close = Ui.Button("×", CloseDrawer, "Close (Esc)", "Clock");   // the Fleet window's ×: the design's grey
        close.SetMeta(UiTest.FullName, "Close window");
        close.AddThemeFontOverride("font", Ui.Grotesk);
        foreach (var c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" }) close.AddThemeColorOverride(c, Look.Text);
        close.CustomMinimumSize = new Vector2(WinButton, WinButton);
        close.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(close);
        parts.AddChild(row);

        // her two switches, ticked here or in Menu > Settings: one state (rule: m6-advisor-sibyl §Switches)
        _sibylStatusRow = Boxed(new HBoxContainer(), FleetHeadPad);
        foreach (var (job, words) in SibylJobs)
        {
            var j = job;
            var cb = new CheckBox { Text = words, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = Ui.Wrap(SibylHint(job) + $" She only uses yards you left idle and always keeps {SibylRoom} upkeep free. Also in Menu > Settings.") };
            cb.AddThemeFontSizeOverride("font_size", 12);
            foreach (var c in new[] { "font_color", "font_pressed_color", "font_focus_color" }) cb.AddThemeColorOverride(c, Look.Text);
            foreach (var c in new[] { "font_hover_color", "font_hover_pressed_color" }) cb.AddThemeColorOverride(c, Colors.White);
            // no box of its own: the tick lines up with her name above it
            foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" }) cb.AddThemeStyleboxOverride(st, new StyleBoxEmpty { ContentMarginTop = 4, ContentMarginBottom = 4 });
            cb.SetMeta(UiTest.FullName, $"Sibyl {words}");
            cb.Toggled += on => S.SetSibyl(j, on);
            _sibylSwitches[job] = cb;
            _sibylStatusRow.AddChild(cb);
        }
        _sibylStatusRow.Visible = S.HasAdvisor;
        parts.AddChild(_sibylStatusRow);
        RefreshSibylStatus();
        return head;
    }

    /// <summary>Her two switches in the words of Menu > Settings.</summary>
    static readonly (Praedra.Core.AI.SibylJob Job, string Words)[] SibylJobs =
        { (Praedra.Core.AI.SibylJob.ManageGarrisons, "Manage garrisons"), (Praedra.Core.AI.SibylJob.ManageProduction, "Manage production") };

    /// <summary>What a switch makes her do, as the advisor plans it (rule: m6-advisor-sibyl §Manage Garrisons, §Manage Production; Ponytail on #372).</summary>
    static string SibylHint(Praedra.Core.AI.SibylJob job) => job == Praedra.Core.AI.SibylJob.ManageGarrisons
        ? "Trains regiments for every world short of its garrison, worlds in uprising first, and builds training facilities when none is free."
        : "Builds mines, refineries and more construction yards at your idle construction yards, founds one if you have none left, and never scraps.";

    /// <summary>Her switches as they will be tomorrow (a tick in Menu > Settings shows here at once).</summary>
    void RefreshSibylStatus()
    {
        if (!S.HasAdvisor) return;
        foreach (var (job, cb) in _sibylSwitches)
            if (IsInstanceValid(cb) && cb.ButtonPressed != S.SibylOn(job)) cb.SetPressedNoSignal(S.SibylOn(job));
    }

    /// <summary>The upkeep room Sibyl always leaves free, from the advisor's own parameters (rule: m6-advisor-sibyl §Manage Garrisons 6).</summary>
    static readonly int SibylRoom = new Praedra.Core.AI.SimpleAiParams().AdvisorUpkeepMargin;

    readonly Dictionary<DrawerTab, Label> _tabCounts = new();

    /// <summary>
    /// The tabs as the Fleet window's strip: cells 1 px apart on the line colour, the open one on the lighter surface so it joins the page
    /// under it; the name, and its count in the dim grey (Needs you's count red or amber when something needs a decision).
    /// </summary>
    Control BuildSibylStrip()
    {
        var strip = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var lines = Look.Box(Look.Line);
        lines.ContentMarginTop = 1;   // the line above the cells
        strip.AddThemeStyleboxOverride("panel", lines);
        var h = Boxed(new HBoxContainer(), 1);   // the line between cells
        _tabRow = h;
        strip.AddChild(h);
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
            var b = new Button { ToggleMode = true, ButtonGroup = group, ButtonPressed = tab == _tab, FocusMode = FocusModeEnum.None,
                MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = Ui.Wrap(tip) };
            b.AddThemeStyleboxOverride("normal", Look.Box(Look.Bg));
            b.AddThemeStyleboxOverride("hover", Look.Box(Look.CellHover));
            b.AddThemeStyleboxOverride("pressed", Look.Box(Look.Band));
            b.AddThemeStyleboxOverride("hover_pressed", Look.Box(Look.Band));
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            b.SetMeta(UiTest.FullName, name);
            b.SetMeta("words", name);
            var row = Boxed(new HBoxContainer(), TabCountGap);
            row.Alignment = BoxContainer.AlignmentMode.Center;
            var words = Look.Words(name, 12, Look.Text);
            row.AddChild(words);
            var count = Look.Words("", 12, Look.Dim);
            count.Visible = false;
            row.AddChild(count);
            _tabCounts[tab] = count;
            Fill(b, row, TabPadX, 8);
            _tabPaint[tab] = CellWords(b, words);   // light on the open tab, grey on the others
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.Pressed += () => ShowTab(t);
            _tabButtons[tab] = b;
            h.AddChild(b);
        }
        return strip;
    }

    readonly Dictionary<DrawerTab, Action> _tabPaint = new();
    const int TabPadX = 8, TabCountGap = 6;

    /// <summary>
    /// The window theme with the Fleet window's buttons (Paul's design, 2026-11-21): 12 px words, 8 px inside, a 1 px #36363C line, #676767
    /// under the mouse, #BABABA when chosen or pressed, a #1E1E21 line and #454545 words when it can't act. A warning or danger button keeps
    /// its colour. Words with no size of their own are 13 px, as the Ground window's.
    /// </summary>
    static Theme SibylTheme
    {
        get
        {
            if (_sibylTheme != null) return _sibylTheme;
            var t = (Theme)Ui.WindowTheme.Duplicate();
            t.DefaultFontSize = Ui.Small;
            var clear = new Color(0, 0, 0, 0);
            void Outlined(string type, Color rest, Color ink)
            {
                foreach (var (state, line) in new[] { ("normal", rest), ("hover", Look.Soft), ("pressed", Look.Text), ("hover_pressed", Look.Text), ("disabled", Look.Line) })
                    t.SetStylebox(state, type, Look.Box(clear, line, 1, 1, 1, 1, 8));
                t.SetStylebox("focus", type, new StyleBoxEmpty());
                t.SetFont("font", type, Ui.Grotesk);
                t.SetFontSize("font_size", type, 12);
                foreach (var c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" }) t.SetColor(c, type, ink);
                t.SetColor("font_disabled_color", type, Look.Dim);
            }
            foreach (var v in new[] { "Button", "Row", "Primary", "Chip" }) Outlined(v, Look.Outline, Look.Text);
            Outlined("WarnButton", new Color(Ui.Warn, 0.7f), Ui.Warn);
            Outlined("DangerButton", new Color(Ui.Danger, 0.8f), Ui.Danger);
            return _sibylTheme = t;
        }
    }
    static Theme _sibylTheme;

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
        foreach (var (k, b) in _tabButtons) { b.SetPressedNoSignal(k == t); if (_tabPaint.TryGetValue(k, out var paint)) paint(); }
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
        bool urgent = _attention.Any(a => a.Level == Level.Danger);
        SetTabCount(DrawerTab.Needs, n == 0 ? null : n.ToString(), urgent ? Ui.Danger : Ui.Warn);
        int f = S.Game.State.Fleets.Values.Count(x => x.Side == Me);
        SetTabCount(DrawerTab.Fleets, f.ToString());
        int m = S.MyMissions.Count + S.PendingMissions.Count();
        SetTabCount(DrawerTab.Missions, m == 0 ? null : m.ToString());
        SetTabCount(DrawerTab.Log, _unread == 0 ? null : _unread > 99 ? "99+" : _unread.ToString());
        RefreshSibylStatus();
    }

    /// <summary>A tab's count, in the dim grey unless it has a colour of its own; none hides it. The playtest reads the tab by name and count.</summary>
    void SetTabCount(DrawerTab tab, string count, Color? ink = null)
    {
        var l = _tabCounts[tab];
        l.Text = count ?? "";
        l.Visible = count != null;
        l.AddThemeColorOverride("font_color", ink ?? Look.Dim);
        var b = _tabButtons[tab];
        string words = b.GetMeta("words").AsString();
        b.SetMeta(UiTest.FullName, count == null ? words : $"{words} {count}");
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
    string PanelKey() => $"{_tab}|{_view.Selected}|{_view.SelectedFleet}|{_plan?.Home}";

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

    /// <summary>True while a tab of the window on the right is being built (the sector window builds with the same helpers).</summary>
    bool InDrawer => _drawer != null && (_into ?? _pv) is Node n && (n == _pv || _drawer.IsAncestorOf(n));

    /// <summary>A section head: a clear gap above it (a new group starts), then the name in small capitals. In the window on the right,
    /// the Fleet window's part title (Paul 2026-11-24): 16 px words, a count in the dim grey beside them ("At your systems 3"), 24 px above.</summary>
    /// <summary>"At your systems (3)": a section name and its count, split for the Fleet window's title.</summary>
    static readonly System.Text.RegularExpressions.Regex CountedHead = new(@"^(.*) \((\d+)\)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    void Heading(string text, string tip = null)
    {
        if (InDrawer)
        {
            if ((_into ?? _pv).GetChildCount() > 0) Add(Ui.Space(WinGap - Ui.Block * 2));
            var m = CountedHead.Match(text);
            var hd = Add(Heading2(m.Success ? m.Groups[1].Value : text, m.Success ? m.Groups[2].Value : null));
            if (tip != null) { hd.TooltipText = Ui.Wrap(tip); hd.MouseFilter = MouseFilterEnum.Stop; hd.MouseDefaultCursorShape = CursorShape.Help; }
            return;
        }
        // the panel list already puts Block above and below this spacer, so Group - 2*Block makes the gap before a section exactly Group
        // in a window the first part of a page sits on the window's 24 px margin, with no gap of its own (Paul 2026-11-14)
        if (!Ui.InWindow || (_into ?? _pv).GetChildCount() > 0) Add(Ui.Space(Ui.Group - Ui.Block * 2));
        var l = Add(Ui.Section(text));
        if (tip != null) { l.TooltipText = Ui.Wrap(tip); l.MouseFilter = MouseFilterEnum.Stop; l.MouseDefaultCursorShape = CursorShape.Help; }
    }

    Button Act(string text, Action a, string tip = null, bool disabled = false, string variation = null)
    {
        var b = Ui.Action(text, a, tip, disabled, variation);
        if (InDrawer) Hug(b);
        return Add(b);
    }

    /// <summary>The Fleet window's button in the window on the right: as wide as its words (12 px, 8 px inside, 32 px high); words too long
    /// for the page keep the full width and wrap.</summary>
    static void Hug(Button b)
    {
        b.CustomMinimumSize = new Vector2(0, 32);
        float words = Ui.Grotesk.GetStringSize(b.Text, HorizontalAlignment.Left, -1, 12).X + 2 * 8 + 2;
        if (words > PageWidth - 2 * Ui.Pad) return;
        b.AutowrapMode = TextServer.AutowrapMode.Off;
        b.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
    }

    /// <summary>What a tab of the window on the right is about, under the strip: one line in the status grey (the tab's name is the strip's).</summary>
    void Lead(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var l = Look.Words(text, 12, Look.Muted);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Add(l);
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

    /// <summary>Lines added inside <c>using (BesidePicture(pic))</c> go in a column to the right of the picture, which sits on the dark
    /// tile as every picture on the redesigned windows (Paul 2026-11-24, "I need the dark tile behind pictures everywhere").</summary>
    IDisposable BesidePicture(Control pic)
    {
        var h = Boxed(new HBoxContainer(), 12);
        var tile = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(RowTile, RowTile), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        tile.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        var c = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        c.AddChild(pic);
        tile.AddChild(c);
        h.AddChild(tile);
        var col = Ui.VBox(Ui.Gap);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(col);
        Add(h);
        var prev = _into;
        _into = col;
        return new Restore(() => _into = prev);
    }

    sealed class Restore : IDisposable
    {
        readonly Action _a;
        public Restore(Action a) => _a = a;
        public void Dispose() => _a();
    }

    /// <summary>
    /// A system's head on the Selected page, as the Ground window's head (Paul 2026-11-24): its sector in small capitals, its name in the
    /// pixel capitals (32 px) with its tags on the right, then one line of what matters most. The name keeps its case for the playtest.
    /// </summary>
    void SystemHead(string kicker, string name, string sub, params (string Text, Color Color)[] tags)
    {
        var v = Boxed(new VBoxContainer(), 4);
        var cap = Look.Words(kicker.ToUpperInvariant(), Ui.Tiny, Look.Soft);
        cap.AddThemeFontOverride("font", Ui.Caps);
        v.AddChild(cap);
        var h = Boxed(new HBoxContainer(), 12);
        var t = Look.Words(name, Ui.NameSmall, Look.Text);
        t.Uppercase = true;   // drawn in capitals; the text keeps its case
        t.AddThemeFontOverride("font", Ui.NameFont(Ui.NameSmall, name.ToUpperInvariant()));
        t.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        t.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        t.ClipText = true;
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        h.AddChild(t);
        foreach (var (text, color) in tags) h.AddChild(Ui.Tag(text, color));
        v.AddChild(h);
        if (sub != null)
        {
            var l = Look.Words(sub, 13, Look.Muted);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            v.AddChild(l);
        }
        Add(v);
    }

    /// <summary>
    /// A row in the fleet rows' look: a picture on the dark tile at the left, a name, under it a state with words on its right, and a 2 px bar
    /// while work is under way. A click does <paramref name="act"/>.
    /// </summary>
    Button PictureRow(Control pic, string name, string state, Color stateInk, string right, float share, string key, Action act, string tip)
    {
        var b = Ui.Button("", act, tip, "Row");
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        b.SetMeta(UiTest.FullName, key);
        var tile = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        tile.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        tile.AnchorBottom = 1;
        tile.OffsetLeft = 1; tile.OffsetTop = 1; tile.OffsetRight = 1 + RowTile; tile.OffsetBottom = -1;
        var c = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        c.AddChild(pic);
        tile.AddChild(c);
        b.AddChild(tile);
        var lines = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        lines.AddThemeConstantOverride("separation", 2);
        lines.AnchorRight = lines.AnchorBottom = 1;
        lines.OffsetLeft = 1 + RowTile + 12; lines.OffsetRight = -12; lines.OffsetTop = 8; lines.OffsetBottom = -8;
        b.AddChild(lines);
        lines.AddChild(Look.Words(name, 13, Look.Text, true));
        var h = Boxed(new HBoxContainer(), 8);
        var st = Look.Words(state, 12, stateInk);
        st.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        st.ClipText = true;
        st.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        h.AddChild(st);
        if (!string.IsNullOrEmpty(right)) h.AddChild(Look.Words(right, 12, Look.Muted));
        lines.AddChild(h);
        if (share > 0)
            lines.AddChild(new ThinBar { Share = share, Fill = Look.Accent, Track = new Color(Look.AccentDeep, 0.5f), CustomMinimumSize = new Vector2(0, 2), MouseFilter = MouseFilterEnum.Ignore });
        void Fit() => b.CustomMinimumSize = new Vector2(0, Math.Max(RowTile + 2, lines.GetCombinedMinimumSize().Y + 16));
        lines.MinimumSizeChanged += Fit;
        Fit();
        return b;
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
        // the windows' look (Paul 2026-11-24): a lead line, then parts with their titles; no framed card
        Lead("Nothing selected. Click a system or one of your fleets on the map.");
        Heading("How it works");
        Line("Click a system to see it at a glance; its Infrastructure and Ground windows build and move what is there.", Look.Text, 13);
        Line("Click one of your fleets (blue arrows), then right-click a system to send it there; double-click it (or F) for its window. Several fleets at a system share one arrow: click it for their list, or Tab steps through them.", Look.Text, 13);
        Line("Needs you (Q) lists what wants a decision, worst first. Press Play (Space) and the game stops by itself when it matters.", Look.Text, 13);
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
        Lead(mine.Count == 0 ? "You have no fleet." : $"{Ui.Count(mine.Count, "fleet", "fleets")}, {Ui.Count(mine.Sum(f => f.Ships.Count), "ship", "ships")}. Click one to select it.");
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
        var hull = HullState(f);
        int regs = S.Host.Troops.RegimentsAboard(f).Count, room = Fm.FreeTroopSlots(f) + regs;
        bool elusive = IsElusive(f);
        string tip = $"{S.NameOf(f)}: {StatusOf(f)}. Strength {Fm.StrengthOf(f):0}."
            + HullWords(hull)
            + (room > 0 ? $" Troops {regs} of {room}." : "")
            + (elusive ? " Elusive: it pulls back when an enemy fleet arrives." : "") + " Click to select it.";
        // two lines, each with its own right-hand words: the name, then ships, damage and strength; where it is or goes, then Elusive and
        // troops. The left words end in "..." before they reach the right ones (a long trip at +140% text ran under them, 2026-11-22).
        var b = Ui.Button("", () => SelectFleet(fl.Id, true), tip, "Row");
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        b.SetMeta(UiTest.FullName, $"{S.NameOf(f)}\n{RowStatus(f)}");   // its words for the playtest: the name, then the trip
        // its map mark on the dark tile at the left, as every picture on the redesigned windows (Paul 2026-11-24): the square for
        // Defensive, the chevron for Elusive, in your blue, pointing up-right as a parked fleet's does
        var tile = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        tile.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        tile.AnchorBottom = 1;
        tile.OffsetLeft = 1; tile.OffsetTop = 1; tile.OffsetRight = 1 + RowTile; tile.OffsetBottom = -1;
        var mark = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        mark.AddChild(IconPic.Of(FleetLayer.Parked(elusive ? "fleet-elusive" : "fleet-defensive"), 2, Ui.You));   // as parked on the map
        tile.AddChild(mark);
        b.AddChild(tile);
        var lines = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        lines.AddThemeConstantOverride("separation", 2);
        lines.AnchorRight = lines.AnchorBottom = 1;
        lines.OffsetLeft = 1 + RowTile + 12; lines.OffsetRight = -12; lines.OffsetTop = 8; lines.OffsetBottom = -8;
        b.AddChild(lines);
        void Fit() => b.CustomMinimumSize = new Vector2(0, Math.Max(RowTile + 2, lines.GetCombinedMinimumSize().Y + 16));
        lines.MinimumSizeChanged += Fit;
        HBoxContainer Line(string left, (string To, int Day)? trip = null, bool status = false)
        {
            var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            h.AddThemeConstantOverride("separation", 0);
            var right = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            right.AddThemeConstantOverride("separation", 0);
            // the name as a card's name; where it is or goes in the Fleet window's status grey
            var words = Ui.Label(left, null, status ? Look.Muted : Look.Text, status ? 12 : 13, wrap: false);
            if (!status) words.AddThemeFontOverride("font", Ui.GroteskMedium);
            words.ClipText = true;
            words.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            words.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            h.AddChild(words);
            h.AddChild(new Control { CustomMinimumSize = new Vector2(12, 0), MouseFilter = MouseFilterEnum.Ignore });
            if (trip is { } tr)
            {
                // a trip, "to Thousand Mouths, day 123": when it is too long only the system's name gives way, so the arrival day always
                // shows (UI review 3)
                void FitTrip()
                {
                    var font = words.GetThemeFont("font");
                    int size = words.GetThemeFontSize("font_size");
                    float room = words.Size.X;
                    string Say(string to) => $"to {to}, day {tr.Day}";
                    string text = Say(tr.To);
                    for (int n = tr.To.Length - 1; n > 0 && room > 0 && font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X > room; n--)
                        text = Say(tr.To[..n].TrimEnd() + "…");
                    if (words.Text != text) words.Text = text;
                }
                words.Resized += FitTrip;
            }
            h.AddChild(right);
            lines.AddChild(h);
            return right;
        }
        void Part(HBoxContainer h, string words, Color ink) => h.AddChild(Ui.Label(words, null, ink, 12, wrap: false));
        // line 1: ships (those under half their hull in amber, red when one is under a quarter: the Ships tab's marks), then strength
        var top = Line(S.NameOf(f));
        Part(top, Ui.Count(f.Ships.Count, "ship", "ships"), Look.Text);
        if (hull.Low > 0) Part(top, $", {hull.Low} damaged", hull.Critical > 0 ? Ui.Danger : Ui.Warn);
        // no bare strength number: it had no scale (Paul 2026-11-22, F7 answer B); it stays in the row's hover
        // line 2: where it is or goes, then Elusive and troops aboard out of room (when it has transports)
        var low = Line(RowStatus(f), RowTrip(f), status: true);
        if (elusive) Part(low, "Elusive", Look.Muted);
        if (elusive && room > 0) Part(low, " · ", Look.Muted);
        if (room > 0) Part(low, $"Troops {regs}/{room}", Look.Muted);
        // the selected fleet: the window look's "chosen" line (#BABABA), kept under the mouse; no fill, the row's own padding
        // (ui-inspo review 2026-11-14: one ladder everywhere, rest line, #676767 under the mouse, #BABABA chosen)
        if (_view.SelectedFleet == f.Id)
        {
            var sel = Ui.Box(new Color(0, 0, 0, 0), 0, Ui.WinActive, 1, 8, 3);
            b.AddThemeStyleboxOverride("normal", sel); b.AddThemeStyleboxOverride("hover", sel);
        }
        Add(b);
        Fit();
    }

    /// <summary>The dark tile at the left of a Fleets row: 48 px wide, the 32 px map mark centred on it.</summary>
    const int RowTile = 48;

    /// <summary>Where a Fleets list row's fleet goes and the day it arrives, if it is moving or about to leave.</summary>
    (string To, int Day)? RowTrip(Fleet f) =>
        f.Transit is { } t && f.At == null ? (t.Path[^1], GameSession.ArrivalDay(t))
        : S.PlannedMoveOf(f.Id) is { } pm ? (pm.To, pm.ArrivalDay) : null;

    /// <summary>A Fleets list row's second line: short, since the group above it already says Moving or where (the hover has it all).</summary>
    string RowStatus(Fleet f) =>
        RowTrip(f) is { } tr ? $"to {tr.To}, day {tr.Day}" : S.PendingOf(f.Id).Any() ? $"at {f.At}; order given" : $"at {f.At}";

    internal IEnumerable<string> FleetListTexts() =>
        S.Game.State.Fleets.Values.Where(f => f.Side == Me).Select(f => $"{S.NameOf(f)}\n{RowStatus(f)}");

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
        // like a click on the map (review 13, Paul 2026-11-23): it selects and never opens the window on the right; an open one follows
        _view.SelectedFleet = here[((i + dir) % here.Count + here.Count) % here.Count].Id;
        OnViewChanged();
        _panelDirty = true; _panelTimer = 0;
    }
}
