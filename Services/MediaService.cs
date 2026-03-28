using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;
using Windows.Media.Control;

namespace wisland.Services
{
    /// <summary>
    /// Encapsulates Windows GSMTC (Global System Media Transport Controls) API.
    /// Tracks stable logical media sources even when raw GSMTC sessions temporarily disappear.
    /// </summary>
    public sealed class MediaService : IDisposable
    {
        private const string UnknownTrackTitle = "Unknown Track";
        private const string UnknownArtistName = "Unknown Artist";

        private readonly object _gate = new();
        private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
        private readonly Dictionary<GlobalSystemMediaTransportControlsSession, TrackedSource> _trackedSourcesBySession =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, TrackedSource> _trackedSourcesByKey =
            new(StringComparer.Ordinal);
        private readonly Timer _waitingExpiryTimer;
        private readonly TimeSpan _missingSourceGrace = TimeSpan.FromMilliseconds(IslandConfig.MediaMissingGraceMs);
        private readonly TimeSpan _refreshBurstDuration = TimeSpan.FromMilliseconds(IslandConfig.MediaRefreshBurstDurationMs);
        private readonly TimeSpan _refreshBurstInterval = TimeSpan.FromMilliseconds(IslandConfig.MediaRefreshBurstIntervalMs);

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private IReadOnlyList<MediaSessionSnapshot> _sessions = Array.Empty<MediaSessionSnapshot>();
        private string? _systemCurrentSessionKey;
        private string? _displayedSessionKey;
        private string? _lastSystemCurrentTrackSignature;
        private int _nextSessionOrdinal = 1;
        private CancellationTokenSource? _refreshBurstCts;
        private bool _isDisposed;

        public MediaService()
            => _waitingExpiryTimer = new Timer(OnWaitingExpiryTimer);

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
                    return _trackedSourcesByKey.Count > 0;
                }
            }
        }

        public event Action? MediaChanged;
        public event Action? SessionsChanged;
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
                await RefreshSessionsAndStatesAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize media manager");
            }
        }

        /// <summary>
        /// Smoothly advances tracked progress locally for active playing sessions.
        /// </summary>
        public void Tick(double dt)
        {
            if (dt <= 0)
            {
                return;
            }

            lock (_gate)
            {
                foreach (TrackedSource tracked in _trackedSourcesByKey.Values)
                {
                    if (tracked.Presence != MediaSessionPresence.Active
                        || tracked.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
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
                return _trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked)
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
                return _trackedSourcesByKey.ContainsKey(sessionKey);
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
                return _trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked)
                    && tracked.Presence == MediaSessionPresence.Active
                    && tracked.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && tracked.DurationSeconds > 0;
            }
        }

        public void SetDisplayedSessionKey(string? sessionKey)
        {
            lock (_gate)
            {
                _displayedSessionKey = sessionKey;
                if (!string.IsNullOrWhiteSpace(sessionKey)
                    && _trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked))
                {
                    tracked.LastDisplayedUtc = DateTimeOffset.UtcNow;
                }
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
            finally
            {
                StartRefreshBurst();
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
            finally
            {
                StartRefreshBurst();
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            StartRefreshBurst();
            _ = RefreshSessionsAndStatesAsync();
        }

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            StartRefreshBurst();
            _ = RefreshSessionsAndStatesAsync();
        }

        private void OnWaitingExpiryTimer(object? state)
            => _ = PruneExpiredWaitingSourcesAsync();

        private async Task RefreshSessionsAndStatesAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            await _refreshSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await RefreshSessionsCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _refreshSemaphore.Release();
            }

            await RefreshActiveSourcesAsync().ConfigureAwait(false);
        }

        private async Task RefreshSessionsCoreAsync()
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
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

                HashSet<GlobalSystemMediaTransportControlsSession> knownSessions;
                lock (_gate)
                {
                    knownSessions = new HashSet<GlobalSystemMediaTransportControlsSession>(
                        _trackedSourcesBySession.Keys,
                        ReferenceEqualityComparer.Instance);
                }

                List<GlobalSystemMediaTransportControlsSession> newSessions = sessions
                    .Where(session => !knownSessions.Contains(session))
                    .ToList();
                Dictionary<GlobalSystemMediaTransportControlsSession, PrefetchedSessionState> prefetchedStates =
                    await PrefetchSessionStatesAsync(newSessions).ConfigureAwait(false);

                List<GlobalSystemMediaTransportControlsSession> sessionsToAttach = new();
                List<GlobalSystemMediaTransportControlsSession> sessionsToDetach = new();
                ServiceChangeResult changeResult = default;
                bool hasChanges = false;
                bool shouldStartBurst = false;

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
                        if (!_trackedSourcesBySession.TryGetValue(session, out TrackedSource? tracked))
                        {
                            PrefetchedSessionState prefetchedState = prefetchedStates.TryGetValue(session, out PrefetchedSessionState state)
                                ? state
                                : PrefetchedSessionState.Empty;

                            tracked = MatchWaitingSource_NoLock(session, prefetchedState, nowUtc, out bool provisionalReconnect)
                                ?? CreateTrackedSource_NoLock(session, nowUtc);

                            BindSourceToRawSession_NoLock(tracked, session, prefetchedState, nowUtc, provisionalReconnect);
                            _trackedSourcesBySession[session] = tracked;
                            sessionsToAttach.Add(session);
                            hasChanges = true;
                        }
                        else
                        {
                            tracked.LastSeenUtc = nowUtc;
                        }
                    }

                    GlobalSystemMediaTransportControlsSession[] removedSessions = _trackedSourcesBySession.Keys
                        .Where(session => !activeSessions.Contains(session))
                        .ToArray();

                    foreach (GlobalSystemMediaTransportControlsSession session in removedSessions)
                    {
                        TrackedSource tracked = _trackedSourcesBySession[session];
                        _trackedSourcesBySession.Remove(session);
                        if (ReferenceEquals(tracked.Session, session))
                        {
                            tracked.Session = null;
                        }

                        sessionsToDetach.Add(session);
                        shouldStartBurst |= string.Equals(tracked.SessionKey, _displayedSessionKey, StringComparison.Ordinal)
                            || tracked.IsSystemCurrent;
                        EnterWaitingState_NoLock(tracked, nowUtc);
                        hasChanges = true;
                    }

                    CleanupExpiredWaitingSources_NoLock(nowUtc, ref hasChanges);

                    string? nextSystemCurrentKey = ResolveSystemCurrentKey_NoLock(currentSession, nowUtc);
                    foreach (TrackedSource tracked in _trackedSourcesByKey.Values)
                    {
                        bool isSystemCurrent = string.Equals(tracked.SessionKey, nextSystemCurrentKey, StringComparison.Ordinal);
                        if (tracked.IsSystemCurrent != isSystemCurrent)
                        {
                            tracked.IsSystemCurrent = isSystemCurrent;
                            hasChanges = true;
                        }

                        if (isSystemCurrent)
                        {
                            tracked.LastSystemCurrentUtc = nowUtc;
                        }
                    }

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
                }

                if (hasChanges)
                {
                    DispatchChange(changeResult);
                }

                if (shouldStartBurst)
                {
                    StartRefreshBurst();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to refresh media sessions");
            }
        }

        private async Task RefreshActiveSourcesAsync()
        {
            GlobalSystemMediaTransportControlsSession[] sessions;
            lock (_gate)
            {
                sessions = _trackedSourcesBySession.Keys.ToArray();
            }

            foreach (GlobalSystemMediaTransportControlsSession session in sessions)
            {
                UpdatePlaybackState(session);
                UpdateTimelineState(session);
                await UpdateMediaPropertiesAsync(session).ConfigureAwait(false);
            }
        }

        private void StartRefreshBurst()
        {
            if (_isDisposed)
            {
                return;
            }

            CancellationTokenSource? previousCts;
            CancellationTokenSource nextCts = new();
            lock (_gate)
            {
                previousCts = _refreshBurstCts;
                _refreshBurstCts = nextCts;
            }

            previousCts?.Cancel();
            previousCts?.Dispose();
            _ = RunRefreshBurstAsync(nextCts);
        }

        private async Task RunRefreshBurstAsync(CancellationTokenSource refreshBurstCts)
        {
            try
            {
                DateTimeOffset deadlineUtc = DateTimeOffset.UtcNow + _refreshBurstDuration;
                while (!refreshBurstCts.IsCancellationRequested && !_isDisposed)
                {
                    await RefreshSessionsAndStatesAsync().ConfigureAwait(false);

                    TimeSpan remaining = deadlineUtc - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    TimeSpan delay = remaining < _refreshBurstInterval
                        ? remaining
                        : _refreshBurstInterval;
                    await Task.Delay(delay, refreshBurstCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Refresh burst failed");
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_refreshBurstCts, refreshBurstCts))
                    {
                        _refreshBurstCts = null;
                    }
                }

                refreshBurstCts.Dispose();
            }
        }

        private static async Task<Dictionary<GlobalSystemMediaTransportControlsSession, PrefetchedSessionState>> PrefetchSessionStatesAsync(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
        {
            if (sessions.Count == 0)
            {
                return new Dictionary<GlobalSystemMediaTransportControlsSession, PrefetchedSessionState>(
                    ReferenceEqualityComparer.Instance);
            }

            Task<(GlobalSystemMediaTransportControlsSession Session, PrefetchedSessionState State)>[] tasks = sessions
                .Select(async session => (session, await CaptureSessionStateAsync(session).ConfigureAwait(false)))
                .ToArray();

            Dictionary<GlobalSystemMediaTransportControlsSession, PrefetchedSessionState> result =
                new(ReferenceEqualityComparer.Instance);
            foreach ((GlobalSystemMediaTransportControlsSession session, PrefetchedSessionState state) in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                result[session] = state;
            }

            return result;
        }

        private static async Task<PrefetchedSessionState> CaptureSessionStateAsync(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus =
                    session.GetPlaybackInfo()?.PlaybackStatus
                    ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

                GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = session.GetTimelineProperties();
                bool hasTimeline = timeline != null && timeline.EndTime.TotalSeconds > 0;
                double positionSeconds = hasTimeline ? timeline!.Position.TotalSeconds : 0;
                double durationSeconds = hasTimeline ? timeline!.EndTime.TotalSeconds : 0;

                GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties =
                    await session.TryGetMediaPropertiesAsync();
                string title = string.IsNullOrWhiteSpace(mediaProperties?.Title)
                    ? UnknownTrackTitle
                    : mediaProperties!.Title;
                string artist = string.IsNullOrWhiteSpace(mediaProperties?.Artist)
                    ? UnknownArtistName
                    : mediaProperties!.Artist;

                return new PrefetchedSessionState(
                    title,
                    artist,
                    playbackStatus,
                    hasTimeline,
                    positionSeconds,
                    durationSeconds);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Falling back to partial session state during prefetch: {ex.Message}");
                return PrefetchedSessionState.Empty;
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
                GlobalSystemMediaTransportControlsSessionMediaProperties? props =
                    await session.TryGetMediaPropertiesAsync();
                if (props == null)
                {
                    return;
                }

                string nextTitle = string.IsNullOrWhiteSpace(props.Title) ? UnknownTrackTitle : props.Title;
                string nextArtist = string.IsNullOrWhiteSpace(props.Artist) ? UnknownArtistName : props.Artist;
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                ServiceChangeResult changeResult = default;
                bool hasChanges = false;

                lock (_gate)
                {
                    if (_isDisposed || !_trackedSourcesBySession.TryGetValue(session, out TrackedSource? tracked))
                    {
                        return;
                    }

                    hasChanges = ApplyMediaProperties_NoLock(tracked, nextTitle, nextArtist, nowUtc);
                    if (hasChanges)
                    {
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
                GlobalSystemMediaTransportControlsSessionPlaybackStatus nextStatus =
                    session.GetPlaybackInfo()?.PlaybackStatus
                    ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                ServiceChangeResult changeResult = default;
                bool hasChanges = false;

                lock (_gate)
                {
                    if (_isDisposed || !_trackedSourcesBySession.TryGetValue(session, out TrackedSource? tracked))
                    {
                        return;
                    }

                    hasChanges = ApplyPlaybackState_NoLock(tracked, nextStatus, nowUtc);
                    if (hasChanges)
                    {
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
                GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = session.GetTimelineProperties();
                bool hasTimeline = timeline != null && timeline.EndTime.TotalSeconds > 0;
                double nextPositionSeconds = hasTimeline ? timeline!.Position.TotalSeconds : 0;
                double nextDurationSeconds = hasTimeline ? timeline!.EndTime.TotalSeconds : 0;
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                ServiceChangeResult changeResult = default;
                bool hasChanges = false;

                lock (_gate)
                {
                    if (_isDisposed || !_trackedSourcesBySession.TryGetValue(session, out TrackedSource? tracked))
                    {
                        return;
                    }

                    hasChanges = ApplyTimelineState_NoLock(
                        tracked,
                        hasTimeline,
                        nextPositionSeconds,
                        nextDurationSeconds,
                        nowUtc);
                    if (hasChanges)
                    {
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
                Logger.Error(ex, "Failed to update timeline state");
            }
        }

        private async Task PruneExpiredWaitingSourcesAsync()
        {
            ServiceChangeResult changeResult = default;
            bool hasChanges = false;

            lock (_gate)
            {
                if (_isDisposed)
                {
                    return;
                }

                CleanupExpiredWaitingSources_NoLock(DateTimeOffset.UtcNow, ref hasChanges);
                if (hasChanges)
                {
                    changeResult = PrepareStateChange_NoLock();
                }
            }

            if (hasChanges)
            {
                DispatchChange(changeResult);
            }

            await RefreshSessionsAndStatesAsync().ConfigureAwait(false);
        }

        private TrackedSource? MatchWaitingSource_NoLock(
            GlobalSystemMediaTransportControlsSession session,
            PrefetchedSessionState prefetchedState,
            DateTimeOffset nowUtc,
            out bool provisionalReconnect)
        {
            provisionalReconnect = false;
            string sourceAppId = session.SourceAppUserModelId;
            List<TrackedSource> waitingCandidates = _trackedSourcesByKey.Values
                .Where(source =>
                    source.Presence == MediaSessionPresence.WaitingForReconnect
                    && string.Equals(source.SourceAppId, sourceAppId, StringComparison.Ordinal)
                    && !IsMissingGraceExpired_NoLock(source, nowUtc))
                .OrderByDescending(source => source.MissingSinceUtc)
                .ToList();

            if (waitingCandidates.Count == 0)
            {
                return null;
            }

            if (prefetchedState.HasConcreteMetadata)
            {
                TrackedSource? exactMatch = waitingCandidates.FirstOrDefault(source =>
                    string.Equals(source.Title, prefetchedState.Title, StringComparison.Ordinal)
                    && string.Equals(source.Artist, prefetchedState.Artist, StringComparison.Ordinal));
                if (exactMatch != null)
                {
                    return exactMatch;
                }
            }

            TrackedSource? fallbackMatch = waitingCandidates
                .Where(source => WasReconnectHintedRecently_NoLock(source, nowUtc))
                .OrderByDescending(GetReconnectPriority_NoLock)
                .ThenByDescending(source => source.MissingSinceUtc)
                .FirstOrDefault();

            if (fallbackMatch != null)
            {
                provisionalReconnect = true;
            }

            return fallbackMatch;
        }

        private static bool WasReconnectHintedRecently_NoLock(TrackedSource source, DateTimeOffset nowUtc)
            => source.LastDisplayedUtc.HasValue
                || source.LastSystemCurrentUtc.HasValue
                || (source.MissingSinceUtc.HasValue && (nowUtc - source.MissingSinceUtc.Value) <= TimeSpan.FromMilliseconds(IslandConfig.MediaMissingGraceMs));

        private static long GetReconnectPriority_NoLock(TrackedSource source)
        {
            DateTimeOffset displayed = source.LastDisplayedUtc ?? DateTimeOffset.MinValue;
            DateTimeOffset systemCurrent = source.LastSystemCurrentUtc ?? DateTimeOffset.MinValue;
            DateTimeOffset missing = source.MissingSinceUtc ?? DateTimeOffset.MinValue;
            return Math.Max(displayed.UtcTicks, Math.Max(systemCurrent.UtcTicks, missing.UtcTicks));
        }

        private TrackedSource CreateTrackedSource_NoLock(GlobalSystemMediaTransportControlsSession session, DateTimeOffset nowUtc)
        {
            TrackedSource tracked = new(
                CreateSessionKey_NoLock(),
                session.SourceAppUserModelId,
                ResolveSourceName(session.SourceAppUserModelId),
                nowUtc);

            _trackedSourcesByKey.Add(tracked.SessionKey, tracked);
            return tracked;
        }

        private void BindSourceToRawSession_NoLock(
            TrackedSource tracked,
            GlobalSystemMediaTransportControlsSession session,
            PrefetchedSessionState prefetchedState,
            DateTimeOffset nowUtc,
            bool provisionalReconnect)
        {
            tracked.Session = session;
            tracked.SourceAppId = session.SourceAppUserModelId;
            tracked.SourceName = ResolveSourceName(session.SourceAppUserModelId);
            tracked.LastSeenUtc = nowUtc;

            if (!provisionalReconnect)
            {
                tracked.Presence = MediaSessionPresence.Active;
                tracked.MissingSinceUtc = null;
                ResetPendingReconnect_NoLock(tracked);
                ApplyMediaProperties_NoLock(tracked, prefetchedState.Title, prefetchedState.Artist, nowUtc);
                ApplyPlaybackState_NoLock(tracked, prefetchedState.PlaybackStatus, nowUtc);
                ApplyTimelineState_NoLock(
                    tracked,
                    prefetchedState.HasTimeline,
                    prefetchedState.PositionSeconds,
                    prefetchedState.DurationSeconds,
                    nowUtc);
                return;
            }

            tracked.HasPendingReconnect = true;
            tracked.PendingTitle = prefetchedState.Title;
            tracked.PendingArtist = prefetchedState.Artist;
            tracked.PendingPlaybackStatus = prefetchedState.PlaybackStatus;
            tracked.PendingHasTimeline = prefetchedState.HasTimeline;
            tracked.PendingPositionSeconds = prefetchedState.PositionSeconds;
            tracked.PendingDurationSeconds = prefetchedState.DurationSeconds;
            TryFinalizePendingReconnect_NoLock(tracked, nowUtc);
        }

        private void EnterWaitingState_NoLock(TrackedSource tracked, DateTimeOffset nowUtc)
        {
            tracked.Presence = MediaSessionPresence.WaitingForReconnect;
            tracked.MissingSinceUtc ??= nowUtc;
            tracked.IsSystemCurrent = false;
            if (tracked.HasPendingReconnect)
            {
                tracked.Session = null;
            }
        }

        private bool ApplyMediaProperties_NoLock(TrackedSource tracked, string nextTitle, string nextArtist, DateTimeOffset nowUtc)
        {
            if (tracked.HasPendingReconnect)
            {
                tracked.PendingTitle = nextTitle;
                tracked.PendingArtist = nextArtist;
                return TryFinalizePendingReconnect_NoLock(tracked, nowUtc);
            }

            if (string.Equals(tracked.Title, nextTitle, StringComparison.Ordinal)
                && string.Equals(tracked.Artist, nextArtist, StringComparison.Ordinal))
            {
                return false;
            }

            tracked.Title = nextTitle;
            tracked.Artist = nextArtist;
            tracked.LastActivityUtc = nowUtc;
            return true;
        }

        private bool ApplyPlaybackState_NoLock(
            TrackedSource tracked,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus nextStatus,
            DateTimeOffset nowUtc)
        {
            if (tracked.HasPendingReconnect)
            {
                tracked.PendingPlaybackStatus = nextStatus;
                return TryFinalizePendingReconnect_NoLock(tracked, nowUtc);
            }

            if (tracked.PlaybackStatus == nextStatus)
            {
                return false;
            }

            tracked.PlaybackStatus = nextStatus;
            tracked.LastActivityUtc = nowUtc;
            return true;
        }

        private bool ApplyTimelineState_NoLock(
            TrackedSource tracked,
            bool hasTimeline,
            double positionSeconds,
            double durationSeconds,
            DateTimeOffset nowUtc)
        {
            if (tracked.HasPendingReconnect)
            {
                tracked.PendingHasTimeline = hasTimeline;
                tracked.PendingPositionSeconds = positionSeconds;
                tracked.PendingDurationSeconds = durationSeconds;
                return TryFinalizePendingReconnect_NoLock(tracked, nowUtc);
            }

            bool timelineAvailabilityChanged = tracked.HasTimeline != hasTimeline;
            bool durationChanged = Math.Abs(tracked.DurationSeconds - durationSeconds) > 0.001;
            bool positionChanged = Math.Abs(tracked.CurrentPositionSeconds - positionSeconds) > 0.001;

            if (!timelineAvailabilityChanged && !durationChanged && !positionChanged)
            {
                return false;
            }

            tracked.HasTimeline = hasTimeline;
            tracked.CurrentPositionSeconds = positionSeconds;
            tracked.DurationSeconds = durationSeconds;
            tracked.Progress = hasTimeline && durationSeconds > 0
                ? positionSeconds / durationSeconds
                : 0;

            if (timelineAvailabilityChanged || durationChanged)
            {
                tracked.LastActivityUtc = nowUtc;
            }

            return true;
        }

        private bool TryFinalizePendingReconnect_NoLock(TrackedSource tracked, DateTimeOffset nowUtc)
        {
            if (!tracked.HasPendingReconnect)
            {
                return false;
            }

            bool hasConcreteMetadata = HasConcreteMetadata(tracked.PendingTitle);
            bool shouldFinalize = hasConcreteMetadata
                && (tracked.PendingPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    || IsMissingGraceExpired_NoLock(tracked, nowUtc));
            if (!shouldFinalize)
            {
                return false;
            }

            bool hasChanges = false;
            if (tracked.Presence != MediaSessionPresence.Active)
            {
                tracked.Presence = MediaSessionPresence.Active;
                tracked.MissingSinceUtc = null;
                hasChanges = true;
            }

            if (!string.Equals(tracked.Title, tracked.PendingTitle, StringComparison.Ordinal)
                || !string.Equals(tracked.Artist, tracked.PendingArtist, StringComparison.Ordinal))
            {
                tracked.Title = tracked.PendingTitle;
                tracked.Artist = tracked.PendingArtist;
                hasChanges = true;
            }

            if (tracked.PlaybackStatus != tracked.PendingPlaybackStatus)
            {
                tracked.PlaybackStatus = tracked.PendingPlaybackStatus;
                hasChanges = true;
            }

            bool timelineAvailabilityChanged = tracked.HasTimeline != tracked.PendingHasTimeline;
            bool durationChanged = Math.Abs(tracked.DurationSeconds - tracked.PendingDurationSeconds) > 0.001;
            bool positionChanged = Math.Abs(tracked.CurrentPositionSeconds - tracked.PendingPositionSeconds) > 0.001;
            if (timelineAvailabilityChanged || durationChanged || positionChanged)
            {
                tracked.HasTimeline = tracked.PendingHasTimeline;
                tracked.CurrentPositionSeconds = tracked.PendingPositionSeconds;
                tracked.DurationSeconds = tracked.PendingDurationSeconds;
                tracked.Progress = tracked.PendingHasTimeline && tracked.PendingDurationSeconds > 0
                    ? tracked.PendingPositionSeconds / tracked.PendingDurationSeconds
                    : 0;
                hasChanges = true;
            }

            tracked.LastActivityUtc = nowUtc;
            ResetPendingReconnect_NoLock(tracked);
            return hasChanges;
        }

        private void ResetPendingReconnect_NoLock(TrackedSource tracked)
        {
            tracked.HasPendingReconnect = false;
            tracked.PendingTitle = UnknownTrackTitle;
            tracked.PendingArtist = UnknownArtistName;
            tracked.PendingPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            tracked.PendingHasTimeline = false;
            tracked.PendingPositionSeconds = 0;
            tracked.PendingDurationSeconds = 0;
        }

        private void CleanupExpiredWaitingSources_NoLock(DateTimeOffset nowUtc, ref bool hasChanges)
        {
            string[] expiredKeys = _trackedSourcesByKey.Values
                .Where(source =>
                    source.Presence == MediaSessionPresence.WaitingForReconnect
                    && IsMissingGraceExpired_NoLock(source, nowUtc)
                    && source.Session == null)
                .Select(source => source.SessionKey)
                .ToArray();

            foreach (string sessionKey in expiredKeys)
            {
                _trackedSourcesByKey.Remove(sessionKey);
                if (string.Equals(_systemCurrentSessionKey, sessionKey, StringComparison.Ordinal))
                {
                    _systemCurrentSessionKey = null;
                }

                hasChanges = true;
            }

            foreach (TrackedSource provisional in _trackedSourcesByKey.Values.Where(source => source.HasPendingReconnect).ToArray())
            {
                if (TryFinalizePendingReconnect_NoLock(provisional, nowUtc))
                {
                    hasChanges = true;
                }
            }
        }

        private bool IsMissingGraceExpired_NoLock(TrackedSource tracked, DateTimeOffset nowUtc)
            => tracked.MissingSinceUtc.HasValue
                && (nowUtc - tracked.MissingSinceUtc.Value) >= _missingSourceGrace;

        private string? ResolveSystemCurrentKey_NoLock(
            GlobalSystemMediaTransportControlsSession? currentSession,
            DateTimeOffset nowUtc)
        {
            if (currentSession != null
                && _trackedSourcesBySession.TryGetValue(currentSession, out TrackedSource? currentTracked))
            {
                return currentTracked.SessionKey;
            }

            if (_systemCurrentSessionKey != null
                && _trackedSourcesByKey.TryGetValue(_systemCurrentSessionKey, out TrackedSource? previousCurrent)
                && previousCurrent.Presence == MediaSessionPresence.WaitingForReconnect
                && !IsMissingGraceExpired_NoLock(previousCurrent, nowUtc))
            {
                return previousCurrent.SessionKey;
            }

            return null;
        }

        private ServiceChangeResult PrepareStateChange_NoLock()
        {
            RescheduleWaitingExpiryTimer_NoLock();

            _sessions = _trackedSourcesByKey.Values
                .Select(CreateSnapshot)
                .ToArray();

            bool shouldNotifyTrack = false;
            string trackTitle = string.Empty;
            string trackArtist = string.Empty;

            if (_systemCurrentSessionKey != null
                && _trackedSourcesByKey.TryGetValue(_systemCurrentSessionKey, out TrackedSource? tracked)
                && tracked.Presence == MediaSessionPresence.Active
                && HasConcreteMetadata(tracked.Title))
            {
                string signature = CreateTrackSignature(tracked.Title, tracked.Artist);
                if (_lastSystemCurrentTrackSignature != null
                    && !string.Equals(_lastSystemCurrentTrackSignature, signature, StringComparison.Ordinal))
                {
                    shouldNotifyTrack = true;
                    trackTitle = tracked.Title;
                    trackArtist = tracked.Artist;
                }

                _lastSystemCurrentTrackSignature = signature;
            }

            return new ServiceChangeResult(
                ShouldNotifySessions: true,
                ShouldNotifyTrack: shouldNotifyTrack,
                TrackTitle: trackTitle,
                TrackArtist: trackArtist);
        }

        private void RescheduleWaitingExpiryTimer_NoLock()
        {
            DateTimeOffset? nextExpiryUtc = _trackedSourcesByKey.Values
                .Where(source =>
                    source.Presence == MediaSessionPresence.WaitingForReconnect
                    && source.MissingSinceUtc.HasValue)
                .Select(source => source.MissingSinceUtc!.Value + _missingSourceGrace)
                .OrderBy(expiry => expiry)
                .Cast<DateTimeOffset?>()
                .FirstOrDefault();

            if (!nextExpiryUtc.HasValue)
            {
                _waitingExpiryTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            TimeSpan dueTime = nextExpiryUtc.Value - DateTimeOffset.UtcNow;
            if (dueTime < TimeSpan.Zero)
            {
                dueTime = TimeSpan.Zero;
            }

            _waitingExpiryTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
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
                return _trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked)
                    ? tracked.Session
                    : null;
            }
        }

        private string CreateSessionKey_NoLock()
            => FormattableString.Invariant($"media-source-{_nextSessionOrdinal++}");

        private static MediaSessionSnapshot CreateSnapshot(TrackedSource tracked)
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
                tracked.LastActivityUtc,
                tracked.Presence,
                tracked.LastSeenUtc,
                tracked.MissingSinceUtc);

        private static string CreateTrackSignature(string title, string artist)
            => string.Concat(title, "\u001f", artist);

        private static bool HasConcreteMetadata(string title)
            => !string.Equals(title, UnknownTrackTitle, StringComparison.Ordinal);

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

            _waitingExpiryTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _waitingExpiryTimer.Dispose();

            CancellationTokenSource? refreshBurstCts;
            lock (_gate)
            {
                refreshBurstCts = _refreshBurstCts;
                _refreshBurstCts = null;
            }

            refreshBurstCts?.Cancel();
            refreshBurstCts?.Dispose();

            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
            }

            GlobalSystemMediaTransportControlsSession[] sessionsToDetach;
            lock (_gate)
            {
                sessionsToDetach = _trackedSourcesBySession.Keys.ToArray();
                _trackedSourcesBySession.Clear();
                _trackedSourcesByKey.Clear();
                _sessions = Array.Empty<MediaSessionSnapshot>();
                _systemCurrentSessionKey = null;
                _displayedSessionKey = null;
            }

            foreach (GlobalSystemMediaTransportControlsSession session in sessionsToDetach)
            {
                DetachSession(session);
            }

            _manager = null;
            _refreshSemaphore.Dispose();
        }

        private sealed class TrackedSource
        {
            public TrackedSource(
                string sessionKey,
                string sourceAppId,
                string sourceName,
                DateTimeOffset nowUtc)
            {
                SessionKey = sessionKey;
                SourceAppId = sourceAppId;
                SourceName = sourceName;
                Title = UnknownTrackTitle;
                Artist = UnknownArtistName;
                LastActivityUtc = nowUtc;
                LastSeenUtc = nowUtc;
                Presence = MediaSessionPresence.Active;
                PendingTitle = UnknownTrackTitle;
                PendingArtist = UnknownArtistName;
                PendingPlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            }

            public GlobalSystemMediaTransportControlsSession? Session { get; set; }
            public string SessionKey { get; }
            public string SourceAppId { get; set; }
            public string SourceName { get; set; }
            public string Title { get; set; }
            public string Artist { get; set; }
            public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; set; }
            public double Progress { get; set; }
            public bool HasTimeline { get; set; }
            public bool IsSystemCurrent { get; set; }
            public DateTimeOffset LastActivityUtc { get; set; }
            public DateTimeOffset LastSeenUtc { get; set; }
            public DateTimeOffset? MissingSinceUtc { get; set; }
            public MediaSessionPresence Presence { get; set; }
            public double CurrentPositionSeconds { get; set; }
            public double DurationSeconds { get; set; }
            public DateTimeOffset? LastDisplayedUtc { get; set; }
            public DateTimeOffset? LastSystemCurrentUtc { get; set; }
            public bool HasPendingReconnect { get; set; }
            public string PendingTitle { get; set; }
            public string PendingArtist { get; set; }
            public GlobalSystemMediaTransportControlsSessionPlaybackStatus PendingPlaybackStatus { get; set; }
            public bool PendingHasTimeline { get; set; }
            public double PendingPositionSeconds { get; set; }
            public double PendingDurationSeconds { get; set; }
        }

        private readonly record struct PrefetchedSessionState(
            string Title,
            string Artist,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus,
            bool HasTimeline,
            double PositionSeconds,
            double DurationSeconds)
        {
            public static readonly PrefetchedSessionState Empty = new(
                UnknownTrackTitle,
                UnknownArtistName,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed,
                HasTimeline: false,
                PositionSeconds: 0,
                DurationSeconds: 0);

            public bool HasConcreteMetadata => MediaService.HasConcreteMetadata(Title);
        }

        private readonly record struct ServiceChangeResult(
            bool ShouldNotifySessions,
            bool ShouldNotifyTrack,
            string TrackTitle,
            string TrackArtist);
    }
}
