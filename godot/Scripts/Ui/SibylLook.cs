using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// Sibyl's panel as Paul drew it in the frame "ui-update-nov-26" (Paper, 2026-11-26): blue-greys, Pixel Operator, the head in the colour of
/// the bar's Sibyl cell so the two read as one tab, and the Needs you cards in red and yellow. The other tabs have no frame yet: they take
/// the same font and the colours of <see cref="Map"/>, which swaps each grey of the windows' look for its blue-grey here.
/// </summary>
public static class SibylLook
{
    public static readonly Color Head = new("202a3b");       // the head, and the bar's Sibyl cell
    public static readonly Color Strip = new("161a22");      // between the tabs, and under the shut ones
    public static readonly Color TabShut = new("0c0f17");    // a tab not open
    public static readonly Color TabHover = new("111520");   // a tab not open, under the mouse (not in the frame: between the two)
    public static readonly Color Page = new("090b11");       // the open tab and the page under it
    public static readonly Color Light = new("b8c2e0");      // the name, the open tab's words, the switches' words
    public static readonly Color Grey = new("54638a");       // the shut tabs' words and counts
    public static readonly Color Hover = new("7893c5");      // words under the mouse (the bar's hover)
    public static readonly Color Count = new("ff674e");      // Needs you's count while something is urgent

    public static readonly Color Urgent = new("e47a69"), UrgentLine = new("4d2f2a"), UrgentBody = new("a3928f");
    public static readonly Color UrgentButton = new("05060a"), UrgentButtonLine = new("38302e"), UrgentButtonWords = UrgentBody;
    public static readonly Color Watch = new("e4da69"), WatchLine = new("4d4a2a"), WatchBody = new("c5c193");
    public static readonly Color WatchButton = new("0a0a05"), WatchButtonLine = new("6a5955"), WatchButtonWords = new("ecead2");
    // "Could do better" has no card in the frame: the page's own blue-greys, with the fleet draft's button (12KN-0)
    public static readonly Color ChanceLine = new("202a3b"), ChanceBody = new("7893c5");
    public static readonly Color Button = new("05060a"), ButtonLine = new("1b3057");

    // the tick box: a 1 px line with its corners cut, filled and ticked when on
    public static readonly Color TickLine = new("dfdfdf"), TickFill = new("7893c5"), TickShade = new("314d7d");

    /// <summary>The windows' greys (<see cref="Look"/>, <see cref="Ui"/>) and the blue-grey each becomes in Sibyl's panel.</summary>
    public static readonly Dictionary<Color, Color> Map = new()
    {
        [Look.Text] = Light,                 // #DFDFDF words
        [Look.Muted] = Hover,                // #7D8699 status words
        [Look.Soft] = Grey,                  // #676767 a zero count, a note
        [Look.Dim] = new("3a4562"),          // #454545 what can't be used
        [Look.Bg] = new("00040f"),           // #0B0B0D a picture's tile (the fleet draft's)
        [Look.Band] = Page,                  // #131313 the page
        [Look.Line] = Strip,                 // #1E1E21 lines
        [Look.Edge] = Head,                  // #2D2E32 a tile's outline
        [Look.Outline] = ButtonLine,         // #36363C a button's and a card's outline
        [Look.Accent] = Hover,               // #909EBD a bar's fill
        [Look.AccentDeep] = TickShade,       // #4A5878 a bar's track
        [Look.Empty] = Strip,                // #252525 a free slot
        [Look.CellHover] = TabHover,         // #0F0F11
        [Look.Frame] = Strip,                // #181B21
        [Ui.Danger] = Urgent,                // the panel's red and yellow
        [Ui.Warn] = Watch,
    };

    /// <summary>Its blue-grey for a grey of the windows' look (the alpha kept), or the colour itself.</summary>
    public static Color Swap(Color c)
    {
        var solid = new Color(c.R, c.G, c.B);
        foreach (var (from, to) in Map)
            if (Close(from, solid)) return new Color(to, c.A);
        return c;
    }

    static bool Close(Color a, Color b) => Mathf.Abs(a.R - b.R) < 0.002f && Mathf.Abs(a.G - b.G) < 0.002f && Mathf.Abs(a.B - b.B) < 0.002f;

    static ImageTexture _tickOn, _tickOff, _tickOffHover;

    /// <summary>The tick box of the frame, 16 px: ticked, a #DFDFDF line round a #7893C5 square with a #DFDFDF tick shaded #314D7D.</summary>
    public static ImageTexture TickOn => _tickOn ??= Tick(true, TickLine);
    /// <summary>Unticked: the line alone, in #7893C5.</summary>
    public static ImageTexture TickOff => _tickOff ??= Tick(false, TickFill);
    /// <summary>Unticked under the mouse: the line in the light words' colour (not in the frame).</summary>
    public static ImageTexture TickOffHover => _tickOffHover ??= Tick(false, Light);

    /// <summary>
    /// The frame's tick box pixel by pixel: a line round 16 x 16 with its four corners cut; ticked, a 12 px square inside with a free pixel
    /// all round, a tick of 2 x 2 steps from (3, 5) down to (7, 9) and up to (13, 3), a 1 px shade under each step, and a shade pixel in
    /// each inner corner. Drawn at four texels a pixel so its pixels stay square at any interface size.
    /// </summary>
    static ImageTexture Tick(bool on, Color line)
    {
        const int n = 16, k = 4;
        var px = new Color[n, n];
        for (int i = 1; i <= 14; i++) { px[i, 0] = px[i, 15] = px[0, i] = px[15, i] = line; }
        if (on)
        {
            for (int y = 2; y <= 13; y++) for (int x = 2; x <= 13; x++) px[x, y] = TickFill;
            foreach (var (x, y) in new[] { (1, 1), (14, 1), (1, 14), (14, 14) }) px[x, y] = TickShade;
            foreach (var (x, y) in new[] { (3, 5), (5, 7), (7, 9), (9, 7), (11, 5), (13, 3) })
            {
                px[x, y] = px[x + 1, y] = px[x, y + 1] = px[x + 1, y + 1] = TickLine;
                px[x, y + 2] = px[x + 1, y + 2] = TickShade;
            }
        }
        var img = Image.CreateEmpty(n * k, n * k, false, Image.Format.Rgba8);
        for (int y = 0; y < n * k; y++) for (int x = 0; x < n * k; x++) img.SetPixel(x, y, px[x / k, y / k]);
        var tex = ImageTexture.CreateFromImage(img);
        tex.SetSizeOverride(new Vector2I(n, n));
        return tex;
    }
}
