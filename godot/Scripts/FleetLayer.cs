using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Rebellion.Core.Clock;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>
/// Draws fleets on top of the galaxy map, using the parent GalaxyView's transform. Read-only: it looks at the core's
/// fleet state (Fleet.At, Fleet.Transit) and never changes it. Positions are purely visual.
/// </summary>
public partial class FleetLayer : Control
{
    public static readonly Color EmpireColor = new(0.45f, 0.95f, 0.45f);
    public static readonly Color AllianceColor = new(1.0f, 0.62f, 0.25f);
    /// <summary>The player's side: its fleets get a white outline.</summary>
    public Side? Player { get; set; }
    /// <summary>Move orders given but not started yet (fleet id to plan); drawn as a route so the player sees what is pending.</summary>
    public Func<IEnumerable<KeyValuePair<int, GameSession.PlannedMove>>> Planned { get; set; }
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

    IEnumerable<(Fleet f, Vector2 pos, Vector2 heading)> Placed()
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal); // counts only, never iterated
        foreach (var f in _game.State.Fleets.Values)               // fleet id order
            if (Place(f, slots, out var p, out var h)) yield return (f, p, h);
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
        var placed = Placed().ToList();

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
    }
}
