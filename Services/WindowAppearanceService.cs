using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using island.Controls;
using island.Helpers;
using island.Models;
using island.Views;

namespace island.Services
{
    public sealed class WindowAppearanceService
    {
        private bool? _lastHiddenLine;
        private BackdropType? _lastBackdropType;
        private MicaBackdrop? _micaBackdrop;
        private DesktopAcrylicBackdrop? _acrylicBackdrop;

        public void ApplyAppearance(
            Window window,
            Border islandBorder,
            CompactView compactView,
            ExpandedMediaView expandedView,
            LiquidProgressBar progressBar,
            BackdropType backdropType,
            IslandThemeKind themeKind,
            Color accentColor)
        {
            ApplyBackdrop(window, backdropType);

            IslandVisualTokens tokens = ResolveTokens(backdropType, themeKind, accentColor);
            islandBorder.Background = new SolidColorBrush(tokens.SurfaceColor);
            compactView.SetTextColor(tokens.PrimaryTextColor);
            expandedView.SetColors(tokens.PrimaryTextColor, tokens.SecondaryTextColor, tokens.IconColor);
            progressBar.ApplyPalette(tokens.ProgressBarPalette);
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

        private void ApplyBackdrop(Window window, BackdropType type)
        {
            if (_lastBackdropType == type)
            {
                return;
            }

            switch (type)
            {
                case BackdropType.Mica:
                    _micaBackdrop ??= new MicaBackdrop();
                    window.SystemBackdrop = _micaBackdrop;
                    break;
                case BackdropType.Acrylic:
                    _acrylicBackdrop ??= new DesktopAcrylicBackdrop();
                    window.SystemBackdrop = _acrylicBackdrop;
                    break;
                case BackdropType.None:
                default:
                    window.SystemBackdrop = null;
                    break;
            }

            _lastBackdropType = type;
        }

        private static IslandVisualTokens ResolveTokens(BackdropType backdropType, IslandThemeKind themeKind, Color accentColor)
        {
            Color accent = NormalizeAccent(accentColor, themeKind);
            return themeKind == IslandThemeKind.Dark
                ? CreateDarkTokens(backdropType, accent)
                : CreateLightTokens(backdropType, accent);
        }

        private static IslandVisualTokens CreateLightTokens(BackdropType backdropType, Color accent)
        {
            Color surfaceBase = backdropType switch
            {
                BackdropType.None => Color.FromArgb(236, 243, 246, 250),
                _ => Color.FromArgb(148, 248, 250, 252)
            };

            Color surface = Blend(surfaceBase, accent, backdropType == BackdropType.None ? 0.08 : 0.04);
            Color primary = Color.FromArgb(255, 26, 31, 40);
            Color secondary = Color.FromArgb(255, 103, 111, 123);
            Color icon = primary;

            Color accentTail = Blend(accent, Color.FromArgb(255, 24, 44, 80), 0.14);
            Color accentHighlight = Blend(accent, Microsoft.UI.Colors.White, 0.34);
            Color baseColor = Blend(Color.FromArgb(255, 110, 126, 150), accent, 0.10);

            return new IslandVisualTokens(
                surface,
                primary,
                secondary,
                icon,
                new ProgressBarPalette(
                    WithAlpha(baseColor, 58),
                    WithAlpha(accent, 0),
                    WithAlpha(accentHighlight, 112),
                    WithAlpha(accent, 0),
                    WithAlpha(accent, 0),
                    WithAlpha(accent, 26),
                    WithAlpha(accentTail, 102),
                    WithAlpha(accentHighlight, 214),
                    WithAlpha(accentHighlight, 255)));
        }

        private static IslandVisualTokens CreateDarkTokens(BackdropType backdropType, Color accent)
        {
            Color surfaceBase = backdropType switch
            {
                BackdropType.None => Color.FromArgb(232, 20, 23, 30),
                _ => Color.FromArgb(132, 18, 22, 28)
            };

            Color surface = Blend(surfaceBase, accent, backdropType == BackdropType.None ? 0.07 : 0.04);
            Color primary = Color.FromArgb(255, 244, 247, 252);
            Color secondary = Color.FromArgb(255, 170, 178, 190);
            Color icon = primary;

            Color accentTail = Blend(accent, Microsoft.UI.Colors.White, 0.12);
            Color accentHighlight = Blend(accent, Microsoft.UI.Colors.White, 0.22);
            Color baseColor = Blend(Color.FromArgb(255, 214, 223, 235), accent, 0.08);

            return new IslandVisualTokens(
                surface,
                primary,
                secondary,
                icon,
                new ProgressBarPalette(
                    WithAlpha(baseColor, 44),
                    WithAlpha(accent, 0),
                    WithAlpha(accentHighlight, 92),
                    WithAlpha(accent, 0),
                    WithAlpha(accent, 0),
                    WithAlpha(accent, 18),
                    WithAlpha(accentTail, 78),
                    WithAlpha(accentHighlight, 188),
                    WithAlpha(Blend(accentHighlight, Microsoft.UI.Colors.White, 0.10), 242)));
        }

        private static Color NormalizeAccent(Color accentColor, IslandThemeKind themeKind)
        {
            double luminance = GetLuminance(accentColor);
            if (themeKind == IslandThemeKind.Light && luminance > 0.62)
            {
                return Blend(accentColor, Color.FromArgb(255, 36, 78, 150), 0.28);
            }

            if (themeKind == IslandThemeKind.Dark && luminance < 0.30)
            {
                return Blend(accentColor, Microsoft.UI.Colors.White, 0.22);
            }

            return accentColor;
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

        private static Color WithAlpha(Color color, byte alpha)
            => Color.FromArgb(alpha, color.R, color.G, color.B);

        private static double GetLuminance(Color color)
            => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;
    }
}
