using System;
using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// Draws the pixel icons of <see cref="PixelIconData"/> (Paul 2026-11-18: "Every icon must be code ONLY", no image files): each pixel is a
/// filled square, 1 px at 16 or 2 px at 32 (whole-pixel doubling only), coloured by tone from the meaning colour's ramp (tone 2 is the
/// UiKit colour itself). A greyed-out icon is flat: every tone in its one grey (Paul 2026-11-14, four greys). Screens only.
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
        ink == Ui.You ? Ramp("you") : ink == Ui.Enemy ? Ramp("enemy") : ink == Ui.WinActive ? Ramp("name")
        : ink == Ui.Warn ? Ramp("idle") : ink == Ui.Danger ? Ramp("danger") : ink == Ui.Good ? Ramp("good") : ink == Ui.Gold ? Ramp("capital")
        : null;

    /// <summary>A grey ramp around a plain colour (the top bar's light grey icons): tone 2 is the colour, 1 darker, 3 lighter, 4 near white.</summary>
    public static Color[] GreyRamp(Color mid) => new[] { mid, mid.Darkened(0.45f), mid, mid.Lightened(0.45f), mid.Lightened(0.8f) };

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
