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
                _controller.IsOffscreen() || ShouldUseDockedLinePresentation(displayWorkArea));
        }

        private void CursorTrackerTimer_Tick(object? sender, object e)
        {
            RectInt32 displayWorkArea = GetCurrentDisplayWorkArea();
            if (!ShouldUseDockedLinePresentation(displayWorkArea) || _controller.IsHovered)
            {
                _cursorTrackerTimer.Stop();
                return;
            }

            GetCursorPos(out var pt);
            int activationTopPhys = displayWorkArea.Y + 1;
            int lineLeftPhys = _lastPhysX;
            int lineRightPhys = _lastPhysX + _lastPhysW;

            if (pt.Y <= activationTopPhys
                && pt.Y >= displayWorkArea.Y - 1
                && pt.X >= lineLeftPhys
                && pt.X <= lineRightPhys)
            {
                _hoverTicks++;
                if (_hoverTicks * IslandConfig.CursorTrackerIntervalMs >= IslandConfig.DockedHoverDelayMs)
                {
                    _hoverTicks = 0;
                    _cursorTrackerTimer.Stop();
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
            _cursorTrackerTimer.Stop();
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
            if (ShouldUseDockedLinePresentation(GetCurrentDisplayWorkArea()))
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
    }
}
