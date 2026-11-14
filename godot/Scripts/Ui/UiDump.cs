using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;

namespace PraedraViewer;

/// <summary>
/// `--dump-ui file.json` beside `--screenshot`: writes what every visible control draws (box, text, font, colours, where) so a
/// script can rebuild the screen as editable layers in Paul's Paper file (tools/paper-sync/). Text, boxes and lines are written
/// out; anything drawn by code or from a picture (icons, planets, the map, gauges) is marked "image" and the script cuts it from
/// the screenshot. A tool for the design file only: the game never reads it.
/// </summary>
public static class UiDump
{
    sealed class N
    {
        public int i, p;
        public string type, cs, name, full, kind, role;
        public float x, y, w, h;
        public bool clip;
        public float[] mod;
        public Dictionary<string, object> box, line, text;
        public float[] color;
    }

    public static void Write(Node root, string path, IEnumerable<(string Key, Control Node)> roots)
    {
        var list = new List<N>();
        var ids = new Dictionary<ulong, int>();
        Walk(root, -1, new Color(1, 1, 1, 1), null, list, ids);
        var keys = roots.Where(r => r.Node != null && GodotObject.IsInstanceValid(r.Node) && ids.ContainsKey(r.Node.GetInstanceId()))
            .Select(r => new { key = r.Key, i = ids[r.Node.GetInstanceId()] }).ToList();
        var vp = root.GetViewport().GetVisibleRect().Size;
        var doc = new { width = vp.X, height = vp.Y, roots = keys, nodes = list };
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        f.StoreString(JsonSerializer.Serialize(doc, new JsonSerializerOptions { IncludeFields = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        GD.Print($"Saved UI dump {path}: {list.Count} nodes, {keys.Count} roots");
    }

    /// <summary>Hide everything on screen but this control and its parents (the map alone, for the background picture of a frame).</summary>
    public static void HideAllBut(Control keep)
    {
        for (Node n = keep; n.GetParent() is Node p; n = p)
            foreach (var s in p.GetChildren())
                if (s != n && s is CanvasItem ci && s is not ColorRect) ci.Visible = false;
    }

    static float[] C(Color c) => new[] { (float)Math.Round(c.R, 4), (float)Math.Round(c.G, 4), (float)Math.Round(c.B, 4), (float)Math.Round(c.A, 4) };

    static void Walk(Node n, int parent, Color mod, Rect2? clip, List<N> list, Dictionary<ulong, int> ids)
    {
        if (n is CanvasItem ci && !ci.Visible) return;
        if (n is Window win && win != n.GetTree().Root && !win.Visible) return;
        int me = parent;
        var m = mod;
        if (n is CanvasItem c0) m *= c0.Modulate;
        if (n is Control ctl)
        {
            var r = ctl.GetGlobalRect();
            if (r.Size.X < 0.5f || r.Size.Y < 0.5f) return;
            if (clip is Rect2 cr && !cr.Intersects(r)) return;
            if (m.A < 0.01f) return;
            var node = Describe(ctl, m * ctl.SelfModulate);
            node.i = list.Count; node.p = parent;
            node.x = r.Position.X; node.y = r.Position.Y; node.w = r.Size.X; node.h = r.Size.Y;
            node.clip = ctl.ClipContents;
            list.Add(node);
            ids[ctl.GetInstanceId()] = node.i;
            me = node.i;
            if (node.kind == "image") return;   // a picture is cut whole from the screenshot, children included
            if (ctl.ClipContents) clip = clip is Rect2 c1 ? c1.Intersection(r) : r;
        }
        else if (n is CanvasItem && n is not Control)
        {
            // a Node2D or similar inside the UI: its parent control should have been an image; skip it
            return;
        }
        foreach (var ch in n.GetChildren()) Walk(ch, me, m, clip, list, ids);
    }

    static bool DrawsByCode(Control c)
    {
        for (var t = c.GetType(); t != null && t.Namespace != null && !t.Namespace.StartsWith("Godot", StringComparison.Ordinal); t = t.BaseType)
            if (t.GetMethod("_Draw", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null) return true;
        return c.GetSignalConnectionList(CanvasItem.SignalName.Draw).Count > 0;
    }

    static N Describe(Control c, Color mod)
    {
        var n = new N { type = c.GetClass(), cs = c.GetType().Name, name = c.Name, kind = "none" };
        if (c.HasMeta(UiTest.FullName)) n.full = c.GetMeta(UiTest.FullName).AsString();
        if (Math.Abs(mod.R - 1) > 0.001f || Math.Abs(mod.G - 1) > 0.001f || Math.Abs(mod.B - 1) > 0.001f || Math.Abs(mod.A - 1) > 0.001f) n.mod = C(mod);
        if (DrawsByCode(c)) { n.kind = "image"; n.role = "drawn"; return n; }
        switch (c)
        {
            case CheckBox or CheckButton or TextureRect or NinePatchRect or TextureButton or TextureProgressBar or ItemList or Tree or RichTextLabel or SubViewportContainer or ScrollBar or Slider or GraphEdit or VideoStreamPlayer:
                n.kind = "image"; n.role = c.GetClass(); return n;
            case ColorRect cr:
                n.kind = "color"; n.color = C(cr.Color * mod); return n;
            case Label l:
                n.kind = "label";
                n.box = Box(c.GetThemeStylebox("normal"), mod, n);
                n.text = Text(c, l.Text, l.Uppercase, l.HorizontalAlignment, l.VerticalAlignment, l.AutowrapMode, l.TextOverrunBehavior, l.ClipText, c.GetThemeColor("font_color") * mod, n.box, false, l.MaxLinesVisible);
                if (n.kind == "image") return n;
                return n;
            case LinkButton lb:
            {
                n.kind = "link";
                var col = lb.Disabled ? c.GetThemeColor("font_disabled_color") : lb.ButtonPressed ? c.GetThemeColor("font_pressed_color") : c.GetThemeColor("font_color");
                n.text = Text(c, lb.Text, false, HorizontalAlignment.Left, VerticalAlignment.Top, TextServer.AutowrapMode.Off, TextServer.OverrunBehavior.NoTrimming, false, col * mod, null, lb.Underline == LinkButton.UnderlineMode.Always);
                return n;
            }
            case Button b:
            {
                n.kind = "button";
                string state = b.Disabled ? "disabled" : b.ButtonPressed ? "pressed" : "normal";
                n.box = Box(c.GetThemeStylebox(state), mod, n);
                if (b.Icon != null) { n.kind = "image"; n.role = "button with icon"; return n; }
                var col = b.Disabled ? c.GetThemeColor("font_disabled_color") : b.ButtonPressed ? c.GetThemeColor("font_pressed_color") : c.GetThemeColor("font_color");
                n.text = Text(c, b.Text, false, b.Alignment, VerticalAlignment.Center, b.AutowrapMode, b.TextOverrunBehavior, b.ClipText, col * mod, n.box, false);
                return n;
            }
            case LineEdit le:
            {
                n.kind = "button";
                n.box = Box(c.GetThemeStylebox("normal"), mod, n);
                bool ph = string.IsNullOrEmpty(le.Text);
                var col = ph ? c.GetThemeColor("font_placeholder_color") : c.GetThemeColor("font_color");
                n.text = Text(c, ph ? le.PlaceholderText : le.Text, false, le.Alignment, VerticalAlignment.Center, TextServer.AutowrapMode.Off, TextServer.OverrunBehavior.TrimEllipsis, true, col * mod, n.box, false);
                return n;
            }
            case ProgressBar:
                n.kind = "image"; n.role = "ProgressBar"; return n;
            case HSeparator or VSeparator:
            {
                n.kind = "line";
                var sb = c.GetThemeStylebox("separator");
                if (sb is StyleBoxLine sl) n.line = new() { ["color"] = C(sl.Color * mod), ["thick"] = sl.Thickness, ["vertical"] = sl.Vertical, ["grow0"] = sl.GrowBegin, ["grow1"] = sl.GrowEnd };
                else if (sb is StyleBoxFlat) n.box = Box(sb, mod, n);
                return n;
            }
            case PanelContainer or Panel:
                n.kind = "panel";
                n.box = Box(c.GetThemeStylebox("panel"), mod, n);
                return n;
            case ScrollContainer:
                n.kind = "panel";
                n.box = Box(c.GetThemeStylebox("panel"), mod, n);
                return n;
            case Container:
                return n;
        }
        if (c.GetClass() == "Control") return n;
        // a Godot control this dump does not know how to read: cut it from the picture
        n.kind = "image"; n.role = c.GetClass();
        return n;
    }

    static Dictionary<string, object> Box(StyleBox sb, Color mod, N n)
    {
        switch (sb)
        {
            case null: return null;
            case StyleBoxEmpty e:
                return new() { ["cm"] = new[] { e.ContentMarginLeft, e.ContentMarginTop, e.ContentMarginRight, e.ContentMarginBottom } };
            case StyleBoxFlat f:
                return new()
                {
                    ["bg"] = f.DrawCenter ? C(f.BgColor * mod) : null,
                    ["bw"] = new[] { f.BorderWidthLeft, f.BorderWidthTop, f.BorderWidthRight, f.BorderWidthBottom },
                    ["bc"] = C(f.BorderColor * mod),
                    ["rad"] = new[] { f.CornerRadiusTopLeft, f.CornerRadiusTopRight, f.CornerRadiusBottomRight, f.CornerRadiusBottomLeft },
                    ["cm"] = new[] { f.GetMargin(Side.Left), f.GetMargin(Side.Top), f.GetMargin(Side.Right), f.GetMargin(Side.Bottom) },
                    ["em"] = new[] { f.ExpandMarginLeft, f.ExpandMarginTop, f.ExpandMarginRight, f.ExpandMarginBottom },
                    ["shadow"] = f.ShadowSize > 0 && f.ShadowColor.A > 0 ? new object[] { f.ShadowSize, C(f.ShadowColor * mod), f.ShadowOffset.X, f.ShadowOffset.Y } : null,
                };
            case StyleBoxLine l:
                return new() { ["lineColor"] = C(l.Color * mod), ["thick"] = l.Thickness, ["vertical"] = l.Vertical };
            default:
                n.kind = "image"; n.role = "StyleBoxTexture";
                return null;
        }
    }

    /// <summary>The text exactly as Godot lays it out: its lines (Godot's own line breaks), font, size, colour and alignment.</summary>
    static Dictionary<string, object> Text(Control c, string text, bool upper, HorizontalAlignment ha, VerticalAlignment va, TextServer.AutowrapMode wrap,
        TextServer.OverrunBehavior overrun, bool clipText, Color col, Dictionary<string, object> box, bool underline, int maxLines = -1)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (upper) text = text.ToUpper();
        var font = c.GetThemeFont("font");
        int size = c.GetThemeFontSize("font_size");
        int spacing = c.HasThemeConstant("line_spacing") ? c.GetThemeConstant("line_spacing") : 0;
        float[] cm = box != null && box.TryGetValue("cm", out var o) ? (float[])o : new float[4];
        float avail = Math.Max(1, c.Size.X - Math.Max(0, cm[0]) - Math.Max(0, cm[2]));   // an unset margin is -1: Godot draws it as 0
        var para = new TextParagraph();
        para.AddString(text, font, size);
        var flags = TextServer.LineBreakFlag.Mandatory;
        flags |= wrap switch
        {
            TextServer.AutowrapMode.Arbitrary => TextServer.LineBreakFlag.GraphemeBound,
            TextServer.AutowrapMode.Word => TextServer.LineBreakFlag.WordBound,
            TextServer.AutowrapMode.WordSmart => TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.Adaptive,
            _ => 0,
        };
        para.BreakFlags = flags;
        if (wrap != TextServer.AutowrapMode.Off) para.Width = avail;
        var lines = new List<string>();
        var widths = new List<float>();
        for (int i = 0; i < para.GetLineCount(); i++)
        {
            var range = para.GetLineRange(i);
            string s = text.Substring(range.X, Math.Min(range.Y, text.Length) - range.X).TrimEnd('\n', '\r');
            if (wrap != TextServer.AutowrapMode.Off) s = s.TrimEnd(' ');
            float lw = font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X;
            if ((overrun != TextServer.OverrunBehavior.NoTrimming || clipText) && lw > avail + 0.5f && wrap == TextServer.AutowrapMode.Off)
            {
                bool ell = overrun is TextServer.OverrunBehavior.TrimEllipsis or TextServer.OverrunBehavior.TrimWordEllipsis;
                string tail = ell ? "…" : "";
                while (s.Length > 0 && font.GetStringSize(s + tail, HorizontalAlignment.Left, -1, size).X > avail) s = s[..^1];
                s = s.TrimEnd(' ') + tail;
                lw = font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X;
            }
            lines.Add(s); widths.Add(lw);
        }
        // a label cut to its last visible line: Godot ends that line with an ellipsis, trimmed to fit (a tile's "Advanced / Constructio…")
        if (maxLines > 0 && lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines); widths.RemoveRange(maxLines, widths.Count - maxLines);
            if (overrun != TextServer.OverrunBehavior.NoTrimming)
            {
                string s = lines[^1];
                while (s.Length > 0 && font.GetStringSize(s + "…", HorizontalAlignment.Left, -1, size).X > avail) s = s[..^1];
                lines[^1] = s.TrimEnd(' ') + "…"; widths[^1] = font.GetStringSize(lines[^1], HorizontalAlignment.Left, -1, size).X;
            }
        }
        string family = font.GetFontName();
        if (font is FontVariation fv && fv.BaseFont != null) family = fv.BaseFont.GetFontName();
        int weight = font.GetFontWeight();
        if (font is FontVariation fv2 && fv2.BaseFont != null) weight = fv2.BaseFont.GetFontWeight();
        return new()
        {
            ["lines"] = lines, ["widths"] = widths, ["family"] = family, ["weight"] = weight, ["style"] = font.GetFontStyleName(), ["size"] = size,
            ["color"] = C(col), ["ha"] = ha.ToString(), ["va"] = va.ToString(), ["lineh"] = font.GetHeight(size) + spacing, ["spacing"] = spacing,
            ["ascent"] = font.GetAscent(size), ["underline"] = underline, ["wrap"] = wrap.ToString(),
            ["letter"] = font is FontVariation lv ? lv.SpacingGlyph : 0,
        };
    }
}
