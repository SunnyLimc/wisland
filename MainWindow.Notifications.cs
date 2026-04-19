using System;
using System.Threading;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;
using wisland.Services.Media.Presentation;
namespace wisland
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// Show an expanded notification for the specified duration.
        /// </summary>
        public void ShowNotification(string title, string message, int durationMs = IslandConfig.DefaultNotificationDurationMs, string? header = null)
        {
            header ??= Loc.GetString("Media/Notification");
            Logger.Info($"Notification shown: '{title}' ({durationMs}ms)");
            _ = ShowNotificationAsync(title, message, header, durationMs);
        }

        private async Task ShowNotificationAsync(string title, string message, string header, int durationMs)
        {
            var previousCts = _notificationCts;
            var notificationCts = new CancellationTokenSource();
            _notificationCts = notificationCts;

            previousCts?.Cancel();
            previousCts?.Dispose();

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
                    ImmersiveContent.ShowNotification(title, message, header);
                    _controller.IsForcedExpanded = true;
                    _presentationMachine?.Dispatch(new NotificationBeginEvent(new NotificationPayload(title, message, header, durationMs)));
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
            _controller.IsForcedExpanded = false;
            // Dispatch the overlay end and let the machine's Resume frame
            // drive the media-view refresh via OnFrameProduced. Calling
            // SyncMediaUI() synchronously here used the still-stale
            // _latestFrame (pre-Resume, possibly carrying the pre-overlay
            // track when a skip landed during the notification), which
            // produced a one-tick flicker of old metadata before the Slide
            // or Crossfade animation from the Resume frame arrived.
            // HandleNotificationEnd always emits a ResumeAfterNotification
            // frame, so OnFrameProduced will refresh the views.
            _presentationMachine?.Dispatch(new NotificationEndEvent());
            UpdateState();
        }
    }
}
