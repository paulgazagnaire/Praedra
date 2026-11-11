using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace PraedraViewer;

/// <summary>
/// One system's planet settings as the Planet Maker draws them (the page's P), read from art/planets/planets-live.json. That file is
/// written by tools/planet-maker/export-game-params.js from the page itself (Paul's saved looks, else the lore sheet's preset, with the
/// moons the page fills in), so nothing of the page's merging is redone here: "base" holds the shared values and each system only what
/// differs. Drawing only: nothing here reads or changes the game.
/// </summary>
public sealed class PlanetLook
{
    const string Path = "res://art/planets/planets-live.json";

    public sealed class Ring
    {
        public bool On; public string Type = "broad";
        public double Inner, Width, Op, Gaps, Struct, Clump, Dust, Feeder, Seed, Spin;
        public string C1 = "#d8c8a4", C2 = "#7a6a55";
        public Ring Clone() => (Ring)MemberwiseClone();
    }

    public sealed class Moon
    {
        public string Type = "rock";
        public double Size, Orbit, Speed, Tilt, Phase, Detail, Feat, Haze, Shape, Spin, Lit, Seed;
        public string C1 = "#9b968e", C2 = "#4d4a46", C3 = "#bcc4d0";
    }

    public string Kind = "terran";
    public double Seed;
    public string[] Pal = { "#000000", "#000000", "#000000", "#000000", "#000000" };
    public string HazeCol = "#aab4c4", LCol = "#ff9e47", FStyle = "domes";
    public double Sea, Mount, Scale = 1, Ice, Clouds, Haze, Wet, Cities, Plan = 0.5, Rad = 6371, Lava, Craters, Scars;
    public double? Land;
    public double LUnder, LSea, LDark;
    public double Bands = 9, Turb, Storms, StormSize = 1;
    public double Spin, CloudSpeed, Flow, MoonSpeed, Tilt = 22, Roll, Sun, SunH, Zoom = 1;
    public Ring R = new();
    public double Moons;
    public List<Moon> MoonList = new();
    // a ring or a shell world
    public double MLayers, MOpen, MCap, MWidth, MWall, MSpan, MShade, MPlate, MLights, MStar, MPorts, MLook;
    public string MSun = "star", MHull = "#8b9199", MAcc = "#e0a64a";
    // orbital ring and space elevator
    public bool OOn, EOn;
    public double OHt, OWid, OLit, ELon, EHt, EN;
    public string OHull = "#8b9199", OAcc = "#e0a64a";

    static Dictionary<string, PlanetLook> _all;

    /// <summary>The settings of a system's planet, or null if the file has none (the tile then keeps its baked picture).</summary>
    public static PlanetLook Of(string system)
    {
        Load();
        return system != null && _all.TryGetValue(system, out var l) ? l : null;
    }

    static void Load()
    {
        if (_all != null) return;
        _all = new Dictionary<string, PlanetLook>(StringComparer.Ordinal);
        using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning($"{Path}: {FileAccess.GetOpenError()}; the sector window shows the baked planet pictures"); return; }
        try
        {
            var root = JsonNode.Parse(f.GetAsText())!.AsObject();
            if (root["format"]?.GetValue<string>() != "praedra-planets-live") throw new InvalidOperationException("not live planet settings");
            var b = root["base"]!.AsObject();
            foreach (var (name, own) in root["systems"]!.AsObject())
            {
                var o = (JsonObject)b.DeepClone();
                foreach (var (k, v) in own!.AsObject()) o[k] = v?.DeepClone();
                _all[name] = Read(o);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            GD.PushWarning($"{Path}: {e.Message}; the sector window shows the baked planet pictures");
            _all.Clear();
        }
    }

    // JavaScript truthiness of a number or a boolean (the page keeps oOn and eOn as 0/1, a harvested copy as false)
    static bool Truthy(JsonNode n) => n is JsonValue v && (v.TryGetValue(out bool b) ? b : v.TryGetValue(out double d) && d != 0 && !double.IsNaN(d));
    static double Num(JsonObject o, string k, double dflt = 0) => o[k] is JsonValue v && v.TryGetValue(out double d) ? d : dflt;
    static string Str(JsonObject o, string k, string dflt) => o[k] is JsonValue v && v.TryGetValue(out string s) ? s : dflt;

    static PlanetLook Read(JsonObject o)
    {
        var p = new PlanetLook
        {
            Kind = Str(o, "kind", "terran"), Seed = Num(o, "seed"),
            HazeCol = Str(o, "hazeCol", "#aab4c4"), LCol = Str(o, "lCol", "#ff9e47"), FStyle = Str(o, "fStyle", "domes"),
            Sea = Num(o, "sea"), Mount = Num(o, "mount"), Scale = Num(o, "scale", 1), Ice = Num(o, "ice"), Clouds = Num(o, "clouds"), Haze = Num(o, "haze"),
            Wet = Num(o, "wet"), Cities = Num(o, "cities"), Plan = Num(o, "plan", 0.5), Rad = Num(o, "rad", 6371), Lava = Num(o, "lava"),
            Craters = Num(o, "craters"), Scars = Num(o, "scars"),
            Land = o["land"] is JsonValue lv && lv.TryGetValue(out double land) ? land : null,
            LUnder = Num(o, "lUnder"), LSea = Num(o, "lSea"), LDark = Num(o, "lDark"),
            Bands = Num(o, "bands", 9), Turb = Num(o, "turb"), Storms = Num(o, "storms"), StormSize = Num(o, "stormSize", 1),
            Spin = Num(o, "spin"), CloudSpeed = Num(o, "cloudSpeed"), Flow = Num(o, "flow"), MoonSpeed = Num(o, "moonSpeed"),
            Tilt = Num(o, "tilt", 22), Roll = Num(o, "roll"), Sun = Num(o, "sun"), SunH = Num(o, "sunH"), Zoom = Num(o, "zoom", 1),
            Moons = Num(o, "moons"),
            MLayers = Num(o, "mLayers"), MOpen = Num(o, "mOpen"), MCap = Num(o, "mCap"), MWidth = Num(o, "mWidth"), MWall = Num(o, "mWall"),
            MSpan = Num(o, "mSpan"), MShade = Num(o, "mShade"), MPlate = Num(o, "mPlate"), MLights = Num(o, "mLights"), MStar = Num(o, "mStar"),
            MPorts = Num(o, "mPorts"), MLook = Num(o, "mLook"), MSun = Str(o, "mSun", "star"), MHull = Str(o, "mHull", "#8b9199"), MAcc = Str(o, "mAcc", "#e0a64a"),
            OOn = Truthy(o["oOn"]), EOn = Truthy(o["eOn"]),
            OHt = Num(o, "oHt"), OWid = Num(o, "oWid"), OLit = Num(o, "oLit"), ELon = Num(o, "eLon"), EHt = Num(o, "eHt"), EN = Num(o, "eN", 1),
            OHull = Str(o, "oHull", "#8b9199"), OAcc = Str(o, "oAcc", "#e0a64a"),
        };
        if (o["pal"] is JsonArray pal) p.Pal = pal.Select(c => c?.GetValue<string>() ?? "#000000").Concat(Enumerable.Repeat("#000000", 5)).Take(5).ToArray();
        if (o["ring"] is JsonObject r)
            p.R = new Ring
            {
                On = Truthy(r["on"]), Type = Str(r, "type", "broad"), Inner = Num(r, "inner"), Width = Num(r, "width"), Op = Num(r, "op"),
                Gaps = Num(r, "gaps"), Struct = Num(r, "struct"), Clump = Num(r, "clump"), Dust = Num(r, "dust"), Feeder = Num(r, "feeder"),
                Seed = Num(r, "seed"), Spin = Num(r, "spin"), C1 = Str(r, "c1", "#d8c8a4"), C2 = Str(r, "c2", "#7a6a55"),
            };
        if (o["moonList"] is JsonArray ml)
            foreach (var mn in ml.OfType<JsonObject>())
                p.MoonList.Add(new Moon
                {
                    Type = Str(mn, "type", "rock"), Size = Num(mn, "size"), Orbit = Num(mn, "orbit"), Speed = Num(mn, "speed"), Tilt = Num(mn, "tilt"),
                    Phase = Num(mn, "phase"), Detail = Num(mn, "detail"), Feat = Num(mn, "feat"), Haze = Num(mn, "haze"), Shape = Num(mn, "shape"),
                    Spin = Num(mn, "spin"), Lit = Num(mn, "lit"), Seed = Num(mn, "seed"),
                    C1 = Str(mn, "c1", "#9b968e"), C2 = Str(mn, "c2", "#4d4a46"), C3 = Str(mn, "c3", "#bcc4d0"),
                });
        return p;
    }

    public PlanetLook Clone()
    {
        var c = (PlanetLook)MemberwiseClone();
        c.Pal = (string[])Pal.Clone();
        c.R = R.Clone();
        c.MoonList = new List<Moon>(MoonList);
        return c;
    }
}
