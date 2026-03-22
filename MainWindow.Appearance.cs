using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;
using wisland.Models;

namespace wisland
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// Apply a system backdrop preference and refresh the current theme-aware palette.
        /// </summary>
        public void SetBackdrop(BackdropType type, bool persist = true)
        {
            _currentBackdropType = type;
            RefreshAppearance();

            if (persist && _settings.BackdropType != type)
            {
                _settings.BackdropType = type;
                _settings.Save();
            }
        }

        private void RefreshAppearance()
        {
            if (_isClosed)
            {
                return;
            }

            IslandVisualTokens tokens = _appearanceService.ApplyAppearance(
                this,
                HostSurface,
                IslandBorder,
                CompactContent,
                ExpandedContent,
                IslandProgressBar,
                _currentBackdropType,
                GetThemeKind(),
                _uiSettings.GetColorValue(UIColorType.Accent));

            _shellVisibilityService.ApplyAppearance(tokens.LinePalette, IslandConfig.NativeLinePhysicalHeight);
        }

        private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
            => RefreshAppearance();

        private void UiSettings_ColorValuesChanged(UISettings sender, object args)
        {
            if (_isClosed)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed)
                {
                    RefreshAppearance();
                }
            });
        }

        private IslandThemeKind GetThemeKind()
        {
            return RootGrid.ActualTheme switch
            {
                ElementTheme.Dark => IslandThemeKind.Dark,
                ElementTheme.Light => IslandThemeKind.Light,
                _ => GetThemeKindFallback()
            };
        }

        private IslandThemeKind GetThemeKindFallback()
        {
            Color background = _uiSettings.GetColorValue(UIColorType.Background);
            double luminance = ((0.2126 * background.R) + (0.7152 * background.G) + (0.0722 * background.B)) / 255.0;
            return luminance < 0.5 ? IslandThemeKind.Dark : IslandThemeKind.Light;
        }
    }
}
