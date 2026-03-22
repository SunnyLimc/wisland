using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Graphics;
using wisland.Helpers;
using wisland.Models;

namespace wisland
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

            double physCenterX = _lastPhysX + (_lastPhysW / 2.0);
            double physTopY = _lastPhysY;

            _dragPhysicalOffsetX = _dragStartScreenPos.X - physCenterX;
            _dragPhysicalOffsetY = _dragStartScreenPos.Y - physTopY;

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

            RectInt32 workArea = WindowInterop.GetDisplayWorkAreaForPoint(currentPos.X, currentPos.Y);
            double targetDpiScale = WindowInterop.GetDpiScaleForPoint(currentPos.X, currentPos.Y);

            int currentPhysWidth = GetPhysicalPixels(_controller.Current.Width, targetDpiScale);
            int currentPhysHeight = GetPhysicalPixels(_controller.Current.Height, targetDpiScale);
            double halfWidthPhys = currentPhysWidth / 2.0;
            targetPhysCenterX = Math.Clamp(targetPhysCenterX, workArea.X + halfWidthPhys, workArea.X + workArea.Width - halfWidthPhys);
            targetPhysCenterY = Math.Clamp(targetPhysCenterY, workArea.Y, workArea.Y + workArea.Height - 10);

            _dpiScale = targetDpiScale;
            SetActiveDisplayAnchorFromDrag(workArea, targetPhysCenterX, targetPhysCenterY, currentPhysHeight);
            _controller.HandleDrag(
                (targetPhysCenterX - workArea.X) / targetDpiScale,
                (targetPhysCenterY - workArea.Y) / targetDpiScale);
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

            if (_isContextFlyoutOpen)
            {
                _dockedHoverDelayTimer.Stop();
                return;
            }

            if (SupportsDockedLinePresentation(GetCurrentDisplayWorkArea()) && !IsPointerHoverMode(_hoverMode))
            {
                _dockedHoverDelayTimer.Stop();
                return;
            }

            if (_controller.IsDocked && !_controller.IsDragging && _controller.IsForegroundMaximized)
            {
                SetHoverMode(HoverMode.PointerPending, updateState: false);
                _dockedHoverDelayTimer.Start();
            }
            else
            {
                SetHoverMode(HoverMode.PointerActive);
            }
        }

        private void RootGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isContextFlyoutOpen)
            {
                _hoverDebounceTimer.Stop();
                _dockedHoverDelayTimer.Stop();
                return;
            }

            if (SupportsDockedLinePresentation(GetCurrentDisplayWorkArea()) && !IsPointerHoverMode(_hoverMode))
            {
                _dockedHoverDelayTimer.Stop();
                return;
            }

            if (_hoverMode == HoverMode.PointerPending)
            {
                _dockedHoverDelayTimer.Stop();
                SetHoverMode(HoverMode.None, updateState: false);
            }
            else
            {
                _hoverDebounceTimer.Start();
            }
        }

        private void IslandContextFlyout_Opening(object sender, object e)
        {
            _isContextFlyoutOpen = true;
            _hoverModeBeforeContextFlyout = _hoverMode;
            _hoverDebounceTimer.Stop();
            _dockedHoverDelayTimer.Stop();
        }

        private void IslandContextFlyout_Closed(object sender, object e)
        {
            _isContextFlyoutOpen = false;
            ReconcileHoverStateAfterContextFlyout();
        }

        private void ReconcileHoverStateAfterContextFlyout()
        {
            if (_isClosed)
            {
                return;
            }

            GetCursorPos(out var cursorPoint);
            if (IsCursorWithinIslandBounds(cursorPoint, 0))
            {
                SetHoverMode(HoverMode.PointerActive);
                return;
            }

            if (_hoverModeBeforeContextFlyout == HoverMode.PointerActive)
            {
                _hoverDebounceTimer.Stop();
                _hoverDebounceTimer.Start();
                return;
            }

            if (_hoverModeBeforeContextFlyout == HoverMode.PointerPending
                || IsLineHoverMode(_hoverModeBeforeContextFlyout))
            {
                SetHoverMode(HoverMode.None);
                return;
            }

            UpdateState();
        }
    }
}
