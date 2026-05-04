using System;
using System.Runtime.InteropServices;
using Windows.UI;
using wisland.Helpers;
using WinRT.Interop;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_NCDESTROY = 0x0082;

        private static readonly UIntPtr NativeResizeBackfillSubclassId = new(0x57534C44);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr NativeSubclassProc(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData);

        private NativeSubclassProc? _nativeResizeBackfillProc;
        private IntPtr _nativeResizeBackfillHwnd;
        private IntPtr _nativeResizeBackfillBrush;
        private uint _nativeResizeBackfillColorRef = uint.MaxValue;

        private void InstallNativeResizeBackfill()
        {
            if (_nativeResizeBackfillProc != null || _isClosed)
            {
                return;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            SetNativeResizeBackfillColor(ResolveResizeBackfillColor());
            if (_nativeResizeBackfillBrush == IntPtr.Zero)
            {
                return;
            }

            _nativeResizeBackfillProc = NativeResizeBackfillWndProc;
            if (!SetWindowSubclass(
                    hwnd,
                    _nativeResizeBackfillProc,
                    NativeResizeBackfillSubclassId,
                    UIntPtr.Zero))
            {
                _nativeResizeBackfillProc = null;
                DisposeNativeResizeBackfillBrush();
                return;
            }

            _nativeResizeBackfillHwnd = hwnd;
        }

        private void SetNativeResizeBackfillColor(Color color)
        {
            color = WindowSurfaceColorMath.CreateOpaque(color);

            uint colorRef = color.R
                | ((uint)color.G << 8)
                | ((uint)color.B << 16);

            if (_nativeResizeBackfillBrush != IntPtr.Zero
                && _nativeResizeBackfillColorRef == colorRef)
            {
                return;
            }

            IntPtr newBrush = CreateSolidBrush(colorRef);
            if (newBrush == IntPtr.Zero)
            {
                return;
            }

            IntPtr oldBrush = _nativeResizeBackfillBrush;
            _nativeResizeBackfillBrush = newBrush;
            _nativeResizeBackfillColorRef = colorRef;

            if (oldBrush != IntPtr.Zero)
            {
                DeleteObject(oldBrush);
            }
        }

        private void PaintNativeResizeBackfillNow()
        {
            if (_nativeResizeBackfillHwnd == IntPtr.Zero
                || _nativeResizeBackfillBrush == IntPtr.Zero)
            {
                return;
            }

            IntPtr hdc = GetDC(_nativeResizeBackfillHwnd);
            if (hdc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                TryPaintNativeResizeBackfillHdc(_nativeResizeBackfillHwnd, hdc);
            }
            finally
            {
                ReleaseDC(_nativeResizeBackfillHwnd, hdc);
            }
        }

        private bool TryPaintNativeResizeBackfillHdc(IntPtr hwnd, IntPtr hdc)
        {
            if (hdc == IntPtr.Zero
                || _nativeResizeBackfillBrush == IntPtr.Zero)
            {
                return false;
            }

            if (!GetClientRect(hwnd, out NativeRect rect))
            {
                return false;
            }

            return FillRect(hdc, ref rect, _nativeResizeBackfillBrush) != 0;
        }

        private IntPtr NativeResizeBackfillWndProc(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData)
        {
            if (msg == WM_ERASEBKGND)
            {
                return TryPaintNativeResizeBackfillHdc(hWnd, wParam)
                    ? new IntPtr(1)
                    : DefSubclassProc(hWnd, msg, wParam, lParam);
            }

            if (msg == WM_NCDESTROY)
            {
                if (_nativeResizeBackfillProc != null)
                {
                    RemoveWindowSubclass(
                        hWnd,
                        _nativeResizeBackfillProc,
                        NativeResizeBackfillSubclassId);
                }

                _nativeResizeBackfillProc = null;
                _nativeResizeBackfillHwnd = IntPtr.Zero;
                DisposeNativeResizeBackfillBrush();
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        private void DisposeNativeResizeBackfill()
        {
            if (_nativeResizeBackfillHwnd != IntPtr.Zero
                && _nativeResizeBackfillProc != null)
            {
                RemoveWindowSubclass(
                    _nativeResizeBackfillHwnd,
                    _nativeResizeBackfillProc,
                    NativeResizeBackfillSubclassId);
            }

            _nativeResizeBackfillProc = null;
            _nativeResizeBackfillHwnd = IntPtr.Zero;
            DisposeNativeResizeBackfillBrush();
        }

        private void DisposeNativeResizeBackfillBrush()
        {
            IntPtr brush = _nativeResizeBackfillBrush;
            _nativeResizeBackfillBrush = IntPtr.Zero;
            _nativeResizeBackfillColorRef = uint.MaxValue;

            if (brush != IntPtr.Zero)
            {
                DeleteObject(brush);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd,
            NativeSubclassProc pfnSubclass,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(
            IntPtr hWnd,
            NativeSubclassProc pfnSubclass,
            UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(
            IntPtr hWnd,
            out NativeRect lpRect);

        [DllImport("user32.dll")]
        private static extern int FillRect(
            IntPtr hDC,
            ref NativeRect lprc,
            IntPtr hbr);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(
            IntPtr hWnd,
            IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint colorRef);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
