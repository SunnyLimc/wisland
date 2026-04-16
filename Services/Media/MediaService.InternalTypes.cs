using System;
using wisland.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace wisland.Services
{
    public sealed partial class MediaService
    {
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
                CreatedUtc = nowUtc;
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
            public DateTimeOffset CreatedUtc { get; }
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
            public IRandomAccessStreamReference? Thumbnail { get; set; }
            public bool HasPendingReconnect { get; set; }
            public string PendingTitle { get; set; }
            public string PendingArtist { get; set; }
            public GlobalSystemMediaTransportControlsSessionPlaybackStatus PendingPlaybackStatus { get; set; }
            public bool PendingHasTimeline { get; set; }
            public double PendingPositionSeconds { get; set; }
            public double PendingDurationSeconds { get; set; }

            // Stabilization state machine. When StabilizationReason != None, snapshots
            // emitted for this source are frozen at FrozenSnapshot instead of reflecting
            // the latest raw fields. Raw fields continue to be updated as GSMTC events
            // arrive, but emission to subscribers is suppressed until the gate releases.
            public MediaSessionStabilizationReason StabilizationReason { get; set; }
            public DateTimeOffset StabilizationArmedAtUtc { get; set; }
            public DateTimeOffset StabilizationExpiresAtUtc { get; set; }
            public string StabilizationBaselineTitle { get; set; } = string.Empty;
            public string StabilizationBaselineArtist { get; set; } = string.Empty;
            public MediaSessionSnapshot FrozenSnapshot { get; set; }
        }

        private readonly record struct PrefetchedSessionState(
            string Title,
            string Artist,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus,
            bool HasTimeline,
            double PositionSeconds,
            double DurationSeconds,
            IRandomAccessStreamReference? Thumbnail)
        {
            public static readonly PrefetchedSessionState Empty = new(
                UnknownTrackTitle,
                UnknownArtistName,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed,
                HasTimeline: false,
                PositionSeconds: 0,
                DurationSeconds: 0,
                Thumbnail: null);

            public bool HasConcreteMetadata => MediaService.HasConcreteMetadata(Title);
        }

        private readonly record struct ServiceChangeResult(
            bool ShouldNotifySessions,
            bool ShouldNotifyTrack,
            string TrackTitle,
            string TrackArtist);

        private readonly record struct PendingSourceMatch(
            TrackedSource Tracked,
            bool ProvisionalReconnect);

        private readonly record struct PendingContinuationCandidate(
            GlobalSystemMediaTransportControlsSession Session,
            TrackedSource Source,
            double Score);

        private readonly record struct PendingTransportContinuation(
            string SessionKey,
            string SourceAppId,
            DateTimeOffset ArmedAtUtc,
            DateTimeOffset ExpiresAtUtc);

        private readonly record struct TransportContinuationCandidate(
            GlobalSystemMediaTransportControlsSession Session,
            int Score);    }
}