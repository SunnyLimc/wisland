using System;
using Windows.UI;
using wisland.Models;

namespace wisland.Helpers
{
    /// <summary>
    /// Stateless palette analyzer for BGRA8 pre-multiplied pixel buffers.
    /// All decoding + caching of album art lives in
    /// <see cref="wisland.Services.Media.MediaVisualCache"/>; this helper is
    /// purely the histogram-based dominant-color pass it calls into.
    /// </summary>
    internal static class AlbumArtColorExtractor
    {
        internal static AlbumArtPalette AnalyzePixels(byte[] pixels, int width, int height)
        {
            // Histogram-based dominant color extraction.
            // Quantize to 4-bit per channel (16×16×16 = 4096 buckets) for fast analysis.
            const int bucketShift = 4;
            const int bucketCount = 16;
            int[] histogram = new int[bucketCount * bucketCount * bucketCount];
            long totalR = 0, totalG = 0, totalB = 0;
            int pixelCount = 0;

            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 128) continue; // Skip transparent pixels

                int ri = r >> bucketShift;
                int gi = g >> bucketShift;
                int bi = b >> bucketShift;
                histogram[(ri * bucketCount + gi) * bucketCount + bi]++;

                totalR += r;
                totalG += g;
                totalB += b;
                pixelCount++;
            }

            if (pixelCount == 0)
                return AlbumArtPalette.Default;

            // Average color (used as secondary/ambient)
            Color average = Color.FromArgb(255,
                (byte)(totalR / pixelCount),
                (byte)(totalG / pixelCount),
                (byte)(totalB / pixelCount));

            // Find top 2 dominant buckets
            int best1Idx = 0, best2Idx = 0;
            int best1Count = 0, best2Count = 0;

            for (int i = 0; i < histogram.Length; i++)
            {
                if (histogram[i] > best1Count)
                {
                    best2Idx = best1Idx;
                    best2Count = best1Count;
                    best1Idx = i;
                    best1Count = histogram[i];
                }
                else if (histogram[i] > best2Count)
                {
                    best2Idx = i;
                    best2Count = histogram[i];
                }
            }

            Color dominant = BucketToColor(best1Idx, bucketCount);
            Color secondary = best2Count > 0 ? BucketToColor(best2Idx, bucketCount) : average;

            // Ensure contrast: if dominant and secondary are too similar, darken secondary
            if (ColorDistance(dominant, secondary) < 60)
            {
                secondary = DarkenColor(dominant, 0.4);
            }

            // Edge colors feed resize-time window surfaces. They are sampled
            // from the area that remains visible after ImmersiveMediaView's
            // UniformToFill crop, then composited later with gradient/scrim.
            Color leftEdge = AverageVisibleLeftEdge(pixels, width, height, average);
            Color rightEdge = AverageVisibleRightEdge(pixels, width, height, average);
            Color bottomEdge = AverageVisibleBottomEdge(pixels, width, height, average);

            return new AlbumArtPalette(
                dominant,
                secondary,
                average,
                leftEdge,
                rightEdge,
                bottomEdge);
        }

        private static Color AverageVisibleLeftEdge(byte[] pixels, int width, int height, Color fallback)
        {
            GetVisibleVerticalRange(width, height, out int startY, out int endY);
            int bandWidth = Math.Max(1, width / 16);
            return AverageRegion(pixels, width, height, 0, bandWidth, startY, endY, fallback);
        }

        private static Color AverageVisibleRightEdge(byte[] pixels, int width, int height, Color fallback)
        {
            GetVisibleVerticalRange(width, height, out int startY, out int endY);
            int bandWidth = Math.Max(1, width / 16);
            return AverageRegion(pixels, width, height, Math.Max(0, width - bandWidth), width, startY, endY, fallback);
        }

        private static Color AverageVisibleBottomEdge(byte[] pixels, int width, int height, Color fallback)
        {
            GetVisibleVerticalRange(width, height, out _, out int visibleBottomY);
            int bandHeight = Math.Max(1, height / 16);
            return AverageRegion(pixels, width, height, 0, width, Math.Max(0, visibleBottomY - bandHeight), visibleBottomY, fallback);
        }

        private static void GetVisibleVerticalRange(int width, int height, out int startY, out int endY)
        {
            if (width <= 0 || height <= 0)
            {
                startY = 0;
                endY = 0;
                return;
            }

            // The immersive background is roughly 2:1 and album art is usually
            // square. UniformToFill crops the top/bottom quarters, so sample the
            // central vertical band that actually reaches the view edges.
            if (width == height)
            {
                startY = height / 4;
                endY = Math.Max(startY + 1, (height * 3) / 4);
                return;
            }

            startY = 0;
            endY = height;
        }

        private static Color AverageRegion(
            byte[] pixels,
            int width,
            int height,
            int startX,
            int endX,
            int startY,
            int endY,
            Color fallback)
        {
            if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
            {
                return fallback;
            }

            startX = Math.Clamp(startX, 0, width);
            endX = Math.Clamp(endX, startX, width);
            startY = Math.Clamp(startY, 0, height);
            endY = Math.Clamp(endY, startY, height);

            long totalR = 0, totalG = 0, totalB = 0;
            int count = 0;

            for (int y = startY; y < endY; y++)
            {
                int rowOffset = y * width * 4;
                for (int x = startX; x < endX; x++)
                {
                    int i = rowOffset + (x * 4);
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte a = pixels[i + 3];
                    if (a < 128) continue;

                    totalR += r;
                    totalG += g;
                    totalB += b;
                    count++;
                }
            }

            if (count == 0)
            {
                return fallback;
            }

            return Color.FromArgb(
                255,
                (byte)(totalR / count),
                (byte)(totalG / count),
                (byte)(totalB / count));
        }

        private static Color BucketToColor(int bucketIndex, int bucketCount)
        {
            int bi = bucketIndex % bucketCount;
            int gi = (bucketIndex / bucketCount) % bucketCount;
            int ri = bucketIndex / (bucketCount * bucketCount);
            return Color.FromArgb(255,
                (byte)Math.Min(255, ri * 17 + 8),
                (byte)Math.Min(255, gi * 17 + 8),
                (byte)Math.Min(255, bi * 17 + 8));
        }

        private static double ColorDistance(Color a, Color b)
        {
            int dr = a.R - b.R;
            int dg = a.G - b.G;
            int db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static Color DarkenColor(Color c, double factor)
        {
            return Color.FromArgb(255,
                (byte)(c.R * factor),
                (byte)(c.G * factor),
                (byte)(c.B * factor));
        }

    }
}
