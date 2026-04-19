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
        private string? _displayedSessionKey;
        private string? _lastDisplayedProgressIdentity;
        private MediaTrackFingerprint _lastDisplayedFingerprint = MediaTrackFingerprint.Empty;
        private MediaPresentationFrame? _latestFrame;
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
                    _presentationMachine.AutoFocusTimerScheduleRequested += OnAutoFocusTimerScheduleRequested;
                    _presentationMachine.ManualLockExpiryScheduleRequested += OnManualLockExpiryScheduleRequested;
                }
                await _mediaService.InitializeAsync();
                // Bootstrap: feed the machine the initial priority-ordered
                // session list so it can apply visual stability and emit the
                // first frame. Legacy SyncMediaUI will run on that frame.
                _presentationMachine?.Dispatch(new GsmtcSessionsChangedEvent(GetPriorityOrderedSessions()));
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
                _presentationMachine?.Dispatch(new GsmtcSessionsChangedEvent(GetPriorityOrderedSessions()));
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
                return new DisplayedMediaContext(
                    DisplayedSession: null,
                    OrderedSessions: prioritySessions,
                    DisplayIndex: -1,
                    CompactText: Loc.GetString("AppName"),
                    ShowTransportSwitchingHint: false);
            }

            // P4c-2b: ManualSelectionLockPolicy in the Machine owns lock
            // expiry and the "locked session no longer present" cleanup, so
            // MainWindow no longer mirrors that state.

            // P4c-2: DisplayedSession comes from the Machine's last-emitted frame.
            // _autoFocusTimer scheduling is now driven by Machine via the
            // AutoFocusTimerScheduleRequested event; see
            // OnAutoFocusTimerScheduleRequested below.
            MediaSessionSnapshot displayedSession =
                _latestFrame?.Session
                ?? prioritySessions[0];
            // Visual stability is now owned by MediaPresentationMachine and
            // arrives via _latestFrame.OrderedSessions. Fall back to the
            // priority list if no frame has been produced yet (e.g. very first
            // paint before the machine's bootstrap frame lands).
            IReadOnlyList<MediaSessionSnapshot> orderedSessions =
                _latestFrame?.OrderedSessions?.Count > 0
                    ? _latestFrame.OrderedSessions
                    : prioritySessions;
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
            if (displayedSessionChanged)
            {
                Logger.Debug($"Session selected: key={sessionKey}, direction={direction}");
                // P4c-2b: lock state + expiry live in ManualSelectionLockPolicy;
                // dispatching the event both arms the lock and (via the policy's
                // ScheduleManualLockExpiryTimer call) restarts MainWindow's
                // _selectionLockTimer through OnManualLockExpiryScheduleRequested.
                _presentationMachine?.Dispatch(new UserSelectSessionEvent(sessionKey, direction));
            }

            SyncMediaUI();
            UpdateRenderLoopState();
        }

        private ContentTransitionDirection GetDirectionToSession(string sessionKey)
        {
            IReadOnlyList<MediaSessionSnapshot> orderedSessions =
                _latestFrame?.OrderedSessions?.Count > 0
                    ? _latestFrame.OrderedSessions
                    : GetPriorityOrderedSessions();
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
            _selectionLockTimer.Stop();
            // P4c-2b: ManualSelectionLockPolicy observes expiry on any event;
            // an AutoFocusTimerFiredEvent is cheap and triggers the necessary
            // reconciliation. The resulting frame flows back via OnFrameProduced.
            _presentationMachine?.Dispatch(new AutoFocusTimerFiredEvent());
        }

        private void AutoFocusTimer_Tick(object? sender, object e)
        {
            _autoFocusTimer.Stop();
            // P4c-2: let the Machine re-reconcile (ManualSelectionLockPolicy will
            // see expiry, FocusArbitrationPolicy will pick a new winner). The
            // resulting frame flows back via OnFrameProduced → SyncMediaUI.
            _presentationMachine?.Dispatch(new AutoFocusTimerFiredEvent());
        }

        private void OnAutoFocusTimerScheduleRequested(DateTimeOffset? dueUtc)
            => RestartUiTimer(_autoFocusTimer, dueUtc);

        private void OnManualLockExpiryScheduleRequested(DateTimeOffset? dueUtc)
            => RestartUiTimer(_selectionLockTimer, dueUtc);

        private static void RestartUiTimer(Microsoft.UI.Xaml.DispatcherTimer timer, DateTimeOffset? dueUtc)
        {
            if (!dueUtc.HasValue || dueUtc.Value <= DateTimeOffset.UtcNow)
            {
                timer.Stop();
                return;
            }
            TimeSpan remaining = dueUtc.Value - DateTimeOffset.UtcNow;
            timer.Stop();
            timer.Interval = remaining < TimeSpan.FromMilliseconds(50)
                ? TimeSpan.FromMilliseconds(50)
                : remaining;
            timer.Start();
        }

        private bool IsManualSelectionLocked()
            => _presentationMachine?.HasManualLock == true;

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
