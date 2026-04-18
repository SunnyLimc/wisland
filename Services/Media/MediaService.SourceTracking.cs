using System;
using System.Collections.Generic;
using System.Linq;
using wisland.Helpers;
using wisland.Models;
using Windows.Media.Control;

namespace wisland.Services
{
    public sealed partial class MediaService
    {
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

            if (TryGetPendingTransportTarget_NoLock(nowUtc, out TrackedSource transportTarget))
            {
                bool isReservedWaitingCandidate = waitingCandidates.Any(candidate =>
                    string.Equals(candidate.SessionKey, transportTarget.SessionKey, StringComparison.Ordinal));
                if (isReservedWaitingCandidate)
                {
                    Logger.Debug($"Waiting source matched via transport continuation target: '{transportTarget.SessionKey}'");
                    provisionalReconnect = true;
                    return transportTarget;
                }
            }

            if (prefetchedState.HasConcreteMetadata)
            {
                TrackedSource? exactMatch = waitingCandidates.FirstOrDefault(source =>
                    string.Equals(source.Title, prefetchedState.Title, StringComparison.Ordinal)
                    && string.Equals(source.Artist, prefetchedState.Artist, StringComparison.Ordinal));
                if (exactMatch != null)
                {
                    Logger.Debug($"Waiting source matched by exact metadata: '{exactMatch.SessionKey}' Title='{prefetchedState.Title}'");
                    return exactMatch;
                }
            }

            if (waitingCandidates.Count == 1
                && WasReconnectHintedRecently_NoLock(waitingCandidates[0], nowUtc)
                && TryGetContinuationScore_NoLock(waitingCandidates[0], prefetchedState, out _))
            {
                provisionalReconnect = true;
                return waitingCandidates[0];
            }

            return null;
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

        private Dictionary<GlobalSystemMediaTransportControlsSession, PendingSourceMatch> MatchImmediateRebinds_NoLock(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> removedSessions,
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> newSessions,
            IReadOnlyDictionary<GlobalSystemMediaTransportControlsSession, PrefetchedSessionState> prefetchedStates,
            DateTimeOffset nowUtc)
        {
            Dictionary<GlobalSystemMediaTransportControlsSession, PendingSourceMatch> matches =
                new(ReferenceEqualityComparer.Instance);
            if (removedSessions.Count == 0 || newSessions.Count == 0)
            {
                return matches;
            }

            List<TrackedSource> removedSources = removedSessions
                .Select(session => _trackedSourcesBySession[session])
                .GroupBy(source => source.SessionKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            HashSet<string> assignedSourceKeys = new(StringComparer.Ordinal);
            HashSet<GlobalSystemMediaTransportControlsSession> assignedSessions =
                new(ReferenceEqualityComparer.Instance);

            foreach (GlobalSystemMediaTransportControlsSession session in newSessions)
            {
                PrefetchedSessionState prefetchedState = prefetchedStates.TryGetValue(session, out PrefetchedSessionState state)
                    ? state
                    : PrefetchedSessionState.Empty;
                if (!prefetchedState.HasConcreteMetadata)
                {
                    continue;
                }

                TrackedSource? exactMatch = removedSources
                    .Where(source =>
                        !assignedSourceKeys.Contains(source.SessionKey)
                        && string.Equals(source.SourceAppId, session.SourceAppUserModelId, StringComparison.Ordinal)
                        && string.Equals(source.Title, prefetchedState.Title, StringComparison.Ordinal)
                        && string.Equals(source.Artist, prefetchedState.Artist, StringComparison.Ordinal))
                    .OrderByDescending(GetReconnectPriority_NoLock)
                    .ThenBy(source => GetTimelineDistance_NoLock(source, prefetchedState))
                    .FirstOrDefault();

                if (exactMatch == null)
                {
                    continue;
                }

                matches[session] = new PendingSourceMatch(exactMatch, ProvisionalReconnect: false);
                assignedSourceKeys.Add(exactMatch.SessionKey);
                assignedSessions.Add(session);
            }

            if (TryGetPendingTransportTarget_NoLock(nowUtc, out TrackedSource transportTarget))
            {
                bool removedTargetExists = removedSources.Any(source =>
                    string.Equals(source.SessionKey, transportTarget.SessionKey, StringComparison.Ordinal));
                if (removedTargetExists
                    && !assignedSourceKeys.Contains(transportTarget.SessionKey))
                {
                    GlobalSystemMediaTransportControlsSession? transportSession = SelectTransportContinuationSession_NoLock(
                        transportTarget,
                        newSessions,
                        prefetchedStates,
                        assignedSessions);
                    if (transportSession != null)
                    {
                        matches[transportSession] = new PendingSourceMatch(transportTarget, ProvisionalReconnect: true);
                        assignedSourceKeys.Add(transportTarget.SessionKey);
                        assignedSessions.Add(transportSession);
                    }
                }
            }

            List<PendingContinuationCandidate> continuationCandidates = new();
            foreach (GlobalSystemMediaTransportControlsSession session in newSessions)
            {
                if (assignedSessions.Contains(session))
                {
                    continue;
                }

                PrefetchedSessionState prefetchedState = prefetchedStates.TryGetValue(session, out PrefetchedSessionState state)
                    ? state
                    : PrefetchedSessionState.Empty;

                foreach (TrackedSource source in removedSources)
                {
                    if (assignedSourceKeys.Contains(source.SessionKey)
                        || !string.Equals(source.SourceAppId, session.SourceAppUserModelId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (TryGetContinuationScore_NoLock(source, prefetchedState, out double score))
                    {
                        continuationCandidates.Add(new PendingContinuationCandidate(session, source, score));
                    }
                }
            }

            foreach (PendingContinuationCandidate candidate in continuationCandidates
                .OrderBy(candidate => candidate.Score)
                .ThenByDescending(candidate => GetReconnectPriority_NoLock(candidate.Source)))
            {
                if (assignedSessions.Contains(candidate.Session) || assignedSourceKeys.Contains(candidate.Source.SessionKey))
                {
                    continue;
                }

                matches[candidate.Session] = new PendingSourceMatch(candidate.Source, ProvisionalReconnect: true);
                assignedSessions.Add(candidate.Session);
                assignedSourceKeys.Add(candidate.Source.SessionKey);
            }

            return matches;
        }

        private GlobalSystemMediaTransportControlsSession? SelectTransportContinuationSession_NoLock(
            TrackedSource target,
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> newSessions,
            IReadOnlyDictionary<GlobalSystemMediaTransportControlsSession, PrefetchedSessionState> prefetchedStates,
            HashSet<GlobalSystemMediaTransportControlsSession> assignedSessions)
        {
            TransportContinuationCandidate? bestCandidate = null;
            foreach (GlobalSystemMediaTransportControlsSession session in newSessions)
            {
                if (assignedSessions.Contains(session)
                    || !string.Equals(session.SourceAppUserModelId, target.SourceAppId, StringComparison.Ordinal))
                {
                    continue;
                }

                PrefetchedSessionState state = prefetchedStates.TryGetValue(session, out PrefetchedSessionState prefetchedState)
                    ? prefetchedState
                    : PrefetchedSessionState.Empty;
                TransportContinuationCandidate candidate = new(
                    Session: session,
                    Score: GetTransportContinuationScore(state));
                if (!bestCandidate.HasValue || candidate.Score < bestCandidate.Value.Score)
                {
                    bestCandidate = candidate;
                }
            }

            return bestCandidate?.Session;
        }

        private static int GetTransportContinuationScore(PrefetchedSessionState state)
        {
            if (state.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return 0;
            }

            if (state.HasConcreteMetadata)
            {
                return 1;
            }

            if (state.HasTimeline)
            {
                return 2;
            }

            return 3;
        }

        private static bool TryGetContinuationScore_NoLock(
            TrackedSource source,
            PrefetchedSessionState prefetchedState,
            out double score)
        {
            score = double.MaxValue;
            if (prefetchedState.HasTimeline && source.HasTimeline)
            {
                double distance = GetTimelineDistance_NoLock(source, prefetchedState);
                if (distance > 8.0)
                {
                    return false;
                }

                score = distance;
                if (source.PlaybackStatus != prefetchedState.PlaybackStatus)
                {
                    score += 2.0;
                }

                if (source.LastDisplayedUtc.HasValue || source.LastSystemCurrentUtc.HasValue)
                {
                    score -= 0.5;
                }

                return true;
            }

            return false;
        }

        private static double GetTimelineDistance_NoLock(TrackedSource source, PrefetchedSessionState prefetchedState)
            => Math.Abs(source.CurrentPositionSeconds - prefetchedState.PositionSeconds);

        private TrackedSource CreateTrackedSource_NoLock(GlobalSystemMediaTransportControlsSession session, DateTimeOffset nowUtc)
        {
            TrackedSource tracked = new(
                CreateSessionKey_NoLock(),
                session.SourceAppUserModelId,
                MediaSourceNameFormatter.Resolve(session.SourceAppUserModelId),
                nowUtc);

            _trackedSourcesByKey.Add(tracked.SessionKey, tracked);
            Logger.Info($"New media session created: '{tracked.SessionKey}' for app '{tracked.SourceName}' ({tracked.SourceAppId})");
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
            tracked.SourceName = MediaSourceNameFormatter.Resolve(session.SourceAppUserModelId);
            tracked.LastSeenUtc = nowUtc;

            if (!provisionalReconnect)
            {
                tracked.Presence = MediaSessionPresence.Active;
                tracked.MissingSinceUtc = null;
                ResetPendingReconnect_NoLock(tracked);
                ApplyMediaProperties_NoLock(tracked, prefetchedState.Title, prefetchedState.Artist, nowUtc);
                tracked.Thumbnail = prefetchedState.Thumbnail;
                ApplyPlaybackState_NoLock(tracked, prefetchedState.PlaybackStatus, nowUtc);
                ApplyTimelineState_NoLock(
                    tracked,
                    prefetchedState.HasTimeline,
                    prefetchedState.PositionSeconds,
                    prefetchedState.DurationSeconds,
                    nowUtc);
                return;
            }

            tracked.Presence = MediaSessionPresence.Active;
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
            Logger.Trace($"Source entering waiting state: '{tracked.SessionKey}' ({tracked.SourceAppId})");
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

            // Prime natural-ending stabilization if the current state is near end-of-track
            // and the incoming metadata differs from what we're currently showing.
            TryArmNaturalEndingStabilization_NoLock(tracked, nextTitle, nextArtist, nowUtc);

            if (string.Equals(tracked.Title, nextTitle, StringComparison.Ordinal)
                && string.Equals(tracked.Artist, nextArtist, StringComparison.Ordinal))
            {
                return false;
            }

            tracked.Title = nextTitle;
            tracked.Artist = nextArtist;
            tracked.LastActivityUtc = nowUtc;
            return EvaluateStabilizationAfterWrite_NoLock(tracked, nowUtc);
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
            return EvaluateStabilizationAfterWrite_NoLock(tracked, nowUtc);
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
            tracked.PositionUpdatedUtc = nowUtc;
            tracked.DurationSeconds = durationSeconds;
            tracked.Progress = hasTimeline && durationSeconds > 0
                ? positionSeconds / durationSeconds
                : 0;

            if (timelineAvailabilityChanged || durationChanged)
            {
                tracked.LastActivityUtc = nowUtc;
            }

            return EvaluateStabilizationAfterWrite_NoLock(tracked, nowUtc);
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

            Logger.Debug($"Pending reconnect finalized for '{tracked.SessionKey}': Title='{tracked.PendingTitle}', Status={tracked.PendingPlaybackStatus}");

            bool hasChanges = false;
            if (tracked.Presence != MediaSessionPresence.Active)
            {
                tracked.Presence = MediaSessionPresence.Active;
                hasChanges = true;
            }

            if (tracked.MissingSinceUtc.HasValue)
            {
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
                tracked.PositionUpdatedUtc = nowUtc;
                tracked.DurationSeconds = tracked.PendingDurationSeconds;
                tracked.Progress = tracked.PendingHasTimeline && tracked.PendingDurationSeconds > 0
                    ? tracked.PendingPositionSeconds / tracked.PendingDurationSeconds
                    : 0;
                hasChanges = true;
            }

            tracked.LastActivityUtc = nowUtc;
            ResetPendingReconnect_NoLock(tracked);
            ClearTransportContinuationIfSatisfied_NoLock(tracked);
            if (!hasChanges)
            {
                return false;
            }
            return EvaluateStabilizationAfterWrite_NoLock(tracked, nowUtc);
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
            PruneExpiredTransportContinuation_NoLock(nowUtc);

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
                Logger.Debug($"Expired waiting source cleaned up: '{sessionKey}'");
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

        private void TryAbsorbTransportContinuationCandidate_NoLock(DateTimeOffset nowUtc, ref bool hasChanges)
        {
            if (!_pendingTransportContinuation.HasValue)
            {
                return;
            }

            PendingTransportContinuation pending = _pendingTransportContinuation.Value;
            if (pending.ExpiresAtUtc <= nowUtc)
            {
                _pendingTransportContinuation = null;
                return;
            }

            if (!_trackedSourcesByKey.TryGetValue(pending.SessionKey, out TrackedSource? target))
            {
                _pendingTransportContinuation = null;
                return;
            }

            if (!string.Equals(target.SourceAppId, pending.SourceAppId, StringComparison.Ordinal)
                || (!target.MissingSinceUtc.HasValue
                    && target.Presence != MediaSessionPresence.WaitingForReconnect
                    && !target.HasPendingReconnect))
            {
                return;
            }

            TrackedSource? candidate = _trackedSourcesByKey.Values
                .Where(source =>
                    !ReferenceEquals(source, target)
                    && source.Session != null
                    && source.Presence == MediaSessionPresence.Active
                    && string.Equals(source.SourceAppId, pending.SourceAppId, StringComparison.Ordinal)
                    && source.CreatedUtc >= pending.ArmedAtUtc)
                .OrderBy(source => GetTransportAdoptionPriority_NoLock(source))
                .ThenByDescending(source => source.CreatedUtc)
                .ThenByDescending(source => source.LastSeenUtc)
                .FirstOrDefault();
            if (candidate == null)
            {
                return;
            }

            MergeTransportCandidateIntoTarget_NoLock(target, candidate, nowUtc);
            Logger.Debug($"Transport continuation candidate absorbed: '{candidate.SessionKey}' -> '{target.SessionKey}'");
            hasChanges = true;
        }

        private void MergeTransportCandidateIntoTarget_NoLock(
            TrackedSource target,
            TrackedSource candidate,
            DateTimeOffset nowUtc)
        {
            GlobalSystemMediaTransportControlsSession? candidateSession = candidate.Session;
            if (candidateSession == null)
            {
                return;
            }

            _trackedSourcesBySession[candidateSession] = target;
            target.Session = candidateSession;
            target.SourceAppId = candidate.SourceAppId;
            target.SourceName = candidate.SourceName;
            target.LastSeenUtc = target.LastSeenUtc >= candidate.LastSeenUtc
                ? target.LastSeenUtc
                : candidate.LastSeenUtc;
            target.LastActivityUtc = target.LastActivityUtc >= candidate.LastActivityUtc
                ? target.LastActivityUtc
                : candidate.LastActivityUtc;
            target.LastDisplayedUtc = MaxDateTimeOffset(target.LastDisplayedUtc, candidate.LastDisplayedUtc);
            target.LastSystemCurrentUtc = MaxDateTimeOffset(target.LastSystemCurrentUtc, candidate.LastSystemCurrentUtc);
            target.IsSystemCurrent |= candidate.IsSystemCurrent;

            if (candidate.HasPendingReconnect || !HasConcreteMetadata(candidate.Title))
            {
                target.Presence = MediaSessionPresence.Active;
                target.HasPendingReconnect = true;
                target.PendingTitle = candidate.HasPendingReconnect ? candidate.PendingTitle : candidate.Title;
                target.PendingArtist = candidate.HasPendingReconnect ? candidate.PendingArtist : candidate.Artist;
                target.PendingPlaybackStatus = candidate.HasPendingReconnect ? candidate.PendingPlaybackStatus : candidate.PlaybackStatus;
                target.PendingHasTimeline = candidate.HasPendingReconnect ? candidate.PendingHasTimeline : candidate.HasTimeline;
                target.PendingPositionSeconds = candidate.HasPendingReconnect ? candidate.PendingPositionSeconds : candidate.CurrentPositionSeconds;
                target.PendingDurationSeconds = candidate.HasPendingReconnect ? candidate.PendingDurationSeconds : candidate.DurationSeconds;
                target.MissingSinceUtc ??= nowUtc;
                TryFinalizePendingReconnect_NoLock(target, nowUtc);
            }
            else
            {
                target.Presence = MediaSessionPresence.Active;
                target.MissingSinceUtc = null;
                ResetPendingReconnect_NoLock(target);
                target.Title = candidate.Title;
                target.Artist = candidate.Artist;
                target.PlaybackStatus = candidate.PlaybackStatus;
                target.HasTimeline = candidate.HasTimeline;
                target.CurrentPositionSeconds = candidate.CurrentPositionSeconds;
                target.PositionUpdatedUtc = candidate.PositionUpdatedUtc == default ? nowUtc : candidate.PositionUpdatedUtc;
                target.DurationSeconds = candidate.DurationSeconds;
                target.Progress = candidate.Progress;
                ClearTransportContinuationIfSatisfied_NoLock(target);
            }

            if (string.Equals(_systemCurrentSessionKey, candidate.SessionKey, StringComparison.Ordinal))
            {
                _systemCurrentSessionKey = target.SessionKey;
            }

            if (string.Equals(_displayedSessionKey, candidate.SessionKey, StringComparison.Ordinal))
            {
                _displayedSessionKey = target.SessionKey;
            }

            _trackedSourcesByKey.Remove(candidate.SessionKey);
        }

        private static int GetTransportAdoptionPriority_NoLock(TrackedSource source)
        {
            if (source.IsSystemCurrent && source.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return 0;
            }

            if (source.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return 1;
            }

            if (HasConcreteMetadata(source.Title))
            {
                return 2;
            }

            if (source.HasTimeline)
            {
                return 3;
            }

            return 4;
        }

        private static DateTimeOffset? MaxDateTimeOffset(DateTimeOffset? left, DateTimeOffset? right)
        {
            if (!left.HasValue)
            {
                return right;
            }

            if (!right.HasValue)
            {
                return left;
            }

            return left.Value >= right.Value
                ? left
                : right;
        }

        private void ArmTransportContinuation(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return;
            }

            lock (_gate)
            {
                if (_isDisposed
                    || !_trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked))
                {
                    return;
                }

                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                _pendingTransportContinuation = new PendingTransportContinuation(
                    tracked.SessionKey,
                    tracked.SourceAppId,
                    nowUtc,
                    nowUtc + _missingSourceGrace);
                Logger.Debug($"Transport continuation armed for '{tracked.SessionKey}' ({tracked.SourceAppId})");
            }
        }

        private bool TryGetPendingTransportTarget_NoLock(DateTimeOffset nowUtc, out TrackedSource tracked)
        {
            tracked = null!;
            if (!_pendingTransportContinuation.HasValue)
            {
                return false;
            }

            PendingTransportContinuation pending = _pendingTransportContinuation.Value;
            if (pending.ExpiresAtUtc <= nowUtc)
            {
                _pendingTransportContinuation = null;
                return false;
            }

            if (!_trackedSourcesByKey.TryGetValue(pending.SessionKey, out TrackedSource? resolvedTracked))
            {
                _pendingTransportContinuation = null;
                return false;
            }

            tracked = resolvedTracked;

            if (!string.Equals(tracked.SourceAppId, pending.SourceAppId, StringComparison.Ordinal))
            {
                _pendingTransportContinuation = null;
                tracked = null!;
                return false;
            }

            return true;
        }

        private void PruneExpiredTransportContinuation_NoLock(DateTimeOffset nowUtc)
        {
            if (_pendingTransportContinuation.HasValue
                && _pendingTransportContinuation.Value.ExpiresAtUtc <= nowUtc)
            {
                _pendingTransportContinuation = null;
            }
        }

        private void ClearTransportContinuationIfSatisfied_NoLock(TrackedSource tracked)
        {
            if (!_pendingTransportContinuation.HasValue)
            {
                return;
            }

            PendingTransportContinuation pending = _pendingTransportContinuation.Value;
            if (!string.Equals(pending.SessionKey, tracked.SessionKey, StringComparison.Ordinal))
            {
                return;
            }

            if (tracked.Presence == MediaSessionPresence.Active
                && !tracked.HasPendingReconnect
                && !tracked.MissingSinceUtc.HasValue
                && HasConcreteMetadata(tracked.Title))
            {
                _pendingTransportContinuation = null;
            }
        }


    }
}
