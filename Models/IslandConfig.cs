namespace island.Models
{
    /// <summary>
    /// Centralized constants for the Island widget. All magic numbers live here.
    /// </summary>
    public static class IslandConfig
    {
        // --- Compact State ---
        public const double CompactWidth = 200;
        public const double CompactHeight = 30;

        // --- Expanded State ---
        public const double ExpandedWidth = 400;
        public const double ExpandedHeight = 120;

        // --- Animation ---
        public const double AnimationSpeed = 25.0;
        public const double MaxDeltaTime = 0.1;
        public const double FallbackDeltaTime = 0.016;

        // --- Positioning ---
        public const double DefaultY = 10;
        public const double DockThreshold = 15;
        public const double DockPeekOffset = 5; // Reduced from 6 for "just progress" look
        public const double MaximizedDockPeekOffset = 1; // Ultra-thin line
        public const int NativeLinePhysicalHeight = 3;

        // --- Timing ---
        public const int ForegroundCheckIntervalMs = 500;
        public const int CursorTrackerIntervalMs = 50;
        public const int HoverDebounceMs = 100;
        public const int DockedHoverDelayMs = 750; // 0.75s delay to trigger expansion when docked
        public const int DockedLineExitHysteresisMs = 150;
        public const int DockedLineBoundsMarginPhysical = 8;
        public const int DefaultNotificationDurationMs = 3000;
        public const int TrackChangeNotificationDurationMs = 4000;

        // --- Opacity Thresholds ---
        public const double HitTestOpacityThreshold = 0.5;
    }
}
