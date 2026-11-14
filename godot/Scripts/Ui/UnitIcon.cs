using System;
using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// A unit or building drawn as a small picture (Paul, 2026-11-03: the windows were "not very usable or visual"; Classic showed every unit
/// as a picture). Drawn in code from what the game already knows (the side, a ship's size class, a facility's kind), so every class has a
/// picture today; painted art can replace a kind later without touching the windows. Hand ships are wide slabs with a rounded bow and a
/// tower near the stern (the Edict concept); Collective ships are welded container blocks. Screens only.
/// </summary>
public partial class UnitIcon : Control
{
    public enum Kind { Ship, Fighter, Regiment, Agent, Mine, Refinery, Shipyard, Training, Construction, Shield, Battery, Sensor, Building }

    public Kind What = Kind.Ship;
    /// <summary>0 small, 1 medium, 2 large, 3 huge (ships); the picture's width follows it.</summary>
    public int Size4 = 1;
    public bool Hand = true;
    public Color Ink = Ui.You;

    public UnitIcon() { MouseFilter = MouseFilterEnum.Ignore; CustomMinimumSize = new Vector2(56, 34); }

    public static UnitIcon For(Kind what, bool hand, Color ink, int size4 = 1, Vector2? min = null)
    {
        var i = new UnitIcon { What = what, Hand = hand, Ink = ink, Size4 = Math.Clamp(size4, 0, 3) };
        if (min is Vector2 m) i.CustomMinimumSize = m;
        return i;
    }

    /// <summary>A facility's name (unit-and-facility-costs.csv) as a picture kind.</summary>
    public static Kind ForFacility(string name)
    {
        string n = name ?? "";
        if (n.Contains("Mine", StringComparison.Ordinal)) return Kind.Mine;
        if (n.Contains("Refinery", StringComparison.Ordinal)) return Kind.Refinery;
        if (n.Contains("Shipyard", StringComparison.Ordinal)) return Kind.Shipyard;
        if (n.Contains("Training", StringComparison.Ordinal)) return Kind.Training;
        if (n.Contains("Construction", StringComparison.Ordinal)) return Kind.Construction;
        if (n.Contains("Sensor", StringComparison.Ordinal)) return Kind.Sensor;
        if (n.StartsWith("Covenant", StringComparison.Ordinal)) return Kind.Shield;
        if (n.StartsWith("Escrow", StringComparison.Ordinal) || n.StartsWith("Lien", StringComparison.Ordinal)) return Kind.Battery;
        return Kind.Building;
    }

    /// <summary>A grey icon in a window (greyed out, or yours but not movable now): lines only, all in its one solid grey, no tinted fill
    /// or lighter highlight (Paul 2026-11-14: four greys, no fills). Meaning colours keep their fill and highlight.</summary>
    bool Flat => Ink == Ui.WinDim || Ink == Ui.WinText;
    Color Fill => Flat ? new Color(0, 0, 0, 0) : new(Ink, 0.22f);
    Color Edge => Flat ? Ink : new(Ink, 0.95f);
    Color Glow => Flat ? Ink : new(Ink.Lightened(0.35f), 0.9f);
    /// <summary>A detail line's colour: the ink at <paramref name="a"/>, or solid when greyed out.</summary>
    Color Faint(float a) => Flat ? Ink : new Color(Ink, a);

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        switch (What)
        {
            case Kind.Ship: if (Hand) HandShip(r); else CollectiveShip(r); break;
            case Kind.Fighter: Fighters(r); break;
            case Kind.Regiment: Squad(r); break;
            case Kind.Agent: Agent(r); break;
            default: Building(r); break;
        }
    }

    // ---- helpers

    void Poly(params Vector2[] pts)
    {
        if (!Flat) DrawColoredPolygon(pts, Fill);
        var loop = new Vector2[pts.Length + 1];
        Array.Copy(pts, loop, pts.Length); loop[^1] = pts[0];
        DrawPolyline(loop, Edge, 1.2f, true);
    }

    void Box(float x, float y, float w, float h) => Poly(new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h));

    static Vector2 P(Rect2 r, float fx, float fy) => r.Position + new Vector2(r.Size.X * fx, r.Size.Y * fy);

    // ---- ships

    float ShipWidth(Rect2 r) => r.Size.X * (0.5f + 0.16f * Size4);

    /// <summary>The Hand, seen from above: a wide slab with a rounded bow to the right, a trench along each side, a stepped (ziggurat)
    /// tower three quarters back seen as nested blocks, two turrets forward, engines glowing at the stern.</summary>
    void HandShip(Rect2 r)
    {
        float w = ShipWidth(r), h = r.Size.Y * (0.5f + 0.08f * Size4);
        float x0 = r.Position.X + (r.Size.X - w) / 2, cy = r.Position.Y + r.Size.Y / 2;
        float y0 = cy - h / 2, nose = h * 0.5f;
        var pts = new List<Vector2> { new(x0, y0 + h * 0.08f), new(x0 + w - nose, y0) };
        for (int i = 1; i < 10; i++) { float a = -Mathf.Pi / 2 + Mathf.Pi * i / 10; pts.Add(new Vector2(x0 + w - nose + Mathf.Cos(a) * nose, cy + Mathf.Sin(a) * h / 2)); }
        pts.Add(new(x0 + w - nose, y0 + h)); pts.Add(new(x0, y0 + h * 0.92f));
        Poly(pts.ToArray());
        // the side trenches
        var trench = Faint(0.55f);
        DrawLine(new Vector2(x0 + w * 0.06f, y0 + h * 0.24f), new Vector2(x0 + w - nose * 0.9f, y0 + h * 0.24f), trench, 1f);
        DrawLine(new Vector2(x0 + w * 0.06f, y0 + h * 0.76f), new Vector2(x0 + w - nose * 0.9f, y0 + h * 0.76f), trench, 1f);
        // the tower, three quarters back: nested blocks, the top one lit
        float tx = x0 + w * 0.14f, tw = w * 0.24f, th = h * 0.44f;
        Box(tx, cy - th / 2, tw, th);
        Box(tx + tw * 0.18f, cy - th * 0.32f, tw * 0.64f, th * 0.64f);
        DrawRect(new Rect2(tx + tw * 0.36f, cy - th * 0.14f, tw * 0.28f, th * 0.28f), Glow);
        // two turrets forward
        if (Size4 >= 1)
        {
            DrawCircle(new Vector2(x0 + w * 0.6f, y0 + h * 0.36f), Math.Max(1.6f, h * 0.07f), Edge);
            DrawCircle(new Vector2(x0 + w * 0.6f, y0 + h * 0.64f), Math.Max(1.6f, h * 0.07f), Edge);
        }
        // engines
        DrawRect(new Rect2(x0 - 3, cy - h * 0.3f, 3, h * 0.18f), Glow);
        DrawRect(new Rect2(x0 - 3, cy + h * 0.12f, 3, h * 0.18f), Glow);
    }

    /// <summary>The Collective, seen from above: container blocks of different lengths welded side by side, a cab forward, engines
    /// glowing at the back.</summary>
    void CollectiveShip(Rect2 r)
    {
        float w = ShipWidth(r), h = r.Size.Y * (0.48f + 0.08f * Size4);
        float x0 = r.Position.X + (r.Size.X - w) / 2, cy = r.Position.Y + r.Size.Y / 2, y0 = cy - h / 2;
        float body = w * 0.7f;
        Box(x0, y0 + h * 0.12f, body * 0.9f, h * 0.34f);              // the upper container
        Box(x0 + body * 0.12f, y0 + h * 0.54f, body * 0.88f, h * 0.34f); // the lower one, offset
        DrawLine(new Vector2(x0 + body * 0.3f, y0 + h * 0.46f), new Vector2(x0 + body * 0.3f, y0 + h * 0.54f), Edge, 1.4f); // the welds
        DrawLine(new Vector2(x0 + body * 0.7f, y0 + h * 0.46f), new Vector2(x0 + body * 0.7f, y0 + h * 0.54f), Edge, 1.4f);
        for (int i = 1; i < 4; i++)
        {
            float x = x0 + body * 0.9f * i / 4;
            DrawLine(new Vector2(x, y0 + h * 0.14f), new Vector2(x, y0 + h * 0.44f), Faint(0.4f), 1f);
        }
        // the cab, blunt and forward
        Poly(new(x0 + body, y0 + h * 0.22f), new(x0 + w * 0.92f, y0 + h * 0.3f), new(x0 + w, cy), new(x0 + w * 0.92f, y0 + h * 0.7f), new(x0 + body, y0 + h * 0.78f));
        DrawRect(new Rect2(x0 + w * 0.86f, cy - h * 0.08f, w * 0.05f, h * 0.16f), Glow);
        DrawRect(new Rect2(x0 - 3, y0 + h * 0.18f, 3, h * 0.2f), Glow);
        DrawRect(new Rect2(x0 + body * 0.12f - 3, y0 + h * 0.6f, 3, h * 0.2f), Glow);
    }

    // ---- fighters: a squadron as three craft in a vee

    void Fighters(Rect2 r)
    {
        float s = Math.Min(r.Size.X / 4.2f, r.Size.Y / 2.6f);
        var c = r.GetCenter();
        Craft(c + new Vector2(s * 0.9f, 0), s);
        Craft(c + new Vector2(-s * 0.5f, -s * 0.85f), s * 0.85f);
        Craft(c + new Vector2(-s * 0.5f, s * 0.85f), s * 0.85f);
    }

    void Craft(Vector2 c, float s)
    {
        if (Hand) Poly(c + new Vector2(s * 0.7f, 0), c + new Vector2(-s * 0.6f, -s * 0.55f), c + new Vector2(-s * 0.3f, 0), c + new Vector2(-s * 0.6f, s * 0.55f));
        else
        {
            Poly(c + new Vector2(s * 0.6f, -s * 0.12f), c + new Vector2(s * 0.6f, s * 0.12f), c + new Vector2(-s * 0.5f, s * 0.12f), c + new Vector2(-s * 0.5f, -s * 0.12f));
            DrawLine(c + new Vector2(-s * 0.4f, -s * 0.55f), c + new Vector2(s * 0.2f, s * 0.55f), Edge, 1.4f, true);
            DrawLine(c + new Vector2(-s * 0.4f, s * 0.55f), c + new Vector2(s * 0.2f, -s * 0.55f), Edge, 1.4f, true);
        }
    }

    // ---- people

    void Squad(Rect2 r)
    {
        float s = Math.Min(r.Size.X / 4.4f, r.Size.Y / 2.2f);
        var c = r.GetCenter() + new Vector2(0, s * 0.2f);
        Trooper(c + new Vector2(-s * 1.15f, s * 0.15f), s * 0.85f);
        Trooper(c + new Vector2(s * 1.15f, s * 0.15f), s * 0.85f);
        Trooper(c, s);
    }

    void Trooper(Vector2 c, float s)
    {
        // shoulders, then a helmet (the Hand's round and closed; the Collective's bare head with a scarf line)
        Poly(c + new Vector2(-s * 0.55f, s * 0.75f), c + new Vector2(-s * 0.5f, s * 0.2f), c + new Vector2(s * 0.5f, s * 0.2f), c + new Vector2(s * 0.55f, s * 0.75f));
        DrawCircle(c + new Vector2(0, -s * 0.2f), s * 0.33f, Fill);
        DrawArc(c + new Vector2(0, -s * 0.2f), s * 0.33f, 0, Mathf.Tau, 20, Edge, 1.2f, true);
        if (Hand) DrawLine(c + new Vector2(-s * 0.22f, -s * 0.18f), c + new Vector2(s * 0.22f, -s * 0.18f), Glow, 1.4f);
        else DrawLine(c + new Vector2(-s * 0.3f, s * 0.08f), c + new Vector2(s * 0.3f, s * 0.08f), Glow, 1.4f);
    }

    void Agent(Rect2 r)
    {
        float s = Math.Min(r.Size.X / 2.6f, r.Size.Y / 2.1f);
        var c = r.GetCenter();
        // a hooded figure with a blade across
        Poly(c + new Vector2(0, -s * 0.85f), c + new Vector2(s * 0.42f, -s * 0.35f), c + new Vector2(s * 0.55f, s * 0.8f), c + new Vector2(-s * 0.55f, s * 0.8f), c + new Vector2(-s * 0.42f, -s * 0.35f));
        DrawCircle(c + new Vector2(0, -s * 0.32f), s * 0.16f, new Color(0, 0, 0, 0.6f));
        DrawLine(c + new Vector2(-s * 0.75f, s * 0.6f), c + new Vector2(s * 0.75f, -s * 0.1f), Glow, 1.6f, true);
    }

    // ---- buildings: a ground line with a shape on it

    void Building(Rect2 r)
    {
        float gy = r.Position.Y + r.Size.Y * 0.86f, x0 = r.Position.X + r.Size.X * 0.14f, x1 = r.End.X - r.Size.X * 0.14f, w = x1 - x0, h = r.Size.Y * 0.7f;
        DrawLine(new Vector2(r.Position.X + 2, gy), new Vector2(r.End.X - 2, gy), Faint(0.5f), 1f);
        switch (What)
        {
            case Kind.Mine: // a derrick over a shaft
                Poly(new(x0 + w * 0.3f, gy), new(x0 + w * 0.5f, gy - h), new(x0 + w * 0.7f, gy));
                DrawLine(new Vector2(x0 + w * 0.38f, gy - h * 0.4f), new Vector2(x0 + w * 0.62f, gy - h * 0.4f), Edge, 1f);
                DrawRect(new Rect2(x0 + w * 0.45f, gy - 3, w * 0.1f, 3), Glow);
                break;
            case Kind.Refinery: // two tanks and a stack
                Box(x0, gy - h * 0.5f, w * 0.3f, h * 0.5f);
                Box(x0 + w * 0.34f, gy - h * 0.62f, w * 0.3f, h * 0.62f);
                Box(x0 + w * 0.72f, gy - h, w * 0.1f, h);
                DrawCircle(new Vector2(x0 + w * 0.77f, gy - h - 3), 2.2f, Glow);
                break;
            case Kind.Shipyard: // a gantry frame with a hull in it
                Box(x0, gy - h, w * 0.08f, h); Box(x1 - w * 0.08f, gy - h, w * 0.08f, h);
                DrawLine(new Vector2(x0, gy - h), new Vector2(x1, gy - h), Edge, 1.2f);
                Poly(new(x0 + w * 0.18f, gy - h * 0.55f), new(x0 + w * 0.72f, gy - h * 0.55f), new(x0 + w * 0.84f, gy - h * 0.4f), new(x0 + w * 0.72f, gy - h * 0.25f), new(x0 + w * 0.18f, gy - h * 0.25f));
                break;
            case Kind.Training: // barracks with a flag
                Box(x0 + w * 0.1f, gy - h * 0.45f, w * 0.65f, h * 0.45f);
                DrawLine(new Vector2(x0 + w * 0.85f, gy), new Vector2(x0 + w * 0.85f, gy - h), Edge, 1.2f);
                Poly(new(x0 + w * 0.85f, gy - h), new(x1 + w * 0.06f, gy - h * 0.86f), new(x0 + w * 0.85f, gy - h * 0.72f));
                break;
            case Kind.Construction: // a crane
                Box(x0 + w * 0.15f, gy - h * 0.35f, w * 0.4f, h * 0.35f);
                DrawLine(new Vector2(x0 + w * 0.65f, gy), new Vector2(x0 + w * 0.65f, gy - h), Edge, 1.4f);
                DrawLine(new Vector2(x0 + w * 0.25f, gy - h), new Vector2(x1, gy - h), Edge, 1.4f);
                DrawLine(new Vector2(x0 + w * 0.3f, gy - h), new Vector2(x0 + w * 0.3f, gy - h * 0.55f), Glow, 1f);
                break;
            case Kind.Shield: // a dome
                DrawArc(new Vector2(x0 + w / 2, gy), w * 0.42f, Mathf.Pi, Mathf.Tau, 24, Edge, 1.4f, true);
                DrawArc(new Vector2(x0 + w / 2, gy), w * 0.3f, Mathf.Pi, Mathf.Tau, 20, Faint(0.45f), 1f, true);
                break;
            case Kind.Battery: // a gun on a block
                Box(x0 + w * 0.15f, gy - h * 0.35f, w * 0.45f, h * 0.35f);
                Poly(new(x0 + w * 0.45f, gy - h * 0.4f), new(x1, gy - h * 0.95f), new(x1 + 2, gy - h * 0.82f), new(x0 + w * 0.55f, gy - h * 0.3f));
                break;
            case Kind.Sensor: // a dish
                DrawLine(new Vector2(x0 + w / 2, gy), new Vector2(x0 + w / 2, gy - h * 0.45f), Edge, 1.4f);
                DrawArc(new Vector2(x0 + w / 2, gy - h * 0.45f), w * 0.32f, Mathf.Pi * 1.1f, Mathf.Pi * 1.9f, 16, Edge, 1.6f, true);
                DrawCircle(new Vector2(x0 + w / 2, gy - h * 0.82f), 2f, Glow);
                break;
            default:
                Box(x0 + w * 0.2f, gy - h * 0.5f, w * 0.6f, h * 0.5f);
                break;
        }
    }
}
