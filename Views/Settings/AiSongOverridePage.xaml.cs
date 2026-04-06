using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
                    ? $" \u2022 {Loc.GetString("AiSong/NativePromptBadge")}"
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
            RefreshLocalizationPanels();
        }

        private void RefreshTargetMarketPlaceholder()
        {
            string? code = _settings.AiPreferredLanguage;
            if (string.IsNullOrEmpty(code))
            {
                TargetMarketBox.PlaceholderText = Loc.GetString("AiSong/RegionPlaceholderDefault");
                return;
            }

            var lang = AiPromptLanguage.All
                .FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            TargetMarketBox.PlaceholderText = lang?.DefaultMarket
                ?? Loc.GetString("AiSong/RegionPlaceholderDefault");
        }

        private void RefreshLocalizationPanels()
        {
            string? code = _settings.AiPreferredLanguage;
            bool hasNative = !string.IsNullOrEmpty(code)
                && AiPromptLanguage.All.Any(l =>
                    string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase) && l.HasNativePrompt);

            // Show the native prompt toggle only when the selected language has a built-in prompt
            NativePromptPanel.Visibility = hasNative ? Visibility.Visible : Visibility.Collapsed;
            PreferNativePromptToggle.IsEnabled = hasNative;

            if (!hasNative && PreferNativePromptToggle.IsOn)
            {
                _suppressSelectionChanged = true;
                PreferNativePromptToggle.IsOn = false;
                _suppressSelectionChanged = false;
                _settings.AiPreferNativePrompt = false;
                _settings.Save();
            }

            // Hide region override when native prompt is active (the built-in prompt already targets the right region)
            bool showRegion = !string.IsNullOrEmpty(code) && !(hasNative && PreferNativePromptToggle.IsOn);
            RegionOverridePanel.Visibility = showRegion ? Visibility.Visible : Visibility.Collapsed;
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
                RefreshLocalizationPanels();
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
            RefreshLocalizationPanels();
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

        private CancellationTokenSource? _testCts;

        private async void TestRun_Click(object sender, RoutedEventArgs e)
        {
            string testTitle = TestSongTitleBox.Text?.Trim() ?? "";
            string testArtist = TestSongArtistBox.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(testTitle))
            {
                TestResultInfoBar.Severity = InfoBarSeverity.Warning;
                TestResultInfoBar.Message = Loc.GetString("AiSong/TestMissingTitle");
                TestResultInfoBar.IsOpen = true;
                return;
            }

            string? activeId = _settings.ActiveAiModelId;
            var profile = string.IsNullOrEmpty(activeId)
                ? null
                : _settings.AiModels.FirstOrDefault(m => string.Equals(m.Id, activeId, StringComparison.Ordinal));

            if (profile == null)
            {
                TestResultInfoBar.Severity = InfoBarSeverity.Warning;
                TestResultInfoBar.Message = Loc.GetString("AiSong/TestNoModel");
                TestResultInfoBar.IsOpen = true;
                return;
            }

            _testCts?.Cancel();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            TestRunButton.IsEnabled = false;
            TestResultInfoBar.Severity = InfoBarSeverity.Informational;
            TestResultInfoBar.Message = Loc.GetString("AiSong/TestRunning");
            TestResultInfoBar.IsOpen = true;

            try
            {
                string sourceName = TestSourceNameBox.Text?.Trim() ?? "Test";
                double duration = double.IsNaN(TestDurationBox.Value) ? 0 : TestDurationBox.Value;

                string systemPrompt = AiSongPromptBuilder.BuildSystemPrompt();
                string userMessage = AiSongPromptBuilder.BuildUserMessage(
                    testTitle, testArtist, duration,
                    string.IsNullOrEmpty(sourceName) ? "Test" : sourceName,
                    _settings.AiPreferredLanguage,
                    _settings.AiTargetMarket,
                    _settings.AiPreferNativePrompt);

                var result = await AiSongResolverService.TestModelAsync(
                    profile, testTitle, testArtist, _testCts.Token);

                TestResultInfoBar.Severity = InfoBarSeverity.Success;
                TestResultInfoBar.Message = string.Format(
                    Loc.GetString("AiSong/TestSuccess"),
                    result?.Title ?? "?", result?.Artist ?? "?");
            }
            catch (OperationCanceledException)
            {
                TestResultInfoBar.Severity = InfoBarSeverity.Warning;
                TestResultInfoBar.Message = Loc.GetString("AiSong/TestTimeout");
            }
            catch (Exception ex)
            {
                TestResultInfoBar.Severity = InfoBarSeverity.Error;
                TestResultInfoBar.Message = string.Format(Loc.GetString("AiSong/TestFailed"), ex.Message);
            }
            finally
            {
                TestRunButton.IsEnabled = true;
            }
        }
    }
}
