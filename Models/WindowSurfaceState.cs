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
        Color ResizeBackdropColor,
        string VersionKey)
    {
        public static WindowSurfaceState CreateCompat(Color surfaceColor, string versionKey)
            => CreateCompat(surfaceColor, surfaceColor, versionKey);

        public static WindowSurfaceState CreateCompat(
            Color surfaceColor,
            Color resizeBackfillColor,
            string versionKey)
            => CreateCompat(surfaceColor, resizeBackfillColor, resizeBackfillColor, versionKey);

        public static WindowSurfaceState CreateCompat(
            Color surfaceColor,
            Color resizeBackfillColor,
            Color resizeBackdropColor,
            string versionKey)
            => new(
                WindowSurfaceMode.Compat,
                surfaceColor,
                CreateOpaqueColor(resizeBackfillColor),
                CreateOpaqueColor(resizeBackdropColor),
                versionKey);

        public static WindowSurfaceState CreateImmersive(
            WindowSurfaceMode mode,
            ImmersiveSurfaceTokens tokens,
            string versionKey)
            => new(
                mode,
                tokens.HostSurfaceColor,
                tokens.OpaqueBackfillColor,
                tokens.OpaqueBackfillColor,
                versionKey);

        private static Color CreateOpaqueColor(Color color)
            => Color.FromArgb(255, color.R, color.G, color.B);
    }
}
