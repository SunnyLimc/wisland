using System;
using System.Threading;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;
namespace wisland
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// Show an expanded notification for the specified duration.
        /// </summary>
        public void ShowNotification(string title, string message, int durationMs = IslandConfig.DefaultNotificationDurationMs, string? header = null)
        {
            header ??= Loc.GetString("Media/Notification");            Logger.Info($"Notification shown: '{title}' ({durationMs}ms)");
            _ = ShowNotificationAsync(title, message, header, durationMs);
        }

        private async Task ShowNotificationAsync(string title, string message, string header, int durationMs)
        {
            var previousCts = _notificationCts;
            var notificationCts = new CancellationTokenSource();
            _notificationCts = notificationCts;

            previousCts?.Cancel();

            try
            {
                if (_isClosed)
                {
                    return;
                }

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isClosed)
                    {
                        return;
                    }

                    HideSessionPickerOverlay(reconcileHover: false);
                    ExpandedContent.ShowNotification(title, message, header);
                    _controller.IsNotifying = true;
                    UpdateState();
                });

                await Task.Delay(Math.Max(0, durationMs), notificationCts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Debug($"Notification cancelled or replaced: '{title}'");
                // A newer notification replaced this one or the window is closing.
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ShowNotification failed");
            }
            finally
            {
                if (ReferenceEquals(_notificationCts, notificationCts))
                {
                    _notificationCts = null;

                    if (!_isClosed)
                    {
                        this.DispatcherQueue.TryEnqueue(ClearNotificationState);
                    }
                }

                notificationCts.Dispose();
            }
        }

        public void SetTaskProgress(double progress)
        {
            _taskProgress = Math.Clamp(progress, 0.0, 1.0);
            UpdateRenderLoopState();
        }

        public void ClearTaskProgress()
        {
            _taskProgress = null;
            UpdateRenderLoopState();
        }

        private void ClearNotificationState()
        {
            _controller.IsNotifying = false;
            UpdateState();
            SyncMediaUI();
        }
    }
}
