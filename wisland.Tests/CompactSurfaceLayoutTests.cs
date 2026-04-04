using wisland.Helpers;
using wisland.Models;
using Xunit;

namespace wisland.Tests
{
    public sealed class CompactSurfaceLayoutTests
    {
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
