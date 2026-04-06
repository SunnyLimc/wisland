using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using wisland.Helpers;
using wisland.Models;

using LogLevel = wisland.Helpers.LogLevel;

namespace wisland.Services
{
    /// <summary>
    /// JSON-based settings persistence to %LocalAppData%/Wisland/settings.json.
    /// Saves user preferences (backdrop, window position, dock state).
    /// </summary>
    public sealed class SettingsService
    {
        private const double DefaultLastY = 10;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wisland", "settings.json");

        /// <summary>User's selected backdrop type name ("Mica", "Acrylic", "None").</summary>
        public BackdropType BackdropType { get; set; } = BackdropType.Mica;

        /// <summary>Last horizontal center position (in logical pixels).</summary>
        public double CenterX { get; set; }

        /// <summary>Last vertical position (in logical pixels).</summary>
        public double LastY { get; set; } = DefaultLastY;

        /// <summary>Whether the island was docked to screen top.</summary>
        public bool IsDocked { get; set; }

        /// <summary>Last resolved anchor point in physical pixels.</summary>
        public int? AnchorPhysicalX { get; set; }

        /// <summary>Last resolved anchor point in physical pixels.</summary>
        public int? AnchorPhysicalY { get; set; }

        /// <summary>Last display-relative horizontal center position in logical pixels.</summary>
        public double? RelativeCenterX { get; set; }

        /// <summary>Last display-relative top position in logical pixels.</summary>
        public double? RelativeTopY { get; set; }

        /// <summary>Configured AI model profiles.</summary>
        public List<AiModelProfile> AiModels { get; set; } = new();

        /// <summary>ID of the active AI model used for song resolution.</summary>
        public string? ActiveAiModelId { get; set; }

        /// <summary>Whether AI song metadata override is enabled.</summary>
        public bool AiSongOverrideEnabled { get; set; }

        /// <summary>User-configured log level. Null means use compile-time default.</summary>
        public LogLevel? LogLevel { get; set; }

        /// <summary>BCP-47 language tag override (e.g. "ja", "zh-Hans"). Null means follow OS.</summary>
        public string? Language { get; set; }

        /// <summary>Preferred language code for AI song prompt (e.g. "zh-Hans", "ja"). Null means no language preference.</summary>
        public string? AiPreferredLanguage { get; set; }

        /// <summary>Target market name used in the AI prompt (e.g. "Mainland China"). Null means use default for the language.</summary>
        public string? AiTargetMarket { get; set; }

        /// <summary>Whether to use a native-language prompt template when available for the preferred language.</summary>
        public bool AiPreferNativePrompt { get; set; }

        /// <summary>
        /// Load settings from disk. Returns silently with defaults if file doesn't exist or is corrupted.
        /// </summary>
        public void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return;

                var json = File.ReadAllText(SettingsPath);
                var data = JsonSerializer.Deserialize<SettingsData>(json);
                if (data != null)
                {
                    BackdropType = ParseBackdropType(data.BackdropType);
                    CenterX = SanitizeCenterX(data.CenterX);
                    LastY = SanitizeLastY(data.LastY);
                    IsDocked = data.IsDocked;
                    AnchorPhysicalX = SanitizeAnchorPhysical(data.AnchorPhysicalX);
                    AnchorPhysicalY = SanitizeAnchorPhysical(data.AnchorPhysicalY);
                    RelativeCenterX = SanitizeRelativeCenterX(data.RelativeCenterX);
                    RelativeTopY = SanitizeRelativeTopY(data.RelativeTopY);
                    AiModels = DeserializeAiModels(data.AiModels);
                    ActiveAiModelId = data.ActiveAiModelId;
                    AiSongOverrideEnabled = data.AiSongOverrideEnabled;
                    LogLevel = ParseLogLevel(data.LogLevel);
                    Language = data.Language;
                    AiPreferredLanguage = data.AiPreferredLanguage;
                    AiTargetMarket = data.AiTargetMarket;
                    AiPreferNativePrompt = data.AiPreferNativePrompt;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load settings, using defaults: {ex.Message}");
            }
        }

        /// <summary>
        /// Persist current settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath)!;
                var tempPath = SettingsPath + ".tmp";
                Directory.CreateDirectory(dir);

                var data = new SettingsData
                {
                    SettingsVersion = 1,
                    BackdropType = FormatBackdropType(BackdropType),
                    CenterX = SanitizeCenterX(CenterX),
                    LastY = SanitizeLastY(LastY),
                    IsDocked = IsDocked,
                    AnchorPhysicalX = SanitizeAnchorPhysical(AnchorPhysicalX),
                    AnchorPhysicalY = SanitizeAnchorPhysical(AnchorPhysicalY),
                    RelativeCenterX = SanitizeRelativeCenterX(RelativeCenterX),
                    RelativeTopY = SanitizeRelativeTopY(RelativeTopY),
                    AiModels = SerializeAiModels(AiModels),
                    ActiveAiModelId = ActiveAiModelId,
                    AiSongOverrideEnabled = AiSongOverrideEnabled,
                    LogLevel = LogLevel?.ToString(),
                    Language = Language,
                    AiPreferredLanguage = AiPreferredLanguage,
                    AiTargetMarket = AiTargetMarket,
                    AiPreferNativePrompt = AiPreferNativePrompt
                };

                var json = JsonSerializer.Serialize(data, JsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsPath, true);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save settings");
            }
        }

        private static BackdropType ParseBackdropType(string? value) => value switch
        {
            "Acrylic" => BackdropType.Acrylic,
            "None" => BackdropType.None,
            _ => BackdropType.Mica,
        };

        private static string FormatBackdropType(BackdropType value) => value.ToString();

        private static double SanitizeCenterX(double value)
            => double.IsFinite(value) && value >= 0 ? value : 0;

        private static double SanitizeLastY(double value)
            => double.IsFinite(value) && value >= 0 ? value : DefaultLastY;

        private static int? SanitizeAnchorPhysical(int? value)
            => value.HasValue ? value : null;

        private static double? SanitizeRelativeCenterX(double? value)
            => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value : null;

        private static double? SanitizeRelativeTopY(double? value)
            => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value : null;

        private static LogLevel? ParseLogLevel(string? value)
            => Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : null;

        private static string ProtectApiKey(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string UnprotectApiKey(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText)) return string.Empty;
            try
            {
                byte[] encrypted = Convert.FromBase64String(protectedText);
                byte[] plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // If decryption fails (e.g., migrated from another user), return empty.
                return string.Empty;
            }
        }

        private static List<AiModelProfileData>? SerializeAiModels(List<AiModelProfile> models)
        {
            if (models.Count == 0) return null;
            var result = new List<AiModelProfileData>(models.Count);
            foreach (var m in models)
            {
                result.Add(new AiModelProfileData
                {
                    Id = m.Id,
                    DisplayName = m.DisplayName,
                    Provider = AiModelProviderNames.Normalize(m.Provider),
                    Endpoint = m.Endpoint,
                    ProtectedApiKey = ProtectApiKey(m.ApiKey),
                    ModelId = m.ModelId,
                    ReasoningEffort = m.ReasoningEffort,
                    GoogleGroundingEnabled = m.GoogleGroundingEnabled,
                    Temperature = m.Temperature
                });
            }
            return result;
        }

        private static List<AiModelProfile> DeserializeAiModels(List<AiModelProfileData>? data)
        {
            if (data == null || data.Count == 0) return new();
            var result = new List<AiModelProfile>(data.Count);
            foreach (var d in data)
            {
                string provider = AiModelProviderNames.Normalize(d.Provider);
                result.Add(new AiModelProfile
                {
                    Id = d.Id ?? Guid.NewGuid().ToString(),
                    DisplayName = d.DisplayName ?? string.Empty,
                    Provider = provider,
                    Endpoint = d.Endpoint ?? string.Empty,
                    ApiKey = UnprotectApiKey(d.ProtectedApiKey ?? string.Empty),
                    ModelId = d.ModelId ?? string.Empty,
                    ReasoningEffort = d.ReasoningEffort,
                    GoogleGroundingEnabled = d.GoogleGroundingEnabled
                        ?? string.Equals(provider, nameof(AiModelProvider.GoogleAIStudio), StringComparison.Ordinal),
                    Temperature = d.Temperature ?? 1.0
                });
            }
            return result;
        }

        private sealed class SettingsData
        {
            public int SettingsVersion { get; set; }
            public string? BackdropType { get; set; }
            public double CenterX { get; set; }
            public double LastY { get; set; }
            public bool IsDocked { get; set; }
            public int? AnchorPhysicalX { get; set; }
            public int? AnchorPhysicalY { get; set; }
            public double? RelativeCenterX { get; set; }
            public double? RelativeTopY { get; set; }
            public List<AiModelProfileData>? AiModels { get; set; }
            public string? ActiveAiModelId { get; set; }
            public bool AiSongOverrideEnabled { get; set; }
            public string? LogLevel { get; set; }
            public string? Language { get; set; }
            public string? AiPreferredLanguage { get; set; }
            public string? AiTargetMarket { get; set; }
            public bool AiPreferNativePrompt { get; set; }
        }

        private sealed class AiModelProfileData
        {
            public string? Id { get; set; }
            public string? DisplayName { get; set; }
            public string? Provider { get; set; }
            public string? Endpoint { get; set; }
            public string? ProtectedApiKey { get; set; }
            public string? ModelId { get; set; }
            public string? ReasoningEffort { get; set; }
            public bool? GoogleGroundingEnabled { get; set; }
            public double? Temperature { get; set; }
        }
    }
}
