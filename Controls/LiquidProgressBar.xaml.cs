using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

namespace island.Controls
{
    public sealed partial class LiquidProgressBar : UserControl
    {
        private double _currentProgressWidth = 0;
        private double _previousProgressWidth = 0;
        private double _smoothedVelocity = 0;

        // Last-rendered values for dirty checking
        private double _lastRenderedWidth = -1;
        private double _lastRenderedVelocity = -1;
        private double _lastRenderedHeight = -1;

        public LiquidProgressBar()
        {
            this.InitializeComponent();
            ShimmerStoryboard.Begin();
        }

        /// <summary>
        /// Updates the progress bar visuals based on delta time and target width.
        /// </summary>
        /// <param name="dt">Delta time in seconds.</param>
        /// <param name="t">Interpolation factor.</param>
        /// <param name="targetProgress">Progress value (0.0 to 1.0).</param>
        /// <param name="containerWidth">Current width of the island.</param>
        public void Update(double dt, double t, double targetProgress, double containerWidth, double currentHeight)
        {
            if (double.IsNaN(targetProgress) || double.IsInfinity(targetProgress)) targetProgress = 0;

            // 1. Position Calculation with Horizontal Inset
            double horizontalInset = 6.0;
            double availableWidth = Math.Max(0, containerWidth - (horizontalInset * 2));
            double targetProgressWidth = (availableWidth * targetProgress) + horizontalInset;

            _previousProgressWidth = _currentProgressWidth;
            _currentProgressWidth += (targetProgressWidth - _currentProgressWidth) * t;

            double finalProgressWidth = Math.Max(horizontalInset, _currentProgressWidth);

            // 2. Velocity-based visual mapping (The "Liquid" feel)
            double instantVelocity = Math.Abs(_currentProgressWidth - _previousProgressWidth) / dt;
            double normalizedVelocity = Math.Clamp(instantVelocity / 1500.0, 0, 1.0);

            // Exponential smoothing for the visual feedback
            double vt = 1.0 - Math.Exp(-12.0 * dt);
            _smoothedVelocity += (normalizedVelocity - _smoothedVelocity) * vt;

            // 3. XAML Sync with Layer 3: Visual Property Guarding
            // Avoid layout thrashing if the change is sub-pixel insignificant
            if (Math.Abs(finalProgressWidth - _lastRenderedWidth) > 0.01)
            {
                LiquidGlassProgressLayer.Width = finalProgressWidth;
                _lastRenderedWidth = finalProgressWidth;
            }
            
            if (Math.Abs(_smoothedVelocity - _lastRenderedVelocity) > 0.005)
            {
                ProgressTail.Width = 60 + (_smoothedVelocity * 140);
                ProgressTail.Opacity = 0.2 + (_smoothedVelocity * 0.4);
                
                ProgressLaserCore.Opacity = 0.7 + (_smoothedVelocity * 0.3);
                ProgressLaserCore.Width = 1.5 + (_smoothedVelocity * 2.0);

                ProgressShimmer.Opacity = 0.15 + (_smoothedVelocity * 0.2);
                _lastRenderedVelocity = _smoothedVelocity;
            }

            // 4. Height Scaling (Leading Edge Core)
            if (Math.Abs(currentHeight - _lastRenderedHeight) > 0.1)
            {
                double coreInset = 1.0 + (currentHeight - 30.0) / 90.0;
                coreInset = Math.Clamp(coreInset, 1.0, 2.0);
                ProgressLaserCore.Height = Math.Max(0, currentHeight - (coreInset * 2));
                ProgressLaserCore.CornerRadius = new CornerRadius(1);
                _lastRenderedHeight = currentHeight;
            }
        }
    }
}
