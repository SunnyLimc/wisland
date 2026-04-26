using Windows.Media.Control;
using wisland.Models;

namespace wisland.Services
{
    /// <summary>
    /// Pure helpers for deciding whether incoming GSMTC position deltas should
    /// be applied to the tracked source. Kept stateless and dependency-free so
    /// the logic can be exercised in unit tests without standing up a full
    /// <see cref="MediaService"/> (which requires a WinRT host).
    /// </summary>
    internal static class MediaPositionGuards
    {
        /// <summary>
        /// Returns true when an incoming TimelineProperties update for a non-Playing
        /// session should be absorbed (suppressed) because the position delta from
        /// the locally tracked value is within
        /// <see cref="IslandConfig.PausedPositionDriftToleranceSeconds"/>.
        ///
        /// Background: while paused, GSMTC commonly delivers a
        /// <c>TimelineProperties.Position</c> that lags the locally-extrapolated
        /// <c>tracked.CurrentPositionSeconds</c> at the instant of pause by up to
        /// ~2 s (the host's cached sample is older than the moment Tick last ran).
        /// Without absorption, the displayed elapsed time visibly snaps backward
        /// each time a paused TimelinePropertiesChanged event fires — the user
        /// perceives the progress bar as "moving while paused".
        ///
        /// Track-identity-changing updates (timeline-availability flip or duration
        /// change) are excluded so a genuine new track or a host clearing its
        /// timeline still gets through. Larger drifts (≥ tolerance) are also
        /// excluded so the bar still catches up smoothly after long render-loop
        /// suspensions where GSMTC has substantially fresher data.
        /// </summary>
        public static bool ShouldAbsorbPausedDrift(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus,
            bool trackedHasTimeline,
            bool incomingHasTimeline,
            double trackedDurationSeconds,
            double incomingDurationSeconds,
            double trackedPositionSeconds,
            double incomingPositionSeconds)
        {
            if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return false;
            }

            if (trackedHasTimeline != incomingHasTimeline)
            {
                return false;
            }

            if (!trackedHasTimeline)
            {
                return false;
            }

            if (System.Math.Abs(trackedDurationSeconds - incomingDurationSeconds) > 0.001)
            {
                return false;
            }

            double delta = System.Math.Abs(trackedPositionSeconds - incomingPositionSeconds);
            if (delta < 0.001)
            {
                // No meaningful change — caller's existing positionChanged guard
                // would already have returned false; keep the same semantics here
                // for callers exercising this helper directly.
                return false;
            }

            return delta < IslandConfig.PausedPositionDriftToleranceSeconds;
        }

        /// <summary>
        /// Returns true when the per-render-frame Tick should advance
        /// <c>tracked.CurrentPositionSeconds</c> for a session.
        ///
        /// In addition to the historical Playing+Active+!HasPendingReconnect+Duration>0
        /// gates, this also rejects sessions whose StabilizationReason is
        /// <see cref="MediaSessionStabilizationReason.SkipTransition"/>. While a
        /// skip is being stabilized the view has been shown an optimistic
        /// pos=0 (see <see cref="ShouldOverrideFrozenProgressForSkip"/>);
        /// letting Tick keep advancing tracked.CurrentPositionSeconds means a
        /// gate that expires via timeout (no real GSMTC release-eligible
        /// sample landed) emits <c>baseline + elapsed</c> via
        /// <c>CreateRawSnapshot</c>'s wall-clock catch-up and the view jumps
        /// forward from 0 by the full gate duration. Holding the counter
        /// steady during a skip gate makes the worst-case expiry snap back to
        /// baseline (consistent with "the skip didn't actually take effect")
        /// rather than introducing a fictional forward jump.
        ///
        /// NaturalEnding is intentionally NOT excluded here: that path arms
        /// near the natural end of the current track while the user may be
        /// watching the song play out, so freezing local advancement would
        /// stutter the visible elapsed time during the last few seconds.
        /// </summary>
        public static bool ShouldTickSession(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus,
            MediaSessionPresence presence,
            bool hasPendingReconnect,
            double durationSeconds,
            MediaSessionStabilizationReason stabilizationReason)
        {
            if (presence != MediaSessionPresence.Active) return false;
            if (hasPendingReconnect) return false;
            if (playbackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return false;
            if (durationSeconds <= 0) return false;
            if (stabilizationReason == MediaSessionStabilizationReason.SkipTransition) return false;
            return true;
        }

        /// <summary>
        /// Returns true when the FrozenSnapshot built at stabilization-arm
        /// time should have its <c>Progress</c> overridden to 0 instead of
        /// preserving the baseline. Only applied to the SkipTransition reason
        /// because that path corresponds to an explicit user click whose
        /// next visible state is overwhelmingly likely to be a track at
        /// pos=0. NaturalEnding is intentionally excluded so the user can
        /// watch the current track play out without a misleading early flash
        /// of "0:00".
        /// </summary>
        public static bool ShouldOverrideFrozenProgressForSkip(MediaSessionStabilizationReason reason)
        {
            return reason == MediaSessionStabilizationReason.SkipTransition;
        }
    }
}
