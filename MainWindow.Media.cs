using System;
using System.Threading.Tasks;
using island.Models;

namespace island
{
    public sealed partial class MainWindow
    {
        private async Task InitializeMediaAsync()
        {
            _mediaService.MediaChanged += OnMediaServiceChanged;
            _mediaService.TrackChanged += OnTrackChanged;
            await _mediaService.InitializeAsync();
        }

        private void OnMediaServiceChanged()
        {
            this.DispatcherQueue?.TryEnqueue(SyncMediaUI);
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

        /// <summary>Sync UI elements with current MediaService state.</summary>
        private void SyncMediaUI()
        {
            ExpandedContent.Update(
                _mediaService.CurrentTitle,
                _mediaService.CurrentArtist,
                _mediaService.HeaderStatus,
                _mediaService.IsPlaying);
        }

        private async void PlayPause_Click(object? sender, EventArgs e) => await _mediaService.PlayPauseAsync();
        private async void SkipNext_Click(object? sender, EventArgs e) => await _mediaService.SkipNextAsync();
        private async void SkipPrevious_Click(object? sender, EventArgs e) => await _mediaService.SkipPreviousAsync();
    }
}
