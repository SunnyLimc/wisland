namespace wisland.Models
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
        public static readonly DirectionalTransitionProfile ExpandedMediaTransitionProfile = new(
            DurationMs: 340,
            OutgoingOffset: 64.0f,
            IncomingOffset: 28.0f,
            OutgoingScale: 0.982f,
            IncomingScale: 0.994f,
            IncomingDelayProgress: 0.18f,
            OutgoingFadeEndProgress: 0.58f,
            OutgoingTravelProgress: 0.72f,
            ClipInsetRatio: 0.16f,
            ClipInsetMin: 22.0f,
            ClipInsetMax: 58.0f);
        public static readonly DirectionalTransitionProfile CompactContentTransitionProfile = new(
            DurationMs: 300,
            OutgoingOffset: 32.0f,
            IncomingOffset: 18.0f,
            OutgoingScale: 0.988f,
            IncomingScale: 0.997f,
            IncomingDelayProgress: 0.14f,
            OutgoingFadeEndProgress: 0.52f,
            OutgoingTravelProgress: 0.68f,
            ClipInsetRatio: 0.12f,
            ClipInsetMin: 12.0f,
            ClipInsetMax: 24.0f);
        public static readonly DirectionalTransitionProfile HeaderChipTransitionProfile = new(
            DurationMs: 260,
            OutgoingOffset: 24.0f,
            IncomingOffset: 12.0f,
            OutgoingScale: 0.99f,
            IncomingScale: 0.997f,
            IncomingDelayProgress: 0.10f,
            OutgoingFadeEndProgress: 0.48f,
            OutgoingTravelProgress: 0.64f,
            ClipInsetRatio: 0.10f,
            ClipInsetMin: 8.0f,
            ClipInsetMax: 18.0f);
        public const int HeaderChipSizeTransitionDurationMs = 240;
        public const float HeaderChipGrowSettleProgress = 0.68f;
        public const float HeaderChipGrowSettleRatio = 0.90f;
        public const float HeaderChipShrinkDelayProgress = 0.16f;
        public const float HeaderChipHoverScale = 1.01f;
        public const float HeaderChipPressedScale = 0.985f;
        public const float HeaderChipHoverTranslateY = -0.5f;
        public const int HeaderChipHoverDurationMs = 180;
        public const int HeaderChipPressDurationMs = 110;
        public const float HeaderLabelShiftGrowDelayProgress = 0.06f;
        public const float HeaderLabelShiftShrinkSettleProgress = 0.56f;
        public const int HeaderAvatarTransitionDurationMs = 220;
        public const float HeaderAvatarEnterTravel = 7.0f;
        public const float HeaderAvatarExitTravel = 9.0f;
        public const float HeaderAvatarEntryDelayProgress = 0.12f;
        public const float HeaderAvatarExitFadeEndProgress = 0.56f;
        public const float HeaderAvatarEnterScaleMultiplier = 0.88f;
        public const float HeaderAvatarExitScaleMultiplier = 0.84f;

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
        public const int TrackSwitchIntentWindowMs = 1600;
        public const int MediaMissingGraceMs = 3000;
        public const int MediaAutoSwitchDebounceMs = 1200;
        public const int MediaRefreshBurstDurationMs = 3000;
        public const int MediaRefreshBurstIntervalMs = 250;
        public const int SelectionLockDurationMs = 10000;
        public const int CompactSessionCountVisibleThreshold = 2;
        public const double SessionPickerOverlayWidth = 312.0;
        public const int SessionPickerOverlayMaxVisibleItems = 4;
        public const double SessionPickerOverlayRowHeight = 58.0;
        public const double SessionPickerOverlayItemSpacing = 6.0;
        public const double SessionPickerOverlayPanelPadding = 8.0;
        public const double SessionPickerOverlayWindowGap = 8.0;
        public const double SessionPickerOverlayScreenMargin = 8.0;

        // --- Opacity Thresholds ---
        public const double HitTestOpacityThreshold = 0.5;
    }
}
