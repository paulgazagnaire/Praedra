using System;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The colours of Paul's Paper design for the windows. First his Infrastructure window (2026-11-18, frame WYD-0), then the "new colors"
/// frames (2026-11-24, greys), and now the frames as updated 2026-11-28 (fleet-new-frame-1..4-new-colors, ground-new-frame-1/2-new-colors,
/// infrastructure-new-frame-1/2-new-colors; export praedra/specs/ui/paper/game-restyle-2026-11-28/): the blue-greys of Sibyl's panel
/// (ui-update-nov-26) and Pixel Operator in every window. The head and the shut tabs #10141E, the page and the open tab #090B11, every
/// line and outline #182139, the words #B8C2E0, the window buttons and quiet words #54638A. The window tokens in <see cref="Ui"/>
/// (WinActive, WinText, WinDim, WinLine, WindowBox) point here. Screens only.
/// </summary>
public static class Look
{
    public static readonly Color Bg = new("10141e");        // the head, a tab or yard not picked, a window or a dialog
    public static readonly Color Band = new("090b11");      // the tab or yard picked, and the page under it
    public static readonly Color Line = new("182139");      // the lines between cells, a card under the mouse
    public static readonly Color Edge = new("182139");      // a building tile's outline, a dashed drop place
    public static readonly Color Text = new("b8c2e0");      // words, numbers, the open tab's name (11:1 on the page)
    public static readonly Color Soft = new("54638a");      // the window buttons, a shut tab's name and count, a zero count, a note (3.3:1)
    public static readonly Color Dim = new("3a4562");       // what can't be used (Sibyl's panel's dim)
    public static readonly Color Accent = new("909ebd");    // a bar's fill, energy built
    public static readonly Color AccentDeep = new("4a5878"); // energy ordered, a bar's track
    public static readonly Color Empty = new("182139");     // a free slot
    public static readonly Color Raw = new("c8c8c8");       // raw slots with a mine
    public static readonly Color RawDeep = new("6a6a6a");   // raw slots with a mine ordered
    public static readonly Color Divider = Line;            // between the two gauges; a bar's track
    public static readonly Color Outline = Line;            // a button's and a card's outline
    public static readonly Color ButtonFill = new("05060a"); // inside the Defensive / Elusive switch and a filled post
    // the status words: what a fleet is doing, a gauge's count, a planet's loyalty %. The frames draw them #54638A (3.3:1 on the page);
    // Paul 2026-11-25 had chosen "B", lift the status grey (then #525252) to a readable one: here Sibyl's hover blue-grey (5.8:1)
    public static readonly Color Muted = new("7893c5");
    public static readonly Color Quiet = Muted;
    // the dark tile behind a picture (Paul 2026-11-24, "I need the dark tile behind pictures everywhere for now"), as the frames draw it
    public static readonly Color PicTile = new("0c0f17");
    public static readonly Color CellHover = new("0d1019"); // a strip cell not open, under the mouse: between Bg and Band
    public static readonly Color Frame = Line;              // the window's 1 px edge
    public static readonly Color OverEdge = Soft;           // a window's edge where it lies over another (review 32: the usual one vanishes)

    /// <summary>A label in the windows' face: Pixel Operator on its 20 px line at 16 px (12, 13, 14 and 16 all become 16), 24 or 32.
    /// <paramref name="medium"/> is kept for the callers: the frames set every word in the one weight.</summary>
    public static Label Words(string text, int size, Color color, bool medium = false)
    {
        int s = Ui.WinSize(size);
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontOverride("font", s < 20 ? Ui.WinFace : Ui.Operator);
        l.AddThemeFontSizeOverride("font_size", s);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    /// <summary>How wide <paramref name="text"/> is in <see cref="Words"/> at <paramref name="size"/>.</summary>
    public static float Width(string text, int size)
    {
        int s = Ui.WinSize(size);
        return (s < 20 ? Ui.WinFace : Ui.Operator).GetStringSize(text, HorizontalAlignment.Left, -1, s).X;
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
