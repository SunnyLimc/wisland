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

        public event Action<WindowFrameInsets>? FrameInsetsChanged;

        public void ShowOverlay(RectInt32 bounds)
        {
            ApplyBounds(bounds);

            _suppressDismiss = true;
            try
            {
                if (!_hasBeenShown)
                {
                    _hasBeenShown = true;
                    Activate();
                }
                else
                {
                    AppWindow.Show();
                    Activate();
                }

                IsOverlayVisible = true;
            }
            finally
            {
                _suppressDismiss = false;
            }

            CaptureFrameInsets();
            View.FocusList();
        }

        public void MoveOverlay(RectInt32 bounds)
        {
            if (_hasBeenShown)
            {
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
            WindowFrameInsets insets = _frameInsets ?? default;
            int moveX = bounds.X - insets.Left;
            int moveY = bounds.Y - insets.Top;
            int clientWidth = Math.Max(1, bounds.Width);
            int clientHeight = Math.Max(1, bounds.Height);

            AppWindow.Move(new PointInt32(moveX, moveY));
            AppWindow.ResizeClient(new SizeInt32(clientWidth, clientHeight));
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
            FrameInsetsChanged?.Invoke(insets);
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
