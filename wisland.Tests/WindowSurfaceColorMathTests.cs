using Windows.UI;
using wisland.Helpers;
using wisland.Models;
using Xunit;

namespace wisland.Tests
{
    public sealed class WindowSurfaceColorMathTests
    {
        [Fact]
        public void CreateOpaquePreservesRgbAndForcesAlpha()
        {
            Color color = Color.FromArgb(42, 10, 20, 30);

            Color opaque = WindowSurfaceColorMath.CreateOpaque(color);

            Assert.Equal(255, opaque.A);
            Assert.Equal(color.R, opaque.R);
            Assert.Equal(color.G, opaque.G);
            Assert.Equal(color.B, opaque.B);
        }

        [Fact]
        public void CompatBackdropFallsBackToSurfaceWhenProgressIsHidden()
        {
            Color surface = Color.FromArgb(132, 18, 22, 28);
            Color progress = Color.FromArgb(44, 214, 223, 235);

            Color backdrop = WindowSurfaceColorMath.ResolveCompatProgressStartBackdropColor(
                surface,
                progress,
                isProgressVisible: false);

            Assert.Equal(WindowSurfaceColorMath.CreateOpaque(surface), backdrop);
        }

        [Fact]
        public void CompatBackdropMatchesProgressWhenProgressIsOpaque()
        {
            Color surface = Color.FromArgb(132, 18, 22, 28);
            Color progress = Color.FromArgb(255, 214, 223, 235);

            Color backdrop = WindowSurfaceColorMath.ResolveCompatProgressStartBackdropColor(
                surface,
                progress,
                isProgressVisible: true);

            Assert.Equal(progress, backdrop);
        }

        [Fact]
        public void ImmersiveTokenFactorySeparatesLeftBackdropFromRightBottomSurface()
        {
            AlbumArtPalette palette = new(
                Color.FromArgb(255, 120, 80, 60),
                Color.FromArgb(255, 80, 90, 100),
                Color.FromArgb(255, 70, 75, 80),
                Color.FromArgb(255, 240, 0, 0),
                Color.FromArgb(255, 0, 240, 0),
                Color.FromArgb(255, 0, 0, 240));

            ImmersiveSurfaceTokens tokens = ImmersiveSurfaceTokenFactory.FromPalette(palette);

            Assert.Equal(tokens.HostSurfaceColor, tokens.OpaqueBackfillColor);
            Assert.NotEqual(tokens.LeftEdgeBackdropColor, tokens.HostSurfaceColor);
        }
    }
}
