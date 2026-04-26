using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI.Text;

namespace wisland.Controls
{
    /// <summary>
    /// Horizontal marquee text control.
    /// Cycle: idle pause → scroll to end → end pause → scroll back → repeat.
    /// Uses RenderTransform (TranslateTransform) for scrolling so the offset
    /// survives layout passes (composition Offset is clobbered by layout).
    /// Uses a custom <see cref="UnboundedWidthPanel"/> host so the inner
    /// TextBlock is measured with infinity and its ActualWidth reflects the
    /// true natural text width.
    /// </summary>
    public sealed partial class MarqueeText : UserControl
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeText),
                new PropertyMetadata(string.Empty, OnTextChanged));

        public static readonly DependencyProperty MarqueeFontSizeProperty =
            DependencyProperty.Register(nameof(MarqueeFontSize), typeof(double), typeof(MarqueeText),
                new PropertyMetadata(12.0));

        public static readonly DependencyProperty MarqueeFontWeightProperty =
            DependencyProperty.Register(nameof(MarqueeFontWeight), typeof(FontWeight), typeof(MarqueeText),
                new PropertyMetadata(FontWeights.Normal));

        public static readonly DependencyProperty MarqueeForegroundProperty =
            DependencyProperty.Register(nameof(MarqueeForeground), typeof(Brush), typeof(MarqueeText),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ScrollSpeedProperty =
            DependencyProperty.Register(nameof(ScrollSpeed), typeof(double), typeof(MarqueeText),
                new PropertyMetadata(30.0));

        public static readonly DependencyProperty IdlePauseProperty =
            DependencyProperty.Register(nameof(IdlePause), typeof(TimeSpan), typeof(MarqueeText),
                new PropertyMetadata(TimeSpan.FromSeconds(1.5)));

        public static readonly DependencyProperty EndPauseProperty =
            DependencyProperty.Register(nameof(EndPause), typeof(TimeSpan), typeof(MarqueeText),
                new PropertyMetadata(TimeSpan.FromSeconds(1.2)));

        public static readonly DependencyProperty HorizontalTextAlignmentProperty =
            DependencyProperty.Register(nameof(HorizontalTextAlignment), typeof(TextAlignment), typeof(MarqueeText),
                new PropertyMetadata(TextAlignment.Start));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public double MarqueeFontSize
        {
            get => (double)GetValue(MarqueeFontSizeProperty);
            set => SetValue(MarqueeFontSizeProperty, value);
        }

        public FontWeight MarqueeFontWeight
        {
            get => (FontWeight)GetValue(MarqueeFontWeightProperty);
            set => SetValue(MarqueeFontWeightProperty, value);
        }

        public Brush? MarqueeForeground
        {
            get => (Brush?)GetValue(MarqueeForegroundProperty);
            set => SetValue(MarqueeForegroundProperty, value);
        }

        public double ScrollSpeed
        {
            get => (double)GetValue(ScrollSpeedProperty);
            set => SetValue(ScrollSpeedProperty, value);
        }

        public TimeSpan IdlePause
        {
            get => (TimeSpan)GetValue(IdlePauseProperty);
            set => SetValue(IdlePauseProperty, value);
        }

        public TimeSpan EndPause
        {
            get => (TimeSpan)GetValue(EndPauseProperty);
            set => SetValue(EndPauseProperty, value);
        }

        public TextAlignment HorizontalTextAlignment
        {
            get => (TextAlignment)GetValue(HorizontalTextAlignmentProperty);
            set => SetValue(HorizontalTextAlignmentProperty, value);
        }

        private bool _isLoaded;
        private bool _isScrolling;
        private bool _reEvaluateQueued;
        private MarqueePhase _phase = MarqueePhase.Idle;
        private DispatcherTimer? _pauseTimer;
        private Storyboard? _activeScrollStoryboard;
        private double _overflowAmount;
        private int _cycleGeneration;

        private enum MarqueePhase { Idle, ScrollingToEnd, PauseAtEnd, ScrollingBack }

        public MarqueeText()
        {
            this.InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Viewport.SizeChanged += OnViewportSizeChanged;
            InnerText.SizeChanged += OnInnerTextSizeChanged;
        }

        internal TextBlock InnerTextBlock => InnerText;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            QueueReEvaluate();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            StopScrolling();
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarqueeText mt) mt.OnTextUpdated();
        }

        private void OnTextUpdated()
        {
            StopScrolling();
            QueueReEvaluate();
        }

        private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyViewportClip(e.NewSize);
            QueueReEvaluate();
        }

        private void OnInnerTextSizeChanged(object sender, SizeChangedEventArgs e)
        {
            QueueReEvaluate();
        }

        private void ApplyViewportClip(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0)
            {
                Viewport.Clip = null;
                return;
            }
            Viewport.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, size.Width, size.Height)
            };
        }

        private void QueueReEvaluate()
        {
            if (!_isLoaded) return;
            if (_reEvaluateQueued) return;
            _reEvaluateQueued = true;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _reEvaluateQueued = false;
                Evaluate();
            });
        }

        private void Evaluate()
        {
            if (!_isLoaded) return;
            double viewportWidth = Viewport.ActualWidth;
            double textWidth = InnerText.ActualWidth;
            if (viewportWidth <= 0 || textWidth <= 0) return;

            double overflow = textWidth - viewportWidth;
            if (overflow <= 0.5)
            {
                StopScrolling();
                TextTranslate.X = 0;
                _overflowAmount = 0;
                return;
            }

            if (!_isScrolling)
            {
                _overflowAmount = overflow;
                TextTranslate.X = 0;
                StartIdlePause();
                return;
            }

            // Already scrolling: if overflow changed materially (e.g. compact↔expanded
            // transition is animating the viewport width per frame, or font weight just
            // swapped), the in-flight storyboard targets a stale -oldOverflow and would
            // overshoot/undershoot the correct end. Smoothly retarget the running
            // animation from the current TextTranslate.X to -newOverflow over the time
            // remaining for that leg, instead of aborting (which would snap X to 0 each
            // tick of a continuous width animation).
            if (Math.Abs(overflow - _overflowAmount) > 0.5)
            {
                _overflowAmount = overflow;
                RetargetActiveLeg();
                return;
            }

            _overflowAmount = overflow;
        }

        private void RetargetActiveLeg()
        {
            if (_phase != MarqueePhase.ScrollingToEnd && _phase != MarqueePhase.ScrollingBack)
            {
                // In Idle / PauseAtEnd we are not animating, so the next leg will be
                // built from the fresh _overflowAmount automatically.
                return;
            }

            double currentX = TextTranslate.X;
            bool forwardLeg = _phase == MarqueePhase.ScrollingToEnd;
            int generation = _cycleGeneration;

            // Forward-leg overshoot: the live X is already at or past the new end
            // (-newOverflow). Animating from currentX to -newOverflow would visibly
            // rewind the text — what the user perceives as "scrolls back a bit, then
            // continues forward." Instead, freeze the leg in place, advance directly
            // to PauseAtEnd, and let ScrollBack animate gracefully from the live X to
            // 0 (it is now overflow-aware via TextTranslate.X, not -_overflowAmount).
            if (forwardLeg && MarqueeRetargetMath.ShouldSkipToPauseOnForwardRetarget(currentX, _overflowAmount))
            {
                StopActiveStoryboard();
                _phase = MarqueePhase.PauseAtEnd;
                StartPauseTimer(EndPause, (_, _) =>
                {
                    if (generation != _cycleGeneration) return;
                    StopPauseTimer();
                    ScrollBack(generation);
                });
                return;
            }

            // Back-leg already-home: very rarely the overflow change makes |currentX|
            // smaller than 0.5 px while the back leg is in flight. Just finish.
            if (!forwardLeg && MarqueeRetargetMath.ShouldFinishBackLeg(currentX))
            {
                StopActiveStoryboard();
                TextTranslate.X = 0;
                if (_overflowAmount > 0.5) StartIdlePause();
                else OnCycleAbort();
                return;
            }

            var (targetX, durationSeconds) = MarqueeRetargetMath.ComputeRetarget(
                currentX, _overflowAmount, ScrollSpeed, forwardLeg);

            MarqueePhase legPhase = _phase;

            StopActiveStoryboard();
            var storyboard = CreateTranslateStoryboard(currentX, targetX, durationSeconds,
                new CubicEase { EasingMode = EasingMode.EaseInOut });
            _activeScrollStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (generation != _cycleGeneration) return;
                if (_phase != legPhase) return;
                if (legPhase == MarqueePhase.ScrollingToEnd)
                {
                    _phase = MarqueePhase.PauseAtEnd;
                    StartPauseTimer(EndPause, (_, _) =>
                    {
                        if (generation != _cycleGeneration) return;
                        StopPauseTimer();
                        ScrollBack(generation);
                    });
                }
                else
                {
                    if (_overflowAmount > 0.5) StartIdlePause();
                    else OnCycleAbort();
                }
            };
            storyboard.Begin();
        }

        private void StartIdlePause()
        {
            _isScrolling = true;
            _phase = MarqueePhase.Idle;
            int generation = ++_cycleGeneration;
            StartPauseTimer(IdlePause, (_, _) =>
            {
                if (generation != _cycleGeneration) return;
                StopPauseTimer();
                ScrollToEnd(generation);
            });
        }

        private void ScrollToEnd(int generation)
        {
            if (generation != _cycleGeneration || !_isLoaded) return;
            if (_overflowAmount <= 0.5)
            {
                OnCycleAbort();
                return;
            }

            _phase = MarqueePhase.ScrollingToEnd;
            double durationSeconds = Math.Max(0.2, _overflowAmount / Math.Max(1.0, ScrollSpeed));
            double target = -_overflowAmount;

            var storyboard = CreateTranslateStoryboard(0, target, durationSeconds,
                new CubicEase { EasingMode = EasingMode.EaseInOut });
            _activeScrollStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (generation != _cycleGeneration) return;
                if (_phase != MarqueePhase.ScrollingToEnd) return;
                _phase = MarqueePhase.PauseAtEnd;
                StartPauseTimer(EndPause, (_, _) =>
                {
                    if (generation != _cycleGeneration) return;
                    StopPauseTimer();
                    ScrollBack(generation);
                });
            };
            storyboard.Begin();
        }

        private void ScrollBack(int generation)
        {
            if (generation != _cycleGeneration || !_isLoaded) return;
            _phase = MarqueePhase.ScrollingBack;

            // Start from the LIVE position rather than -_overflowAmount. When an
            // overflow change during the forward leg caused us to short-circuit
            // PauseAtEnd in place (see Evaluate retarget), the live X may be past
            // -newOverflow. Animating from -_overflowAmount would visibly snap
            // forward by the difference; using the live X yields a clean glide home.
            double start = TextTranslate.X;
            if (MarqueeRetargetMath.ShouldFinishBackLeg(start))
            {
                TextTranslate.X = 0;
                if (_overflowAmount > 0.5) StartIdlePause();
                else OnCycleAbort();
                return;
            }

            double durationSeconds = MarqueeRetargetMath.ComputeBackLegDurationFromLiveX(start, ScrollSpeed);

            var storyboard = CreateTranslateStoryboard(start, 0, durationSeconds,
                new CubicEase { EasingMode = EasingMode.EaseInOut });
            _activeScrollStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (generation != _cycleGeneration) return;
                if (_phase != MarqueePhase.ScrollingBack) return;
                if (_overflowAmount > 0.5) StartIdlePause();
                else OnCycleAbort();
            };
            storyboard.Begin();
        }

        private Storyboard CreateTranslateStoryboard(double from, double to, double durationSeconds, EasingFunctionBase easing)
        {
            TextTranslate.X = from;
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromSeconds(durationSeconds)),
                EasingFunction = easing,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, TextTranslate);
            Storyboard.SetTargetProperty(animation, "X");
            var sb = new Storyboard();
            sb.Children.Add(animation);
            return sb;
        }

        private void OnCycleAbort()
        {
            StopActiveStoryboard();
            TextTranslate.X = 0;
            _isScrolling = false;
            _phase = MarqueePhase.Idle;
        }

        private void StopActiveStoryboard()
        {
            if (_activeScrollStoryboard is null) return;
            try { _activeScrollStoryboard.Stop(); }
            catch { }
            _activeScrollStoryboard = null;
        }

        private void StartPauseTimer(TimeSpan duration, EventHandler<object> handler)
        {
            StopPauseTimer();
            _pauseTimer = new DispatcherTimer { Interval = duration };
            _pauseTimer.Tick += handler;
            _pauseTimer.Start();
        }

        private void StopPauseTimer()
        {
            if (_pauseTimer is null) return;
            _pauseTimer.Stop();
            _pauseTimer = null;
        }

        public void StopScrolling()
        {
            _cycleGeneration++;
            _isScrolling = false;
            _phase = MarqueePhase.Idle;
            _overflowAmount = 0;
            StopPauseTimer();
            StopActiveStoryboard();
            if (TextTranslate is not null) TextTranslate.X = 0;
        }

        public void Reset()
        {
            StopScrolling();
            QueueReEvaluate();
        }
    }
}
