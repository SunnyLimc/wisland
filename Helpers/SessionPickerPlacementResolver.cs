using System;
using Windows.Graphics;

namespace wisland.Helpers
{
    public static class SessionPickerPlacementResolver
    {
        public static RectInt32 Resolve(
            RectInt32 anchorBounds,
            RectInt32 workArea,
            int overlayWidth,
            int overlayHeight,
            int gap,
            int margin)
        {
            int width = Math.Max(1, overlayWidth);
            int height = Math.Max(1, overlayHeight);
            int clampedMargin = Math.Max(0, margin);
            int clampedGap = Math.Max(0, gap);

            int preferredX = anchorBounds.X + (anchorBounds.Width / 2) - (width / 2);
            int minX = workArea.X + clampedMargin;
            int maxX = workArea.X + workArea.Width - clampedMargin - width;
            int resolvedX = maxX < minX
                ? minX
                : Math.Clamp(preferredX, minX, maxX);

            int preferredY = anchorBounds.Y + anchorBounds.Height + clampedGap;
            int minY = workArea.Y + clampedMargin;
            int maxY = workArea.Y + workArea.Height - clampedMargin - height;
            int resolvedY = maxY < minY
                ? minY
                : Math.Clamp(preferredY, minY, maxY);

            return new RectInt32(resolvedX, resolvedY, width, height);
        }
    }
}
