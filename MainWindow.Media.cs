using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using wisland.Models;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private ContentTransitionDirection _pendingMediaTransitionDirection = ContentTransitionDirection.None;
        private long _pendingMediaTransitionTimestamp;
        private string? _selectedSessionKey;
        private string? _displayedSessionKey;
        private DateTimeOffset? _selectionLockUntilUtc;
        private string? _lastDisplayedContentIdentity;
        private string? _lastDisplayedProgressIdentity;

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

        private IReadOnlyList<MediaSessionSnapshot> GetOrderedSessions()
            => _mediaService.Sessions
                .OrderByDescending(session => session.IsSystemCurrent)
                .ThenBy(session => GetPlaybackRank(session))
                .ThenByDescending(session => session.LastActivityUtc)
                .ThenBy(session => session.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.SessionKey, StringComparer.Ordinal)
                .ToArray();

        private DisplayedMediaContext ResolveDisplayedMediaContext()
        {
            IReadOnlyList<MediaSessionSnapshot> orderedSessions = GetOrderedSessions();
            if (orderedSessions.Count == 0)
            {
                ClearManualSelectionLockInternal();
                return new DisplayedMediaContext(
                    DisplayedSession: null,
                    OrderedSessions: orderedSessions,
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
                && !orderedSessions.Any(session => string.Equals(session.SessionKey, _selectedSessionKey, StringComparison.Ordinal)))
            {
                ClearManualSelectionLockInternal();
                hasManualLock = false;
            }

            string? candidateKey = hasManualLock ? _selectedSessionKey : _mediaService.SystemCurrentSessionKey;
            if (!TryFindSession(orderedSessions, candidateKey, out MediaSessionSnapshot displayedSession, out int displayIndex)
                && !TryFindSession(orderedSessions, _mediaService.SystemCurrentSessionKey, out displayedSession, out displayIndex))
            {
                displayedSession = orderedSessions[0];
                displayIndex = 0;
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
            RegisterPendingMediaTransitionDirection(ContentTransitionDirection.Forward);
            await _mediaService.SkipNextAsync(_displayedSessionKey);
        }

        private async void SkipPrevious_Click(object? sender, EventArgs e)
        {
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
                ? (context.DisplayIndex + 1) % context.OrderedSessions.Count
                : (context.DisplayIndex - 1 + context.OrderedSessions.Count) % context.OrderedSessions.Count;

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

            if (displayedSessionChanged && direction != ContentTransitionDirection.None)
            {
                RegisterPendingMediaTransitionDirection(direction);
            }

            SyncMediaUI();
            UpdateRenderLoopState();
        }

        private ContentTransitionDirection GetDirectionToSession(string sessionKey)
        {
            IReadOnlyList<MediaSessionSnapshot> orderedSessions = GetOrderedSessions();
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

        private static int GetPlaybackRank(MediaSessionSnapshot session)
            => session.PlaybackStatus switch
            {
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 0,
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 1,
                _ => 2
            };

        private static string? CreateContentIdentity(MediaSessionSnapshot? session)
            => session.HasValue
                ? string.Concat(
                    session.Value.SessionKey,
                    "\u001f",
                    session.Value.Title,
                    "\u001f",
                    session.Value.Artist)
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
