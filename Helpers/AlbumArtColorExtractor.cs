using System;
using Windows.UI;

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
        internal static AlbumArtPalette AnalyzePixels(byte[] pixels)
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

            return new AlbumArtPalette(dominant, secondary, average);
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

    internal readonly record struct AlbumArtPalette(Color Dominant, Color Secondary, Color Average)
    {
        public static readonly AlbumArtPalette Default = new(
            Color.FromArgb(255, 40, 40, 50),
            Color.FromArgb(255, 25, 25, 35),
            Color.FromArgb(255, 32, 32, 42));
    }
}
