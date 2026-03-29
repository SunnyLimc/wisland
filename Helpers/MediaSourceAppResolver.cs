using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Windows.ApplicationModel;

namespace wisland.Helpers
{
    internal static class MediaSourceAppResolver
    {
        private const string AppsFolderShellPath = "shell:::{4234d49b-0245-4df3-b780-3893943456e1}";
        private const string AppUserModelIdProperty = "System.AppUserModel.ID";
        private const string TargetParsingPathProperty = "System.Link.TargetParsingPath";

        private static readonly Lazy<ConcurrentDictionary<string, AppsFolderEntry>> AppsFolderEntries =
            new(BuildAppsFolderEntries, LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly Lazy<ConcurrentDictionary<string, ShortcutEntry>> ShortcutEntries =
            new(BuildShortcutEntries, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string? TryResolveDisplayName(string? sourceAppId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppId))
            {
                return null;
            }

            ShortcutEntry? shortcutEntry = TryGetShortcutEntry(sourceAppId);
            if (shortcutEntry.HasValue && !string.IsNullOrWhiteSpace(shortcutEntry.Value.DisplayName))
            {
                return shortcutEntry.Value.DisplayName;
            }

            AppsFolderEntry? appsFolderEntry = TryGetAppsFolderEntry(sourceAppId);
            if (appsFolderEntry.HasValue && !string.IsNullOrWhiteSpace(appsFolderEntry.Value.DisplayName))
            {
                return appsFolderEntry.Value.DisplayName;
            }

            AppInfo? appInfo = TryGetPackagedAppInfo(sourceAppId);
            string? packagedDisplayName = appInfo?.DisplayInfo?.DisplayName;
            return string.IsNullOrWhiteSpace(packagedDisplayName)
                ? null
                : packagedDisplayName;
        }

        public static string? TryResolveRegisteredExecutablePath(string? sourceAppId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppId))
            {
                return null;
            }

            AppsFolderEntry? appsFolderEntry = TryGetAppsFolderEntry(sourceAppId);
            if (appsFolderEntry.HasValue && !string.IsNullOrWhiteSpace(appsFolderEntry.Value.TargetParsingPath))
            {
                return appsFolderEntry.Value.TargetParsingPath;
            }

            ShortcutEntry? shortcutEntry = TryGetShortcutEntry(sourceAppId);
            return shortcutEntry.HasValue && !string.IsNullOrWhiteSpace(shortcutEntry.Value.TargetPath)
                ? shortcutEntry.Value.TargetPath
                : null;
        }

        public static AppInfo? TryGetPackagedAppInfo(string? sourceAppId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppId)
                || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                return null;
            }

            try
            {
                return AppInfo.GetFromAppUserModelId(sourceAppId);
            }
            catch
            {
                return null;
            }
        }

        internal static bool HasCustomShortcutIconLocation(string? iconLocation, string? targetPath)
        {
            ParsedIconLocation parsedIconLocation = ParseIconLocation(iconLocation);
            if (string.IsNullOrWhiteSpace(parsedIconLocation.Path))
            {
                return false;
            }

            if (parsedIconLocation.ResourceIndex != 0)
            {
                return true;
            }

            string? normalizedTargetPath = NormalizePath(targetPath);
            if (string.IsNullOrWhiteSpace(normalizedTargetPath))
            {
                return true;
            }

            return !string.Equals(
                parsedIconLocation.Path,
                normalizedTargetPath,
                StringComparison.OrdinalIgnoreCase);
        }

        internal static ParsedIconLocation ParseIconLocation(string? iconLocation)
        {
            if (string.IsNullOrWhiteSpace(iconLocation))
            {
                return ParsedIconLocation.Empty;
            }

            string trimmed = iconLocation.Trim();
            int separatorIndex = trimmed.LastIndexOf(',');
            if (separatorIndex < 0)
            {
                return new ParsedIconLocation(
                    NormalizePath(trimmed),
                    ResourceIndex: 0);
            }

            string pathPart = trimmed[..separatorIndex].Trim().Trim('"');
            string indexPart = trimmed[(separatorIndex + 1)..].Trim();
            _ = int.TryParse(indexPart, out int resourceIndex);

            return new ParsedIconLocation(
                NormalizePath(pathPart),
                resourceIndex);
        }

        internal static RegisteredShortcut? TryResolveRegisteredShortcut(string? sourceAppId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppId))
            {
                return null;
            }

            ShortcutEntry? shortcutEntry = TryGetShortcutEntry(sourceAppId);
            if (!shortcutEntry.HasValue
                || string.IsNullOrWhiteSpace(shortcutEntry.Value.ShortcutPath))
            {
                return null;
            }

            string? iconLocation = shortcutEntry.Value.IconLocation;
            return new RegisteredShortcut(
                shortcutEntry.Value.ShortcutPath,
                shortcutEntry.Value.TargetPath,
                iconLocation,
                ParseIconLocation(iconLocation),
                shortcutEntry.Value.HasCustomIconLocation);
        }

        private static AppsFolderEntry? TryGetAppsFolderEntry(string sourceAppId)
        {
            if (AppsFolderEntries.Value.TryGetValue(sourceAppId, out AppsFolderEntry entry))
            {
                return entry;
            }

            AppsFolderEntry? resolved = TryResolveAppsFolderEntry(sourceAppId);
            if (resolved.HasValue)
            {
                AppsFolderEntries.Value[sourceAppId] = resolved.Value;
            }

            return resolved;
        }

        private static ShortcutEntry? TryGetShortcutEntry(string sourceAppId)
        {
            if (ShortcutEntries.Value.TryGetValue(sourceAppId, out ShortcutEntry entry))
            {
                return entry;
            }

            return null;
        }

        private static ConcurrentDictionary<string, AppsFolderEntry> BuildAppsFolderEntries()
        {
            ConcurrentDictionary<string, AppsFolderEntry> entries =
                new(StringComparer.OrdinalIgnoreCase);

            dynamic? appsFolder = TryGetAppsFolder();
            if (appsFolder == null)
            {
                return entries;
            }

            try
            {
                foreach (dynamic item in appsFolder.Items())
                {
                    AppsFolderEntry? entry = TryCreateAppsFolderEntry(item);
                    if (entry.HasValue)
                    {
                        entries.TryAdd(entry.Value.AppId, entry.Value);
                    }
                }
            }
            catch
            {
            }

            return entries;
        }

        private static ConcurrentDictionary<string, ShortcutEntry> BuildShortcutEntries()
        {
            ConcurrentDictionary<string, ShortcutEntry> entries =
                new(StringComparer.OrdinalIgnoreCase);

            dynamic? shell = TryGetShellApplication();
            dynamic? shortcutShell = TryGetShortcutShell();
            if (shell == null || shortcutShell == null)
            {
                return entries;
            }

            foreach (string shortcutPath in EnumerateStartMenuShortcutPaths())
            {
                ShortcutEntry? entry = TryCreateShortcutEntry(shell, shortcutShell, shortcutPath);
                if (!entry.HasValue)
                {
                    continue;
                }

                if (entries.TryGetValue(entry.Value.AppId, out ShortcutEntry existingEntry))
                {
                    if (GetShortcutEntryRank(entry.Value) > GetShortcutEntryRank(existingEntry))
                    {
                        entries[entry.Value.AppId] = entry.Value;
                    }

                    continue;
                }

                entries[entry.Value.AppId] = entry.Value;
            }

            return entries;
        }

        private static AppsFolderEntry? TryResolveAppsFolderEntry(string sourceAppId)
        {
            dynamic? appsFolder = TryGetAppsFolder();
            if (appsFolder == null)
            {
                return null;
            }

            try
            {
                dynamic? item = appsFolder.ParseName(sourceAppId);
                return item == null
                    ? null
                    : TryCreateAppsFolderEntry(item);
            }
            catch
            {
                return null;
            }
        }

        private static dynamic? TryGetAppsFolder()
        {
            try
            {
                dynamic? shell = TryGetShellApplication();
                return shell?.NameSpace(AppsFolderShellPath);
            }
            catch
            {
                return null;
            }
        }

        private static dynamic? TryGetShellApplication()
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    return null;
                }

                return Activator.CreateInstance(shellType);
            }
            catch
            {
                return null;
            }
        }

        private static dynamic? TryGetShortcutShell()
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return null;
                }

                return Activator.CreateInstance(shellType);
            }
            catch
            {
                return null;
            }
        }

        private static AppsFolderEntry? TryCreateAppsFolderEntry(dynamic item)
        {
            string? appId = TryReadString(() => item.Path);
            if (string.IsNullOrWhiteSpace(appId))
            {
                return null;
            }

            string? displayName = TryReadString(() => item.Name);
            string? targetParsingPath = TryReadString(() => item.ExtendedProperty(TargetParsingPathProperty));

            return new AppsFolderEntry(
                appId,
                displayName ?? string.Empty,
                targetParsingPath);
        }

        private static ShortcutEntry? TryCreateShortcutEntry(dynamic shell, dynamic shortcutShell, string shortcutPath)
        {
            try
            {
                string? directoryPath = Path.GetDirectoryName(shortcutPath);
                string? fileName = Path.GetFileName(shortcutPath);
                if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName))
                {
                    return null;
                }

                dynamic? folder = shell.NameSpace(directoryPath);
                dynamic? item = folder?.ParseName(fileName);
                string? appId = item == null
                    ? null
                    : TryReadString(() => item.ExtendedProperty(AppUserModelIdProperty));
                if (string.IsNullOrWhiteSpace(appId))
                {
                    return null;
                }

                dynamic shortcut = shortcutShell.CreateShortcut(shortcutPath);
                string? displayName = item == null
                    ? null
                    : TryReadString(() => item.Name);
                string? targetPath = NormalizePath(TryReadString(() => shortcut.TargetPath));
                string? iconLocation = TryReadString(() => shortcut.IconLocation);

                return new ShortcutEntry(
                    appId,
                    displayName ?? string.Empty,
                    shortcutPath,
                    targetPath,
                    iconLocation,
                    HasCustomShortcutIconLocation(iconLocation, targetPath));
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> EnumerateStartMenuShortcutPaths()
        {
            EnumerationOptions options = new()
            {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false
            };

            foreach (string programsDirectory in GetStartMenuDirectories())
            {
                if (string.IsNullOrWhiteSpace(programsDirectory) || !Directory.Exists(programsDirectory))
                {
                    continue;
                }

                IEnumerable<string> shortcutPaths;
                try
                {
                    shortcutPaths = Directory.EnumerateFiles(programsDirectory, "*.lnk", options);
                }
                catch
                {
                    continue;
                }

                foreach (string shortcutPath in shortcutPaths)
                {
                    yield return shortcutPath;
                }
            }
        }

        private static IEnumerable<string> GetStartMenuDirectories()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        }

        private static int GetShortcutEntryRank(ShortcutEntry entry)
            => (entry.HasCustomIconLocation ? 2 : 0)
                + (!string.IsNullOrWhiteSpace(entry.TargetPath) ? 1 : 0);

        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            try
            {
                return Path.GetFullPath(expanded);
            }
            catch
            {
                return expanded;
            }
        }

        private static string? TryReadString(Func<object?> getter)
        {
            try
            {
                object? value = getter();
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private readonly record struct AppsFolderEntry(
            string AppId,
            string DisplayName,
            string? TargetParsingPath);

        private readonly record struct ShortcutEntry(
            string AppId,
            string DisplayName,
            string ShortcutPath,
            string? TargetPath,
            string? IconLocation,
            bool HasCustomIconLocation);

        internal readonly record struct ParsedIconLocation(
            string? Path,
            int ResourceIndex)
        {
            public static ParsedIconLocation Empty { get; } = new(null, 0);
        }

        internal readonly record struct RegisteredShortcut(
            string ShortcutPath,
            string? TargetPath,
            string? IconLocation,
            ParsedIconLocation ParsedIconLocation,
            bool HasCustomIconLocation);
    }
}
