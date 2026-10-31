using System;
using System.Collections.Generic;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The picture of every system's planet, drawn by the Planet Maker (tools/planet-maker) and baked into one sheet by
/// tools/planet-maker/bake-thumbs.js: art/planets/atlas.png holds one square cell per system, atlas.json says which cell and what kind of
/// world it is. The pictures are dithered one pixel per dot, so they are shown with nearest filtering at a whole number of screen pixels
/// per dot (the dither style). Screens only; a system with no picture simply shows none.
/// </summary>
public static class PlanetArt
{
    const string Sheet = "res://art/planets/atlas.png";
    const string Index = "res://art/planets/atlas.json";

    static Texture2D _sheet;
    static Dictionary<string, (int Cell, string Kind)> _cells;
    static int _size = 128, _cols = 1;
    static readonly Dictionary<string, AtlasTexture> _cache = new(StringComparer.Ordinal);

    static void Load()
    {
        if (_cells != null) return;
        _cells = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        if (!FileAccess.FileExists(Index) || !ResourceLoader.Exists(Sheet)) { GD.PrintErr("Planet pictures missing: " + Index); return; }
        var root = Json.ParseString(FileAccess.GetFileAsString(Index)).AsGodotDictionary();
        _size = root["size"].AsInt32();
        _cols = Math.Max(1, root["cols"].AsInt32());
        foreach (var (name, v) in root["systems"].AsGodotDictionary())
        {
            var a = v.AsGodotArray();
            _cells[name.AsString()] = (a[0].AsInt32(), a[1].AsString());
        }
        _sheet = GD.Load<Texture2D>(Sheet);
    }

    /// <summary>The picture of a system's planet, or null.</summary>
    public static Texture2D Of(string system)
    {
        Load();
        if (_sheet == null || system == null || !_cells.TryGetValue(system, out var c)) return null;
        if (_cache.TryGetValue(system, out var t)) return t;
        t = new AtlasTexture { Atlas = _sheet, Region = new Rect2(c.Cell % _cols * _size, c.Cell / _cols * _size, _size, _size) };
        _cache[system] = t;
        return t;
    }

    /// <summary>The side of one picture in dots (pixels of the sheet).</summary>
    public static int Dots { get { Load(); return _size; } }

    /// <summary>The kind of world in plain words ("Gas giant", "Ice world"), or null.</summary>
    public static string KindOf(string system)
    {
        Load();
        if (system == null || !_cells.TryGetValue(system, out var c)) return null;
        return c.Kind switch
        {
            "terran" => "Temperate world",
            "ocean" => "Ocean world",
            "swamp" => "Swamp world",
            "desert" => "Desert world",
            "ice" => "Ice world",
            "rock" => "Barren rock",
            "lava" => "Lava world",
            "city" => "City world",
            "locked" => "Tidally locked",
            "gas" => "Gas giant",
            "ring" => "Ring world",
            "shell" => "Shell world",
            _ => null,
        };
    }
}
