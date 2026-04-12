using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly Action<double> _setTaskProgress;
        private readonly Action _clearTaskProgress;
        private bool _suppressSelectionChanged;
        private CancellationTokenSource? _progressTestCts;

        public DiagnosticsPage(
            SettingsService settings,
            Action<double> setTaskProgress,
            Action clearTaskProgress)
        {
            _settings = settings;
            _setTaskProgress = setTaskProgress;
            _clearTaskProgress = clearTaskProgress;
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
                    Logger.Info($"Log level changed to {level}");
                    ShowThenHide(LogLevelStatusText,
                        string.Format(Loc.GetString("LogLevelChanged"), level));
                }
            }
        }

        private async void DumpIconsButton_Click(object sender, RoutedEventArgs e)
        {
            DumpIconsButton.IsEnabled = false;
            try
            {
                ShowDumpStatus(Loc.GetString("DumpIconsTriggered"));
                string dumpDir = await MediaSourceIconResolver.DumpAllSessionIconsAsync();
                ShowDumpStatus(Loc.GetString("DumpIconsDone"));
            }
            catch (Exception ex)
            {
                ShowDumpStatus($"Error: {ex.Message}");
                Logger.Warn($"Icon dump failed: {ex.Message}");
            }
            finally
            {
                DumpIconsButton.IsEnabled = true;
            }
        }

        private async void DumpGsmtcButton_Click(object sender, RoutedEventArgs e)
        {
            DumpGsmtcButton.IsEnabled = false;
            try
            {
                string projectRoot = Path.GetFullPath(
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
                string toolPath = Path.Combine(projectRoot, "Tools", "GsmtcDebugDump.cs");

                if (!File.Exists(toolPath))
                {
                    ShowDumpStatus($"Tool not found: {toolPath}");
                    return;
                }

                string gsmtcDir = Helpers.SafePaths.Combine("logs", "gsmtc-debug");
                Directory.CreateDirectory(gsmtcDir);
                string outputPath = Path.Combine(gsmtcDir,
                    $"gsmtc-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.json");

                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    ArgumentList = { "run", "--file", toolPath, "--", outputPath },
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    ShowDumpStatus("Failed to start dotnet process");
                    return;
                }

                string stdout = await process.StandardOutput.ReadToEndAsync();
                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    ShowDumpStatus(string.IsNullOrWhiteSpace(stdout)
                        ? Loc.GetString("DumpGsmtcDone")
                        : stdout.Trim());
                }
                else
                {
                    ShowDumpStatus($"Exit {process.ExitCode}: {stderr.Trim()}");
                }
            }
            catch (Exception ex)
            {
                ShowDumpStatus($"Error: {ex.Message}");
                Logger.Warn($"GSMTC dump failed: {ex.Message}");
            }
            finally
            {
                DumpGsmtcButton.IsEnabled = true;
            }
        }

        private void OpenDumpFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string logsDir = Helpers.SafePaths.Combine("logs");
            Directory.CreateDirectory(logsDir);
            OpenFolder(logsDir);
        }

        private async void TestProgressButton_Click(object sender, RoutedEventArgs e)
        {
            _progressTestCts?.Cancel();
            var cts = new CancellationTokenSource();
            _progressTestCts = cts;
            TestProgressButton.IsEnabled = false;

            try
            {
                const int durationMs = 5000;
                const int stepMs = 50;
                var sw = Stopwatch.StartNew();

                while (sw.ElapsedMilliseconds < durationMs)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    double progress = (double)sw.ElapsedMilliseconds / durationMs;
                    _setTaskProgress(Math.Clamp(progress, 0.0, 1.0));
                    await Task.Delay(stepMs, cts.Token);
                }

                _setTaskProgress(1.0);
                await Task.Delay(300, cts.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _clearTaskProgress();
                TestProgressButton.IsEnabled = true;
                if (ReferenceEquals(_progressTestCts, cts))
                    _progressTestCts = null;
            }
        }

        private void ShowDumpStatus(string message)
        {
            ShowThenHide(DumpStatusText, message);
        }

        private async void ShowThenHide(TextBlock target, string message, int delayMs = 3000)
        {
            target.Text = message;
            target.Visibility = Visibility.Visible;
            await Task.Delay(delayMs);
            // Only hide if the text hasn't been replaced by another message.
            if (target.Text == message)
                target.Visibility = Visibility.Collapsed;
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to open folder: {ex.Message}");
            }
        }
    }
}
