using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Map;
using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The 2D galaxy map. Draws every system and lane from the core's Galaxy, handles zoom, pan,
/// hover and click. Reads the model only; it never changes game state and holds no rules.
/// </summary>
public partial class GalaxyView : Control
{
    // Zoom (1 = whole galaxy fits).
    const float MinZoom = 0.8f, MaxZoom = 16f;
    // Names of systems nobody holds come in only from NeutralNameZoom, unexplored ones from UnknownNameZoom: zoomed out the map names
    // the worlds that matter (yours, the enemy's, the capitals), and hovering any dot still names it.
    // Paul's mockup (2026-10-28) shows dots and no names at the opening zoom: held systems are named only from OwnedNameZoom, and pointing at
    // a sector names all its systems.
    const float OwnedNameZoom = 3.4f;
    const float NeutralNameZoom = 5.0f;
    const float UnknownNameZoom = 7.0f;
    // Room kept free around every name, so two names never touch: space is part of the label.
    static readonly Vector2 NamePad = new(7, 3);
    const float PickRadiusPx = 11f;
    const float Margin = 40f;

    public Galaxy Galaxy { get; private set; }
    public int Hovered { get; private set; } = -1;
    public int Selected { get; private set; } = -1;
    /// <summary>Fleet id of the selected fleet, or -1.</summary>
    public int SelectedFleet { get; set; } = -1;
    /// <summary>Set by the owner: returns the fleet id under a screen point, or -1. Fleets win over systems on click.</summary>
    public Func<Vector2, int> FleetPicker;

    public event Action StateChanged;

    // ---- game overlays, set by the owner (the map only draws what it is told)
    // Blue is always the player, orange the enemy, grey nobody (Paul's layout canvas, 2026-10-26): the colour says "mine or theirs", whichever side you play.
    public static readonly Color NeutralColor = Ui.Neutral;
    /// <summary>The colour of a side as the player sees it.</summary>
    public Color SideColor(Side s) => Ui.OwnerColor(s, Player ?? Side.Hand);

    /// <summary>A soft halo behind a system for the map layer being shown (support, trouble, idle yards, intel age, slots). Size is in dot radii.</summary>
    public readonly record struct MapMark(Color Color, float Size, bool Ring = false);
    /// <summary>The halo of a system under the current map layer, or null for none. Set by the owner.</summary>
    public Func<int, MapMark?> Mark;
    /// <summary>Owner of a system by index (null = neutral).</summary>
    public Func<int, Side?> OwnerOf;
    /// <summary>True for a headquarters / capital system.</summary>
    public Func<int, bool> IsHq;
    /// <summary>A tag drawn under a system at any zoom ("YOUR HQ", "ENEMY CAPITAL"), or null.</summary>
    public Func<int, (string Text, Color Color)?> CapitalTag;
    /// <summary>True for a system the player's side has never explored (fog of war): drawn dimmed, owner unknown.</summary>
    public Func<int, bool> IsUnknown;
    /// <summary>Set by the owner: returns the system index of an enemy fleet marker under a screen point, or -1 (clicking it selects that system).</summary>
    public Func<Vector2, int> EnemyMarkerPicker;
    /// <summary>True for a system the player's side knows the harvester destroyed: drawn dark grey with an X (also if it was never explored).</summary>
    public Func<int, bool> IsDestroyed;
    /// <summary>Is this system drawn as destroyed right now? (what <see cref="_Draw"/> uses; also read by the playtest)</summary>
    public bool DrawsDestroyed(int i) => IsDestroyed?.Invoke(i) == true;
    /// <summary>The player's side: its systems win the fight for label room.</summary>
    public Side? Player { get; set; }
    /// <summary>Screen rectangles the system names must keep clear of (fleet markers and their names), set by the owner.</summary>
    public Func<IEnumerable<Rect2>> LabelObstacles;
    public Func<int, bool> IsUprising;
    public Func<int, bool> IsBlockaded;
    /// <summary>While true a click on a system raises <see cref="SystemPicked"/> instead of selecting it (used to choose a destination).</summary>
    public bool PickMode { get; set; }
    public event Action<int> SystemPicked;
    /// <summary>A route to draw on top of the hyperspace routes (system indices), e.g. the preview of a move order.
    /// Each leg is a straight segment: solid where it rides a route link, dashed where it is a free jump through open space.</summary>
    public IReadOnlyList<int> PathPreview { get; set; }
    public Color PathColor { get; set; } = Colors.White;
    /// <summary>Per leg of <see cref="PathPreview"/>: true when it rides a route link (solid), false for a free jump (dashed). Null: worked out from the links.</summary>
    public IReadOnlyList<bool> PathRouteHop { get; set; }
    /// <summary>Systems on the preview where the player's picture shows an enemy gravity-well ship (circled in red).</summary>
    public IReadOnlyList<int> PathTraps { get; set; }
    /// <summary>Waypoints the player added with shift-click, in order (numbered on the map).</summary>
    public IReadOnlyList<int> Waypoints { get; set; }

    /// <summary>Whether the map draws the hyperspace routes (Paul 2026-10-16: every route link is visible; L toggles).</summary>
    bool _showRoutes = true;
    public bool ShowRoutes { get => _showRoutes; set { _showRoutes = value; QueueRedraw(); } }

    Vector2[] _pos;                 // world position per system (map_x, map_y)
    int[] _weight;                  // per system, route links weighted by tier (hubs win the fight for label room)
    int[] _drawOrder;               // route links, minor first so the major routes draw on top
    Vector2 _min, _max;
    Vector2 _center;                // world point at the middle of the control
    float _zoom = 1f;
    bool _dragging, _dragMoved;
    Vector2 _pressAt;
    Font _font;
    GalaxyBackdrop _backdrop;       // the galaxy picture, drawn behind this control

    // Sectors (name, member systems), sorted by name; _sectorOf[system] indexes it, -1 outside the sectors.
    readonly List<(string name, int[] members)> _sectors = new();
    int[] _sectorOf;
    Rect2[] _sectorBox;             // world bounds of each sector's systems, to find the sector under the mouse quickly
    const float SectorPull = 0.7f;  // how tightly a sector is drawn around its centre (1 = as in the galaxy data)
    const float SectorPadPx = 14f;  // the sector's shape reaches this far past its outer systems on screen

    /// <summary>The sector under the mouse when it is not over a system (-1: none). Its whole shape lights up; a click opens it.</summary>
    public int HoveredSector { get; private set; } = -1;
    /// <summary>A click inside a sector, away from its systems: the sector's name.</summary>
    public event Action<string> SectorClicked;
    public string SectorName(int k) => k >= 0 && k < _sectors.Count ? _sectors[k].name : null;

    public float Zoom => _zoom;

    public void Init(Galaxy galaxy)
    {
        Galaxy = galaxy;
        _font = Ui.Medium ?? ThemeDB.FallbackFont;
        ClipContents = true;
        int n = galaxy.Systems.Count;
        _pos = new Vector2[n];
        _weight = new int[n];
        _sectorOf = new int[n];
        Array.Fill(_sectorOf, -1);
        _min = new Vector2(float.MaxValue, float.MaxValue);
        _max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < n; i++)
        {
            var s = galaxy.Systems[i];
            _pos[i] = new Vector2(s.MapX, s.MapY);
            _min = new Vector2(Mathf.Min(_min.X, s.MapX), Mathf.Min(_min.Y, s.MapY));
            _max = new Vector2(Mathf.Max(_max.X, s.MapX), Mathf.Max(_max.Y, s.MapY));
        }
        foreach (var lane in galaxy.Lanes)
        {
            int w = lane.Tier switch { 1 => 3, 2 => 2, _ => 1 };
            _weight[lane.A] += w; _weight[lane.B] += w;
        }
        _drawOrder = Enumerable.Range(0, galaxy.Lanes.Count).OrderByDescending(i => galaxy.Lanes[i].Tier).ThenBy(i => i).ToArray();
        foreach (var sec in galaxy.Sectors.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            foreach (int m in sec.Members) _sectorOf[m] = _sectors.Count;
            _sectors.Add((sec.Name, sec.Members.ToArray()));
        }
        // Each sector is drawn a little tighter around its own centre, so open space shows between sectors and every sector reads as one
        // group (Paul 2026-10-28). Drawing only: travel and distances use the galaxy's own coordinates.
        _sectorBox = new Rect2[_sectors.Count];
        for (int k = 0; k < _sectors.Count; k++)
        {
            var mem = _sectors[k].members;
            var c = mem.Aggregate(Vector2.Zero, (a, m) => a + _pos[m]) / Math.Max(1, mem.Length);
            foreach (int m in mem) _pos[m] = c + (_pos[m] - c) * SectorPull;
            var box = new Rect2(_pos[mem[0]], Vector2.Zero);
            foreach (int m in mem) box = box.Expand(_pos[m]);
            _sectorBox[k] = box;
        }
        // the galaxy picture behind the map, centred on Charter (the disc's centre in the galaxy data), else on the map's middle
        var centre = galaxy.TryFindSystem("Charter", out int capital) ? new Vector2(galaxy.Systems[capital].MapX, galaxy.Systems[capital].MapY) : (_min + _max) / 2f;
        _backdrop?.QueueFree();
        _backdrop = new GalaxyBackdrop(this, centre, _pos);
        AddChild(_backdrop);
        ResetView();
    }

    public void ResetView()
    {
        _zoom = 1f;
        _center = (_min + _max) / 2f;
        Changed();
    }

    public void FocusOn(int system, float zoom)
    {
        _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        _center = _pos[system];
        Changed();
    }

    /// <summary>Centre on a system keeping the zoom (zooms in a little if the map is far out).</summary>
    public void Centre(int system) { _center = _pos[system]; if (_zoom < 2.5f) _zoom = 2.5f; Changed(); }
    public void SetZoom(float zoom) { _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom); Changed(); }
    /// <summary>Show these systems in one part of the screen (the sector window covers the rest): pan so they sit in its middle, and
    /// zoom so they fill it well (out if they would not fit, in if they would be a speck).</summary>
    public void FrameIn(IReadOnlyList<int> systems, Rect2 area)
    {
        if (systems.Count == 0 || area.Size.X < 50 || area.Size.Y < 50) return;
        Vector2 min = _pos[systems[0]], max = min;
        foreach (int i in systems) { min = min.Min(_pos[i]); max = max.Max(_pos[i]); }
        var span = (max - min).Max(Vector2.One);
        float fit = Mathf.Min((area.Size.X - 120) / span.X, (area.Size.Y - 140) / span.Y);
        float px = Mathf.Clamp(PxPerUnit, fit * 0.5f, fit);
        _zoom = Mathf.Clamp(px / FitScale(), MinZoom, MaxZoom);
        _center = (min + max) / 2f - (area.GetCenter() - Size / 2f) / PxPerUnit;
        Changed();
    }
    /// <summary>Pretend the mouse is over a system (playtest and screenshots).</summary>
    public void ShotHover(int system) { Hovered = system; QueueRedraw(); }
    /// <summary>Pretend the mouse is over a sector's open space (playtest and screenshots).</summary>
    public void ShotHoverSector(string name) { HoveredSector = _sectors.FindIndex(x => x.name == name); Hovered = -1; QueueRedraw(); }
    /// <summary>Click a sector's open space as the mouse would (playtest).</summary>
    public void ShotClickSector(string name) => SectorClicked?.Invoke(name);
    public void Select(int system) { SelectedFleet = -1; Selected = system; StateChanged?.Invoke(); QueueRedraw(); }

    // ---- transform ----
    float FitScale()
    {
        var span = _max - _min;
        float sx = (Size.X - 2 * Margin) / Mathf.Max(1f, span.X);
        float sy = (Size.Y - 2 * Margin) / Mathf.Max(1f, span.Y);
        return Mathf.Max(0.05f, Mathf.Min(sx, sy));
    }
    public float PxPerUnit => FitScale() * _zoom;
    public Vector2 ViewCenter => _center;
    public Vector2 WorldToScreen(Vector2 w) => Size / 2f + (w - _center) * PxPerUnit;
    public Vector2 ScreenToWorld(Vector2 s) => _center + (s - Size / 2f) / PxPerUnit;
    public Vector2 SystemScreen(int i) => WorldToScreen(_pos[i]);

    void Changed()
    {
        foreach (var child in GetChildren()) if (child is CanvasItem ci) ci.QueueRedraw();
        QueueRedraw();
    }

    // ---- input ----
    public override void _GuiInput(InputEvent e)
    {
        if (Galaxy == null) return;
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) ZoomAt(mb.Position, 1.15f);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) ZoomAt(mb.Position, 1f / 1.15f);
            else if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Middle)
            {
                if (mb.Pressed) { _dragging = true; _dragMoved = false; _pressAt = mb.Position; }
                else
                {
                    if (_dragging && !_dragMoved && mb.ButtonIndex == MouseButton.Left && PickMode)
                    {
                        int pick = Pick(mb.Position);
                        if (pick >= 0) SystemPicked?.Invoke(pick);
                    }
                    else if (_dragging && !_dragMoved && mb.ButtonIndex == MouseButton.Left)
                    {
                        int fleet = FleetPicker?.Invoke(mb.Position) ?? -1;
                        SelectedFleet = fleet;
                        int hit = fleet >= 0 ? -1 : Pick(mb.Position);
                        if (fleet < 0 && hit < 0) hit = EnemyMarkerPicker?.Invoke(mb.Position) ?? -1;
                        Selected = hit; // click on empty space clears the selection
                        StateChanged?.Invoke();
                        if (fleet < 0 && hit < 0 && SectorAt(mb.Position) is int sk and >= 0) SectorClicked?.Invoke(_sectors[sk].name);
                        QueueRedraw();
                    }
                    _dragging = false;
                }
            }
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (_dragging)
            {
                if (!_dragMoved && mm.Position.DistanceTo(_pressAt) > 4f) _dragMoved = true;
                if (_dragMoved) { _center -= mm.Relative / PxPerUnit; Changed(); }
            }
            else
            {
                int h = Pick(mm.Position);
                int hs = h >= 0 ? -1 : SectorAt(mm.Position);
                if (h != Hovered || hs != HoveredSector)
                {
                    Hovered = h; HoveredSector = hs;
                    MouseDefaultCursorShape = h >= 0 || hs >= 0 ? CursorShape.PointingHand : CursorShape.Arrow;
                    StateChanged?.Invoke(); QueueRedraw();
                }
            }
        }
        else if (e is InputEventMagnifyGesture mg) ZoomAt(mg.Position, mg.Factor);
        else if (e is InputEventPanGesture pg) { _center += pg.Delta * 6f / PxPerUnit; Changed(); }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && (Hovered != -1 || HoveredSector != -1)) { Hovered = -1; HoveredSector = -1; StateChanged?.Invoke(); QueueRedraw(); }
        if (what == NotificationResized) Changed();
    }

    public void ZoomAt(Vector2 screen, float factor)
    {
        var before = ScreenToWorld(screen);
        _zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);
        var after = ScreenToWorld(screen);
        _center += before - after;
        Changed();
    }

    public void ZoomCentre(float factor) => ZoomAt(Size / 2f, factor);
    public void PanPixels(Vector2 px) { _center += px / PxPerUnit; Changed(); }

    int Pick(Vector2 screen)
    {
        int best = -1; float bestD = PickRadiusPx * PickRadiusPx;
        for (int i = 0; i < _pos.Length; i++)
        {
            float d = SystemScreen(i).DistanceSquaredTo(screen);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>The sector whose shape holds a screen point (-1: open space).</summary>
    public int SectorAt(Vector2 screen)
    {
        if (_sectorBox == null) return -1;
        var w = ScreenToWorld(screen);
        float pad = (SectorPadPx + 10f) / PxPerUnit;
        int best = -1; float bestD = float.MaxValue;
        for (int k = 0; k < _sectors.Count; k++)
        {
            if (!_sectorBox[k].Grow(pad).HasPoint(w)) continue;
            var shape = SectorOutline(k, SectorPadPx + SystemRadius);
            if (shape == null || !Geometry2D.IsPointInPolygon(screen, shape)) continue;
            // two shapes can overlap at their edges: the sector with the nearest system wins
            float d = _sectors[k].members.Min(m => _pos[m].DistanceSquaredTo(w));
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    float SystemRadius => Mathf.Clamp(3.2f + _zoom * 1.0f, 3.6f, 10f);

    // ---- drawing ----
    // Colours of the map itself. Only the owner of a system gets a strong colour (blue you, orange the enemy);
    // everything else (lanes, sectors, unexplored systems) is drawn in quiet greys and blues so ownership reads first.
    static readonly Color LaneColor = new(0.58f, 0.60f, 0.65f);
    static readonly Color MajorRouteColor = new(0.72f, 0.75f, 0.80f);
    static readonly Color DotOutline = new(0.03f, 0.04f, 0.07f, 0.95f);
    static readonly Color SectorColor = new(0.70f, 0.78f, 0.95f);

    public override void _Draw()
    {
        if (Galaxy == null) return;
        if (_backdrop == null) DrawRect(new Rect2(Vector2.Zero, Size), Ui.MapBg); // the backdrop paints the background itself
        int focus = Selected >= 0 ? Selected : Hovered;
        var screenRect = new Rect2(Vector2.Zero, Size).Grow(40);
        float radius = SystemRadius;

        // the sector under the mouse lights up as one block (fill, edge, name): pointing at its open space, or at one of its systems;
        // with nothing under the mouse, the selected system's sector keeps a quiet outline
        bool areaHover = HoveredSector >= 0;
        int hoverSector = areaHover ? HoveredSector : Hovered >= 0 ? _sectorOf[Hovered] : Selected >= 0 ? _sectorOf[Selected] : -1;
        bool lit = areaHover || Hovered >= 0;
        // every sector is a quiet tile of its own (a faint edge, no colour), so the groups read at a glance and the gaps between them show
        for (int k = 0; k < _sectors.Count; k++)
        {
            if (k == hoverSector) continue;
            var bw = _sectorBox[k];
            var a = WorldToScreen(bw.Position); var b = WorldToScreen(bw.End);
            if (!new Rect2(a, b - a).Grow(radius + SectorPadPx).Intersects(screenRect)) continue;
            var shape = SectorOutline(k, radius + SectorPadPx);
            if (shape == null) continue;
            DrawColoredPolygon(shape, new Color(SectorColor, 0.025f));
            DrawPolyline(Closed(shape), new Color(SectorColor, 0.16f), 1f, true);
        }
        Vector2[] sectorShape = hoverSector >= 0 ? SectorOutline(hoverSector, radius + SectorPadPx) : null;
        if (sectorShape != null)
        {
            DrawColoredPolygon(sectorShape, new Color(SectorColor, lit ? (areaHover ? 0.12f : 0.08f) : 0.05f));
            DrawPolyline(Closed(sectorShape), new Color(SectorColor, lit ? (areaHover ? 0.75f : 0.5f) : 0.3f), areaHover ? 2f : 1.2f, true);
        }

        // hyperspace routes (planets/15, Paul 2026-10-16): ships fly anywhere, the drawn links are the fast stretches.
        // Major routes (3x) are brighter and thicker than minor routes (1.5x); nothing else is drawn between systems.
        for (int k = 0; _showRoutes && k < _drawOrder.Length; k++)
        {
            var lane = Galaxy.Lanes[_drawOrder[k]];
            var a = SystemScreen(lane.A); var b = SystemScreen(lane.B);
            if (!screenRect.HasPoint(a) && !screenRect.HasPoint(b) && !SegmentMayCross(a, b)) continue;
            if (lane.Tier == 1) DrawLine(a, b, new Color(MajorRouteColor, 0.34f), 1.8f, true);
            else DrawLine(a, b, new Color(LaneColor, 0.18f), 1.0f, true);
        }
        // route links of the focused system, drawn on top
        if (_showRoutes && focus >= 0)
            foreach (var nb in Galaxy.NeighboursOf(focus))
                DrawLine(SystemScreen(focus), SystemScreen(nb.System), new Color(1f, 1f, 1f, 0.7f), nb.Lane.Tier == 1 ? 2.6f : 1.8f, true);

        // route preview: solid along a route link, dashed for a free jump through open space
        if (PathPreview is { Count: > 1 })
        {
            for (int k = 0; k + 1 < PathPreview.Count; k++)
            {
                var a = SystemScreen(PathPreview[k]); var b = SystemScreen(PathPreview[k + 1]);
                bool route = PathRouteHop != null && k < PathRouteHop.Count ? PathRouteHop[k] : Galaxy.FindLane(PathPreview[k], PathPreview[k + 1]) != null;
                if (route) DrawLine(a, b, PathColor, 4f, true);
                else DrawDashedLine(a, b, PathColor, 3f, 9f, true, true);
            }
            for (int k = 1; k + 1 < PathPreview.Count; k++) // the stops on the way
                DrawCircle(SystemScreen(PathPreview[k]), 3.2f, PathColor);
        }
        // known traps on the way: a red ring with a cross-hair, so the player sees where the fleet can be pulled out of hyperspace
        if (PathTraps != null)
            foreach (int t in PathTraps)
            {
                var p = SystemScreen(t);
                var red = new Color(1f, 0.3f, 0.25f);
                DrawArc(p, radius + 9f, 0, Mathf.Tau, 28, red, 2.5f, true);
                DrawLine(p + new Vector2(-(radius + 14f), 0), p + new Vector2(-(radius + 5f), 0), red, 2f, true);
                DrawLine(p + new Vector2(radius + 5f, 0), p + new Vector2(radius + 14f, 0), red, 2f, true);
                DrawLine(p + new Vector2(0, -(radius + 14f)), p + new Vector2(0, -(radius + 5f)), red, 2f, true);
                DrawLine(p + new Vector2(0, radius + 5f), p + new Vector2(0, radius + 14f), red, 2f, true);
            }
        // waypoints: numbered yellow squares
        if (Waypoints != null)
            for (int k = 0; k < Waypoints.Count; k++)
            {
                var p = SystemScreen(Waypoints[k]) + new Vector2(radius + 6f, -(radius + 16f));
                DrawRect(new Rect2(p, new Vector2(15, 15)), new Color(1f, 0.85f, 0.35f));
                DrawString(_font, p + new Vector2(3.5f, 12.5f), (k + 1).ToString(), HorizontalAlignment.Left, -1, 12, new Color(0.05f, 0.05f, 0.08f));
            }

        // the map layer: a soft halo behind each marked system (drawn under the dots, so the owner colour still reads)
        if (Mark != null)
            for (int i = 0; i < _pos.Length; i++)
            {
                if (Mark(i) is not { } m) continue;
                var p = SystemScreen(i);
                if (!screenRect.HasPoint(p)) continue;
                float r = radius * m.Size;
                if (m.Ring) { DrawArc(p, r, 0, Mathf.Tau, 32, m.Color, 2.5f, true); continue; }
                for (int k = 4; k >= 1; k--) DrawCircle(p, r * k / 4f, new Color(m.Color, m.Color.A * 0.24f));
            }

        // systems: the whole dot is the owner's colour; neutral = small grey dot; unexplored = faint hollow ring.
        // Bigger double ring = headquarters, red ring = uprising, yellow ring = blockade.
        for (int i = 0; i < _pos.Length; i++)
        {
            var p = SystemScreen(i);
            if (!screenRect.HasPoint(p)) continue;
            if (DrawsDestroyed(i))
            {
                // destroyed by the harvester: a dark grey disc with a red-grey X, no owner, no HQ ring; the lanes through it still work
                DrawCircle(p, radius, new Color(0.16f, 0.16f, 0.18f));
                DrawArc(p, radius, 0, Mathf.Tau, 20, new Color(0.45f, 0.45f, 0.5f), 1.2f, true);
                float k = radius * 1.5f;
                var xc = new Color(0.8f, 0.35f, 0.3f);
                DrawLine(p + new Vector2(-k, -k), p + new Vector2(k, k), xc, 2.2f, true);
                DrawLine(p + new Vector2(-k, k), p + new Vector2(k, -k), xc, 2.2f, true);
                continue;
            }
            if (IsUnknown?.Invoke(i) == true)
            {
                // never explored by the player's side: nothing known about owner or state (fog of war)
                DrawArc(p, radius * 0.7f, 0, Mathf.Tau, 16, new Color(0.45f, 0.48f, 0.55f, 0.55f), 1.2f, true);
                continue;
            }
            var owner = OwnerOf?.Invoke(i);
            if (owner == null)
            {
                DrawCircle(p, radius * 0.72f + 1f, DotOutline);
                DrawCircle(p, radius * 0.72f, NeutralColor);
                continue;
            }
            var fill = SideColor(owner.Value);
            DrawCircle(p, radius + 1.2f, DotOutline);
            DrawCircle(p, radius, fill);
            if (IsHq?.Invoke(i) == true)
            {
                DrawArc(p, radius + 5f, 0, Mathf.Tau, 28, Colors.White, 2f, true);
                DrawArc(p, radius + 8f, 0, Mathf.Tau, 32, fill, 2f, true);
            }
            if (IsUprising?.Invoke(i) == true) DrawArc(p, radius + 11f, 0, Mathf.Tau, 32, Ui.Danger, 2.5f, true);
            if (IsBlockaded?.Invoke(i) == true) // dashed, so it differs from the uprising ring by shape too, not only by colour
                for (int k = 0; k < 12; k++) DrawArc(p, radius + 14f, k * Mathf.Tau / 12f, k * Mathf.Tau / 12f + Mathf.Tau / 24f, 4, Ui.Warn, 2f, true);
        }
        if (focus >= 0) DrawArc(SystemScreen(focus), radius + 4f, 0, Mathf.Tau, 32, Colors.White, 2f, true);
        if (Hovered >= 0 && Hovered != focus) DrawArc(SystemScreen(Hovered), radius + 3f, 0, Mathf.Tau, 32, new Color(1, 1, 1, 0.6f), 1.5f, true);

        // capital tags: always visible, so the player can find the headquarters at any zoom
        var taken = new LabelSpace();
        if (CapitalTag != null)
            for (int i = 0; i < _pos.Length; i++)
            {
                if (CapitalTag(i) is not { } tag) continue;
                var p = SystemScreen(i);
                if (!screenRect.HasPoint(p)) continue;
                // the tag carries the system's name too (white), so the name never loses the fight for room next to its own tag
                int fs = 12;
                string name = Galaxy.Systems[i].Name + "  ";
                var nameSize = _font.GetStringSize(name, HorizontalAlignment.Left, -1, fs + 1);
                var tagSize = _font.GetStringSize(tag.Text, HorizontalAlignment.Left, -1, fs);
                var size = new Vector2(nameSize.X + tagSize.X, Mathf.Max(nameSize.Y, tagSize.Y));
                var box = new Rect2(p + new Vector2(-size.X / 2f - 5f, radius + 12f), size + new Vector2(10f, 4f));
                DrawRect(box, new Color(0.03f, 0.04f, 0.07f, 0.9f));
                DrawRect(box, tag.Color, false, 1.5f);
                var baseLine = box.Position + new Vector2(5f, 2f + _font.GetAscent(fs + 1));
                DrawString(_font, baseLine, name, HorizontalAlignment.Left, -1, fs + 1, Colors.White);
                DrawString(_font, baseLine + new Vector2(nameSize.X, 0), tag.Text, HorizontalAlignment.Left, -1, fs, tag.Color);
                taken.Add(box);
            }

        // the hovered sector's name, just above its outline (only on hover: for the selected system it would sit on its fleets' names)
        if (sectorShape != null && lit)
        {
            string text = _sectors[hoverSector].name.ToUpperInvariant();
            const int fs = 13;
            var size = _font.GetStringSize(text, HorizontalAlignment.Left, -1, fs);
            float top = sectorShape.Min(v => v.Y), cx = sectorShape.Average(v => v.X);
            var box = new Rect2(new Vector2(cx - size.X / 2f, top - size.Y - 2f), size);
            DrawStringOutline(_font, box.Position + new Vector2(0, _font.GetAscent(fs)), text, HorizontalAlignment.Left, -1, fs, 4, new Color(0, 0, 0, 0.9f));
            DrawString(_font, box.Position + new Vector2(0, _font.GetAscent(fs)), text, HorizontalAlignment.Left, -1, fs, new Color(SectorColor, 0.95f));
            taken.Add(box);
        }

        DrawSystemNames(focus, radius, screenRect, taken);
    }

    // ---- system names ----
    // Names never sit on each other, on a system dot, on a capital tag or on a fleet. Each name tries eight spots around its dot
    // and is left out when none is free. Names are placed in order of importance (the focused system, headquarters, your systems,
    // enemy systems, neutral ones; big lane hubs first), so zoomed out you read the key worlds and more names appear as you zoom in.
    static readonly Vector2[] Spots =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(0.7f, -0.7f), new(0.7f, 0.7f), new(-0.7f, -0.7f), new(-0.7f, 0.7f),
    };

    void DrawSystemNames(int focus, float radius, Rect2 screenRect, LabelSpace taken)
    {
        var view = new Rect2(Vector2.Zero, Size);
        var order = new List<(int i, int rank)>();
        for (int i = 0; i < _pos.Length; i++)
        {
            var p = SystemScreen(i);
            if (!screenRect.HasPoint(p)) continue;
            float r = radius + 1.5f;
            taken.Add(new Rect2(p - new Vector2(r, r), new Vector2(2 * r, 2 * r)));
            bool unknown = IsUnknown?.Invoke(i) == true;
            bool key = i == focus || i == Hovered || (HoveredSector >= 0 && _sectorOf[i] == HoveredSector);
            if (!key && unknown && _zoom < UnknownNameZoom) continue;
            if (!key && !unknown && OwnerOf?.Invoke(i) == null && _zoom < NeutralNameZoom) continue;
            if (!key && _zoom < OwnedNameZoom) continue;
            if (CapitalTag?.Invoke(i) != null) continue; // its name is in the tag
            order.Add((i, LabelRank(i, focus, unknown)));
        }
        if (LabelObstacles != null) foreach (var rect in LabelObstacles()) taken.Add(rect);
        order.Sort((x, y) => x.rank != y.rank ? y.rank.CompareTo(x.rank) : _weight[y.i] != _weight[x.i] ? _weight[y.i].CompareTo(_weight[x.i]) : x.i.CompareTo(y.i));

        foreach (var (i, _) in order)
        {
            bool key = i == focus || i == Hovered;
            int fs = key ? 14 : 13;
            string text = Galaxy.Systems[i].Name;
            var size = _font.GetStringSize(text, HorizontalAlignment.Left, -1, fs);
            var p = SystemScreen(i);
            float gap = radius + (key ? 6f : 3f);
            Rect2? spot = null;
            foreach (var d in Spots)
            {
                // the spot's anchor is on a circle around the dot; the text box hangs off it away from the dot
                var anchor = p + d * gap;
                var box = new Rect2(anchor + new Vector2(d.X > 0.1f ? 0 : d.X < -0.1f ? -size.X : -size.X / 2f,
                                                         d.Y > 0.1f ? 0 : d.Y < -0.1f ? -size.Y : -size.Y / 2f), size);
                if (!view.Encloses(box) || taken.Hits(box)) continue;
                spot = box; break;
            }
            if (spot == null && key) spot = new Rect2(p + new Vector2(gap, -size.Y / 2f), size); // the focused name always shows
            if (spot is not { } at) continue;
            taken.Add(at.GrowIndividual(NamePad.X, NamePad.Y, NamePad.X, NamePad.Y)); // the next name keeps its distance
            var baseLine = at.Position + new Vector2(0, _font.GetAscent(fs));
            bool unknown = IsUnknown?.Invoke(i) == true;
            float a = key ? 1f : unknown ? 0.45f : OwnerOf?.Invoke(i) == null ? 0.72f : 0.92f;
            DrawStringOutline(_font, baseLine, text, HorizontalAlignment.Left, -1, fs, 4, new Color(0, 0, 0, 0.85f));
            DrawString(_font, baseLine, text, HorizontalAlignment.Left, -1, fs, new Color(1, 1, 1, a));
        }
    }

    int LabelRank(int i, int focus, bool unknown)
    {
        if (i == focus || i == Hovered) return 100;
        if (unknown) return 0;
        var owner = OwnerOf?.Invoke(i);
        if (owner != null && owner == Player) return 30;
        if (owner != null) return 20;
        return 10;
    }

    /// <summary>Rectangles already used on screen; a coarse grid keeps the overlap test cheap with 360 names.</summary>
    sealed class LabelSpace
    {
        const float Cell = 64f;
        readonly Dictionary<(int, int), List<Rect2>> _cells = new(); // looked up by key only, never iterated
        public void Add(Rect2 r)
        {
            foreach (var key in Keys(r))
            {
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<Rect2>();
                list.Add(r);
            }
        }
        public bool Hits(Rect2 r)
        {
            foreach (var key in Keys(r))
                if (_cells.TryGetValue(key, out var list))
                    foreach (var o in list) if (o.Intersects(r)) return true;
            return false;
        }
        static IEnumerable<(int, int)> Keys(Rect2 r)
        {
            int x0 = Mathf.FloorToInt(r.Position.X / Cell), x1 = Mathf.FloorToInt(r.End.X / Cell);
            int y0 = Mathf.FloorToInt(r.Position.Y / Cell), y1 = Mathf.FloorToInt(r.End.Y / Cell);
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) yield return (x, y);
        }
    }

    // ---- sector outline (shown on hover) ----
    /// <summary>A rounded shape around the systems of a sector on screen, padded by <paramref name="pad"/> pixels.</summary>
    Vector2[] SectorOutline(int sector, float pad)
    {
        var pts = _sectors[sector].members.Select(SystemScreen).ToArray();
        // a small circle around each system, then the hull of all of them: rounded, and never degenerate
        var cloud = new List<Vector2>(pts.Length * 12);
        foreach (var p in pts)
            for (int k = 0; k < 12; k++) cloud.Add(p + Vector2.FromAngle(k * Mathf.Tau / 12f) * pad);
        var hull = Geometry2D.ConvexHull(cloud.ToArray());
        return hull.Length >= 3 ? hull[..^1] : null; // ConvexHull repeats the first point at the end
    }

    static Vector2[] Closed(Vector2[] poly) => poly.Append(poly[0]).ToArray();

    // Cheap test so long lanes crossing the screen from two off-screen ends still draw.
    bool SegmentMayCross(Vector2 a, Vector2 b)
    {
        var r = new Rect2(Vector2.Zero, Size);
        var bb = new Rect2(a, Vector2.Zero).Expand(b);
        return r.Intersects(bb);
    }
}
