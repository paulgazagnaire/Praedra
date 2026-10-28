using System;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The galaxy drawn behind the map's sectors and systems (Paul 2026-10-28). It is a child of the <see cref="GalaxyView"/> drawn
/// behind its parent, and follows the view's pan and zoom. The picture itself is the shader GalaxyBackdrop.gdshader; this node
/// feeds it the view transform and a small map of where the systems are, so the bright parts of the galaxy are where the sectors are.
/// Drawing only: nothing here reads or changes the game.
/// </summary>
public partial class GalaxyBackdrop : Control
{
    // the approved two-arm spiral the systems were laid out on (specs/creative/galaxy-shape/README.md, build_gal.py):
    // disc radius 480 units, arm pitch 17 degrees, arms start at 67 degrees; the winding is 1/tan(pitch) * 0.35 per e-fold of radius
    const float DiscRadius = 480f;
    const float ArmPhaseDeg = 67f;
    const float PitchDeg = 17f;
    const float WindScale = 0.35f;
    const int DensitySize = 128;      // the density map's pixels per side, over the centre +- 1.15 disc radii
    const float DensitySigma = 0.055f; // the blur of each system on the density map, in disc radii

    readonly GalaxyView _view;
    readonly ShaderMaterial _material;
    readonly ColorRect _canvas;
    readonly SubViewport _buffer;

    public GalaxyBackdrop(GalaxyView view, Vector2 centre, ReadOnlySpan<Vector2> systems)
    {
        _view = view;
        MouseFilter = MouseFilterEnum.Ignore;
        ShowBehindParent = true;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Scripts/GalaxyBackdrop.gdshader") };
        _material.SetShaderParameter("galaxy_center", centre);
        _material.SetShaderParameter("galaxy_radius", DiscRadius);
        _material.SetShaderParameter("arm_phase", Mathf.DegToRad(ArmPhaseDeg));
        _material.SetShaderParameter("arm_wind", WindScale / Mathf.Tan(Mathf.DegToRad(PitchDeg)));
        _material.SetShaderParameter("background", Ui.MapBg);
        _material.SetShaderParameter("density", DensityMap(centre, systems));
        // the picture is costly per pixel, so it is drawn into its own buffer only when the view moves, and shown from there every frame
        _canvas = new ColorRect { Material = _material, MouseFilter = MouseFilterEnum.Ignore };
        _buffer = new SubViewport { Disable3D = true, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Once };
        _buffer.AddChild(_canvas);
        AddChild(_buffer);
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
        // the buffer has one texel per screen pixel (Retina screens have two per point)
        float k = Mathf.Max(1f, GetScreenTransform().Scale.X);
        var px = new Vector2I(Math.Max(1, (int)Mathf.Ceil(Size.X * k)), Math.Max(1, (int)Mathf.Ceil(Size.Y * k)));
        if (_buffer.Size != px) _buffer.Size = px;
        _canvas.Size = px;
        _material.SetShaderParameter("view_size", Size);
        _material.SetShaderParameter("view_center", _view.ViewCenter);
        _material.SetShaderParameter("px_per_unit", _view.PxPerUnit);
        _material.SetShaderParameter("pixel_scale", px.X / Mathf.Max(1f, Size.X));
        _buffer.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        DrawTextureRect(_buffer.GetTexture(), new Rect2(Vector2.Zero, Size), false);
    }
}
