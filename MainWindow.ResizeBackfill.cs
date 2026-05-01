using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.UI;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private const int ResizeBackfillRenderedFramesToHold = 2;
        private const int ResizeBackfillFallbackMilliseconds = 300;

        private SpriteVisual? _resizeBackfillVisual;
        private CompositionColorBrush? _resizeBackfillBrush;
        private SolidColorBrush? _resizeBackfillXamlBrush;
        private DispatcherTimer? _resizeBackfillFallbackTimer;
        private uint _lastResizeBackfillArgb;
        private bool _isResizeBackfillVisible;
        private bool _isResizeBackfillRenderedHandlerAttached;
        private int _resizeBackfillRenderedFramesRemaining;

        private bool IsResizeBackfillHidePending
            => _isResizeBackfillVisible && _isResizeBackfillRenderedHandlerAttached;

        private void InstallResizeBackfill()
        {
            if (_resizeBackfillVisual != null || _isClosed)
            {
                return;
            }

            var compositor = ElementCompositionPreview.GetElementVisual(ResizeBackfillHost).Compositor;
            Color backfillColor = ResolveResizeBackfillColor();

            _resizeBackfillBrush = compositor.CreateColorBrush(backfillColor);
            _resizeBackfillVisual = compositor.CreateSpriteVisual();
            _resizeBackfillVisual.RelativeSizeAdjustment = Vector2.One;
            _resizeBackfillVisual.Brush = _resizeBackfillBrush;
            _resizeBackfillVisual.Opacity = 0.0f;

            ElementCompositionPreview.SetElementChildVisual(ResizeBackfillHost, _resizeBackfillVisual);
            SetResizeBackfillColor(backfillColor);
            ApplyResizeBackfillVisibility();
        }

        private void UpdateResizeBackfillSurfaceColor(Color surfaceColor)
            => SetResizeBackfillColor(CreateOpaqueBackfillColor(surfaceColor));

        private void SetResizeBackfillColor(Color backfillColor)
        {
            uint argb = PackColor(backfillColor);
            if (argb == _lastResizeBackfillArgb)
            {
                return;
            }

            _lastResizeBackfillArgb = argb;

            if (_resizeBackfillBrush != null)
            {
                _resizeBackfillBrush.Color = backfillColor;
            }

            if (_resizeBackfillXamlBrush == null)
            {
                _resizeBackfillXamlBrush = new SolidColorBrush(backfillColor);
            }
            else
            {
                _resizeBackfillXamlBrush.Color = backfillColor;
            }

            if (_isResizeBackfillVisible)
            {
                ResizeBackfillHost.Background = _resizeBackfillXamlBrush;
            }

            SetNativeResizeBackfillColor(backfillColor);
        }

        private void ShowResizeBackfillForResize()
        {
            if (_isClosed)
            {
                return;
            }

            InstallResizeBackfill();

            _isResizeBackfillVisible = true;
            _resizeBackfillRenderedFramesRemaining = ResizeBackfillRenderedFramesToHold;
            ApplyResizeBackfillVisibility();
            AttachResizeBackfillRenderedHandler();
            StartResizeBackfillFallbackTimer();
            UpdateRenderLoopState();
        }

        private void ApplyResizeBackfillVisibility()
        {
            if (_resizeBackfillVisual != null)
            {
                _resizeBackfillVisual.Opacity = _isResizeBackfillVisible ? 1.0f : 0.0f;
            }

            ResizeBackfillHost.Background = _isResizeBackfillVisible
                ? _resizeBackfillXamlBrush
                : null;
        }

        private void AttachResizeBackfillRenderedHandler()
        {
            if (_isResizeBackfillRenderedHandlerAttached)
            {
                return;
            }

            CompositionTarget.Rendered += ResizeBackfill_Rendered;
            _isResizeBackfillRenderedHandlerAttached = true;
        }

        private void DetachResizeBackfillRenderedHandler()
        {
            if (!_isResizeBackfillRenderedHandlerAttached)
            {
                return;
            }

            CompositionTarget.Rendered -= ResizeBackfill_Rendered;
            _isResizeBackfillRenderedHandlerAttached = false;
        }

        private void StartResizeBackfillFallbackTimer()
        {
            _resizeBackfillFallbackTimer ??= CreateResizeBackfillFallbackTimer();
            _resizeBackfillFallbackTimer.Stop();
            _resizeBackfillFallbackTimer.Start();
        }

        private DispatcherTimer CreateResizeBackfillFallbackTimer()
        {
            var timer = new DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(ResizeBackfillFallbackMilliseconds)
            };
            timer.Tick += ResizeBackfillFallbackTimer_Tick;
            return timer;
        }

        private void ResizeBackfillFallbackTimer_Tick(object? sender, object e)
        {
            _resizeBackfillFallbackTimer?.Stop();

            if (_isResizeBackfillVisible)
            {
                HideResizeBackfillAfterRenderedFrame();
            }
        }

        private void DisposeResizeBackfillFallbackTimer()
        {
            if (_resizeBackfillFallbackTimer == null)
            {
                return;
            }

            _resizeBackfillFallbackTimer.Stop();
            _resizeBackfillFallbackTimer.Tick -= ResizeBackfillFallbackTimer_Tick;
            _resizeBackfillFallbackTimer = null;
        }

        private void ResizeBackfill_Rendered(object? sender, object e)
        {
            if (!_isResizeBackfillVisible)
            {
                DetachResizeBackfillRenderedHandler();
                return;
            }

            _resizeBackfillRenderedFramesRemaining--;
            if (_resizeBackfillRenderedFramesRemaining > 0)
            {
                return;
            }

            HideResizeBackfillAfterRenderedFrame();
        }

        private void HideResizeBackfillAfterRenderedFrame()
        {
            _resizeBackfillFallbackTimer?.Stop();
            _isResizeBackfillVisible = false;
            _resizeBackfillRenderedFramesRemaining = 0;
            ApplyResizeBackfillVisibility();
            DetachResizeBackfillRenderedHandler();
            UpdateRenderLoopState();
        }

        private void DisposeResizeBackfill()
        {
            DetachResizeBackfillRenderedHandler();
            DisposeResizeBackfillFallbackTimer();

            ResizeBackfillHost.Background = null;

            if (_resizeBackfillVisual != null)
            {
                ElementCompositionPreview.SetElementChildVisual(ResizeBackfillHost, null);
                _resizeBackfillVisual = null;
            }

            _resizeBackfillBrush?.Dispose();
            _resizeBackfillBrush = null;
            _resizeBackfillXamlBrush = null;
            _lastResizeBackfillArgb = 0;
            _isResizeBackfillVisible = false;
            _resizeBackfillRenderedFramesRemaining = 0;
        }

        private Color ResolveResizeBackfillColor()
        {
            Color surfaceColor = _currentVisualTokens?.SurfaceColor
                ?? Color.FromArgb(255, 0x3F, 0x3C, 0x42);
            return CreateOpaqueBackfillColor(surfaceColor);
        }

        private static Color CreateOpaqueBackfillColor(Color color)
            => Color.FromArgb(255, color.R, color.G, color.B);

        private static uint PackColor(Color color)
            => ((uint)color.A << 24)
                | ((uint)color.R << 16)
                | ((uint)color.G << 8)
                | color.B;
    }
}
