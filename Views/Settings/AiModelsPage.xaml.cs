using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wisland.Helpers;
using wisland.Models;
using wisland.Services;

namespace wisland.Views.Settings
{
    public sealed partial class AiModelsPage : UserControl
    {
        private readonly SettingsService _settings;
        private readonly Action _onAiSettingsChanged;
        private readonly ObservableCollection<ModelRowItem> _rows = new();
        private string? _editingModelId;

        public AiModelsPage(SettingsService settings, Action onAiSettingsChanged)
        {
            _settings = settings;
            _onAiSettingsChanged = onAiSettingsChanged;
            this.InitializeComponent();
            ModelList.ItemsSource = _rows;
            TemperatureSlider.ValueChanged += (_, _) => UpdateTemperatureText();
            UpdateTemperatureText();
            Loaded += OnLoaded;
        }

        public void Cleanup()
        {
            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = null;
        }

        private void UpdateTemperatureText()
        {
            TemperatureValueText.Text = TemperatureSlider.Value.ToString("F1");
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => RefreshList();

        private void RefreshList()
        {
            _rows.Clear();
            foreach (var m in _settings.AiModels)
            {
                _rows.Add(new ModelRowItem
                {
                    Id = m.Id,
                    DisplayName = m.DisplayName,
                    Summary = $"{AiModelProviderNames.GetDisplayName(m.Provider)} · {m.ModelId}",
                    IsActive = string.Equals(m.Id, _settings.ActiveAiModelId, StringComparison.Ordinal)
                });
            }
        }

        private void AddModel_Click(object sender, RoutedEventArgs e)
        {
            _editingModelId = null;
            FormTitle.Text = Loc.GetString("AiModels/AddModel");
            ClearForm();
            ProviderCombo.SelectedIndex = 0;
            EndpointBox.Text = GetDefaultEndpoint(GetSelectedProviderTag());
            FormPanel.Visibility = Visibility.Visible;
            SetListInteractivity(false);
        }

        private void EditModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var model = _settings.AiModels.FirstOrDefault(m => m.Id == id);
                if (model == null) return;

                _editingModelId = id;
                FormTitle.Text = Loc.GetString("AiModels/EditModel");
                SetProviderComboByTag(model.Provider);
                DisplayNameBox.Text = model.DisplayName;
                EndpointBox.Text = model.Endpoint;
                ApiKeyBox.Password = model.ApiKey;
                ApiKeyBoxGoogle.Password = model.ApiKey;
                ModelIdBox.Text = model.ModelId;
                SetReasoningEffortCombo(model.ReasoningEffort);
                GroundingToggle.IsOn = model.GoogleGroundingEnabled;
                TemperatureSlider.Value = model.Temperature;
                SyncReasoningEffortVisibility();
                FormPanel.Visibility = Visibility.Visible;
                SetListInteractivity(false);
            }
        }

        private async void DeleteModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var model = _settings.AiModels.FirstOrDefault(m => m.Id == id);
                if (model == null) return;

                var dialog = new ContentDialog
                {
                    Title = Loc.GetString("AiModels/DeleteConfirmTitle"),
                    Content = string.Format(Loc.GetString("AiModels/DeleteConfirmMessage"), model.DisplayName),
                    PrimaryButtonText = Loc.GetString("AiModels/DeleteConfirmYes"),
                    CloseButtonText = Loc.GetString("AiModels/DeleteConfirmNo"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                _settings.AiModels.RemoveAll(m => m.Id == id);
                if (string.Equals(_settings.ActiveAiModelId, id, StringComparison.Ordinal))
                {
                    _settings.ActiveAiModelId = null;
                }
                _settings.Save();
                RefreshList();
                _onAiSettingsChanged();
            }
        }

        private void SaveModel_Click(object sender, RoutedEventArgs e)
        {
            string displayName = DisplayNameBox.Text.Trim();
            string endpoint = EndpointBox.Text.Trim();
            string modelId = ModelIdBox.Text.Trim();
            string provider = AiModelProviderNames.Normalize(GetSelectedProviderTag());
            bool isGoogle = provider == nameof(AiModelProvider.GoogleAIStudio);
            string apiKey = isGoogle ? ApiKeyBoxGoogle.Password : ApiKeyBox.Password;

            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(modelId))
            {
                FormError.Message = Loc.GetString("AiModels/ValidationAllRequired");
                FormError.IsOpen = true;
                return;
            }

            if (!isGoogle)
            {
                if (string.IsNullOrEmpty(endpoint))
                {
                    FormError.Message = Loc.GetString("AiModels/ValidationAllRequired");
                    FormError.IsOpen = true;
                    return;
                }
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
                {
                    FormError.Message = Loc.GetString("AiModels/ValidationInvalidUrl");
                    FormError.IsOpen = true;
                    return;
                }
            }

            string? reasoningEffort = null;
            bool googleGroundingEnabled = false;
            if (isGoogle)
            {
                reasoningEffort = GetSelectedReasoningEffort();
                if (string.IsNullOrEmpty(reasoningEffort)) reasoningEffort = null;
                googleGroundingEnabled = GroundingToggle.IsOn;
            }

            double temperature = TemperatureSlider.Value;

            if (_editingModelId != null)
            {
                var existing = _settings.AiModels.FirstOrDefault(m => m.Id == _editingModelId);
                if (existing != null)
                {
                    existing.DisplayName = displayName;
                    existing.Provider = provider;
                    existing.Endpoint = endpoint;
                    existing.ApiKey = apiKey;
                    existing.ModelId = modelId;
                    existing.ReasoningEffort = reasoningEffort;
                    existing.GoogleGroundingEnabled = googleGroundingEnabled;
                    existing.Temperature = temperature;
                }
            }
            else
            {
                _settings.AiModels.Add(new AiModelProfile
                {
                    Id = Guid.NewGuid().ToString(),
                    DisplayName = displayName,
                    Provider = provider,
                    Endpoint = endpoint,
                    ApiKey = apiKey,
                    ModelId = modelId,
                    ReasoningEffort = reasoningEffort,
                    GoogleGroundingEnabled = googleGroundingEnabled,
                    Temperature = temperature
                });
            }

            _settings.Save();
            FormPanel.Visibility = Visibility.Collapsed;
            FormError.IsOpen = false;
            SetListInteractivity(true);
            RefreshList();
            _onAiSettingsChanged();
        }

        private void CancelForm_Click(object sender, RoutedEventArgs e)
        {
            FormPanel.Visibility = Visibility.Collapsed;
            FormError.IsOpen = false;
            TestResultBar.IsOpen = false;
            SetListInteractivity(true);
        }

        private CancellationTokenSource? _testCts;

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            string provider = AiModelProviderNames.Normalize(GetSelectedProviderTag());
            bool isGoogle = provider == nameof(AiModelProvider.GoogleAIStudio);
            string apiKey = isGoogle ? ApiKeyBoxGoogle.Password : ApiKeyBox.Password;
            string modelId = ModelIdBox.Text.Trim();

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(modelId))
            {
                TestResultBar.Severity = InfoBarSeverity.Warning;
                TestResultBar.Message = Loc.GetString("AiModels/TestMissingFields");
                TestResultBar.IsOpen = true;
                return;
            }

            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            TestConnectionButton.IsEnabled = false;
            TestResultBar.Severity = InfoBarSeverity.Informational;
            TestResultBar.Message = Loc.GetString("AiModels/TestRunning");
            TestResultBar.IsOpen = true;

            try
            {
                var profile = new AiModelProfile
                {
                    Provider = provider,
                    Endpoint = EndpointBox.Text.Trim(),
                    ApiKey = apiKey,
                    ModelId = modelId,
                    ReasoningEffort = GetSelectedReasoningEffort(),
                    GoogleGroundingEnabled = isGoogle && GroundingToggle.IsOn,
                    Temperature = TemperatureSlider.Value
                };

                var result = await AiSongResolverService.TestModelAsync(
                    profile,
                    Loc.GetString("AiModels/TestSongTitle"),
                    Loc.GetString("AiModels/TestSongArtist"),
                    0, "Test", null, null, false,
                    _testCts.Token);

                string groundingTag = result?.GroundingUsed switch
                {
                    true => " [Grounding: ✓]",
                    false => " [Grounding: ✗]",
                    _ => ""
                };

                string main = string.Format(
                    Loc.GetString("AiModels/TestSuccess"),
                    result?.Title ?? "?", result?.Artist ?? "?") + groundingTag;
                string alts = result?.FormatAlternatives() ?? "";

                TestResultBar.Severity = InfoBarSeverity.Success;
                TestResultBar.Message = string.IsNullOrEmpty(alts) ? main : main + "\n" + alts;
            }
            catch (OperationCanceledException)
            {
                TestResultBar.Severity = InfoBarSeverity.Warning;
                TestResultBar.Message = Loc.GetString("AiModels/TestTimeout");
            }
            catch (Exception ex)
            {
                TestResultBar.Severity = InfoBarSeverity.Error;
                TestResultBar.Message = string.Format(Loc.GetString("AiModels/TestFailed"), ex.Message);
            }
            finally
            {
                TestConnectionButton.IsEnabled = true;
            }
        }

        private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncReasoningEffortVisibility();
            if (_editingModelId == null)
            {
                EndpointBox.Text = GetDefaultEndpoint(GetSelectedProviderTag());
            }
        }

        private void SyncReasoningEffortVisibility()
        {
            if (ReasoningEffortCombo == null || GroundingToggle == null || GroundingHintText == null) return;

            bool isGoogle = AiModelProviderNames.Normalize(GetSelectedProviderTag()) == nameof(AiModelProvider.GoogleAIStudio);
            EndpointBox.Visibility = isGoogle ? Visibility.Collapsed : Visibility.Visible;
            ApiKeyBox.Visibility = isGoogle ? Visibility.Collapsed : Visibility.Visible;
            ApiKeyBoxGoogle.Visibility = isGoogle ? Visibility.Visible : Visibility.Collapsed;
            ReasoningEffortCombo.Visibility = isGoogle ? Visibility.Visible : Visibility.Collapsed;
            GroundingToggle.Visibility = isGoogle ? Visibility.Visible : Visibility.Collapsed;
            GroundingHintText.Visibility = isGoogle ? Visibility.Visible : Visibility.Collapsed;
        }

        private string GetSelectedProviderTag()
        {
            return ProviderCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag
                ? tag
                : nameof(AiModelProvider.OpenAICompatible);
        }

        private string? GetSelectedReasoningEffort()
        {
            return ReasoningEffortCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag
                ? tag : null;
        }

        private void SetProviderComboByTag(string provider)
        {
            string normalizedProvider = AiModelProviderNames.Normalize(provider);
            for (int i = 0; i < ProviderCombo.Items.Count; i++)
            {
                if (ProviderCombo.Items[i] is ComboBoxItem ci
                    && ci.Tag is string tag
                    && string.Equals(tag, normalizedProvider, StringComparison.Ordinal))
                {
                    ProviderCombo.SelectedIndex = i;
                    return;
                }
            }
            ProviderCombo.SelectedIndex = 0;
        }

        private void SetReasoningEffortCombo(string? value)
        {
            for (int i = 0; i < ReasoningEffortCombo.Items.Count; i++)
            {
                if (ReasoningEffortCombo.Items[i] is ComboBoxItem ci && ci.Tag is string tag
                    && string.Equals(tag, value ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    ReasoningEffortCombo.SelectedIndex = i;
                    return;
                }
            }
            ReasoningEffortCombo.SelectedIndex = 0;
        }

        private void ClearForm()
        {
            DisplayNameBox.Text = string.Empty;
            EndpointBox.Text = string.Empty;
            ApiKeyBox.Password = string.Empty;
            ApiKeyBoxGoogle.Password = string.Empty;
            ModelIdBox.Text = string.Empty;
            ReasoningEffortCombo.SelectedIndex = 0;
            GroundingToggle.IsOn = true;
            TemperatureSlider.Value = 1.0;
            FormError.IsOpen = false;
            TestResultBar.IsOpen = false;
        }

        private void SetListInteractivity(bool enabled)
        {
            ModelList.IsEnabled = enabled;
            AddModelButton.IsEnabled = enabled;
        }

        private static string GetDefaultEndpoint(string provider) => provider switch
        {
            nameof(AiModelProvider.OpenAICompatible) => "https://api.openai.com/v1",
            _ => string.Empty
        };

        public sealed class ModelRowItem
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Summary { get; set; } = string.Empty;
            public bool IsActive { get; set; }
            public Visibility ActiveBadgeVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
