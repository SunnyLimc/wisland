using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wisland.Helpers;
using wisland.Models;
using wisland.Services;

namespace wisland.Views.Settings
{
    public sealed partial class AiSongOverridePage : UserControl
    {
        private readonly SettingsService _settings;
        private readonly AiSongResolverService _aiResolver;
        private readonly Action _onAiSettingsChanged;
        private bool _suppressSelectionChanged;
        private bool _isRefreshing;
        private readonly List<string> _languageCodes = new();

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
            if (_isRefreshing) return;
            _isRefreshing = true;
            _suppressSelectionChanged = true;

            EnableToggle.IsOn = _settings.AiSongOverrideEnabled;
            RefreshModelSelector();
            RefreshLanguageSelector();
            RefreshCacheCount();

            _suppressSelectionChanged = false;
            _isRefreshing = false;
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

        private void RefreshLanguageSelector()
        {
            if (!IsLoaded) return;

            PreferredLanguageSelector.Items.Clear();
            _languageCodes.Clear();

            // "None" option at index 0
            PreferredLanguageSelector.Items.Add(Loc.GetString("AiSong/LanguageNone"));
            _languageCodes.Add("");

            int selectedIndex = 0;
            var languages = AiPromptLanguage.All;
            for (int i = 0; i < languages.Count; i++)
            {
                var lang = languages[i];
                string badge = lang.HasNativePrompt
                    ? $" \u2022 {Loc.GetString("AiSong/NativePromptAvailable")}"
                    : "";
                PreferredLanguageSelector.Items.Add($"{lang.DisplayLabel}{badge}");
                _languageCodes.Add(lang.Code);

                if (string.Equals(lang.Code, _settings.AiPreferredLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i + 1;
                }
            }

            PreferredLanguageSelector.SelectedIndex = selectedIndex;

            TargetMarketBox.Text = _settings.AiTargetMarket ?? "";
            RefreshTargetMarketPlaceholder();

            PreferNativePromptToggle.IsOn = _settings.AiPreferNativePrompt;
            RefreshNativePromptAvailability();
        }

        private void RefreshTargetMarketPlaceholder()
        {
            string? code = _settings.AiPreferredLanguage;
            if (string.IsNullOrEmpty(code))
            {
                TargetMarketBox.PlaceholderText = Loc.GetString("AiSong/TargetMarketPlaceholderDefault");
                return;
            }

            var lang = AiPromptLanguage.All
                .FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            TargetMarketBox.PlaceholderText = lang?.DefaultMarket
                ?? Loc.GetString("AiSong/TargetMarketPlaceholderDefault");
        }

        private void RefreshNativePromptAvailability()
        {
            string? code = _settings.AiPreferredLanguage;
            bool hasNative = !string.IsNullOrEmpty(code)
                && AiPromptLanguage.All.Any(l =>
                    string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase) && l.HasNativePrompt);

            PreferNativePromptToggle.IsEnabled = hasNative;
            if (!hasNative && PreferNativePromptToggle.IsOn)
            {
                _suppressSelectionChanged = true;
                PreferNativePromptToggle.IsOn = false;
                _suppressSelectionChanged = false;
                _settings.AiPreferNativePrompt = false;
                _settings.Save();
            }

            NativePromptUnavailableHint.IsOpen = !string.IsNullOrEmpty(code) && !hasNative;
            NativePromptUnavailableHint.Message = Loc.GetString("AiSong/NativePromptUnavailable");
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

        private void PreferredLanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            int index = PreferredLanguageSelector.SelectedIndex;
            if (index >= 0 && index < _languageCodes.Count)
            {
                string code = _languageCodes[index];
                _settings.AiPreferredLanguage = string.IsNullOrEmpty(code) ? null : code;
                _settings.Save();
                RefreshTargetMarketPlaceholder();
                RefreshNativePromptAvailability();
                _onAiSettingsChanged();
            }
        }

        private void TargetMarketBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string value = TargetMarketBox.Text?.Trim() ?? "";
            _settings.AiTargetMarket = string.IsNullOrEmpty(value) ? null : value;
            _settings.Save();
            _onAiSettingsChanged();
        }

        private void PreferNativePromptToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            _settings.AiPreferNativePrompt = PreferNativePromptToggle.IsOn;
            _settings.Save();
            _onAiSettingsChanged();
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
