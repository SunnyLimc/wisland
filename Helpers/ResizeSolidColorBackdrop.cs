using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WindowsCompositionColorBrush = Windows.UI.Composition.CompositionColorBrush;
using WindowsCompositionCompositor = Windows.UI.Composition.Compositor;

namespace wisland.Helpers
{
    internal sealed class ResizeSolidColorBackdrop : SystemBackdrop
    {
        private static IntPtr _dispatcherQueueController;

        private WindowsCompositionCompositor? _compositor;
        private WindowsCompositionColorBrush? _brush;
        private Color _color;

        public ResizeSolidColorBackdrop(Color color)
        {
            _color = color;
        }

        public void SetColor(Color color)
        {
            _color = color;

            if (_brush != null)
            {
                _brush.Color = color;
            }
        }

        protected override void OnTargetConnected(
            ICompositionSupportsSystemBackdrop connectedTarget,
            XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);

            EnsureWindowsSystemDispatcherQueue();
            _compositor = new WindowsCompositionCompositor();
            _brush = _compositor.CreateColorBrush(_color);
            connectedTarget.SystemBackdrop = _brush;
        }

        protected override void OnTargetDisconnected(
            ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            disconnectedTarget.SystemBackdrop = null;

            _brush?.Dispose();
            _brush = null;

            _compositor?.Dispose();
            _compositor = null;

            base.OnTargetDisconnected(disconnectedTarget);
        }

        private static void EnsureWindowsSystemDispatcherQueue()
        {
            if (_dispatcherQueueController != IntPtr.Zero
                || Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
            {
                return;
            }

            var options = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = DispatcherQueueThreadType.Current,
                apartmentType = DispatcherQueueApartmentType.None
            };

            int hr = CreateDispatcherQueueController(options, out _dispatcherQueueController);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(
            DispatcherQueueOptions options,
            out IntPtr dispatcherQueueController);

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions
        {
            public int dwSize;
            public DispatcherQueueThreadType threadType;
            public DispatcherQueueApartmentType apartmentType;
        }

        private enum DispatcherQueueThreadType
        {
            Dedicated = 1,
            Current = 2
        }

        private enum DispatcherQueueApartmentType
        {
            None = 0,
            ASTA = 1,
            STA = 2
        }
    }
}
