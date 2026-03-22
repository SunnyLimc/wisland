using System;
using System.Threading.Tasks;
using wisland.Helpers;
using Windows.Media.Control;

namespace wisland.Services
{
    /// <summary>
    /// Encapsulates Windows GSMTC (Global System Media Transport Controls) API.
    /// Manages session lifecycle, exposes media properties and playback control.
    /// </summary>
    public sealed class MediaService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private bool _isDisposed;

        /// <summary>Current track title, or "No Media" if no session.</summary>
        public string CurrentTitle { get; private set; } = "No Media";

        /// <summary>Current track artist, or "Waiting for music..." if no session.</summary>
        public string CurrentArtist { get; private set; } = "Waiting for music...";

        /// <summary>Header status text (e.g. "Now Playing", "Wisland").</summary>
        public string HeaderStatus { get; private set; } = "Wisland";

        /// <summary>Whether the current session is actively playing.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>Current media progress from 0.0 to 1.0.</summary>
        public double Progress { get; private set; }
        public bool HasMediaSource => _currentSession != null;
        public bool ShouldAnimateProgress => IsPlaying && _durationSeconds > 0;

        private double _currentPositionSeconds = 0;
        private double _durationSeconds = 0;

        /// <summary>Fired when media properties (title, artist, playback state) change.</summary>
        public event Action? MediaChanged;

        /// <summary>Fired specifically when a new track starts (for notification trigger).</summary>
        public event Action<string, string>? TrackChanged;

        /// <summary>
        /// Requests a progress-bar reset before either hiding (`true`) or showing the next media source (`false`).
        /// </summary>
        public event Action<bool>? ProgressTransitionRequested;

        /// <summary>
        /// Initialize the media manager and subscribe to session changes.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                if (_isDisposed)
                    return;

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

        /// <summary>
        /// Smoothly advance progress locally based on time delta.
        /// Call this every frame from the UI loop.
        /// </summary>
        public void Tick(double dt)
        {
            if (ShouldAnimateProgress)
            {
                _currentPositionSeconds = Math.Min(_durationSeconds, _currentPositionSeconds + dt);
                Progress = _currentPositionSeconds / _durationSeconds;
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
                _ = UpdateUIFromSessionAsync(_currentSession);
            else
                SetNoMedia();
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
            => UpdateCurrentSession();

        private void UpdateCurrentSession()
        {
            if (_isDisposed)
                return;

            var previousSession = _currentSession;
            var newSession = _manager?.GetCurrentSession();

            _currentSession = newSession;

            if (!ReferenceEquals(previousSession, newSession))
            {
                DetachSession(previousSession);

                if (previousSession != null)
                {
                    ProgressTransitionRequested?.Invoke(newSession == null);
                }
            }

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                _ = UpdateUIFromSessionAsync(_currentSession);
                UpdatePlaybackState(_currentSession);
                UpdateTimelineState(_currentSession);
            }
            else
            {
                SetNoMedia();
            }
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
            => _ = UpdateUIFromSessionAsync(sender);

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
            => UpdatePlaybackState(sender);

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
            => UpdateTimelineState(sender);

        private async Task UpdateUIFromSessionAsync(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (_isDisposed || !ReferenceEquals(session, _currentSession) || props == null)
                {
                    return;
                }

                var newTitle = string.IsNullOrEmpty(props.Title) ? "Unknown Track" : props.Title;
                var newArtist = string.IsNullOrEmpty(props.Artist) ? "Unknown Artist" : props.Artist;
                const string nowPlayingHeader = "Now Playing";
                string previousTitle = CurrentTitle;
                var titleChanged = newTitle != CurrentTitle;
                var artistChanged = newArtist != CurrentArtist;
                var headerChanged = HeaderStatus != nowPlayingHeader;

                if (!titleChanged && !artistChanged && !headerChanged)
                {
                    return;
                }

                CurrentTitle = newTitle;
                CurrentArtist = newArtist;
                HeaderStatus = nowPlayingHeader;

                if (titleChanged && !string.Equals(previousTitle, "No Media", StringComparison.Ordinal))
                {
                    ProgressTransitionRequested?.Invoke(false);
                }

                MediaChanged?.Invoke();

                if (titleChanged)
                    TrackChanged?.Invoke(newTitle, newArtist);
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
                if (_isDisposed || !ReferenceEquals(session, _currentSession))
                    return;

                var info = session.GetPlaybackInfo();
                bool isPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (IsPlaying == isPlaying)
                {
                    return;
                }

                IsPlaying = isPlaying;
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
                if (_isDisposed || !ReferenceEquals(session, _currentSession))
                    return;

                var timeline = session.GetTimelineProperties();
                if (timeline != null && timeline.EndTime.TotalSeconds > 0)
                {
                    _currentPositionSeconds = timeline.Position.TotalSeconds;
                    _durationSeconds = timeline.EndTime.TotalSeconds;
                    Progress = _currentPositionSeconds / _durationSeconds;
                }
                else
                {
                    _currentPositionSeconds = 0;
                    _durationSeconds = 0;
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
            if (CurrentTitle == "No Media"
                && CurrentArtist == "Waiting for music..."
                && HeaderStatus == "Wisland"
                && !IsPlaying
                && _currentPositionSeconds == 0
                && _durationSeconds == 0
                && Progress == 0)
            {
                return;
            }

            CurrentTitle = "No Media";
            CurrentArtist = "Waiting for music...";
            HeaderStatus = "Wisland";
            IsPlaying = false;
            _currentPositionSeconds = 0;
            _durationSeconds = 0;
            Progress = 0;
            MediaChanged?.Invoke();
        }

        private void DetachCurrentSession()
            => DetachSession(_currentSession);

        private void DetachSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (session == null)
                return;

            TryDetachHandler(
                () => session.MediaPropertiesChanged -= OnMediaPropertiesChanged,
                "MediaPropertiesChanged");
            TryDetachHandler(
                () => session.PlaybackInfoChanged -= OnPlaybackInfoChanged,
                "PlaybackInfoChanged");
            TryDetachHandler(
                () => session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged,
                "TimelinePropertiesChanged");
        }

        private static void TryDetachHandler(Action detach, string eventName)
        {
            try
            {
                detach();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ignoring media session detach failure for {eventName}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }

            DetachCurrentSession();
            _currentSession = null;
            _manager = null;
        }
    }
}
