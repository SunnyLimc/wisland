using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace wisland.Helpers
{
    public sealed class MediaSourceIconResolver
    {
        public static MediaSourceIconResolver Shared { get; } = new();

        private readonly ConcurrentDictionary<string, Task<ImageSource?>> _iconCache =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<ImageSource?> ResolveAsync(string? sourceAppId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppId))
            {
                return Task.FromResult<ImageSource?>(null);
            }

            return ResolveCachedAsync(sourceAppId);
        }

        private async Task<ImageSource?> ResolveCachedAsync(string sourceAppId)
        {
            Task<ImageSource?> resolveTask = _iconCache.GetOrAdd(sourceAppId, ResolveCoreAsync);

            try
            {
                ImageSource? resolvedIcon = await resolveTask;
                if (resolvedIcon == null)
                {
                    _iconCache.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(sourceAppId, resolveTask));
                }

                return resolvedIcon;
            }
            catch
            {
                _iconCache.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(sourceAppId, resolveTask));
                throw;
            }
        }

        private static async Task<ImageSource?> ResolveCoreAsync(string sourceAppId)
        {
            ImageSource? packagedIcon = await TryResolvePackagedIconAsync(sourceAppId);
            if (packagedIcon != null)
            {
                Logger.Debug($"Icon resolved via packaged app for '{sourceAppId}'");
                return packagedIcon;
            }

            MediaSourceAppResolver.RegisteredShortcut? registeredShortcut =
                MediaSourceAppResolver.TryResolveRegisteredShortcut(sourceAppId);
            if (registeredShortcut.HasValue)
            {
                ImageSource? shortcutIcon = await TryResolveRegisteredShortcutIconAsync(registeredShortcut.Value);
                if (shortcutIcon != null)
                {
                    Logger.Debug($"Icon resolved via shortcut for '{sourceAppId}'");
                    return shortcutIcon;
                }
            }

            string? executablePath = registeredShortcut?.TargetPath
                ?? MediaSourceAppResolver.TryResolveRegisteredExecutablePath(sourceAppId)
                ?? TryResolveExecutablePath(sourceAppId);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                Logger.Debug($"No icon source found for '{sourceAppId}'");
                return null;
            }

            Logger.Debug($"Icon resolved via executable for '{sourceAppId}': {executablePath}");
            return await TryResolveExecutableIconAsync(executablePath);
        }

        private static async Task<ImageSource?> TryResolvePackagedIconAsync(string sourceAppId)
        {
            try
            {
                AppInfo? appInfo = MediaSourceAppResolver.TryGetPackagedAppInfo(sourceAppId);
                if (appInfo == null)
                {
                    return null;
                }

                var logo = appInfo.DisplayInfo.GetLogo(new Size(256, 256));
                using var stream = await logo.OpenReadAsync();
                return await CreateBitmapImageAsync(stream);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<ImageSource?> TryResolveRegisteredShortcutIconAsync(
            MediaSourceAppResolver.RegisteredShortcut registeredShortcut)
        {
            ImageSource? explicitShortcutIcon = await TryResolveExplicitShortcutIconAsync(registeredShortcut);
            if (explicitShortcutIcon != null)
            {
                return explicitShortcutIcon;
            }

            if (registeredShortcut.HasCustomIconLocation)
            {
                return await TryResolveStorageFileIconAsync(registeredShortcut.ShortcutPath);
            }

            if (string.IsNullOrWhiteSpace(registeredShortcut.TargetPath)
                || !HasMeaningfulFileIcon(registeredShortcut.TargetPath))
            {
                return null;
            }

            ImageSource? shortcutIcon = await TryResolveStorageFileIconAsync(registeredShortcut.ShortcutPath);
            if (shortcutIcon != null)
            {
                return shortcutIcon;
            }

            return string.IsNullOrWhiteSpace(registeredShortcut.TargetPath)
                ? null
                : await TryResolveExecutableIconAsync(registeredShortcut.TargetPath);
        }

        private static async Task<ImageSource?> TryResolveExplicitShortcutIconAsync(
            MediaSourceAppResolver.RegisteredShortcut registeredShortcut)
        {
            if (!registeredShortcut.HasCustomIconLocation)
            {
                return null;
            }

            string? iconPath = registeredShortcut.ParsedIconLocation.Path;
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
            {
                return null;
            }

            string extension = Path.GetExtension(iconPath);
            if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                return await TryResolveStorageFileIconAsync(iconPath);
            }

            if (registeredShortcut.ParsedIconLocation.ResourceIndex == 0
                && HasMeaningfulFileIcon(iconPath))
            {
                return await TryResolveStorageFileIconAsync(iconPath);
            }

            return null;
        }

        private static async Task<ImageSource?> TryResolveExecutableIconAsync(string executablePath)
        {
            if (!HasMeaningfulFileIcon(executablePath))
            {
                return null;
            }

            return await TryResolveStorageFileIconAsync(executablePath);
        }

        private static async Task<ImageSource?> TryResolveStorageFileIconAsync(string filePath)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    256,
                    ThumbnailOptions.ResizeThumbnail);

                if (thumbnail == null)
                {
                    return null;
                }

                return await CreateBitmapImageAsync(thumbnail);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasMeaningfulFileIcon(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            string extension = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return true;
            }

            if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!RequiresEmbeddedIconCheck(extension))
            {
                return true;
            }

            try
            {
                return ExtractIconEx(filePath, -1, null, null, 0) > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool RequiresEmbeddedIconCheck(string extension)
            => extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cpl", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".icl", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mun", StringComparison.OrdinalIgnoreCase);

        private static async Task<ImageSource?> CreateBitmapImageAsync(Windows.Storage.Streams.IRandomAccessStream stream)
        {
            try
            {
                Logger.Debug($"Icon normalize: stream size={stream.Size} bytes");
                var decoder = await BitmapDecoder.CreateAsync(stream);
                uint w = decoder.PixelWidth;
                uint h = decoder.PixelHeight;
                Logger.Debug($"Icon normalize: decoded {w}x{h}, codec={decoder.DecoderInformation?.FriendlyName}");

                var pixelDataProvider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                byte[] pixels = pixelDataProvider.DetachPixelData();

                var crop = ComputeContentCrop(pixels, w, h);
                if (crop.HasValue)
                {
                    var (cx, cy, cw, ch) = crop.Value;
                    Logger.Debug($"Icon content crop: source={w}x{h} -> crop=({cx},{cy},{cw}x{ch})");
                    var transform = new BitmapTransform
                    {
                        Bounds = new BitmapBounds { X = cx, Y = cy, Width = cw, Height = ch }
                    };

                    var croppedData = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);
                    byte[] croppedPixels = croppedData.DetachPixelData();

                    var output = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        cw, ch,
                        decoder.DpiX, decoder.DpiY,
                        croppedPixels);
                    await encoder.FlushAsync();
                    output.Seek(0);

                    BitmapImage cropped = new()
                    {
                        DecodePixelWidth = 32,
                        DecodePixelHeight = 32,
                        DecodePixelType = DecodePixelType.Logical
                    };
                    await cropped.SetSourceAsync(output);
                    return cropped;
                }

                Logger.Debug($"Icon no-crop: source={w}x{h}, content fills >98%");
                stream.Seek(0);
                BitmapImage bitmap = new()
                {
                    DecodePixelWidth = 32,
                    DecodePixelHeight = 32,
                    DecodePixelType = DecodePixelType.Logical
                };
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Icon normalize failed: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    stream.Seek(0);
                    BitmapImage fallback = new() { DecodePixelWidth = 32, DecodePixelHeight = 32 };
                    await fallback.SetSourceAsync(stream);
                    return fallback;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// One-shot dump: enumerates current GSMTC sessions, resolves each icon
        /// stream, decodes pixels, runs content-crop analysis, and writes
        /// original + cropped PNGs to the logs/icon-dump directory.
        /// Returns the dump directory path.
        /// </summary>
        public static async Task<string> DumpAllSessionIconsAsync()
        {
            string dumpDir = SafePaths.Combine("logs", "icon-dump");
            Directory.CreateDirectory(dumpDir);

            // Clear old dumps.
            foreach (string file in Directory.EnumerateFiles(dumpDir, "*.png"))
            {
                try { File.Delete(file); } catch { }
            }

            // Enumerate current GSMTC sessions.
            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int seq = 0;

            foreach (var session in sessions)
            {
                string? sourceAppId;
                try { sourceAppId = session.SourceAppUserModelId; }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(sourceAppId) || !seen.Add(sourceAppId))
                    continue;

                seq++;
                string label = SanitizeFileLabel(
                    MediaSourceAppResolver.TryResolveDisplayName(sourceAppId) ?? sourceAppId,
                    sourceAppId);
                try
                {
                    using IRandomAccessStream? stream = await TryResolveIconStreamAsync(sourceAppId);
                    if (stream == null)
                    {
                        Logger.Debug($"Icon dump #{seq} ({label}): no icon stream");
                        continue;
                    }

                    await DumpSingleIconAsync(dumpDir, seq, label, stream);
                    Logger.Debug($"Icon dump #{seq} ({label}): done");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Icon dump #{seq} ({label}) failed: {ex.Message}");
                }
            }

            Logger.Info($"Icon dump complete: {seq} session(s) → {dumpDir}");
            return dumpDir;
        }

        private static async Task DumpSingleIconAsync(
            string dumpDir, int seq, string label, IRandomAccessStream stream)
        {
            const uint DumpSize = 256;
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint w = decoder.PixelWidth;
            uint h = decoder.PixelHeight;

            var pixelDataProvider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelDataProvider.DetachPixelData();

            // Write original.
            await WriteDumpPngAsync(
                Path.Combine(dumpDir, $"{seq}-{label}-original-{w}x{h}.png"),
                pixels, w, h, DumpSize);

            // Compute and write cropped version.
            var crop = ComputeContentCrop(pixels, w, h);
            if (crop.HasValue)
            {
                var (cx, cy, cw, ch) = crop.Value;
                var transform = new BitmapTransform
                {
                    Bounds = new BitmapBounds { X = cx, Y = cy, Width = cw, Height = ch }
                };
                var croppedData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                byte[] croppedPixels = croppedData.DetachPixelData();

                await WriteDumpPngAsync(
                    Path.Combine(dumpDir, $"{seq}-{label}-cropped-{cw}x{ch}.png"),
                    croppedPixels, cw, ch, DumpSize);
            }
        }

        /// <summary>
        /// Resolves the raw icon stream for a source app ID, bypassing BitmapImage
        /// creation. Returns null if no icon can be found.
        /// The caller is responsible for disposing the returned stream.
        /// </summary>
        private static async Task<IRandomAccessStream?> TryResolveIconStreamAsync(string sourceAppId)
        {
            // 1. Packaged app logo
            try
            {
                AppInfo? appInfo = MediaSourceAppResolver.TryGetPackagedAppInfo(sourceAppId);
                if (appInfo != null)
                {
                    var logo = appInfo.DisplayInfo.GetLogo(new Size(256, 256));
                    return await logo.OpenReadAsync();
                }
            }
            catch { }

            // 2. Shortcut icon
            MediaSourceAppResolver.RegisteredShortcut? registeredShortcut =
                MediaSourceAppResolver.TryResolveRegisteredShortcut(sourceAppId);
            if (registeredShortcut.HasValue)
            {
                IRandomAccessStream? shortcutStream =
                    await TryResolveShortcutIconStreamAsync(registeredShortcut.Value);
                if (shortcutStream != null)
                    return shortcutStream;
            }

            // 3. Executable thumbnail
            string? executablePath = registeredShortcut?.TargetPath
                ?? MediaSourceAppResolver.TryResolveRegisteredExecutablePath(sourceAppId)
                ?? TryResolveExecutablePath(sourceAppId);
            if (!string.IsNullOrWhiteSpace(executablePath) && HasMeaningfulFileIcon(executablePath))
            {
                return await TryGetStorageFileThumbnailStreamAsync(executablePath);
            }

            return null;
        }

        private static async Task<IRandomAccessStream?> TryResolveShortcutIconStreamAsync(
            MediaSourceAppResolver.RegisteredShortcut shortcut)
        {
            // Explicit icon location
            if (shortcut.HasCustomIconLocation)
            {
                string? iconPath = shortcut.ParsedIconLocation.Path;
                if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                {
                    string ext = Path.GetExtension(iconPath);
                    if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        var s = await TryGetStorageFileThumbnailStreamAsync(iconPath);
                        if (s != null) return s;
                    }
                    else if (shortcut.ParsedIconLocation.ResourceIndex == 0
                             && HasMeaningfulFileIcon(iconPath))
                    {
                        var s = await TryGetStorageFileThumbnailStreamAsync(iconPath);
                        if (s != null) return s;
                    }
                }

                var s2 = await TryGetStorageFileThumbnailStreamAsync(shortcut.ShortcutPath);
                if (s2 != null) return s2;
            }

            // Shortcut file thumbnail → target executable
            if (!string.IsNullOrWhiteSpace(shortcut.TargetPath)
                && HasMeaningfulFileIcon(shortcut.TargetPath))
            {
                var s = await TryGetStorageFileThumbnailStreamAsync(shortcut.ShortcutPath);
                if (s != null) return s;

                return await TryGetStorageFileThumbnailStreamAsync(shortcut.TargetPath);
            }

            return null;
        }

        private static async Task<IRandomAccessStream?> TryGetStorageFileThumbnailStreamAsync(string filePath)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                StorageItemThumbnail? thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem, 256, ThumbnailOptions.ResizeThumbnail);
                return thumbnail;
            }
            catch
            {
                return null;
            }
        }

        private static async Task WriteDumpPngAsync(
            string path, byte[] pixels, uint srcW, uint srcH, uint targetSize)
        {
            using var ms = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                srcW, srcH,
                96, 96,
                pixels);
            encoder.BitmapTransform.ScaledWidth = targetSize;
            encoder.BitmapTransform.ScaledHeight = targetSize;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();

            ms.Seek(0);
            using var fileStream = File.Create(path);
            using var inputStream = ms.AsStreamForRead();
            await inputStream.CopyToAsync(fileStream);
        }

        private static string SanitizeFileLabel(string sourceName, string sourceAppId)
        {
            string raw = string.IsNullOrWhiteSpace(sourceName) ? sourceAppId : sourceName;
            char[] invalid = Path.GetInvalidFileNameChars();
            var sanitized = new char[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                sanitized[i] = Array.IndexOf(invalid, raw[i]) >= 0 ? '_' : raw[i];
            string result = new string(sanitized).Trim();
            return result.Length > 40 ? result[..40] : result;
        }

        private static (uint X, uint Y, uint Width, uint Height)? ComputeContentCrop(
            byte[] pixels, uint width, uint height)
        {
            // Detect the dominant border color by sampling corner regions.
            // Icons with opaque background plates (e.g., Windows exe thumbnails)
            // have solid-color borders that alpha-only detection misses.
            uint cornerSize = Math.Max(2, Math.Min(width, height) / 8);
            (byte B, byte G, byte R, byte A) borderColor = SampleCornerColor(pixels, width, height, cornerSize);

            uint minX = width, minY = height, maxX = 0, maxY = 0;
            bool hasContent = false;

            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    uint idx = (y * width + x) * 4;
                    byte b = pixels[idx];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    byte a = pixels[idx + 3];

                    // Skip near-transparent pixels — anti-aliased edges with
                    // very low alpha shouldn't inflate the content bounding box.
                    if (a <= 40)
                        continue;
                    if (IsColorClose(b, g, r, a, borderColor.B, borderColor.G, borderColor.R, borderColor.A, 40))
                        continue;

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                    hasContent = true;
                }
            }

            if (!hasContent) return null;

            uint contentW = maxX - minX + 1;
            uint contentH = maxY - minY + 1;
            uint contentMaxDim = Math.Max(contentW, contentH);
            uint imageMaxDim = Math.Max(width, height);

            double fillRatio = (double)contentMaxDim / imageMaxDim;
            Logger.Debug($"Icon content scan: bounds=({minX},{minY})-({maxX},{maxY}), " +
                $"content={contentW}x{contentH}, fill={fillRatio:P0}, " +
                $"borderColor=({borderColor.R},{borderColor.G},{borderColor.B},{borderColor.A})");

            // Skip cropping only when content truly fills the canvas (>98%).
            // Icons with even small transparent margins (e.g. 90% fill) look
            // noticeably smaller than edge-to-edge icons at small display sizes,
            // so we normalize aggressively.
            if (fillRatio > 0.98) return null;

            // Build a tight square crop centered on the content.
            // Use a minimal 2% padding so the icon doesn't touch the clip edge.
            uint padded = Math.Max((uint)(contentMaxDim * 1.02), contentMaxDim + 2);
            double centerX = minX + contentW * 0.5;
            double centerY = minY + contentH * 0.5;

            uint cropX = (uint)Math.Max(0, (int)Math.Round(centerX - padded * 0.5));
            uint cropY = (uint)Math.Max(0, (int)Math.Round(centerY - padded * 0.5));
            uint cropW = Math.Min(padded, width - cropX);
            uint cropH = Math.Min(padded, height - cropY);
            uint cropSize = Math.Min(cropW, cropH);

            return (cropX, cropY, cropSize, cropSize);
        }

        private static (byte B, byte G, byte R, byte A) SampleCornerColor(
            byte[] pixels, uint width, uint height, uint cornerSize)
        {
            long totalR = 0, totalG = 0, totalB = 0, totalA = 0;
            int count = 0;

            void Sample(uint startX, uint startY)
            {
                for (uint y = startY; y < startY + cornerSize && y < height; y++)
                {
                    for (uint x = startX; x < startX + cornerSize && x < width; x++)
                    {
                        uint idx = (y * width + x) * 4;
                        totalB += pixels[idx];
                        totalG += pixels[idx + 1];
                        totalR += pixels[idx + 2];
                        totalA += pixels[idx + 3];
                        count++;
                    }
                }
            }

            Sample(0, 0);
            Sample(width - cornerSize, 0);
            Sample(0, height - cornerSize);
            Sample(width - cornerSize, height - cornerSize);

            if (count == 0) return (0, 0, 0, 0);
            return ((byte)(totalB / count), (byte)(totalG / count),
                    (byte)(totalR / count), (byte)(totalA / count));
        }

        private static bool IsColorClose(byte b1, byte g1, byte r1, byte a1,
            byte b2, byte g2, byte r2, byte a2, int tolerance)
        {
            return Math.Abs(b1 - b2) <= tolerance &&
                   Math.Abs(g1 - g2) <= tolerance &&
                   Math.Abs(r1 - r2) <= tolerance &&
                   Math.Abs(a1 - a2) <= tolerance;
        }

        private static string? TryResolveExecutablePath(string sourceAppId)
        {
            if (File.Exists(sourceAppId))
            {
                return sourceAppId;
            }

            string processName = ExtractProcessName(sourceAppId);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            try
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        string? candidate = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                        // Ignore processes we cannot inspect.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string ExtractProcessName(string sourceAppId)
        {
            string candidate = sourceAppId.Trim();
            int slashIndex = Math.Max(candidate.LastIndexOf('\\'), candidate.LastIndexOf('/'));
            if (slashIndex >= 0 && slashIndex < candidate.Length - 1)
            {
                candidate = candidate[(slashIndex + 1)..];
            }

            int bangIndex = candidate.LastIndexOf('!');
            if (bangIndex > 0)
            {
                candidate = candidate[..bangIndex];
            }

            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.GetFileNameWithoutExtension(candidate);
            }

            int dotIndex = candidate.LastIndexOf('.');
            if (dotIndex > 0 && candidate.IndexOf(' ') < 0)
            {
                string trailing = candidate[(dotIndex + 1)..];
                if (!string.IsNullOrWhiteSpace(trailing) && trailing.Equals(trailing.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    candidate = trailing;
                }
            }

            return candidate;
        }

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
        private static extern uint ExtractIconEx(
            string lpszFile,
            int nIconIndex,
            IntPtr[]? phiconLarge,
            IntPtr[]? phiconSmall,
            uint nIcons);
    }
}
