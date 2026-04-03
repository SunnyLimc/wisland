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
            double expectedHeight = (IslandConfig.SessionPickerOverlayMaxVisibleItems
                * IslandConfig.SessionPickerOverlayRowHeight)
                + ((IslandConfig.SessionPickerOverlayMaxVisibleItems - 1)
                    * IslandConfig.SessionPickerOverlayItemSpacing);

            Assert.Equal(expectedHeight, SessionPickerOverlayLayout.GetViewportHeight(99));
            Assert.Equal(
                expectedHeight + (IslandConfig.SessionPickerOverlayPanelPadding * 2.0),
                SessionPickerOverlayLayout.GetOverlayHeight(99));
        }

        [Fact]
        public void SingleVisibleRowDoesNotIncludeOuterSpacing()
        {
            Assert.Equal(
                IslandConfig.SessionPickerOverlayRowHeight
                    + (IslandConfig.SessionPickerOverlayNonScrollableViewportEdgeInset * 2.0),
                SessionPickerOverlayLayout.GetViewportHeight(1));
        }

        [Fact]
        public void ScrollAffordancesAppearOnlyWhenItemCountExceedsVisibleLimit()
        {
            Assert.False(SessionPickerOverlayLayout.HasScrollableOverflow(0));
            Assert.False(SessionPickerOverlayLayout.HasScrollableOverflow(
                IslandConfig.SessionPickerOverlayMaxVisibleItems));
            Assert.True(SessionPickerOverlayLayout.HasScrollableOverflow(
                IslandConfig.SessionPickerOverlayMaxVisibleItems + 1));
        }

        [Fact]
        public void ViewportMetricsReflectScrollAffordanceLayout()
        {
            SessionPickerOverlayViewportMetrics nonScrollableMetrics =
                SessionPickerOverlayLayout.GetViewportMetrics(4, viewportCompensation: 0.8);
            Assert.False(nonScrollableMetrics.ShowsScrollAffordance);
            Assert.Equal(IslandConfig.SessionPickerOverlayPanelPadding, nonScrollableMetrics.PanelRightPadding);
            Assert.Equal(0.0, nonScrollableMetrics.ListRightMargin);
            Assert.Equal(
                SessionPickerOverlayLayout.GetViewportHeight(4, viewportCompensation: 0.8),
                nonScrollableMetrics.Height);

            SessionPickerOverlayViewportMetrics scrollableMetrics =
                SessionPickerOverlayLayout.GetViewportMetrics(5, viewportCompensation: 0.8);
            Assert.True(scrollableMetrics.ShowsScrollAffordance);
            Assert.Equal(IslandConfig.SessionPickerOverlayPanelRightPadding, scrollableMetrics.PanelRightPadding);
            Assert.Equal(IslandConfig.SessionPickerOverlayScrollIndicatorGutterWidth, scrollableMetrics.ListRightMargin);
            Assert.Equal(
                SessionPickerOverlayLayout.GetViewportHeight(5),
                scrollableMetrics.Height);
        }

        [Fact]
        public void NonScrollableViewportIncludesEdgeInset()
        {
            Assert.Equal(
                IslandConfig.SessionPickerOverlayNonScrollableViewportEdgeInset,
                SessionPickerOverlayLayout.GetViewportEdgeInset(1));
            Assert.Equal(
                IslandConfig.SessionPickerOverlayNonScrollableViewportEdgeInset,
                SessionPickerOverlayLayout.GetViewportEdgeInset(
                    IslandConfig.SessionPickerOverlayMaxVisibleItems));
            Assert.Equal(
                0.0,
                SessionPickerOverlayLayout.GetViewportEdgeInset(
                    IslandConfig.SessionPickerOverlayMaxVisibleItems + 1));
        }

        [Fact]
        public void ScrollIndicatorIsIntegratedIntoPanelPaddingBudget()
        {
            Assert.True(IslandConfig.SessionPickerOverlayPanelRightPadding >= 0.0);
            Assert.Equal(
                IslandConfig.SessionPickerOverlayPanelPadding,
                IslandConfig.SessionPickerOverlayScrollIndicatorGutterWidth
                    + IslandConfig.SessionPickerOverlayPanelRightPadding);
        }

        [Fact]
        public void ScrollIndicatorAlignsToViewportAtTopAndBottom()
        {
            double viewportHeight = SessionPickerOverlayLayout.GetViewportHeight(4);
            double fullContentHeight = (6 * IslandConfig.SessionPickerOverlayRowHeight)
                + (5 * IslandConfig.SessionPickerOverlayItemSpacing);
            double scrollableHeight = fullContentHeight - viewportHeight;

            Assert.True(SessionPickerOverlayLayout.TryGetScrollIndicatorMetrics(
                viewportHeight,
                scrollableHeight,
                0.0,
                IslandConfig.SessionPickerOverlayScrollIndicatorMinHeight,
                out SessionPickerOverlayScrollIndicatorMetrics topMetrics));
            Assert.Equal(0.0, topMetrics.OffsetY);

            Assert.True(SessionPickerOverlayLayout.TryGetScrollIndicatorMetrics(
                viewportHeight,
                scrollableHeight,
                scrollableHeight,
                IslandConfig.SessionPickerOverlayScrollIndicatorMinHeight,
                out SessionPickerOverlayScrollIndicatorMetrics bottomMetrics));
            Assert.Equal(viewportHeight - bottomMetrics.Height, bottomMetrics.OffsetY);
        }

        [Fact]
        public void ScrollIndicatorCapsHeightForNearlyFullViewportContent()
        {
            double viewportHeight = SessionPickerOverlayLayout.GetViewportHeight(4);
            double fullContentHeight = (5 * IslandConfig.SessionPickerOverlayRowHeight)
                + (4 * IslandConfig.SessionPickerOverlayItemSpacing);
            double scrollableHeight = fullContentHeight - viewportHeight;

            Assert.True(SessionPickerOverlayLayout.TryGetScrollIndicatorMetrics(
                viewportHeight,
                scrollableHeight,
                0.0,
                IslandConfig.SessionPickerOverlayScrollIndicatorMinHeight,
                out SessionPickerOverlayScrollIndicatorMetrics metrics));
            Assert.Equal(
                viewportHeight * IslandConfig.SessionPickerOverlayScrollIndicatorMaxViewportRatio,
                metrics.Height,
                precision: 6);
        }

        [Fact]
        public void ScrollIndicatorIsHiddenWhenListDoesNotScroll()
        {
            Assert.False(SessionPickerOverlayLayout.TryGetScrollIndicatorMetrics(
                SessionPickerOverlayLayout.GetViewportHeight(4),
                0.0,
                0.0,
                IslandConfig.SessionPickerOverlayScrollIndicatorMinHeight,
                out _));
        }
    }
}
