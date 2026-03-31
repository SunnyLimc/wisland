using wisland.Helpers;
using wisland.Models;
using Xunit;

namespace wisland.Tests
{
    public sealed class SessionPickerOverlayLayoutTests
    {
        [Fact]
        public void OverlayHeightUsesStableFixedPitchBetweenCounts()
        {
            double firstItemHeight = SessionPickerOverlayLayout.GetOverlayHeight(1);
            double secondItemHeight = SessionPickerOverlayLayout.GetOverlayHeight(2);

            Assert.Equal(
                IslandConfig.SessionPickerOverlayRowHeight + IslandConfig.SessionPickerOverlayItemSpacing,
                SessionPickerOverlayLayout.RowPitch);
            Assert.Equal(SessionPickerOverlayLayout.RowPitch, secondItemHeight - firstItemHeight);
        }

        [Fact]
        public void ViewportHeightClampsToConfiguredVisibleRowLimit()
        {
            double expectedHeight = IslandConfig.SessionPickerOverlayMaxVisibleItems
                * SessionPickerOverlayLayout.RowPitch;

            Assert.Equal(expectedHeight, SessionPickerOverlayLayout.GetViewportHeight(99));
            Assert.Equal(
                expectedHeight + (IslandConfig.SessionPickerOverlayPanelPadding * 2.0),
                SessionPickerOverlayLayout.GetOverlayHeight(99));
        }
    }
}
