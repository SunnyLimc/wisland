using System;
using System.IO;

namespace wisland.Helpers
{
    /// <summary>
    /// Centralises app-data path resolution and validates that all derived paths
    /// remain within the expected base directory, guarding against path-traversal.
    /// </summary>
    internal static class SafePaths
    {
        /// <summary>
        /// Fully-resolved base directory: %LocalAppData%/Wisland.
        /// </summary>
        public static readonly string BaseDirectory = GetValidatedBaseDirectory();

        /// <summary>
        /// Returns a validated absolute path under <see cref="BaseDirectory"/>.
        /// Throws <see cref="InvalidOperationException"/> if the resolved path
        /// escapes the base directory (e.g. via ".." segments).
        /// </summary>
        public static string Combine(params string[] relativeParts)
        {
            string combined = BaseDirectory;
            foreach (var part in relativeParts)
                combined = Path.Combine(combined, part);

            string full = Path.GetFullPath(combined);

            if (!full.StartsWith(BaseDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Resolved path escapes the application data directory.");

            return full;
        }

        private static string GetValidatedBaseDirectory()
        {
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            if (string.IsNullOrEmpty(localAppData))
                throw new InvalidOperationException(
                    "Could not resolve LocalApplicationData folder.");

            string baseDir = Path.GetFullPath(Path.Combine(localAppData, "Wisland"));

            // Ensure the resolved path is still under LocalAppData
            string normalizedAppData = Path.GetFullPath(localAppData);
            if (!baseDir.StartsWith(normalizedAppData, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Wisland base directory resolved outside LocalApplicationData.");

            return baseDir;
        }
    }
}
