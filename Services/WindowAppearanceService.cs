using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using island.Helpers;
using island.Models;
using island.Views;

namespace island.Services
{
    public sealed class WindowAppearanceService
    {
        private bool? _lastHiddenLine;

        public void ApplyBackdrop(Window window, Border islandBorder, CompactView compactView, ExpandedMediaView expandedView, BackdropType type)
        {
            Color textColor;
            Color subTextColor;

            switch (type)
            {
                case BackdropType.Mica:
                    window.SystemBackdrop = new MicaBackdrop();
                    islandBorder.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                    textColor = Microsoft.UI.Colors.Black;
                    subTextColor = Microsoft.UI.Colors.DimGray;
                    break;
                case BackdropType.Acrylic:
                    window.SystemBackdrop = new DesktopAcrylicBackdrop();
                    islandBorder.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                    textColor = Microsoft.UI.Colors.Black;
                    subTextColor = Microsoft.UI.Colors.DimGray;
                    break;
                case BackdropType.None:
                default:
                    window.SystemBackdrop = null;
                    islandBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
                    textColor = Microsoft.UI.Colors.White;
                    subTextColor = Microsoft.UI.Colors.LightGray;
                    break;
            }

            compactView.SetTextColor(textColor);
            expandedView.SetColors(textColor, subTextColor);
        }

        public void ApplyWindowCornerPreference(Window window, bool isHiddenLine)
        {
            if (_lastHiddenLine == isHiddenLine)
            {
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            int preference = isHiddenLine ? WindowInterop.DWMWCP_DONOTROUND : WindowInterop.DWMWCP_ROUND;
            WindowInterop.DwmSetWindowAttribute(hwnd, WindowInterop.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            _lastHiddenLine = isHiddenLine;
        }
    }
}
