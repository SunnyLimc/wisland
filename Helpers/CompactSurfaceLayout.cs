using System;
using wisland.Models;

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
    }
}
