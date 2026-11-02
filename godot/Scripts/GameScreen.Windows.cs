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
    public enum MgKind { Infra, Ground, Fleet }

    sealed class MgWin
    {
        public MgKind Kind;
        public string System;   // Infra and Ground
        public int Fleet;       // Fleet
        public PanelContainer Panel;
        public VBoxContainer Head, Body;
        public ScrollContainer Scroll;
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
            return s.Length > 2 && s[1] == ':' && int.TryParse(s.AsSpan(2), out int id) ? new Drag(s[0], id) : null;
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

    const int MgWidth = 384;
    const int ChipWidth = 164;

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

    /// <summary>Open (or bring to the front) a system's Infrastructure or Ground window, or a fleet's window.</summary>
    internal void OpenMgmt(MgKind kind, string system = null, int fleet = 0)
    {
        if (kind == MgKind.Fleet ? !S.Game.State.Fleets.TryGetValue(fleet, out var f0) || f0.Side != Me : system == null) return;
        var w = _mgWins.FirstOrDefault(x => x.Kind == kind && (kind == MgKind.Fleet ? x.Fleet == fleet : x.System == system));
        if (w == null)
        {
            w = new MgWin { Kind = kind, System = system, Fleet = fleet };
            BuildMgWin(w);
            _mgWins.Add(w);
            PlaceNew(w);
        }
        else Front(w);
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
        var headPad = Pad(16, 10, 12, 8);
        w.Head = Ui.VBox(Ui.Gap);
        headPad.AddChild(w.Head);
        v.AddChild(headPad);
        // the head moves the window
        Vector2? grab = null;
        headPad.MouseFilter = MouseFilterEnum.Stop;
        headPad.MouseDefaultCursorShape = CursorShape.Move;
        headPad.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b) { grab = b.Pressed ? w.Panel.GetGlobalMousePosition() - w.Panel.Position : null; if (b.Pressed) Front(w); }
            else if (e is InputEventMouseMotion && grab is Vector2 g) { w.Panel.Position = ClampWin(w.Panel.GetGlobalMousePosition() - g, w.Panel.Size); }
        };
        var line = new ColorRect { Color = new Color(1, 1, 1, 0.06f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore };
        v.AddChild(line);
        w.Scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        v.AddChild(w.Scroll);
        var bodyPad = Pad(16, 16, 12, 16);
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

    /// <summary>
    /// A new window goes in the first free column beside the sector window (on the map's side), then in any free column of the screen, else
    /// beside the window on top, shifted so its title stays visible (QA on #227: a second fleet window opened right over the first).
    /// </summary>
    void PlaceNew(MgWin w)
    {
        var vp = _mgLayer.Size;
        var others = _mgWins.Where(x => x != w).Select(x => new Rect2(x.Panel.Position, new Vector2(MgWidth, Math.Max(200, x.Panel.Size.Y)))).ToList();
        bool Free(float x) => !others.Any(r => r.Position.X < x + MgWidth && x < r.End.X);
        IEnumerable<float> Columns(float lo, float hi) { for (float x = lo; x + MgWidth <= hi + 1; x += MgWidth + 12) yield return x; }
        float lo = 16, hi = vp.X - 16;
        if (SectorViewOpen) { var r = _secWin.GetGlobalRect(); if (_secLeft) lo = r.End.X + 12; else hi = r.Position.X - 12; }
        foreach (float x in Columns(lo, hi).Concat(Columns(16, vp.X - 16)).Concat(Columns(vp.X - 16 - MgWidth, vp.X).Reverse()))
            if (Free(x)) { w.Panel.Position = new Vector2(x, TopClear); return; }
        // no free column: step beside the top window, the other way when that runs off the screen
        var last = others.Count > 0 ? others[^1].Position : new Vector2(lo, TopClear);
        float nx = last.X + MgWidth * 0.5f;
        if (nx + MgWidth > vp.X - 8) nx = last.X - MgWidth * 0.5f;
        w.Panel.Position = ClampWin(new Vector2(nx, last.Y + 36), new Vector2(MgWidth, 200));
    }

    /// <summary>The drops of yesterday have been judged: forget them.</summary>
    void NewDay()
    {
        if (_movedDay == S.Game.Day) return;
        _moved.Clear(); _pendingIn.Clear(); _splits.Clear(); _leaving.Clear(); _movedDay = S.Game.Day;
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
        var prev = _into;
        _into = w.Body;
        try
        {
            switch (w.Kind)
            {
                case MgKind.Fleet: FleetWin(w, S.Game.State.Fleets[w.Fleet]); break;
                case MgKind.Ground: GroundWin(w); break;
                default: InfraWin(w); break;
            }
        }
        finally { _into = prev; }
        // a drop looks done at once (Paul 2026-11-02: "instant"): the chip stands where it was dropped. The core applies the order first thing
        // next step (rule: m1-clock-and-orders §Order of work, step 1), before anything else moves, so nothing can happen in between.
        foreach (var z in _zones.Where(z => w.Panel.IsAncestorOf(z.Box)))
            foreach (var (key, mv) in _moved.Where(kv => kv.Value.Zone == z.Key).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (Drag.Parse(key) is { } gd)
                {
                    foreach (var empty in z.Flow.GetChildren().OfType<Label>().ToList()) empty.QueueFree(); // "Empty" gives way
                    bool travels = mv.Words.StartsWith("on the way", StringComparison.Ordinal) || mv.Words.StartsWith("flies", StringComparison.Ordinal);
                    var ghost = Chip(ThingName(gd), travels ? mv.Words : gd.Kind == 'L' ? LeaderPosts(Cm.Get(gd.Id)) : null, gd.Kind == 'L' ? Initials(ThingName(gd)) : null, Ui.You, travels);
                    ghost.TooltipText = Ui.Wrap($"{ThingName(gd)}: {mv.Words}. It can move again tomorrow.");
                    ghost.CustomMinimumSize = new Vector2(ChipWidth, 0);
                    ghost.SetMeta(UiTest.FullName, "Arriving " + key);
                    z.Flow.AddChild(ghost);
                }
        // as tall as the content needs, up to the screen
        Callable.From(() =>
        {
            if (!IsInstanceValid(w.Panel)) return;
            float want = w.Head.GetParent<Control>().GetCombinedMinimumSize().Y + w.Body.GetCombinedMinimumSize().Y + 34;
            float h = Math.Min(want, _mgLayer.Size.Y - w.Panel.Position.Y - 16);
            w.Panel.Size = new Vector2(MgWidth, Math.Max(160, h));
            w.Scroll.ScrollVertical = (int)keep;
        }).CallDeferred();
    }

    /// <summary>The head of a window: a caption in small capitals, the title, a close button; then, optionally, a line under it.</summary>
    void MgHead(MgWin w, string caption, string title, Color titleColor, string sub = null, Control extra = null)
    {
        var row = Ui.HBox(Ui.Block);
        row.MouseFilter = MouseFilterEnum.Ignore;
        var names = Ui.VBox(2);
        names.MouseFilter = MouseFilterEnum.Ignore;
        names.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var cap = Ui.Label(caption.ToUpperInvariant(), null, Ui.Text3, Ui.Tiny, wrap: false);
        cap.AddThemeFontOverride("font", Ui.Caps);
        names.AddChild(cap);
        var t = Ui.Label(title, null, titleColor, 20, wrap: false);
        t.AddThemeFontOverride("font", Ui.SemiBold);
        t.ClipText = true;
        names.AddChild(t);
        row.AddChild(names);
        if (extra != null) { extra.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(extra); }
        var close = Ui.Button("✕", () => CloseMgmt(w), "Close (Esc)", "Clock");
        close.SetMeta(UiTest.FullName, "Close " + w.Key);
        close.CustomMinimumSize = new Vector2(28, 28);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        row.AddChild(close);
        w.Head.AddChild(row);
        if (sub != null) w.Head.AddChild(Ui.Label(sub, null, Ui.Text2, Ui.Small));
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

    // ---- the Infrastructure window: the system's buildings, slots and build queues (the old Buildings pane)

    void InfraWin(MgWin w)
    {
        string n = w.System;
        var eco = S.Host.Economy;
        bool mine = S.OwnerOf(n) == Me;
        MgHead(w, $"Infrastructure · {RegionOf(n)}", n, mine ? Ui.Text : Ui.Text2,
            mine ? $"{eco.EnergyUsed(n)} of {eco.SlotsOf(n).Energy} energy slots used · {eco.OfKindAt(n, FacilityKind.Mine)} mines · {eco.OfKindAt(n, FacilityKind.Refinery)} refineries" : null);
        PaneBuildings(n);
        MgLinks(($"Ground at {n}", () => OpenMgmt(MgKind.Ground, n), "Leaders, troops and fighters on the planet, and the fleets in orbit"));
    }

    // ---- the Ground window: who stands on the planet, and the fleets in orbit

    void GroundWin(MgWin w)
    {
        string n = w.System;
        bool mine = !S.IsDestroyed(n) && S.OwnerOf(n) == Me;
        string sub = null;
        PanelContainer badge = null;
        if (mine)
        {
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            sub = $"Loyalty {S.Host.Economy.SupportOf(n, Me)}% · {Ui.Count(have, "regiment", "regiments")} · {(need == 0 ? "no garrison needed" : $"garrison needs {need}")}";
            if (have < need) badge = Ui.Tag("UNDER-GARRISONED", Ui.Warn);
            if (S.Host.Support.IsUprising(n)) badge = Ui.Tag("UPRISING", Ui.Danger, true);
        }
        MgHead(w, $"Ground · {RegionOf(n)}", n, mine ? Ui.Text : Ui.Text2, sub, badge);
        if (!mine)
        {
            PaneForces(n); // what your side knows of a planet it doesn't hold
            OrbitList(n);
            return;
        }
        Hint("Drag a leader onto a fleet's post, or troops and fighters onto a fleet, in a fleet window. A drop shows at once; the game makes the change when the next day starts, before anything else moves.");

        // the system's General: one post, held by a leader standing here
        var gen = Cm.SystemGeneralOf(n);
        if (gen != null && gen.Side != Me) gen = null;
        Section("System General", null, "The system's General adds his leadership to the defenders when the planet is invaded.");
        var gz = DropZone($"Ground {n} General", "make General", d => RefuseSystemGeneral(d, n), d => DropSystemGeneral(d, n));
        if (gen != null) gz.Flow.AddChild(LeaderChip(gen, "General of " + n));
        else gz.Flow.AddChild(EmptySlot("Drop a leader with the General's skill"));
        Add(gz.Box);

        // leaders standing here (not on a fleet, not the General)
        var here = S.MyCharacters.Where(c => c.Status != CharacterStatus.Dead && c.AttachedFleet == 0 && c.Transit == null && Cm.LocationOf(c) == n && c.Id != gen?.Id)
            .OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        Section("Leaders on the planet", here.Count == 0 ? null : here.Count.ToString());
        var lz = DropZone($"Ground {n} Leaders", "land", d => RefuseToGround(d, n, 'L'), d => DropToGround(d, n));
        foreach (var c in here) lz.Flow.AddChild(LeaderChip(c, null));
        if (here.Count == 0) lz.Flow.AddChild(EmptySlot("No leader of yours stands here"));
        Add(lz.Box);

        // regiments
        var regs = S.Host.Troops.RegimentsOnSystem(n).Where(o => o.Side == Me).OrderBy(o => o.Id).ToList();
        int need0 = S.Host.Support.RequiredGarrison(n);
        Section("Regiments", need0 > 0 ? $"{regs.Count} / {need0} needed" : regs.Count.ToString(), "Below 60% loyalty a planet needs regiments to stay calm: one for every 10 points under 60. Too few and it rises up.");
        var rz = DropZone($"Ground {n} Regiments", "land", d => RefuseToGround(d, n, 'R'), d => DropToGround(d, n));
        foreach (var o in regs) rz.Flow.AddChild(UnitChip(new Drag('R', o.Id), o.Class, null, Ui.You));
        if (regs.Count == 0) rz.Flow.AddChild(EmptySlot("No regiments"));
        Add(rz.Box);

        // fighters based here
        var based = FightersBasedAt(n);
        Section("Fighters based here", based.Count == 0 ? null : based.Count.ToString());
        var fz = DropZone($"Ground {n} Fighters", "base here", d => RefuseToGround(d, n, 'Q'), d => DropToGround(d, n));
        foreach (var o in based) fz.Flow.AddChild(UnitChip(new Drag('Q', o.Id), o.Class, HullWords(o), Ui.You));
        if (based.Count == 0) fz.Flow.AddChild(EmptySlot("No squadrons"));
        Add(fz.Box);

        // special forces: they never ride fleets (m3-troops-and-special-forces); dragged to another system's Ground window they go there (a Move mission)
        var sf = MySpecialForcesAt(n);
        Section("Special forces", sf.Count == 0 ? null : sf.Count.ToString(), "Agents never ride fleets. Drag one onto another system's Ground window to send it there.");
        var sz = DropZone($"Ground {n} SpecialForces", "send here", d => RefuseToGround(d, n, 'F'), d => DropToGround(d, n));
        foreach (var o in sf) sz.Flow.AddChild(UnitChip(SfReady(o) ? new Drag('F', o.Id) : null, S.SpecialForceName(o.Id), SpecialForceState(o), Ui.You));
        if (sf.Count == 0) sz.Flow.AddChild(EmptySlot("No special forces"));
        Add(sz.Box);
        OnTheWay(S.MyCharacters.Where(c => c.Transit is { } t && t.Path.Count > 0 && t.Path[^1] == n && c.JoinFleet == 0), n);
        OrbitList(n);
        var links = new List<(string, Action, string)> { ($"Infrastructure at {n}", () => OpenMgmt(MgKind.Infra, n), "Buildings, slots and what the planet builds") };
        if (CanSendFrom(n)) links.Add(("Send a mission...", () => { CloseSectorView(); OpenPlanner(n); }, "Pick a team standing here, the kind of mission and the target."));
        MgLinks(links.ToArray());
    }

    /// <summary>Your fleets in orbit, each with a button to open its window (where you drop leaders and troops onto it).</summary>
    void OrbitList(string n)
    {
        var own = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n).OrderBy(f => f.Id).ToList();
        Section("Your fleets in orbit", own.Count == 0 ? null : own.Count.ToString());
        if (own.Count == 0) { Hint("None. Fleets you send here show up in this list."); return; }
        foreach (var f in own)
        {
            var h = Ui.HBox(Ui.Block);
            var tri = new SectorMarks { Side = 9 }.Add(SectorMarks.Shape.Triangle, Ui.You, true);
            tri.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(tri);
            var v = Ui.VBox(0);
            v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var nm = Ui.Label(f.Name, null, Ui.Text, Ui.Body, wrap: false);
            nm.AddThemeFontOverride("font", Ui.SemiBold);
            v.AddChild(nm);
            var adm = Cm.HolderOf(f.Id, CharacterPost.Admiral);
            v.AddChild(Ui.Label($"{Ui.Count(f.Ships.Count, "ship", "ships")} · strength {Fm.StrengthOf(f):0}{(adm != null ? $" · Admiral {S.CharName(adm.Id)}" : "")}", null, Ui.Text3, Ui.Tiny, wrap: false));
            h.AddChild(v);
            int fid = f.Id;
            var open = Ui.Button("Open", () => OpenMgmt(MgKind.Fleet, fleet: fid), $"Open {f.Name}'s window: drag leaders, troops and fighters between it and the planet", "Chip");
            open.SetMeta(UiTest.FullName, $"Open {f.Name}");
            open.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(open);
            Add(h);
        }
    }

    // ---- the Fleet window

    void FleetWin(MgWin w, Fleet f)
    {
        bool still = f.At != null && f.Transit == null;
        string where = still ? $"Fleet · at {f.At}" : f.Transit is { } t ? $"Fleet · to {t.Path[^1]}, day {GameSession.ArrivalDay(t)}" : "Fleet";
        // the grip: drag the whole fleet onto another fleet's ships to merge them
        var grip = Ui.Label("⠿", null, Ui.Text3, 18, wrap: false);
        grip.MouseFilter = MouseFilterEnum.Stop;
        grip.MouseDefaultCursorShape = CursorShape.Drag;
        grip.TooltipText = Ui.Wrap($"Drag {f.Name} onto another fleet's ships to merge it into that fleet.");
        if (still && !_moved.ContainsKey(new Drag('M', f.Id).Key)) MakeDraggable(grip, new Drag('M', f.Id), f.Name);
        MgHead(w, where, f.Name, Ui.You, $"{Ui.Count(f.Ships.Count, "ship", "ships")} · strength {Fm.StrengthOf(f):0} · speed {Fm.SpeedOf(f)}", grip);
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var mto)) Line($"This fleet {mto.Words}.", Pending, Ui.Small);
        if (!still) Hint("Travelling: who rides it and what it carries can change when it arrives.");

        // the three posts
        Section("Command", null, LeaderHelp);
        foreach (var post in new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander })
        {
            var p = post;
            var row = Ui.HBox(Ui.Block);
            var lab = Ui.VBox(0);
            lab.CustomMinimumSize = new Vector2(92, 0);
            lab.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            var pl = Ui.Label(GameSession.PostWords(post), null, Ui.Text2, Ui.Small, wrap: false);
            pl.AddThemeFontOverride("font", Ui.SemiBold);
            lab.AddChild(pl);
            lab.AddChild(Ui.Label(post switch { CharacterPost.Admiral => "bombardment", CharacterPost.General => "invasions", _ => "no effect yet" }, null, Ui.Text3, Ui.Tiny, wrap: false));
            row.AddChild(lab);
            var z = DropZone($"Fleet {f.Id} {post}", $"make {GameSession.PostWords(post)}", d => RefuseFleetPost(d, f, p), d => DropFleetPost(d, f, p));
            z.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (Cm.HolderOf(f.Id, post) is { } holder) z.Flow.AddChild(LeaderChip(holder, null));
            else z.Flow.AddChild(EmptySlot("Empty"));
            row.AddChild(z.Box);
            Add(row);
        }
        var riders = Cm.AttachedTo(f.Id).Where(c => c.Post == CharacterPost.None).OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        Section("Passengers", riders.Count == 0 ? null : riders.Count.ToString(), "Leaders who ride the fleet with no post: they travel with it.");
        var pz = DropZone($"Fleet {f.Id} Passengers", "take aboard", d => RefuseFleetPost(d, f, CharacterPost.None), d => DropFleetPost(d, f, CharacterPost.None));
        foreach (var c in riders) pz.Flow.AddChild(LeaderChip(c, null));
        if (riders.Count == 0) pz.Flow.AddChild(EmptySlot("None"));
        Add(pz.Box);
        // leaders and fleets sent to join it from other systems (m5 §Moving a character alone, m3 §Joining a fleet on arrival)
        OnTheWay(Cm.All.Where(c => c.Side == Me && c.JoinFleet == f.Id && c.JoinByOrder && c.Transit != null), f.At,
            S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.JoinFleet == f.Id && x.Transit != null).OrderBy(x => x.Id));

        // ships, and a place to split some off
        var eco = S.Host.Economy;
        Section("Ships", f.Ships.Count.ToString(), "Drag a ship onto another fleet here to move it there, or onto New fleet to split it off.");
        var sz = DropZone($"Fleet {f.Id} Ships", "join", d => RefuseShipsInto(d, f), d => DropShipsInto(d, f));
        foreach (int id in f.Ships) if (eco.Find(id) is { } o) sz.Flow.AddChild(UnitChip(new Drag('S', o.Id), o.Class, HullWords(o), Ui.You));
        if (still && f.Ships.Count > 1)
        {
            var nz = DropZone($"Fleet {f.Id} NewFleet", "split off", d => RefuseSplit(d, f), d => DropSplit(d, f), dashed: true);
            nz.Box.CustomMinimumSize = new Vector2(ChipWidth, 0);
            nz.Flow.AddChild(EmptySlot("+ New fleet"));
            sz.Flow.AddChild(nz.Box);
        }
        Add(sz.Box);

        // fighters aboard
        var aboardF = FightersAboard(f);
        int berths = Fm.FreeBerths(f);
        Section("Fighters aboard", berths + aboardF.Count == 0 ? "no hangar" : $"{aboardF.Count} · room for {Math.Max(0, berths - Promised(f.Id))}");
        var qz = DropZone($"Fleet {f.Id} Fighters", "load", d => RefuseOnto(d, f, 'Q'), d => DropOnto(d, f));
        foreach (var o in aboardF) qz.Flow.AddChild(UnitChip(new Drag('Q', o.Id), o.Class, HullWords(o), Ui.You));
        foreach (int id in f.Squadrons) if (eco.Find(id) is { } o) qz.Flow.AddChild(UnitChip(null, o.Class, "flies with the fleet", Ui.You));
        if (aboardF.Count + f.Squadrons.Count == 0) qz.Flow.AddChild(EmptySlot(berths > 0 ? "Drop squadrons here to load them" : "No carrier in this fleet"));
        Add(qz.Box);

        // troops aboard
        var aboardR = S.Host.Troops.RegimentsAboard(f);
        int room = Fm.FreeTroopSlots(f);
        Section("Troops aboard", room + aboardR.Count == 0 ? "no transport" : $"{aboardR.Count} · room for {Math.Max(0, room - Promised(f.Id))}");
        var tz = DropZone($"Fleet {f.Id} Troops", "load", d => RefuseOnto(d, f, 'R'), d => DropOnto(d, f));
        foreach (var a in aboardR) tz.Flow.AddChild(UnitChip(new Drag('R', a.Regiment.Id), a.Regiment.Class, null, Ui.You));
        if (aboardR.Count == 0) tz.Flow.AddChild(EmptySlot(room > 0 ? "Drop regiments here to load them" : "No transport in this fleet"));
        Add(tz.Box);

        foreach (var pend in S.PendingOf(f.Id)) Line($"Waiting for the next day: {pend}", Pending, Ui.Small);
        int fid = f.Id;
        var links = new List<(string, Action, string)>();
        if (still) links.Add(($"Ground at {f.At}", () => OpenMgmt(MgKind.Ground, f.At), "The planet's leaders, troops and fighters: drag them onto this fleet"));
        if (still) links.Add(("Move...", () => { var ff = S.Game.State.Fleets[fid]; CloseSectorView(); StartMove(ff); }, "Pick a destination on the map"));
        links.Add(("Orders and attack...", () => { CloseSectorView(); SelectFleet(fid, true); ShowTab(DrawerTab.Selected); }, "Bombard, invade, retreat, scrap: the fleet's full page"));
        MgLinks(links.ToArray());
    }

    /// <summary>Who is travelling here (leaders, and fleets sent to join): name and arrival day. Not draggable while they travel.</summary>
    void OnTheWay(IEnumerable<CharacterRecord> leaders, string to, IEnumerable<Fleet> fleets = null)
    {
        var rows = leaders.OrderBy(c => c.Id).Select(c => (Name: S.CharName(c.Id), Sub: Arrives(c.Transit, c.JoinPost != CharacterPost.None ? $"to be {GameSession.PostWords(c.JoinPost)}" : null), Badge: Initials(S.CharName(c.Id))))
            .Concat((fleets ?? Enumerable.Empty<Fleet>()).Select(x => (Name: x.Name, Sub: Arrives(x.Transit, $"{Ui.Count(x.Ships.Count, "ship", "ships")}, to merge"), Badge: (string)null))).ToList();
        if (rows.Count == 0) return;
        Section("On the way", rows.Count.ToString(), $"Sent here from other systems. They take their place when they reach {to}.");
        var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var r in rows)
        {
            var chip = Chip(r.Name, r.Sub, r.Badge, Ui.You, true);
            chip.CustomMinimumSize = new Vector2(ChipWidth, 0);
            flow.AddChild(chip);
        }
        Add(flow);
    }

    string Arrives(FleetTransit t, string what)
    {
        int left = t == null ? 0 : Math.Max(0, t.DueDay - S.Game.Day) + t.HopTicks.Skip(t.HopIndex + 1).Sum();
        return (what != null ? what + ", " : "") + (left <= 1 ? "arrives tomorrow" : $"{left} days");
    }

    // ================================================================== chips and zones

    /// <summary>A leader as a chip: initials, name, what they lead with. Drags unless an order already moves them today, or they cannot act.</summary>
    Control LeaderChip(CharacterRecord c, string role)
    {
        string name = S.CharName(c.Id);
        var d = new Drag('L', c.Id);
        bool moved = _moved.TryGetValue(d.Key, out var mv);
        if (moved) return new Control { Visible = false };   // it already stands where it was dropped
        string to = mv.Words;
        bool can = c.Status == CharacterStatus.Free && c.Transit == null && !Cm.IsOnMission(c.Id) && !moved;
        string posts = LeaderPosts(c);
        string sub = moved ? "→ " + to : c.Status != CharacterStatus.Free ? GameSession.StatusWords(c) : Cm.IsOnMission(c.Id) ? "on a mission" : posts;
        var chip = Chip(name, sub, Initials(name), can ? Ui.You : Ui.Text3, moved);
        chip.CustomMinimumSize = new Vector2(ChipWidth, 0);
        chip.TooltipText = Ui.Wrap($"{name}. {S.SkillsOf(c)}\n{S.CapabilityOf(c)}{(role != null ? $"\n{role}." : "")}" + (can ? "\nDrag onto a post or a fleet." : moved ? $"\nNext day: {to}." : ""));
        chip.SetMeta(UiTest.FullName, "Leader " + name);
        if (can) MakeDraggable(chip, d, name);
        return chip;
    }

    /// <summary>The posts a leader can hold, short enough for a chip (their leadership is in the tooltip).</summary>
    string LeaderPosts(CharacterRecord c)
    {
        if (c == null) return null;
        var cls = Cm.ClassOf(c);
        var can3 = new[] { cls.CanAdmiral ? "Admiral" : null, cls.CanGeneral ? "General" : null, cls.CanCommander ? "Commander" : null }.Where(x => x != null).ToArray();
        return can3.Length == 3 ? "any post" : can3.Length == 0 ? "passenger only" : string.Join(", ", can3);
    }

    /// <summary>A regiment, squadron or ship as a chip. A null drag is shown but doesn't move (fighters flying with the fleet).</summary>
    Control UnitChip(Drag? d, string cls, string sub, Color accent)
    {
        bool moved = d is { } dd && _moved.ContainsKey(dd.Key);
        if (moved) return new Control { Visible = false };   // it already stands where it was dropped
        var chip = Chip(cls, sub, null, d == null ? Ui.Text3 : accent, moved);
        chip.CustomMinimumSize = new Vector2(ChipWidth, 0);
        chip.TooltipText = Ui.Wrap(ClassTip(cls) + (d != null && !moved ? "\nDrag it to another window." : ""));
        if (d is { } drag)
        {
            chip.SetMeta(UiTest.FullName, $"{drag.Kind}:{drag.Id}");
            if (!moved) MakeDraggable(chip, drag, cls);
        }
        return chip;
    }

    static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "?" : parts.Length == 1 ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant() : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    string HullWords(EconObject o) => Fm.ClassOf(o) is { } sc && o.Hull < sc.Hull ? $"hull {o.Hull * 100 / Math.Max(1, sc.Hull)}%" : null;

    PanelContainer Chip(string title, string sub, string badge, Color accent, bool ghost)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };
        p.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel2, 6, new Color(accent, ghost ? 0.25f : 0.32f), 1, 8, 5));
        var h = Ui.HBox(7);
        h.MouseFilter = MouseFilterEnum.Ignore;
        p.AddChild(h);
        if (badge != null)
        {
            var b = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(26, 26) };
            b.AddThemeStyleboxOverride("panel", Ui.Box(new Color(accent, 0.18f), 13, new Color(accent, 0.6f), 1, 0, 0));
            var bl = Ui.Label(badge, null, accent, 10, wrap: false);
            bl.AddThemeFontOverride("font", Ui.SemiBold);
            bl.HorizontalAlignment = HorizontalAlignment.Center; bl.VerticalAlignment = VerticalAlignment.Center;
            b.AddChild(bl);
            b.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(b);
        }
        else
        {
            var bar = new ColorRect { Color = accent, CustomMinimumSize = new Vector2(3, 0), MouseFilter = MouseFilterEnum.Ignore };
            h.AddChild(bar);
        }
        var v = Ui.VBox(0);
        v.MouseFilter = MouseFilterEnum.Ignore;
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var t = Ui.Label(title, null, Ui.Text, Ui.Small, wrap: false);
        t.AddThemeFontOverride("font", Ui.Medium);
        t.ClipText = true; t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(t);
        if (sub != null)
        {
            var s = Ui.Label(sub, null, ghost ? Pending : Ui.Text3, Ui.Tiny, wrap: false);
            s.ClipText = true; s.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            v.AddChild(s);
        }
        h.AddChild(v);
        if (ghost) p.Modulate = new Color(1, 1, 1, 0.6f);
        return p;
    }

    Control EmptySlot(string text)
    {
        var l = Ui.Label(text, null, Ui.Text3, Ui.Tiny, wrap: false);
        l.CustomMinimumSize = new Vector2(0, 30);
        l.VerticalAlignment = VerticalAlignment.Center;
        l.MouseFilter = MouseFilterEnum.Ignore;
        return l;
    }

    void Hint(string text) => Add(Ui.Label(text, null, Ui.Text3, Ui.Tiny));

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
            Callable.From((Action<Vector2, Variant>)((_, data) => { if (Drag.Parse(data) is { } d && Judge(zone, d) == null) { RunDrop(() => drop(d)); RefreshMgmt(); RefreshSectorView(); } })));
        return (box, flow);
    }

    /// <summary>Whether a drop may happen here (null), quietly not (""), or not, and why.</summary>
    string Judge(Zone z, Drag d) => _moved.TryGetValue(d.Key, out var m) ? (z.Refuse(d) == "" ? "" : $"{ThingName(d)} already moves today: {m.Words}.") : z.Refuse(d);

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

    void MakeDraggable(Control c, Drag d, string label)
    {
        c.MouseDefaultCursorShape = CursorShape.Drag;
        c.SetDragForwarding(Callable.From((Func<Vector2, Variant>)(_ =>
        {
            _dragging = d;
            var prev = Chip(label, null, d.Kind == 'L' ? Initials(label) : null, Ui.You, false);
            prev.CustomMinimumSize = new Vector2(ChipWidth, 0);
            prev.Modulate = new Color(1, 1, 1, 0.9f);
            prev.Rotation = -0.03f;
            c.SetDragPreview(prev);
            return d.Key;
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

    string DropWords(Drag d, Zone z) => $"Drop to {z.Verb}: {ThingName(d)}";

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
            if (kind == 'R') return "Regiments travel only aboard a fleet: load them, move the fleet, land them.";
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
        if (d.Kind == 'R' && fleet == 0)
        {
            _leaving[f.At] = (_leaving.TryGetValue(f.At, out int gone) ? gone : 0) + 1;
            if (S.Host.Support.RegimentCount(f.At) - _leaving[f.At] < S.Host.Support.RequiredGarrison(f.At))
                Toast($"{f.At} is now short of troops: it may rise up.", MsgKind.Warn);
        }
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
            if (d.Kind == 'S') return $"Between systems, whole fleets travel: drop the ship on New fleet, then drag that fleet by its grip (⠿).";
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
        if (Drag.Parse(chipKey) is not { } d) return "bad chip: " + chipKey;
        string why = Judge(z, d);
        if (why != null) return why == "" ? "already there" : why;
        RunDrop(() => z.Drop(d));
        RefreshMgmt();
        foreach (var w in _mgWins.ToList()) RebuildMg(w);
        return null;
    }

    internal bool MgHasZone(string zoneKey) => _zones.Any(x => x.Key == zoneKey && IsInstanceValid(x.Box));

    internal string MgText(string key)
    {
        if (_mgWins.FirstOrDefault(w => w.Key == key) is not { } w) return "";
        RebuildMg(w);   // as the next frame would
        return LabelsIn(w.Head) + "\n" + LabelsIn(w.Body);
    }

    /// <summary>For screenshots: show the drop zones lit as if this chip were being dragged.</summary>
    internal void MgShowDrag(string chipKey)
    {
        // "L:?" picks the first leader of yours standing where the first window is (screenshots don't know ids)
        if (chipKey == "L:?" && _mgWins.FirstOrDefault(x => x.System != null) is { } w)
            chipKey = S.MyCharacters.Where(c => S.Host.Characters.LocationOf(c) == w.System && c.AttachedFleet == 0).OrderBy(c => c.Id).Select(c => $"L:{c.Id}").FirstOrDefault() ?? chipKey;
        if (Drag.Parse(chipKey) is { } d) { _dragging = d; _shotDrag = d; LightZones(d); }
    }
    Drag? _shotDrag;   // a screenshot holds a drag without a mouse
}
