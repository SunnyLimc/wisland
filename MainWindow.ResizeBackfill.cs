using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.UI;
using wisland.Helpers;
using wisland.Models;

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
        private ResizeSolidColorBackdrop? _resizeBackdrop;
        private uint _lastResizeBackfillArgb;
        private bool _isResizeBackfillVisible;
        private bool _isResizeBackfillRenderedHandlerAttached;
        private bool _isResizeBackdropActive;
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
        {
            Color backfillColor = WindowSurfaceColorMath.CreateOpaque(surfaceColor);
            SetResizeBackfillColor(backfillColor);
        }

        private void UpdateResizeBackdropColor(Color backdropColor)
        {
            Color resolvedBackdropColor = WindowSurfaceColorMath.CreateOpaque(backdropColor);
            UpdateActiveResizeBackdropColor(resolvedBackdropColor);
        }

        private void SetResizeBackfillColor(Color backfillColor)
        {
            uint argb = WindowSurfaceColorMath.Pack(backfillColor);
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

            RefreshWindowSurfaceState();
            UpdateResizeBackdropForCurrentState();
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
                if (ShouldKeepResizeBackfillActive())
                {
                    StartResizeBackfillFallbackTimer();
                    return;
                }

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

            if (ShouldKeepResizeBackfillActive())
            {
                _resizeBackfillRenderedFramesRemaining = ResizeBackfillRenderedFramesToHold;
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
            UpdateResizeBackdropForCurrentState();
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
            RestoreBackdropAfterResize();
        }

        private Color ResolveResizeBackfillColor()
        {
            if (_hasAppliedWindowSurfaceState)
            {
                return _currentWindowSurfaceState.ResizeBackfillColor;
            }

            Color surfaceColor = _currentVisualTokens?.SurfaceColor
                ?? Color.FromArgb(255, 0x3F, 0x3C, 0x42);
            return WindowSurfaceColorMath.CreateOpaque(surfaceColor);
        }

        private Color ResolveResizeBackdropColor()
        {
            if (_hasAppliedWindowSurfaceState)
            {
                // The window backdrop is not the same as the resize backfill:
                // it is the color DWM can expose before XAML catches up.
                return _currentWindowSurfaceState.ResizeBackdropColor;
            }

            return ResolveResizeBackfillColor();
        }

        private void ApplyResizeBackdropForResize()
        {
            // WinUI's SystemBackdrop is the only layer behind Window.Content.
            // Temporarily replacing Mica/Acrylic with a solid backdrop removes
            // the white/Mica strip that can appear while AppWindow resizes.
            if (!ShouldUseResizeBackdropForCurrentState())
            {
                RestoreBackdropAfterResize();
                return;
            }

            if (_isResizeBackdropActive)
            {
                return;
            }

            Color backdropColor = ResolveResizeBackdropColor();
            _resizeBackdrop ??= new ResizeSolidColorBackdrop(backdropColor);
            _resizeBackdrop.SetColor(backdropColor);

            SystemBackdrop = _resizeBackdrop;
            _isResizeBackdropActive = true;
        }

        private void UpdateResizeBackdropForCurrentState()
        {
            if (!ShouldUseResizeBackdropForCurrentState())
            {
                RestoreBackdropAfterResize();
                return;
            }

            if (!_isResizeBackdropActive)
            {
                ApplyResizeBackdropForResize();
                return;
            }
        }

        private void UpdateActiveResizeBackdropColor(Color backdropColor)
        {
            // Keep the window backdrop stable for the whole resize transaction.
            // Surface-state refreshes may happen while shrinking from immersive
            // to compat, but changing the active backdrop color mid-transaction
            // is visible through translucent surfaces as a flicker.
            if (_isResizeBackdropActive && _currentBackdropType == BackdropType.None)
            {
                RestoreBackdropAfterResize();
                return;
            }

            if (_isResizeBackdropActive && IsExpandedSurfaceRequested())
            {
                _resizeBackdrop?.SetColor(backdropColor);
            }
        }

        private void RestoreBackdropAfterResize()
        {
            if (!_isResizeBackdropActive)
            {
                return;
            }

            _isResizeBackdropActive = false;
            _appearanceService.ApplyBackdrop(this, _currentBackdropType, force: true);
        }

        private bool ShouldKeepResizeBackfillActive()
            => !_isClosed && _controller.HasPendingSurfaceAnimation();

        private bool ShouldUseResizeBackdropForCurrentState()
            => !_isClosed
                && _currentBackdropType != BackdropType.None
                // Drag updates may keep the render loop alive, but they are not
                // a resize gap. Installing a solid backdrop here visibly tints
                // compat surfaces during fast drags from immersive mode.
                && !_controller.IsDragging
                && ShouldUseResizeBackdropForSurfaceTransition();

        private bool ShouldUseResizeBackdropForSurfaceTransition()
        {
            if (IsFullyCompatViewSettled())
            {
                return false;
            }

            return _isResizeBackfillVisible
                || _controller.HasPendingSurfaceAnimation()
                // Covers the reversal case: mouse re-enters while an immersive
                // shrink is in flight, before the next resize backfill pass.
                || IsExpandingSurfaceTransitionVisible();
        }

        private bool IsExpandingSurfaceTransitionVisible()
        {
            if (!IsExpandedSurfaceRequested())
            {
                return false;
            }

            IslandState state = _controller.Current;
            double expandedHeight = _controller.UseImmersiveDimensions
                ? IslandConfig.ImmersiveExpandedHeight
                : IslandConfig.ExpandedHeight;

            return state.Height < expandedHeight - 0.25
                || state.ExpandedOpacity < 0.999;
        }

        private bool IsExpandedSurfaceRequested()
            => !_controller.IsDragging
                && (_controller.IsHovered
                    || _controller.IsTransientSurfaceOpen
                    || _controller.IsForcedExpanded);

        private bool IsFullyCompatViewSettled()
        {
            IslandState state = _controller.Current;
            return !_controller.IsHovered
                && !_controller.IsTransientSurfaceOpen
                && !_controller.IsForcedExpanded
                && !_controller.IsDragging
                && !_controller.HasPendingSurfaceAnimation()
                && state.Height <= IslandConfig.CompactHeight + 0.25
                && state.ExpandedOpacity <= 0.001;
        }

    }
}
