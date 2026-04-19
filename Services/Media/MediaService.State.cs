using System;
using System.Linq;
using System.Threading;
using wisland.Helpers;
using wisland.Models;
using Windows.Media.Control;

namespace wisland.Services
{
    public sealed partial class MediaService
    {
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

            // Use the just-built snapshot (which respects the stabilization
            // freeze) for track-change detection instead of the raw tracked
            // fields.  Raw fields are written even while the stabilization gate
            // is closed (e.g. Chrome briefly reporting another tab's paused
            // media during a skip transition), so referencing them here would
            // fire a false TrackChanged notification with the wrong metadata
            // and corrupt _lastSystemCurrentTrackSignature.
            MediaSessionSnapshot currentSnapshot = default;
            bool foundCurrent = false;
            if (_systemCurrentSessionKey != null)
            {
                foreach (MediaSessionSnapshot s in _sessions)
                {
                    if (string.Equals(s.SessionKey, _systemCurrentSessionKey, StringComparison.Ordinal))
                    {
                        currentSnapshot = s;
                        foundCurrent = true;
                        break;
                    }
                }
            }

            if (foundCurrent
                && currentSnapshot.Presence == MediaSessionPresence.Active
                && HasConcreteMetadata(currentSnapshot.Title))
            {
                string signature = CreateTrackSignature(currentSnapshot.Title, currentSnapshot.Artist);
                if (_lastSystemCurrentTrackSignature != null
                    && !string.Equals(_lastSystemCurrentTrackSignature, signature, StringComparison.Ordinal))
                {
                    shouldNotifyTrack = true;
                    trackTitle = currentSnapshot.Title;
                    trackArtist = currentSnapshot.Artist;
                    Logger.Info($"Track changed for system current '{currentSnapshot.SessionKey}': '{trackTitle}' by '{trackArtist}'");
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
                    source.MissingSinceUtc.HasValue
                    && (source.Presence == MediaSessionPresence.WaitingForReconnect || source.HasPendingReconnect))
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
                Logger.Trace($"Dispatching session change: {_sessions.Count} session(s)");
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
        {
            if (tracked.StabilizationReason != MediaSessionStabilizationReason.None)
            {
                // Emit the frozen pre-transition snapshot, but keep a few identity/
                // presence fields synced with the live source so downstream consumers
                // (focus arbiter, session picker) treat it as the same live session.
                MediaSessionSnapshot frozen = tracked.FrozenSnapshot;
                return frozen with
                {
                    IsSystemCurrent = tracked.IsSystemCurrent,
                    LastActivityUtc = tracked.LastActivityUtc,
                    LastSeenUtc = tracked.LastSeenUtc,
                    Presence = tracked.Presence,
                    MissingSinceUtc = tracked.MissingSinceUtc,
                    StabilizationReason = tracked.StabilizationReason
                };
            }

            return CreateRawSnapshot(tracked);
        }

        private static string CreateTrackSignature(string title, string artist)
            => string.Concat(title, "\u001f", artist);

        private static bool HasConcreteMetadata(string title)
            => !string.Equals(title, UnknownTrackTitle, StringComparison.Ordinal);

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
    }
}
