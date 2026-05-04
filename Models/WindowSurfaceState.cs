using Windows.UI;
using wisland.Helpers;

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
        // This may intentionally differ from ResizeBackfillColor. The window
        // backdrop is only visible through the resize gap/transparent content,
        // so it is matched to the left edge that DWM exposes during resize.
        // Immersive host/backfill instead use the averaged right+bottom edges.
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
                tokens.LeftEdgeBackdropColor,
                versionKey);

        private static Color CreateOpaqueColor(Color color)
            => WindowSurfaceColorMath.CreateOpaque(color);
    }
}
