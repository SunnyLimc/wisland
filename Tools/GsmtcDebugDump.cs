#:property TargetFramework=net10.0-windows10.0.19041.0
#:property Nullable=enable

#pragma warning disable IL2026
#pragma warning disable IL3050
#pragma warning disable IL2072

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Media.Control;

// Usage:
//   dotnet run --file .\Tools\GsmtcDebugDump.cs
//   dotnet run --file .\Tools\GsmtcDebugDump.cs -- C:\temp\gsmtc-dump.json
//   dotnet run --file .\Tools\GsmtcDebugDump.cs -- --stdout

string? explicitOutputPath = args.FirstOrDefault(arg => !string.Equals(arg, "--stdout", StringComparison.OrdinalIgnoreCase));
bool writeToStdout = args.Any(arg => string.Equals(arg, "--stdout", StringComparison.OrdinalIgnoreCase));

GlobalSystemMediaTransportControlsSessionManager manager =
    await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
GlobalSystemMediaTransportControlsSession? currentSession = manager.GetCurrentSession();

GsmtcDebugDump dump = new(
    ExportedAtUtc: DateTimeOffset.UtcNow,
    MachineName: Environment.MachineName,
    UserName: Environment.UserName,
    SessionCount: sessions.Count,
    CurrentSessionObjectId: currentSession == null ? null : FormatSessionObjectId(currentSession),
    CurrentSessionSourceAppUserModelId: TryGet(() => currentSession?.SourceAppUserModelId),
    Sessions: await Task.WhenAll(sessions.Select(session => CaptureSessionAsync(session, currentSession))));

JsonSerializerOptions jsonOptions = new()
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    WriteIndented = true
};

string json = JsonSerializer.Serialize(dump, jsonOptions);
if (writeToStdout)
{
    Console.WriteLine(json);
}
else
{
    string outputPath = ResolveOutputPath(explicitOutputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllTextAsync(outputPath, json);
    Console.WriteLine($"Exported {dump.SessionCount} GSMTC session(s) to:");
    Console.WriteLine(outputPath);
}

static async Task<GsmtcSessionDebugInfo> CaptureSessionAsync(
    GlobalSystemMediaTransportControlsSession session,
    GlobalSystemMediaTransportControlsSession? currentSession)
{
    string? sourceAppUserModelId = TryGet(() => session.SourceAppUserModelId);
    GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo = TryGet(session.GetPlaybackInfo);
    GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = TryGet(session.GetTimelineProperties);
    GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties;
    try
    {
        mediaProperties = await session.TryGetMediaPropertiesAsync();
    }
    catch
    {
        mediaProperties = null;
    }

    GlobalSystemMediaTransportControlsSessionPlaybackControls? controls = playbackInfo?.Controls;
    return new GsmtcSessionDebugInfo(
        SessionObjectId: FormatSessionObjectId(session),
        IsCurrentSession: currentSession != null && ReferenceEquals(session, currentSession),
        SourceAppUserModelId: sourceAppUserModelId,
        App: AppDebugResolver.Resolve(sourceAppUserModelId),
        Playback: new PlaybackDebugInfo(
            PlaybackStatus: playbackInfo?.PlaybackStatus.ToString(),
            PlaybackType: TryGet(() => playbackInfo?.PlaybackType.ToString()),
            AutoRepeatMode: TryGet(() => playbackInfo?.AutoRepeatMode.ToString()),
            Controls: controls == null
                ? null
                : new PlaybackControlsDebugInfo(
                    controls.IsPlayEnabled,
                    controls.IsPauseEnabled,
                    controls.IsStopEnabled,
                    controls.IsNextEnabled,
                    controls.IsPreviousEnabled,
                    controls.IsFastForwardEnabled,
                    controls.IsRewindEnabled,
                    controls.IsShuffleEnabled,
                    controls.IsRepeatEnabled,
                    controls.IsRecordEnabled,
                    controls.IsChannelUpEnabled,
                    controls.IsChannelDownEnabled)),
        Timeline: timeline == null
            ? null
            : new TimelineDebugInfo(
                HasTimeline: timeline.EndTime > TimeSpan.Zero,
                StartTime: timeline.StartTime,
                EndTime: timeline.EndTime,
                MinSeekTime: timeline.MinSeekTime,
                MaxSeekTime: timeline.MaxSeekTime,
                Position: timeline.Position,
                LastUpdatedTime: timeline.LastUpdatedTime),
        Media: mediaProperties == null
            ? null
            : new MediaPropertiesDebugInfo(
                mediaProperties.Title,
                mediaProperties.Artist,
                mediaProperties.AlbumTitle,
                mediaProperties.AlbumArtist,
                mediaProperties.Subtitle,
                mediaProperties.TrackNumber,
                mediaProperties.AlbumTrackCount,
                TryGet(() => mediaProperties.PlaybackType.ToString()),
                mediaProperties.Genres?.ToArray() ?? Array.Empty<string>(),
                mediaProperties.Thumbnail != null));
}

static string ResolveOutputPath(string? explicitOutputPath)
{
    if (!string.IsNullOrWhiteSpace(explicitOutputPath))
    {
        return Path.GetFullPath(explicitOutputPath);
    }

    string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "gsmtc-debug");
    string fileName = $"gsmtc-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.json";
    return Path.Combine(directory, fileName);
}

static string FormatSessionObjectId(object instance)
    => FormattableString.Invariant($"0x{RuntimeHelpers.GetHashCode(instance):X8}");

static T? TryGet<T>(Func<T> getter)
{
    try
    {
        return getter();
    }
    catch
    {
        return default;
    }
}

file static class AppDebugResolver
{
    private const string AppsFolderShellPath = "shell:::{4234d49b-0245-4df3-b780-3893943456e1}";
    private const string AppUserModelIdProperty = "System.AppUserModel.ID";
    private const string TargetParsingPathProperty = "System.Link.TargetParsingPath";

    private static readonly Lazy<Dictionary<string, ShortcutEntry>> ShortcutEntries =
        new(BuildShortcutEntries, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Dictionary<string, AppsFolderEntry>> AppsFolderEntries =
        new(BuildAppsFolderEntries, LazyThreadSafetyMode.ExecutionAndPublication);

    public static AppDebugInfo Resolve(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return new AppDebugInfo(
                SourceAppUserModelId: null,
                ResolvedDisplayNameCandidate: null,
                PackagedApp: null,
                AppsFolder: null,
                Shortcut: null,
                FallbackExecutablePathGuess: null);
        }

        ShortcutEntry? shortcut = TryGetShortcutEntry(sourceAppUserModelId);
        AppsFolderEntry? appsFolder = TryGetAppsFolderEntry(sourceAppUserModelId);
        PackagedAppDebugInfo? packagedApp = TryGetPackagedAppInfo(sourceAppUserModelId);

        string? resolvedDisplayName =
            shortcut?.DisplayName
            ?? appsFolder?.DisplayName
            ?? packagedApp?.DisplayName
            ?? FormatFallbackDisplayName(sourceAppUserModelId);

        return new AppDebugInfo(
            SourceAppUserModelId: sourceAppUserModelId,
            ResolvedDisplayNameCandidate: resolvedDisplayName,
            PackagedApp: packagedApp,
            AppsFolder: appsFolder == null
                ? null
                : new AppsFolderDebugInfo(
                    appsFolder.Value.DisplayName,
                    appsFolder.Value.TargetParsingPath),
            Shortcut: shortcut == null
                ? null
                : new ShortcutDebugInfo(
                    shortcut.Value.DisplayName,
                    shortcut.Value.ShortcutPath,
                    shortcut.Value.TargetPath,
                    shortcut.Value.IconLocation,
                    ParseIconLocation(shortcut.Value.IconLocation),
                    HasCustomShortcutIconLocation(shortcut.Value.IconLocation, shortcut.Value.TargetPath)),
            FallbackExecutablePathGuess: TryResolveExecutablePathGuess(sourceAppUserModelId));
    }

    private static ShortcutEntry? TryGetShortcutEntry(string sourceAppUserModelId)
    {
        return ShortcutEntries.Value.TryGetValue(sourceAppUserModelId, out ShortcutEntry entry)
            ? entry
            : null;
    }

    private static AppsFolderEntry? TryGetAppsFolderEntry(string sourceAppUserModelId)
    {
        if (AppsFolderEntries.Value.TryGetValue(sourceAppUserModelId, out AppsFolderEntry entry))
        {
            return entry;
        }

        AppsFolderEntry? resolved = TryResolveAppsFolderEntry(sourceAppUserModelId);
        if (resolved.HasValue)
        {
            AppsFolderEntries.Value[sourceAppUserModelId] = resolved.Value;
        }

        return resolved;
    }

    private static Dictionary<string, ShortcutEntry> BuildShortcutEntries()
    {
        Dictionary<string, ShortcutEntry> entries = new(StringComparer.OrdinalIgnoreCase);

        dynamic? shell = TryCreateComObject("Shell.Application");
        dynamic? shortcutShell = TryCreateComObject("WScript.Shell");
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

            if (entries.TryGetValue(entry.Value.AppId, out ShortcutEntry existing))
            {
                if (GetShortcutRank(entry.Value) > GetShortcutRank(existing))
                {
                    entries[entry.Value.AppId] = entry.Value;
                }

                continue;
            }

            entries[entry.Value.AppId] = entry.Value;
        }

        return entries;
    }

    private static Dictionary<string, AppsFolderEntry> BuildAppsFolderEntries()
    {
        Dictionary<string, AppsFolderEntry> entries = new(StringComparer.OrdinalIgnoreCase);

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

    private static AppsFolderEntry? TryResolveAppsFolderEntry(string sourceAppUserModelId)
    {
        dynamic? appsFolder = TryGetAppsFolder();
        if (appsFolder == null)
        {
            return null;
        }

        try
        {
            dynamic? item = appsFolder.ParseName(sourceAppUserModelId);
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
            dynamic? shell = TryCreateComObject("Shell.Application");
            return shell?.NameSpace(AppsFolderShellPath);
        }
        catch
        {
            return null;
        }
    }

    private static dynamic? TryCreateComObject(string progId)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID(progId);
            return type == null ? null : Activator.CreateInstance(type);
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

        return new AppsFolderEntry(
            appId,
            TryReadString(() => item.Name),
            TryReadString(() => item.ExtendedProperty(TargetParsingPathProperty)));
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
            return new ShortcutEntry(
                appId,
                item == null ? null : TryReadString(() => item.Name),
                shortcutPath,
                NormalizePath(TryReadString(() => shortcut.TargetPath)),
                TryReadString(() => shortcut.IconLocation));
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

    private static PackagedAppDebugInfo? TryGetPackagedAppInfo(string sourceAppUserModelId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return null;
        }

        try
        {
            AppInfo? appInfo = AppInfo.GetFromAppUserModelId(sourceAppUserModelId);
            return appInfo == null
                ? null
                : new PackagedAppDebugInfo(
                    appInfo.AppUserModelId,
                    appInfo.PackageFamilyName,
                    appInfo.DisplayInfo.DisplayName,
                    appInfo.DisplayInfo.Description);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveExecutablePathGuess(string sourceAppUserModelId)
    {
        if (File.Exists(sourceAppUserModelId))
        {
            return sourceAppUserModelId;
        }

        string processName = ExtractProcessName(sourceAppUserModelId);
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
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ExtractProcessName(string sourceAppUserModelId)
    {
        string candidate = sourceAppUserModelId.Trim();
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
            if (!string.IsNullOrWhiteSpace(trailing)
                && trailing.Equals(trailing.ToLowerInvariant(), StringComparison.Ordinal))
            {
                candidate = trailing;
            }
        }

        return candidate;
    }

    private static ParsedIconLocationDebugInfo? ParseIconLocation(string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return null;
        }

        string trimmed = iconLocation.Trim();
        int separatorIndex = trimmed.LastIndexOf(',');
        if (separatorIndex < 0)
        {
            return new ParsedIconLocationDebugInfo(
                NormalizePath(trimmed),
                0);
        }

        string pathPart = trimmed[..separatorIndex].Trim().Trim('"');
        string indexPart = trimmed[(separatorIndex + 1)..].Trim();
        _ = int.TryParse(indexPart, out int resourceIndex);

        return new ParsedIconLocationDebugInfo(
            NormalizePath(pathPart),
            resourceIndex);
    }

    private static bool HasCustomShortcutIconLocation(string? iconLocation, string? targetPath)
    {
        ParsedIconLocationDebugInfo? parsed = ParseIconLocation(iconLocation);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Path))
        {
            return false;
        }

        if (parsed.ResourceIndex != 0)
        {
            return true;
        }

        string? normalizedTargetPath = NormalizePath(targetPath);
        if (string.IsNullOrWhiteSpace(normalizedTargetPath))
        {
            return true;
        }

        return !string.Equals(parsed.Path, normalizedTargetPath, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetShortcutRank(ShortcutEntry entry)
        => (HasCustomShortcutIconLocation(entry.IconLocation, entry.TargetPath) ? 2 : 0)
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

    private static string? FormatFallbackDisplayName(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return null;
        }

        string source = sourceAppUserModelId.Trim();
        int bangIndex = source.LastIndexOf('!');
        if (bangIndex >= 0 && bangIndex < source.Length - 1)
        {
            source = source[(bangIndex + 1)..];
        }
        else
        {
            int slashIndex = Math.Max(source.LastIndexOf('\\'), source.LastIndexOf('/'));
            if (slashIndex >= 0 && slashIndex < source.Length - 1)
            {
                source = source[(slashIndex + 1)..];
            }
        }

        if (source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            source = source[..^4];
        }

        source = source.Replace('_', ' ').Replace('.', ' ').Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(source.ToLowerInvariant());
    }

    private readonly record struct ShortcutEntry(
        string AppId,
        string? DisplayName,
        string ShortcutPath,
        string? TargetPath,
        string? IconLocation);

    private readonly record struct AppsFolderEntry(
        string AppId,
        string? DisplayName,
        string? TargetParsingPath);
}

file sealed record GsmtcDebugDump(
    DateTimeOffset ExportedAtUtc,
    string MachineName,
    string UserName,
    int SessionCount,
    string? CurrentSessionObjectId,
    string? CurrentSessionSourceAppUserModelId,
    GsmtcSessionDebugInfo[] Sessions);

file sealed record GsmtcSessionDebugInfo(
    string SessionObjectId,
    bool IsCurrentSession,
    string? SourceAppUserModelId,
    AppDebugInfo App,
    PlaybackDebugInfo Playback,
    TimelineDebugInfo? Timeline,
    MediaPropertiesDebugInfo? Media);

file sealed record AppDebugInfo(
    string? SourceAppUserModelId,
    string? ResolvedDisplayNameCandidate,
    PackagedAppDebugInfo? PackagedApp,
    AppsFolderDebugInfo? AppsFolder,
    ShortcutDebugInfo? Shortcut,
    string? FallbackExecutablePathGuess);

file sealed record PackagedAppDebugInfo(
    string? AppUserModelId,
    string? PackageFamilyName,
    string? DisplayName,
    string? Description);

file sealed record AppsFolderDebugInfo(
    string? DisplayName,
    string? TargetParsingPath);

file sealed record ShortcutDebugInfo(
    string? DisplayName,
    string ShortcutPath,
    string? TargetPath,
    string? IconLocation,
    ParsedIconLocationDebugInfo? ParsedIconLocation,
    bool HasCustomIconLocation);

file sealed record ParsedIconLocationDebugInfo(
    string? Path,
    int ResourceIndex);

file sealed record PlaybackDebugInfo(
    string? PlaybackStatus,
    string? PlaybackType,
    string? AutoRepeatMode,
    PlaybackControlsDebugInfo? Controls);

file sealed record PlaybackControlsDebugInfo(
    bool IsPlayEnabled,
    bool IsPauseEnabled,
    bool IsStopEnabled,
    bool IsNextEnabled,
    bool IsPreviousEnabled,
    bool IsFastForwardEnabled,
    bool IsRewindEnabled,
    bool IsShuffleEnabled,
    bool IsRepeatEnabled,
    bool IsRecordEnabled,
    bool IsChannelUpEnabled,
    bool IsChannelDownEnabled);

file sealed record TimelineDebugInfo(
    bool HasTimeline,
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan MinSeekTime,
    TimeSpan MaxSeekTime,
    TimeSpan Position,
    DateTimeOffset LastUpdatedTime);

file sealed record MediaPropertiesDebugInfo(
    string? Title,
    string? Artist,
    string? AlbumTitle,
    string? AlbumArtist,
    string? Subtitle,
    int TrackNumber,
    int AlbumTrackCount,
    string? PlaybackType,
    string[] Genres,
    bool HasThumbnail);
