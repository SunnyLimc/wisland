using Microsoft.UI.Xaml;
using System;
using wisland.Helpers;
namespace wisland.Services
{
    public sealed class ForegroundWindowMonitor : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Func<IntPtr> _windowHandleProvider;
        private bool _isDisposed;

        public bool IsForegroundMaximized { get; private set; }

        public event Action<bool>? ForegroundMaximizedChanged;

        public ForegroundWindowMonitor(Func<IntPtr> windowHandleProvider, TimeSpan interval)
        {
            _windowHandleProvider = windowHandleProvider;
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += OnTimerTick;
        }

        public void SetActive(bool isActive)
        {
            if (_isDisposed)
            {
                return;
            }

            if (isActive)
            {
                if (!_timer.IsEnabled)
                {
                    _timer.Start();
                    CheckNow();
                }
            }
            else
            {
                _timer.Stop();
                UpdateState(false);
            }
        }

        public void CheckNow()
        {
            if (_isDisposed)
            {
                return;
            }

            IntPtr currentWindow = _windowHandleProvider();
            IntPtr foregroundWindow = WindowInterop.GetForegroundWindow();
            bool isMaximized = foregroundWindow != IntPtr.Zero
                && foregroundWindow != currentWindow
                && WindowInterop.IsWindowMaximized(foregroundWindow);

            UpdateState(isMaximized);
        }

        private void OnTimerTick(object? sender, object e) => CheckNow();

        private void UpdateState(bool isForegroundMaximized)
        {
            if (isForegroundMaximized == IsForegroundMaximized)
            {
                return;
            }

            Logger.Debug($"Foreground maximized state: {IsForegroundMaximized} -> {isForegroundMaximized}");
            IsForegroundMaximized = isForegroundMaximized;
            ForegroundMaximizedChanged?.Invoke(isForegroundMaximized);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        }
    }
}
