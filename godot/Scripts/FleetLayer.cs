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
            // beside the dot, above it, stacked to the right
            pos = map.SystemScreen(s) + new Vector2(-6f + 13f * slot, -13f);
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

    public override void _Draw()
    {
        if (_game == null) return;
        var map = Map;
        var font = ThemeDB.FallbackFont;
        var placed = Placed().ToList();

        // remaining route of each moving fleet: faint dashed line from the marker through the rest of its path
        foreach (var (f, p, _) in placed)
        {
            if (f.Transit is not { } t || f.At != null) continue;
            var col = ColorOf(f.Side); col.A = 0.7f;
            var from = p;
            for (int i = t.HopIndex + 1; i < t.Path.Count; i++)
            {
                if (!_galaxy.TryFindSystem(t.Path[i], out int sys)) break;
                var to = map.SystemScreen(sys);
                DrawDashedLine(from, to, col, 2f, 7f);
                from = to;
            }
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
