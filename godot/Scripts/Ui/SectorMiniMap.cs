using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>The sector drawn small in the head of the window: each system a dot where it lies, in its owner's colour (hollow when
/// never seen, a ring round a capital or HQ). Pointing at a card lights its dot; clicking a dot opens that system's window.</summary>
public partial class SectorMiniMap : Control
{
    public readonly record struct Dot(string Name, Vector2 At, Color Color, bool Capital, bool Seen);
    public List<Dot> Dots = new();
    public string Hot, Focus;
    public Action<string> Picked;
    string _under;

    public SectorMiniMap() { MouseFilter = MouseFilterEnum.Stop; TooltipText = " "; }

    Vector2 Place(Vector2 at)
    {
        var r = new Rect2(at, Vector2.Zero);
        foreach (var d in Dots) r = r.Expand(d.At);
        float sx = (Size.X - 24) / Math.Max(1, r.Size.X), sy = (Size.Y - 16) / Math.Max(1, r.Size.Y), k = Math.Min(sx, sy);
        var mid = r.Position + r.Size / 2;
        return Size / 2 + (at - mid) * k;
    }

    string NameAt(Vector2 p)
    {
        string best = null; float bd = 14 * 14;
        foreach (var d in Dots) { float dd = Place(d.At).DistanceSquaredTo(p); if (dd < bd) { bd = dd; best = d.Name; } }
        return best;
    }

    public override string _GetTooltip(Vector2 at) => NameAt(at) is string n ? n + " (click to open)" : "Where the systems lie in this sector";

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm) { var u = NameAt(mm.Position); if (u != _under) { _under = u; MouseDefaultCursorShape = u != null ? CursorShape.PointingHand : CursorShape.Arrow; QueueRedraw(); } }
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb && NameAt(mb.Position) is string n) { Picked?.Invoke(n); AcceptEvent(); }
    }

    public override void _Notification(int what) { if (what == NotificationMouseExit && _under != null) { _under = null; QueueRedraw(); } }

    public override void _Draw()
    {
        if (Dots.Count == 0) return;
        var pts = Dots.Select(d => Place(d.At)).ToList();
        // faint lines to the nearest neighbours, so the dots read as one group
        for (int i = 0; i < pts.Count; i++)
            foreach (int j in Enumerable.Range(0, pts.Count).Where(j => j != i).OrderBy(j => pts[i].DistanceSquaredTo(pts[j])).ThenBy(j => j).Take(2))
                if (i < j || !Enumerable.Range(0, pts.Count).Where(k => k != j).OrderBy(k => pts[j].DistanceSquaredTo(pts[k])).ThenBy(k => k).Take(2).Contains(i))
                    DrawLine(pts[i], pts[j], new Color(1, 1, 1, 0.08f), 1f, true);
        for (int i = 0; i < Dots.Count; i++)
        {
            var d = Dots[i]; var p = pts[i];
            bool lit = d.Name == Hot || d.Name == _under || d.Name == Focus;
            if (lit) DrawCircle(p, 9f, new Color(d.Color, 0.18f));
            if (d.Seen) DrawCircle(p, lit ? 5f : 4f, d.Color);
            else DrawArc(p, 4f, 0, Mathf.Tau, 16, d.Color, 1.2f, true);
            if (d.Capital) DrawArc(p, 7.5f, 0, Mathf.Tau, 24, Ui.Gold, 1.2f, true);
        }
    }
}
