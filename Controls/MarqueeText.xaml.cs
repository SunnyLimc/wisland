using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.UI.Text;

namespace wisland.Controls
{
    /// <summary>
    /// A text element that automatically scrolls horizontally when content overflows.
    /// Cycle: idle pause → scroll to end → end pause → scroll back → repeat.
    /// Uses a hidden ScrollViewer so the TextBlock renders at full natural width.
    /// </summary>
    public sealed partial class MarqueeText : UserControl
    {
        // ── Dependency properties ──────────────────────────────────────

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

        /// <summary>Scroll speed in device-independent pixels per second.</summary>
        public double ScrollSpeed
        {
            get => (double)GetValue(ScrollSpeedProperty);
            set => SetValue(ScrollSpeedProperty, value);
        }

        /// <summary>Pause before the first scroll starts (and between full cycles).</summary>
        public TimeSpan IdlePause
        {
            get => (TimeSpan)GetValue(IdlePauseProperty);
            set => SetValue(IdlePauseProperty, value);
        }

        /// <summary>Pause when scrolled to the end before scrolling back.</summary>
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

        // ── Internal state ─────────────────────────────────────────────

        private Compositor? _compositor;
        private Visual? _contentVisual;
        private bool _isScrolling;
        private MarqueePhase _phase = MarqueePhase.Idle;
        private DispatcherTimer? _pauseTimer;
        private double _overflowAmount;

        private enum MarqueePhase { Idle, ScrollingToEnd, PauseAtEnd, ScrollingBack }

        // ── Lifecycle ──────────────────────────────────────────────────

        public MarqueeText()
        {
            this.InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
        }

        /// <summary>Provides read access to the inner <see cref="TextBlock"/>.</summary>
        internal TextBlock InnerTextBlock => InnerText;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _compositor = ElementCompositionPreview.GetElementVisual(InnerText).Compositor;
            _contentVisual = ElementCompositionPreview.GetElementVisual(InnerText);

            InnerText.SizeChanged += OnTextBlockSizeChanged;
            Evaluate();
        }

        // ── Property change handlers ───────────────────────────────────

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarqueeText mt) mt.OnTextUpdated();
        }

        private void OnTextUpdated()
        {
            StopScrolling();
            Evaluate();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Evaluate();
        }

        private void OnTextBlockSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Re-evaluate when text layout changes.
            if (!_isScrolling)
                Evaluate();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopScrolling();
        }

        // ── Core evaluation ────────────────────────────────────────────

        private void Evaluate()
        {
            if (_contentVisual is null || _compositor is null) return;

            double viewportWidth = Scroller.ViewportWidth;
            double textWidth = Scroller.ExtentWidth;

            if (viewportWidth <= 0 || textWidth <= 0) return;

            _overflowAmount = textWidth - viewportWidth;

            if (_overflowAmount <= 0)
            {
                StopScrolling();
                _contentVisual.Offset = Vector3.Zero;
                return;
            }

            // Text overflows — start scroll cycle.
            if (!_isScrolling)
            {
                _contentVisual.Offset = Vector3.Zero;
                StartIdlePause();
            }
        }

        // ── Scroll cycle ──────────────────────────────────────────────

        private void StartIdlePause()
        {
            _isScrolling = true;
            _phase = MarqueePhase.Idle;
            StartPauseTimer(IdlePause, OnIdlePauseComplete);
        }

        private void OnIdlePauseComplete(object? sender, object e)
        {
            StopPauseTimer();
            ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            if (_contentVisual is null || _compositor is null) return;

            _phase = MarqueePhase.ScrollingToEnd;
            double durationSeconds = _overflowAmount / ScrollSpeed;

            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, 0f);
            animation.InsertKeyFrame(1f, (float)-_overflowAmount,
                _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0.6f, 1f)));
            animation.Duration = TimeSpan.FromSeconds(durationSeconds);

            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += OnScrollToEndComplete;
            _contentVisual.StartAnimation("Offset.X", animation);
            batch.End();
        }

        private void OnScrollToEndComplete(object? sender, CompositionBatchCompletedEventArgs args)
        {
            if (_phase != MarqueePhase.ScrollingToEnd) return;
            _phase = MarqueePhase.PauseAtEnd;
            StartPauseTimer(EndPause, OnEndPauseComplete);
        }

        private void OnEndPauseComplete(object? sender, object e)
        {
            StopPauseTimer();
            ScrollBack();
        }

        private void ScrollBack()
        {
            if (_contentVisual is null || _compositor is null) return;

            _phase = MarqueePhase.ScrollingBack;
            double durationSeconds = _overflowAmount / (ScrollSpeed * 1.4);

            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, (float)-_overflowAmount);
            animation.InsertKeyFrame(1f, 0f,
                _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.5f, 1f)));
            animation.Duration = TimeSpan.FromSeconds(durationSeconds);

            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += OnScrollBackComplete;
            _contentVisual.StartAnimation("Offset.X", animation);
            batch.End();
        }

        private void OnScrollBackComplete(object? sender, CompositionBatchCompletedEventArgs args)
        {
            if (_phase != MarqueePhase.ScrollingBack) return;

            if (_isScrolling && _overflowAmount > 0)
            {
                StartIdlePause();
            }
        }

        // ── Timer helpers ──────────────────────────────────────────────

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

        /// <summary>Stop the entire scroll cycle and reset offset.</summary>
        public void StopScrolling()
        {
            _isScrolling = false;
            _phase = MarqueePhase.Idle;
            StopPauseTimer();

            if (_contentVisual is not null)
            {
                _contentVisual.StopAnimation("Offset.X");
                _contentVisual.Offset = Vector3.Zero;
            }
        }

        /// <summary>Restart the scroll evaluation (call after text or size changes).</summary>
        public void Reset()
        {
            StopScrolling();
            Evaluate();
        }
    }
}
