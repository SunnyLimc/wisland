using System;
using System.Collections.Generic;

namespace wisland.Models
{
    /// <summary>
    /// Kind of content currently being presented. Independent of track identity.
    /// Drives UI hint text (e.g. "Switching", "Confirming") but NEVER participates
    /// in fingerprint comparison — a Kind change alone must not trigger a directional
    /// slide animation.
    /// </summary>
    public enum PresentationKind
    {
        Empty,
        Steady,
        Switching,
        Confirming,
        Missing,
        Notifying
    }

    /// <summary>
    /// Kind of transition animation the view should run when consuming a frame.
    /// Decided by the presentation state machine, not by diffing fingerprints in the view.
    /// </summary>
    public enum FrameTransitionKind
    {
        None,
        Replace,
        SlideForward,
        SlideBackward,
        Crossfade,
        ResumeAfterNotification
    }

    /// <summary>
    /// Content identity of the currently presented track. Used by the state machine
    /// and the view layer to decide whether two frames represent the same song.
    /// Only four dimensions: session key, title, artist, thumbnail hash.
    /// Playback status, stabilization flags, presence etc. are NOT part of this.
    /// </summary>
    public readonly record struct MediaTrackFingerprint(
        string SessionKey,
        string Title,
        string Artist,
        string ThumbnailHash)
    {
        public static MediaTrackFingerprint Empty { get; } =
            new(string.Empty, string.Empty, string.Empty, string.Empty);

        public static MediaTrackFingerprint From(MediaSessionSnapshot session, string thumbnailHash)
            => new(session.SessionKey, session.Title, session.Artist, thumbnailHash);

        public bool IsEmpty
            => string.IsNullOrEmpty(SessionKey)
            && string.IsNullOrEmpty(Title)
            && string.IsNullOrEmpty(Artist);
    }

    public sealed record AiOverrideSnapshot(string Title, string Artist);

    public sealed record NotificationPayload(
        string Title,
        string Message,
        string Header,
        int DurationMs);

    /// <summary>
    /// Single unit of output from the MediaPresentationMachine. Views react to
    /// frames directly; they must never inspect MediaService state.
    /// </summary>
    public sealed record MediaPresentationFrame(
        long Sequence,
        MediaSessionSnapshot? Session,
        IReadOnlyList<MediaSessionSnapshot> OrderedSessions,
        int DisplayIndex,
        PresentationKind Kind,
        FrameTransitionKind Transition,
        MediaTrackFingerprint Fingerprint,
        MediaTrackFingerprint? ProgressFingerprint,
        bool IsFallback,
        bool ThumbnailHashIsFallback,
        AiOverrideSnapshot? AiOverride,
        NotificationPayload? Notification);
}
