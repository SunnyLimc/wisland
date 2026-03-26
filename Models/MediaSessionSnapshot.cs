using System;
using Windows.Media.Control;

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
        bool IsSystemCurrent,
        DateTimeOffset LastActivityUtc)
    {
        public bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }
}
