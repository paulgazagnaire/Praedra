using System;
using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// A row of small drawn marks for the sector window (Paul, 2026-11-01: "small dots, no numbers"): one mark per thing counted, filled
/// for what is there or in use, hollow for what is free. Dots for energy and raw slots, squares for troops, diamonds for defences,
/// triangles for fleets. Drawn, not typed, so every mark is crisp and the same size whatever the font. Screens only.
/// </summary>
public partial class SectorMarks : Control
{
    public enum Shape { Dot, Square, Diamond, Triangle }

    public readonly record struct Mark(Shape Shape, Color Color, bool Filled);

    public readonly List<Mark> Marks = new();
    public float Side = 6f, Gap = 3f;
    public bool Centered = true;

    public SectorMarks() { MouseFilter = MouseFilterEnum.Ignore; }

    public SectorMarks Add(Shape shape, Color color, bool filled, int count = 1)
    {
        for (int i = 0; i < count; i++) Marks.Add(new Mark(shape, color, filled));
        UpdateMinimumSize();
        QueueRedraw();
        return this;
    }

    float RowWidth => Marks.Count == 0 ? 0 : Marks.Count * (Side + Gap) - Gap;

    public override Vector2 _GetMinimumSize() => new(RowWidth, Side + 2);

    public override void _Draw()
    {
        float x = Centered ? Math.Max(0, (Size.X - RowWidth) / 2) : 0, y = (Size.Y - Side) / 2;
        foreach (var m in Marks)
        {
            var r = new Rect2(x, y, Side, Side);
            var c = m.Filled ? m.Color : new Color(m.Color, 0.55f);
            switch (m.Shape)
            {
                case Shape.Dot:
                    if (m.Filled) DrawCircle(r.GetCenter(), Side / 2, c);
                    else DrawArc(r.GetCenter(), Side / 2 - 0.5f, 0, Mathf.Tau, 16, c, 1f, true);
                    break;
                case Shape.Square:
                    if (m.Filled) DrawRect(r, c);
                    else DrawRect(r.Grow(-0.5f), c, false, 1f);
                    break;
                case Shape.Diamond:
                {
                    var ctr = r.GetCenter(); float h = Side / 2 + 0.5f;
                    var pts = new[] { ctr + new Vector2(0, -h), ctr + new Vector2(h, 0), ctr + new Vector2(0, h), ctr + new Vector2(-h, 0) };
                    if (m.Filled) DrawColoredPolygon(pts, c);
                    else DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[0] }, c, 1f, true);
                    break;
                }
                case Shape.Triangle:
                {
                    var pts = new[] { new Vector2(r.Position.X + Side / 2, r.Position.Y), new Vector2(r.End.X, r.End.Y), new Vector2(r.Position.X, r.End.Y) };
                    if (m.Filled) DrawColoredPolygon(pts, c);
                    else DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[0] }, c, 1f, true);
                    break;
                }
            }
            x += Side + Gap;
        }
    }
}

/// <summary>
/// Loyalty as one thin line (Paul, 2026-11-01): your side's share from the left in your colour, the other side's share in theirs. The game
/// keeps one loyalty number per system, the Collective's share, the Hand having the rest (rule: m2-popular-support §State), so the line has
/// two parts. A gap of the window's colour marks the middle so a glance tells more or less than half.
/// </summary>
public partial class LoyaltyLine : Control
{
    float _you = -1;
    Color _youColor, _themColor;

    public LoyaltyLine() { MouseFilter = MouseFilterEnum.Ignore; CustomMinimumSize = new Vector2(0, 4); }

    /// <summary>Your side's share, 0..100; a negative share hides the line (not known).</summary>
    public void Set(int youShare, Color you, Color them) { _you = youShare; _youColor = you; _themColor = them; QueueRedraw(); }

    public override void _Draw()
    {
        if (_you < 0) return;
        float w = Size.X, h = Size.Y, cut = w * Math.Clamp(_you, 0, 100) / 100f;
        if (cut > 0) DrawRect(new Rect2(0, 0, cut, h), _youColor);
        if (cut < w) DrawRect(new Rect2(cut, 0, w - cut, h), new Color(_themColor, 0.85f));
        DrawRect(new Rect2(w / 2 - 0.5f, -1, 1, h + 2), new Color(0, 0, 0, 0.6f)); // the middle
    }
}
