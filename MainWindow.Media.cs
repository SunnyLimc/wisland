using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using wisland.Models;
using wisland.Services;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private readonly MediaFocusArbiter _focusArbiter = new(
            TimeSpan.FromMilliseconds(IslandConfig.MediaAutoSwitchDebounceMs),
            TimeSpan.FromMilliseconds(IslandConfig.MediaMissingGraceMs));
        private ContentTransitionDirection _pendingMediaTransitionDirection = ContentTransitionDirection.None;
        private long _pendingMediaTransitionTimestamp;
        private string? _selectedSessionKey;
        private string? _displayedSessionKey;
        private DateTimeOffset? _selectionLockUntilUtc;
        private string? _lastDisplayedContentIdentity;
        private string? _lastDisplayedProgressIdentity;
        private MediaSessionSnapshot? _transportWaitingSnapshot;
        private DateTimeOffset? _transportWaitingStartedUtc;
        private DateTimeOffset? _transportWaitingUntilUtc;
        private readonly List<string> _sessionVisualOrderKeys = new();

        private async Task InitializeMediaAsync()
        {
            _mediaService.SessionsChanged += OnMediaServiceChanged;
            _mediaService.TrackChanged += OnTrackChanged;
            await _mediaService.InitializeAsync();
            SyncMediaUI();
            UpdateRenderLoopState();
        }

        private void OnMediaServiceChanged()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                SyncMediaUI();
                UpdateRenderLoopState();
            });
        }

        private void OnTrackChanged(string title, string artist)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_controller.IsDocked && !_controller.IsHovered && !_controller.IsDragging)
                {
                    ShowNotification(title, artist, IslandConfig.TrackChangeNotificationDurationMs, "New Track");
                }
            });
        }

        private void SyncMediaUI()
        {
            DisplayedMediaContext context = ResolveDisplayedMediaContext();
            string? nextContentIdentity = CreateContentIdentity(context.DisplayedSession);
            string? nextProgressIdentity = CreateProgressIdentity(context.DisplayedSession);
            bool contentChanged = !string.Equals(_lastDisplayedContentIdentity, nextContentIdentity, StringComparison.Ordinal);
            bool progressSourceChanged = !string.Equals(_lastDisplayedProgressIdentity, nextProgressIdentity, StringComparison.Ordinal);
            ContentTransitionDirection directionHint = contentChanged
                ? GetPendingMediaTransitionDirection()
                : ContentTransitionDirection.None;

            _displayedSessionKey = context.DisplayedSession?.SessionKey;
            _mediaService.SetDisplayedSessionKey(_displayedSessionKey);

            if (progressSourceChanged)
            {
                RequestMediaProgressReset(!context.DisplayedSession.HasValue || !context.DisplayedSession.Value.HasTimeline);
                _lastDisplayedProgressIdentity = nextProgressIdentity;
            }

            CompactContent.Update(context.CompactText, directionHint);
            CompactContent.SetSessionCountHint(context.SessionCountText, context.ShowSessionCount);

            if (!_controller.IsNotifying)
            {
                ExpandedContent.UpdateMedia(
                    context.DisplayedSession,
                    context.DisplayIndex,
                    context.OrderedSessions.Count,
                    context.OrderedSessions,
                    directionHint);
            }

            if (contentChanged)
            {
                _lastDisplayedContentIdentity = nextContentIdentity;
                ClearPendingMediaTransitionDirection();
            }
        }

        private MediaSessionSnapshot? GetDisplayedMediaSessionSnapshot()
            => _mediaService.GetSessionSnapshot(_displayedSessionKey);

        private IReadOnlyList<MediaSessionSnapshot> GetPriorityOrderedSessions()
        {
            List<MediaSessionSnapshot> sessions = _mediaService.Sessions.ToList();
            MediaSessionSnapshot? transportWaiting = GetTransportWaitingFallbackSnapshot(sessions);
            if (transportWaiting.HasValue)
            {
                sessions.Add(transportWaiting.Value);
            }

            return sessions
                .OrderBy(session => GetSessionPriorityRank(session))
                .ThenByDescending(session => session.LastActivityUtc)
                .ThenBy(session => session.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.SessionKey, StringComparer.Ordinal)
                .ToArray();
        }

        private DisplayedMediaContext ResolveDisplayedMediaContext()
        {
            IReadOnlyList<MediaSessionSnapshot> prioritySessions = GetPriorityOrderedSessions();
            if (prioritySessions.Count == 0)
            {
                ClearManualSelectionLockInternal();
                _sessionVisualOrderKeys.Clear();
                SyncAutoFocusTimer(null);
                return new DisplayedMediaContext(
                    DisplayedSession: null,
                    OrderedSessions: prioritySessions,
                    DisplayIndex: -1,
                    CompactText: "Wisland",
                    SessionCountText: string.Empty,
                    ShowSessionCount: false);
            }

            bool hasManualLock = IsManualSelectionLocked();
            if (!hasManualLock && (_selectionLockUntilUtc.HasValue || !string.IsNullOrWhiteSpace(_selectedSessionKey)))
            {
                ClearManualSelectionLockInternal();
            }

            if (!string.IsNullOrWhiteSpace(_selectedSessionKey)
                && !prioritySessions.Any(session => string.Equals(session.SessionKey, _selectedSessionKey, StringComparison.Ordinal)))
            {
                ClearManualSelectionLockInternal();
                hasManualLock = false;
            }

            MediaFocusDecision focusDecision = _focusArbiter.Resolve(
                prioritySessions,
                _displayedSessionKey,
                _selectedSessionKey,
                hasManualLock,
                DateTimeOffset.UtcNow);
            SyncAutoFocusTimer(focusDecision.PendingAutoSwitchDueUtc);

            MediaSessionSnapshot displayedSession = focusDecision.DisplayedSession ?? prioritySessions[0];
            IReadOnlyList<MediaSessionSnapshot> orderedSessions = GetVisualOrderedSessions(prioritySessions);
            int displayIndex = FindSessionIndex(orderedSessions, displayedSession.SessionKey);
            if (displayIndex < 0)
            {
                orderedSessions = prioritySessions;
                displayIndex = FindSessionIndex(orderedSessions, displayedSession.SessionKey);
            }

            string sessionCountText = orderedSessions.Count > 1
                ? FormattableString.Invariant($"{displayIndex + 1}/{orderedSessions.Count}")
                : string.Empty;

            return new DisplayedMediaContext(
                DisplayedSession: displayedSession,
                OrderedSessions: orderedSessions,
                DisplayIndex: displayIndex,
                CompactText: displayedSession.Title,
                SessionCountText: sessionCountText,
                ShowSessionCount: orderedSessions.Count >= IslandConfig.CompactSessionCountVisibleThreshold);
        }

        private async void PlayPause_Click(object? sender, EventArgs e)
            => await _mediaService.PlayPauseAsync(_displayedSessionKey);

        private async void SkipNext_Click(object? sender, EventArgs e)
        {
            RegisterTransportWaitingFallback();
            RegisterPendingMediaTransitionDirection(ContentTransitionDirection.Forward);
            await _mediaService.SkipNextAsync(_displayedSessionKey);
        }

        private async void SkipPrevious_Click(object? sender, EventArgs e)
        {
            RegisterTransportWaitingFallback();
            RegisterPendingMediaTransitionDirection(ContentTransitionDirection.Backward);
            await _mediaService.SkipPreviousAsync(_displayedSessionKey);
        }

        private void RootGrid_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isClosed
                || _isContextFlyoutOpen
                || _controller.IsDragging
                || _controller.IsNotifying
                || _controller.Current.ExpandedOpacity <= IslandConfig.HitTestOpacityThreshold
                || ExpandedContent.IsSessionPickerOpen)
            {
                return;
            }

            int wheelDelta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
            if (wheelDelta == 0)
            {
                return;
            }

            ContentTransitionDirection direction = wheelDelta < 0
                ? ContentTransitionDirection.Forward
                : ContentTransitionDirection.Backward;

            if (TryCycleDisplayedSession(direction))
            {
                e.Handled = true;
            }
        }

        private void OnExpandedContentSessionSelected(string sessionKey)
        {
            SelectSession(
                sessionKey,
                GetDirectionToSession(sessionKey));
        }

        private bool TryCycleDisplayedSession(ContentTransitionDirection direction)
        {
            DisplayedMediaContext context = ResolveDisplayedMediaContext();
            if (context.OrderedSessions.Count <= 1 || context.DisplayIndex < 0)
            {
                return false;
            }

            int targetIndex = direction == ContentTransitionDirection.Forward
                ? context.DisplayIndex + 1
                : context.DisplayIndex - 1;

            if (targetIndex < 0 || targetIndex >= context.OrderedSessions.Count)
            {
                return false;
            }

            SelectSession(context.OrderedSessions[targetIndex].SessionKey, direction);
            return true;
        }

        private void SelectSession(string? sessionKey, ContentTransitionDirection direction)
        {
            if (string.IsNullOrWhiteSpace(sessionKey) || !_mediaService.HasSession(sessionKey))
            {
                return;
            }

            bool displayedSessionChanged = !string.Equals(_displayedSessionKey, sessionKey, StringComparison.Ordinal);
            _selectedSessionKey = sessionKey;
            _selectionLockUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(IslandConfig.SelectionLockDurationMs);
            RestartSelectionLockTimer();
            SyncAutoFocusTimer(null);

            if (displayedSessionChanged && direction != ContentTransitionDirection.None)
            {
                RegisterPendingMediaTransitionDirection(direction);
            }

            SyncMediaUI();
            UpdateRenderLoopState();
        }

        private ContentTransitionDirection GetDirectionToSession(string sessionKey)
        {
            IReadOnlyList<MediaSessionSnapshot> orderedSessions = GetVisualOrderedSessions(GetPriorityOrderedSessions());
            int currentIndex = FindSessionIndex(orderedSessions, _displayedSessionKey);
            int targetIndex = FindSessionIndex(orderedSessions, sessionKey);

            if (currentIndex < 0 || targetIndex < 0 || currentIndex == targetIndex)
            {
                return ContentTransitionDirection.None;
            }

            return targetIndex > currentIndex
                ? ContentTransitionDirection.Forward
                : ContentTransitionDirection.Backward;
        }

        private IReadOnlyList<MediaSessionSnapshot> GetVisualOrderedSessions(IReadOnlyList<MediaSessionSnapshot> prioritySessions)
        {
            if (prioritySessions.Count == 0)
            {
                _sessionVisualOrderKeys.Clear();
                return prioritySessions;
            }

            Dictionary<string, MediaSessionSnapshot> sessionsByKey = prioritySessions.ToDictionary(
                session => session.SessionKey,
                StringComparer.Ordinal);

            HashSet<string> activeKeys = new(sessionsByKey.Keys, StringComparer.Ordinal);
            _sessionVisualOrderKeys.RemoveAll(sessionKey => !activeKeys.Contains(sessionKey));

            HashSet<string> visualKeySet = new(_sessionVisualOrderKeys, StringComparer.Ordinal);
            for (int priorityIndex = 0; priorityIndex < prioritySessions.Count; priorityIndex++)
            {
                string sessionKey = prioritySessions[priorityIndex].SessionKey;
                if (visualKeySet.Contains(sessionKey))
                {
                    continue;
                }

                int insertIndex = ResolveVisualInsertIndex(prioritySessions, priorityIndex, visualKeySet);
                _sessionVisualOrderKeys.Insert(insertIndex, sessionKey);
                visualKeySet.Add(sessionKey);
            }

            return _sessionVisualOrderKeys
                .Where(sessionsByKey.ContainsKey)
                .Select(sessionKey => sessionsByKey[sessionKey])
                .ToArray();
        }

        private int ResolveVisualInsertIndex(IReadOnlyList<MediaSessionSnapshot> prioritySessions, int priorityIndex, HashSet<string> visualKeySet)
        {
            for (int index = priorityIndex - 1; index >= 0; index--)
            {
                string previousKey = prioritySessions[index].SessionKey;
                if (!visualKeySet.Contains(previousKey))
                {
                    continue;
                }

                int previousVisualIndex = _sessionVisualOrderKeys.FindIndex(
                    sessionKey => string.Equals(sessionKey, previousKey, StringComparison.Ordinal));
                if (previousVisualIndex >= 0)
                {
                    return previousVisualIndex + 1;
                }
            }

            for (int index = priorityIndex + 1; index < prioritySessions.Count; index++)
            {
                string nextKey = prioritySessions[index].SessionKey;
                if (!visualKeySet.Contains(nextKey))
                {
                    continue;
                }

                int nextVisualIndex = _sessionVisualOrderKeys.FindIndex(
                    sessionKey => string.Equals(sessionKey, nextKey, StringComparison.Ordinal));
                if (nextVisualIndex >= 0)
                {
                    return nextVisualIndex;
                }
            }

            return _sessionVisualOrderKeys.Count;
        }

        private void SelectionLockTimer_Tick(object? sender, object e)
        {
            if (IsManualSelectionLocked())
            {
                RestartSelectionLockTimer();
                return;
            }

            ClearManualSelectionLockInternal();
            SyncMediaUI();
            UpdateRenderLoopState();
        }

        private void AutoFocusTimer_Tick(object? sender, object e)
        {
            _autoFocusTimer.Stop();
            SyncMediaUI();
            UpdateRenderLoopState();
        }

        private bool IsManualSelectionLocked()
            => _selectionLockUntilUtc.HasValue
                && _selectionLockUntilUtc.Value > DateTimeOffset.UtcNow
                && !string.IsNullOrWhiteSpace(_selectedSessionKey);

        private void RestartSelectionLockTimer()
        {
            if (!_selectionLockUntilUtc.HasValue)
            {
                _selectionLockTimer.Stop();
                return;
            }

            TimeSpan remaining = _selectionLockUntilUtc.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _selectionLockTimer.Stop();
                return;
            }

            _selectionLockTimer.Stop();
            _selectionLockTimer.Interval = remaining < TimeSpan.FromMilliseconds(50)
                ? TimeSpan.FromMilliseconds(50)
                : remaining;
            _selectionLockTimer.Start();
        }

        private void SyncAutoFocusTimer(DateTimeOffset? dueUtc)
        {
            if (!dueUtc.HasValue || dueUtc.Value <= DateTimeOffset.UtcNow)
            {
                _autoFocusTimer.Stop();
                return;
            }

            TimeSpan remaining = dueUtc.Value - DateTimeOffset.UtcNow;
            _autoFocusTimer.Stop();
            _autoFocusTimer.Interval = remaining < TimeSpan.FromMilliseconds(50)
                ? TimeSpan.FromMilliseconds(50)
                : remaining;
            _autoFocusTimer.Start();
        }

        private void ClearManualSelectionLockInternal()
        {
            _selectedSessionKey = null;
            _selectionLockUntilUtc = null;
            _selectionLockTimer.Stop();
        }

        private void RequestMediaProgressReset(bool hideAfterReset)
        {
            if (_isClosed || _taskProgress.HasValue)
            {
                return;
            }

            if (!IslandProgressBar.IsEffectVisible)
            {
                _isMediaProgressResetPending = false;
                _hideMediaProgressWhenResetCompletes = false;
                UpdateRenderLoopState();
                return;
            }

            _isMediaProgressResetPending = true;
            _hideMediaProgressWhenResetCompletes = hideAfterReset;
            UpdateRenderLoopState();
        }

        private void RegisterPendingMediaTransitionDirection(ContentTransitionDirection direction)
        {
            _pendingMediaTransitionDirection = direction;
            _pendingMediaTransitionTimestamp = Environment.TickCount64;
        }

        private ContentTransitionDirection GetPendingMediaTransitionDirection()
        {
            if (_pendingMediaTransitionDirection == ContentTransitionDirection.None)
            {
                return ContentTransitionDirection.None;
            }

            long elapsed = Environment.TickCount64 - _pendingMediaTransitionTimestamp;
            if (elapsed > IslandConfig.TrackSwitchIntentWindowMs)
            {
                ClearPendingMediaTransitionDirection();
                return ContentTransitionDirection.None;
            }

            return _pendingMediaTransitionDirection;
        }

        private void ClearPendingMediaTransitionDirection()
        {
            _pendingMediaTransitionDirection = ContentTransitionDirection.None;
            _pendingMediaTransitionTimestamp = 0;
        }

        private static bool TryFindSession(
            IReadOnlyList<MediaSessionSnapshot> sessions,
            string? sessionKey,
            out MediaSessionSnapshot session,
            out int index)
        {
            if (!string.IsNullOrWhiteSpace(sessionKey))
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    if (string.Equals(sessions[i].SessionKey, sessionKey, StringComparison.Ordinal))
                    {
                        session = sessions[i];
                        index = i;
                        return true;
                    }
                }
            }

            session = default;
            index = -1;
            return false;
        }

        private static int FindSessionIndex(IReadOnlyList<MediaSessionSnapshot> sessions, string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return -1;
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                if (string.Equals(sessions[i].SessionKey, sessionKey, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int GetSessionPriorityRank(MediaSessionSnapshot session)
        {
            if (session.IsWaitingForReconnect)
            {
                return 4;
            }

            return session.PlaybackStatus switch
            {
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing when session.IsSystemCurrent => 0,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 1,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused when session.IsSystemCurrent => 2,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 3,
                _ => 5
            };
        }

        private void RegisterTransportWaitingFallback()
        {
            MediaSessionSnapshot? displayedSession = GetDisplayedMediaSessionSnapshot();
            if (!displayedSession.HasValue)
            {
                return;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            _transportWaitingSnapshot = displayedSession.Value with
            {
                Presence = MediaSessionPresence.WaitingForReconnect,
                MissingSinceUtc = nowUtc,
                LastSeenUtc = nowUtc
            };
            _transportWaitingStartedUtc = nowUtc;
            _transportWaitingUntilUtc = nowUtc.AddMilliseconds(IslandConfig.MediaMissingGraceMs);
        }

        private MediaSessionSnapshot? GetTransportWaitingFallbackSnapshot(IReadOnlyList<MediaSessionSnapshot> sessions)
        {
            if (!_transportWaitingUntilUtc.HasValue
                || !_transportWaitingSnapshot.HasValue)
            {
                return null;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if (_transportWaitingUntilUtc.Value <= nowUtc)
            {
                ClearTransportWaitingFallback();
                return null;
            }

            string sessionKey = _transportWaitingSnapshot.Value.SessionKey;
            if (TryFindSession(sessions, sessionKey, out MediaSessionSnapshot authoritativeSession, out _))
            {
                if (ShouldClearTransportWaitingFallback(authoritativeSession))
                {
                    ClearTransportWaitingFallback();
                }

                return null;
            }

            return _transportWaitingSnapshot.Value with
            {
                MissingSinceUtc = _transportWaitingStartedUtc ?? _transportWaitingSnapshot.Value.MissingSinceUtc ?? nowUtc,
                LastSeenUtc = nowUtc
            };
        }

        private void ClearTransportWaitingFallback()
        {
            _transportWaitingSnapshot = null;
            _transportWaitingStartedUtc = null;
            _transportWaitingUntilUtc = null;
        }

        private bool ShouldClearTransportWaitingFallback(MediaSessionSnapshot authoritativeSession)
        {
            if (!_transportWaitingSnapshot.HasValue)
            {
                return true;
            }

            if (authoritativeSession.IsWaitingForReconnect)
            {
                return true;
            }

            if (authoritativeSession.MissingSinceUtc.HasValue)
            {
                return false;
            }

            MediaSessionSnapshot armedSnapshot = _transportWaitingSnapshot.Value;
            bool metadataChanged =
                !string.Equals(authoritativeSession.Title, armedSnapshot.Title, StringComparison.Ordinal)
                || !string.Equals(authoritativeSession.Artist, armedSnapshot.Artist, StringComparison.Ordinal);
            if (metadataChanged && HasConcreteTransportMetadata(authoritativeSession))
            {
                return true;
            }

            return false;
        }

        private static bool HasConcreteTransportMetadata(MediaSessionSnapshot session)
            => !string.IsNullOrWhiteSpace(session.Title)
                && !string.Equals(session.Title, "Unknown Track", StringComparison.Ordinal);

        private static string? CreateContentIdentity(MediaSessionSnapshot? session)
            => session.HasValue
                ? string.Concat(
                    session.Value.SessionKey,
                    "\u001f",
                    session.Value.Title,
                    "\u001f",
                    session.Value.Artist,
                    "\u001f",
                    session.Value.Presence)
                : null;

        private static string? CreateProgressIdentity(MediaSessionSnapshot? session)
            => session.HasValue
                ? string.Concat(
                    session.Value.SessionKey,
                    "\u001f",
                    session.Value.Title)
                : null;

        private readonly record struct DisplayedMediaContext(
            MediaSessionSnapshot? DisplayedSession,
            IReadOnlyList<MediaSessionSnapshot> OrderedSessions,
            int DisplayIndex,
            string CompactText,
            string SessionCountText,
            bool ShowSessionCount);
    }
}
