using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace wisland.Helpers
{
    /// <summary>
    /// Extracts dominant colors from album art thumbnails for gradient backgrounds.
    /// Results are cached by stream reference identity to avoid redundant computation.
    /// </summary>
    internal static class AlbumArtColorExtractor
    {
        private static readonly ConcurrentDictionary<string, AlbumArtPalette> _cache = new(StringComparer.Ordinal);

        public static async Task<AlbumArtPalette?> ExtractAsync(IRandomAccessStreamReference? thumbnailRef, string cacheKey)
        {
            if (thumbnailRef == null || string.IsNullOrEmpty(cacheKey))
                return null;

            if (_cache.TryGetValue(cacheKey, out AlbumArtPalette cached))
                return cached;

            try
            {
                using IRandomAccessStreamWithContentType stream = await thumbnailRef.OpenReadAsync();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

                // Downsample to 32×32 for fast histogram analysis
                const uint sampleSize = 32;
                BitmapTransform transform = new()
                {
                    ScaledWidth = sampleSize,
                    ScaledHeight = sampleSize,
                    InterpolationMode = BitmapInterpolationMode.Linear
                };

                PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                byte[] pixels = pixelData.DetachPixelData();
                AlbumArtPalette palette = AnalyzePixels(pixels);
                _cache.TryAdd(cacheKey, palette);
                return palette;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Album art color extraction failed: {ex.Message}");
                return null;
            }
        }

        public static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference? thumbnailRef)
        {
            if (thumbnailRef == null)
                return null;

            try
            {
                using IRandomAccessStreamWithContentType stream = await thumbnailRef.OpenReadAsync();
                BitmapImage bitmap = new();
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Album art thumbnail load failed: {ex.Message}");
                return null;
            }
        }

        private static AlbumArtPalette AnalyzePixels(byte[] pixels)
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

        public static void ClearCache() => _cache.Clear();
    }

    internal readonly record struct AlbumArtPalette(Color Dominant, Color Secondary, Color Average)
    {
        public static readonly AlbumArtPalette Default = new(
            Color.FromArgb(255, 40, 40, 50),
            Color.FromArgb(255, 25, 25, 35),
            Color.FromArgb(255, 32, 32, 42));
    }
}
