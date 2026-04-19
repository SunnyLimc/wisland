using System;
using wisland.Models;

namespace wisland.Services.Media.Presentation.Policies
{
    /// <summary>
    /// Wraps <see cref="wisland.Services.MediaFocusArbiter"/> so the machine
    /// can ask "which session should be displayed right now" without reaching
    /// back into MediaService.
    ///
    /// Reads: <see cref="MediaPresentationMachineContext.Sessions"/>,
    /// <see cref="MediaPresentationMachineContext.CurrentDisplayedSessionKey"/>,
    /// <see cref="MediaPresentationMachineContext.ManualLockedSessionKey"/>,
    /// <see cref="MediaPresentationMachineContext.HasManualLock"/>.
    /// Writes: <see cref="MediaPresentationMachineContext.ArbitratedWinnerKey"/>,
    /// <see cref="MediaPresentationMachineContext.AutoSwitchDueUtc"/>.
    /// </summary>
    public sealed class FocusArbitrationPolicy : IPresentationPolicy
    {
        private readonly wisland.Services.MediaFocusArbiter _arbiter;

        public FocusArbitrationPolicy()
            : this(TimeSpan.FromMilliseconds(IslandConfig.MediaAutoSwitchDebounceMs),
                   TimeSpan.FromMilliseconds(IslandConfig.MediaMissingGraceMs))
        {
        }

        public FocusArbitrationPolicy(TimeSpan autoSwitchDebounce, TimeSpan missingSourceGrace)
        {
            _arbiter = new wisland.Services.MediaFocusArbiter(autoSwitchDebounce, missingSourceGrace);
        }

        public void OnAttach(MediaPresentationMachine machine) { }

        public void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context)
        {
            // Arbitrate on every session change and on timer fires. User
            // selection events are handled by ManualSelectionLockPolicy; the
            // arbiter will honor the manual lock set by that policy.
            if (evt is GsmtcSessionsChangedEvent
                || evt is AutoFocusTimerFiredEvent
                || evt is UserSelectSessionEvent
                || evt is UserManualUnlockEvent)
            {
                Resolve(context);
            }
        }

        public void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context)
        {
            Resolve(context);
        }

        private void Resolve(MediaPresentationMachineContext context)
        {
            var decision = _arbiter.Resolve(
                context.Sessions,
                context.CurrentDisplayedSessionKey,
                context.ManualLockedSessionKey,
                context.HasManualLock,
                context.NowUtc);

            context.ArbitratedWinnerKey = decision.DisplayedSession?.SessionKey;
            context.AutoSwitchDueUtc = decision.PendingAutoSwitchDueUtc;
            context.ScheduleAutoFocusTimer(decision.PendingAutoSwitchDueUtc);
        }
    }
}
