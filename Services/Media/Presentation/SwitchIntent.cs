using System;
using wisland.Models;

namespace wisland.Services.Media.Presentation
{
    /// <summary>
    /// Where a <see cref="SwitchIntent"/> originated. Used by the machine to
    /// decide whether the intent should force a Confirming settle on the next
    /// fingerprint change (<see cref="Skip"/>) or fire an immediate transition
    /// (<see cref="SessionSelect"/>), which has no Chrome paused-tab hazard
    /// because the target session was picked from the visible list.
    /// </summary>
    public enum SwitchIntentSource
    {
        Skip,
        SessionSelect
    }

    /// <summary>
    /// A pending directional switch intent. Survives across status-only events,
    /// session list changes, and notification overlays. Only consumed when the
    /// actually displayed track fingerprint changes away from <see cref="Origin"/>.
    /// </summary>
    public readonly record struct SwitchIntent(
        MediaTrackFingerprint Origin,
        ContentTransitionDirection Direction,
        DateTimeOffset DeadlineUtc,
        SwitchIntentSource Source = SwitchIntentSource.Skip)
    {
        public bool IsExpired(DateTimeOffset nowUtc) => nowUtc > DeadlineUtc;

        public bool MatchesOrigin(MediaTrackFingerprint current)
            => Origin.SessionKey == current.SessionKey
            && Origin.Title == current.Title
            && Origin.Artist == current.Artist;
    }
}

