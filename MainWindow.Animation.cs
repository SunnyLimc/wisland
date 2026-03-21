using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using Windows.Graphics;
using island.Models;
using WinUIEx;

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

            _dpiScale = this.GetDpiForWindow() / 96.0;
            _mediaService.Tick(dt);

            double t = 1.0 - Math.Exp(-IslandConfig.AnimationSpeed * dt);
            _controller.Tick(dt);

            var state = _controller.Current;
            IslandProgressBar.Update(dt, t, GetDisplayedProgress(), state.Width, state.Height);

            IslandBorder.Width = state.Width;
            IslandBorder.Height = state.Height;

            bool isDockPeekState = _controller.IsDocked
                && !_controller.IsHovered
                && !_controller.IsNotifying
                && !_controller.IsDragging
                && state.Height <= IslandConfig.CompactHeight + 1;

            double radius = Math.Min(state.Height / 2.0, 20.0);
            IslandBorder.CornerRadius = isDockPeekState
                ? new CornerRadius(0)
                : new CornerRadius(radius);

            var vecRadius = new Vector2((float)radius);
            _contentClip.TopLeftRadius = isDockPeekState ? Vector2.Zero : vecRadius;
            _contentClip.TopRightRadius = isDockPeekState ? Vector2.Zero : vecRadius;
            _contentClip.BottomLeftRadius = isDockPeekState ? Vector2.Zero : vecRadius;
            _contentClip.BottomRightRadius = isDockPeekState ? Vector2.Zero : vecRadius;
            _contentClip.Right = (float)state.Width;
            _contentClip.Bottom = (float)state.Height;

            if (isDockPeekState)
            {
                double peek = _controller.IsForegroundMaximized ? IslandConfig.MaximizedDockPeekOffset : IslandConfig.DockPeekOffset;
                _contentClip.Top = (float)(state.Height - peek);
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

            int physWidth = (int)Math.Ceiling(state.Width * _dpiScale);
            int physHeight = (int)Math.Ceiling(state.Height * _dpiScale);
            int physX = (int)Math.Round((state.CenterX - state.Width / 2.0) * _dpiScale);
            int physY;
            var display = DisplayArea.GetFromWindowId(this.AppWindow.Id, DisplayAreaFallback.Primary);
            int monitorTopPhys = display.WorkArea.Y;

            bool isSettled = Math.Abs(state.Height - IslandConfig.CompactHeight) < 1.0
                && Math.Abs(state.Y - _controller.TargetY) < 1.0;

            if (isSettled && _controller.IsDocked && !_controller.IsHovered && !_controller.IsNotifying && !_controller.IsDragging)
            {
                double peek = _controller.IsForegroundMaximized ? IslandConfig.MaximizedDockPeekOffset : IslandConfig.DockPeekOffset;
                int visiblePhys = (int)Math.Round(peek * _dpiScale);
                physY = monitorTopPhys + visiblePhys - physHeight;
            }
            else
            {
                physY = (int)Math.Round(state.Y * _dpiScale);
            }

            if (_controller.IsOffscreen())
            {
                physY = -1000;
                ShowLineWindow();
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
        }

    }
}
