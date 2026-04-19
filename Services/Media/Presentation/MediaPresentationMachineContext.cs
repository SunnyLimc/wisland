using System;
using System.Collections.Generic;
using wisland.Models;

namespace wisland.Services.Media.Presentation
{
    /// <summary>
    /// Shared mutable context threaded through all policies during event handling.
    /// Only the machine and policies read/write this; never exposed externally.
    /// </summary>
    public sealed class MediaPresentationMachineContext
    {
        public DateTimeOffset NowUtc { get; internal set; }

        public IReadOnlyList<MediaSessionSnapshot> Sessions { get; internal set; }
            = Array.Empty<MediaSessionSnapshot>();

        /// <summary>Currently displayed session key as tracked by the machine.
        /// Set by the machine before dispatching each event so policies can
        /// reason about "what is on screen right now".</summary>
        public string? CurrentDisplayedSessionKey { get; internal set; }

        // --- ManualSelectionLockPolicy output ---
        public string? ManualLockedSessionKey { get; internal set; }
        public bool HasManualLock { get; internal set; }

        // --- FocusArbitrationPolicy output ---
        /// <summary>Session key the arbiter wants to display. Null if no winner.</summary>
        public string? ArbitratedWinnerKey { get; internal set; }
        /// <summary>Debounce deadline before the arbiter will auto-switch. Null if no pending switch.</summary>
        public DateTimeOffset? AutoSwitchDueUtc { get; internal set; }

        // --- StabilizationPolicy output (P3 will populate fully; P2 leaves default) ---
        public StabilizationDirective StabilizationDirective { get; internal set; }

        // --- AiOverridePolicy output ---
        /// <summary>Override for the arbiter's current winner. Kept for policy
        /// reads that need "what override applies to the logical winner". The
        /// frame emission path prefers <see cref="AiOverrideLookup"/> so the
        /// frame's session and its override stay matched during Confirming
        /// (when the frame still shows the previous session).</summary>
        public AiOverrideSnapshot? ActiveAiOverride { get; internal set; }

        /// <summary>Lookup that returns the cached AI override for an arbitrary
        /// snapshot. Populated by <c>AiOverridePolicy</c> on attach; consumed
        /// by <c>MediaPresentationMachine.EmitFrame</c> to attach the override
        /// matching the session actually being emitted. Keying by session key
        /// alone is insufficient because a single session transitions through
        /// distinct tracks (same key, different title/artist), and during
        /// Confirming the emitted snapshot is the previous track while the
        /// context's winner is the next one.</summary>
        public Func<MediaSessionSnapshot, AiOverrideSnapshot?>? AiOverrideLookup { get; internal set; }

        // --- NotificationOverlayPolicy output ---
        public NotificationPayload? ActiveNotification { get; internal set; }

        // --- Machine-provided scheduling hooks (wired by machine on attach) ---
        internal Action<DateTimeOffset>? ScheduleStabilizationTimerCallback { get; set; }
        internal Action<DateTimeOffset>? ScheduleMetadataSettleTimerCallback { get; set; }
        internal Action<DateTimeOffset?>? ScheduleAutoFocusTimerCallback { get; set; }
        internal Action<DateTimeOffset?>? ScheduleManualLockExpiryTimerCallback { get; set; }

        public void ScheduleStabilizationTimer(DateTimeOffset dueUtc)
            => ScheduleStabilizationTimerCallback?.Invoke(dueUtc);

        public void ScheduleMetadataSettleTimer(DateTimeOffset dueUtc)
            => ScheduleMetadataSettleTimerCallback?.Invoke(dueUtc);

        public void ScheduleAutoFocusTimer(DateTimeOffset? dueUtc)
            => ScheduleAutoFocusTimerCallback?.Invoke(dueUtc);

        public void ScheduleManualLockExpiryTimer(DateTimeOffset? dueUtc)
            => ScheduleManualLockExpiryTimerCallback?.Invoke(dueUtc);
    }

    /// <summary>
    /// Directive produced by <c>StabilizationPolicy</c> describing the current
    /// stabilization state (if any). Consumed by the machine to drive Pending*
    /// and Confirming states.
    /// </summary>
    public readonly record struct StabilizationDirective(
        MediaSessionStabilizationReason Reason,
        DateTimeOffset ExpiresAtUtc,
        MediaSessionSnapshot? FrozenSnapshot)
    {
        public static StabilizationDirective None { get; } = default;
        public bool IsActive => Reason != MediaSessionStabilizationReason.None;
    }
}
