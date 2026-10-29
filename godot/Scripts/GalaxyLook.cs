using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The galaxy backdrop's settings as the Galaxy Maker copies them ("Settings for the game", format praedra-galaxy, v 1), read from
/// res://ui/galaxy-backdrop.json (Paul 2026-10-29: "I want the exact same thing I designed in the galaxy maker"). Read the way the
/// Galaxy Maker reads pasted settings: unknown keys are dropped, numbers are clamped to its slider ranges, a missing key keeps the
/// Galaxy Maker's default. Drawing only.
/// </summary>
public sealed class GalaxyLook
{
    public const string Path = "res://ui/galaxy-backdrop.json";

    // key, lowest, highest, default: the Galaxy Maker's controls (tools/galaxy-maker/galaxy-maker.src.html, DEF and CTL)
    static readonly (string Key, float Min, float Max, float Def)[] Numbers =
    {
        ("phase", 0, 360, 67), ("pitch", 8, 35, 17), ("sharp", 1, 8, 3), ("wobble", 0, 1.2f, 0.35f), ("rim", 0.8f, 1.25f, 1),
        ("bright", 0.2f, 2.5f, 1), ("dim", 0.1f, 0.9f, 0.32f), ("bulge", 0, 2.5f, 1), ("bulgeSize", 0.4f, 2.5f, 1), ("disc", 0, 2.5f, 1),
        ("arms", 0, 2.5f, 1), ("follow", 0, 0.9f, 0.3f), ("clouds", 0, 1.5f, 1), ("knots", 0, 4, 1), ("lanes", 0, 1.5f, 0.85f),
        ("web", 0, 1.5f, 0.55f), ("laneOff", -1.2f, 1.2f, 0.42f), ("stars", 0, 3, 1), ("starDens", 0.2f, 3, 1), ("field", 0, 1.5f, 0.45f),
        ("drift", 0, 2, 0.3f), ("breathe", 0, 1, 0.3f), ("twinkle", 0, 1.5f, 0.4f), ("twShare", 0, 0.6f, 0.12f), ("twSpeed", 0.1f, 3, 0.6f),
    };
    static readonly (string Key, string Def)[] Colours =
        { ("cBulge", "#ffd69e"), ("cDisc", "#f5e6d1"), ("cArm", "#99b8ff"), ("cKnot", "#ff7594"), ("cDust", "#4d3d38"), ("cBg", "#111214") };
    static readonly string[] Patterns = { "fine", "coarse", "halftone", "grain" };
    static readonly Dictionary<string, string[]> Palettes = new()
    {
        ["own"] = Array.Empty<string>(),
        ["mono"] = new[] { "#040406", "#f1ede2" },
        ["cold"] = new[] { "#04060c", "#1b3350", "#6e9bc0", "#e6eff6" },
        ["warm"] = new[] { "#0b0604", "#5a2913", "#cf7339", "#f6e1bf" },
    };

    readonly Dictionary<string, float> _n = new();
    readonly Dictionary<string, Vector3> _c = new();
    public bool DitherOn { get; private set; }
    public int Pattern { get; private set; } = 3;
    public string[] Palette { get; private set; } = Palettes["mono"];
    public int Dot { get; private set; } = 2;
    public float Contrast { get; private set; } = 1.15f;
    public float Brightness { get; private set; }
    public int Levels { get; private set; } = 4;

    public float this[string key] => _n[key];
    public Vector3 Colour(string key) => _c[key];
    /// <summary>Any of the movement is on, so the backdrop is redrawn over time and not only when the view moves.</summary>
    public bool Moves => _n["drift"] > 0 || _n["breathe"] > 0 || (_n["twinkle"] > 0 && _n["twShare"] > 0);

    GalaxyLook()
    {
        foreach (var (k, _, _, d) in Numbers) _n[k] = d;
        foreach (var (k, d) in Colours) _c[k] = Rgb(d);
    }

    /// <summary>The settings in the file; the Galaxy Maker's defaults if it is missing or not galaxy settings.</summary>
    public static GalaxyLook Load()
    {
        var look = new GalaxyLook();
        using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning($"{Path}: {FileAccess.GetOpenError()}; the galaxy uses its defaults"); return look; }
        try { look.Read(JsonDocument.Parse(f.GetAsText()).RootElement); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { GD.PushWarning($"{Path}: {e.Message}; the galaxy uses its defaults"); }
        return look;
    }

    void Read(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var fmt) || fmt.GetString() != "praedra-galaxy") throw new InvalidOperationException("not galaxy settings");
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var (k, min, max, _) in Numbers)
                if (p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number) _n[k] = Math.Clamp(v.GetSingle(), min, max);
            foreach (var (k, _) in Colours)
                if (p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && IsHex(v.GetString()!)) _c[k] = Rgb(v.GetString()!);
        }
        if (root.TryGetProperty("dither", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            if (d.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False) DitherOn = on.GetBoolean();
            if (d.TryGetProperty("pat", out var pat) && pat.ValueKind == JsonValueKind.String && Array.IndexOf(Patterns, pat.GetString()) is >= 0 and var i) Pattern = i;
            if (d.TryGetProperty("pal", out var pal) && pal.ValueKind == JsonValueKind.String && Palettes.TryGetValue(pal.GetString()!, out var cols)) Palette = cols;
            float Num(string k, float min, float max, float cur) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetSingle(), min, max) : cur;
            Dot = (int)Num("dot", 1, 6, Dot);
            Contrast = Num("contrast", 0.5f, 2.5f, Contrast);
            Brightness = Num("bright", -0.4f, 0.4f, Brightness);
            Levels = (int)Num("levels", 2, 6, Levels);
        }
    }

    static bool IsHex(string s) => s.Length == 7 && s[0] == '#' && Uri.IsHexDigit(s[1]) && Uri.IsHexDigit(s[2]) && Uri.IsHexDigit(s[3])
        && Uri.IsHexDigit(s[4]) && Uri.IsHexDigit(s[5]) && Uri.IsHexDigit(s[6]);

    /// <summary>A #rrggbb colour as the Galaxy Maker hands it to its shader: each channel / 255, no colour-space change.</summary>
    public static Vector3 Rgb(string hex) => new(Convert.ToInt32(hex[1..3], 16) / 255f, Convert.ToInt32(hex[3..5], 16) / 255f, Convert.ToInt32(hex[5..7], 16) / 255f);

    /// <summary>
    /// The Grain dither's 64 by 64 blue noise ranks, by void and cluster (Ulichney 1993), built exactly as the Galaxy Maker builds it
    /// (makeBlueNoise: the same seed and the same JavaScript arithmetic, so the same dots).
    /// </summary>
    public static byte[] BlueNoise(int size)
    {
        int n = size * size, ones = 0;
        const int R = 6;
        const double Sig2 = 2 * 1.9 * 1.9;
        var ker = new List<(int Dx, int Dy, double W)>();
        for (int dy = -R; dy <= R; dy++) for (int dx = -R; dx <= R; dx++) ker.Add((dx, dy, Math.Exp(-(dx * dx + dy * dy) / Sig2)));
        var on = new byte[n];
        var e = new float[n];
        var rank = new int[n];
        void Add(int i, int sgn) { int x = i % size, y = i / size; foreach (var (dx, dy, w) in ker) { int j = (y + dy + size) % size * size + (x + dx + size) % size; e[j] = (float)(e[j] + sgn * w); } }
        double seed = 12345;
        double Rnd() { seed = (seed * 1103515245.0 + 12345.0) % 4294967296.0; return seed / 4294967296.0; } // JavaScript: (seed * a + c) >>> 0
        while (ones < n / 10.0) { int i = (int)(Rnd() * n); if (on[i] == 0) { on[i] = 1; Add(i, 1); ones++; } }
        int Pick(int want, bool best)
        {
            int bi = -1; double bv = best ? -1e9 : 1e9;
            for (int i = 0; i < n; i++) if (on[i] == want && (best ? e[i] > bv : e[i] < bv)) { bv = e[i]; bi = i; }
            return bi;
        }
        for (int guard = 0; guard < n; guard++)
        {
            int c = Pick(1, true); on[c] = 0; Add(c, -1);
            int v = Pick(0, false);
            if (v == c) { on[c] = 1; Add(c, 1); break; }
            on[v] = 1; Add(v, 1);
        }
        var on0 = (byte[])on.Clone(); var e0 = (float[])e.Clone();
        for (int r = ones - 1; r >= 0; r--) { int c = Pick(1, true); on[c] = 0; Add(c, -1); rank[c] = r; }
        on0.CopyTo(on, 0); e0.CopyTo(e, 0);
        for (int r = ones; r < n; r++) { int v = Pick(0, false); on[v] = 1; Add(v, 1); rank[v] = r; }
        var outp = new byte[n];
        for (int i = 0; i < n; i++) outp[i] = (byte)(rank[i] * 256 / n);
        return outp;
    }
}
