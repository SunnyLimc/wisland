using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIEx;
using wisland.Models;
using wisland.Services;
using wisland.Views.Settings;

namespace wisland
{
    public sealed class SettingsWindow : Window
    {
        private readonly SettingsService _settings;
        private readonly AiSongResolverService _aiResolver;
        private readonly WindowAppearanceService _appearanceService = new();
        private readonly Action<Models.BackdropType> _onBackdropChanged;
        private readonly Action _onAiSettingsChanged;

        private readonly NavigationView _navView;
        private readonly Frame _contentFrame;
        private readonly Grid _rootGrid;

        private AppearancePage? _appearancePage;
        private AiModelsPage? _aiModelsPage;
        private AiSongOverridePage? _aiSongOverridePage;
        private DiagnosticsPage? _diagnosticsPage;

        public SettingsWindow(
            SettingsService settings,
            AiSongResolverService aiResolver,
            Action<Models.BackdropType> onBackdropChanged,
            Action onAiSettingsChanged)
        {
            _settings = settings;
            _aiResolver = aiResolver;
            _onBackdropChanged = onBackdropChanged;
            _onAiSettingsChanged = onAiSettingsChanged;

            Title = "Wisland Settings";
            ExtendsContentIntoTitleBar = false;

            var manager = WindowManager.Get(this);
            manager.MinWidth = 640;
            manager.MinHeight = 480;
            manager.Width = 800;
            manager.Height = 560;

            _contentFrame = new Frame
            {
                Background = new SolidColorBrush(Colors.Transparent)
            };

            _navView = new NavigationView
            {
                IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                IsSettingsVisible = false,
                PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
                OpenPaneLength = 200,
                Background = new SolidColorBrush(Colors.Transparent),
                Content = _contentFrame
            };
            ApplyNavigationViewBackgrounds();

            _navView.MenuItems.Add(new NavigationViewItem
            {
                Content = "Appearance",
                Tag = "appearance",
                Icon = new SymbolIcon(Symbol.Highlight)
            });
            _navView.MenuItems.Add(new NavigationViewItem
            {
                Content = "AI Models",
                Tag = "aimodels",
                Icon = new FontIcon { Glyph = "\uE945" }
            });
            _navView.MenuItems.Add(new NavigationViewItem
            {
                Content = "AI Song Override",
                Tag = "aisongoverride",
                Icon = new FontIcon { Glyph = "\uE8D6" }
            });
            _navView.MenuItems.Add(new NavigationViewItem
            {
                Content = "Diagnostics",
                Tag = "diagnostics",
                Icon = new FontIcon { Glyph = "\uE9D9" }
            });

            _navView.SelectionChanged += NavView_SelectionChanged;
            _navView.Loaded += NavView_Loaded;

            _rootGrid = new Grid
            {
                Background = new SolidColorBrush(Colors.Transparent)
            };
            _rootGrid.Children.Add(_navView);

            Content = _rootGrid;
            ApplyBackdrop(_settings.BackdropType);
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e)
        {
            _navView.SelectedItem = _navView.MenuItems[0];
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                NavigateTo(tag);
            }
        }

        private void NavigateTo(string tag)
        {
            switch (tag)
            {
                case "appearance":
                    _appearancePage ??= new AppearancePage(_settings, OnBackdropChanged);
                    _contentFrame.Content = _appearancePage;
                    break;
                case "aimodels":
                    _aiModelsPage ??= new AiModelsPage(_settings, OnAiSettingsChanged);
                    _contentFrame.Content = _aiModelsPage;
                    break;
                case "aisongoverride":
                    _aiSongOverridePage ??= new AiSongOverridePage(_settings, _aiResolver, OnAiSettingsChanged);
                    _aiSongOverridePage.RefreshUI();
                    _contentFrame.Content = _aiSongOverridePage;
                    break;
                case "diagnostics":
                    _diagnosticsPage ??= new DiagnosticsPage(_settings);
                    _contentFrame.Content = _diagnosticsPage;
                    break;
            }
        }

        private void OnBackdropChanged(BackdropType type)
        {
            ApplyBackdrop(type);
            _onBackdropChanged(type);
        }

        private void OnAiSettingsChanged()
        {
            _aiSongOverridePage?.RefreshUI();
            _onAiSettingsChanged();
        }

        private void ApplyBackdrop(BackdropType type)
        {
            _appearanceService.ApplyBackdrop(this, type);
            _rootGrid.Background = type == BackdropType.None
                ? TryGetThemeBrush("ApplicationPageBackgroundThemeBrush")
                : new SolidColorBrush(Colors.Transparent);
        }

        private void ApplyNavigationViewBackgrounds()
        {
            var transparent = new SolidColorBrush(Colors.Transparent);
            _navView.Resources["NavigationViewContentBackground"] = transparent;
            _navView.Resources["NavigationViewDefaultPaneBackground"] = transparent;
            _navView.Resources["NavigationViewExpandedPaneBackground"] = transparent;
            _navView.Resources["NavigationViewMinimalPaneBackground"] = transparent;
            _navView.Resources["NavigationViewTopPaneBackground"] = transparent;
        }

        private static Brush TryGetThemeBrush(string resourceKey)
        {
            if (Application.Current.Resources.TryGetValue(resourceKey, out object value)
                && value is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(Colors.Transparent);
        }
    }
}
