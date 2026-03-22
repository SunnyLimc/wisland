using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using Windows.Graphics;
using island.Helpers;
using island.Models;

namespace island
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

            if (Math.Abs(_lastProgressBottomBleed - physicalPixelLogical) > 0.0001)
            {
                IslandProgressBar.Margin = new Thickness(0, 0, 0, -physicalPixelLogical);
                _lastProgressBottomBleed = physicalPixelLogical;
            }

            IslandProgressBar.Update(dt, t, GetDisplayedProgress(), renderWidth, renderHeight);

            IslandBorder.Width = renderWidth;
            IslandBorder.Height = renderHeight;

            bool isDockPeekState = _controller.IsDocked
                && !_controller.IsHovered
                && !_controller.IsNotifying
                && !_controller.IsDragging
                && !shouldDisplayDockedLineNow
                && state.Height <= IslandConfig.CompactHeight + 1;

            double radius = Math.Min(renderHeight / 2.0, 20.0);
            IslandBorder.CornerRadius = isDockPeekState
                ? new CornerRadius(0)
                : new CornerRadius(radius);
            HostSurface.CornerRadius = IslandBorder.CornerRadius;

            var vecRadius = new Vector2((float)radius);
            _contentClip.TopLeftRadius = isDockPeekState ? Vector2.Zero : vecRadius;
            _contentClip.TopRightRadius = isDockPeekState ? Vector2.Zero : vecRadius;
            _contentClip.BottomLeftRadius = isDockPeekState ? Vector2.Zero : vecRadius;
            _contentClip.BottomRightRadius = isDockPeekState ? Vector2.Zero : vecRadius;
            _contentClip.Right = (float)renderWidth;
            _contentClip.Bottom = (float)(renderHeight + physicalPixelLogical);

            if (isDockPeekState)
            {
                int visiblePhys = GetDockPeekPhysicalPixels(_dpiScale);
                double visibleLogical = GetLogicalPixels(visiblePhys, _dpiScale);
                _contentClip.Top = (float)(renderHeight - visibleLogical);
            }
            else
            {
                _contentClip.Top = 0;
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
                CompactContent.IsHitTestVisible = !isExpandedActive && state.IsHitTestVisible;
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

    }
}
