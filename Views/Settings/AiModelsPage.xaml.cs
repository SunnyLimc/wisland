using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
            Loaded += OnLoaded;
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
                    Summary = $"{AiModelProviderNames.GetDisplayName(m.Provider)} · {m.ModelId}"
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
        }

        private void ModelList_ItemClick(object sender, ItemClickEventArgs e) { }

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
                ModelIdBox.Text = model.ModelId;
                SetReasoningEffortCombo(model.ReasoningEffort);
                GroundingToggle.IsOn = model.GoogleGroundingEnabled;
                SyncReasoningEffortVisibility();
                FormPanel.Visibility = Visibility.Visible;
            }
        }

        private void DeleteModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
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
            string apiKey = ApiKeyBox.Password;
            string modelId = ModelIdBox.Text.Trim();
            string provider = AiModelProviderNames.Normalize(GetSelectedProviderTag());

            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(endpoint)
                || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(modelId))
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

            string? reasoningEffort = null;
            bool googleGroundingEnabled = false;
            if (provider == nameof(AiModelProvider.GoogleAIStudio))
            {
                reasoningEffort = GetSelectedReasoningEffort();
                if (string.IsNullOrEmpty(reasoningEffort)) reasoningEffort = null;
                googleGroundingEnabled = GroundingToggle.IsOn;
            }

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
                    GoogleGroundingEnabled = googleGroundingEnabled
                });
            }

            _settings.Save();
            FormPanel.Visibility = Visibility.Collapsed;
            FormError.IsOpen = false;
            RefreshList();
            _onAiSettingsChanged();
        }

        private void CancelForm_Click(object sender, RoutedEventArgs e)
        {
            FormPanel.Visibility = Visibility.Collapsed;
            FormError.IsOpen = false;
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

            bool isGoogleAiStudio = AiModelProviderNames.Normalize(GetSelectedProviderTag()) == nameof(AiModelProvider.GoogleAIStudio);
            ReasoningEffortCombo.Visibility = isGoogleAiStudio ? Visibility.Visible : Visibility.Collapsed;
            GroundingToggle.Visibility = isGoogleAiStudio ? Visibility.Visible : Visibility.Collapsed;
            GroundingHintText.Visibility = isGoogleAiStudio ? Visibility.Visible : Visibility.Collapsed;
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
            ModelIdBox.Text = string.Empty;
            ReasoningEffortCombo.SelectedIndex = 0;
            GroundingToggle.IsOn = true;
            FormError.IsOpen = false;
        }

        private static string GetDefaultEndpoint(string provider) => provider switch
        {
            nameof(AiModelProvider.OpenAICompatible) => "https://api.openai.com/v1",
            nameof(AiModelProvider.GoogleAIStudio) => "https://generativelanguage.googleapis.com/v1beta/openai/",
            _ => string.Empty
        };

        public sealed class ModelRowItem
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Summary { get; set; } = string.Empty;
        }
    }
}
