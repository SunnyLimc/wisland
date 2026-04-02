using System;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinUIEx;
using wisland.Helpers;
using wisland.Models;
using wisland.Views;

namespace wisland
{
    public sealed class SessionPickerWindow : Window
    {
        private bool _hasBeenShown;
        private bool _suppressDismiss;
        private WindowFrameInsets? _frameInsets;
        private RectInt32? _lastClientBounds;

        public SessionPickerWindow()
        {
            ExtendsContentIntoTitleBar = true;
            Content = View;
            SystemBackdrop = null;

            WindowManager manager = WindowManager.Get(this);
            manager.IsTitleBarVisible = false;
            manager.IsAlwaysOnTop = true;
            manager.IsResizable = false;
            manager.IsMinimizable = false;
            manager.IsMaximizable = false;
            AppWindow.IsShownInSwitchers = false;
            ConfigureChrome();

            Activated += SessionPickerWindow_Activated;
            View.DismissRequested += View_DismissRequested;
            View.SessionSelected += View_SessionSelected;
        }

        public SessionPickerOverlayView View { get; } = new();

        public bool IsOverlayVisible { get; private set; }

        public event EventHandler? DismissRequested;

        public event Action<string>? SessionSelected;

        public void ShowOverlay(RectInt32 bounds)
        {
            EnsurePrimed();
            ApplyBounds(bounds);

            _suppressDismiss = true;
            try
            {
                AppWindow.Show();
                Activate();
                IsOverlayVisible = true;
            }
            finally
            {
                _suppressDismiss = false;
            }

            CaptureFrameInsets();
            ReapplyBoundsIfNeeded();
            DispatcherQueue?.TryEnqueue(() =>
            {
                ReapplyBoundsIfNeeded();
                View.FocusList();
            });
        }

        public void MoveOverlay(RectInt32 bounds)
        {
            if (_hasBeenShown)
            {
                if (_lastClientBounds.HasValue && _lastClientBounds.Value.Equals(bounds))
                {
                    return;
                }

                ApplyBounds(bounds);
            }
        }

        public void HideOverlay()
        {
            if (!_hasBeenShown || !IsOverlayVisible)
            {
                return;
            }

            _suppressDismiss = true;
            try
            {
                AppWindow.Hide();
                IsOverlayVisible = false;
            }
            finally
            {
                _suppressDismiss = false;
            }
        }

        public void CloseOverlayWindow()
        {
            _suppressDismiss = true;
            try
            {
                HideOverlay();
                Activated -= SessionPickerWindow_Activated;
                View.DismissRequested -= View_DismissRequested;
                View.SessionSelected -= View_SessionSelected;
                Close();
            }
            finally
            {
                _suppressDismiss = false;
            }
        }

        private void View_DismissRequested(object? sender, EventArgs e)
            => DismissRequested?.Invoke(this, EventArgs.Empty);

        private void View_SessionSelected(string sessionKey)
            => SessionSelected?.Invoke(sessionKey);

        private void EnsurePrimed()
        {
            if (_hasBeenShown)
            {
                return;
            }

            RectInt32 primingBounds = GetPrimingBounds();
            _suppressDismiss = true;
            try
            {
                ApplyBounds(primingBounds);
                _hasBeenShown = true;
                Activate();
                CaptureFrameInsets();
                ApplyBounds(primingBounds);
                View.UpdateLayout();
                AppWindow.Hide();
                IsOverlayVisible = false;
            }
            finally
            {
                _suppressDismiss = false;
            }
        }

        private void ConfigureChrome()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                int cornerPreference = WindowInterop.DWMWCP_ROUND;
                WindowInterop.DwmSetWindowAttribute(
                    hwnd,
                    WindowInterop.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref cornerPreference,
                    sizeof(int));
            }
        }

        private void ApplyBounds(RectInt32 bounds)
        {
            ConfigureChrome();
            _lastClientBounds = bounds;
            WindowFrameInsets insets = _frameInsets ?? default;
            int moveX = bounds.X - insets.Left;
            int moveY = bounds.Y - insets.Top;
            int clientWidth = Math.Max(1, bounds.Width);
            int clientHeight = Math.Max(1, bounds.Height);
            int outerWidth = Math.Max(1, clientWidth + insets.Left + insets.Right);
            int outerHeight = Math.Max(1, clientHeight + insets.Top + insets.Bottom);

            // ResizeClient() was leaving this secondary WinUI window with a larger client area
            // than requested, so convert the measured client rect into an explicit outer rect.
            AppWindow.MoveAndResize(new RectInt32(moveX, moveY, outerWidth, outerHeight));
        }

        private void CaptureFrameInsets()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!WindowInterop.TryGetWindowFrameInsets(hwnd, out WindowFrameInsets insets)
                || (_frameInsets.HasValue && _frameInsets.Value.Equals(insets)))
            {
                return;
            }

            _frameInsets = insets;
            ReapplyBoundsIfNeeded();
        }

        private void ReapplyBoundsIfNeeded()
        {
            if (IsOverlayVisible && _lastClientBounds.HasValue)
            {
                ApplyBounds(_lastClientBounds.Value);
            }
        }

        private static RectInt32 GetPrimingBounds()
        {
            const int primingSize = 32;
            const int primingMargin = 64;
            RectInt32 virtualScreen = WindowInterop.GetVirtualScreenBounds();
            return new RectInt32(
                virtualScreen.X - primingSize - primingMargin,
                virtualScreen.Y - primingSize - primingMargin,
                primingSize,
                primingSize);
        }

        private void SessionPickerWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (_suppressDismiss || !IsOverlayVisible)
            {
                return;
            }

            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                DismissRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
