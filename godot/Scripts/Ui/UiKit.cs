using System;
using System.Collections.Generic;
using Godot;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The look of every screen in one place: the colour tokens, the two typefaces, the Theme every control reads, and small factories
/// (buttons, labels, cards, stat rows, bars) so the screens build the same parts the same way. Screens only; no rule lives here.
/// Colours: blue is always you and orange the enemy, whichever side you play (Paul's layout canvas, 2026-10-26); blue/orange stays
/// readable for colour-blind players where green/orange does not, and every state that matters also has a shape or a word.
/// </summary>
public static class Ui
{
    // ---------------------------------------------------------------- colour tokens
    public static readonly Color MapBg = new("0a0d13");
    public static readonly Color Chrome = new("121721");
    public static readonly Color Panel = new("171d29");
    public static readonly Color Panel2 = new("1e2534");
    public static readonly Color Hover = new("252e40");
    public static readonly Color Line = new("2a3344");
    public static readonly Color LineHi = new("3d4a60");
    public static readonly Color Text = new("e8ecf2");
    public static readonly Color Text2 = new("b4bdca");
    public static readonly Color Text3 = new("8592a3");
    public static readonly Color You = new("4ea3f2");
    public static readonly Color YouDeep = new("1d3d5e");
    public static readonly Color Enemy = new("f0904a");
    public static readonly Color EnemyDeep = new("4a2c18");
    public static readonly Color Neutral = new("9aa3af");
    public static readonly Color Danger = new("ff6b6b");
    public static readonly Color DangerDeep = new("3b1a1e");
    public static readonly Color Warn = new("f2c14e");
    public static readonly Color WarnDeep = new("3a3016");
    public static readonly Color Good = new("5ccb8a");
    public static readonly Color GoodDeep = new("173326");
    public static readonly Color Gold = new("f5d76e");
    public static readonly Color Mission = new("b58cff");
    public static readonly Color LeaderRoute = new("7fd3f7");

    /// <summary>The colour of a holder as the player sees it: blue = you, orange = the enemy, grey = nobody.</summary>
    public static Color OwnerColor(Side? owner, Side player) => owner == null ? Neutral : owner == player ? You : Enemy;

    // ---------------------------------------------------------------- type
    static FontFile _regular, _medium, _semibold, _display, _displayMedium;
    public static Font Regular => _regular ??= LoadFont("res://fonts/Inter-Regular.otf");
    public static Font Medium => _medium ??= LoadFont("res://fonts/Inter-Medium.otf");
    public static Font SemiBold => _semibold ??= LoadFont("res://fonts/Inter-SemiBold.otf");
    /// <summary>Barlow Semi Condensed SemiBold: titles, the day, section heads. Condensed so dense panels stay short.</summary>
    public static Font Display => _display ??= LoadFont("res://fonts/BarlowSemiCondensed-SemiBold.ttf");
    public static Font DisplayMedium => _displayMedium ??= LoadFont("res://fonts/BarlowSemiCondensed-Medium.ttf");

    static FontFile LoadFont(string path)
    {
        var f = GD.Load<FontFile>(path);
        if (f == null) { GD.PrintErr("Missing font " + path); return null; }
        return f;
    }

    /// <summary>Tabular figures, so columns of numbers line up and a ticking number does not wobble.</summary>
    static Font Tabular(Font f)
    {
        if (f == null) return ThemeDB.FallbackFont;
        var v = new FontVariation { BaseFont = f };
        v.OpentypeFeatures = new Godot.Collections.Dictionary { { TextServerManager.GetPrimaryInterface().NameToTag("tnum"), 1 } };
        return v;
    }

    static Font _num, _numBold, _caps;
    public static Font Num => _num ??= Tabular(Medium);
    public static Font NumBold => _numBold ??= Tabular(SemiBold);
    /// <summary>Small capitals for section heads (Barlow with a little letter spacing).</summary>
    public static Font Caps => _caps ??= new FontVariation { BaseFont = Display ?? ThemeDB.FallbackFont, SpacingGlyph = 1 };

    // ---------------------------------------------------------------- sizes (at 100 % UI size; the Settings slider scales the whole window)
    public const int Body = 15;
    public const int Small = 13;
    public const int Tiny = 12;

    // ---------------------------------------------------------------- the theme
    static Theme _theme;

    public static StyleBoxFlat Box(Color bg, int radius = 6, Color? border = null, int borderWidth = 1, int padX = 10, int padY = 6)
    {
        var s = new StyleBoxFlat { BgColor = bg, AntiAliasing = true };
        s.SetCornerRadiusAll(radius);
        if (border is Color b) { s.BorderColor = b; s.SetBorderWidthAll(borderWidth); }
        s.ContentMarginLeft = padX; s.ContentMarginRight = padX; s.ContentMarginTop = padY; s.ContentMarginBottom = padY;
        return s;
    }

    static StyleBoxFlat Underline(Color c, int width)
    {
        var s = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = c };
        s.BorderWidthBottom = width;
        s.ContentMarginLeft = 10; s.ContentMarginRight = 10; s.ContentMarginTop = 6; s.ContentMarginBottom = 6;
        return s;
    }

    static void ButtonStyle(Theme t, string type, Color bg, Color border, Color hoverBg, Color hoverBorder, Color pressedBg, Color pressedBorder, Color font, Color fontHover, int radius = 6, int padX = 12, int padY = 6)
    {
        t.SetStylebox("normal", type, Box(bg, radius, border, 1, padX, padY));
        t.SetStylebox("hover", type, Box(hoverBg, radius, hoverBorder, 1, padX, padY));
        t.SetStylebox("pressed", type, Box(pressedBg, radius, pressedBorder, 1, padX, padY));
        t.SetStylebox("hover_pressed", type, Box(pressedBg, radius, hoverBorder, 1, padX, padY));
        t.SetStylebox("disabled", type, Box(new Color(bg, bg.A * 0.55f), radius, new Color(border, border.A * 0.5f), 1, padX, padY));
        t.SetStylebox("focus", type, new StyleBoxEmpty());
        t.SetColor("font_color", type, font);
        t.SetColor("font_hover_color", type, fontHover);
        t.SetColor("font_pressed_color", type, fontHover);
        t.SetColor("font_hover_pressed_color", type, fontHover);
        t.SetColor("font_focus_color", type, font);
        t.SetColor("font_disabled_color", type, new Color(Text3, 0.75f));
    }

    public static Theme Theme
    {
        get
        {
            if (_theme != null) return _theme;
            var t = new Theme { DefaultFont = Regular, DefaultFontSize = Body };

            // labels
            t.SetColor("font_color", "Label", Text);
            t.SetColor("font_outline_color", "Label", new Color(0, 0, 0, 0.9f));
            t.SetTypeVariation("Title", "Label");
            t.SetFont("font", "Title", Display); t.SetFontSize("font_size", "Title", 24);
            t.SetTypeVariation("Subtitle", "Label");
            t.SetFont("font", "Subtitle", Display); t.SetFontSize("font_size", "Subtitle", 18);
            t.SetTypeVariation("Section", "Label");
            t.SetFont("font", "Section", Caps); t.SetFontSize("font_size", "Section", 14); t.SetColor("font_color", "Section", Text3);
            t.SetTypeVariation("Dim", "Label");
            t.SetColor("font_color", "Dim", Text2);
            t.SetTypeVariation("Hint", "Label");
            t.SetColor("font_color", "Hint", Text3); t.SetFontSize("font_size", "Hint", Small);
            t.SetTypeVariation("Strong", "Label");
            t.SetFont("font", "Strong", SemiBold);
            t.SetTypeVariation("Number", "Label");
            t.SetFont("font", "Number", NumBold); t.SetFontSize("font_size", "Number", 17);
            t.SetTypeVariation("StatLabel", "Label");
            t.SetFont("font", "StatLabel", Caps); t.SetFontSize("font_size", "StatLabel", 12); t.SetColor("font_color", "StatLabel", Text3);

            // buttons
            t.SetFont("font", "Button", Medium);
            t.SetFontSize("font_size", "Button", Body);
            ButtonStyle(t, "Button", Panel2, Line, Hover, LineHi, YouDeep, You, Text, Text);
            t.SetConstant("h_separation", "Button", 6);
            t.SetTypeVariation("Primary", "Button");
            ButtonStyle(t, "Primary", new Color("1f4a73"), You, new Color("27598a"), new Color("7cbcf6"), new Color("173a5c"), You, Colors.White, Colors.White);
            t.SetFont("font", "Primary", SemiBold);
            t.SetTypeVariation("DangerButton", "Button");
            ButtonStyle(t, "DangerButton", DangerDeep, new Color(Danger, 0.8f), new Color("4a2025"), Danger, new Color("2d1316"), Danger, new Color("ffd2d2"), Colors.White);
            t.SetTypeVariation("WarnButton", "Button");
            ButtonStyle(t, "WarnButton", WarnDeep, new Color(Warn, 0.7f), new Color("4a3d1b"), Warn, new Color("2d2611"), Warn, new Color("ffeeb8"), Colors.White);
            // a list row: no frame until hovered, text on the left
            t.SetTypeVariation("Row", "Button");
            ButtonStyle(t, "Row", new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), Hover, new Color(0, 0, 0, 0), YouDeep, new Color(0, 0, 0, 0), Text, Colors.White, 5, 8, 5);
            // a tab: underline when chosen
            t.SetTypeVariation("Tab", "Button");
            t.SetStylebox("normal", "Tab", Underline(new Color(0, 0, 0, 0), 3));
            t.SetStylebox("hover", "Tab", Underline(LineHi, 3));
            t.SetStylebox("pressed", "Tab", Underline(You, 3));
            t.SetStylebox("hover_pressed", "Tab", Underline(You, 3));
            t.SetStylebox("disabled", "Tab", Underline(new Color(0, 0, 0, 0), 3));
            t.SetStylebox("focus", "Tab", new StyleBoxEmpty());
            t.SetColor("font_color", "Tab", Text3); t.SetColor("font_hover_color", "Tab", Text); t.SetColor("font_pressed_color", "Tab", Text);
            t.SetColor("font_hover_pressed_color", "Tab", Text); t.SetColor("font_focus_color", "Tab", Text3);
            t.SetFont("font", "Tab", SemiBold); t.SetFontSize("font_size", "Tab", 14);
            // a chip: small toggle (speeds, filters, layers)
            t.SetTypeVariation("Chip", "Button");
            ButtonStyle(t, "Chip", Panel2, Line, Hover, LineHi, YouDeep, You, Text2, Text, 6, 9, 4);
            t.SetFontSize("font_size", "Chip", 14);
            // a link-looking button
            t.SetTypeVariation("Link", "Button");
            ButtonStyle(t, "Link", new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), new Color("8cc4f7"), new Color("b9dbfa"), 4, 2, 1);
            t.SetFontSize("font_size", "Link", Small);

            // check boxes and switches
            foreach (var cb in new[] { "CheckBox", "CheckButton" })
            {
                t.SetStylebox("normal", cb, Box(new Color(0, 0, 0, 0), 5, null, 0, 6, 4));
                t.SetStylebox("hover", cb, Box(Hover, 5, null, 0, 6, 4));
                t.SetStylebox("pressed", cb, Box(new Color(0, 0, 0, 0), 5, null, 0, 6, 4));
                t.SetStylebox("hover_pressed", cb, Box(Hover, 5, null, 0, 6, 4));
                t.SetStylebox("focus", cb, new StyleBoxEmpty());
                t.SetColor("font_color", cb, Text); t.SetColor("font_hover_color", cb, Colors.White); t.SetColor("font_pressed_color", cb, Text);
                t.SetColor("font_hover_pressed_color", cb, Colors.White); t.SetColor("font_focus_color", cb, Text);
            }

            // panels, separators, scroll bars, inputs, tooltips
            t.SetStylebox("panel", "PanelContainer", Box(Chrome, 0, null, 0, 0, 0));
            t.SetStylebox("panel", "Panel", Box(Chrome, 0, null, 0, 0, 0));
            var sep = new StyleBoxLine { Color = Line, Thickness = 1 };
            t.SetStylebox("separator", "HSeparator", sep);
            t.SetConstant("separation", "HSeparator", 9);
            t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Line, Thickness = 1, Vertical = true });
            t.SetConstant("separation", "VSeparator", 12);
            foreach (var sb in new[] { "VScrollBar", "HScrollBar" })
            {
                t.SetStylebox("scroll", sb, Box(new Color(0, 0, 0, 0), 4, null, 0, 0, 0));
                t.SetStylebox("grabber", sb, Box(LineHi, 4, null, 0, 3, 3));
                t.SetStylebox("grabber_highlight", sb, Box(new Color("566580"), 4, null, 0, 3, 3));
                t.SetStylebox("grabber_pressed", sb, Box(You, 4, null, 0, 3, 3));
            }
            t.SetStylebox("normal", "LineEdit", Box(new Color("0e121a"), 6, Line, 1, 10, 6));
            t.SetStylebox("focus", "LineEdit", Box(new Color("0e121a"), 6, You, 1, 10, 6));
            t.SetColor("font_color", "LineEdit", Text);
            t.SetColor("font_placeholder_color", "LineEdit", Text3);
            t.SetColor("caret_color", "LineEdit", You);
            t.SetStylebox("panel", "TooltipPanel", Box(new Color("0f141d"), 6, LineHi, 1, 10, 8));
            t.SetColor("font_color", "TooltipLabel", Text);
            t.SetFont("font", "TooltipLabel", Regular);
            t.SetFontSize("font_size", "TooltipLabel", 14);
            t.SetStylebox("panel", "ItemList", new StyleBoxEmpty());
            t.SetStylebox("focus", "ItemList", new StyleBoxEmpty());
            t.SetStylebox("hovered", "ItemList", Box(Hover, 4, null, 0, 4, 2));
            t.SetStylebox("selected", "ItemList", Box(YouDeep, 4, null, 0, 4, 2));
            t.SetStylebox("selected_focus", "ItemList", Box(YouDeep, 4, null, 0, 4, 2));
            t.SetStylebox("cursor", "ItemList", new StyleBoxEmpty());
            t.SetStylebox("cursor_unfocused", "ItemList", new StyleBoxEmpty());
            t.SetColor("guide_color", "ItemList", new Color(0, 0, 0, 0));
            t.SetColor("font_color", "ItemList", Text);
            t.SetFontSize("font_size", "ItemList", 14);
            t.SetConstant("v_separation", "ItemList", 5);

            // Tree (sector windows)
            t.SetStylebox("panel", "Tree", new StyleBoxEmpty());
            t.SetStylebox("focus", "Tree", new StyleBoxEmpty());
            t.SetStylebox("selected", "Tree", Box(YouDeep, 0, null, 0, 0, 0));
            t.SetStylebox("selected_focus", "Tree", Box(YouDeep, 0, null, 0, 0, 0));
            t.SetStylebox("hovered", "Tree", Box(Hover, 0, null, 0, 0, 0));
            t.SetStylebox("cursor", "Tree", new StyleBoxEmpty());
            t.SetStylebox("cursor_unfocused", "Tree", new StyleBoxEmpty());
            t.SetStylebox("title_button_normal", "Tree", Box(Panel, 0, null, 0, 6, 3));
            t.SetStylebox("title_button_hover", "Tree", Box(Panel, 0, null, 0, 6, 3));
            t.SetStylebox("title_button_pressed", "Tree", Box(Panel, 0, null, 0, 6, 3));
            t.SetColor("title_button_color", "Tree", Text3);
            t.SetFont("title_button_font", "Tree", Caps);
            t.SetFontSize("title_button_font_size", "Tree", 13);
            t.SetColor("font_color", "Tree", Text);
            t.SetColor("font_selected_color", "Tree", Colors.White);
            t.SetColor("guide_color", "Tree", new Color(Line, 0.6f));
            t.SetColor("relationship_line_color", "Tree", new Color(0, 0, 0, 0));
            t.SetConstant("draw_guides", "Tree", 1);
            t.SetConstant("draw_relationship_lines", "Tree", 0);
            t.SetConstant("v_separation", "Tree", 3);
            t.SetConstant("item_margin", "Tree", 0);
            t.SetConstant("inner_item_margin_left", "Tree", 6);
            t.SetConstant("inner_item_margin_right", "Tree", 4);
            t.SetFont("font", "Tree", Num);
            t.SetFontSize("font_size", "Tree", 14);

            // progress bars
            t.SetStylebox("background", "ProgressBar", Box(Panel2, 3, null, 0, 0, 0));
            t.SetStylebox("fill", "ProgressBar", Box(You, 3, null, 0, 0, 0));
            t.SetColor("font_color", "ProgressBar", new Color(0, 0, 0, 0));

            // spin box text
            t.SetFont("font", "SpinBox", Num);
            return _theme = t;
        }
    }

    // ---------------------------------------------------------------- factories

    public static Label Label(string text, string variation = null, Color? color = null, int size = 0, bool wrap = true)
    {
        var l = new Label { Text = text ?? "", MouseFilter = Control.MouseFilterEnum.Ignore };
        if (wrap) { l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; }
        if (variation != null) l.ThemeTypeVariation = variation;
        if (color is Color c) l.AddThemeColorOverride("font_color", c);
        if (size > 0) l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    public static Button Button(string text, Action onPressed, string tip = null, string variation = null, bool disabled = false)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, Disabled = disabled, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        if (tip != null) b.TooltipText = Wrap(tip);
        if (variation != null) b.ThemeTypeVariation = variation;
        if (onPressed != null) b.Pressed += onPressed;
        return b;
    }

    /// <summary>A button that fills the width and wraps its text: the common action button in the drawer.</summary>
    public static Button Action(string text, Action onPressed, string tip = null, bool disabled = false, string variation = null)
    {
        var b = Button(text, onPressed, tip, variation, disabled);
        b.Alignment = HorizontalAlignment.Left;
        b.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        b.CustomMinimumSize = new Vector2(0, 34);
        return b;
    }

    public static Button Chip(string text, Action onPressed, string tip = null, bool pressed = false, ButtonGroup group = null)
    {
        var b = Button(text, onPressed, tip, "Chip");
        b.ToggleMode = true; b.ButtonPressed = pressed;
        if (group != null) b.ButtonGroup = group;
        return b;
    }

    public static PanelContainer Card(Color? bg = null, Color? border = null, int pad = 12, int radius = 8)
    {
        var p = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        p.AddThemeStyleboxOverride("panel", Box(bg ?? Panel, radius, border, 1, pad, pad - 2));
        return p;
    }

    public static VBoxContainer VBox(int gap = 6) { var v = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; v.AddThemeConstantOverride("separation", gap); return v; }
    public static HBoxContainer HBox(int gap = 8) { var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", gap); return h; }
    public static Control Space(int h) => new Control { CustomMinimumSize = new Vector2(0, h), MouseFilter = Control.MouseFilterEnum.Ignore };
    public static Control Grow() => new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>A key and value on one line: the key dim on the left, the value on the right.</summary>
    public static HBoxContainer Row(string key, string value, Color? valueColor = null, string tip = null)
    {
        var h = HBox(8);
        h.MouseFilter = tip != null ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        if (tip != null) h.TooltipText = Wrap(tip);
        var k = Label(key, "Dim", wrap: false);
        k.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        k.ClipText = true;
        h.AddChild(k);
        var v = Label(value, null, valueColor, wrap: false);
        v.AddThemeFontOverride("font", NumBold);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        h.AddChild(v);
        return h;
    }

    /// <summary>A thin bar for a share (0..1).</summary>
    public static Control Bar(float share, Color fill, float height = 6, Color? back = null)
    {
        var bar = new ProgressBar { MinValue = 0, MaxValue = 1000, Value = Math.Clamp(share, 0, 1) * 1000, ShowPercentage = false, CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("fill", Box(fill, 3, null, 0, 0, 0));
        if (back is Color b) bar.AddThemeStyleboxOverride("background", Box(b, 3, null, 0, 0, 0));
        return bar;
    }

    /// <summary>A small coloured pill with a word in it ("LIVE", "YOUR HQ").</summary>
    public static PanelContainer Tag(string text, Color color, bool filled = false)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        p.AddThemeStyleboxOverride("panel", Box(filled ? color : new Color(color, 0.14f), 4, new Color(color, 0.75f), 1, 6, 1));
        var l = Label(text, null, filled ? MapBg : color, Tiny, wrap: false);
        l.AddThemeFontOverride("font", SemiBold);
        p.AddChild(l);
        return p;
    }

    /// <summary>A section head in small capitals, with an optional line under it.</summary>
    public static Label Section(string text) { var l = Label(text, "Section", wrap: false); l.Uppercase = true; return l; }

    /// <summary>Godot tooltips do not wrap: break long tips into lines of about <paramref name="width"/> characters.</summary>
    public static string Wrap(string text, int width = 64)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= width) return text;
        var sb = new System.Text.StringBuilder();
        foreach (var para in text.Split('\n'))
        {
            int col = 0;
            foreach (var word in para.Split(' '))
            {
                if (col > 0 && col + word.Length + 1 > width) { sb.Append('\n'); col = 0; }
                else if (col > 0) { sb.Append(' '); col++; }
                sb.Append(word); col += word.Length;
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static void Clear(Node n)
    {
        foreach (var c in n.GetChildren()) { n.RemoveChild(c); c.QueueFree(); }
    }

    public static string Plural(int n, string one, string many) => n == 1 ? one : many;
    public static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>"in 1 day" / "in 6 days" / "today".</summary>
    public static string InDays(int days) => days <= 0 ? "today" : days == 1 ? "in 1 day" : $"in {days} days";
    public static string DaysAgo(int days) => days <= 0 ? "today" : days == 1 ? "yesterday" : $"{days} days ago";
}

/// <summary>A tiny drawn icon (no art files): dots, rings, fleet triangles, warning marks. Used in the legend, tabs and rows.</summary>
public partial class Glyph : Control
{
    public enum Kind { Dot, Ring, DoubleRing, Triangle, TriangleDown, Square, Diamond, Cross, Warn, DashedRing, Bar }
    public Kind Shape;
    public Color Color = Ui.Text;
    public Color? Fill;
    public float Size = 12;

    public Glyph() { MouseFilter = MouseFilterEnum.Ignore; }
    public Glyph(Kind k, Color c, float size = 12, Color? fill = null) : this()
    {
        Shape = k; Color = c; Size = size; Fill = fill;
        CustomMinimumSize = new Vector2(size + 4, size + 4);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public override void _Draw()
    {
        var c = Size / 2f + 2f;
        var p = new Vector2(c, CustomMinimumSize.Y / 2f);
        float r = Size / 2f;
        switch (Shape)
        {
            case Kind.Dot: DrawCircle(p, r, Color); break;
            case Kind.Ring: DrawArc(p, r - 1, 0, Mathf.Tau, 24, Color, 1.6f, true); break;
            case Kind.DashedRing:
                for (int i = 0; i < 8; i++) DrawArc(p, r - 1, i * Mathf.Tau / 8, i * Mathf.Tau / 8 + Mathf.Tau / 16, 4, Color, 1.8f, true);
                break;
            case Kind.DoubleRing:
                DrawCircle(p, r * 0.45f, Fill ?? Color);
                DrawArc(p, r - 1, 0, Mathf.Tau, 24, Color, 1.6f, true);
                break;
            case Kind.Triangle:
            case Kind.TriangleDown:
            {
                float s = Shape == Kind.Triangle ? -1 : 1;
                var pts = new[] { p + new Vector2(0, s * r), p + new Vector2(-r * 0.9f, -s * r * 0.7f), p + new Vector2(r * 0.9f, -s * r * 0.7f) };
                DrawColoredPolygon(pts, Fill ?? Color);
                DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[0] }, Color, 1.2f, true);
                break;
            }
            case Kind.Square: DrawRect(new Rect2(p - new Vector2(r * 0.8f, r * 0.8f), new Vector2(r * 1.6f, r * 1.6f)), Color); break;
            case Kind.Diamond: DrawColoredPolygon(new[] { p + new Vector2(0, -r), p + new Vector2(r, 0), p + new Vector2(0, r), p + new Vector2(-r, 0) }, Color); break;
            case Kind.Cross:
                DrawCircle(p, r * 0.8f, new Color("29292e"));
                DrawLine(p + new Vector2(-r, -r), p + new Vector2(r, r), Color, 2, true);
                DrawLine(p + new Vector2(-r, r), p + new Vector2(r, -r), Color, 2, true);
                break;
            case Kind.Warn:
            {
                var pts = new[] { p + new Vector2(0, -r), p + new Vector2(r, r * 0.85f), p + new Vector2(-r, r * 0.85f) };
                DrawColoredPolygon(pts, Color);
                DrawLine(p + new Vector2(0, -r * 0.35f), p + new Vector2(0, r * 0.3f), Ui.MapBg, 1.8f);
                DrawCircle(p + new Vector2(0, r * 0.58f), 1.1f, Ui.MapBg);
                break;
            }
            case Kind.Bar: DrawRect(new Rect2(new Vector2(1, p.Y - 2), new Vector2(Size + 2, 4)), Color); break;
        }
    }
}
