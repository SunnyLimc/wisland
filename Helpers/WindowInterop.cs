using System;
using System.Runtime.InteropServices;

namespace island.Helpers
{
    /// <summary>
    /// Helper class encapsulating native Windows API calls (P/Invoke) for window state monitoring and DWM attributes.
    /// </summary>
    public static class WindowInterop
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private enum MONITOR_DPI_TYPE
        {
            MDT_EFFECTIVE_DPI = 0
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        private const int SW_SHOWMAXIMIZED = 3;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// Checks if the provided window handle represents a maximized window.
        /// </summary>
        public static bool IsWindowMaximized(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            
            WINDOWPLACEMENT placement = new WINDOWPLACEMENT();
            placement.length = Marshal.SizeOf(placement);
            
            if (GetWindowPlacement(hwnd, ref placement))
            {
                return placement.showCmd == SW_SHOWMAXIMIZED;
            }
            return false;
        }

        public static double GetDpiScaleForPoint(int x, int y)
        {
            var point = new POINT { X = x, Y = y };
            IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            {
                return dpiX / 96.0;
            }

            return 1.0;
        }

        // --- DWM API for Shadow Control ---
        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        
        public const int DWMWCP_DEFAULT = 0;       // Let the system decide
        public const int DWMWCP_DONOTROUND = 1;    // No rounding, no shadow
        public const int DWMWCP_ROUND = 2;         // Full rounded corners + large shadow
        public const int DWMWCP_ROUNDSMALL = 3;    // Small rounded corners + small/light shadow
    }
}
