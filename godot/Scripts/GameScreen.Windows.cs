using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Missions;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using RejectReason = Praedra.Core.Clock.RejectReason;
using UnloadRegimentsOrder = Praedra.Core.Clock.UnloadRegimentsOrder;
using LoadRegimentsOrder = Praedra.Core.Clock.LoadRegimentsOrder;
using UnloadSquadronsOrder = Praedra.Core.Clock.UnloadSquadronsOrder;
using LoadSquadronsOrder = Praedra.Core.Clock.LoadSquadronsOrder;
using TransferShipsOrder = Praedra.Core.Clock.TransferShipsOrder;
using MergeFleetsOrder = Praedra.Core.Clock.MergeFleetsOrder;
using SplitFleetOrder = Praedra.Core.Clock.SplitFleetOrder;
using SendRegimentOrder = Praedra.Core.Clock.SendRegimentOrder;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The management windows (Paul, 2026-11-02: "dedicated infrastructure, military and fleet windows... the user would open the military
/// window and the fleet window, then drag and drop the character to assign them a new role"). Three kinds of small floating windows, any
/// number open at once and moved by their head: a system's Infrastructure (its buildings and what it builds), a system's Ground (the
/// General's post, the leaders, regiments, fighters and special forces standing there, the fleets in orbit) and one Fleet (its three
/// posts, passengers, ships, fighters and troops aboard). Leaders, regiments, fighter squadrons and ships are chips you drag from one
/// window to another; each drop sends the order or orders the rules already have (rule: m5-characters-and-talent §Orders, m3-fleets
/// §Transfer, m3-troops-and-special-forces §Loading), and the core judges them when the clock moves. Screens only: no rule lives here.
/// </summary>
public partial class GameScreen
{
    /// <summary>A system has a Ground window (tabs) and an Infrastructure window (Paul 2026-11-03: "Keep separate windows"); a fleet has its own.</summary>
    public enum MgKind { Infra, Ground, Fleet }

    sealed class MgWin
    {
        public MgKind Kind;     // Ground (a system's window) or Fleet
        public string System;   // a system's window
        public int Fleet;       // Fleet
        public string Tab;      // the tab shown
        public bool Min;        // folded to its head
        public PanelContainer Panel;
        public VBoxContainer Head, Body;
        public ScrollContainer Scroll;
        public readonly Dictionary<string, (Control Page, Button Tab)> Pages = new(StringComparer.Ordinal);
        public string Key => Kind == MgKind.Fleet ? $"Fleet {Fleet}" : $"{Kind} {System}";
    }

    /// <summary>What is being dragged: a leader (L), a regiment (R), a fighter squadron (Q), a ship (S) or a whole fleet (M), by id.</summary>
    readonly record struct Drag(char Kind, int Id)
    {
        public string Key => $"{Kind}:{Id}";
        public static Drag? Parse(Variant v)
        {
            if (v.VariantType != Variant.Type.String) return null;
            string s = v.AsString();
            return ParseAll(v).FirstOrDefault() is { Kind: not '\0' } d ? d : null;
        }

        /// <summary>A stack dragged with Shift travels as "R:12,13,14": every unit of it.</summary>
        public static List<Drag> ParseAll(Variant v)
        {
            var list = new List<Drag>();
            if (v.VariantType != Variant.Type.String) return list;
            string s = v.AsString();
            if (s.Length < 3 || s[1] != ':') return list;
            foreach (var part in s[2..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int id)) list.Add(new Drag(s[0], id));
            return list;
        }
    }

    /// <summary>A place a chip can be dropped. Refuse says why not (null: it can; "": quietly not, e.g. where it already is).</summary>
    sealed record Zone(string Key, PanelContainer Box, HFlowContainer Flow, Func<Drag, string> Refuse, Action<Drag> Drop, string Verb);

    Control _mgLayer;
    readonly List<MgWin> _mgWins = new();        // bottom to top
    readonly List<Zone> _zones = new();
    readonly Dictionary<string, (string Words, string Zone)> _moved = new(StringComparer.Ordinal); // a chip's key -> where its drop sends it (words, zone), until the clock moves
    readonly Dictionary<int, int> _pendingIn = new();   // fleet -> regiments or squadrons dropped onto it today (room already promised)
    int _movedDay = -1;
    bool _mgDirty, _wasDragging;
    double _mgTimer;
    PanelContainer _dragHint;
    Label _dragHintText;

    // Four tall columns side by side (Paul 2026-11-03: "I liked this grid. I think 4 windows side by side could fit"; 1440 wide screen)
    const int MgWidth = 344;
    const int SlotGap = 10;

    internal int MgmtCount => _mgWins.Count;
    internal IEnumerable<string> MgmtKeys => _mgWins.Select(w => w.Key);

    // ================================================================== the layer

    Control BuildMgmtLayer()
    {
        _mgLayer = new Control { MouseFilter = MouseFilterEnum.Ignore, Name = "Windows" };
        _mgLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _dragHint = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 10, Theme = Ui.WindowTheme };
        _dragHint.AddThemeStyleboxOverride("panel", Ui.WindowBox(10, 6));
        _dragHintText = Ui.Label("", null, Ui.Text, Ui.Small, wrap: false);
        _dragHint.AddChild(_dragHintText);
        _mgLayer.AddChild(_dragHint);
        return _mgLayer;
    }

    /// <summary>Open (or bring to the front) a system's Ground or Infrastructure window, or a fleet's window.</summary>
    internal void OpenMgmt(MgKind kind, string system = null, int fleet = 0, string tab = null)
    {
        if (kind == MgKind.Fleet ? !S.Game.State.Fleets.TryGetValue(fleet, out var f0) || f0.Side != Me : system == null) return;
        if (kind == MgKind.Infra && S.IsDestroyed(system)) kind = MgKind.Ground; // a harvested world has one plain card, not two windows
        var w = _mgWins.FirstOrDefault(x => x.Kind == kind && (kind == MgKind.Fleet ? x.Fleet == fleet : x.System == system));
        if (w == null)
        {
            w = new MgWin { Kind = kind, System = system, Fleet = fleet, Tab = tab };
            BuildMgWin(w);
            _mgWins.Add(w);
            PlaceNew(w);
        }
        else
        {
            Front(w);
            if (tab != null) w.Tab = tab;
            w.Min = false;
        }
        RebuildMg(w);
    }

    void CloseMgmt(MgWin w)
    {
        _mgWins.Remove(w);
        _zones.RemoveAll(z => w.Panel.IsAncestorOf(z.Box));
        w.Panel.QueueFree();
    }

    /// <summary>Esc: the window on top closes first.</summary>
    bool CloseTopMgmt()
    {
        if (_mgWins.Count == 0) return false;
        CloseMgmt(_mgWins[^1]);
        return true;
    }

    void Front(MgWin w)
    {
        _mgWins.Remove(w); _mgWins.Add(w);
        _mgLayer.MoveChild(w.Panel, _mgLayer.GetChildCount() - 1);
        _mgLayer.MoveChild(_dragHint, _mgLayer.GetChildCount() - 1);
    }

    // Paul's "styles" frame (2026-11-11, his answers 2026-11-14): 16 px inside the edge, 24 px between the head, the tabs and the content
    const int WinPad = 16;
    const int WinGap = 24;
    const int WinButton = 24;    // a window button is 24 x 24, 4 px apart
    const int NameGap = 26;      // at least this between the name and the buttons

    void BuildMgWin(MgWin w)
    {
        using var look = Ui.Window();
        w.Panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(MgWidth, 0), Theme = Ui.WindowTheme };
        w.Panel.SetMeta(UiTest.FullName, "Window " + w.Key);
        // square, a thin edge, no shadow (Paul 2026-11-11: "Remove the shadows under the windows entirely")
        w.Panel.AddThemeStyleboxOverride("panel", Ui.WindowBox());
        w.Panel.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true } && _mgWins.Count > 0 && _mgWins[^1] != w) Front(w); };
        var v = Ui.VBox(0);
        w.Panel.AddChild(v);
        var headPad = Pad(WinPad, WinPad, WinPad, WinGap);   // the 24 px under the head is outside the scroll, so a scrolled page never touches it
        w.Head = Ui.VBox(WinGap);
        headPad.AddChild(w.Head);
        v.AddChild(headPad);
        // the head moves the window; let go near one of the four places and it snaps there
        Vector2? grab = null;
        headPad.MouseFilter = MouseFilterEnum.Stop;
        headPad.MouseDefaultCursorShape = CursorShape.Move;
        headPad.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            {
                if (b.Pressed) { grab = w.Panel.GetGlobalMousePosition() - w.Panel.Position; Front(w); if (b.DoubleClick) ToggleMin(w); }
                else { if (grab != null) Snap(w); grab = null; }
            }
            else if (e is InputEventMouseMotion && grab is Vector2 g) { w.Panel.Position = ClampWin(w.Panel.GetGlobalMousePosition() - g, w.Panel.Size); }
        };
        // no line under the head: the tab's underline closes it (Paul's "styles" frame)
        w.Scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        v.AddChild(w.Scroll);
        var bodyPad = Pad(WinPad, WinPad, 0, WinPad);
        bodyPad.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        w.Scroll.AddChild(bodyPad);
        w.Body = Ui.VBox(Ui.Block);
        w.Body.CustomMinimumSize = new Vector2(MgWidth - 34 - 4, 0);   // 4 px spare for the scroll grabber, so a long page never widens the window
        bodyPad.AddChild(w.Body);
        _mgLayer.AddChild(w.Panel);
        _mgLayer.MoveChild(_dragHint, _mgLayer.GetChildCount() - 1);
    }

    static MarginContainer Pad(int l, int r, int t, int b)
    {
        var m = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        m.AddThemeConstantOverride("margin_left", l); m.AddThemeConstantOverride("margin_right", r);
        m.AddThemeConstantOverride("margin_top", t); m.AddThemeConstantOverride("margin_bottom", b);
        return m;
    }

    Vector2 ClampWin(Vector2 at, Vector2 size)
    {
        var vp = _mgLayer.Size;
        return new Vector2(Mathf.Clamp(at.X, 8, Math.Max(8, vp.X - size.X - 8)), Mathf.Clamp(at.Y, TopClear, Math.Max(TopClear, vp.Y - 60)));
    }

    /// <summary>A window is at most the full free height of its column.</summary>
    float SlotHeight => Math.Max(240, _mgLayer.Size.Y - TopClear - BottomClear);

    /// <summary>The places a window snaps to: tall columns side by side across the screen (four on a 1440 wide screen).</summary>
    List<Vector2> Slots(bool leftFirst = true)
    {
        var vp = _mgLayer.Size;
        var cols = new List<Vector2>();
        for (float x = 16; x + MgWidth <= vp.X - 8; x += MgWidth + SlotGap) cols.Add(new Vector2(x, TopClear));
        if (cols.Count == 0) cols.Add(new Vector2(16, TopClear));
        if (!leftFirst) cols.Reverse();
        return cols;
    }

    void Snap(MgWin w)
    {
        var best = Slots().OrderBy(p => p.DistanceTo(w.Panel.Position)).First();
        if (best.DistanceTo(w.Panel.Position) < 64) w.Panel.Position = best;
    }

    /// <summary>
    /// A new window takes the first free column (beside the sector window, on the map's side, first), else steps beside the
    /// window on top, shifted so its title stays visible (QA on #227: a second fleet window opened right over the first).
    /// </summary>
    void PlaceNew(MgWin w)
    {
        var others = _mgWins.Where(x => x != w).Select(x => new Rect2(x.Panel.Position, new Vector2(MgWidth, Math.Max(120, x.Panel.Size.Y)))).ToList();
        var sec = SectorViewOpen ? _secWin.GetGlobalRect() : (Rect2?)null;
        bool Free(Vector2 p)
        {
            var r = new Rect2(p + new Vector2(4, 4), new Vector2(MgWidth - 8, 112));
            return !others.Any(o => o.Intersects(r)) && !(sec is Rect2 s && s.Intersects(r));
        }
        foreach (var p in Slots(leftFirst: !(SectorViewOpen && _secLeft)))
            if (Free(p)) { w.Panel.Position = p; return; }
        var last = others.Count > 0 ? others[^1].Position : new Vector2(16, TopClear);
        w.Panel.Position = ClampWin(last + new Vector2(28, 36), new Vector2(MgWidth, 200));
    }

    void ToggleMin(MgWin w)
    {
        w.Min = !w.Min;
        RebuildMg(w);
    }

    /// <summary>From where it stands down to the foot of the screen, whatever the shown tab holds (Paul 2026-11-11: "Make sure the windows
    /// extend to the bottom of the screen"); the tab scrolls inside it. Just the head when folded.</summary>
    void Fit(MgWin w, float keepScroll = -1)
    {
        foreach (var (name, (page, tab)) in w.Pages) { page.Visible = name == w.Tab; tab.SetPressedNoSignal(name == w.Tab); }
        w.Scroll.Visible = !w.Min;
        void Size()
        {
            if (!IsInstanceValid(w.Panel)) return;
            float head = w.Head.GetParent<Control>().GetCombinedMinimumSize().Y + 2; // the edge, top and bottom
            float h = w.Min ? head - WinGap + WinPad : Math.Min(SlotHeight, _mgLayer.Size.Y - w.Panel.Position.Y - BottomClear);
            w.Panel.Size = new Vector2(MgWidth, Math.Max(w.Min ? 0 : 160, h));
            if (keepScroll >= 0) w.Scroll.ScrollVertical = (int)keepScroll;
        }
        // twice: flowing rows of tiles only know their height once the column width has been laid out
        Callable.From(() => { Size(); Callable.From(Size).CallDeferred(); }).CallDeferred();
        if (IsInsideTree()) GetTree().CreateTimer(0.05).Timeout += Size;
    }

    /// <summary>The drops of yesterday have been judged: forget them.</summary>
    void NewDay()
    {
        if (_movedDay == S.Game.Day) return;
        _moved.Clear(); _pendingIn.Clear(); _splits.Clear(); _leaving.Clear(); _scrapped.Clear(); _dropUndo.Clear(); _movedDay = S.Game.Day;
        if (_mgWins.Count > 0) _mgDirty = true;
    }

    void RefreshMgmt()
    {
        NewDay();
        if (_mgWins.Count == 0) return;
        _mgDirty = true;
        foreach (var w in _mgWins.Where(w => w.Kind == MgKind.Fleet && (!S.Game.State.Fleets.TryGetValue(w.Fleet, out var f) || f.Side != Me)).ToList()) CloseMgmt(w);
    }

    void TickMgmt(double delta)
    {
        NewDay();
        HideCoveredNotes();
        bool dragging = GetViewport().GuiIsDragging() || _shotDrag != null;
        if (_shotDrag is { } sd) _dragging = sd;
        if (dragging != _wasDragging) { _wasDragging = dragging; LightZones(dragging ? _dragging : null); }
        if (dragging) DragHint(); else _dragHint.Visible = false;
        _mgTimer -= delta;
        if (_mgDirty && _mgTimer <= 0 && !dragging && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _mgDirty = false; _mgTimer = 0.25;
            foreach (var w in _mgWins.ToList()) RebuildMg(w);
        }
    }

    // ================================================================== content

    void RebuildMg(MgWin w)
    {
        using var look = Ui.Window();   // everything built here has the window look
        NewDay();
        if (w.Kind == MgKind.Fleet && (!S.Game.State.Fleets.TryGetValue(w.Fleet, out var gone) || gone.Side != Me)) { CloseMgmt(w); return; } // merged away or lost
        float keep = w.Scroll.ScrollVertical;
        _zones.RemoveAll(z => w.Panel.IsAncestorOf(z.Box));
        Ui.Clear(w.Head);
        Ui.Clear(w.Body);
        w.Pages.Clear();
        w.Panel.SetMeta(UiTest.FullName, "Window " + w.Key);
        var prev = _into;
        _into = w.Body;
        try
        {
            if (w.Kind == MgKind.Fleet) FleetWin(w, S.Game.State.Fleets[w.Fleet]);
            else if (w.Kind == MgKind.Infra) InfraWin(w);
            else SystemWin(w);
        }
        finally { _into = prev; }
        // a drop looks done at once (Paul 2026-11-02: "instant"): the unit stands where it was dropped. The core applies the order first thing
        // next step (rule: m1-clock-and-orders §Order of work, step 1), before anything else moves, so nothing can happen in between.
        foreach (var z in _zones.Where(z => w.Panel.IsAncestorOf(z.Box)))
        {
            var arriving = _moved.Where(kv => kv.Value.Zone == z.Key).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (D: Drag.Parse(kv.Key), kv.Value.Words)).Where(x => x.D != null).ToList();
            if (arriving.Count == 0) continue;
            foreach (var empty in z.Flow.GetChildren().OfType<Label>().ToList()) empty.QueueFree(); // "Empty" gives way
            foreach (var g in arriving.GroupBy(x => (ThingName(x.D.Value), x.D.Value.Kind, Travels(x.Words) ? x.Words : null)))
            {
                var gd = g.First().D.Value;
                string words = g.First().Words;
                var ghost = DragTile(gd, Travels(words) ? words : gd.Kind == 'L' ? LeaderPosts(Cm.Get(gd.Id)) : "moved", g.Count(), ghost: true);
                ghost.TooltipText = Ui.Wrap($"{(g.Count() > 1 ? $"{g.Count()} × " : "")}{ThingName(gd)}: {words}. It can move again tomorrow.");
                ghost.SetMeta(UiTest.FullName, "Arriving " + gd.Key);
                z.Flow.AddChild(ghost);
            }
        }
        // a zone whose only unit was just dragged away shows "Empty" in its dashed frame, so it keeps its size and can take the unit back
        foreach (var z in _zones.Where(z => w.Panel.IsAncestorOf(z.Box)))
        {
            if (z.Flow.GetChildren().OfType<Control>().Any(c => c.Visible && !c.IsQueuedForDeletion())) continue;
            bool post = z.Key.EndsWith(" General", StringComparison.Ordinal) || z.Key.EndsWith(" Admiral", StringComparison.Ordinal)
                || z.Key.EndsWith(" Commander", StringComparison.Ordinal);
            z.Flow.AddChild(EmptySlot("Empty", slot: post));
        }
        Fit(w, keep);
    }

    /// <summary>
    /// The notes under the search (the pill, the orders card, the toasts) sit under the window columns. A note a window covers even in part
    /// is hidden whole, so no half note or doubled edge shows between the columns; it comes back when the windows move or close. Alpha and
    /// no mouse for the note and everything in it, not Visible: the game switches these on and off itself. Runs each frame and right after
    /// the notes are laid out (so a note the game shows again is never drawn for a frame before it is checked).
    /// </summary>
    void HideCoveredNotes()
    {
        if (_topCentre == null) return;
        var wins = new List<Rect2>();
        foreach (var w in _mgWins) if (IsInstanceValid(w.Panel) && w.Panel.IsVisibleInTree()) wins.Add(w.Panel.GetGlobalRect());
        if (_secWin != null && IsInstanceValid(_secWin) && _secWin.IsVisibleInTree()) wins.Add(_secWin.GetGlobalRect());
        foreach (var c in _topCentre.GetChildren().OfType<Control>())
        {
            if (c == _searchResults || c.GetIndex() == 0 || !c.Visible) continue;   // the search bar sits above the windows; its results stay
            bool covered = wins.Any(r => r.Intersects(c.GetGlobalRect()));
            var want = covered ? new Color(1, 1, 1, 0) : Colors.White;
            if (c.Modulate == want) continue;
            c.Modulate = want;
            c.MouseBehaviorRecursive = covered ? MouseBehaviorRecursiveEnum.Disabled : MouseBehaviorRecursiveEnum.Inherited;
        }
    }

    static bool Travels(string words) => words.StartsWith("on the way", StringComparison.Ordinal) || words.StartsWith("flies", StringComparison.Ordinal);

    /// <summary>
    /// The head of a window (Paul's "styles" frame): on the left the line saying where it is, the name in pixel capitals and a line of key
    /// numbers under it (Paul kept both lines, 2026-11-14); on the right, pinned to the edge, the window buttons ⋮⋮ – ×. The name is #7D8699
    /// whoever's it is, red when harvested, darkest grey when unexplored (the caller says which). <paramref name="grip"/> is the fleet's own
    /// ⋮⋮ (drag it to merge); any other window gets a ⋮⋮ that moves the window (Paul 2026-11-14), as its whole head does.
    /// </summary>
    void MgHead(MgWin w, string caption, string title, Color titleColor, string sub = null, Control badge = null, Control grip = null)
    {
        // the caption on its own line across the window, then the name with the buttons beside it, centred on it as in Paul's guide,
        // then the stats line and the badge, which wrap rather than lose words
        var block = Ui.VBox(4);
        block.MouseFilter = MouseFilterEnum.Ignore;
        var cap = Ui.Label(caption.ToUpperInvariant(), null, Ui.Text3, Ui.Tiny, wrap: false);
        cap.AddThemeFontOverride("font", Ui.Caps);
        cap.MouseFilter = MouseFilterEnum.Ignore;
        cap.ClipText = true;   // a long caption must not widen the column
        cap.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;   // QA-90: cut with "…", not mid-word
        block.AddChild(cap);

        var row = Ui.HBox(NameGap);
        row.MouseFilter = MouseFilterEnum.Ignore;
        var buttons = Ui.HBox(4);
        buttons.MouseFilter = MouseFilterEnum.Ignore;
        buttons.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        grip ??= MoveGrip();
        buttons.AddChild(grip);
        var fold = Ui.Button(w.Min ? "+" : "–", () => ToggleMin(w), w.Min ? "Unfold (or double-click the title)" : "Fold to the title (or double-click it)", "Clock");
        fold.SetMeta(UiTest.FullName, "Fold " + w.Key);
        fold.CustomMinimumSize = new Vector2(WinButton, WinButton);
        buttons.AddChild(fold);
        var close = Ui.Button("×", () => CloseMgmt(w), "Close (Esc)", "Clock");
        close.SetMeta(UiTest.FullName, "Close " + w.Key);
        close.CustomMinimumSize = new Vector2(WinButton, WinButton);
        buttons.AddChild(close);

        // the name: BitPotion 48 px, or 32 px when it does not fit beside the buttons (Paul 2026-11-14)
        string upper = title.ToUpperInvariant();
        float room = MgWidth - 2 - 2 * WinPad - NameGap - 3 * WinButton - 2 * 4;
        int size = Ui.NameSize(upper, room);
        var t = Ui.Label(upper, null, titleColor, size, wrap: false);
        t.AddThemeFontOverride("font", Ui.NameFont(size, upper));
        t.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        t.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        t.ClipText = true;
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        t.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(t);
        row.AddChild(buttons);
        block.AddChild(row);

        if (sub != null) { var sl = Ui.Label(sub, null, Ui.Text2, Ui.Small); sl.MouseFilter = MouseFilterEnum.Ignore; block.AddChild(sl); }
        if (badge != null) { badge.SizeFlagsHorizontal = SizeFlags.ShrinkBegin; block.AddChild(badge); }
        w.Head.AddChild(block);
    }

    /// <summary>⋮⋮ on a planet's window: the handle that moves it to another column (Paul 2026-11-14). The events pass on to the head, which
    /// does the moving and the snapping.</summary>
    static Label MoveGrip()
    {
        var g = WindowGlyph("⋮⋮");
        g.MouseFilter = MouseFilterEnum.Pass;
        g.MouseDefaultCursorShape = CursorShape.Move;
        g.TooltipText = "Drag to move the window to another column";
        return g;
    }

    /// <summary>A window button's glyph as a plain label (the grips): 24 x 24, JetBrains Mono 24 px, #909EBD.</summary>
    static Label WindowGlyph(string glyph)
    {
        var g = Ui.Label(glyph, null, Ui.WinActive, 24, wrap: false);
        g.AddThemeFontOverride("font", Ui.GripFont);
        g.CustomMinimumSize = new Vector2(WinButton, WinButton);
        g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center;
        return g;
    }

    /// <summary>Links at the foot of a window that open its neighbours: underlined words (Paul 2026-11-14), not buttons.</summary>
    void MgLinks(params (string Text, Action Do, string Tip)[] links)
    {
        var h = new HFlowContainer();
        h.AddThemeConstantOverride("h_separation", 16); h.AddThemeConstantOverride("v_separation", 6);
        foreach (var (text, act, tip) in links) h.AddChild(Ui.Link(text, act, tip));
        Add(Ui.Space(4));
        Add(h);
    }

    void Section(string word, string right = null, string tip = null)
    {
        var h = Ui.HBox(6);
        var l = Ui.Label(word.ToUpperInvariant(), null, Ui.Text3, Ui.Tiny, wrap: false);
        l.AddThemeFontOverride("font", Ui.Caps);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(l);
        if (right != null) { var r = Ui.Label(right, null, Ui.Text3, Ui.Tiny, wrap: false); r.AddThemeFontOverride("font", Ui.Num); h.AddChild(r); }
        if (tip != null) { h.TooltipText = Ui.Wrap(tip); h.MouseFilter = MouseFilterEnum.Pass; }
        if ((_into ?? _pv).GetChildCount() > 0) Add(Ui.Space(6));   // the first part of a page sits on the window's 24 px margin
        Add(h);
    }
}
