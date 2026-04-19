using System;
using wisland.Models;

namespace wisland.Services.Media.Presentation.Policies
{
    /// <summary>
    /// Replaces <c>_selectedSessionKey / _selectionLockUntilUtc</c> in
    /// MainWindow.Media. When the user explicitly picks a session or skips,
    /// the lock holds focus on that session for
    /// <see cref="IslandConfig.SelectionLockDurationMs"/> so the auto-arbiter
    /// cannot steal focus mid-transition.
    /// </summary>
    public sealed class ManualSelectionLockPolicy : IPresentationPolicy
    {
        private readonly TimeSpan _lockDuration;
        private string? _lockedKey;
        private DateTimeOffset _expiresAtUtc;

        public ManualSelectionLockPolicy()
            : this(TimeSpan.FromMilliseconds(IslandConfig.SelectionLockDurationMs))
        {
        }

        public ManualSelectionLockPolicy(TimeSpan lockDuration)
        {
            _lockDuration = lockDuration;
        }

        public void OnAttach(MediaPresentationMachine machine) { }

        public void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context)
        {
            switch (evt)
            {
                case UserSelectSessionEvent select:
                    _lockedKey = select.SessionKey;
                    _expiresAtUtc = context.NowUtc + _lockDuration;
                    break;
                case UserSkipRequestedEvent:
                    // Keep the currently displayed session locked during a skip so
                    // the arbiter doesn't steal focus to a different tab.
                    if (!string.IsNullOrEmpty(context.CurrentDisplayedSessionKey))
                    {
                        _lockedKey = context.CurrentDisplayedSessionKey;
                        _expiresAtUtc = context.NowUtc + _lockDuration;
                    }
                    break;
                case UserManualUnlockEvent:
                    _lockedKey = null;
                    _expiresAtUtc = default;
                    break;
            }

            PublishLockState(context);
        }

        public void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context)
        {
            PublishLockState(context);
        }

        private void PublishLockState(MediaPresentationMachineContext context)
        {
            if (_lockedKey != null && context.NowUtc >= _expiresAtUtc)
            {
                _lockedKey = null;
                _expiresAtUtc = default;
            }

            // Guard: if the locked session is no longer present in the
            // GSMTC list, drop the lock. Replaces MainWindow's manual
            // cleanup branches in ResolveDisplayedMediaContext (P4c-2b).
            if (_lockedKey != null)
            {
                bool stillPresent = false;
                for (int i = 0; i < context.Sessions.Count; i++)
                {
                    if (string.Equals(context.Sessions[i].SessionKey, _lockedKey, StringComparison.Ordinal))
                    {
                        stillPresent = true;
                        break;
                    }
                }
                if (!stillPresent)
                {
                    _lockedKey = null;
                    _expiresAtUtc = default;
                }
            }

            context.ManualLockedSessionKey = _lockedKey;
            context.HasManualLock = _lockedKey != null;
            context.ScheduleManualLockExpiryTimer(_lockedKey != null ? _expiresAtUtc : (DateTimeOffset?)null);
        }
    }
}
