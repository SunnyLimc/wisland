using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wisland.Helpers;
using wisland.Models;
using wisland.Services;

namespace wisland.Views.Settings
{
    public sealed partial class AppearancePage : UserControl
    {
        private readonly SettingsService _settings;
        private readonly Action<BackdropType> _onBackdropChanged;
        private bool _suppressSelectionChanged;

        public AppearancePage(SettingsService settings, Action<BackdropType> onBackdropChanged)
        {
            _settings = settings;
            _onBackdropChanged = onBackdropChanged;
            this.InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _suppressSelectionChanged = true;
            int index = _settings.BackdropType switch
            {
                BackdropType.Mica => 0,
                BackdropType.Acrylic => 1,
                BackdropType.None => 2,
                _ => 0
            };
            BackdropSelector.SelectedIndex = index;

            string? lang = _settings.Language;
            int langIndex = 0;
            for (int i = 0; i < LanguageSelector.Items.Count; i++)
            {
                if (LanguageSelector.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, lang ?? "", StringComparison.Ordinal))
                {
                    langIndex = i;
                    break;
                }
            }
            LanguageSelector.SelectedIndex = langIndex;

            if (!Loc.CanOverrideLanguage)
            {
                LanguageSelector.SelectedIndex = 0;
                LanguageSelector.IsEnabled = false;
                UnpackagedLanguageHint.Message = Loc.GetString("LanguageUnpackagedHint");
                UnpackagedLanguageHint.IsOpen = true;
            }

            _suppressSelectionChanged = false;
        }

        private void BackdropSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (BackdropSelector.SelectedItem is RadioButton rb && rb.Tag is string tag)
            {
                var type = tag switch
                {
                    "Acrylic" => BackdropType.Acrylic,
                    "None" => BackdropType.None,
                    _ => BackdropType.Mica
                };
                _onBackdropChanged(type);
            }
        }

        private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (LanguageSelector.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _settings.Language = string.IsNullOrEmpty(tag) ? null : tag;
                _settings.Save();
                RestartHint.IsOpen = true;
            }
        }
    }
}
