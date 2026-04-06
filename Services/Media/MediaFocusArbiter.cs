using System;
using System.Collections.Generic;
using System.Linq;
using wisland.Helpers;
using wisland.Models;
using Windows.Media.Control;

namespace wisland.Services
{
    public sealed class MediaFocusArbiter
    {
        private readonly TimeSpan _autoSwitchDebounce;
        private readonly TimeSpan _missingSourceGrace;
        private string? _pendingAutoWinnerKey;
        private DateTimeOffset? _pendingAutoWinnerSinceUtc;

        public MediaFocusArbiter(TimeSpan autoSwitchDebounce, TimeSpan missingSourceGrace)
        {
            _autoSwitchDebounce = autoSwitchDebounce;
            _missingSourceGrace = missingSourceGrace;
        }

        public MediaFocusDecision Resolve(
            IReadOnlyList<MediaSessionSnapshot> sessions,
            string? currentDisplayedKey,
            string? manualLockedKey,
            bool hasManualLock,
            DateTimeOffset nowUtc)
        {
            Logger.Trace($"Focus arbiter: resolving with {sessions.Count} session(s), displayed='{currentDisplayedKey}', manualLock={hasManualLock} ('{manualLockedKey}')");

            MediaSessionSnapshot? manualLockedSession = FindSession(sessions, manualLockedKey);
            if (hasManualLock && manualLockedSession.HasValue)
            {
                ClearPendingAutoWinner();
                Logger.Trace($"Focus arbiter: manual lock active, keeping '{manualLockedKey}'");
                return new MediaFocusDecision(
                    manualLockedSession.Value,
                    PendingAutoWinnerKey: null,
                    PendingAutoSwitchDueUtc: null);
            }

            MediaSessionSnapshot? currentDisplayedSession = FindSession(sessions, currentDisplayedKey);
            MediaSessionSnapshot? autoWinner = SelectAutoWinner(sessions);
            UpdatePendingAutoWinner(autoWinner?.SessionKey, nowUtc);

            if (currentDisplayedSession.HasValue
                && IsWithinMissingGrace(currentDisplayedSession.Value, nowUtc))
            {
                DateTimeOffset? waitingSwitchDueUtc = GetPendingAutoSwitchDueUtc();
                return new MediaFocusDecision(
                    currentDisplayedSession.Value,
                    _pendingAutoWinnerKey,
                    waitingSwitchDueUtc.HasValue && waitingSwitchDueUtc.Value > nowUtc
                        ? waitingSwitchDueUtc
                        : null);
            }

            if (!autoWinner.HasValue)
            {
                ClearPendingAutoWinner();
                return new MediaFocusDecision(
                    currentDisplayedSession,
                    PendingAutoWinnerKey: null,
                    PendingAutoSwitchDueUtc: null);
            }

            if (!currentDisplayedSession.HasValue)
            {
                ClearPendingAutoWinner();
                return new MediaFocusDecision(
                    autoWinner.Value,
                    PendingAutoWinnerKey: null,
                    PendingAutoSwitchDueUtc: null);
            }

            if (string.Equals(currentDisplayedSession.Value.SessionKey, autoWinner.Value.SessionKey, StringComparison.Ordinal))
            {
                ClearPendingAutoWinner();
                return new MediaFocusDecision(
                    currentDisplayedSession.Value,
                    PendingAutoWinnerKey: null,
                    PendingAutoSwitchDueUtc: null);
            }

            DateTimeOffset? pendingSwitchDueUtc = GetPendingAutoSwitchDueUtc();
            if (pendingSwitchDueUtc.HasValue && pendingSwitchDueUtc.Value <= nowUtc)
            {
                Logger.Debug($"Auto-switch debounce expired, switching to '{autoWinner.Value.SessionKey}' ({autoWinner.Value.SourceName})");
                ClearPendingAutoWinner();
                return new MediaFocusDecision(
                    autoWinner.Value,
                    PendingAutoWinnerKey: null,
                    PendingAutoSwitchDueUtc: null);
            }

            return new MediaFocusDecision(
                currentDisplayedSession.Value,
                _pendingAutoWinnerKey,
                pendingSwitchDueUtc);
        }

        private static MediaSessionSnapshot? SelectAutoWinner(IReadOnlyList<MediaSessionSnapshot> sessions)
        {
            var winner = sessions
                .Where(session => !session.IsWaitingForReconnect)
                .OrderBy(session => GetPriorityRank(session))
                .ThenByDescending(session => session.LastActivityUtc)
                .ThenBy(session => session.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.SessionKey, StringComparer.Ordinal)
                .Cast<MediaSessionSnapshot?>()
                .FirstOrDefault();
            Logger.Trace($"Focus arbiter: auto winner = '{winner?.SessionKey}' ({winner?.SourceName}), status={winner?.PlaybackStatus}");
            return winner;
        }

        private static int GetPriorityRank(MediaSessionSnapshot session)
            => session.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing when session.IsSystemCurrent => 0,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 1,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused when session.IsSystemCurrent => 2,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 3,
                _ => 4
            };

        private bool IsWithinMissingGrace(MediaSessionSnapshot session, DateTimeOffset nowUtc)
            => session.MissingSinceUtc.HasValue
                && (nowUtc - session.MissingSinceUtc.Value) < _missingSourceGrace;

        private void UpdatePendingAutoWinner(string? winnerKey, DateTimeOffset nowUtc)
        {
            if (string.IsNullOrWhiteSpace(winnerKey))
            {
                ClearPendingAutoWinner();
                return;
            }

            if (string.Equals(_pendingAutoWinnerKey, winnerKey, StringComparison.Ordinal))
            {
                return;
            }

            Logger.Debug($"Auto-switch debounce started for '{winnerKey}'");
            _pendingAutoWinnerKey = winnerKey;
            _pendingAutoWinnerSinceUtc = nowUtc;
        }

        private DateTimeOffset? GetPendingAutoSwitchDueUtc()
            => _pendingAutoWinnerKey != null && _pendingAutoWinnerSinceUtc.HasValue
                ? _pendingAutoWinnerSinceUtc.Value + _autoSwitchDebounce
                : null;

        private void ClearPendingAutoWinner()
        {
            _pendingAutoWinnerKey = null;
            _pendingAutoWinnerSinceUtc = null;
        }

        private static MediaSessionSnapshot? FindSession(IReadOnlyList<MediaSessionSnapshot> sessions, string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return null;
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                if (string.Equals(sessions[i].SessionKey, sessionKey, StringComparison.Ordinal))
                {
                    return sessions[i];
                }
            }

            return null;
        }
    }

    public readonly record struct MediaFocusDecision(
        MediaSessionSnapshot? DisplayedSession,
        string? PendingAutoWinnerKey,
        DateTimeOffset? PendingAutoSwitchDueUtc);
}
