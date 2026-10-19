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
    public static readonly Color EmpireColor = new(0.45f, 0.95f, 0.45f);
    public static readonly Color AllianceColor = new(1.0f, 0.62f, 0.25f);
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
    const float PickRadiusPx = 13f;
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

    public static Color ColorOf(Side s) => s == Side.Empire ? EmpireColor : AllianceColor;

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
            slots.TryGetValue(f.At, out int slot);
            slots[f.At] = slot + 1;
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
        slots.TryGetValue(system, out int slot);
        slots[system] = slot + 1;
        const int rows = 6;
        float colWidth = map.Zoom >= LabelZoom ? 130f : 14f;
        pos = map.SystemScreen(s) + new Vector2(-6f + colWidth * (slot / rows), -13f - 16f * (slot % rows));
        return true;
    }

    /// <summary>The systems where an enemy fleet marker is drawn now (for the playtest).</summary>
    public IReadOnlyList<string> EnemyMarkerSystems() => _game == null ? Array.Empty<string>() : Layout().Enemy.Select(e => e.System).ToList();

    /// <summary>The fleets drawn now from the core's fleet state (only own fleets with fog on), by id (for the playtest).</summary>
    public IReadOnlyList<int> DrawnFleetIds() => _game == null ? Array.Empty<int>() : Layout().Own.Select(o => o.f.Id).ToList();

    /// <summary>Screen rectangles of the fleet markers and their names, so the map keeps system names off them.</summary>
    public IEnumerable<Rect2> Obstacles()
    {
        if (_game == null) yield break;
        var map = Map;
        var font = ThemeDB.FallbackFont;
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
            if (d <= bestD) { bestD = d; best = f.Id; }
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
        var font = ThemeDB.FallbackFont;
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

        foreach (var (f, p, h) in placed)
        {
            var col = ColorOf(f.Side);
            bool selected = map.SelectedFleet == f.Id;
            float r = selected ? 10f : 8.5f;
            var perp = new Vector2(-h.Y, h.X);
            var tri = new[] { p + h * r, p - h * r * 0.7f + perp * r * 0.7f, p - h * r * 0.7f - perp * r * 0.7f };
            DrawColoredPolygon(tri, col);
            bool own = f.Side == Player;
            DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[0] }, selected ? Colors.White : own ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f), selected ? 2.5f : own ? 1.6f : 1.2f, true);

            if (map.Zoom >= LabelZoom || selected)
            {
                var pos = p + new Vector2(r + 3f, -r * 0.6f);
                DrawStringOutline(font, pos, f.Name, HorizontalAlignment.Left, -1, 13, 4, new Color(0, 0, 0, 0.9f));
                DrawString(font, pos, f.Name, HorizontalAlignment.Left, -1, 13, col);
            }
        }

        // enemy fleets the player's side sees now: a pointed-down triangle with a dark outline, labelled with what was seen (no name)
        foreach (var (sys, pic, p) in layout.Enemy)
        {
            var col = ColorOf(pic.Side);
            const float r = 8.5f;
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
}
