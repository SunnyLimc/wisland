using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

                var logo = appInfo.DisplayInfo.GetLogo(new Size(32, 32));
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

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
        private static extern uint ExtractIconEx(
            string lpszFile,
            int nIconIndex,
            IntPtr[]? phiconLarge,
            IntPtr[]? phiconSmall,
            uint nIcons);
    }
}
