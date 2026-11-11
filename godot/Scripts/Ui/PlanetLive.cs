using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>
/// One planet drawn live by the Planet Maker's own shader (PlanetLive.gdshader, generated from the tool by
/// tools/planet-maker/port-godot-shader.py), for the sector window's tiles (Paul 2026-11-11: "in the sector window. the planets need to
/// be animated, like in the planet maker tool, and following the parameters of each planets"). It is a small buffer the size of the
/// baked thumbnail (96 x 96 dots, one pixel per dot), drawn in the thumbnails' style (dither on, Grain, the planet's own colours,
/// see-through sky, framed on the planet and its rings: tools/planet-maker/bake-thumbs.js), so the tile shows it exactly as it showed the
/// baked picture: nearest filtering, a whole number of screen pixels per dot. The shader therefore runs on 9,216 pixels per planet,
/// whatever the screen. <see cref="Advance"/> moves the clock and asks for one new frame; with no call, nothing is drawn.
/// Every number handed to the shader is worked out as the tool's drawP() does (planet-maker.src.html), line for line.
/// Drawing only: nothing here reads or changes the game.
/// </summary>
public partial class PlanetLive : SubViewport
{
    const double D = Math.PI / 180;
    const int MaxMoons = 8;                 // MAX_MOONS: the shader's moon arrays hold this many
    const string BaseLCol = "#ff9e47";      // BASE.lCol: warm sodium city lights
    const string ShaderPath = "res://Scripts/Ui/PlanetLive.gdshader";

    // the thumbnails' dither (bake-thumbs.js: setDither({ on: true, pat: 'grain', pal: 'own', dot: 1, contrast: 1.15, bright: 0.02, levels: 4 }))
    const int DitherGrain = 3;              // DI_PATS: fine, coarse, halftone, grain
    const float DitherContrast = 1.15f, DitherBright = 0.02f, DitherLevels = 4f;
    const int Quality = 1;                  // High detail, as the thumbnails (grab() draws at quality 1)

    readonly PlanetLook _p;                 // what is drawn (the harvested remnant's own settings when harvested)
    readonly PlanetLook _own;               // the system's own settings: the motion's rates come from them, as the tool's rates()
    readonly bool _harvested;
    readonly ShaderMaterial _mat;
    readonly ColorRect _canvas;
    readonly int _dots;
    // the tool's T: how far each motion has run
    double _spin, _cloud, _flow, _ring, _moon, _time;
    // fixed for this planet: the camera and the moons
    Vector3 _u, _a, _w;
    List<PlanetLook.Moon> _mm;

    public PlanetLive(PlanetLook look, bool harvested, int dots)
    {
        _own = look;
        _harvested = harvested;
        _dots = dots;
        // the thumbnails' framing: the tool pulls back to fit the farthest moon; a thumbnail frames the planet and its rings instead
        var p = look.Clone();
        bool mega0 = IsMega(p.Kind);
        double ringFit = p.R.On && !mega0 ? (p.R.Inner + p.R.Width) * 1.04 : 0;
        double moonFit = harvested ? 0 : p.MoonList.Take(MoonCount(p)).Select(o => o.Orbit + o.Size).DefaultIfEmpty(0).Max();
        p.Zoom = p.Zoom * Math.Max(Math.Max(1.3, ringFit), moonFit * 0.95) / Math.Max(1.3, ringFit);
        _p = harvested ? HarvestedP(p) : p;

        Size = new Vector2I(dots, dots);
        TransparentBg = true;               // the tile's own dark ground shows round the planet
        Disable3D = true;
        RenderTargetUpdateMode = UpdateMode.Once;
        _mat = new ShaderMaterial { Shader = Variant(VariantKey()) };
        _canvas = new ColorRect { Material = _mat, Size = new Vector2(dots, dots), MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_canvas);
        Fixed();
        Moving();
    }

    /// <summary>Run the clock on by <paramref name="dt"/> seconds (at most 0.1, as the tool's loop) and draw one new frame.</summary>
    public void Advance(double dt)
    {
        dt = Math.Min(0.1, dt);
        // rates(): how fast each motion runs, per second (from the system's own settings, a harvested remnant included)
        var o = _own;
        _spin += (o.Kind == "locked" ? 0 : o.Spin * 0.5) * dt;
        _cloud += o.CloudSpeed * 0.3 * dt;
        _flow += o.Flow * 0.25 * dt;
        _ring += (_harvested ? (o.R.Spin != 0 ? o.R.Spin : 0.3) : o.R.Spin) * 0.3 * dt;
        _moon += o.MoonSpeed * dt;
        _time += dt;
        Moving();
        RenderTargetUpdateMode = UpdateMode.Once;
    }

    // ================================================================== the tool's tables

    static bool IsMega(string k) => k == "ring" || k == "shell";
    static bool HasSea(string k) => k is "terran" or "ocean" or "swamp" or "city" or "ice" or "locked" or "ring";
    static bool ElevOk(string k) => k != "gas" && k != "locked" && !IsMega(k);
    static int KindType(string k) => k == "gas" ? 1 : k == "locked" ? 2 : IsMega(k) ? 3 : 0;
    static readonly string[] FStyles = { "domes", "great", "equator", "rafts", "refinery" };
    static readonly string[] Centres = { "star", "lamp", "core" };
    static readonly string[] MoonTypes = { "rock", "dirty", "ice", "volcanic", "hazy", "frozen", "asteroid", "living", "industrial" };
    // RING_TYPES: id, whether the kind has a Gaps and a Clumps slider (L.gaps, L.clump), whether it belongs to a moon (feed)
    static readonly (string Id, bool Gaps, bool Clump, bool Feed)[] RingTypes =
    {
        ("broad", true, true, false), ("narrow", false, false, false), ("dusty", false, false, true),
        ("arcs", false, true, true), ("wreck", true, true, true), ("moonfed", false, false, true),
    };
    // CLOUD_COL and LOW_CLOUD: what a world's clouds are made of, and how much of the low deck a frozen world keeps
    static Vector3 CloudCol(string k) => k switch
    {
        "lava" => new(0.36f, 0.31f, 0.28f), "desert" => new(0.86f, 0.68f, 0.48f), "ice" => new(0.62f, 0.78f, 1.0f), _ => new(0.96f, 0.97f, 1.0f),
    };
    static float LowCloud(string k) => k == "ice" ? 0.55f : 1f;
    // RESONANCES: where a moon outside a ring clears gaps (share of its distance, half width, half width per moon size, depth)
    static readonly double[][] Resonances = { new[] { 0.63, 0.012, 0.5, 0.85 }, new[] { 0.763, 0.006, 0.2, 0.45 }, new[] { 0.481, 0.006, 0.25, 0.55 } };

    static int RingIdx(string type) => Math.Max(0, Array.FindIndex(RingTypes, t => t.Id == type));
    static int MoonCount(PlanetLook p) => Math.Clamp((int)Math.Round(p.Moons, MidpointRounding.AwayFromZero), 0, MaxMoons);
    static bool IsHex(string s) => s != null && s.Length == 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
    static Vector3 Rgb(string hex) => IsHex(hex) ? GalaxyLook.Rgb(hex) : Vector3.Zero;

    // population (the tool's popBillions, legacyAmt, popBelowT, landShare, cityStats)
    static double LegacyAmt(double pos) => pos > 0 ? Math.Max(0.0005, Math.Min(1, (11 * pos - 5) / 6)) : 0;
    static double PopBelowT(double pos) => pos > 0 ? Math.Min(1, Math.Max(0, 11 * pos / 5)) : 1;
    static double LandShare(PlanetLook p) => p.Land is double l ? Math.Max(0.02, l) : HasSea(p.Kind) ? Math.Min(1, Math.Max(0.04, 1 - 1.2 * p.Sea)) : 1;
    static double DensLog(PlanetLook p)
    {
        if (IsMega(p.Kind)) return 1.2 + 3.8 * p.Cities;
        double b = p.Cities > 0 ? Math.Pow(10, -7 + 11 * p.Cities) : 0, area = 4 * Math.PI * p.Rad * p.Rad * LandShare(p);
        double dens = b * 1e9 / area;
        if (double.IsNaN(dens) || double.IsInfinity(dens)) dens = 0;
        return Math.Min(8, Math.Log10(Math.Max(dens, 0.001)));
    }
    static double OrbRadius(PlanetLook p) => 1 + Math.Max(p.OHt, p.OWid * 0.5 * 0.8 + 0.012);

    /// <summary>harvestedP(): what a harvester leaves of a world, drawn from its own seed and colours (the tool's "Show it harvested").</summary>
    static PlanetLook HarvestedP(PlanetLook p)
    {
        var h = p.Clone();
        h.R = new PlanetLook.Ring
        {
            On = true, Type = "wreck", C1 = "#9b9184", C2 = "#4f483f", Feeder = 0, Inner = 0.6, Width = 0.55, Op = 0.4, Gaps = 0.6, Struct = 0.9,
            Clump = 1, Dust = 0.15, Seed = p.Seed, Spin = p.R.Spin != 0 ? p.R.Spin : 0.3,
        };
        h.Kind = "rock"; h.Moons = 0; h.MoonList = new(); h.Clouds = 0; h.Haze = 0; h.Cities = 0; h.OOn = false; h.EOn = false;
        h.LUnder = 0; h.LSea = 0; h.LDark = 0;
        return h;
    }

    // ================================================================== shader variants

    // The tool compiles its shader in variants so that work a planet does not use costs nothing; so does the game, once per variant.
    static readonly Dictionary<string, Shader> _variants = new(StringComparer.Ordinal);
    static string _code;

    string VariantKey()
    {
        var p = _p;
        bool mega = IsMega(p.Kind), orb = !mega && (p.OOn || (p.EOn && ElevOk(p.Kind)));
        double clouds = p.Kind == "rock" ? 0 : p.Clouds;
        return (p.Kind != "gas" && clouds > 0.01 ? "C" : "") + (Quality == 1 ? "H" : "") + (MoonCount(p) > 0 ? "M" : "") + (p.R.On && !mega ? "R" : "")
            + (mega ? "G" : "") + (orb ? "O" : "") + "D" + (_harvested ? "X" : "");
    }

    static Shader Variant(string key)
    {
        if (_variants.TryGetValue(key, out var s)) return s;
        _code ??= FileAccess.FileExists(ShaderPath) ? FileAccess.GetFileAsString(ShaderPath) : GD.Load<Shader>(ShaderPath).Code;
        var defs = string.Concat(key.Select(c => c switch
        {
            'C' => "#define PM_CLOUDS\n", 'H' => "#define PM_HIQ\n", 'M' => "#define PM_MOONS\n", 'R' => "#define PM_RINGS\n",
            'G' => "#define PM_MEGA\n", 'O' => "#define PM_ORB\n", 'D' => "#define PM_DITHER\n", 'X' => "#define PM_HARV\n", _ => "",
        }));
        const string mark = "render_mode unshaded, blend_disabled;\n";
        int at = _code.IndexOf(mark, StringComparison.Ordinal);
        s = new Shader { Code = at < 0 ? _code : _code.Insert(at + mark.Length, defs) };
        _variants[key] = s;
        return s;
    }

    static ImageTexture _blue;
    static ImageTexture BlueNoise() => _blue ??= ImageTexture.CreateFromImage(Image.CreateFromData(64, 64, false, Image.Format.R8, GalaxyLook.BlueNoise(64)));

    // ================================================================== drawP(): the uniforms

    void Set(string name, Variant v) => _mat.SetShaderParameter(name, v);

    /// <summary>The part of drawP() that does not change while the planet turns.</summary>
    void Fixed()
    {
        var p = _p;
        double w = _dots, h = _dots;
        double phi = p.Tilt * D, rl = p.Roll * D;
        var U = new[] { Math.Cos(rl), Math.Sin(rl), 0 };
        var A = new[] { -Math.Sin(rl) * Math.Cos(phi), Math.Cos(rl) * Math.Cos(phi), Math.Sin(phi) };
        var W = Cross(U, A);
        double sd = p.Sun * D, sh = p.SunH * D;
        var L = new[] { Math.Sin(sd) * Math.Cos(sh), Math.Sin(sh), Math.Cos(sd) * Math.Cos(sh) };
        double ringOut = p.R.Inner + p.R.Width;
        bool mega = IsMega(p.Kind);
        _mm = mega ? new() : p.MoonList.Take(MoonCount(p)).ToList();
        double mx = _mm.Select(o => o.Orbit + o.Size).DefaultIfEmpty(0).Max();
        bool orbOn = !mega && p.OOn, elevOn = !mega && p.EOn && ElevOk(p.Kind), orb = orbOn || elevOn;
        double orbR = orbOn ? OrbRadius(p) : 0, orbH = p.OWid * 0.5, elevTop = elevOn ? 1 + p.EHt : 0;
        double orbExt = orbOn ? (orbR + orbH * 2.2 + 0.01) * 1.05 : 0, elevExt = elevTop * 1.05;
        double extent = Max(1.3, p.R.On && !mega ? ringOut * 1.04 : 0, mx * 0.95, orbExt, elevExt);
        double aspect = w / h;
        double extentV = Max(1.3, p.R.On && !mega ? ringOut * Math.Max(0.72, Math.Sin(phi)) * 1.04 : 0, mx * 0.75, orbExt * Math.Max(0.72, Math.Sin(phi)), elevExt);
        double look = p.Kind == "ring" ? p.MLook : 0;
        var farS = new double[] { 0, 0 };
        if (look > 0) { var wv = Cross(U, A); double sg = wv[2] > 0 ? -1 : 1; farS[0] = sg * wv[0] * look; farS[1] = sg * wv[1] * look; }
        double zoom = Math.Min(aspect * 0.92 / extent, 0.92 / extentV) * p.Zoom * (1 + 3.0 * look);
        double clouds = p.Kind == "rock" ? 0 : p.Clouds;   // an airless world has no weather, whatever the slider says

        Set("uRes", new Vector2((float)w, (float)h)); Set("uZoom", (float)zoom); Set("uShift", new Vector2((float)farS[0], (float)farS[1]));
        Set("uType", KindType(p.Kind));
        Set("uPal", p.Pal.Select(Rgb).ToArray());
        Set("uHazeCol", Rgb(p.HazeCol));
        Set("uSeed", (float)(p.Seed % 997 * 0.173)); Set("uSea", (float)p.Sea); Set("uMount", (float)p.Mount); Set("uScale", (float)p.Scale);
        Set("uIce", (float)p.Ice); Set("uClouds", (float)clouds); Set("uAsh", p.Kind == "lava" ? 1f : 0f); Set("uLowCl", LowCloud(p.Kind));
        Set("uCloudCol", CloudCol(p.Kind)); Set("uHaze", (float)p.Haze);
        Set("uWet", (float)p.Wet); Set("uBiome", p.Kind is "rock" or "lava" ? 0f : 1f); Set("uQ", (float)Quality);
        Set("uCities", (float)(IsMega(p.Kind) ? p.Cities : LegacyAmt(p.Cities))); Set("uPopT", (float)(IsMega(p.Kind) ? 1 : PopBelowT(p.Cities)));
        Set("uDens", (float)DensLog(p)); Set("uPlan", (float)p.Plan); Set("uRad", (float)p.Rad); Set("uLava", (float)p.Lava);
        Set("uCraters", (float)p.Craters); Set("uScars", (float)p.Scars);
        int fs = Array.IndexOf(FStyles, p.FStyle);
        Set("uFStyle", (float)(fs > 0 ? fs : 0));
        Set("uLights", new Vector4((float)p.LUnder, HasSea(p.Kind) ? (float)p.LSea : 0f, (float)p.LDark, IsHex(p.LCol) && p.LCol.ToLowerInvariant() != BaseLCol ? 1f : 0f));
        Set("uLCol", Rgb(IsHex(p.LCol) ? p.LCol : BaseLCol));
        Set("uBands", (float)p.Bands); Set("uTurb", (float)p.Turb); Set("uStorms", (float)p.Storms); Set("uStormSize", (float)p.StormSize);
        Set("uStarDens", 0f);                // no stars: the tile is the sky (the thumbnails set starDens 0 too)
        Set("uLoop", Vector4.Zero);          // not a looping video
        Set("uBg", Vector3.Zero);            // the black sky of the see-through picture (seeThrough works out the white one)
        if (mega)
        {
            double wd = p.MWidth * 0.5;
            int mt = p.Kind == "shell" ? 1 : 0;
            Set("uMegaT", (float)mt);
            if (mt == 1)
            {
                Set("uMegaC", new Vector4((float)Math.Round(p.MLayers, MidpointRounding.AwayFromZero), (float)p.MOpen, (float)Math.Cos(Math.PI * p.MCap), 0));
                Set("uMegaD", new Vector4((float)Math.Round(14 + 24 * p.MPlate, MidpointRounding.AwayFromZero), 0, 1.12f, 0));
            }
            Set("uMegaE", new Vector4(mt == 1 ? 0 : Math.Max(0, Array.IndexOf(Centres, p.MSun)), mt == 1 ? 0 : (float)p.MPorts, 0, 0));
            Set("uMegaA", new Vector4((float)wd, (float)p.MWall, 0.02f, (float)p.MSpan));
            Set("uMegaB", new Vector4((float)p.MShade, (float)p.MPlate, (float)p.MLights, (float)p.MStar));
            Set("uMegaHull", Rgb(p.MHull)); Set("uMegaAcc", Rgb(p.MAcc));
        }
        _orbA = new Vector4(orbOn ? 1 : 0, (float)orbR, (float)orbH, 0);
        _orb = orb; _elevOn = elevOn; _elevTop = elevTop;
        if (orb) { Set("uOrbA", _orbA); Set("uOrbHull", Rgb(p.OHull)); Set("uOrbAcc", Rgb(p.OAcc)); }
        _u = V(U); _a = V(A); _w = V(W);
        Set("uAxis", _a); Set("uU", _u); Set("uW", _w); Set("uL", V(L));
        var rg = p.R;
        var rt = RingTypes[RingIdx(rg.Type)];
        Set("uRingOn", rg.On && !mega ? 1f : 0f); Set("uRingIn", (float)rg.Inner); Set("uRingOut", (float)ringOut); Set("uRingOp", (float)rg.Op);
        Set("uRingGaps", rt.Gaps ? (float)rg.Gaps : 0f); Set("uRingStruct", (float)rg.Struct); Set("uRingClump", rt.Clump ? (float)rg.Clump : 0f);
        Set("uRingDust", (float)rg.Dust); Set("uRingSeed", (float)(rg.Seed % 997 * 0.173)); Set("uRingType", RingIdx(rg.Type));
        Set("uRingCol", Rgb(rg.C1)); Set("uRingCol2", Rgb(rg.C2));
        // the gaps moons (and an orbital ring) cut in the ring: they depend on the orbits, not on where the moons are now
        var gaps = rg.On ? RingGapList(_mm, rg, rt.Gaps && rg.Gaps > 0, orbOn ? new[] { orbR, orbH + 0.05 } : null) : new List<double[]>();
        var gv = new Vector4[6];
        for (int i = 0; i < gaps.Count; i++) gv[i] = new Vector4((float)gaps[i][0], (float)gaps[i][1], (float)gaps[i][2], (float)gaps[i][3]);
        Set("uRGapN", gaps.Count); Set("uRGap", gv);
        // the dither of the thumbnails: Grain, the planet's own colours (an empty palette)
        Set("uDiA", new Vector4(DitherGrain, 0, DitherContrast, DitherBright));
        Set("uDiLv", DitherLevels);
        Set("uDiPal", new Vector3[4]);
        Set("uBlue", BlueNoise());
    }

    Vector4 _orbA;
    bool _orb, _elevOn;
    double _elevTop;

    /// <summary>The part of drawP() that moves: the clock, the cables, the moons and the moon a ring belongs to.</summary>
    void Moving()
    {
        var p = _p;
        Set("uSpin", (float)_spin); Set("uCloudAng", (float)_cloud); Set("uFlowT", (float)_flow); Set("uRingAng", (float)_ring); Set("uTime", (float)_time);
        Set("uCirAng", (float)(_cloud * 1.8 + 0.9));
        if (_orb) Set("uOrbB", new Vector4((float)p.OLit, _elevOn ? (float)Math.Round(p.EN, MidpointRounding.AwayFromZero) : 0, (float)_elevTop, (float)(p.ELon * D + _spin)));
        // moons in world space, far to near (a stable sort, as JavaScript's)
        var ms = _mm.Select(m =>
        {
            double a = m.Phase * D + _moon * 0.35 * m.Speed / Math.Pow(m.Orbit, 1.5);
            double inc = m.Tilt * D;
            double x = Math.Cos(a) * m.Orbit, z0 = Math.Sin(a) * m.Orbit;
            double y = z0 * Math.Sin(inc), z = z0 * Math.Cos(inc);
            var wp = new Vector3((float)(_u.X * x + _a.X * y + _w.X * z), (float)(_u.Y * x + _a.Y * y + _w.Y * z), (float)(_u.Z * x + _a.Z * y + _w.Z * z));
            return (wp, m, a);
        }).OrderBy(o => o.wp.Z).Take(MaxMoons).ToList();
        if (ms.Count > 0 || p.R.On)
        {
            var mv = new Vector4[MaxMoons]; var mp = new Vector4[MaxMoons]; var mq = new Vector4[MaxMoons];
            var mc1 = new Vector3[MaxMoons]; var mc2 = new Vector3[MaxMoons]; var mc3 = new Vector3[MaxMoons];
            for (int i = 0; i < ms.Count; i++)
            {
                var (wp, m, a) = ms[i];
                mv[i] = new Vector4(wp.X, wp.Y, wp.Z, (float)m.Size);
                mp[i] = new Vector4(Math.Max(0, Array.IndexOf(MoonTypes, m.Type)), (float)m.Detail, (float)m.Feat, (float)m.Haze);
                mq[i] = new Vector4((float)m.Shape, (float)m.Seed, (float)(a + _moon * m.Spin * 0.5), (float)m.Lit);   // turning with the orbit keeps one face to the planet
                mc1[i] = Rgb(m.C1); mc2[i] = Rgb(m.C2); mc3[i] = Rgb(m.C3);
            }
            Set("uMoonN", ms.Count);
            Set("uMoon", mv); Set("uMoonP", mp); Set("uMoonQ", mq);
            Set("uMoonCol", mc1); Set("uMoonCol2", mc2); Set("uMoonCol3", mc3);
        }
        else Set("uMoonN", 0);
        // the moon a ring belongs to tells the ring where to bunch up (its orbit) and where along the orbit it is now
        var feed = Vector4.Zero;
        var rg = p.R;
        if (rg.On && RingTypes[RingIdx(rg.Type)].Feed && rg.Feeder > 0 && rg.Feeder <= _mm.Count)
        {
            var fm = _mm[(int)rg.Feeder - 1];
            foreach (var o in ms)
                if (ReferenceEquals(o.m, fm)) { feed = new Vector4((float)fm.Orbit, (float)Math.Atan2(Math.Sin(o.a) * Math.Cos(fm.Tilt * D), Math.Cos(o.a)), 1, 0); break; }
        }
        Set("uRingFeed", feed);
    }

    /// <summary>ringGapList(): the lanes moons sweep clear in a ring, the resonance gaps they cut, and a broad ring's two famous gaps.</summary>
    static List<double[]> RingGapList(List<PlanetLook.Moon> mm, PlanetLook.Ring ring, bool extras, double[] orb)
    {
        double lo = ring.Inner, hi = ring.Inner + ring.Width;
        var lanes = new List<double[]>(); var outp = new List<double[]>();
        for (int i = 0; i < mm.Count; i++)
        {
            var m = mm[i];
            if (ring.Feeder == i + 1) continue;   // the moon that feeds a ring lives in it and does not cut it
            double t = Math.Min(1, Math.Max(0, (Math.Abs(m.Tilt) - 10) / 20)), plane = 1 - t * t * (3 - 2 * t);
            if (plane <= 0.01) continue;
            if (m.Orbit > lo - 0.02 && m.Orbit < hi + 0.02) lanes.Add(new[] { m.Orbit, 0.018 + m.Size * 0.45, 0.95 * plane, 1 });
            if (!extras) continue;
            foreach (var r in Resonances) { double c = m.Orbit * r[0]; if (c > lo && c < hi) outp.Add(new[] { c, r[1] + m.Size * r[2], r[3] * plane, 0 }); }
        }
        if (orb != null && orb[0] > lo - 0.02 && orb[0] < hi + 0.02) lanes.Add(new[] { orb[0], orb[1], 1, 1 });
        outp = outp.OrderByDescending(g => g[1] * g[2]).ToList();   // stable, as JavaScript's sort
        if (extras && ring.Type == "broad")
        {
            bool Near(double a, double b) => lanes.Concat(outp).Any(g => g[0] > lo + a * ring.Width && g[0] < lo + b * ring.Width);
            if (!Near(0.5, 0.8)) outp.Add(new[] { lo + 0.655 * ring.Width, 0.04 * ring.Width, 0.92, 0 });
            if (!Near(0.82, 0.99)) outp.Add(new[] { lo + 0.905 * ring.Width, 0.008 * ring.Width + 0.003, 0.9, 0 });
        }
        return lanes.Concat(outp).Take(6).ToList();
    }

    static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    static Vector3 V(double[] v) => new((float)v[0], (float)v[1], (float)v[2]);
    static double Max(params double[] v) => v.Max();
}

/// <summary>
/// The sector window's live planets, kept across the window's rebuilds (it rebuilds its tiles a few times a second while the clock
/// runs, and a planet must not start its turn again each time). A planet is drawn only while this node is visible: closing the sector
/// window, or leaving the game window in the background, stops all drawing. Planets of systems no longer shown are freed.
/// </summary>
public partial class PlanetLiveSet : Control
{
    readonly Dictionary<string, PlanetLive> _live = new(StringComparer.Ordinal);
    bool _focused = true;

    public PlanetLiveSet() { MouseFilter = MouseFilterEnum.Ignore; Size = Vector2.Zero; }

    /// <summary>The live picture of a system's planet (its harvested remnant when <paramref name="harvested"/>), or null when there are no
    /// settings for it (the caller keeps the baked picture).</summary>
    public Texture2D Of(string system, bool harvested)
    {
        string key = (harvested ? "x:" : "") + system;
        if (_live.TryGetValue(key, out var pl)) return pl.GetTexture();
        var look = PlanetLook.Of(system);
        if (look == null) return null;
        pl = new PlanetLive(look, harvested, PlanetArt.Dots);
        _live[key] = pl;
        AddChild(pl);
        return pl.GetTexture();
    }

    /// <summary>Free the planets of systems not in <paramref name="shown"/> (a new sector, a system harvested since).</summary>
    public void KeepOnly(IEnumerable<string> shownKeys)
    {
        var keep = new HashSet<string>(shownKeys, StringComparer.Ordinal);
        foreach (var k in _live.Keys.Where(k => !keep.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            _live[k].QueueFree();
            _live.Remove(k);
        }
    }

    public static string Key(string system, bool harvested) => (harvested ? "x:" : "") + system;

    // redrawn at most 30 times a second, as the galaxy backdrop (GalaxyBackdrop.FrameSeconds): a 120 Hz screen would otherwise run the full
    // Planet Maker shader for every tile 120 times a second (Ponytail on #286)
    const double FrameSeconds = 1.0 / 30.0;
    double _sinceFrame;

    public override void _Process(double delta)
    {
        if (!_focused || !IsVisibleInTree()) return;
        _sinceFrame += delta;
        if (_sinceFrame < FrameSeconds) return;
        foreach (var pl in _live.Values) pl.Advance(_sinceFrame);   // the whole time since the last frame, so the speeds stay the tool's
        _sinceFrame = 0;
    }

    public override void _Notification(int what)
    {
        // nothing moves while the game window is in the background, so the shader does not keep the graphics card busy
        if (what == NotificationApplicationFocusOut) _focused = false;
        else if (what == NotificationApplicationFocusIn) _focused = true;
    }
}
