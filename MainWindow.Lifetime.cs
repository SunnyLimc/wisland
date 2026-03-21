using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace island
{
    public sealed partial class MainWindow
    {
        /// <summary>Save current position and dock state to settings.</summary>
        private void SavePositionSettings()
        {
            _settings.CenterX = _controller.Current.CenterX;
            _settings.LastY = _controller.Current.Y;
            _settings.IsDocked = _controller.IsDocked;
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
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _hoverDebounceTimer.Tick -= HoverDebounceTimer_Tick;
            _dockedHoverDelayTimer.Tick -= DockedHoverDelayTimer_Tick;
            _cursorTrackerTimer.Tick -= CursorTrackerTimer_Tick;

            _hoverDebounceTimer.Stop();
            _dockedHoverDelayTimer.Stop();
            _cursorTrackerTimer.Stop();
            _foregroundWindowMonitor.ForegroundMaximizedChanged -= OnForegroundMaximizedChanged;
            _foregroundWindowMonitor.Dispose();

            _mediaService.MediaChanged -= OnMediaServiceChanged;
            _mediaService.TrackChanged -= OnTrackChanged;
            _mediaService.Dispose();

            _shellVisibilityService.Dispose();
        }
    }
}
