using System;
using System.IO;
using System.Text.Json;
using island.Helpers;
using island.Models;

namespace island.Services
{
    /// <summary>
    /// JSON-based settings persistence to %LocalAppData%/Island/settings.json.
    /// Saves user preferences (backdrop, window position, dock state).
    /// </summary>
    public sealed class SettingsService
    {
        private const double DefaultLastY = 10;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Island", "settings.json");

        /// <summary>User's selected backdrop type name ("Mica", "Acrylic", "None").</summary>
        public BackdropType BackdropType { get; set; } = BackdropType.Mica;

        /// <summary>Last horizontal center position (in logical pixels).</summary>
        public double CenterX { get; set; }

        /// <summary>Last vertical position (in logical pixels).</summary>
        public double LastY { get; set; } = DefaultLastY;

        /// <summary>Whether the island was docked to screen top.</summary>
        public bool IsDocked { get; set; }

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
                    BackdropType = FormatBackdropType(BackdropType),
                    CenterX = SanitizeCenterX(CenterX),
                    LastY = SanitizeLastY(LastY),
                    IsDocked = IsDocked
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

        private sealed class SettingsData
        {
            public string? BackdropType { get; set; }
            public double CenterX { get; set; }
            public double LastY { get; set; }
            public bool IsDocked { get; set; }
        }
    }
}
