using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Map;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Draws fleets on top of the galaxy map, using the parent GalaxyView's transform. Read-only: it looks at the core's
/// fleet state (Fleet.At, Fleet.Transit) and never changes it. Positions are purely visual.
/// Fog of war: the player's own fleets come from the core's fleet state; enemy fleets only from what the player's side sees now
/// (<see cref="EnemyFleets"/> = the fog's VisibleFleets: class counts at systems it has live access to, no names, ids or routes).
/// rule: m5-fog-and-hq §F3, §N8.
/// </summary>
public partial class FleetLayer : Control
{
    /// <summary>The player's side: its fleets get a white outline.</summary>
    public Side? Player { get; set; }
    /// <summary>Move orders given but not started yet (fleet id to plan); drawn as a route so the player sees what is pending.</summary>
    public Func<IEnumerable<KeyValuePair<int, GameSession.PlannedMove>>> Planned { get; set; }
    /// <summary>The enemy fleets the player's side sees now (system, class counts). Null = no fog (every fleet drawn from the core).</summary>
    public Func<IReadOnlyList<(string System, FleetPicture Fleet)>> EnemyFleets { get; set; }
    /// <summary>The player's own HQ while it moves (path, the hop it is on, arrival day), or null.</summary>
    public Func<(IReadOnlyList<string> Path, int HopIndex, int ArrivalDay)?> HqRoute { get; set; }
    /// <summary>The player's own characters travelling alone (label, path, the hop they are on), including trips that start next day.</summary>
    public Func<IEnumerable<(string Label, IReadOnlyList<string> Path, int HopIndex)>> CharacterRoutes { get; set; }
    /// <summary>The player's own things travelling that are not fleets (leaders alone, mission teams, built units and buildings on the way,
    /// regiments sent alone, the moving HQ). Own side only: fog hides the enemy's (rule: m5-fog-and-hq §F3).</summary>
    public Func<IEnumerable<Mover>> Movers { get; set; }
    /// <summary>Draw deliveries (units and buildings on their way from the yard, troops sent alone)? Layers > Deliveries.</summary>
    public bool ShowDeliveries { get; set; } = true;
    static bool IsDelivery(MoverKind k) => k is MoverKind.Regiment or MoverKind.Ship or MoverKind.Fighter or MoverKind.Facility;
    const float PickRadiusPx = 13f;
    const float MoverR = 5.5f;
    const float LabelZoom = 2.0f;

    Game _game;
    Galaxy _galaxy;
    /// <summary>0..1: how far through the current day the real-time clock is (0 when paused). Smooths motion between Steps.</summary>
    public float DayFraction { get; set; }

    public GalaxyView Map => GetParent<GalaxyView>();

    public void Bind(Game game, Galaxy galaxy) { _game = game; _galaxy = galaxy; QueueRedraw(); }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta) => QueueRedraw(); // markers move with the real-time fraction

    /// <summary>Blue for the player's fleets, orange for the enemy's, whichever side the player is.</summary>
    Color ColorOf(Side s) => Ui.OwnerColor(s, Player ?? Side.Hand);

    /// <summary>Zoomed out, the fleets of one side at a system share one marker with a count (Tab steps through them); zoomed in, each has its own.</summary>
    bool Stacked => Map.Zoom < LabelZoom;

    // how many fleets share each stacked marker, filled by Layout (looked up by key, never iterated)
    readonly Dictionary<(string System, bool Own), int> _stack = new();

    int SlotFor(string system, bool own, Dictionary<string, int> slots)
    {
        if (Stacked)
        {
            var key = (system, own);
            _stack[key] = (_stack.TryGetValue(key, out int n) ? n : 0) + 1;
            return own ? 0 : 1;
        }
        slots.TryGetValue(system, out int slot);
        slots[system] = slot + 1;
        return slot;
    }

    /// <summary>Visual position (screen) of a fleet and its heading (unit vector), or false if it cannot be placed.</summary>
    bool Place(Fleet f, Dictionary<string, int> slots, out Vector2 pos, out Vector2 heading)
    {
        heading = Vector2.Up; pos = Vector2.Zero;
        var map = Map;
        if (f.Transit is { } t && f.At == null)
        {
            if (!_galaxy.TryFindSystem(t.Path[t.HopIndex], out int a) || !_galaxy.TryFindSystem(t.Path[t.HopIndex + 1], out int b)) return false;
            int ticks = Math.Max(1, t.HopTicks[t.HopIndex]);
            // days already flown in this hop: ticks - days still to go; plus the real-time part of the current day.
            float elapsed = ticks - (t.DueDay - _game.Day) + DayFraction;
            float k = Mathf.Clamp(elapsed / ticks, 0f, 1f);
            var pa = map.SystemScreen(a); var pb = map.SystemScreen(b);
            pos = pa.Lerp(pb, k);
            var d = pb - pa;
            if (d.LengthSquared() > 0.01f) heading = d.Normalized();
            return true;
        }
        if (f.At != null && _galaxy.TryFindSystem(f.At, out int s))
        {
            int slot = SlotFor(f.At, f.Side == Player || Player == null, slots);
            // above the dot, stacked upwards one row per fleet (a name label fits beside each marker); after 6 rows a new column
            const int rows = 6;
            float colWidth = map.Zoom >= LabelZoom ? 130f : 14f;
            pos = map.SystemScreen(s) + new Vector2(-6f + colWidth * (slot / rows), -13f - 16f * (slot % rows));
            return true;
        }
        return false;
    }

    /// <summary>Own fleets (all fleets when there is no fog source).</summary>
    IEnumerable<(Fleet f, Vector2 pos, Vector2 heading)> Placed() => Layout().Own;

    /// <summary>Own fleets, then the enemy markers in the slots after them at each system.</summary>
    (List<(Fleet f, Vector2 pos, Vector2 heading)> Own, List<(string System, FleetPicture Fleet, Vector2 pos)> Enemy) Layout()
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal); // counts only, never iterated
        _stack.Clear();
        var own = new List<(Fleet, Vector2, Vector2)>();
        foreach (var f in _game.State.Fleets.Values)               // fleet id order
            if ((EnemyFleets == null || f.Side == Player) && Place(f, slots, out var p, out var h)) own.Add((f, p, h));
        var enemy = new List<(string, FleetPicture, Vector2)>();
        if (EnemyFleets != null)
            foreach (var (sys, pic) in EnemyFleets())                  // systems in ordinal order
                if (PlaceAt(sys, slots, out var p)) enemy.Add((sys, pic, p));
        return (own, enemy);
    }

    bool PlaceAt(string system, Dictionary<string, int> slots, out Vector2 pos)
    {
        pos = Vector2.Zero;
        if (!_galaxy.TryFindSystem(system, out int s)) return false;
        var map = Map;
        int slot = SlotFor(system, false, slots);
        const int rows = 6;
        float colWidth = map.Zoom >= LabelZoom ? 130f : 14f;
        pos = map.SystemScreen(s) + new Vector2(-6f + colWidth * (slot / rows), -13f - 16f * (slot % rows));
        return true;
    }

    /// <summary>The systems where an enemy fleet marker is drawn now (for the playtest).</summary>
    public IReadOnlyList<string> EnemyMarkerSystems() => _game == null ? Array.Empty<string>() : Layout().Enemy.Select(e => e.System).ToList();

    /// <summary>The fleets drawn now from the core's fleet state (only own fleets with fog on), by id (for the playtest).</summary>
    public IReadOnlyList<int> DrawnFleetIds() => _game == null ? Array.Empty<int>() : Layout().Own.Select(o => o.f.Id).ToList();

    /// <summary>The movers with their screen position and heading (unit vector). A held one sits just below its system.</summary>
    List<(Mover m, Vector2 pos, Vector2 heading)> PlacedMovers()
    {
        var list = new List<(Mover, Vector2, Vector2)>();
        if (Movers == null) return list;
        foreach (var m in Movers())
            if ((ShowDeliveries || !IsDelivery(m.Kind)) && PlaceMover(m, out var p, out var h)) list.Add((m, p, h));
        return list;
    }

    bool PlaceMover(Mover m, out Vector2 pos, out Vector2 heading)
    {
        pos = Vector2.Zero; heading = Vector2.Right;
        var map = Map;
        var pts = new List<Vector2>(m.Path.Count);
        foreach (var n in m.Path) { if (!_galaxy.TryFindSystem(n, out int i)) return false; pts.Add(map.SystemScreen(i)); }
        if (pts.Count == 0) return false;
        int hop = Math.Clamp(m.HopIndex, 0, pts.Count - 1);
        if (m.Held || pts.Count < 2 || hop >= pts.Count - 1) { pos = pts[hop] + new Vector2(9f, 9f); return true; }
        if (m.HopTicks != null)
        {
            // as a fleet: days flown in this hop, plus the real-time part of the current day
            int ticks = Math.Max(1, m.HopTicks[hop]);
            float k = Mathf.Clamp((ticks - (m.DueDay - _game.Day) + DayFraction) / ticks, 0f, 1f);
            pos = pts[hop].Lerp(pts[hop + 1], k);
            var d = pts[hop + 1] - pts[hop];
            if (d.LengthSquared() > 0.01f) heading = d.Normalized();
            return true;
        }
        // only the trip's total is known (a mission team): spread it over the path by length
        float total = 0f;
        for (int i = 0; i + 1 < pts.Count; i++) total += pts[i].DistanceTo(pts[i + 1]);
        int all = Math.Max(1, m.TotalTicks);
        float want = Mathf.Clamp((all - (m.DueDay - _game.Day) + DayFraction) / all, 0f, 1f) * total;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            float len = pts[i].DistanceTo(pts[i + 1]);
            if (want <= len || i + 2 == pts.Count)
            {
                pos = pts[i].Lerp(pts[i + 1], len > 0.01f ? Mathf.Clamp(want / len, 0f, 1f) : 1f);
                if (len > 0.01f) heading = (pts[i + 1] - pts[i]) / len;
                return true;
            }
            want -= len;
        }
        pos = pts[^1]; return true;
    }

    /// <summary>The labels of the movers drawn now (for the playtest).</summary>
    public IReadOnlyList<string> MoverLabels() => _game == null ? Array.Empty<string>() : PlacedMovers().Select(x => x.m.Label).ToList();

    /// <summary>Screen rectangles of the fleet markers and their names, so the map keeps system names off them.</summary>
    public IEnumerable<Rect2> Obstacles()
    {
        if (_game == null) yield break;
        var map = Map;
        var font = Ui.Medium ?? ThemeDB.FallbackFont;
        var layout = Layout();
        foreach (var (f, p, _) in layout.Own)
        {
            bool selected = map.SelectedFleet == f.Id;
            float r = selected ? 10f : 8.5f;
            yield return new Rect2(p - new Vector2(r, r), new Vector2(2 * r, 2 * r));
            if (map.Zoom >= LabelZoom || selected) yield return NameBox(font, p, r, f.Name);
        }
        foreach (var (_, pic, p) in layout.Enemy)
        {
            const float r = 8.5f;
            yield return new Rect2(p - new Vector2(r, r), new Vector2(2 * r, 2 * r));
            if (map.Zoom >= LabelZoom) yield return NameBox(font, p, r, EnemyText(pic));
        }
        foreach (var (m, p, _) in PlacedMovers())
        {
            yield return new Rect2(p - new Vector2(MoverR + 1, MoverR + 1), new Vector2(2 * MoverR + 2, 2 * MoverR + 2));
            if (map.Zoom >= LabelZoom) yield return NameBox(font, p, MoverR, m.Label);
        }
    }

    static Rect2 NameBox(Font font, Vector2 p, float r, string text)
    {
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, 13);
        var baseLine = p + new Vector2(r + 3f, -r * 0.6f);
        return new Rect2(baseLine - new Vector2(0, font.GetAscent(13)), size);
    }

    static string EnemyText(FleetPicture pic) => $"Enemy: {pic.ShipCount} ship(s){(pic.SquadronCount > 0 ? $", {pic.SquadronCount} sq." : "")}";

    /// <summary>The system index of the enemy marker under a screen point, or -1.</summary>
    public int PickEnemySystem(Vector2 screen)
    {
        if (_game == null) return -1;
        string best = null; float bestD = PickRadiusPx * PickRadiusPx;
        foreach (var (sys, _, p) in Layout().Enemy)
        {
            float d = p.DistanceSquaredTo(screen);
            if (d <= bestD) { bestD = d; best = sys; }
        }
        return best != null && _galaxy.TryFindSystem(best, out int idx) ? idx : -1;
    }

    /// <summary>The fleet id under a screen point, or -1.</summary>
    public int Pick(Vector2 screen)
    {
        if (_game == null) return -1;
        int best = -1; float bestD = PickRadiusPx * PickRadiusPx;
        foreach (var (f, p, _) in Placed())
        {
            float d = p.DistanceSquaredTo(screen);
            if (d < bestD || (best < 0 && d <= bestD)) { bestD = d; best = f.Id; } // a stack picks its first fleet (lowest id)
        }
        return best;
    }

    void DrawDestination(Font font, Vector2 at, string text, Color col, bool label)
    {
        DrawArc(at, 9f, 0, Mathf.Tau, 20, col, 2f, true);
        if (!label) return;
        var pos = at + new Vector2(12f, 16f);
        DrawStringOutline(font, pos, text, HorizontalAlignment.Left, -1, 12, 4, new Color(0, 0, 0, 0.9f));
        DrawString(font, pos, text, HorizontalAlignment.Left, -1, 12, col);
    }

    public override void _Draw()
    {
        if (_game == null) return;
        var map = Map;
        var font = Ui.Medium ?? ThemeDB.FallbackFont;
        var layout = Layout();
        var placed = layout.Own;

        // the player's own HQ while it moves: a dashed gold line along the rest of its route, arrival day at the end
        if (HqRoute?.Invoke() is { } hq && hq.Path.Count > 1)
        {
            var gold = new Color(1f, 0.95f, 0.5f, 0.95f);
            Vector2? from = null;
            for (int i = Math.Max(0, hq.HopIndex); i < hq.Path.Count; i++)
            {
                if (!_galaxy.TryFindSystem(hq.Path[i], out int sys)) break;
                var to = map.SystemScreen(sys);
                if (from is Vector2 f0) DrawDashedLine(f0, to, gold, 2.5f, 5f);
                from = to;
            }
            if (from is Vector2 end) DrawDestination(font, end, $"HQ arrives day {hq.ArrivalDay}", gold, true);
        }

        // the player's characters travelling alone: a thin dashed light-blue line, arrival day at the end
        if (CharacterRoutes != null)
            foreach (var cr in CharacterRoutes())
            {
                var blue = new Color(0.55f, 0.85f, 1f, 0.9f);
                Vector2? from = null;
                for (int i = Math.Max(0, cr.HopIndex); i < cr.Path.Count; i++)
                {
                    if (!_galaxy.TryFindSystem(cr.Path[i], out int sys)) break;
                    var to = map.SystemScreen(sys);
                    if (from is Vector2 f0) DrawDashedLine(f0, to, blue, 1.8f, 4f);
                    from = to;
                }
                if (from is Vector2 end) DrawDestination(font, end, cr.Label, blue, map.Zoom >= LabelZoom);
            }

        // remaining route of each moving fleet: dashed line from the marker through the rest of its path; the selected own fleet's route is bold
        foreach (var (f, p, _) in placed)
        {
            if (f.Transit is not { } t || f.At != null) continue;
            bool sel = map.SelectedFleet == f.Id;
            var col = ColorOf(f.Side); col.A = sel ? 1f : 0.7f;
            var from = p;
            for (int i = t.HopIndex + 1; i < t.Path.Count; i++)
            {
                if (!_galaxy.TryFindSystem(t.Path[i], out int sys)) break;
                var to = map.SystemScreen(sys);
                DrawDashedLine(from, to, col, sel ? 3.5f : 2f, 7f);
                from = to;
            }
            if (f.Side == Player || sel)
            {
                int arrive = t.DueDay; for (int h2 = t.HopIndex + 1; h2 < t.HopTicks.Count; h2++) arrive += t.HopTicks[h2];
                DrawDestination(font, from, $"{f.Name} arrives day {arrive}", col, sel || map.Zoom >= LabelZoom);
            }
        }

        // orders given but not started yet (they begin when the next day does)
        if (Planned != null)
            foreach (var kv in Planned())
            {
                var hit = placed.FirstOrDefault(x => x.f.Id == kv.Key);
                if (hit.f == null) continue;
                var plan = kv.Value;
                var from = hit.pos;
                var white = new Color(1f, 1f, 1f, 0.9f);
                for (int i = 1; i < plan.Path.Count; i++)
                {
                    if (!_galaxy.TryFindSystem(plan.Path[i], out int sys)) break;
                    var to = map.SystemScreen(sys);
                    DrawDashedLine(from, to, white, 3f, 6f);
                    from = to;
                }
                DrawDestination(font, from, $"{hit.f.Name} arrives day {plan.ArrivalDay}", white, map.SelectedFleet == kv.Key || map.Zoom >= LabelZoom);
            }

        DrawMovers(font);

        var drawnStacks = new HashSet<string>(StringComparer.Ordinal); // membership only
        foreach (var (f, p, h) in placed.OrderBy(x => map.SelectedFleet == x.f.Id ? 0 : 1).ThenBy(x => x.f.Id))
        {
            var col = ColorOf(f.Side);
            bool selected = map.SelectedFleet == f.Id;
            int count = 1;
            if (Stacked && f.At != null)
            {
                if (!drawnStacks.Add(f.At)) continue; // one marker per stack: the selected fleet's, else the first
                count = _stack.TryGetValue((f.At, f.Side == Player || Player == null), out int n) ? n : 1;
            }
            float r = selected ? 10f : 8.5f;
            var perp = new Vector2(-h.Y, h.X);
            var tri = new[] { p + h * r, p - h * r * 0.7f + perp * r * 0.7f, p - h * r * 0.7f - perp * r * 0.7f };
            DrawColoredPolygon(tri, col);
            bool own = f.Side == Player;
            DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[0] }, selected ? Colors.White : own ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f), selected ? 2.5f : own ? 1.6f : 1.2f, true);

            if (count > 1) Badge(font, p, r, count, col);
            if (map.Zoom >= LabelZoom || selected)
            {
                var pos = p + new Vector2(r + 3f + (count > 1 ? 14f : 0f), -r * 0.6f);
                DrawStringOutline(font, pos, f.Name, HorizontalAlignment.Left, -1, 13, 4, new Color(0, 0, 0, 0.9f));
                DrawString(font, pos, f.Name, HorizontalAlignment.Left, -1, 13, col);
            }
        }

        // enemy fleets the player's side sees now: a pointed-down triangle with a dark outline, labelled with what was seen (no name)
        var drawnEnemy = new HashSet<string>(StringComparer.Ordinal); // membership only
        foreach (var (sys, pic, p) in layout.Enemy)
        {
            if (Stacked && !drawnEnemy.Add(sys)) continue;
            var col = ColorOf(pic.Side);
            const float r = 8.5f;
            if (Stacked && _stack.TryGetValue((sys, false), out int ne) && ne > 1) Badge(font, p, r, ne, col);
            var h = Vector2.Down; var perp = new Vector2(-h.Y, h.X);
            var tri = new[] { p + h * r, p - h * r * 0.7f + perp * r * 0.7f, p - h * r * 0.7f - perp * r * 0.7f };
            DrawColoredPolygon(tri, col);
            DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[0] }, new Color(0, 0, 0, 0.85f), 1.2f, true);
            if (map.Zoom >= LabelZoom)
            {
                string text = EnemyText(pic);
                var pos = p + new Vector2(r + 3f, -r * 0.6f);
                DrawStringOutline(font, pos, text, HorizontalAlignment.Left, -1, 13, 4, new Color(0, 0, 0, 0.9f));
                DrawString(font, pos, text, HorizontalAlignment.Left, -1, 13, col);
            }
        }
    }

    /// <summary>
    /// The player's own movers: a small glyph per kind (leader diamond, mission team diamond in a ring, regiment square, building hexagon, ship or
    /// fighter hollow triangle, HQ gold disc), moving like a fleet; the rest of the route as a faint dashed line; the label only when zoomed in.
    /// </summary>
    void DrawMovers(Font font)
    {
        var map = Map;
        bool labels = map.Zoom >= LabelZoom;
        foreach (var (m, p, h) in PlacedMovers())
        {
            var col = m.Kind == MoverKind.Hq ? new Color(1f, 0.95f, 0.5f) : ColorOf(Player ?? Side.Hand);
            var edge = new Color(1, 1, 1, 0.9f);
            // the route still ahead (a lone leader, with its hop times, and the HQ already have their own route line; a Move group does not)
            bool ownLine = m.Kind == MoverKind.Hq || (m.Kind == MoverKind.Leader && m.HopTicks != null);
            if (!m.Held && !ownLine && m.Path.Count > 1)
            {
                var faint = col; faint.A = 0.45f;
                var from = p;
                int next = m.HopTicks != null ? m.HopIndex + 1 : NextPoint(m, p);
                for (int i = next; i < m.Path.Count; i++)
                {
                    if (!_galaxy.TryFindSystem(m.Path[i], out int sys)) break;
                    var to = map.SystemScreen(sys);
                    DrawDashedLine(from, to, faint, 1.3f, 3f);
                    from = to;
                }
            }
            float r = MoverR;
            switch (m.Kind)
            {
                case MoverKind.Leader:
                case MoverKind.Mission:
                {
                    var d = new[] { p + new Vector2(0, -r), p + new Vector2(r, 0), p + new Vector2(0, r), p + new Vector2(-r, 0) };
                    DrawColoredPolygon(d, col);
                    DrawPolyline(new[] { d[0], d[1], d[2], d[3], d[0] }, edge, 1.2f, true);
                    if (m.Kind == MoverKind.Mission) DrawArc(p, r + 3f, 0, Mathf.Tau, 20, col, 1.3f, true);
                    break;
                }
                case MoverKind.Regiment:
                {
                    var q = new[] { p + new Vector2(-r * 0.8f, -r * 0.8f), p + new Vector2(r * 0.8f, -r * 0.8f), p + new Vector2(r * 0.8f, r * 0.8f), p + new Vector2(-r * 0.8f, r * 0.8f) };
                    DrawColoredPolygon(q, col);
                    DrawPolyline(new[] { q[0], q[1], q[2], q[3], q[0] }, edge, 1.2f, true);
                    break;
                }
                case MoverKind.Facility:
                {
                    var hex = new Vector2[7];
                    for (int i = 0; i < 6; i++) hex[i] = p + Vector2.FromAngle(Mathf.Pi / 6f + i * Mathf.Pi / 3f) * r;
                    hex[6] = hex[0];
                    DrawColoredPolygon(hex[..6], col);
                    DrawPolyline(hex, edge, 1.2f, true);
                    break;
                }
                case MoverKind.Hq:
                    DrawCircle(p, r + 0.5f, col);
                    DrawArc(p, r + 0.5f, 0, Mathf.Tau, 20, new Color(0, 0, 0, 0.85f), 1.2f, true);
                    break;
                default: // ships and fighters flying alone: a hollow triangle, smaller for fighters
                {
                    float rr = m.Kind == MoverKind.Fighter ? r * 0.85f : r * 1.1f;
                    var perp = new Vector2(-h.Y, h.X);
                    var tri = new[] { p + h * rr, p - h * rr * 0.7f + perp * rr * 0.7f, p - h * rr * 0.7f - perp * rr * 0.7f, p + h * rr };
                    DrawColoredPolygon(tri[..3], Ui.MapBg);
                    DrawPolyline(tri, col, 1.6f, true);
                    break;
                }
            }
            if (m.Count > 1) Badge(font, p, r, m.Count, col);
            if (labels)
            {
                var pos = p + new Vector2(r + 4f + (m.Count > 1 ? 12f : 0f), 4f);
                DrawStringOutline(font, pos, m.Label, HorizontalAlignment.Left, -1, 12, 4, new Color(0, 0, 0, 0.9f));
                DrawString(font, pos, m.Label, HorizontalAlignment.Left, -1, 12, col);
            }
        }
    }

    /// <summary>For a mover placed by length: the index of the first path system still ahead of it.</summary>
    int NextPoint(Mover m, Vector2 p)
    {
        var map = Map;
        float best = float.MaxValue; int at = 1;
        for (int i = 0; i + 1 < m.Path.Count; i++)
        {
            if (!_galaxy.TryFindSystem(m.Path[i], out int a) || !_galaxy.TryFindSystem(m.Path[i + 1], out int b)) break;
            var pa = map.SystemScreen(a); var pb = map.SystemScreen(b);
            float d = DistToSegment(p, pa, pb);
            if (d < best - 0.01f) { best = d; at = i + 1; }
        }
        return at;
    }

    static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float l2 = ab.LengthSquared();
        if (l2 < 0.0001f) return p.DistanceTo(a);
        float t = Mathf.Clamp((p - a).Dot(ab) / l2, 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>The number of fleets in a stacked marker, in a small dark disc at its top right.</summary>
    void Badge(Font font, Vector2 p, float r, int count, Color col)
    {
        var at = p + new Vector2(r * 0.95f, -r * 0.95f);
        DrawCircle(at, 7.5f, Ui.MapBg);
        DrawArc(at, 7.5f, 0, Mathf.Tau, 16, col, 1.4f, true);
        string t = count > 9 ? "9+" : count.ToString();
        var size = font.GetStringSize(t, HorizontalAlignment.Left, -1, 11);
        DrawString(font, at + new Vector2(-size.X / 2f, font.GetAscent(11) / 2f - 1f), t, HorizontalAlignment.Left, -1, 11, Colors.White);
    }
}

/// <summary>What a mover on the map is; picks its glyph.</summary>
public enum MoverKind { Leader, Mission, Regiment, Ship, Fighter, Facility, Hq }

/// <summary>
/// One of the player's own things travelling that is not a fleet, as the map draws it. Path is the whole trip (system names); HopTicks, HopIndex
/// and DueDay as in a fleet transit. HopTicks null = only the total is known (a mission team): it is placed by length along the path over the
/// days [DueDay - TotalTicks, DueDay]. Held = waiting at Path[HopIndex] (a delivery stopped by an enemy fleet or a lost destination).
/// </summary>
public sealed record Mover(MoverKind Kind, string Label, IReadOnlyList<string> Path, IReadOnlyList<int> HopTicks, int HopIndex, int DueDay,
    int TotalTicks = 0, int Count = 1, bool Held = false);
