using System;
using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// Draws the pixel icons of <see cref="PixelIconData"/> (Paul 2026-11-18: "Every icon must be code ONLY", no image files): each pixel is a
/// filled square, 1 px at 16 or 2 px at 32 (whole-pixel doubling only), coloured by tone from the meaning colour's ramp, in the calm
/// colours since 2026-11-28 (<see cref="PixelIconData.Ramps"/>). A greyed-out icon is flat: every tone in its one grey (Paul 2026-11-14,
/// four greys). Screens only.
/// </summary>
public static class PixelIcons
{
    public const int Grid = 16;

    static readonly Dictionary<string, Color[]> _ramps = new(StringComparer.Ordinal);

    /// <summary>The four tone colours (index 1 to 4; 0 unused) of a ramp in <see cref="PixelIconData.Ramps"/>, or null.</summary>
    public static Color[] Ramp(string name)
    {
        if (_ramps.TryGetValue(name, out var r)) return r;
        if (!PixelIconData.Ramps.TryGetValue(name, out var hex)) return null;
        r = new Color[5];
        for (int i = 0; i < 4; i++) r[i + 1] = new Color(hex[i]);
        return _ramps[name] = r;
    }

    /// <summary>The ramp of a UiKit meaning colour, or null when the colour has none (then the icon is drawn flat in that colour).</summary>
    public static Color[] RampOf(Color ink) =>
        ink == Ui.You ? Ramp("you") : ink == Ui.Enemy ? Ramp("enemy") : ink == Look.Accent ? Ramp("name") : ink == Ui.WinActive ? WordsRamp
        : ink == Ui.Warn ? Ramp("idle") : ink == Ui.Danger ? Ramp("danger") : ink == Ui.Good ? Ramp("good") : ink == Ui.Gold ? Ramp("capital")
        : null;

    /// <summary>The windows' words colour as four tones, for an icon beside a name: the calm words ramp (2026-11-28).</summary>
    static Color[] WordsRamp => Ramp("words");

    /// <summary>A grey ramp around a plain colour (the top bar's icons): tone 2 is the colour, 1 darker, 3 lighter, 4 lighter still, each
    /// then calmed (<see cref="Calm"/>) as Paul's frames draw them since 2026-11-28, so no tone is near white.</summary>
    public static Color[] GreyRamp(Color mid) => new[] { mid, Calm(mid.Darkened(0.45f)), Calm(mid), Calm(mid.Lightened(0.45f)), Calm(mid.Lightened(0.8f)) };

    /// <summary>
    /// A colour in no ramp, calmed as the Paper frames were on 2026-11-28 (Paul: "Softer, as in the frame"; praedra/specs/ui/paper/
    /// frames-calm-icons-2026-11-28/job.py and overrides.json): a grey (saturation under 15) at L40 or more takes hue 225, saturation 31
    /// and the words grey's lightness ladder (48, 87, 93, 98 become 36, 72, 78, 84); a blue-grey (hue 212 to 238) above L70 is pressed
    /// into L70 to L84 in order, saturation 0.7 of its own (at most 45, at least 28); any other colour keeps its hue with 0.7 of its
    /// saturation, at most L84 (and S45 above L80). Dark lines and calm blue-greys stay as they are.
    /// </summary>
    public static Color Calm(Color c)
    {
        var (h, s, l) = Hsl(c);
        if (s < 15) return l < 40 ? c : FromHsl(225, 31, Ladder(l), c.A);
        if (h >= 212 && h <= 238)
            return l <= 70 ? c : FromHsl(h, Math.Clamp(s * 0.7f, 28, 45), 70 + (l - 70) * 14 / 30, c.A);
        float s2 = s * 0.7f;
        if (l > 80) s2 = Math.Min(s2, 45);
        return FromHsl(h, s2, Math.Min(l, 84), c.A);
    }

    static readonly (float From, float To)[] LadderSteps = { (0, 0), (48, 36), (87, 72), (93, 78), (98, 84), (100, 86) };

    static float Ladder(float l)
    {
        for (int i = 1; i < LadderSteps.Length; i++)
            if (l <= LadderSteps[i].From)
            {
                var (a, b) = LadderSteps[i - 1];
                var (c, d) = LadderSteps[i];
                return b + (l - a) * (d - b) / (c - a);
            }
        return 86;
    }

    /// <summary>Hue in degrees, saturation and lightness in percent.</summary>
    static (float H, float S, float L) Hsl(Color c)
    {
        float max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), l = (max + min) / 2, d = max - min;
        if (d < 1e-6f) return (0, 0, l * 100);
        float s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
        float h = max == c.R ? (c.G - c.B) / d + (c.G < c.B ? 6 : 0) : max == c.G ? (c.B - c.R) / d + 2 : (c.R - c.G) / d + 4;
        return (h * 60, s * 100, l * 100);
    }

    static Color FromHsl(float h, float s, float l, float a)
    {
        s /= 100; l /= 100;
        float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q, k = h / 360;
        float Ch(float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p;
        }
        return new Color(Ch(k + 1f / 3), Ch(k), Ch(k - 1f / 3), a);
    }

    /// <summary>The largest allowed pixel size (1 or 2) at which a <paramref name="w"/> x <paramref name="h"/> grid fits in
    /// <paramref name="box"/>; 0 if not even 1 fits.</summary>
    public static int ScaleFor(Vector2 box, int w = Grid, int h = Grid) =>
        box.X >= 2 * w && box.Y >= 2 * h ? 2 : box.X >= w && box.Y >= h ? 1 : 0;

    /// <summary>Draw <paramref name="grid"/> with its top left at <paramref name="at"/> (snapped to whole pixels). <paramref name="ramp"/>
    /// null = flat in <paramref name="flat"/>.</summary>
    public static void Draw(CanvasItem ci, string[] grid, Vector2 at, int scale, Color[] ramp, Color flat)
    {
        at = at.Floor();
        for (int y = 0; y < grid.Length; y++)
        {
            string row = grid[y];
            for (int x = 0; x < row.Length; x++)
            {
                int t = row[x] - '0';
                if (t < 1 || t > 4) continue;
                ci.DrawRect(new Rect2(at.X + x * scale, at.Y + y * scale, scale, scale), ramp?[t] ?? flat);
            }
        }
    }

    /// <summary>Draw <paramref name="grid"/> centred in <paramref name="box"/> at the largest scale that fits. False if it does not fit.</summary>
    public static bool DrawCentred(CanvasItem ci, string[] grid, Rect2 box, Color ink)
    {
        int w = grid.Length > 0 ? grid[0].Length : 0, h = grid.Length;   // any grid size (ships may be wider than 16)
        int s = ScaleFor(box.Size, w, h);
        if (s == 0) return false;
        var size = new Vector2(w * s, h * s);
        Draw(ci, grid, box.Position + (box.Size - size) / 2f, s, RampOf(ink), ink);
        return true;
    }

    /// <summary>
    /// A unit's picture: its class's own sprite (Paul 2026-11-23, "one picture per class": a ship has a ship picture, a regiment its
    /// soldier), else the kind's picture for its side and size. Sprites are drawn on a transparent background (Paul, a hard rule).
    /// </summary>
    public static string[] Unit(UnitIcon.Kind k, bool hand, int size4, string cls)
    {
        var set = k switch
        {
            UnitIcon.Kind.Ship => PixelIconData.Ships, UnitIcon.Kind.Fighter => PixelIconData.Craft,
            UnitIcon.Kind.Regiment => PixelIconData.Troops, UnitIcon.Kind.Agent => PixelIconData.Agents, _ => null,
        };
        return set != null && cls != null && set.TryGetValue(Slug(cls), out var g) ? g : Unit(k, hand, size4);
    }

    /// <summary>A class name as its sprite's name: "Decree II" is "decree-ii", "Nhal Lookouts" is "nhal-lookouts".</summary>
    public static string Slug(string cls) => cls.Trim().ToLowerInvariant().Replace(' ', '-');

    /// <summary>The roles a leader's picture shows: a fleet officer, a general, an official (Paul 2026-11-23: a hero gets a character).</summary>
    public enum LeaderRole { Admiral, General, Civil }

    /// <summary>A leader's picture by side and role (one picture per role, not a face per person: Paul's pick 2026-11-23).</summary>
    public static string[] Leader(bool hand, LeaderRole role) =>
        PixelIconData.Leaders.TryGetValue($"{(hand ? "hand" : "collective")}-{role.ToString().ToLowerInvariant()}", out var g) ? g : null;

    /// <summary>
    /// A leader's role for the picture: their rank says it first (character-ranks.csv: Admiral, Captain, Commodore and Commander command
    /// fleets; General commands troops; the others are officials), else what they can command: fleets, or fleets and troops both, is the
    /// fleet officer; troops only is the general; neither is the official, who commands nothing (UI review 15). Screens only.
    /// </summary>
    public static LeaderRole RoleOf(string rank, bool canAdmiral, bool canGeneral)
    {
        switch (rank ?? "")
        {
            case "Admiral": case "Captain": case "Commodore": case "Commander": return LeaderRole.Admiral;
            case "General": return LeaderRole.General;
            case "": break;
            default: return LeaderRole.Civil;
        }
        return canAdmiral ? LeaderRole.Admiral : canGeneral ? LeaderRole.General : LeaderRole.Civil;
    }

    /// <summary>The unit icon for a picture kind and side (ships by size class 0 small to 3 huge), or null.</summary>
    public static string[] Unit(UnitIcon.Kind k, bool hand, int size4)
    {
        string side = hand ? "hand" : "collective";
        string key = k switch
        {
            UnitIcon.Kind.Ship => $"ship-{side}-{(size4 switch { 0 => "small", 1 => "medium", 2 => "large", _ => "huge" })}",
            UnitIcon.Kind.Fighter => $"fighter-{side}", UnitIcon.Kind.Regiment => $"regiment-{side}", UnitIcon.Kind.Agent => $"agent-{side}",
            _ => null,
        };
        return key != null && PixelIconData.Units.TryGetValue(key, out var g) ? g : null;
    }

    /// <summary>The building icon for a picture kind, or null when none is drawn yet.</summary>
    public static string[] Building(UnitIcon.Kind k)
    {
        string key = k switch
        {
            UnitIcon.Kind.Mine => "mine", UnitIcon.Kind.Refinery => "refinery", UnitIcon.Kind.Shipyard => "shipyard",
            UnitIcon.Kind.Training => "training", UnitIcon.Kind.Construction => "construction", UnitIcon.Kind.Shield => "shield",
            UnitIcon.Kind.Battery => "battery", UnitIcon.Kind.Sensor => "sensor", UnitIcon.Kind.Building => "building", _ => null,
        };
        return key != null && PixelIconData.Buildings.TryGetValue(key, out var g) ? g : null;
    }
}
