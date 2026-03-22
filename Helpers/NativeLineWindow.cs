using System;
using System.Runtime.InteropServices;

namespace island.Helpers
{
    public sealed class NativeLineWindow : IDisposable
    {
        private IntPtr _hwnd;
        private readonly WndProcDelegate _wndProc;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hwnd, [In] ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern IntPtr FillRect(IntPtr hDC, [In] ref RECT lprc, IntPtr hbr);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint WS_POPUP = 0x80000000;

        private const int SWP_NOACTIVATE = 0x0010;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNA = 8;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint LWA_ALPHA = 0x00000002;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_NCHITTEST = 0x0084;
        private static readonly IntPtr HTTRANSPARENT = new IntPtr(-1);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private string _className = "DynamicIslandLineClass";
        private double _progress = 0.0;
        private IntPtr _activeBrush;
        private IntPtr _bgBrush;

        public NativeLineWindow()
        {
            _wndProc = WndProc; 
            var hInstance = GetModuleHandle(null);

            _activeBrush = CreateSolidBrush(0x00FFFFFF); // Pure white
            _bgBrush = CreateSolidBrush(0x00404040);     // Dark gray

            WNDCLASSEX wndClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                style = 0,
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = _className,
                hbrBackground = IntPtr.Zero
            };

            RegisterClassEx(ref wndClass);

            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                _className,
                "IslandLine",
                WS_POPUP,
                0, 0, 0, 0,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_hwnd != IntPtr.Zero)
            {
                SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
            }
        }

        public void SetProgress(double progress)
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            if (Math.Abs(_progress - progress) > 0.0001)
            {
                // Calculate if the pixel width actually changes (Layer 2: Progress Thresholding)
                GetClientRect(_hwnd, out var rect);
                int w = rect.Right - rect.Left;
                int oldPixelW = (int)Math.Round(w * _progress);
                int newPixelW = (int)Math.Round(w * progress);

                _progress = Math.Clamp(progress, 0.0, 1.0);

                // Only invalidate if at least one pixel of the progress bar has moved
                if (oldPixelW != newPixelW)
                {
                    InvalidateRect(_hwnd, IntPtr.Zero, true);
                }
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_NCHITTEST)
            {
                return HTTRANSPARENT;
            }

            if (msg == WM_PAINT)
            {
                IntPtr hdc = BeginPaint(hWnd, out var ps);
                GetClientRect(hWnd, out var rect);
                
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                int progressW = (int)Math.Round(w * _progress);

                // Draw Background Line (Darker)
                RECT bgRect = new RECT { Left = progressW, Top = 0, Right = w, Bottom = h };
                FillRect(hdc, ref bgRect, _bgBrush);

                // Draw Active Progress (Brighter)
                RECT activeRect = new RECT { Left = 0, Top = 0, Right = progressW, Bottom = h };
                FillRect(hdc, ref activeRect, _activeBrush);

                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Show(int x, int y, int width, int height)
        {
            if (_hwnd != IntPtr.Zero)
            {
                SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
                ShowWindow(_hwnd, SW_SHOWNA);
            }
        }

        public void Hide()
        {
            if (_hwnd != IntPtr.Zero)
            {
                ShowWindow(_hwnd, SW_HIDE);
            }
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_activeBrush != IntPtr.Zero) DeleteObject(_activeBrush);
            if (_bgBrush != IntPtr.Zero) DeleteObject(_bgBrush);
        }
    }
}
