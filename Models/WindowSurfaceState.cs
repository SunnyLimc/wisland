using Windows.UI;

namespace wisland.Models
{
    internal enum WindowSurfaceMode
    {
        Compat,
        ImmersivePending,
        ImmersiveSettled,
        Notification,
        LineMode
    }

    internal readonly record struct WindowSurfaceState(
        WindowSurfaceMode Mode,
        Color HostSurfaceColor,
        Color ResizeBackfillColor,
        string VersionKey)
    {
        public static WindowSurfaceState CreateCompat(Color surfaceColor, string versionKey)
            => new(
                WindowSurfaceMode.Compat,
                surfaceColor,
                CreateOpaqueColor(surfaceColor),
                versionKey);

        public static WindowSurfaceState CreateImmersive(
            WindowSurfaceMode mode,
            ImmersiveSurfaceTokens tokens,
            string versionKey)
            => new(
                mode,
                tokens.HostSurfaceColor,
                tokens.OpaqueBackfillColor,
                versionKey);

        private static Color CreateOpaqueColor(Color color)
            => Color.FromArgb(255, color.R, color.G, color.B);
    }
}
