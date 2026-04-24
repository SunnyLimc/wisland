using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services.Media
{
    /// <summary>
    /// Decoded-album-art cache keyed by <see cref="MediaSessionSnapshot.ThumbnailHash"/>.
    /// Owns <see cref="BitmapImage"/> + <see cref="LoadedImageSurface"/> + <see cref="AlbumArtPalette"/>
    /// so session switches are a synchronous hash lookup rather than a fresh decode.
    /// <para>
    /// Subscribes to <see cref="MediaService.SessionsChanged"/> (NOT FrameProduced),
    /// because the presentation machine absorbs session updates that do not change
    /// the displayed session's fingerprint. By listening to SessionsChanged directly
    /// we can prewarm every session's visuals — foreground and background — so a
    /// mouse-wheel tab switch hits a warm cache.
    /// </para>
    /// </summary>
    internal sealed class MediaVisualCache : IDisposable
    {
        private const int LruCap = 12;
        private const int DecodePixelWidth = 640;

        private readonly MediaService _mediaService;
        private readonly DispatcherQueue _uiDispatcher;
        private readonly object _gate = new();
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruIndex =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, MediaVisualAssets> _assets =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>
        /// Fired (on UI thread) when a previously-missing hash becomes available.
        /// Subscribers can re-query <see cref="TryGet"/> and apply the assets
        /// without a decode. Useful for views that were showing "pending" visuals
        /// while waiting for the async thumbnail-hash / decode to complete.
        /// </summary>
        public event Action<string>? AssetsReady;

        public MediaVisualCache(MediaService mediaService)
        {
            _mediaService = mediaService ?? throw new ArgumentNullException(nameof(mediaService));
            _uiDispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("MediaVisualCache must be constructed on a UI thread with a DispatcherQueue.");
            _mediaService.SessionsChanged += OnSessionsChanged;
            // Prewarm anything already known (initial snapshot before we subscribed).
            QueuePrewarm(_mediaService.Sessions);
        }

        public bool TryGet(string? hash, out MediaVisualAssets? assets)
        {
            assets = null;
            if (string.IsNullOrEmpty(hash)) return false;
            lock (_gate)
            {
                if (_disposed) return false;
                if (!_assets.TryGetValue(hash, out MediaVisualAssets? found)) return false;
                Touch_NoLock(hash);
                assets = found;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (MediaVisualAssets a in _assets.Values)
                {
                    try { a.Dispose(); } catch { }
                }
                _assets.Clear();
                _lru.Clear();
                _lruIndex.Clear();
                _pending.Clear();
            }
            _mediaService.SessionsChanged -= OnSessionsChanged;
        }

        private void OnSessionsChanged()
        {
            if (_disposed) return;
            QueuePrewarm(_mediaService.Sessions);
        }

        private void QueuePrewarm(IReadOnlyList<MediaSessionSnapshot> sessions)
        {
            if (sessions == null) return;
            for (int i = 0; i < sessions.Count; i++)
            {
                MediaSessionSnapshot s = sessions[i];
                string hash = s.ThumbnailHash ?? string.Empty;
                IRandomAccessStreamReference? thumb = s.Thumbnail;
                if (thumb is null || string.IsNullOrEmpty(hash)) continue;

                lock (_gate)
                {
                    if (_disposed) return;
                    if (_assets.ContainsKey(hash))
                    {
                        Touch_NoLock(hash);
                        continue;
                    }
                    if (!_pending.Add(hash)) continue;
                }
                _ = LoadAsync(hash, thumb);
            }
        }

        private async Task LoadAsync(string hash, IRandomAccessStreamReference thumbnailRef)
        {
            try
            {
                byte[] bytes = await ReadAllBytesAsync(thumbnailRef).ConfigureAwait(false);
                if (bytes.Length == 0) { Forget(hash); return; }

                AlbumArtPalette palette = await AnalyzePaletteAsync(bytes).ConfigureAwait(false);

                // BitmapImage + LoadedImageSurface construction must happen on the
                // UI/compositor thread.
                var tcs = new TaskCompletionSource<MediaVisualAssets?>();
                bool scheduled = _uiDispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        BitmapImage bitmap = new() { DecodePixelWidth = DecodePixelWidth };
                        using (IRandomAccessStream bmpStream = CreateStream(bytes))
                        {
                            await bitmap.SetSourceAsync(bmpStream);
                        }

                        LoadedImageSurface surface = LoadedImageSurface.StartLoadFromStream(
                            CreateStream(bytes));
                        // Wait for LoadCompleted so callers can apply the surface
                        // synchronously without a one-frame blank gap.
                        var surfaceTcs = new TaskCompletionSource<bool>();
                        Windows.Foundation.TypedEventHandler<LoadedImageSurface, LoadedImageSourceLoadCompletedEventArgs>? handler = null;
                        handler = (s, a) =>
                        {
                            if (handler != null) s.LoadCompleted -= handler;
                            surfaceTcs.TrySetResult(a.Status == LoadedImageSourceLoadStatus.Success);
                        };
                        surface.LoadCompleted += handler;
                        Task timeout = Task.Delay(2500);
                        Task completed = await Task.WhenAny(surfaceTcs.Task, timeout);
                        if (completed == timeout)
                        {
                            // Proceed anyway — surface may still finish loading; the
                            // view will pick it up on the next composition pass.
                        }

                        tcs.TrySetResult(new MediaVisualAssets(hash, bitmap, surface, palette));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"MediaVisualCache UI-side decode failed (hash={hash}): {ex.Message}");
                        tcs.TrySetResult(null);
                    }
                });
                if (!scheduled) { Forget(hash); return; }

                MediaVisualAssets? assets = await tcs.Task.ConfigureAwait(false);
                if (assets is null) { Forget(hash); return; }

                bool admitted;
                MediaVisualAssets? evictedOverwrite = null;
                List<MediaVisualAssets>? evictedByLru = null;
                lock (_gate)
                {
                    if (_disposed) { assets.Dispose(); return; }
                    _pending.Remove(hash);
                    if (_assets.TryGetValue(hash, out MediaVisualAssets? existing))
                    {
                        // Race: another pass produced it first. Keep existing.
                        evictedOverwrite = assets;
                        admitted = false;
                    }
                    else
                    {
                        _assets[hash] = assets;
                        Touch_NoLock(hash);
                        admitted = true;
                        evictedByLru = EvictIfNeeded_NoLock();
                    }
                }

                evictedOverwrite?.Dispose();
                if (evictedByLru != null)
                {
                    foreach (MediaVisualAssets e in evictedByLru) e.Dispose();
                }

                if (admitted)
                {
                    _uiDispatcher.TryEnqueue(() =>
                    {
                        try { AssetsReady?.Invoke(hash); } catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"MediaVisualCache load failed (hash={hash}): {ex.Message}");
                Forget(hash);
            }
        }

        private void Forget(string hash)
        {
            lock (_gate) { _pending.Remove(hash); }
        }

        private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStreamReference thumbnailRef)
        {
            try
            {
                using IRandomAccessStreamWithContentType src = await thumbnailRef.OpenReadAsync();
                uint size = (uint)src.Size;
                if (size == 0) return Array.Empty<byte>();
                IBuffer buffer = await src.ReadAsync(
                    new Windows.Storage.Streams.Buffer(size),
                    size,
                    InputStreamOptions.None);
                byte[] bytes = new byte[buffer.Length];
                DataReader.FromBuffer(buffer).ReadBytes(bytes);
                return bytes;
            }
            catch (Exception ex)
            {
                Logger.Warn($"MediaVisualCache failed to read thumbnail stream: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        private static IRandomAccessStream CreateStream(byte[] bytes)
        {
            // MemoryStream backed IRandomAccessStream — zero-copy, synchronous, safe to
            // hand to both BitmapImage.SetSourceAsync and LoadedImageSurface.StartLoadFromStream.
            return new MemoryStream(bytes, writable: false).AsRandomAccessStream();
        }

        private static async Task<AlbumArtPalette> AnalyzePaletteAsync(byte[] bytes)
        {
            try
            {
                using IRandomAccessStream stream = CreateStream(bytes);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
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
                return AlbumArtColorExtractor.AnalyzePixels(pixelData.DetachPixelData());
            }
            catch
            {
                return AlbumArtPalette.Default;
            }
        }

        private void Touch_NoLock(string hash)
        {
            if (_lruIndex.TryGetValue(hash, out LinkedListNode<string>? node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
            }
            else
            {
                _lruIndex[hash] = _lru.AddLast(hash);
            }
        }

        private List<MediaVisualAssets>? EvictIfNeeded_NoLock()
        {
            List<MediaVisualAssets>? evicted = null;
            while (_lru.Count > LruCap)
            {
                LinkedListNode<string> node = _lru.First!;
                _lru.RemoveFirst();
                _lruIndex.Remove(node.Value);
                if (_assets.Remove(node.Value, out MediaVisualAssets? e))
                {
                    (evicted ??= new List<MediaVisualAssets>()).Add(e);
                }
            }
            return evicted;
        }
    }

}
