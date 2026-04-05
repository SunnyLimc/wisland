using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wisland.Helpers;
using wisland.Services;

using LogLevel = wisland.Helpers.LogLevel;

namespace wisland.Views.Settings
{
    public sealed partial class DiagnosticsPage : UserControl
    {
        private readonly SettingsService _settings;
        private bool _suppressSelectionChanged;

        public DiagnosticsPage(SettingsService settings)
        {
            _settings = settings;
            this.InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _suppressSelectionChanged = true;
            LogLevelSelector.SelectedIndex = Logger.MinimumLevel switch
            {
                LogLevel.Trace => 0,
                LogLevel.Debug => 1,
                LogLevel.Info => 2,
                LogLevel.Warn => 3,
                LogLevel.Error => 4,
                _ => 2
            };
            _suppressSelectionChanged = false;
        }

        private void LogLevelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (LogLevelSelector.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                if (Enum.TryParse<LogLevel>(tag, ignoreCase: true, out var level))
                {
                    Logger.SetMinimumLevel(level);
                    _settings.LogLevel = level;
                    _settings.Save();
                }
            }
        }

        private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wisland", "logs");
            Directory.CreateDirectory(logDir);

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Best-effort
            }
        }
    }
}
