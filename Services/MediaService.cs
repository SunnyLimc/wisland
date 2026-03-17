using System;
using System.Threading.Tasks;
using island.Helpers;
using Windows.Media.Control;

namespace island.Services
{
    /// <summary>
    /// Encapsulates Windows GSMTC (Global System Media Transport Controls) API.
    /// Manages session lifecycle, exposes media properties and playback control.
    /// </summary>
    public sealed class MediaService
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        /// <summary>Current track title, or "No Media" if no session.</summary>
        public string CurrentTitle { get; private set; } = "No Media";

        /// <summary>Current track artist, or "Waiting for music..." if no session.</summary>
        public string CurrentArtist { get; private set; } = "Waiting for music...";

        /// <summary>Header status text (e.g. "Now Playing", "Dynamic Island").</summary>
        public string HeaderStatus { get; private set; } = "Dynamic Island";

        /// <summary>Whether the current session is actively playing.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>Current media progress from 0.0 to 1.0.</summary>
        public double Progress { get; private set; }

        /// <summary>Fired when media properties (title, artist, playback state) change.</summary>
        public event Action? MediaChanged;

        /// <summary>Fired specifically when a new track starts (for notification trigger).</summary>
        public event Action<string, string>? TrackChanged;

        /// <summary>
        /// Initialize the media manager and subscribe to session changes.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                    UpdateCurrentSession();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize media manager");
            }
        }

        /// <summary>Toggle play/pause on the current media session.</summary>
        public async Task PlayPauseAsync()
        {
            try
            {
                if (_currentSession != null)
                    await _currentSession.TryTogglePlayPauseAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayPause failed");
            }
        }

        /// <summary>Skip to the next track.</summary>
        public async Task SkipNextAsync()
        {
            try
            {
                if (_currentSession != null)
                    await _currentSession.TrySkipNextAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SkipNext failed");
            }
        }

        /// <summary>Skip to the previous track.</summary>
        public async Task SkipPreviousAsync()
        {
            try
            {
                if (_currentSession != null)
                    await _currentSession.TrySkipPreviousAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SkipPrevious failed");
            }
        }

        /// <summary>
        /// Refresh media properties from the current session.
        /// Call this after notification dismissal to restore media info.
        /// </summary>
        public void RefreshFromCurrentSession()
        {
            if (_currentSession != null)
                UpdateUIFromSession(_currentSession);
            else
                SetNoMedia();
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
            => UpdateCurrentSession();

        private void UpdateCurrentSession()
        {
            var newSession = _manager?.GetCurrentSession();

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }

            _currentSession = newSession;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                UpdateUIFromSession(_currentSession);
                UpdatePlaybackState(_currentSession);
                UpdateTimelineState(_currentSession);
            }
            else
            {
                SetNoMedia();
            }
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
            => UpdateUIFromSession(sender);

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
            => UpdatePlaybackState(sender);

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
            => UpdateTimelineState(sender);

        private async void UpdateUIFromSession(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    var newTitle = string.IsNullOrEmpty(props.Title) ? "Unknown Track" : props.Title;
                    var newArtist = string.IsNullOrEmpty(props.Artist) ? "Unknown Artist" : props.Artist;
                    var titleChanged = newTitle != CurrentTitle;

                    CurrentTitle = newTitle;
                    CurrentArtist = newArtist;
                    HeaderStatus = "Now Playing";

                    MediaChanged?.Invoke();

                    if (titleChanged)
                        TrackChanged?.Invoke(newTitle, newArtist);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to update media properties");
            }
        }

        private void UpdatePlaybackState(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var info = session.GetPlaybackInfo();
                IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                MediaChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to update playback state");
            }
        }

        private void UpdateTimelineState(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null && timeline.EndTime.TotalSeconds > 0)
                {
                    Progress = timeline.Position.TotalSeconds / timeline.EndTime.TotalSeconds;
                }
                else
                {
                    Progress = 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to update timeline state");
            }
        }

        private void SetNoMedia()
        {
            CurrentTitle = "No Media";
            CurrentArtist = "Waiting for music...";
            HeaderStatus = "Dynamic Island";
            IsPlaying = false;
            Progress = 0;
            MediaChanged?.Invoke();
        }
    }
}
