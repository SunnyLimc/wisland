using System;

namespace wisland.Controls
{
    /// <summary>
    /// Pure helper for <see cref="MarqueeText"/>'s retarget logic. Extracted so the
    /// load-bearing math (currentX → new -overflow over remaining time) can be
    /// regression-tested without instantiating XAML / Storyboards.
    /// </summary>
    internal static class MarqueeRetargetMath
    {
        public const double ForwardLegMinDurationSeconds = 0.2;
        public const double BackwardLegMinDurationSeconds = 0.15;
        public const double BackwardSpeedMultiplier = 1.4;

        /// <summary>
        /// Tolerance in pixels for "close enough" position comparisons. Below this
        /// we treat positions as identical to avoid sub-pixel jitter / micro
        /// storyboards on every layout pass.
        /// </summary>
        public const double PositionEpsilonPx = 0.5;

        /// <summary>
        /// On a forward-leg retarget, returns true when the live X is already at or
        /// past the new <c>-newOverflow</c>. Caller should skip the visible rewind
        /// and advance directly to PauseAtEnd; the back leg will then animate home
        /// from the live X.
        /// </summary>
        public static bool ShouldSkipToPauseOnForwardRetarget(double currentX, double newOverflow)
            => currentX <= -newOverflow + PositionEpsilonPx;

        /// <summary>
        /// On a back-leg retarget, returns true when the live X is already at home
        /// (within epsilon of 0). Caller should finish the cycle without starting a
        /// negligible micro storyboard.
        /// </summary>
        public static bool ShouldFinishBackLeg(double currentX)
            => currentX >= -PositionEpsilonPx;

        /// <summary>
        /// Compute the back-leg duration when the leg starts from the live X
        /// (typically used after a forward-leg overshoot skip-to-pause). Mirrors
        /// the speed multiplier and min-duration policy of <see cref="ComputeRetarget"/>.
        /// </summary>
        public static double ComputeBackLegDurationFromLiveX(double startX, double scrollSpeed)
        {
            double speed = Math.Max(1.0, scrollSpeed * BackwardSpeedMultiplier);
            return Math.Max(BackwardLegMinDurationSeconds, Math.Abs(startX) / speed);
        }

        /// <summary>
        /// Compute the new storyboard target X and duration for an in-flight scroll
        /// leg whose overflow has just changed (e.g. compact↔expanded transition is
        /// animating viewport width per frame).
        /// </summary>
        /// <param name="currentX">Live <c>TextTranslate.X</c> value at retarget time.</param>
        /// <param name="newOverflow">Fresh <c>textWidth - viewportWidth</c> (must be &gt; 0).</param>
        /// <param name="scrollSpeed">Configured <c>ScrollSpeed</c> (px / s).</param>
        /// <param name="forwardLeg">True if the active leg is ScrollingToEnd; false for ScrollingBack.</param>
        /// <returns>(targetX, durationSeconds) for the replacement storyboard.</returns>
        public static (double TargetX, double DurationSeconds) ComputeRetarget(
            double currentX,
            double newOverflow,
            double scrollSpeed,
            bool forwardLeg)
        {
            double targetX = forwardLeg ? -newOverflow : 0.0;
            double speed = forwardLeg
                ? Math.Max(1.0, scrollSpeed)
                : Math.Max(1.0, scrollSpeed * BackwardSpeedMultiplier);
            double minDuration = forwardLeg
                ? ForwardLegMinDurationSeconds
                : BackwardLegMinDurationSeconds;
            double remaining = Math.Abs(targetX - currentX);
            double durationSeconds = Math.Max(minDuration, remaining / speed);
            return (targetX, durationSeconds);
        }
    }
}
