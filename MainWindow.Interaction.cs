using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Graphics;
using Microsoft.UI.Input;
using wisland.Helpers;
using wisland.Models;
namespace wisland
{
    public sealed partial class MainWindow
    {
        private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_hasInitializedWindowBounds)
            {
                return;
            }

            _lastPointerDeviceType = e.Pointer.PointerDeviceType;
            _lastPointerScreenPos = GetPointerScreenPosition(e);

            if (e.OriginalSource is DependencyObject origin && IsWithinButton(origin))
            {
                if (IsTouch) RestartTouchAutoCollapseTimer();
                return;
            }

            if (_isSessionPickerOpen)
            {
                HideSessionPickerOverlay(reconcileHover: false);
            }

            var props = e.GetCurrentPoint(RootGrid).Properties;
            if (!props.IsLeftButtonPressed)
            {
                return;
            }

            if (IsTouch)
            {
                _touchDownTimestamp = e.GetCurrentPoint(RootGrid).Timestamp;
                _touchDownPosition = e.GetCurrentPoint(RootGrid).Position;
                _isTouchTapCandidate = true;
                _isTouchSwiping = false;
                _swipeCumulativeX = 0;
                RootGrid.CapturePointer(e.Pointer);
                _touchLongPressTimer.Start();

                InitTouchDragTracking(e);
                _dragStartScreenPos = GetDragScreenPosition(e);
                Logger.Debug($"Touch down at screen ({_dragStartScreenPos.X}, {_dragStartScreenPos.Y})");

                double physCenterX = _lastPhysX + (_lastPhysW / 2.0);
                double physTopY = _lastPhysY;
                _dragPhysicalOffsetX = _dragStartScreenPos.X - physCenterX;
                _dragPhysicalOffsetY = _dragStartScreenPos.Y - physTopY;
                RestartTouchAutoCollapseTimer();
            }
            else
            {
                _controller.IsDragging = true;
                RootGrid.CapturePointer(e.Pointer);

                _dragStartScreenPos = GetDragScreenPosition(e);
                Logger.Debug($"Drag started at screen ({_dragStartScreenPos.X}, {_dragStartScreenPos.Y})");

                double physCenterX = _lastPhysX + (_lastPhysW / 2.0);
                double physTopY = _lastPhysY;
                _dragPhysicalOffsetX = _dragStartScreenPos.X - physCenterX;
                _dragPhysicalOffsetY = _dragStartScreenPos.Y - physTopY;
                UpdateState();
            }
        }

        private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _lastPointerDeviceType = e.Pointer.PointerDeviceType;

            if (IsTouch && _isTouchTapCandidate)
            {
                var currentPoint = e.GetCurrentPoint(RootGrid).Position;
                double dx = currentPoint.X - _touchDownPosition.X;
                double dy = currentPoint.Y - _touchDownPosition.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance > IslandConfig.TouchTapMaxDistanceDip)
                {
                    _isTouchTapCandidate = false;
                    _touchLongPressTimer.Stop();

                    bool isExpanded = _controller.Current.ExpandedOpacity > IslandConfig.HitTestOpacityThreshold;
                    if (isExpanded && Math.Abs(dx) > Math.Abs(dy))
                    {
                        _isTouchSwiping = true;
                        _swipeCumulativeX = dx;
                    }
                    else
                    {
                        InitTouchDragTracking(e);
                        _controller.IsDragging = true;
                        UpdateState();
                    }
                }
                return;
            }

            if (IsTouch && _isTouchSwiping)
            {
                var currentPoint = e.GetCurrentPoint(RootGrid).Position;
                double dx = currentPoint.X - _touchDownPosition.X;
                _swipeCumulativeX = dx;

                if (Math.Abs(_swipeCumulativeX) >= IslandConfig.SwipeThresholdDip)
                {
                    ContentTransitionDirection direction = _swipeCumulativeX < 0
                        ? ContentTransitionDirection.Forward
                        : ContentTransitionDirection.Backward;

                    TryCycleDisplayedSession(direction);
                    _touchDownPosition = currentPoint;
                    _swipeCumulativeX = 0;
                    RestartTouchAutoCollapseTimer();
                }
                return;
            }

            if (!_controller.IsDragging)
            {
                return;
            }

            var currentPos = GetDragScreenPosition(e);
            _lastPointerScreenPos = currentPos;

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

            // For touch: move window IMMEDIATELY to break the feedback loop.
            // The render loop's ApplyWindowBounds will be a no-op for position.
            if (IsTouch && _isTouchDragging)
            {
                ApplyTouchDragWindowMove(targetPhysCenterX, targetPhysCenterY, currentPhysWidth);
            }

            _controller.HandleDrag(
                (targetPhysCenterX - workArea.X) / targetDpiScale,
                (targetPhysCenterY - workArea.Y) / targetDpiScale);
        }

        private void RootGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _lastPointerDeviceType = e.Pointer.PointerDeviceType;
            _lastPointerScreenPos = GetPointerScreenPosition(e);
            _touchLongPressTimer.Stop();

            if (IsTouch && _isTouchSwiping)
            {
                _isTouchSwiping = false;
                _swipeCumulativeX = 0;
                RootGrid.ReleasePointerCapture(e.Pointer);
                return;
            }

            if (IsTouch && _isTouchTapCandidate)
            {
                _isTouchTapCandidate = false;
                RootGrid.ReleasePointerCapture(e.Pointer);
                ulong elapsedMicroseconds = e.GetCurrentPoint(RootGrid).Timestamp - _touchDownTimestamp;
                if (elapsedMicroseconds <= (ulong)IslandConfig.TouchTapMaxDurationMs * 1000)
                {
                    ToggleTouchExpansion();
                }
                return;
            }

            if (!_controller.IsDragging)
            {
                return;
            }

            _controller.IsDragging = false;
            _isTouchDragging = false;
            RootGrid.ReleasePointerCapture(e.Pointer);
            _controller.FinalizeDrag();
            UpdateState();
            UpdateShadowState();
            SavePositionSettings();
            Logger.Debug($"Drag ended at CenterX={_controller.Current.CenterX:F1}, Y={_controller.Current.Y:F1}");
        }

        private void RootGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _lastPointerDeviceType = e.Pointer.PointerDeviceType;
            _lastPointerScreenPos = GetPointerScreenPosition(e);

            if (IsTouch)
            {
                _hoverDebounceTimer.Stop();
                return;
            }

            _hoverDebounceTimer.Stop();

            if (HasBlockingSurfaceOpen)
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
            _lastPointerDeviceType = e.Pointer.PointerDeviceType;

            if (IsTouch)
            {
                _hoverDebounceTimer.Stop();
                _dockedHoverDelayTimer.Stop();
                return;
            }

            if (HasBlockingSurfaceOpen)
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
            HideSessionPickerOverlay(reconcileHover: false);
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

            if (_touchExpandedLatch)
            {
                SetHoverMode(HoverMode.PointerActive);
                RestartTouchAutoCollapseTimer();
                return;
            }

            if (IsCursorWithinIslandBounds(_lastPointerScreenPos, 0))
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

        private static bool IsWithinButton(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is Button)
                {
                    return true;
                }

                element = VisualTreeHelper.GetParent(element);
            }

            return false;
        }
    }
}
