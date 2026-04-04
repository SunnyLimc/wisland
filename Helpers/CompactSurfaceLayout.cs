using System;
using wisland.Models;
using Windows.Graphics;

namespace wisland.Helpers
{
    public static class CompactSurfaceLayout
    {
        public static bool IsCompactState(double height)
            => height <= IslandConfig.CompactHeight + 1.0;

        public static bool NeedsBoundsReconcile(double expectedExtent, double actualExtent, double physicalPixelLogical)
        {
            if (actualExtent <= 0.0)
            {
                return true;
            }

            return Math.Abs(actualExtent - expectedExtent) > (physicalPixelLogical + 0.05);
        }

        public static double ResolveExtent(double requestedExtent, double actualExtent, bool isCompactState)
        {
            if (!isCompactState || actualExtent <= 0.0)
            {
                return requestedExtent;
            }

            return Math.Abs(actualExtent - requestedExtent) <= IslandConfig.CompactSurfaceExtentSnapTolerance
                ? actualExtent
                : requestedExtent;
        }

        public static bool TryGetVisibleVerticalSlice(
            RectInt32 clientBounds,
            RectInt32 displayBounds,
            double rasterScale,
            double surfaceHeight,
            out double visibleTop,
            out double visibleBottom)
        {
            visibleTop = 0.0;
            visibleBottom = 0.0;

            if (rasterScale <= 0.0 || clientBounds.Height <= 0 || displayBounds.Height <= 0 || surfaceHeight <= 0.0)
            {
                return false;
            }

            int visibleTopPhys = Math.Max(clientBounds.Y, displayBounds.Y);
            int visibleBottomPhys = Math.Min(clientBounds.Y + clientBounds.Height, displayBounds.Y + displayBounds.Height);
            if (visibleBottomPhys <= visibleTopPhys)
            {
                return false;
            }

            visibleTop = Math.Clamp((visibleTopPhys - clientBounds.Y) / rasterScale, 0.0, surfaceHeight);
            visibleBottom = Math.Clamp((visibleBottomPhys - clientBounds.Y) / rasterScale, 0.0, surfaceHeight);
            return visibleBottom > visibleTop;
        }

        public static double ResolveDockPeekClipTop(
            double surfaceHeight,
            double fallbackVisibleLogical,
            RectInt32 clientBounds,
            RectInt32 displayBounds,
            double rasterScale)
        {
            double fallbackClipTop = Math.Clamp(surfaceHeight - fallbackVisibleLogical, 0.0, surfaceHeight);
            return TryGetVisibleVerticalSlice(clientBounds, displayBounds, rasterScale, surfaceHeight, out double visibleTop, out _)
                ? visibleTop
                : fallbackClipTop;
        }

        public static RectInt32 ProjectClientBounds(
            RectInt32 actualClientBounds,
            RectInt32 previousWindowBounds,
            RectInt32 targetWindowBounds)
        {
            if (actualClientBounds.Width <= 0 || actualClientBounds.Height <= 0)
            {
                return actualClientBounds;
            }

            int deltaX = targetWindowBounds.X - previousWindowBounds.X;
            int deltaY = targetWindowBounds.Y - previousWindowBounds.Y;
            int deltaW = targetWindowBounds.Width - previousWindowBounds.Width;
            int deltaH = targetWindowBounds.Height - previousWindowBounds.Height;

            return new RectInt32(
                actualClientBounds.X + deltaX,
                actualClientBounds.Y + deltaY,
                Math.Max(0, actualClientBounds.Width + deltaW),
                Math.Max(0, actualClientBounds.Height + deltaH));
        }
    }
}
