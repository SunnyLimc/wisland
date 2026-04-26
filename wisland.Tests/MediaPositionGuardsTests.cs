using Windows.Media.Control;
using wisland.Models;
using wisland.Services;
using Xunit;

namespace wisland.Tests
{
    public class MediaPositionGuardsTests
    {
        private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Playing
            = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Paused
            = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Stopped
            = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped;

        [Fact]
        public void Playing_NeverAbsorbsDrift()
        {
            // While Playing, GSMTC's TimelineProperties is the authoritative
            // signal (Tick re-anchors from it via wall-clock catchup); we must
            // not absorb position deltas.
            Assert.False(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Playing,
                trackedHasTimeline: true,
                incomingHasTimeline: true,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 200,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 29.5));
        }

        [Fact]
        public void Paused_SmallDriftAbsorbed()
        {
            // Common case: user pauses at 30s (locally extrapolated); GSMTC
            // reports its cached 28.9s a few ms later. Suppress the write so
            // the displayed elapsed time stays at 30s.
            Assert.True(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Paused,
                trackedHasTimeline: true,
                incomingHasTimeline: true,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 200,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 28.9));
        }

        [Fact]
        public void Paused_StoppedAlsoAbsorbed()
        {
            // Stopped is treated identically to Paused for drift-absorption
            // purposes — the only meaningful state for "trust GSMTC over local"
            // is Playing.
            Assert.True(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Stopped,
                trackedHasTimeline: true,
                incomingHasTimeline: true,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 200,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 29.0));
        }

        [Fact]
        public void Paused_LargeDriftRejected()
        {
            // Large drift (>= tolerance) should fall through and apply via the
            // normal write path. The 260ms scale animation in the view smooths
            // the catch-up visually.
            double aboveTolerance = IslandConfig.PausedPositionDriftToleranceSeconds + 0.5;
            Assert.False(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Paused,
                trackedHasTimeline: true,
                incomingHasTimeline: true,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 200,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 30.0 + aboveTolerance));
        }

        [Fact]
        public void Paused_DriftRightAtToleranceBoundary()
        {
            // Boundary semantics: strictly < tolerance is absorbed; equal-or-above
            // is not. Verify both sides of the boundary so the behavior is locked.
            double tolerance = IslandConfig.PausedPositionDriftToleranceSeconds;
            Assert.True(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Paused,
                trackedHasTimeline: true,
                incomingHasTimeline: true,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 200,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 30.0 + tolerance - 0.01));
            Assert.False(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Paused,
                trackedHasTimeline: true,
                incomingHasTimeline: true,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 200,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 30.0 + tolerance));
        }

        [Fact]
        public void Paused_TimelineAvailabilityChangeRejected()
        {
            // A timeline availability flip is a structural change (e.g. host
            // tore down its timeline before a track switch); never absorb.
            Assert.False(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Paused,
                trackedHasTimeline: true,
                incomingHasTimeline: false,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 0,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 0));
        }

        [Fact]
        public void Paused_DurationChangeRejected()
        {
            // Duration change indicates a new track. Never absorb the position
            // delta even if it happens to be small.
            Assert.False(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Paused,
                trackedHasTimeline: true,
                incomingHasTimeline: true,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 240,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 29.0));
        }

        [Fact]
        public void Paused_NoTimelineRejected()
        {
            // Without a tracked timeline there's nothing meaningful to compare;
            // let the caller's other guards apply.
            Assert.False(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Paused,
                trackedHasTimeline: false,
                incomingHasTimeline: false,
                trackedDurationSeconds: 0,
                incomingDurationSeconds: 0,
                trackedPositionSeconds: 0,
                incomingPositionSeconds: 0));
        }

        [Fact]
        public void Paused_SubMillisecondDeltaNotAbsorbed()
        {
            // A delta below 1ms is not "drift to absorb" — it's already a
            // no-op at the caller's positionChanged guard. We mirror that
            // semantics here so the helper has a clean "I would absorb"
            // contract independent of the caller.
            Assert.False(MediaPositionGuards.ShouldAbsorbPausedDrift(
                Paused,
                trackedHasTimeline: true,
                incomingHasTimeline: true,
                trackedDurationSeconds: 200,
                incomingDurationSeconds: 200,
                trackedPositionSeconds: 30.0,
                incomingPositionSeconds: 30.0));
        }

        // --- Tick gate ------------------------------------------------------

        [Fact]
        public void Tick_PlayingActiveDurationPositive_Advances()
        {
            Assert.True(MediaPositionGuards.ShouldTickSession(
                Playing,
                MediaSessionPresence.Active,
                hasPendingReconnect: false,
                durationSeconds: 200,
                stabilizationReason: MediaSessionStabilizationReason.None));
        }

        [Fact]
        public void Tick_PausedRejected()
        {
            Assert.False(MediaPositionGuards.ShouldTickSession(
                Paused,
                MediaSessionPresence.Active,
                hasPendingReconnect: false,
                durationSeconds: 200,
                stabilizationReason: MediaSessionStabilizationReason.None));
        }

        [Fact]
        public void Tick_NotActiveRejected()
        {
            Assert.False(MediaPositionGuards.ShouldTickSession(
                Playing,
                MediaSessionPresence.WaitingForReconnect,
                hasPendingReconnect: false,
                durationSeconds: 200,
                stabilizationReason: MediaSessionStabilizationReason.None));
        }

        [Fact]
        public void Tick_PendingReconnectRejected()
        {
            // While reconnecting the position is meaningless; do not advance.
            Assert.False(MediaPositionGuards.ShouldTickSession(
                Playing,
                MediaSessionPresence.Active,
                hasPendingReconnect: true,
                durationSeconds: 200,
                stabilizationReason: MediaSessionStabilizationReason.None));
        }

        [Fact]
        public void Tick_ZeroOrNegativeDurationRejected()
        {
            Assert.False(MediaPositionGuards.ShouldTickSession(
                Playing,
                MediaSessionPresence.Active,
                hasPendingReconnect: false,
                durationSeconds: 0,
                stabilizationReason: MediaSessionStabilizationReason.None));
            Assert.False(MediaPositionGuards.ShouldTickSession(
                Playing,
                MediaSessionPresence.Active,
                hasPendingReconnect: false,
                durationSeconds: -1,
                stabilizationReason: MediaSessionStabilizationReason.None));
        }

        [Fact]
        public void Tick_SkipTransitionStabilizationFreezes()
        {
            // The optimistic pos=0 the view shows during a skip gate is
            // load-bearing UX; Tick must not advance tracked here, otherwise
            // a gate-by-timeout expiry emits baseline+elapsed and the bar
            // visibly jumps forward from 0.
            Assert.False(MediaPositionGuards.ShouldTickSession(
                Playing,
                MediaSessionPresence.Active,
                hasPendingReconnect: false,
                durationSeconds: 200,
                stabilizationReason: MediaSessionStabilizationReason.SkipTransition));
        }

        [Fact]
        public void Tick_NaturalEndingStabilizationStillAdvances()
        {
            // NaturalEnding fires near the actual end of a track while the
            // user may still be watching the song play out. Freezing local
            // advancement would stutter the visible elapsed time during the
            // last few seconds. The frozen snapshot keeps its baseline
            // position by design (no Progress=0 override), so allowing
            // Tick to advance does not corrupt anything.
            Assert.True(MediaPositionGuards.ShouldTickSession(
                Playing,
                MediaSessionPresence.Active,
                hasPendingReconnect: false,
                durationSeconds: 200,
                stabilizationReason: MediaSessionStabilizationReason.NaturalEnding));
        }

        // --- Frozen.Progress=0 override gate -------------------------------

        [Fact]
        public void FrozenOverride_SkipTransitionOverridden()
        {
            // The optimistic UX behavior is gated entirely on this method
            // returning true for SkipTransition.
            Assert.True(MediaPositionGuards.ShouldOverrideFrozenProgressForSkip(
                MediaSessionStabilizationReason.SkipTransition));
        }

        [Fact]
        public void FrozenOverride_NaturalEndingPreserved()
        {
            // NaturalEnding must keep the baseline position so the user can
            // watch the current track's last seconds play out without an
            // early flash of 0:00.
            Assert.False(MediaPositionGuards.ShouldOverrideFrozenProgressForSkip(
                MediaSessionStabilizationReason.NaturalEnding));
        }

        [Fact]
        public void FrozenOverride_NoneNotApplicable()
        {
            // Defensive: we should never be building a FrozenSnapshot when
            // stabilization is not armed, but the gate should be safe-by-default.
            Assert.False(MediaPositionGuards.ShouldOverrideFrozenProgressForSkip(
                MediaSessionStabilizationReason.None));
        }
    }
}
