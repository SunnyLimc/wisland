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

            if (_isContextFlyoutOpen)
            {
                return;
            }

            SetHoverMode(HoverMode.None);
        }

        private void DockedHoverDelayTimer_Tick(object? sender, object e)
        {
            _dockedHoverDelayTimer.Stop();

            if (_isContextFlyoutOpen)
            {
                return;
            }

            SetHoverMode(HoverMode.PointerActive);
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
            if (_isContextFlyoutOpen)
            {
                return;
            }

            RectInt32 displayWorkArea = GetCurrentDisplayWorkArea();
            if (!SupportsDockedLinePresentation(displayWorkArea))
            {
                _lineHoverElapsedMs = 0;
                _lineExitElapsedMs = 0;

                if (_hoverMode == HoverMode.LineActive)
                {
                    GetCursorPos(out var fallbackPoint);
                    SetHoverMode(
                        IsCursorWithinIslandBounds(fallbackPoint, IslandConfig.DockedLineBoundsMarginPhysical)
                            ? HoverMode.PointerActive
                            : HoverMode.None);
                }
                else if (IsLineHoverMode(_hoverMode))
                {
                    SetHoverMode(HoverMode.None);
                }

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

            switch (_hoverMode)
            {
                case HoverMode.LineActive:
                    _lineHoverElapsedMs = 0;
                    if (IsCursorWithinIslandBounds(pt, IslandConfig.DockedLineBoundsMarginPhysical))
                    {
                        _lineExitElapsedMs = 0;
                        return;
                    }

                    _lineExitElapsedMs += IslandConfig.CursorTrackerIntervalMs;
                    if (_lineExitElapsedMs >= IslandConfig.DockedLineExitHysteresisMs)
                    {
                        SetHoverMode(HoverMode.LineExitCooldown);
                    }
                    return;

                case HoverMode.LineExitCooldown:
                    _lineHoverElapsedMs = 0;
                    _lineExitElapsedMs = 0;
                    if (!isInActivationBand)
                    {
                        SetHoverMode(HoverMode.None, updateState: false);
                    }
                    return;

                case HoverMode.LinePending:
                    if (isInActivationBand)
                    {
                        _lineHoverElapsedMs += IslandConfig.CursorTrackerIntervalMs;
                        if (_lineHoverElapsedMs >= IslandConfig.DockedHoverDelayMs)
                        {
                            _controller.Current.Height = 1;
                            SetHoverMode(HoverMode.LineActive);
                            UpdateShadowState();
                        }
                    }
                    else
                    {
                        SetHoverMode(HoverMode.None, updateState: false);
                    }
                    return;

                default:
                    if (isInActivationBand)
                    {
                        if (_hoverMode != HoverMode.LinePending)
                        {
                            _lineHoverElapsedMs = 0;
                            SetHoverMode(HoverMode.LinePending, updateState: false);
                        }

                        _lineHoverElapsedMs += IslandConfig.CursorTrackerIntervalMs;
                        if (_lineHoverElapsedMs >= IslandConfig.DockedHoverDelayMs)
                        {
                            _controller.Current.Height = 1;
                            SetHoverMode(HoverMode.LineActive);
                            UpdateShadowState();
                        }
                    }
                    else if (_hoverMode == HoverMode.LinePending)
                    {
                        SetHoverMode(HoverMode.None, updateState: false);
                    }

                    return;
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
            _lineHoverElapsedMs = 0;
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
            UpdateRenderLoopState();
        }

        private void UpdateCursorTrackerState()
        {
            if (SupportsDockedLinePresentation(GetCurrentDisplayWorkArea()) && !IsPointerHoverMode(_hoverMode))
            {
                if (!_cursorTrackerTimer.IsEnabled)
                {
                    _cursorTrackerTimer.Start();
                }
            }
            else
            {
                _lineHoverElapsedMs = 0;
                _lineExitElapsedMs = 0;
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

        private void SetHoverMode(HoverMode mode, bool updateState = true)
        {
            HoverMode previousMode = _hoverMode;
            _hoverMode = mode;

            if (mode != HoverMode.LinePending)
            {
                _lineHoverElapsedMs = 0;
            }

            if (mode != HoverMode.LineActive)
            {
                _lineExitElapsedMs = 0;
            }

            if (!IsLineHoverMode(mode) && IsLineHoverMode(previousMode))
            {
                _lineHoverElapsedMs = 0;
                _lineExitElapsedMs = 0;
            }

            _controller.IsHovered = IsActiveHoverMode(mode);
            _controller.IsHoverPending = IsPendingHoverMode(mode);

            if (updateState)
            {
                UpdateState();
            }
        }

        private static bool IsPointerHoverMode(HoverMode mode)
            => mode == HoverMode.PointerPending || mode == HoverMode.PointerActive;

        private static bool IsLineHoverMode(HoverMode mode)
            => mode == HoverMode.LinePending || mode == HoverMode.LineActive || mode == HoverMode.LineExitCooldown;

        private static bool IsActiveHoverMode(HoverMode mode)
            => mode == HoverMode.PointerActive || mode == HoverMode.LineActive;

        private static bool IsPendingHoverMode(HoverMode mode)
            => mode == HoverMode.PointerPending || mode == HoverMode.LinePending;

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
