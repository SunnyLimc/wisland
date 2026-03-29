using Windows.Graphics;
using wisland.Helpers;
using Xunit;

namespace wisland.Tests
{
    public sealed class SessionPickerPlacementResolverTests
    {
        [Fact]
        public void CentersOverlayBelowAnchorWhenSpaceAllows()
        {
            RectInt32 bounds = SessionPickerPlacementResolver.Resolve(
                anchorBounds: new RectInt32(200, 40, 80, 24),
                workArea: new RectInt32(0, 0, 600, 400),
                overlayWidth: 312,
                overlayHeight: 220,
                gap: 8,
                margin: 8);

            Assert.Equal(84, bounds.X);
            Assert.Equal(72, bounds.Y);
            Assert.Equal(312, bounds.Width);
            Assert.Equal(220, bounds.Height);
        }

        [Fact]
        public void ClampsOverlayToWorkAreaEdges()
        {
            RectInt32 bounds = SessionPickerPlacementResolver.Resolve(
                anchorBounds: new RectInt32(8, 40, 60, 24),
                workArea: new RectInt32(0, 0, 360, 220),
                overlayWidth: 312,
                overlayHeight: 200,
                gap: 8,
                margin: 8);

            Assert.Equal(8, bounds.X);
            Assert.Equal(12, bounds.Y);
        }
    }
}
