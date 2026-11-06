using System;
using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// Painted pictures of unit classes (Paul, 2026-11-06: the Lictors' illustration first). art/units/units.json maps a class name to its picture
/// and to the 2:1 crop shown on small tiles; a class with no entry keeps its drawn UnitIcon. Screens only.
/// </summary>
public static class UnitArt
{
    const string Dir = "res://art/units/";
    const string Index = Dir + "units.json";

    static Dictionary<string, (string File, Rect2? Thumb)> _entries;
    static readonly Dictionary<string, Texture2D> _full = new(StringComparer.Ordinal);
    static readonly Dictionary<string, Texture2D> _thumbs = new(StringComparer.Ordinal);

    static void Load()
    {
        if (_entries != null) return;
        _entries = new Dictionary<string, (string, Rect2?)>(StringComparer.Ordinal);
        if (!FileAccess.FileExists(Index)) return;
        foreach (var (k, v) in Json.ParseString(FileAccess.GetFileAsString(Index)).AsGodotDictionary())
        {
            string name = k.AsString();
            if (name.StartsWith('_') || v.VariantType != Variant.Type.Dictionary) continue;
            var d = v.AsGodotDictionary();
            Rect2? thumb = null;
            if (d.ContainsKey("thumb") && d["thumb"].AsGodotArray() is { Count: 4 } a)
                thumb = new Rect2(a[0].AsInt32(), a[1].AsInt32(), a[2].AsInt32(), a[3].AsInt32());
            _entries[name] = (d["file"].AsString(), thumb);
        }
    }

    /// <summary>Does this class have a painted picture?</summary>
    public static bool Has(string cls) { Load(); return cls != null && _entries.ContainsKey(cls) && Full(cls) != null; }

    /// <summary>The whole picture of a class, or null.</summary>
    public static Texture2D Full(string cls)
    {
        Load();
        if (cls == null || !_entries.TryGetValue(cls, out var e)) return null;
        if (_full.TryGetValue(cls, out var t)) return t;
        string path = Dir + e.File;
        t = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        if (t == null) GD.PushWarning("Unit picture missing: " + path);
        return _full[cls] = t;
    }

    /// <summary>The tile crop of a class's picture (the whole picture when units.json gives no crop), or null.</summary>
    public static Texture2D Thumb(string cls)
    {
        if (Full(cls) is not Texture2D full) return null;
        if (_thumbs.TryGetValue(cls, out var t)) return t;
        var e = _entries[cls];
        t = e.Thumb is Rect2 r ? new AtlasTexture { Atlas = full, Region = r } : full;
        return _thumbs[cls] = t;
    }

    /// <summary>The hover card of a class: its whole picture (480 x 270) over the tile's own tip text, or null with no picture.</summary>
    public static Control Card(string cls, string text)
    {
        if (Full(cls) is not Texture2D tex) return null;
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        var pic = Rect(tex);
        pic.CustomMinimumSize = new Vector2(480, 270);
        v.AddChild(pic);
        if (!string.IsNullOrEmpty(text))
            v.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(480, 0) });
        return v;
    }

    /// <summary>A picture filling its box, cropped to cover it.</summary>
    public static TextureRect Rect(Texture2D tex) => new()
    {
        Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true,
    };
}

/// <summary>A tile that shows a class's whole painted picture as its hover card. Screens only.</summary>
public partial class ArtTile : PanelContainer
{
    public string Cls;
    public override Control _MakeCustomTooltip(string forText) => UnitArt.Card(Cls, forText);
}

/// <summary>A build card button that shows a class's whole painted picture as its hover card. Screens only.</summary>
public partial class ArtButton : Button
{
    public string Cls;
    public override Control _MakeCustomTooltip(string forText) => UnitArt.Card(Cls, forText);
}
