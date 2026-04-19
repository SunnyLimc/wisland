using Windows.Media.Control;
using wisland.Models;

namespace wisland.Services
{
    /// <summary>
    /// Pure helpers used by <c>MediaService.Stabilization</c> to decide whether
    /// an incoming raw snapshot represents a genuine track restart. Extracted
    /// so the fresh-track gate can be unit-tested without standing up a full
    /// <c>MediaService</c>.
    /// </summary>
    internal static class StabilizationReleaseGuards
    {
        /// <summary>
        /// Returns true when the raw playback state looks like a fresh track
        /// (Playing, timeline present, near-zero position). Callers must still
        /// verify metadata change against the baseline; this guard only looks
        /// at playback/timeline shape.
        /// </summary>
        public static bool LooksLikeFreshTrackShape(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus,
            bool hasTimeline,
            double durationSeconds,
            double currentPositionSeconds)
        {
            return playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && hasTimeline
                && durationSeconds > 0
                && currentPositionSeconds <= IslandConfig.SkipTransitionFreshTrackPositionSeconds;
        }

        /// <summary>
        /// Returns true when the current position has dropped far enough below
        /// the baseline position to indicate a real track restart, rather than
        /// a metadata-only flicker where the timeline keeps advancing on the
        /// prior playback (Chrome can briefly surface another tab's title/artist
        /// while the original tab is still playing).
        ///
        /// When the baseline had no timeline we cannot compare positions, so we
        /// conservatively accept the shape-only signal by returning true.
        /// </summary>
        public static bool PositionLooksRestarted(
            double currentPositionSeconds,
            double baselinePositionSeconds,
            bool baselineHasTimeline)
        {
            if (!baselineHasTimeline) return true;
            return currentPositionSeconds
                < baselinePositionSeconds - IslandConfig.SkipTransitionPositionRestartMarginSeconds;
        }
    }
}
