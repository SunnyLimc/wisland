using System;
using wisland.Models;

namespace wisland.Services.Media.Presentation
{
    /// <summary>
    /// A pending directional switch intent. Survives across status-only events,
    /// session list changes, and notification overlays. Only consumed when the
    /// actually displayed track fingerprint changes away from <see cref="Origin"/>.
    /// </summary>
    public readonly record struct SwitchIntent(
        MediaTrackFingerprint Origin,
        ContentTransitionDirection Direction,
        DateTimeOffset DeadlineUtc)
    {
        public bool IsExpired(DateTimeOffset nowUtc) => nowUtc > DeadlineUtc;

        public bool MatchesOrigin(MediaTrackFingerprint current)
            => Origin.SessionKey == current.SessionKey
            && Origin.Title == current.Title
            && Origin.Artist == current.Artist;
    }
}
