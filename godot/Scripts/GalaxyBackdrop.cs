using System;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The galaxy drawn behind the map's sectors and systems (Paul 2026-10-28). It is a child of the <see cref="GalaxyView"/> drawn
/// behind its parent, and follows the view's pan and zoom. The picture itself is the shader GalaxyBackdrop.gdshader; this node
/// feeds it the view transform, a small map of where the systems are (so the bright parts of the galaxy are where the sectors are)
/// and the look Paul tuned in the Galaxy Maker (<see cref="GalaxyLook"/>, Paul 2026-10-29), its dither and movement included.
/// Drawing only: nothing here reads or changes the game.
/// </summary>
public partial class GalaxyBackdrop : Control
{
    // the approved two-arm spiral the systems were laid out on (specs/creative/galaxy-shape/README.md, build_gal.py):
    // disc radius 480 units, arm pitch 17 degrees, arms start at 67 degrees; the winding is 1/tan(pitch) * 0.35 per e-fold of radius
    // (the arm start and pitch are now the look's: GalaxyLook "phase" and "pitch")
    const float DiscRadius = 480f;
    const float WindScale = 0.35f;
    const int DensitySize = 128;      // the density map's pixels per side, over the centre +- 1.15 disc radii
    const float DensitySigma = 0.055f; // the blur of each system on the density map, in disc radii

    readonly GalaxyView _view;
    readonly ShaderMaterial _material;
    readonly ColorRect _canvas;
    readonly SubViewport _buffer;
    readonly GalaxyLook _look;
    const float FrameSeconds = 1f / 30f; // with movement on, the picture is redrawn at most 30 times a second, as in the Galaxy Maker
    float _time, _sinceFrame;

    public GalaxyBackdrop(GalaxyView view, Vector2 centre, ReadOnlySpan<Vector2> systems)
    {
        _view = view;
        MouseFilter = MouseFilterEnum.Ignore;
        ShowBehindParent = true;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Scripts/GalaxyBackdrop.gdshader") };
        _material.SetShaderParameter("galaxy_center", centre);
        _material.SetShaderParameter("galaxy_radius", DiscRadius);
        _material.SetShaderParameter("density", DensityMap(centre, systems));
        _look = GalaxyLook.Load();
        ApplyLook(_look);
        // the dither's dots are whole buffer pixels shown enlarged, so they must not be smoothed
        if (_look.DitherOn) TextureFilter = TextureFilterEnum.Nearest;
        SetProcess(_look.Moves);
        // the picture is costly per pixel, so it is drawn into its own buffer when the view moves (and at most 30 times a second while
        // the gas and stars move), and shown from there every frame
        _canvas = new ColorRect { Material = _material, MouseFilter = MouseFilterEnum.Ignore };
        _buffer = new SubViewport { Disable3D = true, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Once };
        _buffer.AddChild(_canvas);
        AddChild(_buffer);
    }

    /// <summary>The look's numbers into the shader, as the Galaxy Maker's draw() hands them to its own.</summary>
    void ApplyLook(GalaxyLook l)
    {
        _material.SetShaderParameter("arm", new Vector4(Mathf.DegToRad(l["phase"]), WindScale / Mathf.Tan(Mathf.DegToRad(l["pitch"])), l["sharp"], l["wobble"]));
        _material.SetShaderParameter("light_mix", new Vector4(l["bulge"], l["bulgeSize"], l["disc"], l["arms"]));
        _material.SetShaderParameter("shape_mix", new Vector4(l["rim"], l["follow"], l["clouds"], l["knots"]));
        _material.SetShaderParameter("dust_mix", new Vector4(l["lanes"], l["web"], l["laneOff"], l["bright"]));
        _material.SetShaderParameter("star_mix", new Vector4(l["stars"], l["field"], l["starDens"], l["dim"]));
        _material.SetShaderParameter("move", new Vector4(l["drift"], l["breathe"], l["twinkle"], l["twShare"]));
        _material.SetShaderParameter("twinkle_speed", l["twSpeed"]);
        foreach (var (key, uniform) in new[] { ("cBulge", "c_bulge"), ("cDisc", "c_disc"), ("cArm", "c_arm"), ("cKnot", "c_knot"), ("cDust", "c_dust"), ("cBg", "c_bg") })
            _material.SetShaderParameter(uniform, l.Colour(key));
        _material.SetShaderParameter("dither_on", l.DitherOn);
        if (!l.DitherOn) return;
        _material.SetShaderParameter("dither_a", new Vector4(l.Pattern, l.Palette.Length, l.Contrast, l.Brightness));
        _material.SetShaderParameter("dither_levels", (float)l.Levels);
        var pal = new Vector3[4];
        for (int i = 0; i < l.Palette.Length; i++) pal[i] = GalaxyLook.Rgb(l.Palette[i]);
        _material.SetShaderParameter("dither_pal", pal);
        if (l.Pattern == 3) // Grain
        {
            const int N = 64;
            var img = Image.CreateFromData(N, N, false, Image.Format.R8, GalaxyLook.BlueNoise(N));
            _material.SetShaderParameter("blue_noise", ImageTexture.CreateFromImage(img));
        }
    }

    public override void _Process(double delta)
    {
        // the movement: the clock runs on its own, the picture is redrawn when a frame's worth of it has passed
        _time += (float)delta;
        _sinceFrame += (float)delta;
        if (_sinceFrame < FrameSeconds || !IsVisibleInTree()) return;
        _sinceFrame = 0f;
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        // the movement stops while the game window is in the background, so the shader does not keep the graphics card busy
        if (what == NotificationApplicationFocusOut) SetProcess(false);
        else if (what == NotificationApplicationFocusIn) SetProcess(_look.Moves);
    }

    /// <summary>A small greyscale picture of how crowded each part of the map is: every system adds a soft spot.</summary>
    static ImageTexture DensityMap(Vector2 centre, ReadOnlySpan<Vector2> systems)
    {
        var acc = new float[DensitySize * DensitySize];
        float span = 2.3f * DiscRadius; // the map covers centre +- 1.15 radii
        float sigmaPx = DensitySigma * DiscRadius / span * DensitySize;
        int reach = (int)Mathf.Ceil(sigmaPx * 3f);
        foreach (var s in systems)
        {
            var p = ((s - centre) / span + new Vector2(0.5f, 0.5f)) * DensitySize;
            int cx = (int)p.X, cy = (int)p.Y;
            for (int y = Math.Max(0, cy - reach); y <= Math.Min(DensitySize - 1, cy + reach); y++)
                for (int x = Math.Max(0, cx - reach); x <= Math.Min(DensitySize - 1, cx + reach); x++)
                {
                    float dx = x + 0.5f - p.X, dy = y + 0.5f - p.Y;
                    acc[y * DensitySize + x] += Mathf.Exp(-(dx * dx + dy * dy) / (2f * sigmaPx * sigmaPx));
                }
        }
        // scale so a typical crowded spot is 1 (the 95th percentile of the non-empty pixels), not the single busiest one
        var sorted = Array.FindAll(acc, v => v > 0.01f);
        Array.Sort(sorted);
        float top = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.95f)] : 1f;
        var img = Image.CreateEmpty(DensitySize, DensitySize, false, Image.Format.L8);
        for (int y = 0; y < DensitySize; y++)
            for (int x = 0; x < DensitySize; x++)
            {
                float v = Mathf.Clamp(acc[y * DensitySize + x] / top, 0f, 1f);
                img.SetPixel(x, y, new Color(v, v, v));
            }
        return ImageTexture.CreateFromImage(img);
    }

    public override void _Draw()
    {
        // the buffer has one texel per screen pixel (Retina screens have two per point); with the dither, one texel per dot of Dot screen
        // pixels, as in the Galaxy Maker. Screen pixels per layout pixel come from the window, which counts real pixels: the screen
        // transform leaves out the window's stretch, so on a Retina Mac it said 1 and the dots came out twice too big (Paul 2026-10-29).
        float screenPx = GetWindow().Size.X / Mathf.Max(1f, GetViewportRect().Size.X) * GetGlobalTransformWithCanvas().Scale.X;
        float k = Mathf.Max(1f, screenPx) / (_look.DitherOn ? _look.Dot : 1);
        var px = new Vector2I(Math.Max(1, (int)Mathf.Ceil(Size.X * k)), Math.Max(1, (int)Mathf.Ceil(Size.Y * k)));
        if (_buffer.Size != px) _buffer.Size = px;
        _canvas.Size = px;
        _material.SetShaderParameter("view_size", Size);
        _material.SetShaderParameter("view_center", _view.ViewCenter);
        _material.SetShaderParameter("px_per_unit", _view.PxPerUnit);
        _material.SetShaderParameter("pixel_scale", px.X / Mathf.Max(1f, Size.X));
        _material.SetShaderParameter("time", _time);
        _buffer.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        DrawTextureRect(_buffer.GetTexture(), new Rect2(Vector2.Zero, Size), false);
    }
}
