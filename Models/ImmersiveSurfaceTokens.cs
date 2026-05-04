using Windows.UI;

namespace wisland.Models
{
    internal readonly record struct ImmersiveSurfaceTokens(
        Color OpaqueBackfillColor,
        Color HostSurfaceColor,
        Color LeftEdgeBackdropColor,
        Color GradientStartColor,
        Color GradientMidColor,
        Color GradientEndColor,
        Color ProgressAccentColor);
}
