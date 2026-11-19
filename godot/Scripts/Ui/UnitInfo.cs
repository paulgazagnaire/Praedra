using System;
using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// What each thing the player can build or train is, for its hover card (Paul 2026-11-19: "I need a description of the ship. I need to
/// know what is the ships role and stats. And possibly an illustration ... same for all building and units"). The role and the
/// description come from data/design/unit-descriptions.csv, written from the Lore Master's specs/creative files (each row names its
/// source). Screens only; no rule reads it. A class missing from the file shows its stats only.
/// </summary>
public static class UnitInfo
{
    public sealed record Entry(string Role, string Description);

    static Dictionary<string, Entry> _all;

    /// <summary>The role and description of a class, or null when the file has none.</summary>
    public static Entry Of(string cls)
    {
        if (_all == null)
        {
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            string path = System.IO.Path.Combine(ProjectSettings.GlobalizePath("user://gamedata"), "data", "design", "unit-descriptions.csv");
            if (!System.IO.File.Exists(path)) GD.PushWarning($"Unit descriptions: {path} is missing, so the hover cards show stats only.");
            else
            {
                var t = Praedra.Core.Data.CsvTable.Load(path);
                for (int r = 0; r < t.RowCount; r++) map[t.Get(r, "name")] = new Entry(t.Get(r, "role"), t.Get(r, "description"));
            }
            _all = map;
        }
        return cls != null && _all.TryGetValue(cls, out var e) ? e : null;
    }

    const int Width = 380;

    /// <summary>
    /// The hover card: the picture (the class's painted placeholder where there is one, else its pixel icon drawn large) beside the name
    /// and role, the description, the stats as label and value pairs, then <paramref name="foot"/> (cost, days, why not, how to order).
    /// </summary>
    public static Control Card(string cls, string shownName, string[] icon, Color ink, IReadOnlyList<(string Label, string Value)> stats, string foot)
    {
        var info = Of(cls);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(Width, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 10);
        v.SetMeta(UiTest.FullName, "Info card " + cls);

        if (UnitArt.Full(cls) is Texture2D painted)
        {
            var pic = UnitArt.Rect(painted);
            pic.CustomMinimumSize = new Vector2(Width, Width * 9 / 16);
            v.AddChild(pic);
        }
        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 14);
        if (UnitArt.Full(cls) == null && icon is { Length: > 0 })
        {
            // the pixel icon, a whole number of screen pixels per icon pixel, about 96 px wide (Paul: icons are code only)
            int scale = Math.Clamp(96 / Math.Max(1, icon[0].Length), 2, 6);
            var big = IconPic.Of(icon, scale, ink);
            big.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            head.AddChild(big);
        }
        var titles = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 2);
        titles.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        titles.AddChild(Look.Words(shownName, 18, Look.Text, true));
        if (info != null && info.Role.Length > 0) titles.AddChild(Look.Words(info.Role, 13, Look.Accent));
        head.AddChild(titles);
        v.AddChild(head);

        if (info != null && info.Description.Length > 0) v.AddChild(Wrapped(info.Description, 13, Look.Text));

        if (stats.Count > 0)
        {
            var grid = new GridContainer { Columns = 4, MouseFilter = Control.MouseFilterEnum.Ignore };
            grid.AddThemeConstantOverride("h_separation", 12);
            grid.AddThemeConstantOverride("v_separation", 3);
            foreach (var (label, value) in stats)
            {
                var l = Look.Words(label, 12, Look.Soft);
                l.CustomMinimumSize = new Vector2(96, 0);
                grid.AddChild(l);
                var val = Look.Words(value, 12, Look.Text, true);
                val.CustomMinimumSize = new Vector2(70, 0);
                grid.AddChild(val);
            }
            v.AddChild(grid);
        }
        if (!string.IsNullOrEmpty(foot)) v.AddChild(Wrapped(foot, 12, Look.Soft));
        return v;
    }

    static Label Wrapped(string text, int size, Color color)
    {
        var l = Look.Words(text, size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(Width, 0);
        return l;
    }
}

/// <summary>A build card whose hover card is <see cref="UnitInfo.Card"/>: what the thing is, its stats and its picture. Screens only.</summary>
public partial class InfoButton : Button
{
    public Func<Control> Make;
    public override Control _MakeCustomTooltip(string forText) => Make?.Invoke();
}
