using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace wisland.Helpers
{
    public sealed class MediaSourceIconResolver
    {
        private readonly ConcurrentDictionary<string, Task<ImageSource?>> _iconCache =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<ImageSource?> ResolveAsync(string? sourceAppId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppId))
            {
                return Task.FromResult<ImageSource?>(null);
            }

            return _iconCache.GetOrAdd(sourceAppId, ResolveCoreAsync);
        }

        private static async Task<ImageSource?> ResolveCoreAsync(string sourceAppId)
        {
            ImageSource? packagedIcon = await TryResolvePackagedIconAsync(sourceAppId);
            if (packagedIcon != null)
            {
                return packagedIcon;
            }

            string? executablePath = TryResolveExecutablePath(sourceAppId);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            return await TryResolveExecutableIconAsync(executablePath);
        }

        private static async Task<ImageSource?> TryResolvePackagedIconAsync(string sourceAppId)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                return null;
            }

            try
            {
                AppInfo appInfo = AppInfo.GetFromAppUserModelId(sourceAppId);
                var logo = appInfo.DisplayInfo.GetLogo(new Size(32, 32));
                using var stream = await logo.OpenReadAsync();
                return await CreateBitmapImageAsync(stream);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<ImageSource?> TryResolveExecutableIconAsync(string executablePath)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(executablePath);
                using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    32,
                    ThumbnailOptions.UseCurrentScale);

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

        private static async Task<ImageSource?> CreateBitmapImageAsync(Windows.Storage.Streams.IRandomAccessStream stream)
        {
            BitmapImage bitmap = new()
            {
                DecodePixelWidth = 32
            };

            await bitmap.SetSourceAsync(stream);
            return bitmap;
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
    }
}
