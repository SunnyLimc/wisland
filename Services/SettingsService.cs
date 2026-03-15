using System;
using System.IO;
using System.Text.Json;
using island.Helpers;

namespace island.Services
{
    /// <summary>
    /// JSON-based settings persistence to %LocalAppData%/Island/settings.json.
    /// Saves user preferences (backdrop, window position, dock state).
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Island", "settings.json");

        /// <summary>User's selected backdrop type name ("Mica", "Acrylic", "None").</summary>
        public string BackdropType { get; set; } = "Mica";

        /// <summary>Last horizontal center position (in logical pixels).</summary>
        public double CenterX { get; set; }

        /// <summary>Last vertical position (in logical pixels).</summary>
        public double LastY { get; set; } = 10;

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
                    BackdropType = data.BackdropType ?? "Mica";
                    CenterX = data.CenterX;
                    LastY = data.LastY;
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
                Directory.CreateDirectory(dir);

                var data = new SettingsData
                {
                    BackdropType = BackdropType,
                    CenterX = CenterX,
                    LastY = LastY,
                    IsDocked = IsDocked
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save settings");
            }
        }

        private sealed class SettingsData
        {
            public string? BackdropType { get; set; }
            public double CenterX { get; set; }
            public double LastY { get; set; }
            public bool IsDocked { get; set; }
        }
    }
}
