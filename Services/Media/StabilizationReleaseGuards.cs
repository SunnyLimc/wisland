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
        /// Returns true when the current position dropped below the baseline
        /// in a shape consistent with a real track restart, rather than a
        /// metadata-only flicker where the timeline keeps advancing on the
        /// prior playback (Chrome can briefly surface another tab's
        /// title/artist while the original tab is still playing — in that
        /// case <c>currentPos &gt;= baselinePos</c>).
        ///
        /// When the baseline had no timeline we cannot compare positions, so
        /// we conservatively accept the shape-only signal by returning true.
        ///
        /// Acceptance criteria (any one suffices):
        /// <list type="number">
        /// <item>Clear drop beyond the jitter margin — the classic "baseline
        /// was mid-track (e.g. 30s), new track started near 0" path.</item>
        /// <item>Any strict drop where the new position also lands within the
        /// fresh-track threshold (<see cref="IslandConfig.SkipTransitionFreshTrackPositionSeconds"/>).
        /// This rescues rapid-skip pileups where the baseline itself was
        /// captured from a track that had only just started (e.g. baseline
        /// pos=0.8s because a previous skip released and a new song began
        /// playing right before the user skipped again). A legitimate next
        /// track arriving at pos≈0.5s would otherwise fail the margin check
        /// (0.5 &lt; 0.8 − 0.5 = 0.3) and the gate would stay closed until
        /// <c>SkipTransitionTimeoutMs</c> (~10s) fired. The extra
        /// <c>currentPos ≤ 3s</c> guard keeps this narrow: positions past 3s
        /// cannot clear the caller's <see cref="LooksLikeFreshTrackShape"/>
        /// check anyway, so real tab-leak flickers (currentPos ≥ baselinePos)
        /// are still rejected here.</item>
        /// </list>
        /// </summary>
        public static bool PositionLooksRestarted(
            double currentPositionSeconds,
            double baselinePositionSeconds,
            bool baselineHasTimeline)
        {
            if (!baselineHasTimeline) return true;
            double margin = IslandConfig.SkipTransitionPositionRestartMarginSeconds;
            if (currentPositionSeconds < baselinePositionSeconds - margin)
            {
                return true;
            }
            if (currentPositionSeconds < baselinePositionSeconds
                && currentPositionSeconds <= IslandConfig.SkipTransitionFreshTrackPositionSeconds)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when the raw playback position is small enough to be
        /// consistent with a same-track restart (e.g. YouTube Music's "previous"
        /// command replaying the current track from the beginning rather than
        /// switching to a different track). Callers gate this against a
        /// metadata-matches-baseline check before consulting; the threshold is
        /// kept tight (≤ <see cref="IslandConfig.SameTrackRestartMaxPositionSeconds"/>,
        /// 1s) so an in-track backward jitter cannot fire this branch.
        ///
        /// Note this guard intentionally narrower than
        /// <see cref="LooksLikeFreshTrackShape"/> (≤3s): when metadata differs
        /// from the baseline, a 0–3s position is sufficient evidence of a real
        /// new track; when metadata matches, the position must be near-zero
        /// because the only thing distinguishing a real restart from
        /// "host transiently re-reported an older cached position while still
        /// playing the same track at the same place" is that a restart resets
        /// the timeline to the very start.
        /// </summary>
        public static bool SameTrackRestartLooksGenuine(double currentPositionSeconds)
        {
            return currentPositionSeconds <= IslandConfig.SameTrackRestartMaxPositionSeconds;
        }
    }
}
