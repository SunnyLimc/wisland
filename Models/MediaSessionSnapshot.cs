using System;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace wisland.Models
{
    /// <summary>
    /// Immutable UI-facing snapshot for a single GSMTC session.
    /// </summary>
    public readonly record struct MediaSessionSnapshot(
        string SessionKey,
        string SourceAppId,
        string SourceName,
        string Title,
        string Artist,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus,
        double Progress,
        bool HasTimeline,
        double DurationSeconds,
        bool IsSystemCurrent,
        DateTimeOffset LastActivityUtc,
        MediaSessionPresence Presence,
        DateTimeOffset LastSeenUtc,
        DateTimeOffset? MissingSinceUtc,
        MediaSessionStabilizationReason StabilizationReason = MediaSessionStabilizationReason.None,
        IRandomAccessStreamReference? Thumbnail = null,
        string ThumbnailHash = "")
    {
        public bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        public bool IsWaitingForReconnect => Presence == MediaSessionPresence.WaitingForReconnect;
        public bool IsStabilizing => StabilizationReason != MediaSessionStabilizationReason.None;
    }
}
