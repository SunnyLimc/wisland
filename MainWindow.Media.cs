using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;
using wisland.Services;
using wisland.Services.Media.Presentation;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private readonly MediaFocusArbiter _focusArbiter = new(
            TimeSpan.FromMilliseconds(IslandConfig.MediaAutoSwitchDebounceMs),
            TimeSpan.FromMilliseconds(IslandConfig.MediaMissingGraceMs));
        private string? _selectedSessionKey;
        private string? _displayedSessionKey;
        private DateTimeOffset? _selectionLockUntilUtc;
        private string? _lastDisplayedProgressIdentity;
        private MediaTrackFingerprint _lastDisplayedFingerprint = MediaTrackFingerprint.Empty;
        private MediaPresentationFrame? _latestFrame;
        private readonly List<string> _sessionVisualOrderKeys = new();
        private CancellationTokenSource? _aiResolveCts;
        private MediaTrackFingerprint _lastAiResolveFingerprint = MediaTrackFingerprint.Empty;
        private string? _lastAiOverrideLookupIdentity;
        private AiSongResult? _lastAiOverrideLookupResult;

        private async Task InitializeMediaAsync()
        {
            try
            {
                _mediaService.SessionsChanged += OnMediaServiceChanged;
                _mediaService.TrackChanged += OnTrackChanged;
                if (_presentationMachine != null)
                {
                    _presentationMachine.FrameProduced += OnFrameProduced;
                }
                await _mediaService.InitializeAsync();
                // Bootstrap: feed the machine the initial session list so it can
                // emit the first frame. Legacy SyncMediaUI will run on that frame.
                _presentationMachine?.Dispatch(new GsmtcSessionsChangedEvent(_mediaService.Sessions));
                SyncMediaUI();
                UpdateRenderLoopState();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize media service");
            }
        }

        private void OnMediaServiceChanged()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                _presentationMachine?.Dispatch(new GsmtcSessionsChangedEvent(_mediaService.Sessions));
                UpdateRenderLoopState();
            });
        }

        private void OnFrameProduced(MediaPresentationFrame frame)
        {
            // Machine already posted us to the UI thread via IDispatcherPoster.
            _latestFrame = frame;
            SyncMediaUI();
            UpdateRenderLoopState();
        }

        private void OnTrackChanged(string title, string artist)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_controller.IsDocked && !_controller.IsHovered && !_controller.IsDragging)
                {
                    ShowNotification(title, artist, IslandConfig.TrackChangeNotificationDurationMs, Loc.GetString("Media/NewTrack"));
                }
            });
        }

        private void SyncMediaUI()
        {
            DisplayedMediaContext rawContext = ResolveDisplayedMediaContext();
            // AI override is still applied locally (moves to AiOverridePolicy in P3).
            DisplayedMediaContext context = ApplyAiOverrideToContext(rawContext);

            // Direction / switching hint come from the machine frame, not from
            // local identity diffing. Falling back to None when no frame yet.
            MediaPresentationFrame? frame = _latestFrame;
            ContentTransitionDirection directionHint = FrameTransitionToDirection(frame?.Transition ?? FrameTransitionKind.None);
            bool showTransportSwitchingHint = frame?.Kind == PresentationKind.Switching;

            MediaTrackFingerprint nextFingerprint = frame?.Fingerprint ?? ComputeFallbackFingerprint(context.DisplayedSession);
            bool contentChanged = !FingerprintEquals(_lastDisplayedFingerprint, nextFingerprint);

            string? nextProgressIdentity = CreateProgressIdentity(context.DisplayedSession);
            bool progressSourceChanged = !string.Equals(_lastDisplayedProgressIdentity, nextProgressIdentity, StringComparison.Ordinal);

            _displayedSessionKey = context.DisplayedSession?.SessionKey;
            _mediaService.SetDisplayedSessionKey(_displayedSessionKey);

            if (progressSourceChanged)
            {
                RequestMediaProgressReset(!context.DisplayedSession.HasValue || !context.DisplayedSession.Value.HasTimeline);
                _lastDisplayedProgressIdentity = nextProgressIdentity;
            }

            CompactContent.Update(context.CompactText, directionHint);

            if (!_controller.IsForcedExpanded)
            {
                // Sync immersive dimensions on the controller
                _controller.UseImmersiveDimensions = IsImmersiveActive;

                ExpandedContent.UpdateMedia(
                    context.DisplayedSession,
                    context.DisplayIndex,
                    context.OrderedSessions.Count,
                    context.OrderedSessions,
                    directionHint,
                    showTransportSwitchingHint);

                ImmersiveContent.UpdateMedia(
                    context.DisplayedSession,
                    context.DisplayIndex,
                    context.OrderedSessions.Count,
                    context.OrderedSessions,
                    directionHint,
                    showTransportSwitchingHint);
            }
            else
            {
                HideSessionPickerOverlay(reconcileHover: false);
            }

            SyncSessionPickerOverlay(context);

            if (contentChanged)
            {
                _lastDisplayedFingerprint = nextFingerprint;
                TryRequestAiResolveForFrame(rawContext.DisplayedSession, nextFingerprint);
            }
        }

        private static ContentTransitionDirection FrameTransitionToDirection(FrameTransitionKind kind) => kind switch
        {
            FrameTransitionKind.SlideForward => ContentTransitionDirection.Forward,
            FrameTransitionKind.SlideBackward => ContentTransitionDirection.Backward,
            _ => ContentTransitionDirection.None
        };

        private static bool FingerprintEquals(MediaTrackFingerprint a, MediaTrackFingerprint b)
            => string.Equals(a.SessionKey, b.SessionKey, StringComparison.Ordinal)
            && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && string.Equals(a.Artist, b.Artist, StringComparison.Ordinal)
            && string.Equals(a.ThumbnailHash, b.ThumbnailHash, StringComparison.Ordinal);

        private static MediaTrackFingerprint ComputeFallbackFingerprint(MediaSessionSnapshot? session)
            => session.HasValue
                ? new MediaTrackFingerprint(session.Value.SessionKey, session.Value.Title, session.Value.Artist, string.Empty)
                : MediaTrackFingerprint.Empty;

        private DisplayedMediaContext ApplyAiOverrideToContext(DisplayedMediaContext context)
        {
            if (!context.DisplayedSession.HasValue)
                return context;

            var session = context.DisplayedSession.Value;
            var cached = GetCachedAiOverride(session);
            if (cached == null)
                return context;

            Logger.Trace($"AI override applied: '{session.Title}' → '{cached.Title}'");
            var overridden = session with { Title = cached.Title, Artist = cached.Artist };
            return context with
            {
                DisplayedSession = overridden,
                CompactText = cached.Title
            };
        }

        private async void TryRequestAiResolveForFrame(MediaSessionSnapshot? session, MediaTrackFingerprint fingerprint)
        {
            if (!_settings.AiSongOverrideEnabled || _aiSongResolver == null || !session.HasValue)
                return;

            var displayed = session.Value;

            // Already resolved from cache — no need to call AI
            var cached = GetCachedAiOverride(displayed);
            if (cached != null)
                return;

            // Cancel any previous in-flight request
            _aiResolveCts?.Cancel();
            _aiResolveCts?.Dispose();
            var cts = new CancellationTokenSource();
            _aiResolveCts = cts;

            _lastAiResolveFingerprint = fingerprint;

            try
            {
                string sourceName = MediaSourceAppResolver.TryResolveDisplayName(displayed.SourceAppId)
                    ?? displayed.SourceName;

                var result = await _aiSongResolver.ResolveAsync(
                    displayed.SourceAppId, displayed.Title, displayed.Artist,
                    sourceName, displayed.DurationSeconds,
                    cts.Token);

                if (cts.Token.IsCancellationRequested)
                    return;

                // Only update UI if we're still displaying the same track
                if (result != null && FingerprintEquals(_lastAiResolveFingerprint, fingerprint))
                {
                    ResetAiOverrideLookupState();
                    SyncMediaUI();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warn($"AI resolve failed: {ex.Message}");
            }
        }

        internal void OnAiSettingsChanged()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                ResetAiOverrideLookupState();
                _lastAiResolveFingerprint = MediaTrackFingerprint.Empty;
                _lastDisplayedFingerprint = MediaTrackFingerprint.Empty;
                SyncMediaUI();
            });
        }

        private AiSongResult? GetCachedAiOverride(MediaSessionSnapshot session)
        {
            if (!_settings.AiSongOverrideEnabled || _aiSongResolver == null)
            {
                ResetAiOverrideLookupState();
                return null;
            }

            string lookupIdentity = CreateAiLookupIdentity(session);
            if (!string.Equals(_lastAiOverrideLookupIdentity, lookupIdentity, StringComparison.Ordinal))
            {
                _lastAiOverrideLookupIdentity = lookupIdentity;
                _lastAiOverrideLookupResult = _aiSongResolver.TryGetCached(session.SourceAppId, session.Title, session.Artist);
            }

            return _lastAiOverrideLookupResult;
        }

        private void ResetAiOverrideLookupState()
        {
            _lastAiOverrideLookupIdentity = null;
            _lastAiOverrideLookupResult = null;
        }

        private MediaSessionSnapshot? GetDisplayedMediaSessionSnapshot()
            => _mediaService.GetSessionSnapshot(_displayedSessionKey);

        private IReadOnlyList<MediaSessionSnapshot> GetPriorityOrderedSessions()
        {
            return _mediaService.Sessions
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
                    CompactText: Loc.GetString("AppName"),
                    ShowTransportSwitchingHint: false);
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

            // Kind-based switching hint now comes from the machine frame; legacy
            // fallback: use session.IsStabilizing when no frame has arrived yet.
            bool showTransportSwitchingHint = displayedSession.IsStabilizing;

            return new DisplayedMediaContext(
                DisplayedSession: displayedSession,
                OrderedSessions: orderedSessions,
                DisplayIndex: displayIndex,
                CompactText: displayedSession.Title,
                ShowTransportSwitchingHint: showTransportSwitchingHint);
        }

        private async void PlayPause_Click(object? sender, EventArgs e)
        {
            try { await _mediaService.PlayPauseAsync(_displayedSessionKey); }
            catch (Exception ex) { Logger.Warn($"PlayPause failed: {ex.Message}"); }
        }

        private async void SkipNext_Click(object? sender, EventArgs e)
        {
            try
            {
                _presentationMachine?.Dispatch(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
                await _mediaService.SkipNextAsync(_displayedSessionKey);
            }
            catch (Exception ex) { Logger.Warn($"SkipNext failed: {ex.Message}"); }
        }

        private async void SkipPrevious_Click(object? sender, EventArgs e)
        {
            try
            {
                _presentationMachine?.Dispatch(new UserSkipRequestedEvent(ContentTransitionDirection.Backward));
                await _mediaService.SkipPreviousAsync(_displayedSessionKey);
            }
            catch (Exception ex) { Logger.Warn($"SkipPrevious failed: {ex.Message}"); }
        }

        private async void ImmersiveContent_SeekRequested(object? sender, double ratio)
        {
            try
            {
                MediaSessionSnapshot? displayed = _mediaService.GetSessionSnapshot(_displayedSessionKey);
                if (!displayed.HasValue) return;
                if (!displayed.Value.HasTimeline || displayed.Value.DurationSeconds <= 0) return;
                double target = Math.Clamp(ratio, 0, 1) * displayed.Value.DurationSeconds;
                await _mediaService.SeekAsync(_displayedSessionKey, target);
            }
            catch (Exception ex) { Logger.Warn($"Seek failed: {ex.Message}"); }
        }

        private void RootGrid_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isClosed
                || HasBlockingSurfaceOpen
                || _controller.IsDragging
                || _controller.IsForcedExpanded
                || _controller.Current.ExpandedOpacity <= IslandConfig.HitTestOpacityThreshold)
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

            if (displayedSessionChanged)
            {
                Logger.Debug($"Session selected: key={sessionKey}, direction={direction}");
                _presentationMachine?.Dispatch(new UserSelectSessionEvent(sessionKey, direction));
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

            // Snap the bar to zero immediately so the new session's position can
            // start growing from 0 on the next frame — avoids the 2-3 second drain
            // physics that made it look like the bar wasn't moving.
            IslandProgressBar.SnapToZero();
            _isMediaProgressResetPending = true;
            _hideMediaProgressWhenResetCompletes = hideAfterReset;
            UpdateRenderLoopState();
        }

        private void RegisterPendingMediaTransitionDirection(ContentTransitionDirection direction)
        {
            // P2b: intent is now owned by MediaPresentationMachine via UserSkipRequestedEvent /
            // UserSelectSessionEvent. Legacy callers (drag gesture, etc.) still invoke this;
            // forward to the machine if one exists. This shim is removed in P4.
            if (direction == ContentTransitionDirection.None) return;
            _presentationMachine?.Dispatch(new UserSkipRequestedEvent(direction));
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

        private static string CreateAiLookupIdentity(MediaSessionSnapshot session)
            => string.Concat(
                session.SourceAppId,
                "\u001f",
                session.Title,
                "\u001f",
                session.Artist);

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
            bool ShowTransportSwitchingHint);
    }
}
