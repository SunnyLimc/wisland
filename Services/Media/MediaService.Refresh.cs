using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;
using Windows.Media.Control;

namespace wisland.Services
{
    public sealed partial class MediaService
    {
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

                    PruneExpiredTransportContinuation_NoLock(nowUtc);

                    HashSet<GlobalSystemMediaTransportControlsSession> activeSessions =
                        new(sessions, ReferenceEqualityComparer.Instance);
                    GlobalSystemMediaTransportControlsSession[] removedSessions = _trackedSourcesBySession.Keys
                        .Where(session => !activeSessions.Contains(session))
                        .ToArray();
                    Dictionary<GlobalSystemMediaTransportControlsSession, PendingSourceMatch> immediateRebinds =
                        MatchImmediateRebinds_NoLock(
                            removedSessions,
                            newSessions,
                            prefetchedStates,
                            nowUtc);
                    HashSet<string> reboundSourceKeys = new(
                        immediateRebinds.Values.Select(match => match.Tracked.SessionKey),
                        StringComparer.Ordinal);

                    foreach (GlobalSystemMediaTransportControlsSession session in sessions)
                    {
                        if (!_trackedSourcesBySession.TryGetValue(session, out TrackedSource? tracked))
                        {
                            PrefetchedSessionState prefetchedState = prefetchedStates.TryGetValue(session, out PrefetchedSessionState state)
                                ? state
                                : PrefetchedSessionState.Empty;

                            bool provisionalReconnect;
                            if (immediateRebinds.TryGetValue(session, out PendingSourceMatch immediateMatch))
                            {
                                tracked = immediateMatch.Tracked;
                                provisionalReconnect = immediateMatch.ProvisionalReconnect;
                                if (provisionalReconnect)
                                {
                                    tracked.MissingSinceUtc ??= nowUtc;
                                }
                            }
                            else
                            {
                                tracked = MatchWaitingSource_NoLock(session, prefetchedState, nowUtc, out provisionalReconnect)
                                    ?? CreateTrackedSource_NoLock(session, nowUtc);
                            }

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

                    foreach (GlobalSystemMediaTransportControlsSession session in removedSessions)
                    {
                        TrackedSource tracked = _trackedSourcesBySession[session];
                        _trackedSourcesBySession.Remove(session);
                        if (ReferenceEquals(tracked.Session, session))
                        {
                            tracked.Session = null;
                        }

                        sessionsToDetach.Add(session);
                        if (reboundSourceKeys.Contains(tracked.SessionKey))
                        {
                            continue;
                        }

                        shouldStartBurst |= string.Equals(tracked.SessionKey, _displayedSessionKey, StringComparison.Ordinal)
                            || tracked.IsSystemCurrent;
                        EnterWaitingState_NoLock(tracked, nowUtc);
                        hasChanges = true;
                    }

                    TryAbsorbTransportContinuationCandidate_NoLock(nowUtc, ref hasChanges);
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

    }
}