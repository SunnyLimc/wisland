using System;
using wisland.Models;

namespace wisland.Helpers
{
    public static class SessionPickerOverlayLayout
    {
        public static double RowPitch
            => IslandConfig.SessionPickerOverlayRowHeight + IslandConfig.SessionPickerOverlayItemSpacing;

        public static int GetVisibleRowCount(int itemCount)
            => Math.Clamp(itemCount, 0, IslandConfig.SessionPickerOverlayMaxVisibleItems);

        public static double GetViewportHeight(int itemCount)
            => GetVisibleRowCount(itemCount) * RowPitch;

        public static double GetOverlayHeight(int itemCount)
            => GetViewportHeight(itemCount) + (IslandConfig.SessionPickerOverlayPanelPadding * 2.0);
    }
}
