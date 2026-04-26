using wisland.Controls;
using Xunit;

namespace wisland.Tests
{
    /// <summary>
    /// Regression tests for <see cref="MarqueeRetargetMath"/>.
    ///
    /// Bug context: when a compact↔expanded transition animates the IslandBorder's
    /// width per render frame, the inner MarqueeText's viewport width changes
    /// continuously. The marquee's in-flight scroll storyboard was created with a
    /// stale <c>To = -oldOverflow</c> and would overshoot the correct end. The fix
    /// retargets the live storyboard from the current X to <c>-newOverflow</c> over
    /// the time still required to cover the remaining distance, rather than
    /// aborting (which would snap X back to 0 every tick of the width animation).
    /// </summary>
    public class MarqueeRetargetMathTests
    {
        [Fact]
        public void ForwardLeg_OverflowGrew_RetargetsToNewEndOverRemainingDistance()
        {
            // Old overflow 100, scrolled halfway to -50. Viewport shrank → new
            // overflow 140. Should target -140; remaining distance = 90; at speed 30
            // → 3.0 s.
            var (target, dur) = MarqueeRetargetMath.ComputeRetarget(
                currentX: -50.0, newOverflow: 140.0, scrollSpeed: 30.0, forwardLeg: true);

            Assert.Equal(-140.0, target, precision: 6);
            Assert.Equal(3.0, dur, precision: 6);
        }

        [Fact]
        public void ForwardLeg_OverflowShrankBelowCurrentX_StillTargetsNewEnd()
        {
            // Scrolled to -120. Viewport grew → new overflow 50. The new target
            // (-50) is on the opposite side of currentX. Remaining distance = 70;
            // at speed 30 → ~2.333 s. The Completed handler will then advance to
            // PauseAtEnd as if the leg finished normally.
            var (target, dur) = MarqueeRetargetMath.ComputeRetarget(
                currentX: -120.0, newOverflow: 50.0, scrollSpeed: 30.0, forwardLeg: true);

            Assert.Equal(-50.0, target, precision: 6);
            Assert.Equal(70.0 / 30.0, dur, precision: 6);
        }

        [Fact]
        public void ForwardLeg_TinyRemainingDistance_ClampsToMinDuration()
        {
            // Overflow grew by only 0.6 px while we're already at -100. Without the
            // floor, durationSeconds would round to ~0.02 s → visible snap.
            var (target, dur) = MarqueeRetargetMath.ComputeRetarget(
                currentX: -100.0, newOverflow: 100.6, scrollSpeed: 30.0, forwardLeg: true);

            Assert.Equal(-100.6, target, precision: 6);
            Assert.Equal(MarqueeRetargetMath.ForwardLegMinDurationSeconds, dur, precision: 6);
        }

        [Fact]
        public void BackwardLeg_UsesFasterMultiplierAndShorterMinDuration()
        {
            // Backward leg from -100 toward 0, overflow stable at 100. Remaining
            // 100; speed = 30 * 1.4 = 42 → ~2.381 s.
            var (target, dur) = MarqueeRetargetMath.ComputeRetarget(
                currentX: -100.0, newOverflow: 100.0, scrollSpeed: 30.0, forwardLeg: false);

            Assert.Equal(0.0, target, precision: 6);
            Assert.Equal(100.0 / (30.0 * MarqueeRetargetMath.BackwardSpeedMultiplier), dur, precision: 6);
        }

        [Fact]
        public void BackwardLeg_NearZero_ClampsToBackwardMinDuration()
        {
            // Already almost back at 0 when overflow shifts.
            var (_, dur) = MarqueeRetargetMath.ComputeRetarget(
                currentX: -1.0, newOverflow: 80.0, scrollSpeed: 30.0, forwardLeg: false);

            Assert.Equal(MarqueeRetargetMath.BackwardLegMinDurationSeconds, dur, precision: 6);
        }

        [Fact]
        public void ZeroSpeedClampsToOnePxPerSecondFloor()
        {
            // Defensive: ScrollSpeed=0 must not produce Infinity or NaN.
            var (target, dur) = MarqueeRetargetMath.ComputeRetarget(
                currentX: 0.0, newOverflow: 50.0, scrollSpeed: 0.0, forwardLeg: true);

            Assert.Equal(-50.0, target, precision: 6);
            Assert.Equal(50.0, dur, precision: 6); // 50 px / 1 px·s⁻¹
        }

        // -- Phase-control predicates ----------------------------------------

        [Theory]
        [InlineData(-120.0, 50.0, true)]   // currentX (-120) is past new end (-50)
        [InlineData(-50.0, 50.0, true)]    // exactly at new end
        [InlineData(-49.6, 50.0, true)]    // within epsilon
        [InlineData(-49.0, 50.0, false)]   // not yet at new end → must keep animating
        [InlineData(-10.0, 100.0, false)]  // halfway through, no overshoot
        public void ShouldSkipToPauseOnForwardRetarget_DetectsOvershoot(
            double currentX, double newOverflow, bool expected)
        {
            Assert.Equal(expected, MarqueeRetargetMath.ShouldSkipToPauseOnForwardRetarget(
                currentX, newOverflow));
        }

        [Theory]
        [InlineData(0.0, true)]      // exactly home
        [InlineData(-0.4, true)]     // within epsilon
        [InlineData(-0.5, true)]     // boundary
        [InlineData(-0.6, false)]    // not home yet
        [InlineData(-50.0, false)]   // mid-leg
        public void ShouldFinishBackLeg_DetectsHome(double currentX, bool expected)
        {
            Assert.Equal(expected, MarqueeRetargetMath.ShouldFinishBackLeg(currentX));
        }

        [Fact]
        public void ComputeBackLegDurationFromLiveX_UsesAbsoluteDistanceAndBackwardMultiplier()
        {
            // Suppose forward leg overshot to -120 with speed 30 → back leg covers
            // 120 px at speed 42 → ~2.857 s.
            double dur = MarqueeRetargetMath.ComputeBackLegDurationFromLiveX(
                startX: -120.0, scrollSpeed: 30.0);

            Assert.Equal(120.0 / (30.0 * MarqueeRetargetMath.BackwardSpeedMultiplier), dur, precision: 6);
        }

        [Fact]
        public void ComputeBackLegDurationFromLiveX_TinyDistance_ClampsToMinDuration()
        {
            double dur = MarqueeRetargetMath.ComputeBackLegDurationFromLiveX(
                startX: -1.0, scrollSpeed: 30.0);

            Assert.Equal(MarqueeRetargetMath.BackwardLegMinDurationSeconds, dur, precision: 6);
        }
    }
}
