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

    static Texture2D _sheet, _harvested;
    static Dictionary<string, (int Cell, string Kind)> _cells;
    static int _size = 128, _cols = 1;
    static readonly Dictionary<string, AtlasTexture> _cache = new(StringComparer.Ordinal), _harvestedCache = new(StringComparer.Ordinal);

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
        // the remnant each system leaves after a harvest, same cells on a second sheet (atlas.json "harvested"; Tool maker, PR #242)
        string h = root.ContainsKey("harvested") ? "res://art/planets/" + root["harvested"].AsString() : null;
        if (h != null && ResourceLoader.Exists(h)) _harvested = GD.Load<Texture2D>(h);
        else GD.PushWarning("Harvested planet pictures missing: " + (h ?? "no \"harvested\" key in " + Index));
    }

    /// <summary>The picture of a system's planet, or null.</summary>
    public static Texture2D Of(string system) => Cell(false, system);

    /// <summary>What is left of a system's planet after a harvest (a gutted core in a belt of rubble), or null.</summary>
    public static Texture2D HarvestedOf(string system) => Cell(true, system);

    static Texture2D Cell(bool harvested, string system)
    {
        Load();
        var (sheet, cache) = harvested ? (_harvested, _harvestedCache) : (_sheet, _cache);
        if (sheet == null || system == null || !_cells.TryGetValue(system, out var c)) return null;
        if (cache.TryGetValue(system, out var t)) return t;
        t = new AtlasTexture { Atlas = sheet, Region = new Rect2(c.Cell % _cols * _size, c.Cell / _cols * _size, _size, _size) };
        cache[system] = t;
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
