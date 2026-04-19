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
            Logger.Trace("GSMTC event: CurrentSessionChanged");
            StartRefreshBurst();
            _ = RefreshSessionsAndStatesAsync();
        }

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            Logger.Trace("GSMTC event: SessionsChanged");
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

            try
            {
                await _refreshSemaphore.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
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

                Logger.Trace($"Refreshing media sessions: system reports {sessions.Count} session(s)");

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
                                Logger.Debug($"Session rebound immediately: '{tracked.SessionKey}' ({tracked.SourceAppId}), provisional={provisionalReconnect}");
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

                        Logger.Info($"Media session removed: '{tracked.SessionKey}' ({tracked.SourceAppId})");
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

            Logger.Trace("Starting refresh burst");

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

        /// <summary>
        /// GSMTC reports TimelineProperties.Position as a cached value that is only
        /// refreshed when the OS polls the source. For a playing session, the real
        /// position at "now" is <c>Position + (Now - LastUpdatedTime)</c>. Without
        /// this adjustment the bar snaps backward to the last-cached sample every
        /// time a TimelinePropertiesChanged event fires or we re-query the session
        /// (e.g. after session switch-back).
        /// </summary>
        private static double AdjustTimelinePositionForWallClock(
            double positionSeconds, double durationSeconds, DateTimeOffset lastUpdatedTime)
        {
            if (lastUpdatedTime == default) return positionSeconds;
            double elapsed = (DateTimeOffset.UtcNow - lastUpdatedTime).TotalSeconds;
            if (elapsed <= 0 || elapsed > 12 * 3600) return positionSeconds;
            double adjusted = positionSeconds + elapsed;
            return durationSeconds > 0 ? Math.Min(adjusted, durationSeconds) : adjusted;
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
                if (hasTimeline
                    && playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    positionSeconds = AdjustTimelinePositionForWallClock(
                        positionSeconds, durationSeconds, timeline!.LastUpdatedTime);
                }

                GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties =
                    await session.TryGetMediaPropertiesAsync();
                string title = string.IsNullOrWhiteSpace(mediaProperties?.Title)
                    ? UnknownTrackTitle
                    : mediaProperties!.Title;
                string artist = string.IsNullOrWhiteSpace(mediaProperties?.Artist)
                    ? UnknownArtistName
                    : mediaProperties!.Artist;
                Windows.Storage.Streams.IRandomAccessStreamReference? thumbnail = mediaProperties?.Thumbnail;

                Logger.Trace($"Prefetched session state: '{session.SourceAppUserModelId}' Title='{title}', Artist='{artist}', Status={playbackStatus}, pos={positionSeconds:F1}s, dur={durationSeconds:F1}s");

                return new PrefetchedSessionState(
                    title,
                    artist,
                    playbackStatus,
                    hasTimeline,
                    positionSeconds,
                    durationSeconds,
                    thumbnail);
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
                Logger.Trace($"Session event handlers attached: {session.SourceAppUserModelId}");
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
        {
            Logger.Trace($"GSMTC event: MediaPropertiesChanged for session {sender.SourceAppUserModelId}");
            _ = UpdateMediaPropertiesAsync(sender);
        }

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            Logger.Trace($"GSMTC event: PlaybackInfoChanged for session {sender.SourceAppUserModelId}");
            UpdatePlaybackState(sender);
        }

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            Logger.Trace($"GSMTC event: TimelinePropertiesChanged for session {sender.SourceAppUserModelId}");
            UpdateTimelineState(sender);
        }

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
                Windows.Storage.Streams.IRandomAccessStreamReference? nextThumbnail = props.Thumbnail;
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

                    // Always update the raw thumbnail reference so it is current
                    // when stabilization eventually releases, but only treat it as
                    // a visible state change when the stabilization gate is open.
                    // Otherwise a thumbnail-only change during a skip transition
                    // would bypass the gate and dispatch an intermediate snapshot
                    // (e.g. Chrome briefly reporting another tab's paused media).
                    if (!ReferenceEquals(tracked.Thumbnail, nextThumbnail))
                    {
                        tracked.Thumbnail = nextThumbnail;
                        // Hash is stale until the async compute below finishes.
                        tracked.ThumbnailHash = string.Empty;
                        if (tracked.StabilizationReason == MediaSessionStabilizationReason.None)
                        {
                            hasChanges = true;
                        }
                    }

                    if (hasChanges)
                    {
                        Logger.Debug($"Media properties updated for '{tracked.SessionKey}': Title='{nextTitle}', Artist='{nextArtist}'");
                        changeResult = PrepareStateChange_NoLock();
                    }
                }

                if (hasChanges)
                {
                    DispatchChange(changeResult);
                }

                // Kick off async thumbnail hashing after the visible dispatch.
                // The hash result arrives on a later SessionsChanged event once it
                // has been stored on the tracked source; its only consumer today
                // is the MediaPresentationMachine's fingerprint.
                if (nextThumbnail != null)
                {
                    _ = ComputeAndStoreThumbnailHashAsync(session, nextThumbnail);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to update media properties");
            }
        }

        private async Task ComputeAndStoreThumbnailHashAsync(
            GlobalSystemMediaTransportControlsSession session,
            Windows.Storage.Streams.IRandomAccessStreamReference thumbnailRef)
        {
            string hash;
            try
            {
                using var stream = await thumbnailRef.OpenReadAsync();
                if (stream == null)
                {
                    return;
                }

                // Hash the first 4KB per spec. Full-image hashing is overkill for
                // dedup and adds latency; the first 4KB of a typical JPEG/PNG is
                // distinct enough for album art diffing.
                const int sampleSize = 4096;
                long length = Math.Min(sampleSize, (long)stream.Size);
                if (length <= 0)
                {
                    return;
                }

                var buffer = new Windows.Storage.Streams.Buffer((uint)length);
                var read = await stream.ReadAsync(buffer, (uint)length, Windows.Storage.Streams.InputStreamOptions.None);
                byte[] bytes = new byte[read.Length];
                using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(read))
                {
                    reader.ReadBytes(bytes);
                }

                ulong h = System.IO.Hashing.XxHash64.HashToUInt64(bytes);
                hash = h.ToString("x16");
            }
            catch (Exception ex)
            {
                Logger.Trace($"Thumbnail hash compute failed: {ex.Message}");
                return;
            }

            ServiceChangeResult changeResult = default;
            bool dispatched = false;
            lock (_gate)
            {
                if (_isDisposed || !_trackedSourcesBySession.TryGetValue(session, out TrackedSource? tracked))
                {
                    return;
                }
                // Bail if the thumbnail reference has been replaced since we
                // started: a newer hash computation is (or will be) in flight.
                if (!ReferenceEquals(tracked.Thumbnail, thumbnailRef))
                {
                    return;
                }
                if (string.Equals(tracked.ThumbnailHash, hash, StringComparison.Ordinal))
                {
                    return;
                }
                tracked.ThumbnailHash = hash;
                changeResult = PrepareStateChange_NoLock();
                dispatched = true;
            }

            if (dispatched)
            {
                DispatchChange(changeResult);
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
                        Logger.Debug($"Playback state updated for '{tracked.SessionKey}': {nextStatus}");
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
                if (hasTimeline)
                {
                    var playbackStatus = session.GetPlaybackInfo()?.PlaybackStatus
                        ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                    if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        nextPositionSeconds = AdjustTimelinePositionForWallClock(
                            nextPositionSeconds, nextDurationSeconds, timeline!.LastUpdatedTime);
                    }
                }
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
                        Logger.Trace($"Timeline updated for '{tracked.SessionKey}': pos={nextPositionSeconds:F1}s, dur={nextDurationSeconds:F1}s");
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