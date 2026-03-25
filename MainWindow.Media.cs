using System;
using System.Threading.Tasks;
using wisland.Models;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private TrackSwitchDirection _pendingTrackSwitchDirection = TrackSwitchDirection.None;
        private long _pendingTrackSwitchTimestamp;

        private async Task InitializeMediaAsync()
        {
            _mediaService.MediaChanged += OnMediaServiceChanged;
            _mediaService.TrackChanged += OnTrackChanged;
            _mediaService.ProgressTransitionRequested += OnMediaProgressTransitionRequested;
            await _mediaService.InitializeAsync();
        }

        private void OnMediaServiceChanged()
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                SyncMediaUI();
                UpdateRenderLoopState();
            });
        }

        private void OnTrackChanged(string title, string artist)
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                if (_controller.IsDocked && !_controller.IsHovered && !_controller.IsDragging)
                {
                    ShowNotification(title, artist, IslandConfig.TrackChangeNotificationDurationMs, "New Track");
                }
            });
        }

        private void OnMediaProgressTransitionRequested(bool hideAfterReset)
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isClosed || _taskProgress.HasValue)
                {
                    return;
                }

                if (!IslandProgressBar.IsEffectVisible)
                {
                    _isMediaProgressResetPending = false;
                    _hideMediaProgressWhenResetCompletes = false;
                    UpdateRenderLoopState();
                    return;
                }

                _isMediaProgressResetPending = true;
                _hideMediaProgressWhenResetCompletes = hideAfterReset;
                UpdateRenderLoopState();
            });
        }

        /// <summary>Sync UI elements with current MediaService state.</summary>
        private void SyncMediaUI()
        {
            TrackSwitchDirection directionHint = GetTrackSwitchDirectionHint();
            bool metadataChanged = ExpandedContent.Update(
                _mediaService.CurrentTitle,
                _mediaService.CurrentArtist,
                _mediaService.HeaderStatus,
                _mediaService.IsPlaying,
                directionHint);

            if (metadataChanged)
            {
                ClearTrackSwitchDirectionHint();
            }
        }

        private async void PlayPause_Click(object? sender, EventArgs e) => await _mediaService.PlayPauseAsync();
        private async void SkipNext_Click(object? sender, EventArgs e)
        {
            RegisterTrackSwitchDirectionHint(TrackSwitchDirection.Next);
            await _mediaService.SkipNextAsync();
        }

        private async void SkipPrevious_Click(object? sender, EventArgs e)
        {
            RegisterTrackSwitchDirectionHint(TrackSwitchDirection.Previous);
            await _mediaService.SkipPreviousAsync();
        }

        private void RegisterTrackSwitchDirectionHint(TrackSwitchDirection direction)
        {
            _pendingTrackSwitchDirection = direction;
            _pendingTrackSwitchTimestamp = Environment.TickCount64;
        }

        private TrackSwitchDirection GetTrackSwitchDirectionHint()
        {
            if (_pendingTrackSwitchDirection == TrackSwitchDirection.None)
            {
                return TrackSwitchDirection.None;
            }

            long elapsed = Environment.TickCount64 - _pendingTrackSwitchTimestamp;
            if (elapsed > IslandConfig.TrackSwitchIntentWindowMs)
            {
                ClearTrackSwitchDirectionHint();
                return TrackSwitchDirection.None;
            }

            return _pendingTrackSwitchDirection;
        }

        private void ClearTrackSwitchDirectionHint()
        {
            _pendingTrackSwitchDirection = TrackSwitchDirection.None;
            _pendingTrackSwitchTimestamp = 0;
        }
    }
}
