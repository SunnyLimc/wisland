using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services.Media.Presentation
{
    /// <summary>
    /// Single source of truth for "what media should be presented right now".
    ///
    /// Consumes <see cref="PresentationEvent"/>s on a single worker loop, threads
    /// them through the configured <see cref="IPresentationPolicy"/> chain, and
    /// emits <see cref="MediaPresentationFrame"/>s via <see cref="FrameProduced"/>.
    ///
    /// P2 phase: implements §4.4 transitions in a flattened form (explicit
    /// Pending/Confirming states arrive in P3). <see cref="SwitchIntent"/> is
    /// live, <see cref="FrameTransitionKind.ResumeAfterNotification"/> is
    /// honoured, and <see cref="PresentationKind"/> is kept independent from
    /// <see cref="MediaTrackFingerprint"/> so Kind-only changes never trigger
    /// a directional slide.
    /// </summary>
    public sealed class MediaPresentationMachine : IDisposable
    {
        private readonly IReadOnlyList<IPresentationPolicy> _policies;
        private readonly MediaPresentationMachineContext _context = new();
        private readonly MediaPresentationState _state = new();
        private readonly Channel<PresentationEvent> _events;
        private readonly CancellationTokenSource _cts = new();
        private readonly IDispatcherPoster _dispatcherPoster;
        private Task? _worker;
        private long _sequence;
        private bool _isDisposed;

        public MediaPresentationMachine(
            IReadOnlyList<IPresentationPolicy> policies,
            IDispatcherPoster dispatcherPoster)
        {
            _policies = policies;
            _dispatcherPoster = dispatcherPoster;
            _events = Channel.CreateUnbounded<PresentationEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            _context.ScheduleStabilizationTimerCallback = ScheduleStabilizationTimer;
            _context.ScheduleMetadataSettleTimerCallback = ScheduleMetadataSettleTimer;
            _context.ScheduleAutoFocusTimerCallback = ScheduleAutoFocusTimer;

            foreach (var policy in _policies)
            {
                policy.OnAttach(this);
            }
        }

        /// <summary>Frame output. Posted via <see cref="IDispatcherPoster"/> so
        /// the event usually fires on the UI thread.</summary>
        public event Action<MediaPresentationFrame>? FrameProduced;

        public void Start()
        {
            if (_worker != null || _isDisposed) return;
            _worker = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Dispatch(PresentationEvent evt)
        {
            if (_isDisposed) return;
            _events.Writer.TryWrite(evt);
        }

        /// <summary>
        /// Synchronous event processing path. Unit tests drive the machine
        /// through this entry point to avoid thread races; production code uses
        /// <see cref="Dispatch"/> which posts to the worker loop.
        /// </summary>
        internal void ProcessForTests(PresentationEvent evt) => ProcessEvent(evt);

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (await _events.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (_events.Reader.TryRead(out var evt))
                    {
                        ProcessEvent(evt);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error(ex, "MediaPresentationMachine worker crashed");
            }
        }

        private void ProcessEvent(PresentationEvent evt)
        {
            _context.NowUtc = DateTimeOffset.UtcNow;

            if (evt is GsmtcSessionsChangedEvent sessionsChanged)
            {
                // Apply visual stability to whatever ordering the caller supplied
                // (typically priority-ordered by MainWindow). Stability means
                // previously-seen session keys keep their visual slot; new keys
                // are inserted at their priority-anchor. Removed keys drop out.
                _context.Sessions = ApplyVisualOrdering(sessionsChanged.Sessions);
            }

            // Seed CurrentDisplayedSessionKey so policies (manual lock, arbiter)
            // see the same reality the machine does.
            _context.CurrentDisplayedSessionKey = _state.DisplayedSnapshot?.SessionKey;

            Logger.Trace($"[Machine] event={evt.GetType().Name} kind={_state.DisplayedKind} fp=[{DescribeFingerprint(_state.DisplayedFingerprint)}] intent={DescribeIntent(_state.Intent)} notifying={_state.IsNotifying}");

            foreach (var policy in _policies)
            {
                try
                {
                    policy.OnEvent(evt, _context);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Policy {policy.GetType().Name} threw on {evt.GetType().Name}: {ex.Message}");
                }
            }

            switch (evt)
            {
                case UserSkipRequestedEvent skip:
                    HandleUserSkip(skip);
                    break;
                case UserSelectSessionEvent select:
                    HandleUserSelect(select);
                    break;
                case NotificationBeginEvent begin:
                    HandleNotificationBegin(begin);
                    break;
                case NotificationEndEvent:
                    HandleNotificationEnd();
                    break;
                case GsmtcSessionsChangedEvent:
                case AutoFocusTimerFiredEvent:
                case StabilizationTimerFiredEvent:
                case MetadataSettleTimerFiredEvent:
                    ReconcileDisplay();
                    break;
                case AiResolveCompletedEvent ai:
                    HandleAiResolveCompleted(ai);
                    break;
            }
        }

        // --- Event handlers -------------------------------------------------

        private void HandleUserSkip(UserSkipRequestedEvent evt)
        {
            // Capture intent with the CURRENT displayed fingerprint as origin.
            // Retained across stabilization, status flips, and overlay wraps;
            // only consumed when the displayed fingerprint actually changes
            // away from Origin (§4.5).
            _state.Intent = new SwitchIntent(
                _state.DisplayedFingerprint,
                evt.Direction,
                _context.NowUtc + TimeSpan.FromMilliseconds(IslandConfig.SkipTransitionTimeoutMs));
            Logger.Debug($"[Machine] UserSkip {evt.Direction} captured. Origin={_state.DisplayedFingerprint.Title}/{_state.DisplayedFingerprint.SessionKey} deadline=+{IslandConfig.SkipTransitionTimeoutMs}ms");
            ReconcileDisplay();
        }

        private void HandleUserSelect(UserSelectSessionEvent evt)
        {
            _state.Intent = new SwitchIntent(
                _state.DisplayedFingerprint,
                evt.Direction,
                _context.NowUtc + TimeSpan.FromMilliseconds(IslandConfig.SkipTransitionTimeoutMs));
            Logger.Debug($"[Machine] UserSelect '{evt.SessionKey}' dir={evt.Direction}");
            ReconcileDisplay();
        }

        private void HandleNotificationBegin(NotificationBeginEvent evt)
        {
            if (_state.IsNotifying) return;
            _state.IsNotifying = true;
            _state.NotificationPayload = evt.Payload;
            // Snapshot the current display as "inner" so subsequent GSMTC
            // events during the overlay can advance inner state without
            // leaking frames.
            _state.InnerSnapshot = _state.DisplayedSnapshot;
            _state.InnerFingerprint = _state.DisplayedFingerprint;
            _state.InnerKind = _state.DisplayedKind;
            _state.InnerChangedDuringOverlay = false;

            EmitFrame(
                session: _state.DisplayedSnapshot,
                orderedSessions: _context.Sessions,
                kind: PresentationKind.Notifying,
                transition: FrameTransitionKind.None,
                fingerprint: _state.DisplayedFingerprint,
                isFallback: false,
                notification: evt.Payload);
        }

        private void HandleNotificationEnd()
        {
            if (!_state.IsNotifying)
            {
                return;
            }
            _state.IsNotifying = false;
            _state.NotificationPayload = null;

            // Promote accumulated inner state to displayed, then emit Resume.
            // Per D1: if the inner fingerprint changed during the overlay we
            // prefer a Slide (if intent is valid) else Crossfade. If nothing
            // changed, still emit ResumeAfterNotification so views know the
            // overlay is gone, but with Transition=ResumeAfterNotification
            // (views can choose to treat that as a no-op).
            bool fpChanged = !FingerprintEquals(_state.InnerFingerprint, _state.DisplayedFingerprint);
            FrameTransitionKind transition = FrameTransitionKind.ResumeAfterNotification;

            _state.DisplayedSnapshot = _state.InnerSnapshot;
            _state.DisplayedKind = _state.InnerKind;
            _state.DisplayedFingerprint = _state.InnerFingerprint;

            if (fpChanged && _state.Intent.HasValue && !_state.Intent.Value.IsExpired(_context.NowUtc))
            {
                // Upgrade Resume → directional Slide to preserve the animation
                // opportunity (§5.2, D1).
                transition = _state.Intent.Value.Direction switch
                {
                    ContentTransitionDirection.Forward => FrameTransitionKind.SlideForward,
                    ContentTransitionDirection.Backward => FrameTransitionKind.SlideBackward,
                    _ => FrameTransitionKind.Crossfade
                };
                _state.Intent = null;
            }
            else if (fpChanged)
            {
                transition = FrameTransitionKind.Crossfade;
            }

            EmitFrame(
                session: _state.DisplayedSnapshot,
                orderedSessions: _context.Sessions,
                kind: _state.DisplayedKind,
                transition: transition,
                fingerprint: _state.DisplayedFingerprint,
                isFallback: false,
                notification: null);

            _state.InnerSnapshot = null;
            _state.InnerFingerprint = MediaTrackFingerprint.Empty;
            _state.InnerKind = PresentationKind.Empty;
            _state.InnerChangedDuringOverlay = false;
        }

        private void HandleAiResolveCompleted(AiResolveCompletedEvent evt)
        {
            // P3/P4 will thread AI overrides through AiOverridePolicy. For P2
            // we simply re-emit the current frame so any UI-visible override
            // state can refresh without a slide. Without AiOverridePolicy
            // populating ActiveAiOverride this is a harmless no-op.
            if (_state.IsNotifying || !_state.Initialized) return;
            EmitFrame(
                session: _state.DisplayedSnapshot,
                orderedSessions: _context.Sessions,
                kind: _state.DisplayedKind,
                transition: FrameTransitionKind.None,
                fingerprint: _state.DisplayedFingerprint,
                isFallback: false,
                notification: null);
        }

        // --- Core reconciliation --------------------------------------------

        private void ReconcileDisplay()
        {
            // Compute what the display should look like given the latest
            // arbiter decision + sessions.
            var winnerKey = _context.ArbitratedWinnerKey;
            MediaSessionSnapshot? winner = FindSession(_context.Sessions, winnerKey);

            PresentationKind kind;
            MediaTrackFingerprint fingerprint;
            MediaSessionSnapshot? snapshotForFrame;
            bool isFallback = false;

            if (!winner.HasValue)
            {
                kind = PresentationKind.Empty;
                fingerprint = MediaTrackFingerprint.Empty;
                snapshotForFrame = null;
            }
            else if (winner.Value.IsStabilizing)
            {
                // MediaService is suppressing raw metadata. Keep the previous
                // fingerprint/snapshot and mark kind=Switching so the UI chip
                // can show hint text without mutating the main visual.
                kind = PresentationKind.Switching;
                fingerprint = _state.DisplayedFingerprint;
                snapshotForFrame = _state.DisplayedSnapshot ?? winner;
            }
            else if (winner.Value.IsWaitingForReconnect)
            {
                kind = PresentationKind.Missing;
                fingerprint = BuildFingerprint(winner.Value);
                snapshotForFrame = winner;
            }
            else
            {
                kind = PresentationKind.Steady;
                fingerprint = BuildFingerprint(winner.Value);
                snapshotForFrame = winner;
            }

            // Determine transition based on fingerprint delta + intent.
            FrameTransitionKind transition = FrameTransitionKind.None;
            bool fpChanged = !FingerprintEquals(fingerprint, _state.DisplayedFingerprint);
            bool firstFrame = !_state.Initialized;
            bool kindChanged = kind != _state.DisplayedKind;

            if (_state.IsNotifying)
            {
                // Overlay: don't emit frames; accumulate into inner. Intent is
                // preserved across the overlay so the Resume frame can upgrade
                // to a directional Slide (D1).
                if (fpChanged)
                {
                    _state.InnerChangedDuringOverlay = true;
                }
                _state.InnerSnapshot = snapshotForFrame;
                _state.InnerKind = kind;
                _state.InnerFingerprint = fingerprint;
                return;
            }

            if (firstFrame && kind != PresentationKind.Empty)
            {
                transition = FrameTransitionKind.Replace;
            }
            else if (fpChanged)
            {
                if (_state.Intent.HasValue
                    && !_state.Intent.Value.IsExpired(_context.NowUtc)
                    && _state.Intent.Value.MatchesOrigin(_state.DisplayedFingerprint))
                {
                    transition = _state.Intent.Value.Direction switch
                    {
                        ContentTransitionDirection.Forward => FrameTransitionKind.SlideForward,
                        ContentTransitionDirection.Backward => FrameTransitionKind.SlideBackward,
                        _ => FrameTransitionKind.Replace
                    };
                    _state.Intent = null; // consumed
                }
                else
                {
                    transition = FrameTransitionKind.Replace;
                    // Expired or mismatched intent is dropped so it can't
                    // stale-fire on a later unrelated change.
                    if (_state.Intent.HasValue && _state.Intent.Value.IsExpired(_context.NowUtc))
                    {
                        _state.Intent = null;
                    }
                }
            }

            // Non-overlay: persist state and emit if something changed.
            _state.DisplayedSnapshot = snapshotForFrame;
            _state.DisplayedKind = kind;
            _state.DisplayedFingerprint = fingerprint;

            if (firstFrame || fpChanged || kindChanged)
            {
                _state.Initialized = true;
                EmitFrame(
                    session: snapshotForFrame,
                    orderedSessions: _context.Sessions,
                    kind: kind,
                    transition: fpChanged ? transition : FrameTransitionKind.None,
                    fingerprint: fingerprint,
                    isFallback: isFallback,
                    notification: null);
            }
        }

        // --- Emit helper ----------------------------------------------------

        private void EmitFrame(
            MediaSessionSnapshot? session,
            IReadOnlyList<MediaSessionSnapshot> orderedSessions,
            PresentationKind kind,
            FrameTransitionKind transition,
            MediaTrackFingerprint fingerprint,
            bool isFallback,
            NotificationPayload? notification)
        {
            int displayIndex = -1;
            if (session.HasValue)
            {
                for (int i = 0; i < orderedSessions.Count; i++)
                {
                    if (string.Equals(orderedSessions[i].SessionKey, session.Value.SessionKey, StringComparison.Ordinal))
                    {
                        displayIndex = i;
                        break;
                    }
                }
            }

            var frame = new MediaPresentationFrame(
                Sequence: NextSequence(),
                Session: session,
                OrderedSessions: orderedSessions,
                DisplayIndex: displayIndex,
                Kind: kind,
                Transition: transition,
                Fingerprint: fingerprint,
                ProgressFingerprint: fingerprint,
                IsFallback: isFallback,
                ThumbnailHashIsFallback: string.IsNullOrEmpty(fingerprint.ThumbnailHash),
                AiOverride: _context.ActiveAiOverride,
                Notification: notification);

            // §6 invariants (P5 item 34). Debug-only; only trip in tests / local
            // builds so production is unaffected if a policy misbehaves.
            System.Diagnostics.Debug.Assert(
                frame.Sequence > _lastEmittedSequence,
                $"Invariant #1 violated: Sequence must strictly increase (last={_lastEmittedSequence}, new={frame.Sequence})");

            bool fpChanged = !fingerprint.Equals(_lastEmittedFingerprint);
            bool isSlide = transition == FrameTransitionKind.SlideForward
                        || transition == FrameTransitionKind.SlideBackward;

            System.Diagnostics.Debug.Assert(
                !isSlide || fpChanged,
                $"Invariant #2 violated: Slide transition requires fingerprint change (transition={transition}, fp={DescribeFingerprint(fingerprint)})");

            // Invariant #3: fp change ⇒ transition != None. Exception: the very
            // first emit from an Empty baseline is allowed to use None only when
            // the new fp is also Empty (i.e. startup idle). Any non-empty fp
            // transition must be visually acknowledged.
            bool baselineWasEmpty = _lastEmittedFingerprint.IsEmpty;
            bool newIsEmpty = fingerprint.IsEmpty;
            System.Diagnostics.Debug.Assert(
                !fpChanged || transition != FrameTransitionKind.None || (baselineWasEmpty && newIsEmpty),
                $"Invariant #3 violated: fingerprint changed but transition=None (fp_from={DescribeFingerprint(_lastEmittedFingerprint)}, fp_to={DescribeFingerprint(fingerprint)})");

            // Structured emit log (section 12 P5 item 33). Captures the seq, the
            // transition decision, fp delta across the emit boundary, and the
            // current intent so regressions can be diagnosed from logs alone.
            Logger.Debug($"[Machine] emit seq={frame.Sequence} kind={_lastEmittedKind}->{kind} transition={transition} fp_from=[{DescribeFingerprint(_lastEmittedFingerprint)}] fp_to=[{DescribeFingerprint(fingerprint)}] intent={DescribeIntent(_state.Intent)} notification={(notification != null ? "yes" : "no")} fallback={isFallback}");

            _lastEmittedSequence = frame.Sequence;
            _lastEmittedKind = kind;
            _lastEmittedFingerprint = fingerprint;

            _dispatcherPoster.Post(() => FrameProduced?.Invoke(frame));
        }

        private long _lastEmittedSequence = -1;
        private PresentationKind _lastEmittedKind = PresentationKind.Empty;
        private MediaTrackFingerprint _lastEmittedFingerprint = MediaTrackFingerprint.Empty;

        private static string DescribeFingerprint(MediaTrackFingerprint fp)
        {
            if (fp.IsEmpty) return "empty";
            string title = string.IsNullOrEmpty(fp.Title) ? "-" : fp.Title;
            string artist = string.IsNullOrEmpty(fp.Artist) ? "-" : fp.Artist;
            string hash = string.IsNullOrEmpty(fp.ThumbnailHash) ? "nohash" : fp.ThumbnailHash.Substring(0, Math.Min(8, fp.ThumbnailHash.Length));
            return $"{fp.SessionKey} '{title}' by '{artist}' #{hash}";
        }

        private static string DescribeIntent(SwitchIntent? intent)
        {
            if (!intent.HasValue) return "none";
            var i = intent.Value;
            long dueMs = (long)(i.DeadlineUtc - DateTimeOffset.UtcNow).TotalMilliseconds;
            return $"dir={i.Direction} origin='{i.Origin.Title}' due={dueMs}ms";
        }

        // --- Utility --------------------------------------------------------

        /// <summary>Preserved session-key order so newly arriving sessions don't
        /// reshuffle positions the user is already tracking. Mutated only inside
        /// <see cref="ApplyVisualOrdering"/>, which runs on the worker thread.</summary>
        private readonly List<string> _visualOrderKeys = new();

        private IReadOnlyList<MediaSessionSnapshot> ApplyVisualOrdering(
            IReadOnlyList<MediaSessionSnapshot> prioritySessions)
        {
            if (prioritySessions.Count == 0)
            {
                _visualOrderKeys.Clear();
                return prioritySessions;
            }

            Dictionary<string, MediaSessionSnapshot> sessionsByKey = new(StringComparer.Ordinal);
            for (int i = 0; i < prioritySessions.Count; i++)
            {
                sessionsByKey[prioritySessions[i].SessionKey] = prioritySessions[i];
            }

            HashSet<string> activeKeys = new(sessionsByKey.Keys, StringComparer.Ordinal);
            _visualOrderKeys.RemoveAll(key => !activeKeys.Contains(key));

            HashSet<string> visualKeySet = new(_visualOrderKeys, StringComparer.Ordinal);
            for (int priorityIndex = 0; priorityIndex < prioritySessions.Count; priorityIndex++)
            {
                string sessionKey = prioritySessions[priorityIndex].SessionKey;
                if (visualKeySet.Contains(sessionKey)) continue;

                int insertIndex = ResolveVisualInsertIndex(prioritySessions, priorityIndex, visualKeySet);
                _visualOrderKeys.Insert(insertIndex, sessionKey);
                visualKeySet.Add(sessionKey);
            }

            var result = new List<MediaSessionSnapshot>(_visualOrderKeys.Count);
            foreach (string key in _visualOrderKeys)
            {
                if (sessionsByKey.TryGetValue(key, out var snap))
                {
                    result.Add(snap);
                }
            }
            return result;
        }

        private int ResolveVisualInsertIndex(
            IReadOnlyList<MediaSessionSnapshot> prioritySessions,
            int priorityIndex,
            HashSet<string> visualKeySet)
        {
            for (int index = priorityIndex - 1; index >= 0; index--)
            {
                string previousKey = prioritySessions[index].SessionKey;
                if (!visualKeySet.Contains(previousKey)) continue;
                int previousVisualIndex = _visualOrderKeys.FindIndex(
                    k => string.Equals(k, previousKey, StringComparison.Ordinal));
                if (previousVisualIndex >= 0) return previousVisualIndex + 1;
            }
            for (int index = priorityIndex + 1; index < prioritySessions.Count; index++)
            {
                string nextKey = prioritySessions[index].SessionKey;
                if (!visualKeySet.Contains(nextKey)) continue;
                int nextVisualIndex = _visualOrderKeys.FindIndex(
                    k => string.Equals(k, nextKey, StringComparison.Ordinal));
                if (nextVisualIndex >= 0) return nextVisualIndex;
            }
            return _visualOrderKeys.Count;
        }

        private static MediaTrackFingerprint BuildFingerprint(MediaSessionSnapshot session)
            // P3: ThumbnailHash is xxhash64(first 4KB) populated asynchronously by
            // MediaService after the thumbnail reference updates. Empty until the
            // first compute completes; that is treated as its own fingerprint
            // value so a hash arriving on a later dispatch surfaces as a fp
            // change and can trigger the appropriate frame.
            => new(session.SessionKey ?? string.Empty,
                   session.Title ?? string.Empty,
                   session.Artist ?? string.Empty,
                   session.ThumbnailHash ?? string.Empty);

        private static bool FingerprintEquals(MediaTrackFingerprint a, MediaTrackFingerprint b)
            => string.Equals(a.SessionKey, b.SessionKey, StringComparison.Ordinal)
            && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && string.Equals(a.Artist, b.Artist, StringComparison.Ordinal)
            && string.Equals(a.ThumbnailHash, b.ThumbnailHash, StringComparison.Ordinal);

        private static MediaSessionSnapshot? FindSession(IReadOnlyList<MediaSessionSnapshot> sessions, string? sessionKey)
        {
            if (string.IsNullOrEmpty(sessionKey)) return null;
            for (int i = 0; i < sessions.Count; i++)
            {
                if (string.Equals(sessions[i].SessionKey, sessionKey, StringComparison.Ordinal))
                {
                    return sessions[i];
                }
            }
            return null;
        }

        // --- Timer scheduling stubs (real impl lands with P3 Stabilization) -

        private void ScheduleStabilizationTimer(DateTimeOffset dueUtc)
        {
            Logger.Trace($"[Machine] ScheduleStabilizationTimer due={dueUtc:HH:mm:ss.fff}");
        }

        private void ScheduleMetadataSettleTimer(DateTimeOffset dueUtc)
        {
            Logger.Trace($"[Machine] ScheduleMetadataSettleTimer due={dueUtc:HH:mm:ss.fff}");
        }

        private void ScheduleAutoFocusTimer(DateTimeOffset? dueUtc)
        {
            Logger.Trace($"[Machine] ScheduleAutoFocusTimer due={dueUtc?.ToString("HH:mm:ss.fff") ?? "-"}");
        }

        internal long NextSequence() => Interlocked.Increment(ref _sequence);

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { _cts.Cancel(); } catch { }
            _events.Writer.TryComplete();
            try { _worker?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }
    }

    public interface IDispatcherPoster
    {
        void Post(Action action);
    }
}

