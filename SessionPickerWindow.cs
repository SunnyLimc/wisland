using System;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinUIEx;
using wisland.Views;

namespace wisland
{
    public sealed class SessionPickerWindow : Window
    {
        private bool _hasBeenShown;
        private bool _suppressDismiss;

        public SessionPickerWindow()
        {
            ExtendsContentIntoTitleBar = true;
            Content = View;

            WindowManager manager = WindowManager.Get(this);
            manager.IsTitleBarVisible = false;
            manager.IsAlwaysOnTop = true;
            manager.IsResizable = false;
            manager.IsMinimizable = false;
            manager.IsMaximizable = false;
            AppWindow.IsShownInSwitchers = false;

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
            AppWindow.MoveAndResize(bounds);

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

            View.FocusList();
        }

        public void MoveOverlay(RectInt32 bounds)
        {
            if (_hasBeenShown)
            {
                AppWindow.MoveAndResize(bounds);
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
