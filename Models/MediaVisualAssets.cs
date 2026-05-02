using System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using wisland.Helpers;

namespace wisland.Models
{
    /// <summary>
    /// Decoded, GPU-ready visual resources for a single album art payload.
    /// Owned by <see cref="wisland.Services.Media.MediaVisualCache"/>; handed out
    /// by reference for instant, decode-free application on session switches.
    /// </summary>
    internal sealed class MediaVisualAssets : IDisposable
    {
        public string Hash { get; }
        public BitmapImage Bitmap { get; }
        public LoadedImageSurface BlurSurface { get; }
        public AlbumArtPalette Palette { get; }
        public ImmersiveSurfaceTokens ImmersiveSurfaceTokens { get; }
        private bool _disposed;

        public MediaVisualAssets(
            string hash,
            BitmapImage bitmap,
            LoadedImageSurface blurSurface,
            AlbumArtPalette palette,
            ImmersiveSurfaceTokens immersiveSurfaceTokens)
        {
            Hash = hash;
            Bitmap = bitmap;
            BlurSurface = blurSurface;
            Palette = palette;
            ImmersiveSurfaceTokens = immersiveSurfaceTokens;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { BlurSurface.Dispose(); }
            catch { /* best-effort */ }
            // BitmapImage has no Dispose; GC releases when no XAML element references it.
        }
    }
}
