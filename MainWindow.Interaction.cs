using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Graphics;
using island.Models;
using WinUIEx;

namespace island
{
    public sealed partial class MainWindow
    {
        private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement fe && (fe is Button || fe.Parent is Button))
            {
                return;
            }

            var props = e.GetCurrentPoint(RootGrid).Properties;
            if (!props.IsLeftButtonPressed)
            {
                return;
            }

            _controller.IsDragging = true;
            RootGrid.CapturePointer(e.Pointer);

            GetCursorPos(out _dragStartScreenPos);

            double physCenterX = _controller.Current.CenterX * _dpiScale;
            double physCenterY = _controller.Current.Y * _dpiScale;

            _dragPhysicalOffsetX = _dragStartScreenPos.X - physCenterX;
            _dragPhysicalOffsetY = _dragStartScreenPos.Y - physCenterY;

            UpdateState();
        }

        private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_controller.IsDragging)
            {
                return;
            }

            GetCursorPos(out var currentPos);

            double targetPhysCenterX = currentPos.X - _dragPhysicalOffsetX;
            double targetPhysCenterY = currentPos.Y - _dragPhysicalOffsetY;

            var display = DisplayArea.GetFromPoint(new PointInt32(currentPos.X, currentPos.Y), DisplayAreaFallback.Primary);
            var bounds = display.WorkArea;

            double halfWidthPhys = (IslandConfig.CompactWidth / 2.0) * _dpiScale;
            targetPhysCenterX = Math.Clamp(targetPhysCenterX, bounds.X + halfWidthPhys, bounds.X + bounds.Width - halfWidthPhys);
            targetPhysCenterY = Math.Clamp(targetPhysCenterY, bounds.Y, bounds.Y + bounds.Height - 10);

            _dpiScale = this.GetDpiForWindow() / 96.0;
            _controller.HandleDrag(targetPhysCenterX / _dpiScale, targetPhysCenterY / _dpiScale);
        }

        private void RootGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_controller.IsDragging)
            {
                return;
            }

            _controller.IsDragging = false;
            RootGrid.ReleasePointerCapture(e.Pointer);
            _controller.FinalizeDrag();
            UpdateState();
            UpdateShadowState();
            SavePositionSettings();
        }

        private void RootGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _hoverDebounceTimer.Stop();

            if (_controller.IsDocked && !_controller.IsDragging && _controller.IsForegroundMaximized)
            {
                _controller.IsHoverPending = true;
                _dockedHoverDelayTimer.Start();
            }
            else
            {
                _controller.IsHovered = true;
                UpdateState();
            }
        }

        private void RootGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_controller.IsHoverPending)
            {
                _controller.IsHoverPending = false;
                _dockedHoverDelayTimer.Stop();
            }
            else
            {
                _hoverDebounceTimer.Start();
            }
        }
    }
}
