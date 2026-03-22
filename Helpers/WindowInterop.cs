using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Graphics;

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

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr lprcClip,
            MonitorEnumProc lpfnEnum,
            IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        private const int SW_SHOWMAXIMIZED = 3;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int DisplayWorkAreaCacheLifetimeMs = 1000;
        private static readonly object s_displayWorkAreaLock = new();
        private static RectInt32[]? s_cachedDisplayWorkAreas;
        private static long s_cachedDisplayWorkAreasTick;

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

        public static RectInt32 GetDisplayWorkAreaForPoint(int x, int y)
        {
            var point = new POINT { X = x, Y = y };
            IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            return GetMonitorWorkArea(monitor);
        }

        public static IReadOnlyList<RectInt32> GetDisplayWorkAreas()
        {
            long now = Environment.TickCount64;

            lock (s_displayWorkAreaLock)
            {
                if (s_cachedDisplayWorkAreas != null
                    && now - s_cachedDisplayWorkAreasTick < DisplayWorkAreaCacheLifetimeMs)
                {
                    return s_cachedDisplayWorkAreas;
                }
            }

            List<RectInt32> workAreas = new();

            EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                (hMonitor, _, _, _) =>
                {
                    MONITORINFOEX info = new MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                        szDevice = string.Empty
                    };

                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        RECT work = info.rcWork;
                        workAreas.Add(new RectInt32(
                            work.left,
                            work.top,
                            Math.Max(0, work.right - work.left),
                            Math.Max(0, work.bottom - work.top)));
                    }

                    return true;
                },
                IntPtr.Zero);

            RectInt32[] snapshot = workAreas.ToArray();

            lock (s_displayWorkAreaLock)
            {
                s_cachedDisplayWorkAreas = snapshot;
                s_cachedDisplayWorkAreasTick = now;
            }

            return snapshot;
        }

        public static RectInt32 GetPrimaryDisplayWorkArea()
        {
            foreach (RectInt32 workArea in GetDisplayWorkAreas())
            {
                bool containsOrigin = workArea.X <= 0
                    && workArea.Y <= 0
                    && workArea.X + workArea.Width > 0
                    && workArea.Y + workArea.Height > 0;

                if (containsOrigin)
                {
                    return workArea;
                }
            }

            IReadOnlyList<RectInt32> workAreas = GetDisplayWorkAreas();
            if (workAreas.Count > 0)
            {
                return workAreas[0];
            }

            return new RectInt32(0, 0, 1920, 1080);
        }

        public static RectInt32 GetVirtualScreenBounds()
            => new RectInt32(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                Math.Max(0, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
                Math.Max(0, GetSystemMetrics(SM_CYVIRTUALSCREEN)));

        private static RectInt32 GetMonitorWorkArea(IntPtr monitor)
        {
            if (monitor == IntPtr.Zero)
            {
                return GetPrimaryDisplayWorkArea();
            }

            MONITORINFOEX info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty
            };

            if (GetMonitorInfo(monitor, ref info))
            {
                RECT work = info.rcWork;
                return new RectInt32(
                    work.left,
                    work.top,
                    Math.Max(0, work.right - work.left),
                    Math.Max(0, work.bottom - work.top));
            }

            return GetPrimaryDisplayWorkArea();
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
