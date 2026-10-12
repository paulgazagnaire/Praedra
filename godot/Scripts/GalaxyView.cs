using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Rebellion.Core.Map;
using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>
/// The 2D galaxy map. Draws every system and lane from the core's Galaxy, handles zoom, pan,
/// hover and click. Reads the model only; it never changes game state and holds no rules.
/// </summary>
public partial class GalaxyView : Control
{
    // Zoom (1 = whole galaxy fits). Names fade in above NameZoom, sector names fade out there.
    const float MinZoom = 0.8f, MaxZoom = 16f;
    const float NameZoomFrom = 2.0f, NameZoomTo = 3.0f;
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
    public static readonly Color EmpireColor = new(0.35f, 0.85f, 0.35f);
    public static readonly Color AllianceColor = new(1.0f, 0.55f, 0.15f);
    public static readonly Color NeutralColor = new(0.55f, 0.57f, 0.62f);
    /// <summary>Owner of a system by index (null = neutral).</summary>
    public Func<int, Side?> OwnerOf;
    /// <summary>True for a headquarters / capital system.</summary>
    public Func<int, bool> IsHq;
    /// <summary>A tag drawn under a system at any zoom ("YOUR HQ", "ENEMY CAPITAL"), or null.</summary>
    public Func<int, (string Text, Color Color)?> CapitalTag;
    public Func<int, bool> IsUprising;
    public Func<int, bool> IsBlockaded;
    /// <summary>While true a click on a system raises <see cref="SystemPicked"/> instead of selecting it (used to choose a destination).</summary>
    public bool PickMode { get; set; }
    public event Action<int> SystemPicked;
    /// <summary>A route to draw on top of the lanes (system indices), e.g. the preview of a move order.</summary>
    public IReadOnlyList<int> PathPreview { get; set; }
    public Color PathColor { get; set; } = Colors.White;

    Vector2[] _pos;                 // world position per system (map_x, map_y)
    Color[] _color;                 // per system, by sector
    Vector2 _min, _max;
    Vector2 _center;                // world point at the middle of the control
    float _zoom = 1f;
    bool _dragging, _dragMoved;
    Vector2 _pressAt;
    Font _font;

    // Sector label anchors (centroid, colour), sorted by name.
    readonly List<(string name, Vector2 world, Color color)> _sectorLabels = new();

    public float Zoom => _zoom;

    public void Init(Galaxy galaxy)
    {
        Galaxy = galaxy;
        _font = ThemeDB.FallbackFont;
        ClipContents = true;
        int n = galaxy.Systems.Count;
        _pos = new Vector2[n];
        _color = new Color[n];
        _min = new Vector2(float.MaxValue, float.MaxValue);
        _max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < n; i++)
        {
            var s = galaxy.Systems[i];
            _pos[i] = new Vector2(s.MapX, s.MapY);
            _min = new Vector2(Mathf.Min(_min.X, s.MapX), Mathf.Min(_min.Y, s.MapY));
            _max = new Vector2(Mathf.Max(_max.X, s.MapX), Mathf.Max(_max.Y, s.MapY));
        }
        // Stable colour per sector: sectors are sorted by name, hue steps by the golden ratio.
        var names = galaxy.Sectors.Select(s => s.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var byName = new Dictionary<string, Color>(StringComparer.Ordinal);
        for (int k = 0; k < names.Count; k++)
        {
            float hue = (k * 0.61803398875f) % 1f;
            float sat = 0.55f + 0.2f * ((k * 7) % 3) / 2f;
            float val = 0.85f + 0.1f * ((k * 5) % 2);
            byName[names[k]] = Color.FromHsv(hue, sat, val);
        }
        for (int i = 0; i < n; i++)
            _color[i] = byName.TryGetValue(galaxy.Systems[i].Sector, out var c) ? c : new Color(1, 1, 1);
        foreach (var sec in galaxy.Sectors.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var sum = Vector2.Zero;
            foreach (int m in sec.Members) sum += _pos[m];
            _sectorLabels.Add((sec.Name, sum / sec.Members.Count, byName[sec.Name]));
        }
        ResetView();
    }

    public Color ColorOf(int system) => _color[system];

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
    public Vector2 WorldToScreen(Vector2 w) => Size / 2f + (w - _center) * PxPerUnit;
    public Vector2 ScreenToWorld(Vector2 s) => _center + (s - Size / 2f) / PxPerUnit;
    public Vector2 SystemWorld(int i) => _pos[i];
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
                        Selected = hit; // click on empty space clears the selection
                        StateChanged?.Invoke();
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
                if (h != Hovered) { Hovered = h; StateChanged?.Invoke(); QueueRedraw(); }
            }
        }
        else if (e is InputEventMagnifyGesture mg) ZoomAt(mg.Position, mg.Factor);
        else if (e is InputEventPanGesture pg) { _center += pg.Delta * 6f / PxPerUnit; Changed(); }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && Hovered != -1) { Hovered = -1; StateChanged?.Invoke(); QueueRedraw(); }
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

    // ---- drawing ----
    public override void _Draw()
    {
        if (Galaxy == null) return;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.03f, 0.04f, 0.07f));
        int focus = Selected >= 0 ? Selected : Hovered;
        var screenRect = new Rect2(Vector2.Zero, Size).Grow(40);

        // lanes: thickness and brightness by tier (1 route, 2 official, 3 generated)
        foreach (var lane in Galaxy.Lanes)
        {
            var a = SystemScreen(lane.A); var b = SystemScreen(lane.B);
            if (!screenRect.HasPoint(a) && !screenRect.HasPoint(b) && !SegmentMayCross(a, b)) continue;
            var (w, alpha) = lane.Tier switch { 1 => (2.6f, 0.95f), 2 => (1.6f, 0.60f), _ => (1.0f, 0.22f) };
            var col = lane.Tier == 1 ? new Color(0.95f, 0.85f, 0.45f) : new Color(0.62f, 0.72f, 0.90f);
            col.A = alpha;
            DrawLine(a, b, col, w, true);
        }
        // lanes of the focused system, drawn on top
        if (focus >= 0)
            foreach (var nb in Galaxy.NeighboursOf(focus))
                DrawLine(SystemScreen(focus), SystemScreen(nb.System), new Color(1f, 1f, 1f, 0.9f), 2.2f, true);

        // sector names (zoomed out)
        float sectorAlpha = Mathf.Clamp((NameZoomTo - _zoom) / (NameZoomTo - NameZoomFrom + 0.4f), 0f, 1f);
        if (sectorAlpha > 0.02f)
        {
            foreach (var (name, world, color) in _sectorLabels)
            {
                var p = WorldToScreen(world);
                if (!screenRect.HasPoint(p)) continue;
                string text = name.EndsWith(" Sector", StringComparison.Ordinal) ? name[..^7] : name;
                int fs = 15;
                var size = _font.GetStringSize(text, HorizontalAlignment.Left, -1, fs);
                var pos = p + new Vector2(-size.X / 2f, fs / 3f);
                DrawStringOutline(_font, pos, text, HorizontalAlignment.Left, -1, fs, 4, new Color(0, 0, 0, 0.85f * sectorAlpha));
                DrawString(_font, pos, text, HorizontalAlignment.Left, -1, fs, new Color(color, 0.95f * sectorAlpha));
            }
        }

        // route preview
        if (PathPreview is { Count: > 1 })
        {
            for (int k = 0; k + 1 < PathPreview.Count; k++)
                DrawLine(SystemScreen(PathPreview[k]), SystemScreen(PathPreview[k + 1]), PathColor, 4f, true);
        }

        // systems: fill = owner, thin ring = sector, bigger double ring = headquarters, red ring = uprising
        float radius = Mathf.Clamp(3.2f + _zoom * 1.0f, 3.6f, 10f);
        for (int i = 0; i < _pos.Length; i++)
        {
            var p = SystemScreen(i);
            if (!screenRect.HasPoint(p)) continue;
            var s = Galaxy.Systems[i];
            var owner = OwnerOf?.Invoke(i);
            var fill = owner == Side.Empire ? EmpireColor : owner == Side.Alliance ? AllianceColor : NeutralColor;
            if (s.IsOutsideSectors) DrawArc(p, radius + 4.5f, 0, Mathf.Tau, 24, new Color(1, 1, 1, 0.8f), 1.5f, true);
            DrawArc(p, radius + 1.2f, 0, Mathf.Tau, 20, _color[i], 2.2f, true);
            DrawCircle(p, radius, fill);
            if (IsHq?.Invoke(i) == true)
            {
                DrawArc(p, radius + 5f, 0, Mathf.Tau, 28, Colors.White, 2f, true);
                DrawArc(p, radius + 8f, 0, Mathf.Tau, 32, fill, 2f, true);
            }
            if (IsUprising?.Invoke(i) == true) DrawArc(p, radius + 11f, 0, Mathf.Tau, 32, new Color(1f, 0.15f, 0.15f), 2.5f, true);
            if (IsBlockaded?.Invoke(i) == true) DrawArc(p, radius + 14f, 0, Mathf.Tau, 32, new Color(1f, 0.9f, 0.2f, 0.9f), 1.5f, true);
        }
        if (focus >= 0) DrawArc(SystemScreen(focus), radius + 4f, 0, Mathf.Tau, 32, Colors.White, 2f, true);
        if (Hovered >= 0 && Hovered != focus) DrawArc(SystemScreen(Hovered), radius + 3f, 0, Mathf.Tau, 32, new Color(1, 1, 1, 0.6f), 1.5f, true);

        // capital tags: always visible, so the player can find the headquarters at any zoom
        if (CapitalTag != null)
            for (int i = 0; i < _pos.Length; i++)
            {
                if (CapitalTag(i) is not { } tag) continue;
                var p = SystemScreen(i);
                if (!screenRect.HasPoint(p)) continue;
                int fs = 12;
                var size = _font.GetStringSize(tag.Text, HorizontalAlignment.Left, -1, fs);
                var box = new Rect2(p + new Vector2(-size.X / 2f - 4f, radius + 12f), size + new Vector2(8f, 4f));
                DrawRect(box, new Color(0.03f, 0.04f, 0.07f, 0.9f));
                DrawRect(box, tag.Color, false, 1.5f);
                DrawString(_font, box.Position + new Vector2(4f, size.Y - 1f), tag.Text, HorizontalAlignment.Left, -1, fs, tag.Color);
            }

        // system names (zoomed in)
        float nameAlpha = Mathf.Clamp((_zoom - NameZoomFrom) / (NameZoomTo - NameZoomFrom), 0f, 1f);
        if (nameAlpha > 0.02f)
        {
            int fs = 13;
            for (int i = 0; i < _pos.Length; i++)
            {
                var p = SystemScreen(i);
                if (!screenRect.HasPoint(p)) continue;
                var pos = p + new Vector2(radius + 3f, fs / 3f);
                string text = Galaxy.Systems[i].Name;
                DrawStringOutline(_font, pos, text, HorizontalAlignment.Left, -1, fs, 4, new Color(0, 0, 0, 0.85f * nameAlpha));
                DrawString(_font, pos, text, HorizontalAlignment.Left, -1, fs, new Color(1, 1, 1, 0.92f * nameAlpha));
            }
        }
        else if (focus >= 0)
        {
            // always label the focused system, even when zoomed out
            var p = SystemScreen(focus);
            var pos = p + new Vector2(radius + 6f, 5f);
            string text = Galaxy.Systems[focus].Name;
            DrawStringOutline(_font, pos, text, HorizontalAlignment.Left, -1, 14, 4, new Color(0, 0, 0, 0.9f));
            DrawString(_font, pos, text, HorizontalAlignment.Left, -1, 14, Colors.White);
        }
    }

    // Cheap test so long lanes crossing the screen from two off-screen ends still draw.
    bool SegmentMayCross(Vector2 a, Vector2 b)
    {
        var r = new Rect2(Vector2.Zero, Size);
        var bb = new Rect2(a, Vector2.Zero).Expand(b);
        return r.Intersects(bb);
    }
}
