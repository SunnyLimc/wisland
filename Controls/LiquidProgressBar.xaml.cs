using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using wisland.Models;

namespace wisland.Controls
{
    public readonly record struct LiquidProgressBarDiagnostics(
        double ControlActualWidth,
        double ControlActualHeight,
        double RootActualWidth,
        double RootActualHeight,
        double ProgressLayerActualWidth,
        double ProgressLayerActualHeight,
        double BaseActualHeight,
        double TailActualHeight,
        double LaserCoreActualHeight);

    public sealed partial class LiquidProgressBar : UserControl
    {
        private const double HorizontalInset = 6.0;
        private double _currentProgressWidth = 0;
        private double _previousProgressWidth = 0;
        private double _smoothedVelocity = 0;
        private double _targetProgressWidth = 0;
        private bool _isEffectVisible;
        private bool _isShimmerActive;
        private bool _shouldSnapToTargetWidth;

        // Last-rendered values for dirty checking
        private double _lastRenderedWidth = -1;
        private double _lastRenderedVelocity = -1;
        private double _lastRenderedHeight = -1;
        private double _lastRenderedCoreInset = -1;

        public LiquidProgressBar()
        {
            this.InitializeComponent();
            _isEffectVisible = Visibility == Visibility.Visible;
            if (_isEffectVisible)
            {
                SetShimmerActive(true);
            }
        }

        public bool IsEffectVisible => _isEffectVisible;
        public bool IsShimmerActive => _isShimmerActive;

        public bool IsAnimationActive
            => _isEffectVisible
                && (_shouldSnapToTargetWidth
                    || Math.Abs(_currentProgressWidth - _targetProgressWidth) > 0.05
                    || _smoothedVelocity > 0.01);

        public bool IsSettledAtZero
            => !_shouldSnapToTargetWidth
                && Math.Abs(_targetProgressWidth - HorizontalInset) <= 0.05
                && Math.Abs(_currentProgressWidth - HorizontalInset) <= 0.25
                && _smoothedVelocity <= 0.01;

        public LiquidProgressBarDiagnostics GetDiagnosticsSnapshot()
            => new(
                ControlActualWidth: ActualWidth,
                ControlActualHeight: ActualHeight,
                RootActualWidth: RootGrid.ActualWidth,
                RootActualHeight: RootGrid.ActualHeight,
                ProgressLayerActualWidth: LiquidGlassProgressLayer.ActualWidth,
                ProgressLayerActualHeight: LiquidGlassProgressLayer.ActualHeight,
                BaseActualHeight: ProgressBase.ActualHeight,
                TailActualHeight: ProgressTail.ActualHeight,
                LaserCoreActualHeight: ProgressLaserCore.ActualHeight);

        public void SetEffectVisible(bool isVisible)
        {
            if (_isEffectVisible == isVisible)
            {
                return;
            }

            _isEffectVisible = isVisible;
            Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

            if (isVisible)
            {
                _shouldSnapToTargetWidth = true;
            }
            else
            {
                SetShimmerActive(false);
                _smoothedVelocity = 0;
                _lastRenderedVelocity = -1;
            }
        }

        public void SetShimmerActive(bool isActive)
        {
            bool targetState = _isEffectVisible && isActive;
            if (_isShimmerActive == targetState)
            {
                return;
            }

            _isShimmerActive = targetState;

            if (_isShimmerActive)
            {
                ShimmerTransform.X = -100;
                ShimmerStoryboard.Begin();
            }
            else
            {
                ShimmerStoryboard.Stop();
                ShimmerTransform.X = -100;
                ProgressShimmer.Opacity = 0;
            }
        }

        public void ApplyPalette(ProgressBarPalette palette)
        {
            ProgressBaseBrush.Color = palette.BaseColor;
            ProgressShimmerStartStop.Color = palette.ShimmerStartColor;
            ProgressShimmerHighlightStop.Color = palette.ShimmerHighlightColor;
            ProgressShimmerEndStop.Color = palette.ShimmerEndColor;
            ProgressTailStartStop.Color = palette.TailStartColor;
            ProgressTailMidStop.Color = palette.TailMidColor;
            ProgressTailNearEndStop.Color = palette.TailNearEndColor;
            ProgressTailEndStop.Color = palette.TailEndColor;
            ProgressLaserCoreBrush.Color = palette.LeadingEdgeColor;
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
            if (!_isEffectVisible)
            {
                return;
            }

            if (double.IsNaN(targetProgress) || double.IsInfinity(targetProgress)) targetProgress = 0;

            // 1. Position Calculation with Horizontal Inset
            double availableWidth = Math.Max(0, containerWidth - (HorizontalInset * 2));
            double targetProgressWidth = (availableWidth * targetProgress) + HorizontalInset;
            _targetProgressWidth = targetProgressWidth;

            if (_shouldSnapToTargetWidth)
            {
                _currentProgressWidth = targetProgressWidth;
                _previousProgressWidth = targetProgressWidth;
                _smoothedVelocity = 0;
                _lastRenderedVelocity = -1;
                _shouldSnapToTargetWidth = false;
            }
            else
            {
                _previousProgressWidth = _currentProgressWidth;
                _currentProgressWidth += (targetProgressWidth - _currentProgressWidth) * t;
            }

            double finalProgressWidth = Math.Max(HorizontalInset, _currentProgressWidth);

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
                _lastRenderedVelocity = _smoothedVelocity;
            }

            double shimmerOpacity = _isShimmerActive ? 0.15 + (_smoothedVelocity * 0.2) : 0;
            if (Math.Abs(ProgressShimmer.Opacity - shimmerOpacity) > 0.005)
            {
                ProgressShimmer.Opacity = shimmerOpacity;
            }

            // 4. Height Scaling (Leading Edge Core)
            if (Math.Abs(currentHeight - _lastRenderedHeight) > 0.1)
            {
                double coreInset = 1.0 + (currentHeight - 30.0) / 90.0;
                coreInset = Math.Clamp(coreInset, 1.0, 2.0);

                if (Math.Abs(coreInset - _lastRenderedCoreInset) > 0.01)
                {
                    ProgressLaserCore.Margin = new Thickness(0, coreInset, 0, coreInset);
                    _lastRenderedCoreInset = coreInset;
                }

                ProgressLaserCore.Height = double.NaN;
                ProgressLaserCore.CornerRadius = new CornerRadius(1);
                _lastRenderedHeight = currentHeight;
            }
        }
    }
}
