using Windows.UI;
using wisland.Helpers;

namespace wisland.Models
{
    internal static class ImmersiveSurfaceTokenFactory
    {
        private const double SettledBlurOpacity = 0.60;
        private const double NoBlurOpacity = 0.0;
        private const double DarkScrimOpacity = 0x59 / 255.0;

        public static readonly ImmersiveSurfaceTokens Default = FromPalette(
            AlbumArtPalette.Default,
            NoBlurOpacity);

        public static ImmersiveSurfaceTokens FromPalette(AlbumArtPalette palette)
            => FromPalette(palette, SettledBlurOpacity);

        private static ImmersiveSurfaceTokens FromPalette(AlbumArtPalette palette, double blurOpacity)
        {
            Color gradientStart = WindowSurfaceColorMath.ClampLuminance(palette.Dominant, 80);
            Color gradientMid = WindowSurfaceColorMath.ClampLuminance(palette.Secondary, 55);
            Color gradientEnd = WindowSurfaceColorMath.ClampLuminance(palette.Average, 65);

            // Match the actual immersive background stack before deriving
            // window colors: gradient plane, blurred art, then the dark scrim.
            // Mapping:
            // - leftEdge -> ResizeBackdropColor
            // - rightEdge + bottomEdge -> HostSurfaceColor/ResizeBackfillColor
            Color leftEdge = WindowSurfaceColorMath.ResolveVisibleBackgroundColor(
                WindowSurfaceColorMath.SampleGradient(gradientStart, gradientMid, gradientEnd, 0.25),
                palette.LeftEdge,
                blurOpacity,
                DarkScrimOpacity);
            Color rightEdge = WindowSurfaceColorMath.ResolveVisibleBackgroundColor(
                WindowSurfaceColorMath.SampleGradient(gradientStart, gradientMid, gradientEnd, 0.75),
                palette.RightEdge,
                blurOpacity,
                DarkScrimOpacity);
            Color bottomEdge = WindowSurfaceColorMath.ResolveVisibleBackgroundColor(
                WindowSurfaceColorMath.SampleGradient(gradientStart, gradientMid, gradientEnd, 0.75),
                palette.BottomEdge,
                blurOpacity,
                DarkScrimOpacity);
            // Do not clamp this darker like the old ambient backfill. During
            // immersive resize the surface needs to visually meet the right/bottom
            // artwork edges, so use the visible edge colors directly.
            Color edgeSurface = WindowSurfaceColorMath.ResolveEdgeSurfaceColor(rightEdge, bottomEdge);

            return new ImmersiveSurfaceTokens(
                edgeSurface,
                edgeSurface,
                leftEdge,
                gradientStart,
                gradientMid,
                gradientEnd,
                WindowSurfaceColorMath.EnsureBright(palette.Dominant));
        }
    }
}
