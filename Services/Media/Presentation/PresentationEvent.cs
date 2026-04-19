using System.Collections.Generic;
using wisland.Models;
using AiSongResult = wisland.Models.AiSongResult;

namespace wisland.Services.Media.Presentation
{
    /// <summary>
    /// Base type for all events fed into <see cref="MediaPresentationMachine"/>.
    /// Events are processed sequentially on the machine's worker loop; no event
    /// handler runs concurrently with another.
    /// </summary>
    public abstract record PresentationEvent;

    public sealed record GsmtcSessionsChangedEvent(
        IReadOnlyList<MediaSessionSnapshot> Sessions) : PresentationEvent;

    public sealed record UserSkipRequestedEvent(
        ContentTransitionDirection Direction) : PresentationEvent;

    public sealed record UserSelectSessionEvent(
        string SessionKey,
        ContentTransitionDirection Direction) : PresentationEvent;

    public sealed record UserManualUnlockEvent : PresentationEvent;

    public sealed record AutoFocusTimerFiredEvent : PresentationEvent;

    public sealed record StabilizationTimerFiredEvent : PresentationEvent;

    public sealed record MetadataSettleTimerFiredEvent : PresentationEvent;

    public sealed record AiResolveCompletedEvent(
        string SourceAppId,
        string Title,
        string Artist,
        AiSongResult? Result) : PresentationEvent;

    public sealed record NotificationBeginEvent(NotificationPayload Payload) : PresentationEvent;

    public sealed record NotificationEndEvent : PresentationEvent;

    public sealed record SettingsChangedEvent(SettingsChangeScope Scope) : PresentationEvent;

    public enum SettingsChangeScope
    {
        AiOverride,
        Language,
        ImmersiveMode,
        Other
    }
}
