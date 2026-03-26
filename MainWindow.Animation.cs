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

            if (Math.Abs(renderWidth - _lastRenderedIslandWidth) > 0.01)
            {
                IslandBorder.Width = renderWidth;
                _lastRenderedIslandWidth = renderWidth;
            }

            if (Math.Abs(renderHeight - _lastRenderedIslandHeight) > 0.01)
            {
                IslandBorder.Height = renderHeight;
                _lastRenderedIslandHeight = renderHeight;
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

            IslandProgressBar.Update(dt, t, GetDisplayedProgress(), renderWidth, renderHeight);

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

            double radius = Math.Min(renderHeight / 2.0, 20.0);
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

            float clipRight = (float)renderWidth;
            if (float.IsNaN(_lastClipRight) || Math.Abs(_lastClipRight - clipRight) > 0.01f)
            {
                _contentClip.Right = clipRight;
                _lastClipRight = clipRight;
            }

            float clipBottom = (float)(renderHeight + physicalPixelLogical);
            if (float.IsNaN(_lastClipBottom) || Math.Abs(_lastClipBottom - clipBottom) > 0.01f)
            {
                _contentClip.Bottom = clipBottom;
                _lastClipBottom = clipBottom;
            }

            if (isDockPeekState)
            {
                int visiblePhys = GetDockPeekPhysicalPixels(_dpiScale);
                double visibleLogical = GetLogicalPixels(visiblePhys, _dpiScale);
                float clipTop = (float)(renderHeight - visibleLogical);
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

            int physX = displayWorkArea.X + (int)Math.Round((state.CenterX * _dpiScale) - (physWidth / 2.0));
            int physY;

            bool isSettled = Math.Abs(state.Height - IslandConfig.CompactHeight) < 1.0
                && Math.Abs(state.Y - _controller.TargetY) < 1.0;

            if (isSettled && _controller.IsDocked && !_controller.IsHovered && !_controller.IsNotifying && !_controller.IsDragging)
            {
                int visiblePhys = GetDockPeekPhysicalPixels(_dpiScale);
                physY = monitorTopPhys + visiblePhys - physHeight;
            }
            else
            {
                physY = displayWorkArea.Y + (int)Math.Round(state.Y * _dpiScale);
            }

            if (_controller.IsOffscreen() || shouldDisplayDockedLineNow)
            {
                RectInt32 virtualScreen = WindowInterop.GetVirtualScreenBounds();
                physY = virtualScreen.Y - physHeight - 64;
                ShowLineWindow(physX, monitorTopPhys, physWidth);
                UpdateShadowState();
            }

            if (physX != _lastPhysX || physY != _lastPhysY || physWidth != _lastPhysW || physHeight != _lastPhysH)
            {
                this.AppWindow.MoveAndResize(new RectInt32(physX, physY, physWidth, physHeight));
                _lastPhysX = physX;
                _lastPhysY = physY;
                _lastPhysW = physWidth;
                _lastPhysH = physHeight;
            }

            UpdateAnchorPhysicalPoint(displayWorkArea, state, physWidth, physHeight);
            UpdateRenderLoopState();
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
