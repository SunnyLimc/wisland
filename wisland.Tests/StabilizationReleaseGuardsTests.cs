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
        public void PositionRestart_DropWithinMarginRejected()
        {
            // Margin is 0.5s: a 0.3s drop is noise (seek jitter), not a restart.
            Assert.False(StabilizationReleaseGuards.PositionLooksRestarted(
                currentPositionSeconds: 2.5,
                baselinePositionSeconds: 2.8,
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
    }
}
