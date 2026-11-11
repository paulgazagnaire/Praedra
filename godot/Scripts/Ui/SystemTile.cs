using System;
using Godot;
using Clock = Praedra.Core.Clock;

namespace PraedraViewer;

/// <summary>
/// One system in the sector window, drawn pixel for pixel from Paul's Paper frame "system-info" (2026-11-11, file "Praedra", page
/// "UIs---V2--human"; reference copy in the shared folder praedra/specs/ui/paper/system-info/): 96 x 133, the name in a pixel font on top, the planet (96 dots) in the
/// middle, two 15-step gauges on its left (resources outside, energy inside: bright = in use, dark = free, grey = slots the system
/// lacks, an arrow at the level), a loyalty bar on its right (yours from the bottom, the enemy's faded on top), and four 16-pixel icons
/// in the corners: buildings, fleets, troops, and the holder's emblem. Drawn, not built from controls, so every pixel lands where the
/// frame puts it. Screens only: the caller fills <see cref="Data"/> from the game.
/// </summary>
public partial class SystemTile : Control
{
    public const int W = 96, H = 133;
    public const int Steps = 15;      // a gauge holds 15 steps: the slot cap (the frame draws 15)

    /// <summary>What the tile shows. Counts beyond <see cref="Steps"/> are cut; the tooltip has the numbers.</summary>
    public sealed class Data
    {
        public string Name = "";
        public Color NameColor = Colors.White;
        public Texture2D Planet;
        public Color PlanetTint = Colors.White;
        public bool Ring;                       // trouble: an uprising, a harvester; a red circle round the planet
        public bool Gauges;                     // false: a world never looked at, or harvested: planet and name only
        public int RawSlots, Mines, EnergySlots, EnergyUsed;
        public int Loyalty = -1;                // your share, 0-100; -1 = not known
        public Color You, Enemy;              // the two sides' colours: yours fills the loyalty bar from the bottom, theirs (faded) on top
        public Color? Buildings, Fleets, Troops, Emblem;   // null: the icon is drawn faint (nothing there)
        public bool EmblemHand = true;          // the Hand's open hand, else the Collective's braid
        public Color Faint;
    }

    public Data D = new();

    // the frame's colours (praedra/specs/ui/paper/system-info/notes.md §Colours)
    public static readonly Color Ground = new("0b0b0d");
    public static readonly Color Panel = new(0, 0, 0, 0.6f);
    public static readonly Color Empty = new(0x55 / 255f, 0x55 / 255f, 0x55 / 255f, 0.6f);
    public static readonly Color BracketTop = new(0x55 / 255f, 0x55 / 255f, 0x55 / 255f, 0.7f);
    public static readonly Color BracketBottom = new("474747");
    public static readonly Color RawUsed = new("ffa942"), RawFree = new("874300");
    public static readonly Color EnergyUsed = new("a1b1ff"), EnergyFree = new("325697");
    // faction colours (Paul 2026-11-11: "Green for the Hand, blue for the Collective"): the frame's green for the Hand, a blue for the
    // Collective kept clear of the energy gauge's lilac blues
    public static readonly Color HandGreen = new("009126"), HandName = new("03c93b");
    public static readonly Color CollectiveBlue = new("1e8fe0"), CollectiveName = new("4fb3ff");
    public static Color Of(Clock.Side side) => side == Clock.Side.Hand ? HandGreen : CollectiveBlue;
    public static Color NameOf(Clock.Side side) => side == Clock.Side.Hand ? HandName : CollectiveName;

    static Font _font;
    /// <summary>BitPotion (Joeb Rogers, CC-BY 4.0; credit in godot/fonts/BitPotion-LICENSE.txt), 16 px, letter spacing 0.1 em as in the frame.</summary>
    public static Font NameFont(int spacing) => new FontVariation { BaseFont = _font ??= GD.Load<FontFile>("res://fonts/BitPotion.ttf"), SpacingGlyph = spacing };

    public SystemTile()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(W, H);
        TextureFilter = TextureFilterEnum.Nearest;
    }

    // ---- where the parts sit (x, y from the tile's top-left; the frame's own numbers)
    public static readonly Rect2 InfraIcon = new(10, 22, 16, 16), FleetIcon = new(73, 22, 16, 16), TroopIcon = new(10, 117, 16, 16), EmblemIcon = new(73, 117, 16, 16);
    public static readonly Rect2 RawGauge = new(7, 42, 10, 71), EnergyGauge = new(19, 42, 10, 71), LoyaltyBar = new(77, 42, 8, 72);
    public static readonly Rect2 PlanetBox = new(0, 30, 96, 96);
    /// <summary>The part of the tile that opens the Infrastructure window: its icon and the two gauges.</summary>
    public static Rect2 InfraHot => new Rect2(4, 20, 28, 95);

    public override void _Draw()
    {
        var d = D;
        // the planet first: everything else lies over it, as in the frame
        if (d.Planet != null)
        {
            var sz = d.Planet.GetSize();
            var at = PlanetBox.Position + (PlanetBox.Size - sz) / 2;
            DrawTextureRect(d.Planet, new Rect2(at.Round(), sz), false, d.PlanetTint);
        }
        if (d.Ring) DrawArc(PlanetBox.GetCenter(), 44, 0, Mathf.Tau, 64, UiKitDanger, 1f, false);
        DrawName(d);
        if (!d.Gauges) return;
        Gauge(RawGauge, d.RawSlots, d.Mines, RawUsed, RawFree, outer: true);
        Gauge(EnergyGauge, d.EnergySlots, d.EnergyUsed, EnergyUsed, EnergyFree, outer: false);
        Loyalty(d);
        Icon(InfraIcon, Infrastructure, d.Buildings ?? d.Faint);
        Icon(FleetIcon, Fleet, d.Fleets ?? d.Faint);
        Icon(TroopIcon, Military, d.Troops ?? d.Faint);
        if (d.Emblem is Color ec) Icon(EmblemIcon, d.EmblemHand ? Hand : Braid, ec);
    }

    static readonly Color UiKitDanger = new("ff6b6b");

    void DrawName(Data d)
    {
        string t = d.Name.ToUpperInvariant();
        // the frame's spacing (0.1 em = 2 px); tighter for a long name, then an ellipsis, so it never leaves the tile
        foreach (int sp in new[] { 2, 1, 0 })
        {
            var f = NameFont(sp);
            float w = f.GetStringSize(t, HorizontalAlignment.Left, -1, 16).X;
            if (w <= W - 4 || sp == 0)
            {
                while (w > W - 4 && t.Length > 2) { t = t[..^2] + "…"; w = f.GetStringSize(t, HorizontalAlignment.Left, -1, 16).X; }
                float x = Mathf.Round((W - w) / 2);
                DrawString(f, new Vector2(x, 14), t, HorizontalAlignment.Left, -1, 16, d.NameColor);
                return;
            }
        }
    }

    /// <summary>A 15-step gauge in its dark panel, filling from the bottom: <paramref name="used"/> bright, the rest of <paramref name="slots"/>
    /// dark, the steps above them grey. The arrow points at the top bright step (from outside for the outer gauge, from the planet side for
    /// the inner one). Brackets mark the panel's outer corners.</summary>
    void Gauge(Rect2 r, int slots, int used, Color on, Color free, bool outer)
    {
        slots = Math.Clamp(slots, 0, Steps); used = Math.Clamp(used, 0, slots);
        DrawRect(r, Panel);
        var o = r.Position;
        int colX = outer ? 5 : 0;
        for (int i = 0; i < Steps; i++)
        {
            int fromBottom = Steps - 1 - i;
            Color c = fromBottom < used ? on : fromBottom < slots ? free : Empty;
            DrawRect(new Rect2(o.X + colX, o.Y + 6 + i * 4, 5, 3), c);
        }
        // brackets: an L of three 2 x 2 squares at the top, the same upside down at the bottom, on the outer edge
        int bx = outer ? 0 : 6;
        Bracket(o + new Vector2(bx, 0), outer, top: true);
        Bracket(o + new Vector2(bx, 67), outer, top: false);
        if (used > 0)
        {
            int top = Steps - used;                       // index from the top of the highest bright step
            float ay = o.Y + 6 + top * 4 - 1;
            if (outer) Pixels(ArrowRight, new Vector2(o.X, ay), on);
            else Pixels(ArrowLeft, new Vector2(o.X + 7, ay), on);
        }
    }

    void Bracket(Vector2 at, bool openRight, bool top)
    {
        var c = top ? BracketTop : BracketBottom;
        // three squares: the row along the edge (two) and one on the outer side
        int y0 = top ? 0 : 2, y1 = top ? 2 : 0;
        DrawRect(new Rect2(at.X, at.Y + y0, 2, 2), c);
        DrawRect(new Rect2(at.X + 2, at.Y + y0, 2, 2), c);
        DrawRect(new Rect2(at.X + (openRight ? 0 : 2), at.Y + y1, 2, 2), c);
    }

    /// <summary>The loyalty bar: your share in green from the bottom, the enemy's faded on top, between two small brackets.</summary>
    void Loyalty(Data d)
    {
        var r = LoyaltyBar;
        DrawRect(r, Panel);
        DrawRect(new Rect2(r.Position.X + 3, r.Position.Y, 2, 4), BracketBottom);
        DrawRect(new Rect2(r.Position.X + 3, r.Position.Y + 68, 2, 4), BracketBottom);
        float x = r.Position.X + 3, y0 = r.Position.Y + 7, len = 58;
        if (d.Loyalty < 0) { DrawRect(new Rect2(x, y0, 2, len), Empty); return; }
        float you = Mathf.Round(len * Math.Clamp(d.Loyalty, 0, 100) / 100f);
        DrawRect(new Rect2(x, y0, 2, len - you), new Color(d.Enemy, 0.4f));
        DrawRect(new Rect2(x, y0 + len - you, 2, you), d.You);
    }

    void Icon(Rect2 r, string[] map, Color c) => Pixels(map, r.Position, c);

    void Pixels(string[] map, Vector2 at, Color c)
    {
        for (int y = 0; y < map.Length; y++)
            for (int x = 0; x < map[y].Length; x++)
                if (map[y][x] == '#') DrawRect(new Rect2(at.X + x, at.Y + y, 1, 1), c);
    }

    /// <summary>Draw one of the tile's icons anywhere (the legend uses it).</summary>
    public static void DrawIcon(CanvasItem ci, string[] map, Vector2 at, Color c)
    {
        for (int y = 0; y < map.Length; y++)
            for (int x = 0; x < map[y].Length; x++)
                if (map[y][x] == '#') ci.DrawRect(new Rect2(at.X + x, at.Y + y, 1, 1), c);
    }

    // ---- the frame's icons, pixel for pixel (praedra/specs/ui/paper/system-info/svgs/)
    public static readonly string[] ArrowRight = { "#..", "##.", "###", "##.", "#.." };
    public static readonly string[] ArrowLeft = { "..#", ".##", "###", ".##", "..#" };

    public static readonly string[] Infrastructure =
    {
        "................", "................", "................", "..........#..#..", ".....#....#..#..", "....##....#..#..",
        "....##...##.##..", "...###########..", "...###########..", "...##...#######.", ".####...#######.", ".##############.",
        ".##############.", "................", "................", "................",
    };
    public static readonly string[] Fleet =
    {
        "................", "................", "................", "...###..........", "...........#....", "..........###...",
        "..........###...", ".........#####..", ".##############.", "..#############.", "...############.", "...###########..",
        ".........###....", "................", "................", "................",
    };
    public static readonly string[] Military =
    {
        "................", "................", "................", ".....###........", "....#####....#..", "...###########..",
        "...###########..", "...######.......", "....#####.......", ".....###........", ".....###........", "..########......",
        ".##############.", "................", "................", "................",
    };
    public static readonly string[] Hand =
    {
        "................", "................", "........#.......", "......#.#.#.....", "......#.#.#.....", "....#.#.#.#.....",
        "....#.#.#.#.#...", "....#.#.#.#.#...", "....#######.#...", "....#####...#...", "....#########...", "....#########...",
        ".....#######....", "......#####.....", "................", "................",
    };
    /// <summary>The Collective's emblem (not in the frame yet: three braided strands, for its eight strands bound together).</summary>
    public static readonly string[] Braid =
    {
        "................", "................", "...#...#...#....", "...#...#...#....", "....#.#.#.#.....", ".....#...#......",
        "....#.#.#.#.....", "...#...#...#....", "...#...#...#....", "....#.#.#.#.....", ".....#...#......", "....#.#.#.#.....",
        "...#...#...#....", "...#...#...#....", "................", "................",
    };
}

/// <summary>A small drawn mark for the sector window's key: whatever <see cref="Paint"/> draws, nearest-filtered so pixels stay pixels.</summary>
public partial class PixelMark : Control
{
    public Action<CanvasItem> Paint;
    public PixelMark(Vector2 size, Action<CanvasItem> paint) { CustomMinimumSize = size; Paint = paint; MouseFilter = MouseFilterEnum.Ignore; TextureFilter = TextureFilterEnum.Nearest; }
    public PixelMark() { MouseFilter = MouseFilterEnum.Ignore; }
    public override void _Draw() => Paint?.Invoke(this);
}
