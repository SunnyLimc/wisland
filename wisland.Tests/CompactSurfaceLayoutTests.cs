using wisland.Helpers;
using wisland.Models;
using Windows.Graphics;
using Xunit;

namespace wisland.Tests
{
    public sealed class CompactSurfaceLayoutTests
    {
        [Fact]
        public void ResolveDockPeekClipTopUsesActualVisibleSliceWhenWindowCrossesDisplayTop()
        {
            RectInt32 clientBounds = new(0, -33, 250, 39);
            RectInt32 displayBounds = new(0, 0, 2560, 1440);

            double clipTop = CompactSurfaceLayout.ResolveDockPeekClipTop(
                surfaceHeight: 30.4,
                fallbackVisibleLogical: 0.8,
                clientBounds,
                displayBounds,
                rasterScale: 1.25);

            Assert.Equal(26.4, clipTop, precision: 6);
        }

        [Fact]
        public void ResolveDockPeekClipTopFallsBackToPeekHeightWhenVisibleSliceIsUnavailable()
        {
            double clipTop = CompactSurfaceLayout.ResolveDockPeekClipTop(
                surfaceHeight: 30.4,
                fallbackVisibleLogical: 0.8,
                new RectInt32(),
                new RectInt32(),
                rasterScale: 1.25);

            Assert.Equal(29.6, clipTop, precision: 6);
        }

        [Fact]
        public void TryGetVisibleVerticalSliceReturnsVisibleClientSegmentInLogicalPixels()
        {
            Assert.True(CompactSurfaceLayout.TryGetVisibleVerticalSlice(
                new RectInt32(0, -33, 250, 39),
                new RectInt32(0, 0, 2560, 1440),
                1.25,
                30.4,
                out double visibleTop,
                out double visibleBottom));
            Assert.Equal(26.4, visibleTop, precision: 6);
            Assert.Equal(30.4, visibleBottom, precision: 6);
        }

        [Fact]
        public void ProjectClientBoundsAdvancesClientSliceWithCurrentFrameWindowMove()
        {
            RectInt32 projected = CompactSurfaceLayout.ProjectClientBounds(
                new RectInt32(100, -33, 250, 39),
                new RectInt32(100, -33, 250, 38),
                new RectInt32(100, -32, 250, 38));

            Assert.Equal(-32, projected.Y);
            Assert.Equal(39, projected.Height);
        }

        [Fact]
        public void NeedsBoundsReconcileWhenActualExtentIsUnavailable()
        {
            Assert.True(CompactSurfaceLayout.NeedsBoundsReconcile(30.22, 0.0, 0.444));
        }

        [Fact]
        public void NeedsBoundsReconcileWhenResidualExceedsPhysicalPixel()
        {
            Assert.True(CompactSurfaceLayout.NeedsBoundsReconcile(30.22, 32.00, 0.444));
        }

        [Fact]
        public void DoesNotNeedBoundsReconcileWithinPhysicalPixelThreshold()
        {
            Assert.False(CompactSurfaceLayout.NeedsBoundsReconcile(31.11, 31.56, 0.444));
        }

        [Fact]
        public void ResolveExtentSnapsToActualExtentForCompactResidualWithinTolerance()
        {
            double requestedHeight = 30.22;
            double actualHeight = 32.00;

            Assert.Equal(
                actualHeight,
                CompactSurfaceLayout.ResolveExtent(
                    requestedHeight,
                    actualHeight,
                    isCompactState: true));
        }

        [Fact]
        public void ResolveExtentDoesNotSnapOutsideCompactState()
        {
            Assert.Equal(
                120.00,
                CompactSurfaceLayout.ResolveExtent(
                    120.00,
                    121.50,
                    isCompactState: false));
        }

        [Fact]
        public void ResolveExtentDoesNotSnapWhenResidualExceedsTolerance()
        {
            double requestedHeight = IslandConfig.CompactHeight;
            double actualHeight = requestedHeight + IslandConfig.CompactSurfaceExtentSnapTolerance + 0.01;

            Assert.Equal(
                requestedHeight,
                CompactSurfaceLayout.ResolveExtent(
                    requestedHeight,
                    actualHeight,
                    isCompactState: true));
        }

        [Fact]
        public void ResolveExtentIgnoresUnavailableActualExtent()
        {
            Assert.Equal(
                IslandConfig.CompactHeight,
                CompactSurfaceLayout.ResolveExtent(
                    IslandConfig.CompactHeight,
                    0.0,
                    isCompactState: true));
        }
    }
}
