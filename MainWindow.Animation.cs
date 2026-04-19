using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using Windows.Graphics;
using wisland.Helpers;
using wisland.Models;
namespace wisland
{
    public sealed partial class MainWindow
    {
        private bool _lastLoggedOffscreenState;
        private double _lastLoggedDpiScale;
        private int _lastLoggedWorkAreaX, _lastLoggedWorkAreaY, _lastLoggedWorkAreaW, _lastLoggedWorkAreaH;

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

            if (Logger.IsEnabled(Helpers.LogLevel.Debug))
            {
                if (Math.Abs(_dpiScale - _lastLoggedDpiScale) > 0.01)
                {
                    Logger.Debug($"DPI scale changed: {_lastLoggedDpiScale:F2} -> {_dpiScale:F2}");
                    _lastLoggedDpiScale = _dpiScale;
                }
                if (displayWorkArea.X != _lastLoggedWorkAreaX || displayWorkArea.Y != _lastLoggedWorkAreaY
                    || displayWorkArea.Width != _lastLoggedWorkAreaW || displayWorkArea.Height != _lastLoggedWorkAreaH)
                {
                    Logger.Debug($"Display work area changed: X={displayWorkArea.X}, Y={displayWorkArea.Y}, W={displayWorkArea.Width}, H={displayWorkArea.Height}");
                    _lastLoggedWorkAreaX = displayWorkArea.X;
                    _lastLoggedWorkAreaY = displayWorkArea.Y;
                    _lastLoggedWorkAreaW = displayWorkArea.Width;
                    _lastLoggedWorkAreaH = displayWorkArea.Height;
                }
            }

            _mediaService.Tick(dt);

            double t = 1.0 - Math.Exp(-IslandConfig.AnimationSpeed * dt);
            _controller.Tick(dt);

            // Cache the displayed session snapshot once per frame to avoid redundant lookups
            MediaSessionSnapshot? _frameDisplayedSession = GetDisplayedMediaSessionSnapshot();

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
                && !_controller.IsForcedExpanded
                && !_controller.IsDragging
                && !shouldDisplayDockedLineNow
                && state.Height <= IslandConfig.CompactHeight + 1;

            bool immersive = IsImmersiveActive;

            bool shouldShowProgressEffect = ShouldShowProgressEffect(_frameDisplayedSession);
            // Hide liquid progress bar when immersive view is the active expanded view
            if (immersive && state.ExpandedOpacity > 0.5)
                shouldShowProgressEffect = false;
            IslandProgressBar.SetEffectVisible(shouldShowProgressEffect);
            IslandProgressBar.SetShimmerActive(ShouldAnimateProgressShimmer(_frameDisplayedSession));

            double progressTopBleed = isDockPeekState ? physicalPixelLogical : 0;
            if (Math.Abs(IslandProgressBar.Margin.Top + progressTopBleed) > 0.0001
                || Math.Abs(IslandProgressBar.Margin.Bottom + physicalPixelLogical) > 0.0001)
            {
                IslandProgressBar.Margin = new Thickness(0, -progressTopBleed, 0, -physicalPixelLogical);
            }

            IslandProgressBar.Update(dt, t, GetDisplayedProgress(_frameDisplayedSession), visualWidth, visualHeight);
            RectInt32 bounds = ResolveIslandWindowBounds(state, displayWorkArea, _dpiScale);

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
                double clipTopLogical = Math.Clamp(visualHeight - visibleLogical, 0.0, visualHeight);
                double rasterScale = RootGrid.XamlRoot?.RasterizationScale > 0.0
                    ? RootGrid.XamlRoot.RasterizationScale
                    : _dpiScale;
                if (TryGetProjectedClientBounds(bounds, out RectInt32 clientBounds))
                {
                    clipTopLogical = CompactSurfaceLayout.ResolveDockPeekClipTop(
                        visualHeight,
                        visibleLogical,
                        clientBounds,
                        displayWorkArea,
                        rasterScale);
                }

                float clipTop = (float)clipTopLogical;
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

            if (Math.Abs(CompactContent.Opacity - state.CompactOpacity) > 0.005)
            {
                CompactContent.Opacity = state.CompactOpacity;
            }

            // Route expanded opacity to the active view; keep the inactive view hidden
            double activeExpandedOpacity = state.ExpandedOpacity;
            double classicOpacity = immersive ? 0 : activeExpandedOpacity;
            double immersiveOpacity = immersive ? activeExpandedOpacity : 0;

            if (Math.Abs(ExpandedContent.Opacity - classicOpacity) > 0.005)
            {
                ExpandedContent.Opacity = classicOpacity;
            }

            if (Math.Abs(ImmersiveContent.Opacity - immersiveOpacity) > 0.005)
            {
                ImmersiveContent.Opacity = immersiveOpacity;
            }

            bool isExpandedActive = state.ExpandedOpacity > IslandConfig.HitTestOpacityThreshold;
            bool classicHitTest = isExpandedActive && !immersive;
            bool immersiveHitTest = isExpandedActive && immersive;

            if (ExpandedContent.IsHitTestVisible != classicHitTest)
            {
                ExpandedContent.IsHitTestVisible = classicHitTest;
            }

            if (ImmersiveContent.IsHitTestVisible != immersiveHitTest)
            {
                ImmersiveContent.IsHitTestVisible = immersiveHitTest;
            }

            bool compactHitTestVisible = !isExpandedActive && state.IsHitTestVisible;
            if (CompactContent.IsHitTestVisible != compactHitTestVisible)
            {
                CompactContent.IsHitTestVisible = compactHitTestVisible;
            }

            int physX = bounds.X;
            int physY = bounds.Y;

            if (_controller.IsOffscreen() || shouldDisplayDockedLineNow)
            {
                RectInt32 virtualScreen = WindowInterop.GetVirtualScreenBounds();
                physY = virtualScreen.Y - physHeight - 64;
                ShowLineWindow(physX, monitorTopPhys, physWidth);
                UpdateShadowState();
                bounds = new RectInt32(physX, physY, physWidth, physHeight);

                if (!_lastLoggedOffscreenState)
                {
                    Logger.Debug($"Island moved offscreen, line mode active (offscreen={_controller.IsOffscreen()}, dockedLine={shouldDisplayDockedLineNow})");
                    _lastLoggedOffscreenState = true;
                }
            }
            else if (_lastLoggedOffscreenState)
            {
                Logger.Debug("Island returned on-screen from line mode");
                _lastLoggedOffscreenState = false;
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

            if (isSettled && _controller.IsDocked && !_controller.IsHovered && !_controller.IsForcedExpanded && !_controller.IsDragging)
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

        private bool TryGetProjectedClientBounds(RectInt32 targetWindowBounds, out RectInt32 clientBounds)
        {
            clientBounds = default;
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!WindowInterop.TryGetClientScreenBounds(hwnd, out RectInt32 actualClientBounds))
            {
                return false;
            }

            if (!_hasInitializedWindowBounds)
            {
                clientBounds = actualClientBounds;
                return true;
            }

            clientBounds = CompactSurfaceLayout.ProjectClientBounds(
                actualClientBounds,
                new RectInt32(_lastPhysX, _lastPhysY, _lastPhysW, _lastPhysH),
                targetWindowBounds);
            return true;
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
            Logger.Debug("Render loop started");
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
            Logger.Debug("Render loop stopped");
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
