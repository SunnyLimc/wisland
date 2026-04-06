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
                    Logger.Info($"Track changed for system current '{tracked.SessionKey}': '{trackTitle}' by '{trackArtist}'");
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
                Logger.Debug($"Dispatching session change: {_sessions.Count} session(s)");
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
                tracked.DurationSeconds,
                tracked.IsSystemCurrent,
                tracked.LastActivityUtc,
                tracked.Presence,
                tracked.LastSeenUtc,
                tracked.MissingSinceUtc);

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
