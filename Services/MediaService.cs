using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;
using Windows.Media.Control;

namespace wisland.Services
{
    /// <summary>
    /// Encapsulates Windows GSMTC (Global System Media Transport Controls) API.
    /// Tracks all available sessions and exposes immutable UI snapshots.
    /// </summary>
    public sealed class MediaService : IDisposable
    {
        private const string UnknownTrackTitle = "Unknown Track";
        private const string UnknownArtistName = "Unknown Artist";

        private readonly object _gate = new();
        private readonly Dictionary<GlobalSystemMediaTransportControlsSession, TrackedSession> _trackedSessionsBySession =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, TrackedSession> _trackedSessionsByKey =
            new(StringComparer.Ordinal);

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private IReadOnlyList<MediaSessionSnapshot> _sessions = Array.Empty<MediaSessionSnapshot>();
        private string? _systemCurrentSessionKey;
        private string? _lastSystemCurrentTrackSignature;
        private int _nextSessionOrdinal = 1;
        private bool _isDisposed;

        public IReadOnlyList<MediaSessionSnapshot> Sessions
        {
            get
            {
                lock (_gate)
                {
                    return _sessions;
                }
            }
        }

        public string? SystemCurrentSessionKey
        {
            get
            {
                lock (_gate)
                {
                    return _systemCurrentSessionKey;
                }
            }
        }

        public bool HasAnySession
        {
            get
            {
                lock (_gate)
                {
                    return _trackedSessionsBySession.Count > 0;
                }
            }
        }

        /// <summary>Raised when the tracked session collection or any session snapshot changes.</summary>
        public event Action? MediaChanged;

        /// <summary>Raised when the tracked session collection or any session snapshot changes.</summary>
        public event Action? SessionsChanged;

        /// <summary>Raised when the system current session switches to a different track.</summary>
        public event Action<string, string>? TrackChanged;

        public async Task InitializeAsync()
        {
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_manager == null)
                {
                    return;
                }

                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                _manager.SessionsChanged += OnSessionsChanged;
                await RefreshSessionsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize media manager");
            }
        }

        /// <summary>
        /// Smoothly advances tracked progress locally for playing sessions.
        /// </summary>
        public void Tick(double dt)
        {
            if (dt <= 0)
            {
                return;
            }

            lock (_gate)
            {
                foreach (TrackedSession tracked in _trackedSessionsByKey.Values)
                {
                    if (tracked.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        || tracked.DurationSeconds <= 0)
                    {
                        continue;
                    }

                    double nextPosition = Math.Min(tracked.DurationSeconds, tracked.CurrentPositionSeconds + dt);
                    if (Math.Abs(nextPosition - tracked.CurrentPositionSeconds) <= 0.0001)
                    {
                        continue;
                    }

                    tracked.CurrentPositionSeconds = nextPosition;
                    tracked.Progress = nextPosition / tracked.DurationSeconds;
                }
            }
        }

        public MediaSessionSnapshot? GetSessionSnapshot(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return null;
            }

            lock (_gate)
            {
                return _trackedSessionsByKey.TryGetValue(sessionKey, out TrackedSession? tracked)
                    ? CreateSnapshot(tracked)
                    : null;
            }
        }

        public bool HasSession(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return false;
            }

            lock (_gate)
            {
                return _trackedSessionsByKey.ContainsKey(sessionKey);
            }
        }

        public bool ShouldAnimateProgress(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return false;
            }

            lock (_gate)
            {
                return _trackedSessionsByKey.TryGetValue(sessionKey, out TrackedSession? tracked)
                    && tracked.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && tracked.DurationSeconds > 0;
            }
        }

        public async Task PlayPauseAsync(string? sessionKey)
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session = GetSession(sessionKey);
                if (session != null)
                {
                    await session.TryTogglePlayPauseAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayPause failed");
            }
        }

        public async Task SkipNextAsync(string? sessionKey)
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session = GetSession(sessionKey);
                if (session != null)
                {
                    await session.TrySkipNextAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SkipNext failed");
            }
        }

        public async Task SkipPreviousAsync(string? sessionKey)
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session = GetSession(sessionKey);
                if (session != null)
                {
                    await session.TrySkipPreviousAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SkipPrevious failed");
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
            => _ = RefreshSessionsAsync();

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
            => _ = RefreshSessionsAsync();

        private async Task RefreshSessionsAsync()
        {
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                GlobalSystemMediaTransportControlsSessionManager? manager = _manager;
                if (manager == null)
                {
                    return;
                }

                IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
                GlobalSystemMediaTransportControlsSession? currentSession = manager.GetCurrentSession();

                List<GlobalSystemMediaTransportControlsSession> sessionsToAttach = new();
                List<GlobalSystemMediaTransportControlsSession> sessionsToDetach = new();
                ServiceChangeResult changeResult = default;
                bool hasChanges = false;

                lock (_gate)
                {
                    if (_isDisposed)
                    {
                        return;
                    }

                    HashSet<GlobalSystemMediaTransportControlsSession> activeSessions =
                        new(sessions, ReferenceEqualityComparer.Instance);

                    foreach (GlobalSystemMediaTransportControlsSession session in sessions)
                    {
                        if (!_trackedSessionsBySession.TryGetValue(session, out TrackedSession? tracked))
                        {
                            tracked = new TrackedSession(
                                session,
                                CreateSessionKey_NoLock(),
                                session.SourceAppUserModelId,
                                ResolveSourceName(session.SourceAppUserModelId));

                            _trackedSessionsBySession.Add(session, tracked);
                            _trackedSessionsByKey.Add(tracked.SessionKey, tracked);
                            sessionsToAttach.Add(session);
                            hasChanges = true;
                        }

                        bool isSystemCurrent = ReferenceEquals(session, currentSession);
                        if (tracked.IsSystemCurrent != isSystemCurrent)
                        {
                            tracked.IsSystemCurrent = isSystemCurrent;
                            hasChanges = true;
                        }
                    }

                    GlobalSystemMediaTransportControlsSession[] removedSessions = _trackedSessionsBySession.Keys
                        .Where(session => !activeSessions.Contains(session))
                        .ToArray();

                    foreach (GlobalSystemMediaTransportControlsSession session in removedSessions)
                    {
                        TrackedSession tracked = _trackedSessionsBySession[session];
                        _trackedSessionsBySession.Remove(session);
                        _trackedSessionsByKey.Remove(tracked.SessionKey);
                        sessionsToDetach.Add(session);
                        hasChanges = true;
                    }

                    string? nextSystemCurrentKey = currentSession != null
                        && _trackedSessionsBySession.TryGetValue(currentSession, out TrackedSession? currentTracked)
                            ? currentTracked.SessionKey
                            : null;

                    if (!string.Equals(_systemCurrentSessionKey, nextSystemCurrentKey, StringComparison.Ordinal))
                    {
                        _systemCurrentSessionKey = nextSystemCurrentKey;
                        hasChanges = true;
                    }

                    if (hasChanges)
                    {
                        changeResult = PrepareStateChange_NoLock();
                    }
                }

                foreach (GlobalSystemMediaTransportControlsSession session in sessionsToDetach)
                {
                    DetachSession(session);
                }

                foreach (GlobalSystemMediaTransportControlsSession session in sessionsToAttach)
                {
                    AttachSession(session);
                    UpdatePlaybackState(session);
                    UpdateTimelineState(session);
                    _ = UpdateMediaPropertiesAsync(session);
                }

                if (hasChanges)
                {
                    DispatchChange(changeResult);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to refresh media sessions");
            }
        }

        private void AttachSession(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ignoring media session attach failure: {ex.Message}");
            }
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
            => _ = UpdateMediaPropertiesAsync(sender);

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
            => UpdatePlaybackState(sender);

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
            => UpdateTimelineState(sender);

        private async Task UpdateMediaPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props == null)
                {
                    return;
                }

                ServiceChangeResult changeResult = default;
                bool hasChanges = false;

                lock (_gate)
                {
                    if (_isDisposed || !_trackedSessionsBySession.TryGetValue(session, out TrackedSession? tracked))
                    {
                        return;
                    }

                    string nextTitle = string.IsNullOrWhiteSpace(props.Title) ? UnknownTrackTitle : props.Title;
                    string nextArtist = string.IsNullOrWhiteSpace(props.Artist) ? UnknownArtistName : props.Artist;

                    if (!string.Equals(tracked.Title, nextTitle, StringComparison.Ordinal)
                        || !string.Equals(tracked.Artist, nextArtist, StringComparison.Ordinal))
                    {
                        tracked.Title = nextTitle;
                        tracked.Artist = nextArtist;
                        tracked.LastActivityUtc = DateTimeOffset.UtcNow;
                        hasChanges = true;
                        changeResult = PrepareStateChange_NoLock();
                    }
                }

                if (hasChanges)
                {
                    DispatchChange(changeResult);
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
                GlobalSystemMediaTransportControlsSessionPlaybackStatus nextStatus =
                    info?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

                ServiceChangeResult changeResult = default;
                bool hasChanges = false;

                lock (_gate)
                {
                    if (_isDisposed || !_trackedSessionsBySession.TryGetValue(session, out TrackedSession? tracked))
                    {
                        return;
                    }

                    if (tracked.PlaybackStatus != nextStatus)
                    {
                        tracked.PlaybackStatus = nextStatus;
                        tracked.LastActivityUtc = DateTimeOffset.UtcNow;
                        hasChanges = true;
                        changeResult = PrepareStateChange_NoLock();
                    }
                }

                if (hasChanges)
                {
                    DispatchChange(changeResult);
                }
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
                bool hasTimeline = timeline != null && timeline.EndTime.TotalSeconds > 0;
                double nextPositionSeconds = hasTimeline ? timeline!.Position.TotalSeconds : 0;
                double nextDurationSeconds = hasTimeline ? timeline!.EndTime.TotalSeconds : 0;
                double nextProgress = hasTimeline && nextDurationSeconds > 0
                    ? nextPositionSeconds / nextDurationSeconds
                    : 0;

                ServiceChangeResult changeResult = default;
                bool hasChanges = false;

                lock (_gate)
                {
                    if (_isDisposed || !_trackedSessionsBySession.TryGetValue(session, out TrackedSession? tracked))
                    {
                        return;
                    }

                    bool timelineAvailabilityChanged = tracked.HasTimeline != hasTimeline;
                    bool durationChanged = Math.Abs(tracked.DurationSeconds - nextDurationSeconds) > 0.001;
                    bool positionChanged = Math.Abs(tracked.CurrentPositionSeconds - nextPositionSeconds) > 0.001;

                    if (!timelineAvailabilityChanged && !durationChanged && !positionChanged)
                    {
                        return;
                    }

                    tracked.HasTimeline = hasTimeline;
                    tracked.CurrentPositionSeconds = nextPositionSeconds;
                    tracked.DurationSeconds = nextDurationSeconds;
                    tracked.Progress = nextProgress;

                    if (timelineAvailabilityChanged || durationChanged)
                    {
                        tracked.LastActivityUtc = DateTimeOffset.UtcNow;
                    }

                    hasChanges = true;
                    changeResult = PrepareStateChange_NoLock();
                }

                if (hasChanges)
                {
                    DispatchChange(changeResult);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to update timeline state");
            }
        }

        private ServiceChangeResult PrepareStateChange_NoLock()
        {
            _sessions = _trackedSessionsByKey.Values
                .Select(CreateSnapshot)
                .ToArray();

            bool shouldNotifyTrack = false;
            string trackTitle = string.Empty;
            string trackArtist = string.Empty;

            if (_systemCurrentSessionKey != null
                && _trackedSessionsByKey.TryGetValue(_systemCurrentSessionKey, out TrackedSession? tracked))
            {
                string signature = CreateTrackSignature(tracked.Title, tracked.Artist);
                bool hasConcreteTrackMetadata = !string.Equals(
                    tracked.Title,
                    UnknownTrackTitle,
                    StringComparison.Ordinal);
                if (_lastSystemCurrentTrackSignature != null
                    && !string.Equals(_lastSystemCurrentTrackSignature, signature, StringComparison.Ordinal))
                {
                    shouldNotifyTrack = hasConcreteTrackMetadata;
                    if (hasConcreteTrackMetadata)
                    {
                        trackTitle = tracked.Title;
                        trackArtist = tracked.Artist;
                    }
                }

                _lastSystemCurrentTrackSignature = signature;
            }
            else
            {
                _lastSystemCurrentTrackSignature = null;
            }

            return new ServiceChangeResult(
                ShouldNotifySessions: true,
                ShouldNotifyTrack: shouldNotifyTrack,
                TrackTitle: trackTitle,
                TrackArtist: trackArtist);
        }

        private void DispatchChange(ServiceChangeResult changeResult)
        {
            if (changeResult.ShouldNotifySessions)
            {
                SessionsChanged?.Invoke();
                MediaChanged?.Invoke();
            }

            if (changeResult.ShouldNotifyTrack)
            {
                TrackChanged?.Invoke(changeResult.TrackTitle, changeResult.TrackArtist);
            }
        }

        private GlobalSystemMediaTransportControlsSession? GetSession(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return null;
            }

            lock (_gate)
            {
                return _trackedSessionsByKey.TryGetValue(sessionKey, out TrackedSession? tracked)
                    ? tracked.Session
                    : null;
            }
        }

        private string CreateSessionKey_NoLock()
            => FormattableString.Invariant($"media-session-{_nextSessionOrdinal++}");

        private static MediaSessionSnapshot CreateSnapshot(TrackedSession tracked)
            => new(
                tracked.SessionKey,
                tracked.SourceAppId,
                tracked.SourceName,
                tracked.Title,
                tracked.Artist,
                tracked.PlaybackStatus,
                tracked.Progress,
                tracked.HasTimeline,
                tracked.IsSystemCurrent,
                tracked.LastActivityUtc);

        private static string CreateTrackSignature(string title, string artist)
            => string.Concat(title, "\u001f", artist);

        private static string ResolveSourceName(string rawSourceName)
        {
            if (string.IsNullOrWhiteSpace(rawSourceName))
            {
                return "Media";
            }

            string source = rawSourceName.Trim();

            int bangIndex = source.LastIndexOf('!');
            if (bangIndex >= 0 && bangIndex < source.Length - 1)
            {
                source = source[(bangIndex + 1)..];
            }
            else
            {
                int slashIndex = Math.Max(source.LastIndexOf('\\'), source.LastIndexOf('/'));
                if (slashIndex >= 0 && slashIndex < source.Length - 1)
                {
                    source = source[(slashIndex + 1)..];
                }
            }

            if (source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                source = source[..^4];
            }

            source = source.Replace('_', ' ').Replace('.', ' ').Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                return "Media";
            }

            TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;
            return textInfo.ToTitleCase(source.ToLowerInvariant());
        }

        private void DetachSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (session == null)
            {
                return;
            }

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
            {
                return;
            }

            _isDisposed = true;

            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
            }

            GlobalSystemMediaTransportControlsSession[] sessionsToDetach;
            lock (_gate)
            {
                sessionsToDetach = _trackedSessionsBySession.Keys.ToArray();
                _trackedSessionsBySession.Clear();
                _trackedSessionsByKey.Clear();
                _sessions = Array.Empty<MediaSessionSnapshot>();
                _systemCurrentSessionKey = null;
                _lastSystemCurrentTrackSignature = null;
            }

            foreach (GlobalSystemMediaTransportControlsSession session in sessionsToDetach)
            {
                DetachSession(session);
            }

            _manager = null;
        }

        private sealed class TrackedSession
        {
            public TrackedSession(
                GlobalSystemMediaTransportControlsSession session,
                string sessionKey,
                string sourceAppId,
                string sourceName)
            {
                Session = session;
                SessionKey = sessionKey;
                SourceAppId = sourceAppId;
                SourceName = sourceName;
                Title = UnknownTrackTitle;
                Artist = UnknownArtistName;
                LastActivityUtc = DateTimeOffset.UtcNow;
            }

            public GlobalSystemMediaTransportControlsSession Session { get; }
            public string SessionKey { get; }
            public string SourceAppId { get; }
            public string SourceName { get; }
            public string Title { get; set; }
            public string Artist { get; set; }
            public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; set; }
            public double Progress { get; set; }
            public bool HasTimeline { get; set; }
            public bool IsSystemCurrent { get; set; }
            public DateTimeOffset LastActivityUtc { get; set; }
            public double CurrentPositionSeconds { get; set; }
            public double DurationSeconds { get; set; }
        }

        private readonly record struct ServiceChangeResult(
            bool ShouldNotifySessions,
            bool ShouldNotifyTrack,
            string TrackTitle,
            string TrackArtist);
    }
}
