using System;
using wisland.Models;

namespace wisland.Helpers
{
    public readonly record struct SessionPickerOverlayViewportMetrics(
        double Height,
        double EdgeInset,
        double PanelRightPadding,
        double ListRightMargin,
        bool ShowsScrollAffordance);

    public readonly record struct SessionPickerOverlayScrollIndicatorMetrics(
        double Height,
        double OffsetY);

    public static class SessionPickerOverlayLayout
    {
        public static double RowPitch
            => IslandConfig.SessionPickerOverlayRowHeight + IslandConfig.SessionPickerOverlayItemSpacing;

        public static int GetVisibleRowCount(int itemCount)
            => Math.Clamp(itemCount, 0, IslandConfig.SessionPickerOverlayMaxVisibleItems);

        public static bool HasScrollableOverflow(int itemCount)
            => itemCount > IslandConfig.SessionPickerOverlayMaxVisibleItems;

        public static double GetViewportEdgeInset(int itemCount)
            => itemCount > 0 && !HasScrollableOverflow(itemCount)
                ? IslandConfig.SessionPickerOverlayNonScrollableViewportEdgeInset
                : 0.0;

        public static double GetViewportHeight(int itemCount, double viewportCompensation = 0.0)
        {
            int visibleRowCount = GetVisibleRowCount(itemCount);
            if (visibleRowCount <= 0)
            {
                return 0.0;
            }

            return (visibleRowCount * IslandConfig.SessionPickerOverlayRowHeight)
                + ((visibleRowCount - 1) * IslandConfig.SessionPickerOverlayItemSpacing)
                + (GetViewportEdgeInset(itemCount) * 2.0)
                + Math.Max(0.0, viewportCompensation);
        }

        public static double GetOverlayHeight(int itemCount, double viewportCompensation = 0.0)
            => GetViewportHeight(itemCount, viewportCompensation) + (IslandConfig.SessionPickerOverlayPanelPadding * 2.0);

        public static SessionPickerOverlayViewportMetrics GetViewportMetrics(
            int itemCount,
            double viewportCompensation = 0.0)
        {
            bool hasOverflow = HasScrollableOverflow(itemCount);
            return new SessionPickerOverlayViewportMetrics(
                Height: GetViewportHeight(itemCount, hasOverflow ? 0.0 : viewportCompensation),
                EdgeInset: GetViewportEdgeInset(itemCount),
                PanelRightPadding: hasOverflow
                    ? IslandConfig.SessionPickerOverlayPanelRightPadding
                    : IslandConfig.SessionPickerOverlayPanelPadding,
                ListRightMargin: hasOverflow
                    ? IslandConfig.SessionPickerOverlayScrollIndicatorGutterWidth
                    : 0.0,
                ShowsScrollAffordance: hasOverflow);
        }

        public static bool TryGetScrollIndicatorMetrics(
            double viewportHeight,
            double scrollableHeight,
            double verticalOffset,
            double minThumbHeight,
            out SessionPickerOverlayScrollIndicatorMetrics metrics)
        {
            metrics = default;

            double clampedViewportHeight = Math.Max(0.0, viewportHeight);
            double clampedScrollableHeight = Math.Max(0.0, scrollableHeight);
            if (clampedViewportHeight <= 0.0 || clampedScrollableHeight <= 0.0)
            {
                return false;
            }

            double extentHeight = clampedViewportHeight + clampedScrollableHeight;
            double maxThumbHeight = Math.Max(
                Math.Max(0.0, minThumbHeight),
                clampedViewportHeight * IslandConfig.SessionPickerOverlayScrollIndicatorMaxViewportRatio);
            double thumbHeight = Math.Clamp(
                (clampedViewportHeight * clampedViewportHeight) / extentHeight,
                Math.Max(0.0, minThumbHeight),
                Math.Min(clampedViewportHeight, maxThumbHeight));
            double travel = Math.Max(0.0, clampedViewportHeight - thumbHeight);
            double offsetY = travel <= 0.0
                ? 0.0
                : (Math.Clamp(verticalOffset, 0.0, clampedScrollableHeight) / clampedScrollableHeight) * travel;

            metrics = new SessionPickerOverlayScrollIndicatorMetrics(thumbHeight, offsetY);
            return true;
        }
    }
}
