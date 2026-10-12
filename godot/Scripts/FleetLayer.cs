using Godot;

namespace RebellionViewer;

/// <summary>
/// Placeholder layer that sits on top of the galaxy map. It uses the parent GalaxyView's
/// transform (WorldToScreen) so anything drawn here stays locked to the map.
///
/// TODO (second pass, after m1/fleets is merged): read the core's fleet and movement state
/// and draw each fleet: parked ones as a marker at their system, travelling ones as a marker
/// interpolated along the lane between the two systems (days travelled / hop days), coloured
/// by side. Read state only; orders go through the core. Nothing is drawn yet.
/// </summary>
public partial class FleetLayer : Control
{
    public GalaxyView Map => GetParent<GalaxyView>();

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Draw()
    {
        // TODO fleets: see class comment. Intentionally empty in the first pass.
    }
}
