using Windows.Media.Control;
using wisland.Services;
using Xunit;

namespace wisland.Tests
{
    public class StabilizationReleaseGuardsTests
    {
        private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Playing
            = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Paused
            = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

        [Fact]
        public void FreshShape_PlayingWithNearZeroPosition_IsFresh()
        {
            Assert.True(StabilizationReleaseGuards.LooksLikeFreshTrackShape(
                Playing, hasTimeline: true, durationSeconds: 200, currentPositionSeconds: 0.4));
        }

        [Fact]
        public void FreshShape_PausedRejected()
        {
            Assert.False(StabilizationReleaseGuards.LooksLikeFreshTrackShape(
                Paused, hasTimeline: true, durationSeconds: 200, currentPositionSeconds: 0.4));
        }

        [Fact]
        public void FreshShape_NoTimelineRejected()
        {
            Assert.False(StabilizationReleaseGuards.LooksLikeFreshTrackShape(
                Playing, hasTimeline: false, durationSeconds: 200, currentPositionSeconds: 0.4));
        }

        [Fact]
        public void FreshShape_PositionPastFreshThresholdRejected()
        {
            Assert.False(StabilizationReleaseGuards.LooksLikeFreshTrackShape(
                Playing, hasTimeline: true, durationSeconds: 200, currentPositionSeconds: 30.0));
        }

        // --- Position-restart guard ------------------------------------------

        [Fact]
        public void PositionRestart_BaselineWithoutTimelineAcceptsShape()
        {
            // Without a timeline baseline we cannot compare; fall back to shape-only.
            Assert.True(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 2.9,
                baselinePositionSeconds: 0,
                baselineHasTimeline: false));
        }

        [Fact]
        public void PositionRestart_TrueTrackChangeDropsToZero()
        {
            // Sacred was at 2.7s, the new track starts near 0 — a genuine restart.
            Assert.True(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 0.2,
                baselinePositionSeconds: 2.7,
                baselineHasTimeline: true));
        }

        [Fact]
        public void PositionRestart_MetadataFlickerAtSamePositionRejected()
        {
            // Reproduction of the regression: Chrome surfaced a different tab's
            // title at pos=2.9 while the baseline was pos=2.7. The timeline did
            // NOT reset, so this must NOT be treated as a fresh track.
            Assert.False(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 2.9,
                baselinePositionSeconds: 2.7,
                baselineHasTimeline: true));
        }

        [Fact]
        public void PositionRestart_SmallDropWithinFreshThresholdAccepted()
        {
            // Any strict backward position delta that lands within the
            // fresh-track threshold (3s) is treated as a genuine restart.
            // Rationale: in the MediaService pipeline positions only ever
            // advance for Playing tracks (wall-clock adjustment); an actual
            // decrease with differing metadata is real-world only produced by
            // a track transition, not by "seek jitter". Accepting small drops
            // here unblocks the rapid-skip pileup where baseline pos gets
            // captured just above the near-zero margin (e.g. 0.8s) and the
            // next track starts at a similarly low but strictly smaller pos
            // (e.g. 0.5s). The companion leak case (pos advanced past baseline)
            // is still rejected via the `currentPos < baselinePos` condition.
            Assert.True(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 2.5,
                baselinePositionSeconds: 2.8,
                baselineHasTimeline: true));
            Assert.True(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 0.5,
                baselinePositionSeconds: 0.8,
                baselineHasTimeline: true));
        }

        [Fact]
        public void PositionRestart_DropLandingPastFreshThresholdRejected()
        {
            // New position is past the fresh-track threshold (3s): the rescue
            // does not apply. The margin-delta path also cannot fire because
            // the caller's LooksLikeFreshTrackShape check would reject any
            // currentPos>3s before this guard is consulted — but we still
            // exercise the guard for robustness.
            Assert.False(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 4.5,
                baselinePositionSeconds: 5.0,
                baselineHasTimeline: true));
        }

        [Fact]
        public void PositionRestart_ClearDropBeyondMarginAccepted()
        {
            Assert.True(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 1.0,
                baselinePositionSeconds: 200.0,
                baselineHasTimeline: true));
        }

        [Fact]
        public void PositionRestart_NearZeroRescueAfterRapidReSkip()
        {
            // Regression: rapid consecutive skips can seal a baseline captured
            // from a track that just started (e.g. baseline pos=0.3 because the
            // previous skip released and B began playing right before the user
            // skipped again). The next track C arrives at pos≈0, but the
            // margin-subtracted delta check (0 < 0.3 − 0.5 = −0.2) would fail,
            // leaving the gate closed until the 10s SkipTransition timeout.
            Assert.True(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 0.0,
                baselinePositionSeconds: 0.3,
                baselineHasTimeline: true));
            Assert.True(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 0.1,
                baselinePositionSeconds: 0.4,
                baselineHasTimeline: true));
        }

        [Fact]
        public void PositionRestart_NearZeroRescueRequiresStrictDrop()
        {
            // Rescue must not fire when current pos is not below baseline
            // (e.g. metadata flicker at pos=0.3 while baseline also pos=0.3).
            Assert.False(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 0.3,
                baselinePositionSeconds: 0.3,
                baselineHasTimeline: true));
            Assert.False(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 0.5,
                baselinePositionSeconds: 0.3,
                baselineHasTimeline: true));
        }
    }
}
