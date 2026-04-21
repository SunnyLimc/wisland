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
        private readonly Func<DateTimeOffset> _nowProvider;
        private Task? _worker;
        private long _sequence;
        private bool _isDisposed;

        public MediaPresentationMachine(
            IReadOnlyList<IPresentationPolicy> policies,
            IDispatcherPoster dispatcherPoster,
            Func<DateTimeOffset>? nowProvider = null)
        {
            _policies = policies;
            _dispatcherPoster = dispatcherPoster;
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
            _events = Channel.CreateUnbounded<PresentationEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            _context.ScheduleStabilizationTimerCallback = ScheduleStabilizationTimer;
            _context.ScheduleMetadataSettleTimerCallback = ScheduleMetadataSettleTimer;
            _context.ScheduleAutoFocusTimerCallback = ScheduleAutoFocusTimer;
            _context.ScheduleManualLockExpiryTimerCallback = ScheduleManualLockExpiryTimer;

            foreach (var policy in _policies)
            {
                policy.OnAttach(this);
            }
        }

        /// <summary>Frame output. Posted via <see cref="IDispatcherPoster"/> so
        /// the event usually fires on the UI thread.</summary>
        public event Action<MediaPresentationFrame>? FrameProduced;

        /// <summary>Fired when FocusArbitrationPolicy wants to schedule (or
        /// cancel with null) the auto-focus timer. The host must restart its
        /// timer to fire at <c>dueUtc</c>; the timer tick handler should
        /// <see cref="Dispatch"/> <see cref="AutoFocusTimerFiredEvent"/>.</summary>
        public event Action<DateTimeOffset?>? AutoFocusTimerScheduleRequested;

        /// <summary>Fired when ManualSelectionLockPolicy wants the host to
        /// schedule (or cancel with null) a tick at lock-expiry time. The host
        /// must restart its timer to fire at <c>dueUtc</c>; the tick handler
        /// should <see cref="Dispatch"/> any event (e.g. <see cref="AutoFocusTimerFiredEvent"/>)
        /// so the policy's OnEvent runs and expiry is published.</summary>
        public event Action<DateTimeOffset?>? ManualLockExpiryScheduleRequested;

        /// <summary>Fired when the Confirming settle timer should be armed
        /// (or restarted) at <c>dueUtc</c>. The host should restart its
        /// DispatcherTimer to fire at that instant; the tick handler must
        /// <see cref="Dispatch"/> <see cref="MetadataSettleTimerFiredEvent"/>.</summary>
        public event Action<DateTimeOffset?>? MetadataSettleTimerScheduleRequested;

        /// <summary>Public snapshot of ManualSelectionLockPolicy's current
        /// decision. Intended for MainWindow's rendering-path queries
        /// (e.g. "is the user currently holding focus?").</summary>
        public bool HasManualLock => _context.HasManualLock;

        /// <summary>Session key currently held by ManualSelectionLockPolicy;
        /// null when no lock is active.</summary>
        public string? ManualLockedSessionKey => _context.ManualLockedSessionKey;

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
            _context.NowUtc = _nowProvider();

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
                    ReconcileDisplay(evt);
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
            // away from Origin (§4.5). Source=Skip so the subsequent fp change
            // is absorbed through Confirming (guards against Chrome's paused-
            // tab metadata flicker).
            _state.Intent = new SwitchIntent(
                _state.DisplayedFingerprint,
                evt.Direction,
                _context.NowUtc + TimeSpan.FromMilliseconds(IslandConfig.SkipTransitionTimeoutMs),
                SwitchIntentSource.Skip);
            Logger.Debug($"[Machine] UserSkip {evt.Direction} captured. Origin={_state.DisplayedFingerprint.Title}/{_state.DisplayedFingerprint.SessionKey} deadline=+{IslandConfig.SkipTransitionTimeoutMs}ms");
            ReconcileDisplay(null);
        }

        private void HandleUserSelect(UserSelectSessionEvent evt)
        {
            // Source=SessionSelect: the user picked a specific, already-visible
            // session. There is no Chrome-side ambiguity (the target is known
            // and stable), so the resulting fingerprint change must animate
            // immediately — Confirming would strand the scroll-picker's slide
            // animation for 250ms and feel unresponsive.
            _state.Intent = new SwitchIntent(
                _state.DisplayedFingerprint,
                evt.Direction,
                _context.NowUtc + TimeSpan.FromMilliseconds(IslandConfig.SkipTransitionTimeoutMs),
                SwitchIntentSource.SessionSelect);
            Logger.Debug($"[Machine] UserSelect '{evt.SessionKey}' dir={evt.Direction}");
            ReconcileDisplay(null);
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
            // The dispatcher ran AiOverridePolicy.OnEvent for this event just
            // before us. The policy does not read evt.Result directly; it
            // re-queries the resolver cache (now populated by the completed
            // resolve) and rebuilds context.AiOverrideLookup so EmitFrame
            // below attaches the newly cached override to the emitted frame.
            // Transition=None / same fingerprint keeps views from sliding —
            // they just refresh the override label/artist in place.
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

        private void ReconcileDisplay(PresentationEvent? trigger)
        {
            // Compute what the display should look like given the latest
            // arbiter decision + sessions.
            var winnerKey = _context.ArbitratedWinnerKey;
            MediaSessionSnapshot? winner = FindSession(_context.Sessions, winnerKey);

            // P3b-2: if we're currently in a Confirming settle, service it
            // before any other display logic. Overlay path still wins if a
            // notification is active — notifications freeze the main visual
            // and Confirming's draft continues to evolve only implicitly via
            // the inner-state path.
            if (_state.IsConfirming && !_state.IsNotifying)
            {
                if (ServiceConfirming(winner, trigger))
                {
                    // Handled end-to-end by ServiceConfirming: either still
                    // settling / draft reset (no frame), or released and the
                    // confirmed Steady frame was already emitted.
                    return;
                }
                // False ⇒ winner re-entered stabilization; draft was dropped.
                // Fall through so the normal reconcile path emits the
                // Switching kind for this new raw state.
            }

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

            // P3b-2 + P4d-2: detour into Confirming on fp change either when
            // stabilization has just ended (previous kind was Switching) OR
            // when a still-valid SwitchIntent sourced from a SKIP is in flight
            // (see intentForcesConfirming below). Direct Steady→Steady fp
            // changes with no skip intent and no observed Switching phase
            // (e.g. tests' simulated track replacement, natural-end flips)
            // still emit immediately. Session-select intents also bypass
            // Confirming — the target is known and the 250ms settle would
            // only strand the scroll animation.
            bool stabilizationJustEnded =
                _state.DisplayedKind == PresentationKind.Switching
                && kind == PresentationKind.Steady;
            // Only detour through Confirming when the logical identity
            // (session+title+artist) actually changed; a pure thumbnail-hash
            // update on the same track should not trigger a 250ms settle.
            bool identityChangedForConfirming =
                !FingerprintIdentityEquals(fingerprint, _state.DisplayedFingerprint);
            // A fresh, still-valid SwitchIntent sourced from a SKIP signals
            // "user just clicked skip and a switch is coming". When the first
            // post-click fp change arrives we must ALWAYS absorb it through
            // Confirming, even if MediaService released stabilization so fast
            // we never observed a Switching kind transition. This prevents
            // Chrome's brief paused-tab or wrong-tab flash from leaking onto
            // the UI: if the initial candidate bounces to the real next track
            // within the 250ms window, the draft resets and the user sees a
            // single clean A->C transition rather than A->B->C.
            //
            // Session-select intents (user picked a visible session from the
            // picker) must NOT force Confirming — the target is known and
            // stable, so the scroll animation must fire immediately.
            bool intentForcesConfirming = _state.Intent.HasValue
                && _state.Intent.Value.Source == SwitchIntentSource.Skip
                && !_state.Intent.Value.IsExpired(_context.NowUtc)
                && _state.Intent.Value.MatchesOrigin(_state.DisplayedFingerprint)
                && kind == PresentationKind.Steady;
            if ((stabilizationJustEnded || intentForcesConfirming)
                && fpChanged && identityChangedForConfirming && !firstFrame)
            {
                EnterConfirming(winner!.Value, fingerprint);
                return;
            }

            if (firstFrame && kind != PresentationKind.Empty)
            {
                transition = FrameTransitionKind.Replace;
            }
            else if (fpChanged)
            {
                // §6 invariant #6: a thumbnail-hash-only change for the same
                // logical track (same session+title+artist) must NOT produce a
                // directional Slide or Replace. ThumbnailHash is computed
                // asynchronously so it commonly lands a few hundred ms after
                // the title/artist write; treating that as a new track would
                // emit a spurious second frame (same song "flashes" or loses
                // its already-played slide).
                bool identityChanged = !FingerprintIdentityEquals(fingerprint, _state.DisplayedFingerprint);
                if (!identityChanged)
                {
                    transition = FrameTransitionKind.None;
                }
                else if (_state.Intent.HasValue
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
            // Detect same-identity snapshot refreshes (e.g. user pauses/resumes
            // or progress advances) so the UI can update its pause icon / time
            // readout even when fp/kind are unchanged. Such frames carry
            // Transition=None.
            bool snapshotChanged = !SnapshotEquals(_state.DisplayedSnapshot, snapshotForFrame);
            _state.DisplayedSnapshot = snapshotForFrame;
            _state.DisplayedKind = kind;
            _state.DisplayedFingerprint = fingerprint;

            if (firstFrame || fpChanged || kindChanged || snapshotChanged)
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

        private static bool SnapshotEquals(MediaSessionSnapshot? a, MediaSessionSnapshot? b)
        {
            if (!a.HasValue && !b.HasValue) return true;
            if (!a.HasValue || !b.HasValue) return false;
            var x = a.Value;
            var y = b.Value;
            // Compare UI-visible fields only. LastSeenUtc/LastActivityUtc tick
            // on every refresh so they must NOT participate in equality.
            return x.PlaybackStatus == y.PlaybackStatus
                && x.Presence == y.Presence
                && x.HasTimeline == y.HasTimeline
                && x.DurationSeconds.Equals(y.DurationSeconds)
                && x.IsSystemCurrent == y.IsSystemCurrent
                && x.StabilizationReason == y.StabilizationReason;
        }

        // P3b-2 helpers ------------------------------------------------------

        private void EnterConfirming(MediaSessionSnapshot draft, MediaTrackFingerprint draftFp)
        {
            _state.IsConfirming = true;
            _state.ConfirmingDraftSnapshot = draft;
            _state.ConfirmingDraftFingerprint = draftFp;
            _state.ConfirmingFirstSeenUtc = _context.NowUtc;
            _state.DisplayedKind = PresentationKind.Confirming;

            Logger.Debug($"[Machine] enter Confirming draft=[{DescribeFingerprint(draftFp)}] settleMs={IslandConfig.MediaMetadataSettleMs}");

            _context.ScheduleMetadataSettleTimer(
                _context.NowUtc + TimeSpan.FromMilliseconds(IslandConfig.MediaMetadataSettleMs));

            // Emit a Confirming frame so the UI can update its chip text.
            // Fingerprint stays at the displayed value so invariant #3 holds.
            EmitFrame(
                session: _state.DisplayedSnapshot,
                orderedSessions: _context.Sessions,
                kind: PresentationKind.Confirming,
                transition: FrameTransitionKind.None,
                fingerprint: _state.DisplayedFingerprint,
                isFallback: false,
                notification: null);
        }

        /// <summary>
        /// Processes an event while IsConfirming is active.
        /// <para>Returns <c>true</c> when the caller should NOT run the normal
        /// reconcile path. That covers both "still settling / draft reset"
        /// AND "released and this method already emitted the Steady frame".
        /// </para>
        /// <para>Returns <c>false</c> only when the winner re-entered
        /// stabilization (draft is discarded) so the caller falls through to
        /// the normal path to emit the <c>Switching</c> kind frame.</para>
        /// </summary>
        private bool ServiceConfirming(MediaSessionSnapshot? winner, PresentationEvent? trigger)
        {
            // Re-entering stabilization while confirming: drop draft.
            if (!winner.HasValue || winner.Value.IsStabilizing)
            {
                Logger.Debug("[Machine] Confirming: winner re-entered stabilization, dropping draft");
                ClearConfirming();
                return false; // let normal path emit Switching
            }

            var rawFp = BuildFingerprint(winner.Value);
            // §4.6: thumbnail writes do not affect settle judgement. Compare
            // on identity (session+title+artist); thumbnail hash arriving for
            // the same logical draft is simply absorbed into the stored draft.
            bool sameAsDraft = FingerprintIdentityEquals(rawFp, _state.ConfirmingDraftFingerprint);
            bool timerFired = trigger is MetadataSettleTimerFiredEvent;
            var elapsed = _context.NowUtc - _state.ConfirmingFirstSeenUtc;
            var settle = TimeSpan.FromMilliseconds(IslandConfig.MediaMetadataSettleMs);

            if (!sameAsDraft)
            {
                // Draft bounced — reset window with the new candidate.
                Logger.Debug($"[Machine] Confirming: draft mismatch, resetting to [{DescribeFingerprint(rawFp)}]");
                _state.ConfirmingDraftSnapshot = winner;
                _state.ConfirmingDraftFingerprint = rawFp;
                _state.ConfirmingFirstSeenUtc = _context.NowUtc;
                _context.ScheduleMetadataSettleTimer(_context.NowUtc + settle);
                return true;
            }

            // Identity matches: absorb any updated thumbnail hash into the
            // stored draft so the released Steady frame carries the latest
            // fingerprint.
            if (!FingerprintEquals(rawFp, _state.ConfirmingDraftFingerprint))
            {
                _state.ConfirmingDraftSnapshot = winner;
                _state.ConfirmingDraftFingerprint = rawFp;
            }

            if (!timerFired && elapsed < settle)
            {
                // Still settling. No-op.
                return true;
            }

            // Release: consume draft as the new displayed fp.
            Logger.Debug($"[Machine] Confirming: release after {elapsed.TotalMilliseconds:F0}ms, fp=[{DescribeFingerprint(rawFp)}]");

            MediaTrackFingerprint draftFp = _state.ConfirmingDraftFingerprint;
            MediaSessionSnapshot? draftSnap = _state.ConfirmingDraftSnapshot;
            ClearConfirming();

            FrameTransitionKind transition;
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
                _state.Intent = null;
            }
            else
            {
                transition = FrameTransitionKind.Replace;
                if (_state.Intent.HasValue && _state.Intent.Value.IsExpired(_context.NowUtc))
                    _state.Intent = null;
            }

            _state.DisplayedSnapshot = draftSnap;
            _state.DisplayedKind = PresentationKind.Steady;
            _state.DisplayedFingerprint = draftFp;
            _state.Initialized = true;
            EmitFrame(
                session: draftSnap,
                orderedSessions: _context.Sessions,
                kind: PresentationKind.Steady,
                transition: transition,
                fingerprint: draftFp,
                isFallback: false,
                notification: null);
            return true;
        }

        private void ClearConfirming()
        {
            _state.IsConfirming = false;
            _state.ConfirmingDraftSnapshot = null;
            _state.ConfirmingDraftFingerprint = MediaTrackFingerprint.Empty;
            _state.ConfirmingFirstSeenUtc = default;
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

            // Pick the AI override keyed off the snapshot the frame actually
            // carries so the override matches whatever the view is about to
            // render. During Confirming the emitted session is the previous
            // (old) one, so using _context.ActiveAiOverride (which tracks the
            // arbiter winner, i.e. the new session) would tag the old snapshot
            // with the new track's override — causing a one-frame title/artist
            // flash of raw-on-old before the Slide animation.
            AiOverrideSnapshot? frameAiOverride = null;
            if (session.HasValue && _context.AiOverrideLookup is { } lookup)
            {
                frameAiOverride = lookup(session.Value);
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
                AiOverride: frameAiOverride,
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

            // Invariant #3: fp change ⇒ transition != None. Exceptions:
            //   (a) the very first emit from an Empty baseline to another
            //       Empty baseline (startup idle);
            //   (b) thumbnail-hash-only change for the same logical identity
            //       (§6 #6 — thumbnail writes do not produce a directional
            //       animation because the visible track didn't change).
            bool baselineWasEmpty = _lastEmittedFingerprint.IsEmpty;
            bool newIsEmpty = fingerprint.IsEmpty;
            bool identityChanged =
                !string.Equals(_lastEmittedFingerprint.SessionKey, fingerprint.SessionKey, StringComparison.Ordinal)
                || !string.Equals(_lastEmittedFingerprint.Title, fingerprint.Title, StringComparison.Ordinal)
                || !string.Equals(_lastEmittedFingerprint.Artist, fingerprint.Artist, StringComparison.Ordinal);
            System.Diagnostics.Debug.Assert(
                !fpChanged || transition != FrameTransitionKind.None
                    || (baselineWasEmpty && newIsEmpty)
                    || !identityChanged,
                $"Invariant #3 violated: fingerprint identity changed but transition=None (fp_from={DescribeFingerprint(_lastEmittedFingerprint)}, fp_to={DescribeFingerprint(fingerprint)})");

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

        private MediaTrackFingerprint BuildFingerprint(MediaSessionSnapshot session)
        {
            // P3: ThumbnailHash is xxhash64(first 4KB) populated asynchronously by
            // MediaService after the thumbnail reference updates. Empty until the
            // first compute completes; that is treated as its own fingerprint
            // value so a hash arriving on a later dispatch surfaces as a fp
            // change and can trigger the appropriate frame.
            //
            // P4 safety net: absorb a transient empty ThumbnailHash on the same
            // logical track. Upstream (MediaService.Refresh) already keeps the old
            // hash when a thumbnail stream-reference is reissued with identical
            // bytes, but an empty hash can still legitimately appear before any
            // art is known. If identity (session+title+artist) matches whatever
            // the machine last displayed with a non-empty hash, carry that hash
            // forward so a future refactor upstream can't accidentally reintroduce
            // hash="" fingerprint churn for the currently-shown track.
            string hash = session.ThumbnailHash ?? string.Empty;
            if (string.IsNullOrEmpty(hash)
                && !string.IsNullOrEmpty(_state.DisplayedFingerprint.ThumbnailHash)
                && string.Equals(session.SessionKey ?? string.Empty,
                    _state.DisplayedFingerprint.SessionKey, StringComparison.Ordinal)
                && string.Equals(session.Title ?? string.Empty,
                    _state.DisplayedFingerprint.Title, StringComparison.Ordinal)
                && string.Equals(session.Artist ?? string.Empty,
                    _state.DisplayedFingerprint.Artist, StringComparison.Ordinal))
            {
                hash = _state.DisplayedFingerprint.ThumbnailHash;
            }
            return new MediaTrackFingerprint(
                session.SessionKey ?? string.Empty,
                session.Title ?? string.Empty,
                session.Artist ?? string.Empty,
                hash);
        }

        private static bool FingerprintEquals(MediaTrackFingerprint a, MediaTrackFingerprint b)
            => string.Equals(a.SessionKey, b.SessionKey, StringComparison.Ordinal)
            && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && string.Equals(a.Artist, b.Artist, StringComparison.Ordinal)
            && string.Equals(a.ThumbnailHash, b.ThumbnailHash, StringComparison.Ordinal);

        /// <summary>
        /// Identity comparison that ignores <see cref="MediaTrackFingerprint.ThumbnailHash"/>.
        /// Used to detect cases where the thumbnail arrived/changed for the SAME
        /// logical track (same session+title+artist): those should not force a
        /// Replace transition nor reset the Confirming settle window (§4.6, §6 #6).
        /// </summary>
        private static bool FingerprintIdentityEquals(MediaTrackFingerprint a, MediaTrackFingerprint b)
            => string.Equals(a.SessionKey, b.SessionKey, StringComparison.Ordinal)
            && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && string.Equals(a.Artist, b.Artist, StringComparison.Ordinal);

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
            var local = dueUtc;
            _dispatcherPoster.Post(() => MetadataSettleTimerScheduleRequested?.Invoke(local));
        }

        private void ScheduleAutoFocusTimer(DateTimeOffset? dueUtc)
        {
            Logger.Trace($"[Machine] ScheduleAutoFocusTimer due={dueUtc?.ToString("HH:mm:ss.fff") ?? "-"}");
            // Post to the UI dispatcher so timer restart happens on the UI
            // thread (DispatcherTimer requires its owner's thread).
            var local = dueUtc;
            _dispatcherPoster.Post(() => AutoFocusTimerScheduleRequested?.Invoke(local));
        }

        private void ScheduleManualLockExpiryTimer(DateTimeOffset? dueUtc)
        {
            Logger.Trace($"[Machine] ScheduleManualLockExpiryTimer due={dueUtc?.ToString("HH:mm:ss.fff") ?? "-"}");
            var local = dueUtc;
            _dispatcherPoster.Post(() => ManualLockExpiryScheduleRequested?.Invoke(local));
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

