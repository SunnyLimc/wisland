using System;
using System.Runtime.InteropServices;
using Windows.UI;
using island.Models;

namespace island.Helpers
{
    public sealed class NativeLineWindow : IDisposable
    {
        private IntPtr _hwnd;
        private readonly WndProcDelegate _wndProc;
        private readonly string _className = "DynamicIslandLineClass";
        private double _progress;
        private bool _isVisible;
        private int _lastX;
        private int _lastY;
        private int _lastWidth;
        private int _lastHeight;
        private LinePalette _palette = new(
            Color.FromArgb(96, 255, 255, 255),
            Color.FromArgb(78, 104, 118, 138),
            Color.FromArgb(224, 116, 176, 255),
            Color.FromArgb(248, 214, 236, 255),
            Color.FromArgb(96, 0, 0, 0),
            Color.FromArgb(92, 72, 84, 100));

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
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            uint crKey,
            ref BLENDFUNCTION pblend,
            uint dwFlags);

        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint WS_POPUP = 0x80000000;

        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNA = 8;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint WM_NCHITTEST = 0x0084;
        private static readonly IntPtr HTTRANSPARENT = new IntPtr(-1);
        private const uint ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;

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
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        public NativeLineWindow()
        {
            _wndProc = WndProc;
            IntPtr hInstance = GetModuleHandle(null);

            WNDCLASSEX wndClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                style = 0,
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = _className,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = string.Empty
            };

            RegisterClassEx(ref wndClass);

            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                _className,
                "IslandLine",
                WS_POPUP,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);
        }

        public void ApplyPalette(LinePalette palette)
        {
            _palette = palette;

            if (_isVisible)
            {
                RenderLayeredWindow();
            }
        }

        public void SetProgress(double progress)
        {
            double clamped = Math.Clamp(progress, 0.0, 1.0);
            if (Math.Abs(_progress - clamped) <= 0.0001)
            {
                return;
            }

            _progress = clamped;

            if (_isVisible)
            {
                RenderLayeredWindow();
            }
        }

        public void Show(int x, int y, int width, int height)
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            _lastX = x;
            _lastY = y;
            _lastWidth = Math.Max(1, width);
            _lastHeight = Math.Max(1, height);

            SetWindowPos(_hwnd, HWND_TOPMOST, _lastX, _lastY, _lastWidth, _lastHeight, SWP_NOACTIVATE);
            RenderLayeredWindow();
            ShowWindow(_hwnd, SW_SHOWNA);
            _isVisible = true;
        }

        public void Hide()
        {
            if (_hwnd == IntPtr.Zero || !_isVisible)
            {
                return;
            }

            ShowWindow(_hwnd, SW_HIDE);
            _isVisible = false;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_NCHITTEST)
            {
                return HTTRANSPARENT;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void RenderLayeredWindow()
        {
            if (_hwnd == IntPtr.Zero || _lastWidth <= 0 || _lastHeight <= 0)
            {
                return;
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return;
            }

            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                return;
            }

            BITMAPINFO bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = _lastWidth,
                    biHeight = -_lastHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            IntPtr pixelBuffer;
            IntPtr dibSection = CreateDIBSection(screenDc, ref bitmapInfo, DIB_RGB_COLORS, out pixelBuffer, IntPtr.Zero, 0);
            if (dibSection == IntPtr.Zero || pixelBuffer == IntPtr.Zero)
            {
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                return;
            }

            IntPtr oldBitmap = SelectObject(memoryDc, dibSection);

            try
            {
                int[] pixels = BuildPixelBuffer(_lastWidth, _lastHeight);
                Marshal.Copy(pixels, 0, pixelBuffer, pixels.Length);

                POINT destination = new POINT { X = _lastX, Y = _lastY };
                SIZE size = new SIZE { cx = _lastWidth, cy = _lastHeight };
                POINT source = new POINT { X = 0, Y = 0 };
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(_hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(memoryDc, oldBitmap);
                DeleteObject(dibSection);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private int[] BuildPixelBuffer(int width, int height)
        {
            int[] pixels = new int[width * height];
            int progressWidth = Math.Clamp((int)Math.Round(width * _progress), 0, width);
            int headWidth = progressWidth > 0 ? Math.Min(2, progressWidth) : 0;
            int headStart = Math.Max(0, progressWidth - headWidth);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool isActive = x < progressWidth;
                    bool isHead = isActive && x >= headStart;
                    Color color = ResolvePixelColor(x, y, width, height, isActive, isHead);
                    double edgeOpacity = GetEdgeOpacity(x, width);
                    pixels[(y * width) + x] = unchecked((int)ToPremultipliedArgb(color, edgeOpacity));
                }
            }

            return pixels;
        }

        private Color ResolvePixelColor(int column, int row, int width, int height, bool isActive, bool isHead)
        {
            Color baseColor;
            if (height <= 1)
            {
                baseColor = isHead ? _palette.ProgressHeadColor : (isActive ? _palette.ProgressFillColor : _palette.TrackBackgroundColor);
                return ApplyEdgeOutline(baseColor, column, width);
            }

            if (row == 0)
            {
                baseColor = isActive
                    ? Blend(_palette.TopHighlightColor, isHead ? _palette.ProgressHeadColor : _palette.ProgressFillColor, isHead ? 0.52 : 0.28)
                    : _palette.TopHighlightColor;
                return ApplyEdgeOutline(baseColor, column, width);
            }

            if (row == height - 1)
            {
                baseColor = isActive
                    ? Blend(_palette.BottomShadowColor, _palette.ProgressFillColor, 0.20)
                    : _palette.BottomShadowColor;
                return ApplyEdgeOutline(baseColor, column, width);
            }

            if (isHead)
            {
                baseColor = _palette.ProgressHeadColor;
                return ApplyEdgeOutline(baseColor, column, width);
            }

            baseColor = isActive ? _palette.ProgressFillColor : _palette.TrackBackgroundColor;
            return ApplyEdgeOutline(baseColor, column, width);
        }

        private Color ApplyEdgeOutline(Color color, int column, int width)
        {
            if (width <= 2)
            {
                return color;
            }

            if (column == 0 || column == width - 1)
            {
                return Blend(color, _palette.EdgeOutlineColor, 0.72);
            }

            if (column == 1 || column == width - 2)
            {
                return Blend(color, _palette.EdgeOutlineColor, 0.28);
            }

            return color;
        }

        private static double GetEdgeOpacity(int x, int width)
        {
            if (width <= 6)
            {
                return 1.0;
            }

            int distanceToEnd = Math.Min(x, width - 1 - x);
            return distanceToEnd switch
            {
                0 => 0.78,
                1 => 0.92,
                _ => 1.0
            };
        }

        private static uint ToPremultipliedArgb(Color color, double opacity)
        {
            byte alpha = (byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * color.A);
            byte red = (byte)Math.Round((color.R * alpha) / 255.0);
            byte green = (byte)Math.Round((color.G * alpha) / 255.0);
            byte blue = (byte)Math.Round((color.B * alpha) / 255.0);
            return ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
        }

        private static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0.0, 1.0);
            byte BlendChannel(byte start, byte end) => (byte)Math.Round(start + ((end - start) * amount));

            return Color.FromArgb(
                BlendChannel(from.A, to.A),
                BlendChannel(from.R, to.R),
                BlendChannel(from.G, to.G),
                BlendChannel(from.B, to.B));
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
