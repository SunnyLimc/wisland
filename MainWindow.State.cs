using System;
using Windows.Graphics;
using island.Models;

namespace island
{
    public sealed partial class MainWindow
    {
        private void HoverDebounceTimer_Tick(object? sender, object e)
        {
            _hoverDebounceTimer.Stop();
            _controller.IsHovered = false;
            _controller.IsHoverPending = false;
            UpdateState();
        }

        private void DockedHoverDelayTimer_Tick(object? sender, object e)
        {
            _dockedHoverDelayTimer.Stop();
            _controller.IsHoverPending = false;
            _controller.IsHovered = true;
            UpdateState();
        }

        private void OnForegroundMaximizedChanged(bool isForegroundMaximized)
        {
            if (isForegroundMaximized != _controller.IsForegroundMaximized)
            {
                _controller.IsForegroundMaximized = isForegroundMaximized;
                UpdateState();
            }
        }

        private void UpdateShadowState()
        {
            RectInt32 displayWorkArea = GetCurrentDisplayWorkArea();
            _appearanceService.ApplyWindowCornerPreference(
                this,
                _controller.IsOffscreen() || ShouldDisplayDockedLineNow(displayWorkArea));
        }

        private void CursorTrackerTimer_Tick(object? sender, object e)
        {
            RectInt32 displayWorkArea = GetCurrentDisplayWorkArea();
            if (!SupportsDockedLinePresentation(displayWorkArea))
            {
                _hoverTicks = 0;
                _cursorTrackerTimer.Stop();
                return;
            }

            GetCursorPos(out var pt);
            int activationTopPhys = displayWorkArea.Y + 1;
            int lineLeftPhys = _lastPhysX;
            int lineRightPhys = _lastPhysX + _lastPhysW;
            bool isInActivationBand = pt.Y <= activationTopPhys
                && pt.Y >= displayWorkArea.Y - 1
                && pt.X >= lineLeftPhys
                && pt.X <= lineRightPhys;

            if (_controller.IsHovered)
            {
                _hoverTicks = 0;
                if (!IsCursorWithinIslandBounds(pt, 8))
                {
                    _controller.IsHovered = false;
                    _controller.IsHoverPending = false;
                    _lineWakeRequiresExitReset = true;
                    UpdateState();
                }

                return;
            }

            if (_lineWakeRequiresExitReset)
            {
                if (isInActivationBand)
                {
                    _hoverTicks = 0;
                    return;
                }

                _lineWakeRequiresExitReset = false;
            }

            if (isInActivationBand)
            {
                _hoverTicks++;
                if (_hoverTicks * IslandConfig.CursorTrackerIntervalMs >= IslandConfig.DockedHoverDelayMs)
                {
                    _hoverTicks = 0;
                    _controller.Current.Height = 1;
                    _controller.IsHovered = true;
                    UpdateState();
                    UpdateShadowState();
                }
            }
            else
            {
                _hoverTicks = 0;
            }
        }

        private void ShowLineWindow(int physicalX, int monitorTopPhysical, int physicalWidth)
        {
            _shellVisibilityService.ShowDockedLine(
                physicalX,
                monitorTopPhysical,
                physicalWidth,
                GetDisplayedProgress());
        }

        private void HideLineWindow()
        {
            _hoverTicks = 0;
            _shellVisibilityService.HideDockedLine();
        }

        private void UpdateState()
        {
            _controller.UpdateTargetState();
            _foregroundWindowMonitor.SetActive(_controller.IsDocked);
            UpdateCursorTrackerState();

            if (!ShouldUseDockedLinePresentation(GetCurrentDisplayWorkArea()))
            {
                HideLineWindow();
            }

            UpdateShadowState();
        }

        private void UpdateCursorTrackerState()
        {
            if (SupportsDockedLinePresentation(GetCurrentDisplayWorkArea()))
            {
                if (!_cursorTrackerTimer.IsEnabled)
                {
                    _cursorTrackerTimer.Start();
                }
            }
            else
            {
                _hoverTicks = 0;
                _cursorTrackerTimer.Stop();
            }
        }

        private bool IsCursorWithinIslandBounds(POINT point, int marginPhysical)
        {
            int left = _lastPhysX - marginPhysical;
            int top = _lastPhysY - marginPhysical;
            int right = _lastPhysX + _lastPhysW + marginPhysical;
            int bottom = _lastPhysY + _lastPhysH + marginPhysical;

            return point.X >= left
                && point.X <= right
                && point.Y >= top
                && point.Y <= bottom;
        }

        private bool ShouldDisplayDockedLineNow(RectInt32 workArea)
        {
            if (!ShouldUseDockedLinePresentation(workArea))
            {
                return false;
            }

            return _controller.Current.Height <= IslandConfig.CompactHeight + 1.0
                && Math.Abs(_controller.Current.Y - _controller.TargetY) < 1.0
                && _controller.Current.ExpandedOpacity < 0.05;
        }
    }
}
