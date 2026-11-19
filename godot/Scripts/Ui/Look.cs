using System;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The colours of Paul's Paper design for the Infrastructure window (2026-11-18, "CHARTER: INFRASTRUCTURE", Paper file "Praedra",
/// frame WYD-0). Paul 2026-11-19: every window takes them; the window tokens in <see cref="Ui"/> (WinActive, WinText, WinDim, WinLine,
/// WindowBox) point here. Screens only.
/// </summary>
public static class Look
{
    public static readonly Color Bg = new("141416");        // the window, a card, a yard not picked
    public static readonly Color Band = new("19191b");      // the yard picked, and the part under it
    public static readonly Color Line = new("1e1e21");      // the lines between cells, a button's outline, a card under the mouse
    public static readonly Color Edge = new("2d2e32");      // a building tile's outline
    public static readonly Color Text = new("bababa");      // words, numbers and icons
    public static readonly Color Soft = new("676767");      // a zero count, a note
    public static readonly Color Dim = new("454545");       // counts, "Idle", what can't be used
    public static readonly Color Accent = new("909ebd");    // the window buttons, a bar's fill, energy built
    public static readonly Color AccentDeep = new("4a5878"); // energy ordered, a bar's track
    public static readonly Color Empty = new("252525");     // a free slot
    public static readonly Color Raw = new("c8c8c8");       // raw slots with a mine
    public static readonly Color RawDeep = new("6a6a6a");   // raw slots with a mine ordered
    public static readonly Color Divider = new(0x61 / 255f, 0x61 / 255f, 0x61 / 255f, 0.4f); // between the two gauges

    /// <summary>A label in the design's face: Space Grotesk, Medium for names and headings, Regular for numbers and states.</summary>
    public static Label Words(string text, int size, Color color, bool medium = false)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontOverride("font", medium ? Ui.GroteskMedium : Ui.Grotesk);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    /// <summary>A flat box: a fill, and a line on the given sides only.</summary>
    public static StyleBoxFlat Box(Color bg, Color? line = null, int left = 0, int top = 0, int right = 0, int bottom = 0, int pad = 0)
    {
        var b = new StyleBoxFlat { BgColor = bg, BorderColor = line ?? bg, AntiAliasing = false };
        b.BorderWidthLeft = left; b.BorderWidthTop = top; b.BorderWidthRight = right; b.BorderWidthBottom = bottom;
        b.ContentMarginLeft = b.ContentMarginRight = b.ContentMarginTop = b.ContentMarginBottom = pad;
        return b;
    }
}

/// <summary>A pixel icon (<see cref="PixelIconData"/>) at its top left, at 1 or 2 px a pixel: four tones of its grey or meaning colour,
/// or flat in one colour when greyed out (Paul 2026-11-18: icons are code only).</summary>
public partial class IconPic : Control
{
    string[] _grid;
    int _scale = 1;
    Color _ink;
    bool _flat;

    public static IconPic Of(string[] grid, int scale, Color ink, bool flat = false)
    {
        var i = new IconPic { _grid = grid, _scale = Math.Clamp(scale, 1, 6), _ink = ink, _flat = flat, MouseFilter = MouseFilterEnum.Ignore };
        int w = grid is { Length: > 0 } ? grid[0].Length : 0, h = grid?.Length ?? 0;
        i.CustomMinimumSize = new Vector2(w * i._scale, h * i._scale);
        return i;
    }

    public override void _Draw()
    {
        if (_grid == null) return;
        var ramp = _flat ? null : PixelIcons.RampOf(_ink) ?? PixelIcons.GreyRamp(_ink);
        PixelIcons.Draw(this, _grid, Vector2.Zero, _scale, ramp, _ink);
    }
}

/// <summary>
/// A slot gauge from Paul's design: one 9 px square per slot, 1 px apart. Bright: a building stands there; mid: one is ordered or being
/// built; dark: free. The small ▾ above marks the last square built.
/// </summary>
public partial class SlotSquares : Control
{
    const int Square = 9, Step = 10, Top = 4;
    int _built, _used, _total;
    Color _bright, _mid;

    public static SlotSquares Of(int built, int used, int total, Color bright, Color mid)
    {
        var s = new SlotSquares { _built = Math.Max(0, built), _used = Math.Max(built, used), _total = Math.Max(0, total), _bright = bright, _mid = mid };
        s.MouseFilter = MouseFilterEnum.Ignore;
        s.CustomMinimumSize = new Vector2(Math.Max(1, s._total) * Step - 1, Top + Square);
        return s;
    }

    public override void _Draw()
    {
        for (int i = 0; i < _total; i++)
            DrawRect(new Rect2(i * Step, Top, Square, Square), i < _built ? _bright : i < _used ? _mid : Look.Empty);
        if (_built <= 0 || _built > _total) return;
        // the ▾: 5, 3 and 1 px rows, centred over the last square built
        int cx = (_built - 1) * Step + Square / 2;
        for (int row = 0; row < 3; row++)
            DrawRect(new Rect2(cx - 2 + row, row, 5 - 2 * row, 1), _bright);
    }
}

/// <summary>A button that shows other words in one of its labels while the mouse is over it (Paul 2026-11-19: a yard says "Building:",
/// and the name of what it builds on hover).</summary>
public partial class HoverWordsButton : Button
{
    public Label Target;
    public string Normal, Hover;

    public override void _Process(double delta)
    {
        if (Target == null || !IsInstanceValid(Target) || Hover == null) return;
        string want = IsHovered() ? Hover : Normal;
        if (Target.Text != want) Target.Text = want;
    }
}
