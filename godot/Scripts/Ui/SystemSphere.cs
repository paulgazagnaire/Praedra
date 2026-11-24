using System;
using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// A system on the map as a small pixel-art sphere (Paul 2026-11-24: "very subtle pixel-art spheres instead of the boring flat circles",
/// the same treatment as the map's pixel sprites). A disc of whole pixels in four tones of its colour, as the pixel icons use them: a dark
/// rim on the shaded side, the body, a lighter cap and a small shine, with the ramp's small hue shift, lit from the top left; then a
/// one-pixel dark outline so it stands off the galaxy. Drawn in code (icons are code only, Paul 2026-11-18), cached per colour and size.
/// Screens only.
/// </summary>
public static class SystemSphere
{
    // one art pixel = one layout pixel, held as four texels with hard edges: the pixels stay square at any UI size (as UiKit's tick box)
    const int K = 4;
    // the light: from the top left, a little in front
    static readonly Vector3 Light = new Vector3(-0.55f, -0.65f, 0.52f).Normalized();
    static readonly Color Outline = new(0.03f, 0.04f, 0.07f, 0.95f);
    // "very subtle": the colour stays the colour; the tones move it only a little, and lean its hue as the pixel icons' ramp does
    // (Paul's pick A1: shadows cooler, lights warmer; review 22): dark 8 degrees toward 245, light 6 and shine 10 toward 55
    const float DarkBy = 0.28f, LightBy = 0.14f, ShineBy = 0.40f;
    const float CoolHue = 245f, WarmHue = 55f, DarkLean = 8f, LightLean = 6f, ShineLean = 10f;
    const float ShineFrom = 0.95f, LightFrom = 0.66f, DarkBelow = 0.0f;

    static readonly Dictionary<(Color, int), ImageTexture> Cache = new();

    /// <summary>The sphere of this colour, <paramref name="across"/> pixels wide, plus its outline: (across + 2) layout px square. Pass only
    /// the fixed map colours: the cache keeps one texture per colour and size and never empties, so a faded or animated colour would
    /// fill it (Ponytail on #379).</summary>
    public static ImageTexture Of(Color c, int across)
    {
        int d = Math.Clamp(across, 3, 48);
        if (Cache.TryGetValue((c, d), out var hit)) return hit;
        var px = Grid(c, d);
        int w = d + 2;
        var img = Image.CreateEmpty(w * K, w * K, false, Image.Format.Rgba8);
        var none = new Color(0, 0, 0, 0);
        for (int y = 0; y < w * K; y++) for (int x = 0; x < w * K; x++) img.SetPixel(x, y, px[x / K, y / K] ?? none);
        var tex = ImageTexture.CreateFromImage(img);
        tex.SetSizeOverride(new Vector2I(w, w));
        Cache[(c, d)] = tex;
        return tex;
    }

    /// <summary>The sphere's pixels, (d + 2) square with the outline, [x, y]; null where it is clear.</summary>
    public static Color?[,] Grid(Color c, int d)
    {
        var dark = Lean(c.Darkened(DarkBy), DarkLean, CoolHue);
        var light = Lean(c.Lerp(Colors.White, LightBy), LightLean, WarmHue);
        var shine = Lean(c.Lerp(Colors.White, ShineBy), ShineLean, WarmHue);
        int w = d + 2;
        var px = new Color?[w, w];
        float r = d / 2f;
        for (int y = 0; y < d; y++) for (int x = 0; x < d; x++)
        {
            float dx = x + 0.5f - r, dy = y + 0.5f - r;
            if (dx * dx + dy * dy > r * r + 0.3f * r) continue;   // a round pixel disc, without one-pixel nubs on its sides
            float nx = dx / r, ny = dy / r, nz = MathF.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny));
            float lit = nx * Light.X + ny * Light.Y + nz * Light.Z;
            px[x + 1, y + 1] = lit > ShineFrom ? shine : lit > LightFrom ? light : lit > DarkBelow ? c : dark;
        }
        for (int y = 0; y < w; y++) for (int x = 0; x < w; x++)
        {
            if (px[x, y] != null) continue;
            bool edge = (x > 0 && px[x - 1, y] is { } a && a != Outline) || (x + 1 < w && px[x + 1, y] is { } b && b != Outline)
                || (y > 0 && px[x, y - 1] is { } e && e != Outline) || (y + 1 < w && px[x, y + 1] is { } f && f != Outline);
            if (edge) px[x, y] = Outline;
        }
        return px;
    }

    /// <summary>The colour with its hue turned up to <paramref name="by"/> degrees toward <paramref name="toward"/>, the short way round
    /// (a grey has no hue and stays as it is).</summary>
    static Color Lean(Color c, float by, float toward)
    {
        float h = c.H * 360f, d = (toward - h + 540f) % 360f - 180f;
        float turned = h + Math.Sign(d) * Math.Min(Math.Abs(d), by);
        return Color.FromHsv((turned % 360f + 360f) % 360f / 360f, c.S, c.V, c.A);
    }
}
