using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace wisland
{
    public sealed partial class MainWindow
    {
        /// <summary>Save current position and dock state to settings.</summary>
        private void SavePositionSettings()
        {
            RectInt32 workArea = GetCurrentDisplayWorkArea();
            int physWidth = GetPhysicalPixels(_controller.Current.Width, _dpiScale);
            int physHeight = GetPhysicalPixels(_controller.Current.Height, _dpiScale);
            UpdateAnchorPhysicalPoint(workArea, _controller.Current, physWidth, physHeight);

            _settings.CenterX = _controller.Current.CenterX;
            _settings.LastY = _controller.IsDocked ? 0 : _controller.Current.Y;
            _settings.IsDocked = _controller.IsDocked;
            _settings.RelativeCenterX = _controller.Current.CenterX;
            _settings.RelativeTopY = _controller.IsDocked ? 0 : _controller.Current.Y;
            _settings.AnchorPhysicalX = _hasAnchorPhysicalPoint ? _anchorPhysicalX : null;
            _settings.AnchorPhysicalY = _hasAnchorPhysicalPoint ? _anchorPhysicalY : null;
            _settings.Save();
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;

            _notificationCts?.Cancel();
            _notificationCts = null;

            RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
            _uiSettings.ColorValuesChanged -= UiSettings_ColorValuesChanged;
            StopRenderLoop();
            _hoverDebounceTimer.Tick -= HoverDebounceTimer_Tick;
            _dockedHoverDelayTimer.Tick -= DockedHoverDelayTimer_Tick;
            _cursorTrackerTimer.Tick -= CursorTrackerTimer_Tick;
            _selectionLockTimer.Tick -= SelectionLockTimer_Tick;
            _autoFocusTimer.Tick -= AutoFocusTimer_Tick;
            ExpandedContent.SessionPickerToggleRequested -= ExpandedContent_SessionPickerToggleRequested;

            _hoverDebounceTimer.Stop();
            _dockedHoverDelayTimer.Stop();
            _cursorTrackerTimer.Stop();
            _selectionLockTimer.Stop();
            _autoFocusTimer.Stop();
            _foregroundWindowMonitor.ForegroundMaximizedChanged -= OnForegroundMaximizedChanged;
            _foregroundWindowMonitor.Dispose();

            _mediaService.SessionsChanged -= OnMediaServiceChanged;
            _mediaService.TrackChanged -= OnTrackChanged;
            _mediaService.Dispose();

            DisposeSessionPickerOverlay();
            _shellVisibilityService.Dispose();
        }

        private void RequestAppExit()
        {
            if (_isClosed)
            {
                return;
            }

            DisposeSessionPickerOverlay();
            Application.Current.Exit();
        }
    }
}
