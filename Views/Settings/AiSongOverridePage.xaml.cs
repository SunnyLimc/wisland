using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wisland.Helpers;
using wisland.Services;

namespace wisland.Views.Settings
{
    public sealed partial class AiSongOverridePage : UserControl
    {
        private readonly SettingsService _settings;
        private readonly AiSongResolverService _aiResolver;
        private readonly Action _onAiSettingsChanged;
        private bool _suppressSelectionChanged;

        public AiSongOverridePage(SettingsService settings, AiSongResolverService aiResolver, Action onAiSettingsChanged)
        {
            _settings = settings;
            _aiResolver = aiResolver;
            _onAiSettingsChanged = onAiSettingsChanged;
            this.InitializeComponent();
            Loaded += OnLoaded;
            _aiResolver.CacheChanged += OnCacheChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshUI();
        }

        public void RefreshUI()
        {
            _suppressSelectionChanged = true;

            EnableToggle.IsOn = _settings.AiSongOverrideEnabled;
            RefreshModelSelector();
            RefreshCacheCount();

            _suppressSelectionChanged = false;
        }

        private void RefreshModelSelector()
        {
            ModelSelector.Items.Clear();
            var models = _settings.AiModels;

            if (models.Count == 0)
            {
                NoModelsHint.Visibility = Visibility.Visible;
                ModelSelector.Visibility = Visibility.Collapsed;
                return;
            }

            NoModelsHint.Visibility = Visibility.Collapsed;
            ModelSelector.Visibility = Visibility.Visible;

            int selectedIndex = -1;
            for (int i = 0; i < models.Count; i++)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{models[i].DisplayName} ({models[i].ModelId})",
                    Tag = models[i].Id
                };
                ModelSelector.Items.Add(item);
                if (string.Equals(models[i].Id, _settings.ActiveAiModelId, StringComparison.Ordinal))
                {
                    selectedIndex = i;
                }
            }

            if (selectedIndex >= 0)
                ModelSelector.SelectedIndex = selectedIndex;
        }

        private void RefreshCacheCount()
        {
            CacheCountText.Text = Loc.GetFormatted("AiSong/CachedEntries", _aiResolver.CacheCount);
        }

        private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            _settings.AiSongOverrideEnabled = EnableToggle.IsOn;
            _settings.Save();
            _onAiSettingsChanged();
        }

        private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (ModelSelector.SelectedItem is ComboBoxItem ci && ci.Tag is string id)
            {
                _settings.ActiveAiModelId = id;
                _settings.Save();
                _onAiSettingsChanged();
            }
        }

        private void ClearLast_Click(object sender, RoutedEventArgs e)
        {
            _aiResolver.ClearLastEntry();
            RefreshCacheCount();
            _onAiSettingsChanged();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            _aiResolver.ClearCache();
            RefreshCacheCount();
            _onAiSettingsChanged();
        }

        private void OnCacheChanged()
        {
            DispatcherQueue?.TryEnqueue(RefreshCacheCount);
        }
    }
}
