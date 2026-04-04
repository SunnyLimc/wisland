using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics;
using wisland.Helpers;
using wisland.Models;
using wisland.Controls;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private void OnCompositionTargetRendering(object? sender, object e)
        {
            if (e is RenderingEventArgs args)
            {
                UpdateAnimation(args.RenderingTime);
            }
        }

        /// <summary>
        /// Per-frame animation using exponential decay interpolation.
        /// Syncs XAML elements and OS window position/size every frame.
        /// </summary>
        private void UpdateAnimation(TimeSpan renderingTime)
        {
            if (_lastRenderTime == TimeSpan.Zero)
            {
                _lastRenderTime = renderingTime;
                return;
            }

            double dt = (renderingTime - _lastRenderTime).TotalSeconds;
            _lastRenderTime = renderingTime;
            if (dt <= 0 || dt > IslandConfig.MaxDeltaTime)
            {
                dt = IslandConfig.FallbackDeltaTime;
            }

            RectInt32 displayWorkArea = GetCurrentDisplayWorkArea();
            int monitorTopPhys = displayWorkArea.Y;
            _dpiScale = GetDisplayDpiScale(displayWorkArea);
            _mediaService.Tick(dt);

            double t = 1.0 - Math.Exp(-IslandConfig.AnimationSpeed * dt);
            _controller.Tick(dt);

            var state = _controller.Current;
            ClampControllerPositionToDisplay(displayWorkArea, state.Width, state.Height, _dpiScale);
            _controller.UpdateTargetState();
            bool shouldDisplayDockedLineNow = ShouldDisplayDockedLineNow(displayWorkArea);

            int physWidth = GetPhysicalPixels(state.Width, _dpiScale);
            int physHeight = GetPhysicalPixels(state.Height, _dpiScale);
            double renderWidth = GetLogicalPixels(physWidth, _dpiScale);
            double renderHeight = GetLogicalPixels(physHeight, _dpiScale);
            double physicalPixelLogical = GetLogicalPixels(1, _dpiScale);
            bool isCompactSurfaceState = CompactSurfaceLayout.IsCompactState(state.Height);
            double visualWidth = CompactSurfaceLayout.ResolveExtent(renderWidth, RootGrid.ActualWidth, isCompactSurfaceState);
            double visualHeight = CompactSurfaceLayout.ResolveExtent(renderHeight, RootGrid.ActualHeight, isCompactSurfaceState);

            if (Math.Abs(visualWidth - _lastRenderedIslandWidth) > 0.01)
            {
                IslandBorder.Width = visualWidth;
                _lastRenderedIslandWidth = visualWidth;
            }

            if (Math.Abs(visualHeight - _lastRenderedIslandHeight) > 0.01)
            {
                IslandBorder.Height = visualHeight;
                _lastRenderedIslandHeight = visualHeight;
            }

            bool isDockPeekState = _controller.IsDocked
                && !_controller.IsHovered
                && !_controller.IsNotifying
                && !_controller.IsDragging
                && !shouldDisplayDockedLineNow
                && state.Height <= IslandConfig.CompactHeight + 1;

            bool shouldShowProgressEffect = ShouldShowProgressEffect();
            IslandProgressBar.SetEffectVisible(shouldShowProgressEffect);
            IslandProgressBar.SetShimmerActive(ShouldAnimateProgressShimmer());

            double progressTopBleed = isDockPeekState ? physicalPixelLogical : 0;
            if (Math.Abs(IslandProgressBar.Margin.Top + progressTopBleed) > 0.0001
                || Math.Abs(IslandProgressBar.Margin.Bottom + physicalPixelLogical) > 0.0001)
            {
                IslandProgressBar.Margin = new Thickness(0, -progressTopBleed, 0, -physicalPixelLogical);
            }

            IslandProgressBar.Update(dt, t, GetDisplayedProgress(), visualWidth, visualHeight);
            LogCompactProgressCoverageIfNeeded(
                state,
                isDockPeekState,
                shouldDisplayDockedLineNow,
                renderHeight,
                visualHeight,
                physHeight,
                physicalPixelLogical);

            if (_isMediaProgressResetPending && IslandProgressBar.IsSettledAtZero)
            {
                _isMediaProgressResetPending = false;

                if (_hideMediaProgressWhenResetCompletes && !_taskProgress.HasValue)
                {
                    _hideMediaProgressWhenResetCompletes = false;
                    IslandProgressBar.SetEffectVisible(false);
                }
                else
                {
                    _hideMediaProgressWhenResetCompletes = false;
                }
            }

            double radius = Math.Min(visualHeight / 2.0, 20.0);
            bool cornerShapeChanged = !_lastRenderedDockPeekState.HasValue
                || _lastRenderedDockPeekState.Value != isDockPeekState
                || Math.Abs(radius - _lastRenderedCornerRadius) > 0.01;
            if (cornerShapeChanged)
            {
                CornerRadius cornerRadius = isDockPeekState
                    ? new CornerRadius(0)
                    : new CornerRadius(radius);
                IslandBorder.CornerRadius = cornerRadius;
                HostSurface.CornerRadius = cornerRadius;

                Vector2 clipRadius = isDockPeekState ? Vector2.Zero : new Vector2((float)radius);
                _contentClip.TopLeftRadius = clipRadius;
                _contentClip.TopRightRadius = clipRadius;
                _contentClip.BottomLeftRadius = clipRadius;
                _contentClip.BottomRightRadius = clipRadius;

                _lastRenderedDockPeekState = isDockPeekState;
                _lastRenderedCornerRadius = radius;
            }

            float clipRight = (float)visualWidth;
            if (float.IsNaN(_lastClipRight) || Math.Abs(_lastClipRight - clipRight) > 0.01f)
            {
                _contentClip.Right = clipRight;
                _lastClipRight = clipRight;
            }

            float clipBottom = (float)(visualHeight + physicalPixelLogical);
            if (float.IsNaN(_lastClipBottom) || Math.Abs(_lastClipBottom - clipBottom) > 0.01f)
            {
                _contentClip.Bottom = clipBottom;
                _lastClipBottom = clipBottom;
            }

            if (isDockPeekState)
            {
                int visiblePhys = GetDockPeekPhysicalPixels(_dpiScale);
                double visibleLogical = GetLogicalPixels(visiblePhys, _dpiScale);
                float clipTop = (float)(visualHeight - visibleLogical);
                if (float.IsNaN(_lastClipTop) || Math.Abs(_lastClipTop - clipTop) > 0.01f)
                {
                    _contentClip.Top = clipTop;
                    _lastClipTop = clipTop;
                }
            }
            else
            {
                if (float.IsNaN(_lastClipTop) || Math.Abs(_lastClipTop) > 0.01f)
                {
                    _contentClip.Top = 0;
                    _lastClipTop = 0;
                }
            }

            if (CompactContent.Opacity != state.CompactOpacity)
            {
                CompactContent.Opacity = state.CompactOpacity;
            }

            if (ExpandedContent.Opacity != state.ExpandedOpacity)
            {
                ExpandedContent.Opacity = state.ExpandedOpacity;
            }

            bool isExpandedActive = state.ExpandedOpacity > IslandConfig.HitTestOpacityThreshold;
            if (ExpandedContent.IsHitTestVisible != isExpandedActive)
            {
                ExpandedContent.IsHitTestVisible = isExpandedActive;
            }

            bool compactHitTestVisible = !isExpandedActive && state.IsHitTestVisible;
            if (CompactContent.IsHitTestVisible != compactHitTestVisible)
            {
                CompactContent.IsHitTestVisible = compactHitTestVisible;
            }

            RectInt32 bounds = ResolveIslandWindowBounds(state, displayWorkArea, _dpiScale);
            int physX = bounds.X;
            int physY = bounds.Y;

            if (_controller.IsOffscreen() || shouldDisplayDockedLineNow)
            {
                RectInt32 virtualScreen = WindowInterop.GetVirtualScreenBounds();
                physY = virtualScreen.Y - physHeight - 64;
                ShowLineWindow(physX, monitorTopPhys, physWidth);
                UpdateShadowState();
                bounds = new RectInt32(physX, physY, physWidth, physHeight);
            }

            ApplyWindowBounds(bounds);

            UpdateAnchorPhysicalPoint(displayWorkArea, state, physWidth, physHeight);
            UpdateSessionPickerOverlayPlacement();
            TickSessionPickerOverlayAnimation(renderingTime);
            UpdateRenderLoopState();
        }

        private void ApplyInitialWindowState()
        {
            _controller.SnapToTargetState();

            RectInt32 displayWorkArea = GetCurrentDisplayWorkArea();
            _dpiScale = GetDisplayDpiScale(displayWorkArea);

            IslandState state = _controller.Current;
            ClampControllerPositionToDisplay(displayWorkArea, state.Width, state.Height, _dpiScale);

            RectInt32 bounds = ResolveIslandWindowBounds(state, displayWorkArea, _dpiScale);
            ApplyWindowBounds(bounds);
            UpdateAnchorPhysicalPoint(displayWorkArea, state, bounds.Width, bounds.Height);
        }

        private RectInt32 ResolveIslandWindowBounds(IslandState state, RectInt32 displayWorkArea, double dpiScale)
        {
            int physWidth = GetPhysicalPixels(state.Width, dpiScale);
            int physHeight = GetPhysicalPixels(state.Height, dpiScale);
            int physX = displayWorkArea.X + (int)Math.Round((state.CenterX * dpiScale) - (physWidth / 2.0));
            int physY = ResolveIslandWindowTop(state, displayWorkArea, dpiScale, physHeight);

            return new RectInt32(physX, physY, physWidth, physHeight);
        }

        private int ResolveIslandWindowTop(IslandState state, RectInt32 displayWorkArea, double dpiScale, int physHeight)
        {
            bool isSettled = Math.Abs(state.Height - IslandConfig.CompactHeight) < 1.0
                && Math.Abs(state.Y - _controller.TargetY) < 1.0;

            if (isSettled && _controller.IsDocked && !_controller.IsHovered && !_controller.IsNotifying && !_controller.IsDragging)
            {
                int visiblePhys = GetDockPeekPhysicalPixels(dpiScale);
                return displayWorkArea.Y + visiblePhys - physHeight;
            }

            return displayWorkArea.Y + (int)Math.Round(state.Y * dpiScale);
        }

        private void ApplyWindowBounds(RectInt32 bounds, bool force = false)
        {
            if (force
                || bounds.X != _lastPhysX
                || bounds.Y != _lastPhysY
                || bounds.Width != _lastPhysW
                || bounds.Height != _lastPhysH)
            {
                AppWindow.MoveAndResize(bounds);
                _lastPhysX = bounds.X;
                _lastPhysY = bounds.Y;
                _lastPhysW = bounds.Width;
                _lastPhysH = bounds.Height;
            }

            _hasInitializedWindowBounds = true;
        }

        private int GetDockPeekPhysicalPixels(double dpiScale)
        {
            double peek = _controller.IsForegroundMaximized ? IslandConfig.MaximizedDockPeekOffset : IslandConfig.DockPeekOffset;
            return Math.Max(1, (int)Math.Round(peek * dpiScale));
        }

        private static int GetPhysicalPixels(double logicalPixels, double dpiScale)
            => Math.Max(1, (int)Math.Ceiling(logicalPixels * dpiScale));

        private static double GetLogicalPixels(int physicalPixels, double dpiScale)
            => physicalPixels / dpiScale;

        private void LogCompactProgressCoverageIfNeeded(
            IslandState state,
            bool isDockPeekState,
            bool shouldDisplayDockedLineNow,
            double requestedHeight,
            double visualHeight,
            int physHeight,
            double physicalPixelLogical)
        {
            bool isCompactNormalState = state.Height <= IslandConfig.CompactHeight + 1.0
                && !isDockPeekState
                && !shouldDisplayDockedLineNow;
            if (!isCompactNormalState || !IslandProgressBar.IsEffectVisible)
            {
                return;
            }

            XamlRoot? xamlRoot = RootGrid.XamlRoot;
            if (RootGrid.ActualHeight <= 0.0
                || IslandBorder.ActualHeight <= 0.0
                || IslandProgressBar.ActualHeight <= 0.0
                || xamlRoot == null)
            {
                return;
            }

            Rect islandBounds;
            Rect progressBounds;
            try
            {
                islandBounds = IslandBorder
                    .TransformToVisual(RootGrid)
                    .TransformBounds(new Rect(0, 0, IslandBorder.ActualWidth, IslandBorder.ActualHeight));
                progressBounds = IslandProgressBar
                    .TransformToVisual(RootGrid)
                    .TransformBounds(new Rect(0, 0, IslandProgressBar.ActualWidth, IslandProgressBar.ActualHeight));
            }
            catch
            {
                return;
            }

            double rootUncoveredByIslandTop = Math.Max(0.0, islandBounds.Top);
            double rootUncoveredByIslandBottom = Math.Max(0.0, RootGrid.ActualHeight - islandBounds.Bottom);
            double islandUncoveredByProgressTop = Math.Max(0.0, progressBounds.Top - islandBounds.Top);
            double islandUncoveredByProgressBottom = Math.Max(0.0, islandBounds.Bottom - progressBounds.Bottom);
            double rootUncoveredByProgressTop = Math.Max(0.0, progressBounds.Top);
            double rootUncoveredByProgressBottom = Math.Max(0.0, RootGrid.ActualHeight - progressBounds.Bottom);
            if (rootUncoveredByIslandTop <= 0.2
                && rootUncoveredByIslandBottom <= 0.2
                && islandUncoveredByProgressTop <= 0.2
                && islandUncoveredByProgressBottom <= 0.2
                && rootUncoveredByProgressTop <= 0.2
                && rootUncoveredByProgressBottom <= 0.2)
            {
                return;
            }

            LiquidProgressBarDiagnostics diagnostics = IslandProgressBar.GetDiagnosticsSnapshot();
            string signature = FormattableString.Invariant(
                $"dpi={_dpiScale:F3};scale={xamlRoot.RasterizationScale:F3};requestedH={requestedHeight:F2};visualH={visualHeight:F2};physH={physHeight};pixel={physicalPixelLogical:F3};rootH={RootGrid.ActualHeight:F2};hostH={HostSurface.ActualHeight:F2};islandTop={islandBounds.Top:F2};islandBottom={islandBounds.Bottom:F2};islandH={islandBounds.Height:F2};progressTop={progressBounds.Top:F2};progressBottom={progressBounds.Bottom:F2};progressH={progressBounds.Height:F2};rootGapTop={rootUncoveredByIslandTop:F2};rootGapBottom={rootUncoveredByIslandBottom:F2};progressGapTop={islandUncoveredByProgressTop:F2};progressGapBottom={islandUncoveredByProgressBottom:F2};rootProgressGapTop={rootUncoveredByProgressTop:F2};rootProgressGapBottom={rootUncoveredByProgressBottom:F2};marginTop={IslandProgressBar.Margin.Top:F3};marginBottom={IslandProgressBar.Margin.Bottom:F3};clipTop={_contentClip.Top:F2};clipBottom={_contentClip.Bottom:F2};controlH={diagnostics.ControlActualHeight:F2};progressRootH={diagnostics.RootActualHeight:F2};layerH={diagnostics.ProgressLayerActualHeight:F2};baseH={diagnostics.BaseActualHeight:F2};tailH={diagnostics.TailActualHeight:F2};laserH={diagnostics.LaserCoreActualHeight:F2}");
            if (string.Equals(signature, _lastCompactProgressCoverageSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastCompactProgressCoverageSignature = signature;
            Logger.Info($"Compact progress coverage anomaly detected: {signature}");
        }

        private void StartRenderLoop()
        {
            if (_isClosed || _isRenderLoopActive)
            {
                return;
            }

            _lastRenderTime = TimeSpan.Zero;
            CompositionTarget.Rendering += OnCompositionTargetRendering;
            _isRenderLoopActive = true;
        }

        private void StopRenderLoop()
        {
            if (!_isRenderLoopActive)
            {
                return;
            }

            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _isRenderLoopActive = false;
            _lastRenderTime = TimeSpan.Zero;
        }

        private void UpdateRenderLoopState()
        {
            if (NeedsRenderLoop())
            {
                StartRenderLoop();
            }
            else
            {
                StopRenderLoop();
            }
        }

        private bool NeedsRenderLoop()
        {
            if (_isClosed)
            {
                return false;
            }

            if (_controller.HasPendingAnimation())
            {
                return true;
            }

            if (IsSessionPickerOverlayAnimating)
            {
                return true;
            }

            if (_isMediaProgressResetPending)
            {
                return true;
            }

            if (ShouldShowProgressEffect() != IslandProgressBar.IsEffectVisible)
            {
                return true;
            }

            if (ShouldAnimateProgressShimmer() != IslandProgressBar.IsShimmerActive)
            {
                return true;
            }

            if (_mediaService.ShouldAnimateProgress(_displayedSessionKey))
            {
                return true;
            }

            return IslandProgressBar.IsAnimationActive;
        }

    }
}
