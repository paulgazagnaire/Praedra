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
/// §Transfer, m3-troops-and-special-forces §Loading), and the core judges them when the next day starts. Screens only: no rule lives here.
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
    readonly Dictionary<string, (string Words, string Zone)> _moved = new(StringComparer.Ordinal); // a chip's key -> where its drop sends it (words, zone), until the next day
    readonly Dictionary<int, int> _pendingIn = new();   // fleet -> regiments or squadrons dropped onto it today (room already promised)
    int _movedDay = -1;
    bool _mgDirty, _wasDragging;
    double _mgTimer;
    PanelContainer _dragHint;
    Label _dragHintText;

    // Four tall columns side by side (Paul 2026-11-03: "I liked this grid. I think 4 windows side by side could fit"; 1440 wide screen)
    const int MgWidth = 344;
    const int SlotGap = 10;

    static readonly Color ZoneBg = new("0e0f12");
    static readonly Color ZoneLine = new(1, 1, 1, 0.07f);

    internal int MgmtCount => _mgWins.Count;
    internal IEnumerable<string> MgmtKeys => _mgWins.Select(w => w.Key);

    // ================================================================== the layer

    Control BuildMgmtLayer()
    {
        _mgLayer = new Control { MouseFilter = MouseFilterEnum.Ignore, Name = "Windows" };
        _mgLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _dragHint = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 10 };
        _dragHint.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Chrome, 6, new Color(1, 1, 1, 0.14f), 1, 10, 6));
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

    void BuildMgWin(MgWin w)
    {
        w.Panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(MgWidth, 0) };
        w.Panel.SetMeta(UiTest.FullName, "Window " + w.Key);
        var sb = Ui.Box(Ui.Chrome, 12, new Color(1, 1, 1, 0.1f), 1, 0, 0);
        sb.ShadowColor = new Color(0, 0, 0, 0.55f); sb.ShadowSize = 22;
        w.Panel.AddThemeStyleboxOverride("panel", sb);
        w.Panel.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true } && _mgWins.Count > 0 && _mgWins[^1] != w) Front(w); };
        var v = Ui.VBox(0);
        w.Panel.AddChild(v);
        var headPad = Pad(16, 10, 10, 0);
        w.Head = Ui.VBox(Ui.Gap);
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
        var line = new ColorRect { Color = new Color(1, 1, 1, 0.06f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore };
        v.AddChild(line);
        w.Scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        v.AddChild(w.Scroll);
        var bodyPad = Pad(16, 16, 12, 14);
        bodyPad.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        w.Scroll.AddChild(bodyPad);
        w.Body = Ui.VBox(Ui.Block);
        w.Body.CustomMinimumSize = new Vector2(MgWidth - 34, 0);
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
    float SlotHeight => Math.Max(240, _mgLayer.Size.Y - TopClear - 12);

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

    /// <summary>As tall as the shown tab needs, up to half the screen (or just the head when folded).</summary>
    void Fit(MgWin w, float keepScroll = -1)
    {
        foreach (var (name, (page, tab)) in w.Pages) { page.Visible = name == w.Tab; tab.SetPressedNoSignal(name == w.Tab); }
        w.Scroll.Visible = !w.Min;
        void Size()
        {
            if (!IsInstanceValid(w.Panel)) return;
            float head = w.Head.GetParent<Control>().GetCombinedMinimumSize().Y + 1;
            float want = w.Min ? head + 10 : head + w.Body.GetCombinedMinimumSize().Y + 30;
            float h = Math.Min(want, Math.Min(SlotHeight, _mgLayer.Size.Y - w.Panel.Position.Y - 12));
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
        _moved.Clear(); _pendingIn.Clear(); _splits.Clear(); _leaving.Clear(); _scrapped.Clear(); _movedDay = S.Game.Day;
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
        Fit(w, keep);
    }

    static bool Travels(string words) => words.StartsWith("on the way", StringComparison.Ordinal) || words.StartsWith("flies", StringComparison.Ordinal);

    /// <summary>The head of a window: a caption in small capitals, the title, fold and close buttons; then, optionally, a line under it.</summary>
    void MgHead(MgWin w, string caption, string title, Color titleColor, string sub = null, Control extra = null)
    {
        var row = Ui.HBox(Ui.Block);
        row.MouseFilter = MouseFilterEnum.Ignore;
        var names = Ui.VBox(2);
        names.MouseFilter = MouseFilterEnum.Ignore;
        names.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var cap = Ui.Label(caption.ToUpperInvariant(), null, Ui.Text3, Ui.Tiny, wrap: false);
        cap.AddThemeFontOverride("font", Ui.Caps);
        cap.MouseFilter = MouseFilterEnum.Ignore;
        cap.ClipText = true;   // a long caption must not widen the column
        cap.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;   // QA-90: cut with "…", not mid-word
        names.AddChild(cap);
        var t = Ui.Label(title, null, titleColor, 20, wrap: false);
        t.AddThemeFontOverride("font", Ui.SemiBold);
        t.ClipText = true;
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        t.MouseFilter = MouseFilterEnum.Ignore;
        names.AddChild(t);
        row.AddChild(names);
        if (extra != null) { extra.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(extra); }
        var fold = Ui.Button(w.Min ? "▢" : "–", () => ToggleMin(w), w.Min ? "Unfold (or double-click the title)" : "Fold to the title (or double-click it)", "Clock");
        fold.SetMeta(UiTest.FullName, "Fold " + w.Key);
        fold.CustomMinimumSize = new Vector2(28, 28);
        fold.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        row.AddChild(fold);
        var close = Ui.Button("✕", () => CloseMgmt(w), "Close (Esc)", "Clock");
        close.SetMeta(UiTest.FullName, "Close " + w.Key);
        close.CustomMinimumSize = new Vector2(28, 28);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        row.AddChild(close);
        w.Head.AddChild(row);
        if (sub != null) { var sl = Ui.Label(sub, null, Ui.Text2, Ui.Small, wrap: false); sl.ClipText = true; sl.MouseFilter = MouseFilterEnum.Ignore; w.Head.AddChild(sl); }
    }

    /// <summary>Small buttons at the foot of a window that open its neighbours.</summary>
    void MgLinks(params (string Text, Action Do, string Tip)[] links)
    {
        var h = new HFlowContainer();
        h.AddThemeConstantOverride("h_separation", 6); h.AddThemeConstantOverride("v_separation", 6);
        foreach (var (text, act, tip) in links) h.AddChild(Ui.Button(text, act, tip, "Chip"));
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
        Add(Ui.Space(6));
        Add(h);
    }

    // ---- a system's window: tabs for its leaders, troops, fighters, fleets in orbit and buildings

    void SystemWin(MgWin w)
    {
        string n = w.System;
        if (S.IsDestroyed(n)) { HarvestedWin(w, n); return; }
        bool mine = !S.IsDestroyed(n) && S.OwnerOf(n) == Me;
        string sub = null;
        PanelContainer badge = null;
        if (mine)
        {
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            sub = $"Loyalty {S.Host.Economy.SupportOf(n, Me)}% · {Ui.Count(have, "regiment", "regiments")}{(need == 0 ? "" : $" of {need} needed")}";
            if (have < need) badge = Ui.Tag("UNDER-GARRISONED", Ui.Warn);
            if (S.Host.Support.IsUprising(n)) badge = Ui.Tag("UPRISING", Ui.Danger, true);
        }
        MgHead(w, $"Ground · {RegionOf(n)}", n, mine ? Ui.Text : Ui.Text2, sub, badge);
        var own = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n).OrderBy(f => f.Id).ToList();
        if (!mine)
        {
            // what your side knows of a planet it doesn't hold
            Tabs(w, w.Body,
                ("Ground", null, () => PaneForces(n)),
                ("Fleets", own.Count, () => OrbitList(n, own)),
                ("Lore", null, () => LoreTab(n)));
            InfraLink(n);
            return;
        }
        var gen = Cm.SystemGeneralOf(n);
        if (gen != null && gen.Side != Me) gen = null;
        var here = S.MyCharacters.Where(c => c.Status != CharacterStatus.Dead && c.AttachedFleet == 0 && c.Transit == null && Cm.LocationOf(c) == n && c.Id != gen?.Id)
            .OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        var regs = S.Host.Troops.RegimentsOnSystem(n).Where(o => o.Side == Me).OrderBy(o => o.Id).ToList();
        var sf = MySpecialForcesAt(n);
        var based = FightersBasedAt(n);
        Tabs(w, w.Body,
            ("Leaders", here.Count + (gen != null ? 1 : 0), () => LeadersTab(n, gen, here)),
            ("Troops", regs.Count + sf.Count, () => TroopsTab(n, regs, sf)),
            ("Fighters", based.Count, () => FightersTab(n, based)),
            ("Fleets", own.Count, () => OrbitList(n, own)),
            ("Lore", null, () => LoreTab(n)));
        InfraLink(n);
    }

    // ---- a system's Lore tab (Paul 2026-11-04: "a lore tab for each system... we'll have the content later"): the Lore Master's text,
    // from data/design/system-lore.csv. Lore is common knowledge, so every side reads it, fog or not; a system without any says so.
    void LoreTab(string n, bool harvested = false)
    {
        var e = SystemLore.Of(n);
        if (e == null) { Note("The story of this system is still to be written."); return; }
        void Part(string head, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Section(head);
            Line(text, Ui.Text2, Ui.Small);
        }
        if (harvested)
        {
            // the record of what the harvest took: the world as it was and its story, no living people, moons or trade
            Part("Before the harvest", e.World);
            Part("Story", e.Story);
            Part("History", e.History);
            return;
        }
        Part("The world", e.World);
        Part("From space", e.FromSpace);
        Part("People", e.People);
        Part("Moons", e.Moons);
        Part("Resources", e.Resources);
        Part("Story", e.Story);
        Part("History", e.History);
    }

    // A harvested world (Paul 2026-11-04: "what happens to the system after being harvested?"): no tabs, no buildings, no Infrastructure link;
    // its name, the day, what it means, and your fleets standing in the debris. rule: m5-harvester §2 effect 5.
    void HarvestedWin(MgWin w, string n)
    {
        MgHead(w, $"Harvested · {RegionOf(n)}", n, Ui.Danger, S.DestroyedWords(n), Ui.Tag("HARVESTED", Ui.Danger, true));
        if (PlanetArt.HarvestedOf(n) is Texture2D rem)
            Add(new TextureRect { Texture = rem, TextureFilter = TextureFilterEnum.Nearest, CustomMinimumSize = new Vector2(PlanetArt.Dots, PlanetArt.Dots),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
        Line("The planet and everything on it are gone for good: no people, buildings or troops. Nobody holds it, and nobody can build, invade or send missions here.", Ui.Text2, Ui.Small);
        Line("It stays on the map as a field of debris. Fleets can still pass through it and stop here.", Ui.Text3, Ui.Small);
        var own = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n).OrderBy(f => f.Id).ToList();
        if (own.Count > 0) OrbitList(n, own);
        LoreTab(n, harvested: true);
    }

    void InfraLink(string n) => MgLinks(($"Infrastructure at {n}", () => OpenMgmt(MgKind.Infra, n), "Yards, what the planet builds, slots and buildings"));

    // ---- a system's Infrastructure window (Paul 2026-11-03 chose to keep it a window of its own): yards, build tiles, slots, buildings

    void InfraWin(MgWin w)
    {
        string n = w.System;
        if (S.IsDestroyed(n)) { HarvestedWin(w, n); return; } // already open when the harvester struck: no dead slots (Ponytail on #241)
        bool mine = S.OwnerOf(n) == Me;
        var eco = S.Host.Economy;
        MgHead(w, $"Infrastructure · {RegionOf(n)}", n, mine ? Ui.Text : Ui.Text2,
            mine ? $"{eco.EnergyUsed(n)} of {eco.SlotsOf(n).Energy} energy slots used · {Ui.Count(eco.OfKindAt(n, FacilityKind.Mine), "mine", "mines")} · {Ui.Count(eco.OfKindAt(n, FacilityKind.Refinery), "refinery", "refineries")}" : null);
        if (mine) BuildTab(n); else PaneBuildings(n);
        MgLinks(($"Ground at {n}", () => OpenMgmt(MgKind.Ground, n), "Leaders, troops and fighters on the planet, and the fleets in orbit"));
    }

    void LeadersTab(string n, CharacterRecord gen, List<CharacterRecord> here)
    {
        var row = Ui.HBox(Ui.Block);
        // the system's General: one post, held by a leader standing here
        var gcol = Ui.VBox(4);
        gcol.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        gcol.AddChild(Caption("General", null, "The system's General adds their leadership to the defenders when the planet is invaded."));
        var gz = DropZone($"Ground {n} General", "make General", d => RefuseSystemGeneral(d, n), d => DropSystemGeneral(d, n));
        gz.Box.CustomMinimumSize = new Vector2(TileW + 14, 0);
        if (gen != null) gz.Flow.AddChild(LeaderChip(gen, "General of " + n));
        else gz.Flow.AddChild(EmptySlot("Empty"));
        gcol.AddChild(gz.Box);
        row.AddChild(gcol);
        // leaders standing here (not on a fleet, not the General)
        var lcol = Ui.VBox(4);
        lcol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lcol.AddChild(Caption("Leaders on the planet", null));
        var lz = DropZone($"Ground {n} Leaders", "land", d => RefuseToGround(d, n, 'L'), d => DropToGround(d, n));
        foreach (var c in here) lz.Flow.AddChild(LeaderChip(c, null));
        if (here.Count == 0) lz.Flow.AddChild(EmptySlot("No leader of yours stands here"));
        lcol.AddChild(lz.Box);
        row.AddChild(lcol);
        Add(row);
        OnTheWay(S.MyCharacters.Where(c => c.Transit is { } t && t.Path.Count > 0 && t.Path[^1] == n && c.JoinFleet == 0), n);
        if (CanSendFrom(n)) MgLinks(("Send a mission...", () => { CloseSectorView(); OpenPlanner(n); }, "Pick a team standing here, the kind of mission and the target."));
    }

    void TroopsTab(string n, List<EconObject> regs, List<EconObject> sf)
    {
        int need = S.Host.Support.RequiredGarrison(n);
        Section("Regiments", need > 0 ? $"{regs.Count} / {need} needed" : regs.Count.ToString(), "Below 60% loyalty a planet needs regiments to stay calm: one for every 10 points under 60. Too few and it rises up.");
        var rz = DropZone($"Ground {n} Regiments", "land", d => RefuseToGround(d, n, 'R'), d => DropToGround(d, n));
        UnitTiles(rz.Flow, regs.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), n);
        if (regs.Count == 0) rz.Flow.AddChild(EmptySlot("No regiments"));
        Add(rz.Box);
        // special forces: they never ride fleets (m3-troops-and-special-forces); dragged to another system's window they go there (a Move mission)
        Section("Special forces", sf.Count == 0 ? null : sf.Count.ToString(), "Agents never ride fleets. Drag one onto another system's Troops tab to send it there.");
        var sz = DropZone($"Ground {n} SpecialForces", "send here", d => RefuseToGround(d, n, 'F'), d => DropToGround(d, n));
        foreach (var o in sf)
        {
            Drag? d = SfReady(o) ? new Drag('F', o.Id) : null;
            if (d is { } dd && _moved.ContainsKey(dd.Key)) continue;
            var t = Tile(UnitIcon.Kind.Agent, Me == Side.Hand, 1, S.SpecialForceName(o.Id), SpecialForceState(o), 1, d == null ? Ui.Text3 : Ui.You);
            t.TooltipText = Ui.Wrap(S.SpecialForceName(o.Id) + (d != null ? "\nDrag it onto another system to send it there." : ""));
            if (d is { } drag) { t.SetMeta(UiTest.FullName, drag.Key); MakeDraggable(t, new List<Drag> { drag }, S.SpecialForceName(o.Id)); }
            sz.Flow.AddChild(t);
        }
        if (sf.Count == 0) sz.Flow.AddChild(EmptySlot("No special forces"));
        Add(sz.Box);
    }

    void FightersTab(string n, List<EconObject> based)
    {
        Section("Fighters based here", based.Count == 0 ? null : based.Count.ToString(), "Squadrons based on the planet defend it. Drag them onto a fleet's Fighters tab to load them on a carrier.");
        var fz = DropZone($"Ground {n} Fighters", "base here", d => RefuseToGround(d, n, 'Q'), d => DropToGround(d, n));
        UnitTiles(fz.Flow, based.Select(o => ((Drag?)new Drag('Q', o.Id), o, HullWords(o))), n);
        if (based.Count == 0) fz.Flow.AddChild(EmptySlot("No squadrons"));
        Add(fz.Box);
    }

    /// <summary>Your fleets in orbit: a row each (drop ships, troops, fighters or leaders on it), with a button to open its window.</summary>
    void OrbitList(string n, List<Fleet> own)
    {
        if (own.Count == 0) { Add(EmptySlot("No fleets of yours here. Fleets you send here show up in this list.")); return; }
        Section("Your fleets in orbit", own.Count.ToString());
        foreach (var f in own) Add(FleetRow(f, false, null));
    }

    /// <summary>
    /// One fleet as a row: its mark, name, ships and strength. A drop zone: ships and whole fleets join it, troops and fighters board it,
    /// leaders ride it. In a fleet window it picks the fleet shown (<paramref name="pick"/>); elsewhere it has an Open button.
    /// </summary>
    Control FleetRow(Fleet f, bool selected, Action pick)
    {
        var z = DropZone($"Fleet {f.Id} Row", $"send to {f.Name}", d => d.Kind switch
        {
            'S' or 'M' => RefuseShipsInto(d, f),
            'R' or 'Q' => RefuseOnto(d, f, d.Kind),
            'L' => RefuseFleetPost(d, f, CharacterPost.None),
            _ => "",
        }, d =>
        {
            if (d.Kind is 'S' or 'M') DropShipsInto(d, f);
            else if (d.Kind is 'R' or 'Q') DropOnto(d, f);
            else if (d.Kind == 'L') DropFleetPost(d, f, CharacterPost.None);
        });
        if (selected) z.Box.AddThemeStyleboxOverride("panel", Ui.Box(new Color(Ui.You, 0.12f), 8, new Color(Ui.You, 0.6f), 1, 6, 6));
        z.Box.SetMeta("selected", selected);
        var h = Ui.HBox(8);
        h.MouseFilter = MouseFilterEnum.Ignore;
        h.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var size = f.Ships.Select(id => S.Host.Economy.Find(id)?.Class).Where(c => c != null).Select(SizeOfClass).DefaultIfEmpty(1).Max();
        var icon = UnitIcon.For(UnitIcon.Kind.Ship, Me == Side.Hand, Ui.You, size, new Vector2(46, 26));
        icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(icon);
        var v = Ui.VBox(0);
        v.MouseFilter = MouseFilterEnum.Ignore;
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var nm = Ui.Label(f.Name, null, Ui.Text, Ui.Small, wrap: false);
        nm.AddThemeFontOverride("font", Ui.SemiBold);
        nm.ClipText = true;
        v.AddChild(nm);
        string state = f.Transit is { } t ? $"to {t.Path[^1]}" : pick != null ? $"str {Fm.StrengthOf(f):0}" : $"strength {Fm.StrengthOf(f):0}";
        var sl = Ui.Label($"{Ui.Count(f.Ships.Count, "ship", "ships")} · {state}", null, Ui.Text3, Ui.Tiny, wrap: false);
        sl.ClipText = true;
        v.AddChild(sl);
        h.AddChild(v);
        int fid = f.Id;
        if (pick == null)
        {
            var open = Ui.Button("Open", () => OpenMgmt(MgKind.Fleet, fleet: fid), $"Open {f.Name}'s window", "Chip");
            open.SetMeta(UiTest.FullName, $"Open {f.Name}");
            open.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(open);
        }
        z.Flow.AddChild(h);
        z.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        z.Box.MouseDefaultCursorShape = CursorShape.PointingHand;
        z.Box.TooltipText = Ui.Wrap($"{f.Name}. Drop ships, troops, fighters or leaders here to send them to it." + (pick != null && !selected ? " Click to show it." : ""));
        z.Box.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) return;
            if (pick != null) pick(); else OpenMgmt(MgKind.Fleet, fleet: fid);
        };
        // the flow would stretch the row's content oddly: let the HBox fill it
        h.CustomMinimumSize = new Vector2(pick != null ? FleetListW - 16 : MgWidth - 60, 0);
        return z.Box;
    }

    const int FleetListW = 152;

    // ---- a fleet's window: your fleets at the same place on the left, the one shown on the right with its tabs (Classic's list and detail)

    void FleetWin(MgWin w, Fleet f)
    {
        bool still = f.At != null && f.Transit == null;
        string where = still ? $"Fleet · at {f.At}" : f.Transit is { } t ? $"Fleet · travelling to {t.Path[^1]}, arrives day {GameSession.ArrivalDay(t)}" : "Fleet";
        // the grip: drag the whole fleet onto another fleet to merge them
        var grip = Ui.Label("⠿", null, Ui.Text3, 18, wrap: false);
        grip.MouseFilter = MouseFilterEnum.Stop;
        grip.MouseDefaultCursorShape = CursorShape.Drag;
        grip.TooltipText = Ui.Wrap($"Drag {f.Name} onto another fleet to merge it into that fleet.");
        if (still && !_moved.ContainsKey(new Drag('M', f.Id).Key)) MakeDraggable(grip, new List<Drag> { new('M', f.Id) }, f.Name);
        MgHead(w, where, f.Name, Ui.You, $"{Ui.Count(f.Ships.Count, "ship", "ships")} · strength {Fm.StrengthOf(f):0} · speed {Fm.SpeedOf(f)}", grip);
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var mto)) Line($"This fleet {mto.Words}.", Pending, Ui.Small);

        var split = Ui.VBox(Ui.Block);
        split.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // the list on top: your fleets standing at the same system (a travelling fleet stands alone), two to a row
        var mates = still ? S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.At == f.At && x.Transit == null).OrderBy(x => x.Id).ToList() : new List<Fleet> { f };
        split.AddChild(Caption(still ? "Fleets here" : "Travelling", mates.Count.ToString()));
        var list = new HFlowContainer();
        list.AddThemeConstantOverride("h_separation", 6); list.AddThemeConstantOverride("v_separation", 6);
        foreach (var m in mates)
        {
            int mid = m.Id;
            var row = FleetRow(m, m.Id == f.Id, () => ShowFleetIn(w, mid));
            row.SizeFlagsHorizontal = SizeFlags.Fill;
            row.CustomMinimumSize = new Vector2(FleetListW, 0);
            list.AddChild(row);
        }
        split.AddChild(list);
        var detail = Ui.VBox(Ui.Block);
        detail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        split.AddChild(detail);
        Add(split);

        var eco = S.Host.Economy;
        var riders = Cm.AttachedTo(f.Id).Where(c => c.Post == CharacterPost.None).OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        var aboardF = FightersAboard(f);
        var aboardR = S.Host.Troops.RegimentsAboard(f);
        int posts = new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander }.Count(p => Cm.HolderOf(f.Id, p) != null);
        Tabs(w, detail,
            ("Command", posts + riders.Count, () => CommandTab(f, riders)),
            ("Ships", f.Ships.Count, () => ShipsTab(f, still)),
            ("Fighters", aboardF.Count + f.Squadrons.Count, () => FightersAboardTab(f, aboardF)),
            ("Troops", aboardR.Count, () => TroopsAboardTab(f, aboardR.Select(a => a.Regiment).ToList())));

        foreach (var pend in S.PendingOf(f.Id)) Line($"Waiting for the next day: {pend}", Pending, Ui.Small);
        int fid = f.Id;
        var links = new List<(string, Action, string)>();
        if (still) links.Add(($"{f.At}", () => OpenMgmt(MgKind.Ground, f.At), "The planet's window: drag its leaders, troops and fighters onto this fleet"));
        if (still) links.Add(("Move...", () => { var ff = S.Game.State.Fleets[fid]; CloseSectorView(); StartMove(ff); }, "Pick a destination on the map"));
        links.Add(("Orders and attack...", () => { CloseSectorView(); SelectFleet(fid, true); ShowTab(DrawerTab.Selected); }, "Bombard, invade, retreat, scrap: the fleet's full page"));
        MgLinks(links.ToArray());
    }

    /// <summary>Show another fleet in this window (a click on the list); if a window already shows it, that one comes forward.</summary>
    void ShowFleetIn(MgWin w, int fid)
    {
        if (w.Fleet == fid) return;
        if (_mgWins.FirstOrDefault(x => x.Kind == MgKind.Fleet && x.Fleet == fid) is { } other) { Front(other); return; }
        w.Fleet = fid;
        RebuildMg(w);
    }

    void CommandTab(Fleet f, List<CharacterRecord> riders)
    {
        // the three posts, side by side
        var row = Ui.HBox(8);
        foreach (var post in new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander })
        {
            var p = post;
            var col = Ui.VBox(4);
            col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            col.AddChild(Caption(GameSession.PostWords(post), null, post switch
            {
                CharacterPost.Admiral => "The Admiral leads the fleet in space battles and bombardments.",
                CharacterPost.General => "The General leads the troops when the fleet invades.",
                _ => "The Commander has no effect yet.",
            }));
            var z = DropZone($"Fleet {f.Id} {post}", $"make {GameSession.PostWords(post)}", d => RefuseFleetPost(d, f, p), d => DropFleetPost(d, f, p));
            z.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (Cm.HolderOf(f.Id, post) is { } holder) z.Flow.AddChild(LeaderChip(holder, null));
            else z.Flow.AddChild(EmptySlot("Empty"));
            col.AddChild(z.Box);
            row.AddChild(col);
        }
        Add(row);
        Section("Passengers", riders.Count == 0 ? null : riders.Count.ToString(), "Leaders who ride the fleet with no post: they travel with it.");
        var pz = DropZone($"Fleet {f.Id} Passengers", "take aboard", d => RefuseFleetPost(d, f, CharacterPost.None), d => DropFleetPost(d, f, CharacterPost.None));
        foreach (var c in riders) pz.Flow.AddChild(LeaderChip(c, null));
        if (riders.Count == 0) pz.Flow.AddChild(EmptySlot("None"));
        Add(pz.Box);
        // leaders and fleets sent to join it from other systems (m5 §Moving a character alone, m3 §Joining a fleet on arrival)
        OnTheWay(Cm.All.Where(c => c.Side == Me && c.JoinFleet == f.Id && c.JoinByOrder && c.Transit != null), f.At,
            S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.JoinFleet == f.Id && x.Transit != null).OrderBy(x => x.Id));
    }

    void ShipsTab(Fleet f, bool still)
    {
        var eco = S.Host.Economy;
        var sz = DropZone($"Fleet {f.Id} Ships", "join", d => RefuseShipsInto(d, f), d => DropShipsInto(d, f));
        UnitTiles(sz.Flow, f.Ships.Select(id => eco.Find(id)).Where(o => o != null).Select(o => ((Drag?)new Drag('S', o.Id), o, HullWords(o))), f.At);
        if (still && f.Ships.Count > 1)
        {
            var nz = DropZone($"Fleet {f.Id} NewFleet", "split off", d => RefuseSplit(d, f), d => DropSplit(d, f), dashed: true);
            nz.Box.CustomMinimumSize = new Vector2(TileW, TileH);
            var l = EmptySlot("+ New fleet");
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.CustomMinimumSize = new Vector2(TileW - 12, TileH - 12);
            nz.Flow.AddChild(l);
            nz.Box.TooltipText = Ui.Wrap("Drop ships here to split them off into a new fleet.");
            sz.Flow.AddChild(nz.Box);
        }
        Add(sz.Box);
    }

    void FightersAboardTab(Fleet f, List<EconObject> aboard)
    {
        var eco = S.Host.Economy;
        int berths = Fm.FreeBerths(f);
        Section("Fighters aboard", berths + aboard.Count == 0 ? "no hangar" : $"{aboard.Count} · room for {Math.Max(0, berths - Promised(f.Id))}");
        var qz = DropZone($"Fleet {f.Id} Fighters", "load", d => RefuseOnto(d, f, 'Q'), d => DropOnto(d, f));
        UnitTiles(qz.Flow, aboard.Select(o => ((Drag?)new Drag('Q', o.Id), o, HullWords(o)))
            .Concat(f.Squadrons.Select(id => eco.Find(id)).Where(o => o != null).Select(o => ((Drag?)null, o, "flies along"))), f.At);
        if (aboard.Count + f.Squadrons.Count == 0) qz.Flow.AddChild(EmptySlot(berths > 0 ? "Drop squadrons here to load them" : "No carrier in this fleet"));
        Add(qz.Box);
    }

    void TroopsAboardTab(Fleet f, List<EconObject> aboard)
    {
        int room = Fm.FreeTroopSlots(f);
        Section("Troops aboard", room + aboard.Count == 0 ? "no transport" : $"{aboard.Count} · room for {Math.Max(0, room - Promised(f.Id))}");
        var tz = DropZone($"Fleet {f.Id} Troops", "load", d => RefuseOnto(d, f, 'R'), d => DropOnto(d, f));
        UnitTiles(tz.Flow, aboard.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), f.At);
        if (aboard.Count == 0) tz.Flow.AddChild(EmptySlot(room > 0 ? "Drop regiments here to load them" : "No transport in this fleet"));
        Add(tz.Box);
    }

    /// <summary>Who is travelling here (leaders, and fleets sent to join): name and arrival day. Not draggable while they travel.</summary>
    void OnTheWay(IEnumerable<CharacterRecord> leaders, string to, IEnumerable<Fleet> fleets = null)
    {
        var ls = leaders.OrderBy(c => c.Id).ToList();
        var fs = (fleets ?? Enumerable.Empty<Fleet>()).ToList();
        if (ls.Count + fs.Count == 0) return;
        Section("On the way", (ls.Count + fs.Count).ToString(), $"Sent here from other systems. They take their place when they reach {to}.");
        var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var c in ls)
        {
            string name = S.CharName(c.Id);
            var t = Tile(null, false, 0, name, Arrives(c.Transit, c.JoinPost != CharacterPost.None ? $"to be {GameSession.PostWords(c.JoinPost)}" : null), 1, Ui.You, ghost: true, avatar: Initials(name));
            flow.AddChild(t);
        }
        foreach (var x in fs) flow.AddChild(Tile(UnitIcon.Kind.Ship, Me == Side.Hand, 2, x.Name, Arrives(x.Transit, $"{Ui.Count(x.Ships.Count, "ship", "ships")}, to merge"), 1, Ui.You, ghost: true));
        Add(flow);
    }

    string Arrives(FleetTransit t, string what)
    {
        int left = t == null ? 0 : Math.Max(0, t.DueDay - S.Game.Day) + t.HopTicks.Skip(t.HopIndex + 1).Sum();
        return (what != null ? what + ", " : "") + (left <= 1 ? "arrives tomorrow" : $"{left} days");
    }

    // ================================================================== chips and zones

    /// <summary>A leader as a tile: initials, name, the posts they can hold. Drags unless an order already moves them today, or they cannot act.</summary>
    Control LeaderChip(CharacterRecord c, string role)
    {
        string name = S.CharName(c.Id);
        var d = new Drag('L', c.Id);
        if (_moved.ContainsKey(d.Key)) return new Control { Visible = false };   // it already stands where it was dropped
        bool can = c.Status == CharacterStatus.Free && c.Transit == null && !Cm.IsOnMission(c.Id);
        string sub = c.Status != CharacterStatus.Free ? GameSession.StatusWords(c) : Cm.IsOnMission(c.Id) ? "on a mission" : LeaderPosts(c);
        var tile = Tile(null, false, 0, name, sub, 1, can ? Ui.You : Ui.Text3, avatar: Initials(name));
        tile.TooltipText = Ui.Wrap($"{name}. {S.SkillsOf(c)}\n{S.CapabilityOf(c)}{(role != null ? $"\n{role}." : "")}" + (can ? "\nDrag onto a post or a fleet." : ""));
        tile.SetMeta(UiTest.FullName, "Leader " + name);
        if (can) MakeDraggable(tile, new List<Drag> { d }, name);
        return tile;
    }

    /// <summary>The posts a leader can hold, short enough for a tile (their leadership is in the tooltip).</summary>
    string LeaderPosts(CharacterRecord c)
    {
        if (c == null) return null;
        var cls = Cm.ClassOf(c);
        var can3 = new[] { cls.CanAdmiral ? "Admiral" : null, cls.CanGeneral ? "General" : null, cls.CanCommander ? "Commander" : null }.Where(x => x != null).ToArray();
        return can3.Length == 3 ? "any post" : can3.Length == 0 ? "passenger" : string.Join(", ", can3);
    }

    static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "?" : parts.Length == 1 ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant() : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    string HullWords(EconObject o) => Fm.ClassOf(o) is { } sc && o.Hull < sc.Hull ? $"hull {o.Hull * 100 / Math.Max(1, sc.Hull)}%" : null;

    Label EmptySlot(string text)
    {
        var l = Ui.Label(text, null, Ui.Text3, Ui.Tiny, wrap: false);
        l.CustomMinimumSize = new Vector2(0, 34);
        l.VerticalAlignment = VerticalAlignment.Center;
        l.MouseFilter = MouseFilterEnum.Ignore;
        return l;
    }

    (PanelContainer Box, HFlowContainer Flow) DropZone(string key, string verb, Func<Drag, string> refuse, Action<Drag> drop, bool dashed = false)
    {
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        box.SetMeta(UiTest.FullName, "Zone " + key);
        box.AddThemeStyleboxOverride("panel", ZoneStyle(null, dashed));
        var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(flow);
        var zone = new Zone(key, box, flow, refuse, drop, verb);
        _zones.Add(zone);
        box.SetMeta("dashed", dashed);
        box.SetDragForwarding(new Callable(),
            Callable.From((Func<Vector2, Variant, bool>)((_, data) => Drag.Parse(data) is { } d && Judge(zone, d) == null)),
            Callable.From((Action<Vector2, Variant>)((_, data) =>
            {
                // a stack drops unit by unit, each judged after the ones before it (room fills up)
                bool any = false;
                foreach (var d in Drag.ParseAll(data)) if (Judge(zone, d) == null) { RunDrop(() => drop(d)); any = true; }
                if (any) { RefreshMgmt(); RefreshSectorView(); }
            })));
        return (box, flow);
    }

    /// <summary>Whether a drop may happen here (null), quietly not (""), or not, and why.</summary>
    string Judge(Zone z, Drag d) => d.Kind is not ('L' or 'M') && _scrapped.Contains(d.Id) ? $"{ThingName(d)} is scrapped next day." : _moved.TryGetValue(d.Key, out var m) ? (z.Refuse(d) == "" ? "" : $"{ThingName(d)} already moves today: {m.Words}.") : z.Refuse(d);

    /// <summary>A zone's look: quiet; lit blue where the chip being dragged can go; dim where it can't.</summary>
    static StyleBoxFlat ZoneStyle(bool? ok, bool dashed)
    {
        var sb = ok switch
        {
            true => Ui.Box(new Color(Ui.You, 0.10f), 8, new Color(Ui.You, 0.85f), 1, 6, 6),
            false => Ui.Box(new Color(ZoneBg, 0.6f), 8, new Color(1, 1, 1, 0.03f), 1, 6, 6),
            _ => Ui.Box(dashed ? new Color(0, 0, 0, 0) : ZoneBg, 8, dashed ? new Color(1, 1, 1, 0.14f) : ZoneLine, 1, 6, 6),
        };
        return sb;
    }

    void LightZones(Drag? d)
    {
        foreach (var z in _zones)
        {
            if (!IsInstanceValid(z.Box)) continue;
            bool dashed = z.Box.HasMeta("dashed") && z.Box.GetMeta("dashed").AsBool();
            string why = d is { } dd ? Judge(z, dd) : "";
            bool? ok = why == null ? true : why == "" ? null : false; // where it already is stays quiet
            z.Box.AddThemeStyleboxOverride("panel", ZoneStyle(ok, dashed));
            z.Box.Modulate = ok == false ? new Color(1, 1, 1, 0.55f) : Colors.White;
        }
    }

    Drag? _dragging;

    int _dragCount = 1;

    /// <summary>
    /// A tile drags one unit; with Shift held when the drag starts, the whole stack (every unit the tile counts). The preview is the tile's
    /// picture with the count.
    /// </summary>
    void MakeDraggable(Control c, IReadOnlyList<Drag> ds, string label)
    {
        if (ds.Count == 0) return;
        c.MouseDefaultCursorShape = CursorShape.Drag;
        c.SetDragForwarding(Callable.From((Func<Vector2, Variant>)(_ =>
        {
            var take = Input.IsKeyPressed(Key.Shift) ? ds : ds.Take(1).ToList();
            _dragging = take[0];
            _dragCount = take.Count;
            var prev = DragTile(take[0], take.Count > 1 ? "all of them" : null, take.Count, ghost: false);
            prev.Modulate = new Color(1, 1, 1, 0.9f);
            prev.Rotation = -0.03f;
            c.SetDragPreview(prev);
            return $"{take[0].Kind}:{string.Join(",", take.Select(x => x.Id))}";
        })), new Callable(), new Callable());
    }

    /// <summary>While dragging: a small label by the mouse says what dropping here would do, or why it can't.</summary>
    void DragHint()
    {
        var mouse = _mgLayer.GetGlobalMousePosition();
        var z = _zones.LastOrDefault(z => IsInstanceValid(z.Box) && z.Box.IsVisibleInTree() && z.Box.GetGlobalRect().HasPoint(mouse));
        if (z == null || _dragging is not { } d) { _dragHint.Visible = false; return; }
        string why = Judge(z, d);
        if (why == "") { _dragHint.Visible = false; return; }
        _dragHintText.Text = why ?? DropWords(d, z);
        _dragHintText.AddThemeColorOverride("font_color", why == null ? Ui.Text : Ui.Warn);
        _dragHint.Visible = true;
        _dragHint.ResetSize();
        _dragHint.Position = ClampWin(mouse + new Vector2(18, 22), _dragHint.Size) with { Y = Math.Min(mouse.Y + 22, _mgLayer.Size.Y - 40) };
    }

    string DropWords(Drag d, Zone z) => $"Drop to {z.Verb}: {(_dragCount > 1 ? $"{_dragCount} × " : "")}{ThingName(d)}";

    string ThingName(Drag d) => d.Kind switch
    {
        'L' => S.CharName(d.Id),
        'M' => S.Game.State.Fleets.TryGetValue(d.Id, out var f) ? f.Name : "fleet",
        'F' => S.SpecialForceName(d.Id),
        _ => S.Host.Economy.Find(d.Id)?.Class ?? "unit",
    };

    // ================================================================== what a drop may do (the core judges again next day)

    /// <summary>Where a dragged thing is now: its system, and its fleet (0 when on the ground).</summary>
    (string System, int Fleet) WhereNow(Drag d)
    {
        var eco = S.Host.Economy;
        switch (d.Kind)
        {
            case 'L':
                var c = Cm.Get(d.Id);
                return c == null ? (null, 0) : (Cm.LocationOf(c), c.AttachedFleet);
            case 'M':
                return S.Game.State.Fleets.TryGetValue(d.Id, out var mf) ? (mf.At, mf.Id) : (null, 0);
            default:
                var o = eco.Find(d.Id);
                if (o == null) return (null, 0);
                if (d.Kind == 'S') { var sf = S.Game.State.Fleets.Values.FirstOrDefault(f => f.Ships.Contains(o.Id)); return (sf?.At, sf?.Id ?? 0); }
                if (o.AboardShip == 0) return (o.System, 0);
                var hf = S.Game.State.Fleets.Values.FirstOrDefault(f => f.Ships.Contains(o.AboardShip));
                return (hf?.At, hf?.Id ?? 0);
        }
    }

    static bool Still(Fleet f) => f.At != null && f.Transit == null;

    readonly Dictionary<string, int> _leaving = new(StringComparer.Ordinal); // regiments boarding from each planet today (lookup only)

    int Promised(int fleet) => _pendingIn.TryGetValue(fleet, out int n) ? n : 0;

    // ------------------------------------------------------------------ the core's own order checks
    // A drop is refused for the same reasons the core would refuse its order at dawn: each Refuse* below asks the core check for that order
    // (FleetModule/TroopModule/CharacterModule Check*), then adds only what the core can't know: today's other drops and nicer words.

    /// <summary>The words for a core refusal (null stays null: the order would be accepted).</summary>
    static string Words(string reason, bool character = false) =>
        reason == null ? null : "Can't: " + (character ? GameSession.WhyCharacter(reason) : GameSession.Why(reason)) + ".";

    static List<int> One(int id) => new() { id };

    string CheckUnloadOne(char kind, int fleet, int id) => kind == 'R'
        ? S.Host.Troops.CheckUnloadRegiments(new UnloadRegimentsOrder(0, Me, fleet, One(id)))
        : Fm.CheckUnloadSquadrons(new UnloadSquadronsOrder(0, Me, fleet, One(id)));

    string CheckLoadOne(char kind, int fleet, int id) => kind == 'R'
        ? S.Host.Troops.CheckLoadRegiments(new LoadRegimentsOrder(0, Me, fleet, One(id)))
        : Fm.CheckLoadSquadrons(new LoadSquadronsOrder(0, Me, fleet, One(id)));

    string RefuseFleetPost(Drag d, Fleet f, CharacterPost post)
    {
        if (d.Kind != 'L') return "";
        var c = Cm.Get(d.Id);
        if (c == null) return "";
        if (c.AttachedFleet == f.Id && c.Post == post) return "";
        if (!Still(f)) return $"{f.Name} is travelling: send leaders when it stands still.";
        if (post != CharacterPost.None && !Cm.ClassOf(c).Can(post)) return $"{S.CharName(c.Id)} can't be {GameSession.PostWords(post)}.";
        var (at, _) = WhereNow(d);
        if (at != f.At) return CanTravel(c, f.At);   // another system: they set off and take the post on arrival
        // the drop first takes them off their post and lands the post's holder (LeaveCurrentPost, Detach), then attaches
        return Words(Cm.CheckAttach(new AttachCharacterOrder(0, Me, c.Id, f.Id, post), leavesPostsFirst: true), character: true);
    }

    /// <summary>Why a leader can't set off for <paramref name="to"/> now (null: they can). rule: m5-characters-and-talent §Moving a character alone.</summary>
    string CanTravel(CharacterRecord c, string to)
    {
        string name = S.CharName(c.Id);
        if (c.Status != CharacterStatus.Free || c.Transit != null || Cm.IsOnMission(c.Id)) return $"{name} is busy.";
        if (c.AttachedFleet != 0 && S.Game.State.Fleets.TryGetValue(c.AttachedFleet, out var of) && !Still(of)) return $"{name}'s fleet is travelling.";
        if (Cm.LocationOf(c) is not string from) return $"{name} is busy.";
        return Fm.FindPath(from, to, Cm.Params.CharacterSpeed).Ok ? null : $"No route from {from} to {to}.";
    }

    int TravelDays(CharacterRecord c, string to) => Cm.LocationOf(c) is string from && Fm.FindPath(from, to, Cm.Params.CharacterSpeed) is { Ok: true } p ? p.TotalTicks : 0;

    /// <summary>A leader leaves any post and sets off for <paramref name="to"/>, joining <paramref name="f"/> there in <paramref name="post"/> (no fleet: they land).</summary>
    void SendLeader(CharacterRecord c, string to, Fleet f, CharacterPost post, string zone)
    {
        LeaveCurrentPost(c);
        int cid = c.Id, fid = f?.Id ?? 0, days = TravelDays(c, to);
        string name = S.CharName(cid);
        string role = f == null ? $"to {to}" : post == CharacterPost.None ? $"to ride {f.Name}" : $"to be {GameSession.PostWords(post)} of {f.Name}";
        S.TrackCharacter(cid, -1, S.Game.Submit(seq => new MoveCharacterOrder(seq, Me, cid, to, fid, post)), $"Send {name} {role}");
        Sent($"send {name} {role} at {to}", to);
        _moved[new Drag('L', cid).Key] = ($"on the way {role}, {Ui.Count(days, "day", "days")}", zone);
    }

    void DropFleetPost(Drag d, Fleet f, CharacterPost post)
    {
        var c = Cm.Get(d.Id);
        string zone = post == CharacterPost.None ? $"Fleet {f.Id} Passengers" : $"Fleet {f.Id} {post}";
        if (WhereNow(d).System != f.At) { SendLeader(c, f.At, f, post, zone); return; }
        LeaveCurrentPost(c);
        if (post != CharacterPost.None && Cm.HolderOf(f.Id, post) is { } holder && holder.Id != c.Id)
        {
            Detach(holder, f); // the old holder stays at the system, on the ground
            _moved[new Drag('L', holder.Id).Key] = ($"lands at {f.At}", $"Ground {f.At} Leaders");
        }
        Attach(c, f, post);
        _moved[d.Key] = (post == CharacterPost.None ? $"{f.Name}, passenger" : $"{GameSession.PostWords(post)} of {f.Name}", zone);
    }

    /// <summary>A leader leaves the fleet post or the system General's post they hold, so a new post can be given in the same day.</summary>
    void LeaveCurrentPost(CharacterRecord c)
    {
        if (c.AttachedFleet != 0 && S.Game.State.Fleets.TryGetValue(c.AttachedFleet, out var of)) Detach(c, of);
        if (S.SystemPostOf(c) is string gs) ReleaseGeneral(c, gs);
    }

    string RefuseSystemGeneral(Drag d, string n)
    {
        if (d.Kind != 'L') return "";
        var c = Cm.Get(d.Id);
        if (c == null || S.SystemPostOf(c) == n) return "";
        var (at, _) = WhereNow(d);
        if (at != n) return $"{S.CharName(c.Id)} is not at {n}: drop them on its leaders to send them there first.";
        if (!Cm.ClassOf(c).CanGeneral) return $"{S.CharName(c.Id)} can't be a General.";
        // the drop first frees them and the post (LeaveCurrentPost, ReleaseGeneral), then assigns
        return Words(Cm.CheckAssign(new AssignSystemGeneralOrder(0, Me, c.Id, n), leavesPostsFirst: true), character: true);
    }

    void DropSystemGeneral(Drag d, string n)
    {
        var c = Cm.Get(d.Id);
        LeaveCurrentPost(c);
        if (Cm.SystemGeneralOf(n) is { } old && old.Id != c.Id && old.Side == Me)
        {
            ReleaseGeneral(old, n);
            _moved[new Drag('L', old.Id).Key] = ($"steps down at {n}", $"Ground {n} Leaders");
        }
        AssignGeneral(c, n);
        _moved[d.Key] = ($"General of {n}", $"Ground {n} General");
    }

    /// <summary>Onto the planet: a leader leaves their post, a regiment or squadron lands. Only on a planet you hold.</summary>
    string RefuseToGround(Drag d, string n, char kind)
    {
        if (d.Kind != kind) return "";
        var (at, fleet) = WhereNow(d);
        if (kind == 'L')
        {
            var c = Cm.Get(d.Id);
            if (c == null) return "";
            if (at != n) return CanTravel(c, n);   // another system: they set off
            if (fleet == 0 && S.SystemPostOf(c) == null) return "";
        }
        else if (kind == 'F') return at == n ? "" : SfCanTravel(d, n);
        else if (at != n)
        {
            if (kind == 'R') return RegimentCanGo(d, n);
            if (fleet != 0) return $"Land it at {at} first, or move its fleet.";
            return SquadronCanFly(d, n);
        }
        else if (fleet == 0) return "";
        if (fleet != 0 && S.Game.State.Fleets.TryGetValue(fleet, out var f) && !Still(f)) return "The fleet is travelling.";
        if (kind == 'L') return null;
        // rule: m3-troops-and-special-forces §Orders (UnloadRegiments, switch UnloadOnlyAtHeldSystem); m3-fleets §Orders (UnloadSquadrons)
        string why = CheckUnloadOne(kind, fleet, d.Id);
        if (why == RejectReason.NotYourSystem) return $"Land troops only on a planet you hold. To take {n}, invade from the fleet's page.";
        return Words(why);
    }

    /// <summary>A regiment goes alone to another system its side holds; anywhere else it needs a ship. rule: m3-troops-and-special-forces §Orders (SendRegiment).</summary>
    string RegimentCanGo(Drag d, string n)
    {
        string why = S.Host.Construction.CheckSendRegiment(new SendRegimentOrder(0, Me, d.Id, n));
        if (why == RejectReason.NotYourSystem) return $"{n} isn't yours: regiments need a ship to go there. Load them on a fleet.";
        return Words(why);
    }

    /// <summary>Squadrons based at one system fly alone to another (they base there). rule: m3-fleets §Unescorted squadrons (Immobile at speed 0).</summary>
    string SquadronCanFly(Drag d, string n)
    {
        if (S.Host.Economy.Find(d.Id) is not { } o || o.System is not string from) return "";
        int speed = Fm.SpeedOfShip(o);
        if (speed <= 0) return $"{o.Class} can't fly without a carrier: load it on a fleet.";
        return Fm.FindPath(from, n, speed).Ok ? null : $"No route from {from} to {n}.";
    }

    /// <summary>A special force moves to another system as a Move mission. rule: m5-missions §Orders (MoveGroup).</summary>
    string SfCanTravel(Drag d, string n)
    {
        if (S.Host.Economy.Find(d.Id) is not { } o) return "";
        return SfReady(o) ? null : $"{S.SpecialForceName(o.Id)} is busy.";
    }

    void DropToGround(Drag d, string n)
    {
        var (at, fleet) = WhereNow(d);
        if (d.Kind == 'L' && at != n) { SendLeader(Cm.Get(d.Id), n, null, CharacterPost.None, $"Ground {n} Leaders"); return; }
        if (d.Kind == 'L') { LeaveCurrentPost(Cm.Get(d.Id)); _moved[d.Key] = ($"lands at {n}", $"Ground {n} Leaders"); return; }
        if (d.Kind == 'F')
        {
            int id = d.Id;
            S.Track(S.Game.Submit(seq => new MoveGroupOrder(seq, Me, new List<int>(), new List<int> { id }, n)), $"Send {S.SpecialForceName(id)} to {n}");
            Sent($"send {S.SpecialForceName(id)} to {n}", n);
            _moved[d.Key] = ($"on the way to {n}", $"Ground {n} SpecialForces");
            return;
        }
        if (d.Kind == 'R' && at != n)   // a regiment goes alone to a system we hold. rule: m3-troops-and-special-forces §Orders (SendRegiment)
        {
            int id = d.Id;
            var order = new SendRegimentOrder(0, Me, id, n);
            int days = S.Host.Construction.SendRegimentDays(order);
            WarnTraps(S.Host.Construction.SendRegimentRoute(order), $"The {ThingName(d)}'s way to {n}");
            S.Track(S.Game.Submit(seq => order with { Sequence = seq }), $"Send the {ThingName(d)} to {n}");
            Sent($"send the {ThingName(d)} from {at} to {n}", n);
            _moved[d.Key] = ($"on the way to {n}, {Ui.Count(days, "day", "days")}", $"Ground {n} Regiments");
            if (fleet == 0) WarnGarrison(at);
            return;
        }
        if (fleet == 0)   // fighters based elsewhere fly over
        {
            var o = S.Host.Economy.Find(d.Id);
            int days = Fm.FindPath(at, n, Fm.SpeedOfShip(o)) is { Ok: true } p ? p.TotalTicks : 0;
            S.Track(Fm.SubmitMoveSquadrons(Me, new[] { d.Id }, n), $"Fly the {o.Class} to {n}");
            Sent($"fly the {o.Class} from {at} to {n}", n);
            _moved[d.Key] = ($"on the way to {n}, {Ui.Count(days, "day", "days")}", $"Ground {n} Fighters");
            return;
        }
        var f = S.Game.State.Fleets[fleet];
        if (d.Kind == 'R') UnloadTroops(f, new List<int> { d.Id });
        else UnloadFighters(f, new List<int> { d.Id });
        _moved[d.Key] = ($"lands at {n}", d.Kind == 'R' ? $"Ground {n} Regiments" : $"Ground {n} Fighters");
    }

    /// <summary>Onto a fleet: a regiment or squadron boards it (from the planet, or from another fleet here by landing first).</summary>
    string RefuseOnto(Drag d, Fleet f, char kind)
    {
        if (d.Kind != kind) return "";
        var (at, fleet) = WhereNow(d);
        if (fleet == f.Id) return "";
        if (!Still(f)) return $"{f.Name} is travelling.";
        if (at != f.At) return $"{ThingName(d)} is not at {f.At}.";
        // from another fleet the drop unloads first, then loads (rule: m3-fleets §Orders, m3-troops-and-special-forces §Orders)
        string why = fleet != 0 ? CheckUnloadOne(kind, fleet, d.Id) : CheckLoadOne(kind, f.Id, d.Id);
        if (why == RejectReason.NotYourSystem) return "Move it through the planet: only on a planet you hold.";
        if (why != null && why != RejectReason.NoCapacity) return Words(why);
        int room = (kind == 'R' ? Fm.FreeTroopSlots(f) : Fm.FreeBerths(f)) - Promised(f.Id);
        if (room <= 0) return kind == 'R' ? $"{f.Name} has no troop room left." : $"{f.Name} has no hangar room left.";
        return null;
    }

    void DropOnto(Drag d, Fleet f)
    {
        var (_, fleet) = WhereNow(d);
        if (fleet != 0 && S.Game.State.Fleets.TryGetValue(fleet, out var from))
        {
            if (d.Kind == 'R') UnloadTroops(from, new List<int> { d.Id }); else UnloadFighters(from, new List<int> { d.Id });
        }
        if (d.Kind == 'R') LoadTroops(f, new List<int> { d.Id }); else LoadFighters(f, new List<int> { d.Id });
        _pendingIn[f.Id] = Promised(f.Id) + 1;
        _moved[d.Key] = ($"boards {f.Name}", d.Kind == 'R' ? $"Fleet {f.Id} Troops" : $"Fleet {f.Id} Fighters");
        if (d.Kind == 'R' && fleet == 0) WarnGarrison(f.At);
    }

    /// <summary>A lone traveller's way crosses a known enemy interdictor: say so in red (as the fleet move preview does). rule: m1-travel §Path rule (5).</summary>
    void WarnTraps(IReadOnlyList<string> route, string what)
    {
        var traps = S.Host.KnownTraps(Me, route);
        if (traps.Count == 0) return;
        Toast($"{what} passes {string.Join(", ", traps.Select(t => t.System))}, where an enemy interdictor was seen: it may be caught there.", MsgKind.Warn);
    }

    /// <summary>One more regiment leaves the planet <paramref name="at"/> today: warn when its garrison drops too low.</summary>
    void WarnGarrison(string at)
    {
        _leaving[at] = (_leaving.TryGetValue(at, out int gone) ? gone : 0) + 1;
        if (S.Host.Support.RegimentCount(at) - _leaving[at] < S.Host.Support.RequiredGarrison(at))
            Toast($"{at} is now short of troops: it may rise up.", MsgKind.Warn);
    }

    /// <summary>Ships: from another fleet here (a transfer), or a whole fleet dragged by its grip (a merge).</summary>
    string RefuseShipsInto(Drag d, Fleet f)
    {
        if (d.Kind is not ('S' or 'M')) return "";
        var (at, fleet) = WhereNow(d);
        if (fleet == f.Id) return "";
        if (!Still(f)) return $"{f.Name} is travelling.";
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var itself)) return $"{f.Name} itself {itself.Words}.";
        if (!S.Game.State.Fleets.TryGetValue(fleet, out var from) || !Still(from)) return $"{ThingName(d)}'s fleet is travelling.";
        if (at != f.At)
        {
            if (d.Kind == 'S')
            {
                // a capital ship splits off, flies there and joins on arrival. rule: m3-fleets §Orders (SplitFleet: sending ships)
                if (_moved.TryGetValue(new Drag('M', from.Id).Key, out var src)) return $"{from.Name} itself {src.Words}.";
                if (from.Ships.Count - ShipsLeaving(from) <= 1) return $"It is {from.Name}'s last ship: drag {from.Name} by its grip (⠿) to send the whole fleet.";
                return Words(Fm.CheckSplitFleet(new SplitFleetOrder(0, Me, from.Id, One(d.Id), f.At, f.Id)));
            }
            return Fm.FindPath(from.At, f.At, Fm.SpeedOf(from)).Ok ? null : $"{from.Name} can't reach {f.At}.";   // it flies there and merges on arrival
        }
        if (d.Kind == 'S' && from.Ships.Count - ShipsLeaving(from) <= 1) return $"It is {from.Name}'s last ship: drag {from.Name} by its grip (⠿) to merge the fleets.";
        // rule: m3-fleets §Orders (TransferShips, MergeFleets)
        return Words(d.Kind == 'S' ? Fm.CheckTransferShips(new TransferShipsOrder(0, Me, One(d.Id), f.Id)) : Fm.CheckMergeFleets(new MergeFleetsOrder(0, Me, from.Id, f.Id)));
    }

    int ShipsLeaving(Fleet f) => f.Ships.Count(id => _moved.ContainsKey(new Drag('S', id).Key));

    void DropShipsInto(Drag d, Fleet f)
    {
        var (_, fleet) = WhereNow(d);
        var from = S.Game.State.Fleets[fleet];
        if (d.Kind == 'M' && from.At != f.At)
        {
            // rule: m3-fleets §Orders (MoveFleet: joining a fleet on arrival)
            int days = Fm.FindPath(from.At, f.At, Fm.SpeedOf(from)) is { Ok: true } p ? p.TotalTicks : 0;
            S.TrackFleet(from.Id, Fm.SubmitMove(Me, from.Id, f.At, null, f.Id), $"Fly {from.Name} to {f.At} and merge into {f.Name}");
            Sent($"fly {from.Name} to {f.At} to merge into {f.Name}", f.At);
            _moved[d.Key] = ($"flies to join {f.Name}, {Ui.Count(days, "day", "days")}", $"Fleet {f.Id} Ships");
            return;
        }
        if (d.Kind == 'S' && from.At != f.At)
        {
            // rule: m3-fleets §Orders (SplitFleet: sending ships)
            int id = d.Id;
            var order = new SplitFleetOrder(0, Me, from.Id, One(id), f.At, f.Id);
            int days = Fm.SplitSendDays(order);
            WarnTraps(Fm.SplitSendRoute(order), $"The {ThingName(d)}'s way to {f.At}");
            S.TrackFleet(from.Id, S.Game.Submit(seq => order with { Sequence = seq }), $"Send the {ThingName(d)} from {from.Name} to join {f.Name}");
            Sent($"send the {ThingName(d)} from {from.Name} to join {f.Name} at {f.At}", f.At);
            _moved[d.Key] = ($"flies to join {f.Name}, {Ui.Count(days, "day", "days")}", $"Fleet {f.Id} Ships");
            return;
        }
        if (d.Kind == 'M') { Merge(from, f); _moved[d.Key] = ($"merges into {f.Name}", $"Fleet {f.Id} Ships"); return; }
        S.TrackFleet(from.Id, Fm.SubmitTransferShips(Me, new[] { d.Id }, f.Id), $"Move a ship from {from.Name} into {f.Name}");
        Sent($"move the {ThingName(d)} from {from.Name} into {f.Name}", f.At);
        _moved[d.Key] = ($"joins {f.Name}", $"Fleet {f.Id} Ships");
    }

    string RefuseSplit(Drag d, Fleet f)
    {
        if (d.Kind != 'S') return "";
        var (_, fleet) = WhereNow(d);
        if (fleet != f.Id) return "Split ships off their own fleet: drop them on its New fleet.";
        // QA-82: a split given after today's merge of this fleet would run after it, on a fleet that is gone by then
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var itself)) return $"{f.Name} itself {itself.Words}.";
        if (f.Ships.Count - ShipsLeaving(f) <= 1) return "A fleet keeps at least one ship.";
        // the drop withdraws today's split of this fleet and sends one with every ship so far (DropSplit). rule: m3-fleets §Orders (SplitFleet)
        var ships = _splits.TryGetValue(f.Id, out var prev) ? new List<int>(prev.Ships) { d.Id } : One(d.Id);
        return Words(Fm.CheckSplitFleet(new SplitFleetOrder(0, Me, f.Id, ships)));
    }

    readonly Dictionary<int, (long Seq, List<int> Ships)> _splits = new(); // today's split order per fleet (lookup only)

    void DropSplit(Drag d, Fleet f)
    {
        var ships = new List<int> { d.Id };
        if (_splits.TryGetValue(f.Id, out var prev) && S.Withdraw(prev.Seq)) ships.InsertRange(0, prev.Ships);   // rule: m1-clock-and-orders §Orders in (withdraw)
        _splits[f.Id] = (S.TrackFleet(f.Id, Fm.SubmitSplitFleet(Me, f.Id, ships), ships.Count == 1 ? $"Split a ship off {f.Name}" : $"Split {ships.Count} ships off {f.Name}"), ships);
        Sent($"split the {ThingName(d)} off {f.Name} into a new fleet", f.At);
        _moved[d.Key] = ("forms a new fleet", $"Fleet {f.Id} NewFleet");
    }

    string RegionOf(string n)
    {
        if (SectorOf(n) is not string sec) return "Outer space";
        var s = G.Sectors.FirstOrDefault(x => x.Name == sec);
        string region = s?.Group switch { "heartland" => "Heartland", "midreach" => "Midreach", "farreach" => "Farreach", var g => g ?? "" };
        return $"{region} · {sec}";
    }

    /// <summary>A drop is its own feedback (the chip moves at once): the "order sent, starts next day" toast stays quiet meanwhile.</summary>
    internal bool MgDropping { get; private set; }
    void RunDrop(Action drop) { MgDropping = true; try { drop(); } finally { MgDropping = false; } }

    // ================================================================== for the playtest

    /// <summary>Drop a chip on a zone as the mouse would: returns null when it was done, else why not.</summary>
    internal string MgDrop(string chipKey, string zoneKey)
    {
        NewDay();
        var z = _zones.FirstOrDefault(x => x.Key == zoneKey && IsInstanceValid(x.Box));
        if (z == null) return "no such zone: " + zoneKey;
        var all = Drag.ParseAll(chipKey);
        if (all.Count == 0) return "bad chip: " + chipKey;
        string why = Judge(z, all[0]);
        if (why != null) return why == "" ? "already there" : why;
        foreach (var d in all) if (Judge(z, d) == null) RunDrop(() => z.Drop(d));
        RefreshMgmt();
        foreach (var w in _mgWins.ToList()) RebuildMg(w);
        return null;
    }

    internal bool MgHasZone(string zoneKey) => _zones.Any(x => x.Key == zoneKey && IsInstanceValid(x.Box));

    internal string MgText(string key)
    {
        if (_mgWins.FirstOrDefault(w => w.Key == key) is not { } w) return "";
        RebuildMg(w);   // as the next frame would
        return LabelsIn(w.Head) + "\n" + string.Join("\n", w.Pages.Values.Select(p => PageLabels(p.Page))) + "\n" + LabelsIn(w.Body);
    }

    /// <summary>Every label of a tab, shown or not (the playtest reads tabs that are not in front).</summary>
    static string PageLabels(Control page)
    {
        bool was = page.Visible;
        page.Visible = true;
        string text = LabelsIn(page);
        page.Visible = was;
        return text;
    }

    internal string MgTab(string key) => _mgWins.FirstOrDefault(w => w.Key == key)?.Tab;
    internal IReadOnlyList<string> MgTabs(string key) => _mgWins.FirstOrDefault(w => w.Key == key)?.Pages.Keys.ToList() ?? new List<string>();

    /// <summary>For screenshots: show the drop zones lit as if this chip were being dragged.</summary>
    internal void MgShowDrag(string chipKey)
    {
        // "L:?" picks the first leader of yours standing where the first window is (screenshots don't know ids)
        if (chipKey == "L:?" && _mgWins.FirstOrDefault(x => x.System != null) is { } w)
            chipKey = S.MyCharacters.Where(c => S.Host.Characters.LocationOf(c) == w.System && c.AttachedFleet == 0).OrderBy(c => c.Id).Select(c => $"L:{c.Id}").FirstOrDefault() ?? chipKey;
        if (Drag.Parse(chipKey) is { } d) { _dragging = d; _dragCount = Drag.ParseAll(chipKey).Count; _shotDrag = d; LightZones(d); }
    }
    Drag? _shotDrag;   // a screenshot holds a drag without a mouse
}
