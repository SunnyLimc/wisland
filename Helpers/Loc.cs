using Microsoft.Windows.ApplicationModel.Resources;

namespace wisland.Helpers
{
    /// <summary>
    /// Lightweight localization helper wrapping MRT Core.
    /// Call <see cref="Initialize"/> once at startup before any UI is created.
    /// </summary>
    public static class Loc
    {
        private static ResourceManager? _resourceManager;

        /// <summary>
        /// <c>true</c> when the app runs with package identity and
        /// <see cref="Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride"/>
        /// can be set. When <c>false</c> the app is unpackaged and the language
        /// follows the OS setting.
        /// </summary>
        public static bool CanOverrideLanguage { get; private set; }

        /// <summary>
        /// Initialise the resource manager and apply the persisted language override (if any).
        /// Must be called once before any window is created.
        /// </summary>
        public static void Initialize(string? languageOverride)
        {
            try
            {
                _resourceManager = new ResourceManager();
            }
            catch
            {
                // WinRT activation is unavailable (e.g. unit-test host).
                return;
            }

            if (!string.IsNullOrWhiteSpace(languageOverride))
            {
                try
                {
                    Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = languageOverride;
                    CanOverrideLanguage = true;
                }
                catch
                {
                    // Unpackaged apps cannot set PrimaryLanguageOverride.
                    CanOverrideLanguage = false;
                }
            }
            else
            {
                // Probe whether we *could* override (packaged mode check).
                try
                {
                    // Read-only access to detect packaged mode.
                    _ = Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
                    CanOverrideLanguage = true;
                }
                catch
                {
                    CanOverrideLanguage = false;
                }
            }
        }

        /// <summary>
        /// Look up a localized string by resource key (e.g. "Tray/Show").
        /// Returns the key itself when the resource cannot be found so the UI is never blank.
        /// </summary>
        public static string GetString(string key)
        {
            if (_resourceManager is null)
                return key;

            try
            {
                var candidate = _resourceManager.MainResourceMap.GetValue($"Resources/{key}");
                return candidate.ValueAsString;
            }
            catch
            {
                return key;
            }
        }

        /// <summary>
        /// Look up a localized format string and apply <paramref name="args"/>.
        /// Example resource value: "Cached entries: {0}"
        /// </summary>
        public static string GetFormatted(string key, params object[] args)
            => string.Format(GetString(key), args);
    }
}
