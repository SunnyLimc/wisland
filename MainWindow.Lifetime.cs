using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using wisland.Helpers;
using wisland.Models;
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
            Logger.Debug($"Position saved: CenterX={_settings.CenterX:F1}, Y={_settings.LastY:F1}, Docked={_settings.IsDocked}");
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_isClosed)
            {
                return;
            }

            Logger.Info("MainWindow closing, beginning cleanup");
            _isClosed = true;

            _notificationCts?.Cancel();
            _notificationCts = null;

            RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
            RootGrid.Loaded -= RootGrid_Loaded;
            RootGrid.SizeChanged -= RootGrid_SizeChanged;
            this.Activated -= MainWindow_Activated;
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
            DisposePresentationMachine();
            _mediaService.Dispose();

            DisposeSessionPickerOverlay();
            _shellVisibilityService.Dispose();
            Logger.Flush();
        }

        private void RequestAppExit()
        {
            if (_isClosed)
            {
                return;
            }

            Logger.Info("Application exit requested");

            try { _settingsWindow?.Close(); } catch { }
            _settingsWindow = null;

            DisposeSessionPickerOverlay();
            Application.Current.Exit();
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            ReconcileStartupWindowBounds();
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            ReconcileStartupWindowBounds();
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ReconcileStartupWindowBounds();
        }

        private void ReconcileStartupWindowBounds()
        {
            if (_isClosed || _hasCompletedStartupBoundsReconcile)
            {
                return;
            }

            RectInt32 displayWorkArea = GetCurrentDisplayWorkArea();
            _dpiScale = GetDisplayDpiScale(displayWorkArea);

            IslandState state = _controller.Current;
            ClampControllerPositionToDisplay(displayWorkArea, state.Width, state.Height, _dpiScale);

            int physWidth = GetPhysicalPixels(state.Width, _dpiScale);
            int physHeight = GetPhysicalPixels(state.Height, _dpiScale);
            double expectedWidth = GetLogicalPixels(physWidth, _dpiScale);
            double expectedHeight = GetLogicalPixels(physHeight, _dpiScale);
            double physicalPixelLogical = GetLogicalPixels(1, _dpiScale);

            bool widthMismatch = CompactSurfaceLayout.NeedsBoundsReconcile(
                expectedWidth,
                RootGrid.ActualWidth,
                physicalPixelLogical);
            bool heightMismatch = CompactSurfaceLayout.NeedsBoundsReconcile(
                expectedHeight,
                RootGrid.ActualHeight,
                physicalPixelLogical);

            if (!widthMismatch && !heightMismatch)
            {
                _hasCompletedStartupBoundsReconcile = true;
                return;
            }

            if (_startupBoundsReconcileAttempts >= IslandConfig.StartupBoundsReconcileMaxPasses)
            {
                return;
            }

            _startupBoundsReconcileAttempts++;
            Logger.Debug($"Startup bounds reconciliation pass {_startupBoundsReconcileAttempts}: expected={expectedWidth:F1}x{expectedHeight:F1}, actual={RootGrid.ActualWidth:F1}x{RootGrid.ActualHeight:F1}");
            RectInt32 bounds = ResolveIslandWindowBounds(state, displayWorkArea, _dpiScale);
            ApplyWindowBounds(bounds, force: true);
            UpdateAnchorPhysicalPoint(displayWorkArea, state, physWidth, physHeight);
            StartRenderLoop();
        }
    }
}
