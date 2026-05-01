# Wisland Media Presentation Architecture — Design

Status: **Implemented** (P1 through P5 + P4d-2 follow-ups are merged on
`album-view`).
Original goal: consolidate the "what media is currently being shown, how does
it transition, how do we absorb jitter" logic that used to be scattered across
`MediaService`, `MainWindow.Media`, `IslandController`, `AiSongResolver`, and
`Notifications` into a single source of truth — `MediaPresentationMachine` —
so the two long-standing intermittent bugs are eliminated for good:

1. The expanded view's slide animation would sometimes fail to fire on skip
   (the new track metadata replaced in place, no motion).
2. The moment of the skip occasionally leaked another Chrome tab's paused
   metadata / thumbnail.

---

## 1. Legacy chain inventory

### 1.1 Raw capture — `Services/Media/MediaService*` + `Refresh`
Subscribes to GSMTC, maintains `TrackedSource` with raw title / artist /
thumbnail / playback / timeline / presence / missingSince fields, and
broadcasts `SessionsChanged` / `TrackChanged` through
`PrepareStateChange_NoLock + DispatchChange` when needed.

### 1.2 Skip / end-of-track stabilization — `MediaService.Stabilization`
Combines three reasons:
- `SkipTransition`: after the user taps skip, shields the UI from the
  paused-tab metadata another Chrome tab might briefly push.
- `NaturalEnding`: armed near end-of-track to swallow tail noise.
- A short `StabilizationMetadataConfirmationHoldMs=80ms` fresh-track hold
  for non-metadata writes.
Shares `FrozenSnapshot`, `StabilizationBaselineTitle/Artist`,
`StabilizationExpiresAtUtc`, and `_stabilizationTimer`.

### 1.3 Focus arbitration — `MediaFocusArbiter`
Decides which session to display across multiple GSMTC sources, with
`autoSwitchDebounce` + `missingSourceGrace`. Internally tracks
`_pendingAutoWinnerKey / _pendingAutoWinnerSinceUtc`.

### 1.4 User-selection lock — `MainWindow.Media`
`_selectedSessionKey / _selectionLockUntilUtc / _selectionLockTimer`. After a
scroll or picker click, the arbiter cannot steal focus for a configured
window.

### 1.5 Visual ordering — `_sessionVisualOrderKeys / GetVisualOrderedSessions`
Keeps the avatar strip and session picker stable across churn.

### 1.6 Auto-focus timer — `_autoFocusTimer / SyncAutoFocusTimer`

### 1.7 Transition direction intent —
`_pendingMediaTransitionDirection + _pendingMediaTransitionTimestamp`.
One-shot token with `TrackSwitchIntentWindowMs=1600ms`; written by
`SkipNext_Click / SkipPrevious_Click / TryCycleDisplayedSession /
SelectSession`, consumed by the first `contentChanged`.

### 1.8 UI identity dedup —
`_lastDisplayedContentIdentity / _lastDisplayedProgressIdentity`.
String hash that at the time also folded `(switching|steady)` into the
identity.

### 1.9 AI rewrite — `AiSongResolverService` + `ApplyAiOverride` +
`_aiResolveCts`. Async rewrite on `(sourceId, title, artist)`, with
`_lastAiResolveContentIdentity / _lastAiOverrideLookupIdentity /
_lastAiOverrideLookupResult`.

### 1.10 Notification overlay — `MainWindow.Notifications` +
`IslandController.IsNotifying`. While `ShowNotification` is active
`SyncMediaUI` did not call `UpdateMedia`, and there was no "replay the
animation" pass after the notification ended.

### 1.11 Progress reset — `RequestMediaProgressReset` /
`_isMediaProgressResetPending` / `IslandProgressBar.SnapToZero`.
A separate identity `_lastDisplayedProgressIdentity` running parallel to the
main one.

### 1.12 Album art / palette —
`ImmersiveMediaView._lastAlbumArtIdentity / _isBusyTransport` +
`AlbumArtColorExtractor`. The view decided on its own when to keep the old
cover. `BuildAlbumArtIdentity` was its own private notion of identity.

### 1.13 Session Picker overlay — `MainWindow.SessionPicker` +
`SessionPickerWindow`. Uses `IsTransientSurfaceOpen` to influence the island
controller.

### 1.14 Island shape — `IslandController`.
Inputs: `IsHovered / IsDragging / IsDocked / IsNotifying /
IsForegroundMaximized / IsHoverPending / IsTransientSurfaceOpen /
UseImmersiveDimensions`. Outputs: target W/H/Y/opacity.

### 1.15 Foreground monitor — `ForegroundWindowMonitor` →
`IsForegroundMaximized`.

### 1.16 Render-loop throttling — `UpdateRenderLoopState`.

---

## 2. Conflict points in the legacy design

| # | Conflict / gap | Symptom |
|---|---|---|
| C1 | `CreateContentIdentity` folded `switching\|steady` into the identity | During Pending, any `SessionsChanged` consumed the pending direction on the `switching` jump; the real track change fell through to `ApplyImmediately` — **animation lost** |
| C2 | `TrackSwitchIntentWindowMs=1600ms` vs `SkipTransitionTimeoutMs=10000ms` | On Chrome's slow skip the intent expired first — **animation lost** |
| C3 | The `IsNotifying` branch skipped `UpdateMedia` but still updated `_lastDisplayedContentIdentity` and called `ClearPendingMediaTransitionDirection` | A track that arrived while the notification was up was **silently applied forever** |
| C4 | The non-metadata-write fresh-track short hold was only 80ms | Shorter than Chrome's actual metadata delivery delay — when it expired, raw paused-tab state **leaked through** |
| C5 | `ShouldShowTransportSwitchingHint=IsStabilizing` vs the UI's `showBusyTransportState = IsStabilizing && MissingSinceUtc.HasValue` | Identity said "switching" but the UI guard was not on; album art / subtitle could update too early |
| C6 | `tracked.Thumbnail` was still overwritten by raw during stabilization, and `ArmSkipStabilization`'s re-arm captured the current raw into the frozen baseline | Rapid double-skip would seal tab B's thumbnail/title into the frozen baseline and leak it on release |

---

## 3. Responsibility grouping

| Group | Members |
|---|---|
| **Core state that decides "what media is currently shown"** | 1.1 / 1.2 / 1.3 / 1.4 / 1.6 / 1.7 / 1.8 / 1.9 / 1.10 / 1.11 / 1.12 |
| **Island shape / interaction** (consumes state, does not produce media state) | 1.13 / 1.14 / 1.15 / 1.16 |
| **Layout helpers** | 1.5 |

All "core state" should live in `MediaPresentationMachine` and its policies;
outer layers subscribe to `Frame` only.

---

## 4. Target architecture

### 4.1 Namespace layout

```
Services/Media/
  MediaService (raw data layer, unchanged)
  Presentation/
    MediaPresentationMachine       // single-threaded event-driven core
    MediaPresentationFrame         // the public frame contract
    MediaTrackFingerprint          // Session x Title x Artist x Thumbnail
    PresentationKind               // Steady | Switching | Confirming | Missing | Empty | Notifying
    FrameTransitionKind            // None | Replace | Slide(direction) | Crossfade | ResumeAfterNotification
    SwitchIntent                   // Origin + Direction + DeadlineUtc + Source
    Policies/
      FocusArbitrationPolicy       // wraps MediaFocusArbiter
      ManualSelectionLockPolicy    // the old selection-lock fields
      StabilizationPolicy          // placeholder; shipped stabilization is still projected from MediaService snapshots
      AiOverridePolicy             // was AiSongResolver injection code
      NotificationOverlayPolicy    // was the notification overlay logic
```

### 4.2 State set

```
Idle
Steady(track, orderedSessions)
PendingUserSwitch(frozen, intent, deadlineUtc)
PendingNaturalSwitch(frozen, deadlineUtc)
Confirming(draft, firstSeenUtc, lastConfirmedUtc, pendingThumbnail, carriedIntent?)
NotifyingOverlay(inner: State)      // orthogonal wrapper
```

### 4.3 Event set

```
GsmtcSessionsChanged(snapshot[])
UserSkipRequested(direction)
UserSelectSession(key, direction)
UserManualUnlock()
AutoFocusTimerFired
StabilizationTimerFired
MetadataSettleTimerFired
AiResolveCompleted(result)
NotificationBegin(payload)
NotificationEnd
SettingsChanged(scope)
```

Every event is consumed on a single thread (`Channel<TEvent>` + a dedicated
worker, or a dispatcher post-back).

### 4.4 Key transitions

| From | Event | Guard | To | Action |
|---|---|---|---|---|
| `Idle` | `GsmtcSessionsChanged` | sessions >= 1 | `Steady` | pick winner via `FocusArbitrationPolicy`, emit `Replace` frame |
| `Steady` | `UserSkipRequested` | session can be stabilized | `PendingUserSwitch` | capture frozen; `intent = SwitchIntent(fingerprint, direction, now + SkipTransitionTimeoutMs, Skip)` |
| `Steady` | `GsmtcSessionsChanged` | shown session is near end + metadata about to change | `PendingNaturalSwitch` | capture frozen |
| `Steady` | `GsmtcSessionsChanged` | focus needs to switch (arbiter decision) | `Steady` (new track) | `Transition = Replace` (no user intent) |
| `PendingUser/NaturalSwitch` | `GsmtcSessionsChanged` | raw looks like a paused tab (Paused, or pos far from 0, or empty title) | self | **do not change baseline, do not emit**, do not refresh thumbnail baseline |
| `PendingUser/NaturalSwitch` | `GsmtcSessionsChanged` | raw Playing + pos<=3s + title differs from baseline + title is concrete | `Confirming` | `draft = raw`, `firstSeenUtc = now`, intent preserved |
| `PendingUser/NaturalSwitch` | `StabilizationTimerFired` | deadline reached | `Steady(raw, fallback=true)` | if intent not expired → `Slide(direction)`; otherwise `Replace` |
| `Confirming` | `GsmtcSessionsChanged` | raw differs from draft | `PendingUserSwitch` (intent preserved) | drop draft |
| `Confirming` | `GsmtcSessionsChanged / MetadataSettleTimerFired` | `(now - firstSeenUtc) >= MetadataSettleMs` | `Steady(confirmed)` | emit `Slide(intent.direction)` or `Replace`; promote `pendingThumbnail` to thumbnail; consume intent |
| any | `NotificationBegin` |  | `NotifyingOverlay(inner=current)` | emit `FlagsOnly` kind=Notifying |
| `NotifyingOverlay` | `NotificationEnd` |  | inner (which may itself have migrated to a new Steady internally) | emit `ResumeAfterNotification` |
| `NotifyingOverlay` | other events |  | recurse into inner | inner migrates offscreen; no frame emitted until Resume collapses everything |

### 4.5 `SwitchIntent` lifecycle

```csharp
record SwitchIntent(
    MediaTrackFingerprint Origin,
    ContentTransitionDirection Direction,
    DateTimeOffset DeadlineUtc,
    SwitchIntentSource Source); // Skip | SessionSelect

enum SwitchIntentSource { Skip, SessionSelect }
```

- Written by: `UserSkipRequested` (`Source = Skip`) / `UserSelectSession`
  (`Source = SessionSelect`).
- Retained while: `now <= Deadline && CurrentFingerprint == Origin`.
- Consumed on: `Confirming → Steady` where `newFingerprint != Origin`, OR the
  first `fpChanged` steady-to-steady transition after the intent was captured.
- Overwritten by: the next user skip / select (Origin is kept so consecutive
  clicks still animate from the original anchor).
- Dropped when: deadline expires, or fallback still finds it expired.
- **State-flag flips, other-session churn, and notification overlays never
  consume the intent.**

#### Why `Source` matters — the Confirming detour

`Confirming` exists to absorb Chrome's paused-tab metadata flash that can
appear for <=250ms between the user clicking skip and the real next track
arriving. A skip intent therefore *always* detours through Confirming on the
first fp change, even if `MediaService` released stabilization so quickly
that no `Switching` kind was observed.

A `SessionSelect` intent is fundamentally different: the user picked a
visible session from the picker, so the target is known, stable, and free
of Chrome paused-tab ambiguity. Forcing that intent through Confirming would
strand the scroll-to-switch animation for the 250ms settle window and feel
broken. `intentForcesConfirming` in `ReconcileDisplay` is therefore gated on
`Source == Skip`.

### 4.6 Confirming settle rule (replaces `StabilizationMetadataConfirmationHoldMs`)

Constants:

```
MetadataSettleMs                         = 250   // how long draft must stay consistent
StabilizationMetadataConfirmationHoldMs  = removed
SkipTransitionTimeoutMs                  = 10000 (unchanged)
NaturalEndingTransitionTimeoutMs         = 3000  (unchanged)
```

Logic:
- Enter `Confirming(draft, firstSeenUtc)`.
- On metadata write:
  - Matches draft → `lastConfirmedUtc = now`; if
    `now - firstSeenUtc >= MetadataSettleMs` → release.
  - Mismatches → go back to `PendingUserSwitch` (intent preserved), reset
    draft.
- On thumbnail: write into `pendingThumbnail`, does not affect settle check.
- If we hit the stabilization deadline without releasing → fallback.

### 4.7 Public contract

```csharp
public enum PresentationKind { Empty, Steady, Switching, Confirming, Missing, Notifying }
public enum FrameTransitionKind { None, Replace, SlideForward, SlideBackward, Crossfade, ResumeAfterNotification }

public readonly record struct MediaTrackFingerprint(
    string SessionKey, string Title, string Artist, string ThumbnailHash);

public sealed record MediaPresentationFrame(
    long Sequence,
    MediaSessionSnapshot Session,
    IReadOnlyList<MediaSessionSnapshot> OrderedSessions,
    int DisplayIndex,
    PresentationKind Kind,
    FrameTransitionKind Transition,
    MediaTrackFingerprint Fingerprint,
    MediaTrackFingerprint? ProgressFingerprint,
    bool IsFallback,
    AiOverrideSnapshot? AiOverride);
```

`ProgressFingerprint` is the progress-track identity, not the full visual
content identity. It is built from the raw `SessionKey + Title + Artist` of the
frame's own session and deliberately leaves `ThumbnailHash` empty. AI title
rewrites and async album-art hash arrivals must not reset progress.

**Invariants**:
1. `Sequence` strictly increases; the UI ignores stale frames.
2. `Transition != None ⇒ fingerprint changed OR Transition == ResumeAfterNotification`.
3. Fingerprint change ⇒ exactly one `Transition != None` is produced
   (no dropped animation opportunities).
4. `PresentationKind` changes (Steady→Switching→Confirming) without a
   fingerprint change ⇒ `Transition = None` (the UI only updates the chip,
   it does not move the main visual).
5. `Pending*` internal states do not emit frames (except while wrapped in
   `NotifyingOverlay` as inner pass-through, which is collapsed on Resume).
6. Thumbnail in `Pending/Confirming` only lives in `pendingThumbnail`; it
   never leaks into `Frame.Session`.
7. `Switching` frames keep the displayed fingerprint/metadata, but carry a
   display-safe stabilization snapshot for progress/presence. In particular,
   `SkipTransition` overlays `Progress=0` so immersive and classic progress UI
   reset immediately without showing transient raw metadata from another tab.
8. A progress-only timeline correction emits a refresh frame with
   `Transition=None` once the position delta is meaningful; views use that to
   re-anchor local progress tickers without treating it as a track change.

### 4.8 MainWindow.Media after the refactor

`MainWindow.Media.cs` keeps only:
- A subscription to `MediaPresentationMachine.FrameProduced`.
- Feeding the frame into `ExpandedMediaView.UpdateMedia(frame)` /
  `ImmersiveMediaView.UpdateMedia(frame)`.
- Driving from the frame:
  - `IslandController.UseImmersiveDimensions`
  - `UpdateRenderLoopState`
  - Progress reset (gated on raw `frame.ProgressFingerprint`, not rendered /
    AI-overridden title text)
- Forwarding user input events to the machine (`UserSkipRequested` /
  `UserSelectSession` / `UserManualUnlock`).

All removed: `_pendingMediaTransitionDirection`, `_selectedSessionKey`,
`_selectionLockUntilUtc`, `_sessionVisualOrderKeys`, and the `_ai*` fields.
`MainWindow` keeps only last-consumed frame/progress fingerprints so it can
detect whether the next frame should reset progress.

### 4.9 Tightening `IslandController`

- Rename `IsNotifying` → `IsForcedExpanded`, set only by
  `NotificationOverlayPolicy` through a narrow API.
- Session Picker and Notification code no longer reach into
  `_controller.IsNotifying` / `IsTransientSurfaceOpen` directly; they route
  through policy → controller.

---

## 5. Event-flow examples

### 5.1 Normal Chrome skip-next

```
t0      User clicks Skip
        Dispatch(UserSkipRequested(Forward))
        Steady(A) → PendingUserSwitch{ frozen=A, intent(A→?, Forward, deadline=t0+10s) }
        (no frame)
t0+300  Chrome pushes paused tab B's title
        guard: Paused & not fresh → stay Pending; baseline unchanged; pendingThumbnail untouched
        (no frame)
t0+800  Chrome pushes Playing + pos=0.1s, but raw.title is still B
        guard: title == baseline? if so, title hasn't flipped yet — stay Pending
t0+1100 Chrome pushes the real title C + Artist C
        guard: title=C, concrete, differs from baseline → Confirming{ draft=C, firstSeenUtc=t0+1100 }
        (no frame)
t0+1350 Chrome re-confirms C (or no metadata change but settle timer fires)
        now - firstSeenUtc = 250 ms >= MetadataSettleMs
        Confirming → Steady(C), Transition=SlideForward (intent still valid, matches origin)
        pendingThumbnail promoted to thumbnail
        emit Frame(Sequence+1, C, Kind=Steady, Transition=SlideForward)
```

UI gets `SlideForward` → slide animation plays. Even if Chrome is slow, the
intent deadline is 10s so animation is still picked up correctly.

### 5.2 Track change behind a notification overlay

```
t0      NotificationBegin(payload)
        Steady(A) → NotifyingOverlay(inner=Steady(A))
        emit Frame(Kind=Notifying, Transition=None)
t0+500  GSMTC moves to C (runs Pending → Confirming → Steady inside the overlay)
        inner: Steady(A) → ... → Steady(C, pendingTransition=Slide)
        no outward frame (overlay masks it)
t0+2500 NotificationEnd
        NotifyingOverlay → Steady(C)
        emit Frame(Kind=Steady, Transition=ResumeAfterNotification, Fingerprint=C)
```

On Resume the UI can pick `Crossfade` or `Slide` (strategy is configurable):
**the animation is never lost.**

### 5.3 Rapid double-skip

```
Skip(1): Steady(A) → PendingUserSwitch{ frozen=A, intent1(A→?, Forward, t1) }
         ArmSkipStabilization(): fresh arm, baseline=A @ pos_A
(800ms later, raw has already been updated by Chrome to paused tab B, but
baseline is unchanged)
Skip(2): UserSkipRequested(Forward)
         ArmSkipStabilization(): re-arm — ONLY extends expiry.
                                  baseline stays at A @ pos_A (never advanced).
         intent = SwitchIntent(A, Forward, new deadline t2)  ← Origin is still A
Eventually C arrives → Confirming → Steady(C), SlideForward
```

B is never sealed into the frozen baseline, and never leaks. Re-arm also
does not promote the frozen snapshot, so consecutive clicks cannot cause
the release guard to be compared against a near-zero baseline position
(see §11.8).

### 5.3b Very rapid ≥3 clicks — 10 s hang (fixed)

Previously a >3-skip-in-<100 ms burst could produce a ~10 s UI freeze
even though Chrome had already settled on the new track. Two stacked
causes, both in `MediaService.Stabilization`:

1. The re-arm path used to overwrite the baseline whenever `raw looks
   fresh`. After the first release (baseline=A@30s dropped, B becomes
   visible at pos≈0.5s), the second click's re-arm captured baseline=B
   @ 0.5s. The next real track C arrives at pos≈0s — but the release
   guard required `currentPos < baselinePos − 0.5s`, i.e. `0 < 0`,
   which can never be true. The gate stayed closed until
   `SkipTransitionTimeoutMs = 10 s`.
2. Even without baseline corruption, Chrome's first timeline report
   for a new track can land at 0.5–1.2 s due to GSMTC latency. When
   the baseline was naturally low (`~1 s`, first click hit just after
   a natural track change), the new-track write at `~0.7 s` still
   failed the margin-subtracted delta check.

Fixes:

- **(1)** `ArmSkipStabilization` re-arm now extends only the expiry;
  baseline + frozen snapshot are never promoted. Keeping the original
  baseline maximises the position delta a real new track has to clear.
  Paused-tab leaks remain blocked by `metadataDifferentFromBaseline`
  plus `LooksLikeFreshTrackShape`; we no longer rely on baseline
  promotion to guard against them.
- **(2)** `PositionLooksRestarted` now also accepts any strict drop
  (`currentPos < baselinePos`) as long as `currentPos` also lands
  within `SkipTransitionFreshTrackPositionSeconds (3 s)`. Because the
  caller's fresh-track shape check already requires `pos ≤ 3 s`, this
  broader rescue only applies to plausible restarts. The leak scenario
  (`currentPos ≥ baselinePos`) is still rejected by the strict-drop
  condition.

Observability: every rejection branch in
`ShouldReleaseStabilization_NoLock` now emits a structured
`Stabilization release denied for '...'` log (Trace for shape/presence/
metadata, Debug for position restart) so future regressions can be
pinpointed from logs alone.

### 5.4 Stabilization timeout fallback

```
Skip → PendingUserSwitch
... 10s elapse without a Playing + concrete title
StabilizationTimerFired → Steady(raw_fallback, IsFallback=true)
  if intent still valid → Transition=SlideForward
  else → Transition=Replace
```

The UI still gets an animation opportunity as long as the intent is intact.

---

## 6. Key invariants (can be written as debug assertions)

1. `frame.Sequence` strictly increases.
2. `frame.Transition == SlideForward | SlideBackward ⇒
    frame.Fingerprint != previous.Fingerprint`.
3. `previous.Fingerprint != frame.Fingerprint ⇒ frame.Transition != None`.
4. No frames while `state ∈ {Pending*, Confirming}` (except the overlay
   pass-through, which collapses into Resume).
5. `SwitchIntent` is kept only while
   `CurrentFingerprint == intent.Origin && now <= intent.Deadline`.
6. `frame.Session.Thumbnail` always comes from settled metadata, never raw.

---

## 7. Phased rollout

> Rollout status (as of this branch): P1 / P2 / P3a / P3b-1 / P3b-2 / P4a /
> P4b / P4c-1 / P4c-2a / P4c-2b / P4d / P4d-2 / P5-log / P5-assert /
> P5-tests are all merged.
> Remaining work: on-device validation and observation-driven tuning.

### P1 · Skeleton migration (behavior-neutral) — ✅
- Created the `Services/Media/Presentation/*` skeleton.
- Moved `MediaFocusArbiter / Stabilization / SelectionLock / AI override
  cache` into policies, preserving the original behavior.
- `MediaPresentationMachine` runs downstream of
  `MediaService.SessionsChanged` and emits frames; `MainWindow.Media` kept
  the old code path in parallel for A/B comparison.

### P2 · The three animation root causes — ✅
- Split `Fingerprint` and `PresentationKind`; deleted
  `CreateContentIdentity`.
- `SwitchIntent` replaced `_pendingMediaTransitionDirection`;
  `Deadline = SkipTransitionTimeoutMs`.
- `NotifyingOverlay` with pass-through and `ResumeAfterNotification` — the
  notification path never swallows identity again.
- Removed `TrackSwitchIntentWindowMs` (folded into intent deadline).

### P3 · The three leak root causes — ✅
- ✅ `ArmSkipStabilization` re-arm no longer advances the baseline at all
  (P3a + rapid-skip hardening). Previously the re-arm would promote
  baseline to the current raw when raw "looked fresh"; this caused a
  ~10 s gate hang after a third rapid click because the baseline's
  position got sealed near zero and the release guard could never
  satisfy `currentPos < baselinePos − margin`. Baseline is now only
  ever written on the initial fresh arm.
- ✅ `PositionLooksRestarted` accepts any strict drop that lands within
  `SkipTransitionFreshTrackPositionSeconds` in addition to the
  margin-delta path. Covers the case where Chrome's first timeline
  report for a new track lands at 0.5–1.2 s due to GSMTC latency;
  does not regress the tab-metadata leak scenario (`currentPos ≥
  baselinePos` is still rejected).
- ✅ Removed the `StabilizationMetadataConfirmationHoldMs=80ms` path (P3a).
- ✅ `MediaService` computes `ThumbnailHash` (xxhash64 of the first 4KB,
  async, cleared on ref-change) and folds it into `MediaTrackFingerprint`
  (P3b-1).
- ✅ `Confirming` state + `MediaMetadataSettleMs=250ms` (P3b-2); only
  engages on `Switching → Steady` fp change — direct Steady→Steady fp
  changes still emit immediately. Added
  `MetadataSettleTimerScheduleRequested` + a MainWindow-owned
  `_metadataSettleTimer`.

### P4 · View decoupling — ✅
- ✅ `ExpandedMediaView.UpdateMedia(frame)` /
  `ImmersiveMediaView.UpdateMedia(frame)` thin wrappers are in place;
  `ImmersiveMediaView._lastAlbumArtIdentity` uses `MediaTrackFingerprint`
  (P4a).
- ✅ Visual ordering moved inside the machine; `frame.OrderedSessions` is
  the stable order; MainWindow's `_sessionVisualOrderKeys` is deleted
  (P4b).
- ✅ `frame.Session` is the displayed session; MainWindow's `_focusArbiter`
  is deleted (P4c-1 / 2a).
- ✅ Manual-lock state (`_selectedSessionKey` / `_selectionLockUntilUtc`)
  lives entirely in `ManualSelectionLockPolicy`; MainWindow keeps only the
  UI-thread `DispatcherTimer` forwarder (P4c-2b).
- ✅ AI override moved into `AiOverridePolicy`; added `IAiOverrideResolver`
  + `AiOverrideResolverAdapter` that own the async resolve and dispatch
  `AiResolveCompletedEvent` back into the machine; MainWindow is down to
  `ApplyFrameAiOverride(frame.AiOverride)` and
  `SettingsChangedEvent(AiOverride)` dispatch (P4d).

### P4d-2 · Follow-ups (post-release regressions) — ✅

- ✅ `SwitchIntent` gained a `Source` discriminator
  (`Skip` | `SessionSelect`). `UserSelectSessionEvent` now bypasses the
  Confirming detour entirely so the scroll-to-switch-session animation
  fires immediately. Only `Skip` intents still force a Confirming pass
  against Chrome's paused-tab flicker hazard. Regression lock-in:
  `UserSelectSlidesImmediatelyWithoutConfirming`.
- ✅ `EmitFrame` now reads the AI override off the **frame's own snapshot**
  via `MediaPresentationMachineContext.AiOverrideLookup`
  (`Func<MediaSessionSnapshot, AiOverrideSnapshot?>`), not off
  `ActiveAiOverride` (which tracks the arbiter's winner). During
  `Confirming` the emitted session is the previous track; using the
  winner's override would tag the old snapshot with the next track's AI
  title/artist, producing a one-frame raw-on-old flash before the Slide.
  The lookup keys on `(SourceAppId, Title, Artist)` so same-SessionKey
  back-to-back tracks resolve independently. Regression lock-in:
  `ConfirmingFrameCarriesOldSessionsAiOverride`.

### P5 · Observability & tests — ✅
- ✅ Every event and every emit logs a structured line
  `{seq, state_from, state_to, transition, fp_from, fp_to, intent, notification, fallback}`
  (P5-log).
- ✅ `EmitFrame` now has `Debug.Assert` checks for §6 invariants #1 / #2 /
  #3; #4 / #5 / #6 remain at the policy layer (P5-assert).
- ✅ 7 extra P5 tests + 1 P3b-2 Confirming-bounce test (currently 86/86
  with the two P4d-2 regression tests added).

---

## 8. Mapping legacy components to new code

| Legacy | Target | Disposition |
|---|---|---|
| `MediaService.Stabilization.cs` | `MediaService` frozen snapshots + machine `Switching/Confirming` projection | `StabilizationPolicy` remains a placeholder |
| `MediaFocusArbiter` | `FocusArbitrationPolicy` | kept, signature unchanged |
| `_selectedSessionKey / _selectionLockUntilUtc / _selectionLockTimer` | `ManualSelectionLockPolicy` | removed from MainWindow |
| `_pendingMediaTransitionDirection / _pendingMediaTransitionTimestamp` | `SwitchIntent` | removed from MainWindow |
| `_lastDisplayedContentIdentity / _lastDisplayedProgressIdentity` | `Frame.Fingerprint / ProgressFingerprint` | replaced by last-consumed frame fingerprint caches |
| `_lastAiResolveContentIdentity / _lastAiOverrideLookupIdentity / _lastAiOverrideLookupResult` | internal cache in `AiOverridePolicy` | removed |
| `ShouldShowTransportSwitchingHint` / `CreateContentIdentity(...,switching)` | `PresentationKind.Switching` as its own field | functions removed |
| `_controller.IsNotifying` | `IslandController.IsForcedExpanded` + `NotificationOverlayPolicy` | narrowed |
| `_sessionVisualOrderKeys / GetVisualOrderedSessions` | internal to the machine | implementation kept |
| `_autoFocusTimer / SyncAutoFocusTimer` | scheduled by the machine | removed from MainWindow |
| `ImmersiveMediaView._lastAlbumArtIdentity / _isBusyTransport` | `frame.Fingerprint / frame.Kind == Switching` | simplified |
| `StabilizationMetadataConfirmationHoldMs` constant | removed | `MetadataSettleMs` took over |
| `TrackSwitchIntentWindowMs` constant | removed | `SkipTransitionTimeoutMs` took over |

---

## 9. Risks and trade-offs

1. **Threading model**: the machine must consume events on a single thread
   (`Channel<TEvent>` + dedicated worker). GSMTC events come from arbitrary
   threads; UI events come from the dispatcher queue; a dispatcher-bridge +
   back-post mechanism is required.
2. **AI rewrite timing**: on cache miss we emit the un-rewritten frame first,
   then on AI completion emit one more frame with `Transition=None` and the
   same `Kind` that only refreshes the title — so we never produce an extra
   Slide just for the AI rewrite.
3. **Dual-subscription during P1**: the old logic and the machine run in
   parallel; debug logs compare the frame stream against the old identity
   stream. Only after the A/B check passes do we flip to P2.
4. **NotifyingOverlay pass-through** is the subtlest piece of the design:
   when the inner state migrates multiple times behind the overlay, Resume
   emits only the final frame. If a valid intent plus changed fingerprint
   ended up there, Resume prefers `SlideX`; otherwise it degrades to
   `Crossfade`.
5. **Test coverage**: every migration step requires dedicated tests; the
   upfront cost is high but it pays off in eliminating the existing
   intermittent bugs permanently.
6. **Thumbnail hash**: `MediaTrackFingerprint.ThumbnailHash` cannot be
   reference-equality based (`IRandomAccessStreamReference` can re-issue
   the same bytes under a new reference). `MediaService` must compute a
   stable hash when it gets the thumbnail, otherwise "cover art changed
   but fingerprint didn't" is unsolvable.
7. **Thumbnail hash must never transiently empty for the currently
   displayed track** (added post-P4). Two mechanisms enforce this:
   - Upstream, `MediaService.Refresh.UpdateMediaPropertiesAsync` keeps
     `TrackedSource.ThumbnailHash` across a thumbnail reference swap —
     the async recompute (`ComputeAndStoreThumbnailHashAsync`) only
     dispatches a frame if the new bytes hash differently. The hash is
     cleared only when the thumbnail becomes `null`. This prevents GSMTC
     hosts (Chrome / Edge / Spotify) that reissue identical artwork as a
     new stream reference on pause / play / seek / SessionsChanged
     reshuffle from producing a transient `hash=""` fingerprint that
     views mistake for a real art change.
   - Downstream, `MediaPresentationMachine.BuildFingerprint` absorbs an
     empty `ThumbnailHash` on a snapshot whose identity
     (`SessionKey + Title + Artist`) matches the machine's last
     displayed fingerprint: the previously displayed non-empty hash is
     carried forward. This is a safety net — a future upstream
     regression that reintroduces hash churn cannot reach the frame
     stream as long as identity is unchanged.
   - An identity change (different title/artist/session) with
     `hash=""` still emits a fresh frame normally; absorption is
     strictly same-identity. Covered by
     `EmptyHashOnSameIdentityDoesNotChangeFingerprint` and
     `EmptyHashWithIdentityChangeStillEmitsFrame` in
     `MediaPresentationMachineTests`.

---

## 10. Decision points (resolved)

### D1 · Default animation for the Resume frame — **resolved**
- Valid `SwitchIntent` and fingerprint changed → `SlideForward /
  SlideBackward`.
- Otherwise (no track change behind the overlay, or intent expired) →
  `Crossfade`.
- Never no-op `Replace` (hard-cut Resume feels bad).
- Implementation note: the `NotifyingOverlay` inner needs to carry a
  "pending Slide" flag (the accumulated intent is consumed when Resume
  emits).

### D2 · `MetadataSettleMs` — **resolved, = 250 ms**
- Constant location: `IslandConfig.MediaMetadataSettleMs = 250` (tunable).
- Rationale: Spotify <100ms / YouTube Music 300–600ms / user perception
  threshold ~400ms.
- No dynamic settle — a fixed value is simpler and already covers 90% of
  cases; slow paths are caught by `SkipTransitionTimeoutMs` fallback.

### D3 · `ThumbnailHash` algorithm and trigger point — **resolved**
- Algorithm: **xxhash64 of the first 4 KB** of the thumbnail stream, async.
- Trigger: after `MediaService.UpdateMediaPropertiesAsync` receives a new
  thumbnail, it computes the hash asynchronously and writes it into
  `TrackedSource.pendingThumbnailHash`; only on `Confirming → Steady` is
  it promoted into the frame's `Fingerprint`.
- Fallback: `(Title, Artist, Duration, SourceAppId)` is a weak hash; the
  frame is marked `ThumbnailHashIsFallback = true`.
- Key constraint: the fingerprint must be ready before the frame is
  emitted, otherwise we hit "same content Slide" false positives.
- **P4 refinement (post-implementation)**: a thumbnail stream-reference
  swap alone no longer clears `TrackedSource.ThumbnailHash`; the old hash
  is preserved as an optimistic value and replaced atomically only when
  the async recompute produces a genuinely different hash (or the
  thumbnail becomes `null`). The machine additionally absorbs empty
  hashes that arrive on a snapshot with unchanged
  session+title+artist identity (see §6 item 7). Together these ensure
  that pause/play/seek/SessionsChanged never ripples through to the
  view's album-art / palette / blur reload path.

### D4 · `NotificationOverlayPolicy` scope — **resolved, responsibilities split**
- The policy owns: notification lifecycle (Begin/End/Duration/Cancel), the
  overlay semantics inside the machine, and the pass-through aggregation
  over the inner state.
- `MainWindow.Notifications.cs` still owns: turning a
  `NotificationPayload` into actual UI (text, header icon, duration
  resolution).
- Public contract: frames with `Kind = Notifying` carry a
  `NotificationPayload`; the view renders it.

### D5 · Machine `IDisposable` — **resolved, yes**
- The machine owns a `Channel<TEvent>`, a dedicated worker `Task`, two
  timers (stabilization / metadataSettle), and an AI-resolve in-flight
  token.
- `Dispose()` responsibilities: cancel the worker (CancellationToken),
  dispose the timers, cancel the AI request, unsubscribe from
  `MediaService`.
- Lifecycle order: on shutdown
  `Machine.Dispose() → MediaService.Dispose()` (otherwise the worker
  could receive stale events).
- Worker loop pattern: `await channel.Reader.WaitToReadAsync(ct)`, cancel
  the token for a clean exit.

---

## 11. API type signatures (final contract)

All types live under `wisland.Services.Media.Presentation` unless marked
otherwise. Model types live under `wisland.Models`.

### 11.1 Frame / Fingerprint / enums

```csharp
namespace wisland.Models;

public enum PresentationKind
{
    Empty,
    Steady,
    Switching,       // UI hint while a stabilized switch is in flight. The
                     // frame preserves the displayed fingerprint/metadata but
                     // may overlay safe live fields such as Progress=0 for a
                     // SkipTransition.
    Confirming,
    Missing,
    Notifying
}

public enum FrameTransitionKind
{
    None,
    Replace,
    SlideForward,
    SlideBackward,
    Crossfade,
    ResumeAfterNotification
}

public readonly record struct MediaTrackFingerprint(
    string SessionKey,
    string Title,
    string Artist,
    string ThumbnailHash)
{
    public static MediaTrackFingerprint Empty { get; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty);

    public static MediaTrackFingerprint From(MediaSessionSnapshot session, string thumbnailHash)
        => new(session.SessionKey, session.Title, session.Artist, thumbnailHash);
}

public sealed record AiOverrideSnapshot(string Title, string Artist);

public sealed record NotificationPayload(
    string Title,
    string Message,
    string Header,
    int DurationMs);

public sealed record MediaPresentationFrame(
    long Sequence,
    MediaSessionSnapshot? Session,
    IReadOnlyList<MediaSessionSnapshot> OrderedSessions,
    int DisplayIndex,
    PresentationKind Kind,
    FrameTransitionKind Transition,
    MediaTrackFingerprint Fingerprint,
    MediaTrackFingerprint? ProgressFingerprint,
    bool IsFallback,
    bool ThumbnailHashIsFallback,
    AiOverrideSnapshot? AiOverride,
    NotificationPayload? Notification);
```

`ProgressFingerprint` is nullable when no media session is displayed. When it
is present, `ThumbnailHash` is intentionally empty because progress identity is
raw session/title/artist only.

### 11.2 SwitchIntent

```csharp
namespace wisland.Services.Media.Presentation;

public enum SwitchIntentSource { Skip, SessionSelect }

public readonly record struct SwitchIntent(
    MediaTrackFingerprint Origin,
    ContentTransitionDirection Direction,
    DateTimeOffset DeadlineUtc,
    SwitchIntentSource Source = SwitchIntentSource.Skip)
{
    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc > DeadlineUtc;
    public bool MatchesOrigin(MediaTrackFingerprint current)
        => Origin.SessionKey == current.SessionKey
        && Origin.Title == current.Title
        && Origin.Artist == current.Artist;
}
```

### 11.3 Events

```csharp
namespace wisland.Services.Media.Presentation;

public abstract record PresentationEvent;

public sealed record GsmtcSessionsChangedEvent(IReadOnlyList<MediaSessionSnapshot> Sessions) : PresentationEvent;
public sealed record UserSkipRequestedEvent(ContentTransitionDirection Direction) : PresentationEvent;
public sealed record UserSelectSessionEvent(string SessionKey, ContentTransitionDirection Direction) : PresentationEvent;
public sealed record UserManualUnlockEvent : PresentationEvent;
public sealed record AutoFocusTimerFiredEvent : PresentationEvent;
public sealed record StabilizationTimerFiredEvent : PresentationEvent;
public sealed record MetadataSettleTimerFiredEvent : PresentationEvent;
public sealed record AiResolveCompletedEvent(string SourceAppId, string Title, string Artist, AiSongResult? Result) : PresentationEvent;
public sealed record NotificationBeginEvent(NotificationPayload Payload) : PresentationEvent;
public sealed record NotificationEndEvent : PresentationEvent;
public sealed record SettingsChangedEvent(SettingsChangeScope Scope) : PresentationEvent;

public enum SettingsChangeScope { AiOverride, Language, ImmersiveMode, Other }
```

### 11.4 Machine

```csharp
namespace wisland.Services.Media.Presentation;

public sealed class MediaPresentationMachine : IDisposable
{
    // Actual shipped constructor (as of album-view). Policies own their
    // service dependencies: FocusArbitrationPolicy wraps the arbiter and
    // AiOverridePolicy takes an IAiOverrideResolver injected from the host.
    // Stabilization is still supplied by MediaService snapshots; the machine
    // itself only needs the policy list + dispatcher.
    public MediaPresentationMachine(
        IReadOnlyList<IPresentationPolicy> policies,   // injection order = resolve order
        IDispatcherPoster dispatcherPoster);           // posts frames back to the UI thread

    public event Action<MediaPresentationFrame>? FrameProduced;

    // Timer-arming requests raised by policies; the host owns the actual
    // DispatcherTimer and dispatches the corresponding *TimerFiredEvent
    // back in when the timer elapses.
    public event Action<DateTimeOffset?>? AutoFocusTimerScheduleRequested;
    public event Action<DateTimeOffset?>? ManualLockExpiryScheduleRequested;
    public event Action<DateTimeOffset?>? MetadataSettleTimerScheduleRequested;

    // Input
    public void Dispatch(PresentationEvent evt);

    // Lifecycle
    public void Start();
    public void Dispose();
}

public interface IDispatcherPoster
{
    void Post(Action action);
}
```

### 11.5 Policy abstraction

```csharp
namespace wisland.Services.Media.Presentation;

public interface IPresentationPolicy
{
    // A stateful policy declares its read-interface for "shadow state" here.
    // The machine calls OnEvent at specific transitions to inject events or
    // query the policy. OnAttach receives the machine itself so policies can
    // subscribe to its schedule-request events (auto-focus / manual-lock /
    // metadata-settle timers) during attachment.
    void OnAttach(MediaPresentationMachine machine);
    void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context);
    void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context);
}

public sealed class MediaPresentationMachineContext
{
    public DateTimeOffset NowUtc { get; internal set; }
    public IReadOnlyList<MediaSessionSnapshot> Sessions { get; internal set; } = Array.Empty<MediaSessionSnapshot>();

    // Policy-writable: arbiter / manual lock / stabilization / ai / notification
    public string? ManualLockedSessionKey { get; internal set; }
    public bool HasManualLock { get; internal set; }
    public StabilizationDirective StabilizationDirective { get; internal set; }
    public AiOverrideSnapshot? ActiveAiOverride { get; internal set; }
    // Per-frame AI override lookup (see §12.4). Keyed on the snapshot itself.
    public Func<MediaSessionSnapshot, AiOverrideSnapshot?>? AiOverrideLookup { get; internal set; }
    public NotificationPayload? ActiveNotification { get; internal set; }

    // Machine utilities
    public void ScheduleStabilizationTimer(DateTimeOffset dueUtc);
    public void ScheduleMetadataSettleTimer(DateTimeOffset dueUtc);
    public void ScheduleAutoFocusTimer(DateTimeOffset? dueUtc);
}

public readonly record struct StabilizationDirective(
    MediaSessionStabilizationReason Reason,
    DateTimeOffset ExpiresAtUtc,
    MediaSessionSnapshot? FrozenSnapshot);
```

### 11.6 Concrete policy classes

```csharp
public sealed class FocusArbitrationPolicy : IPresentationPolicy
{
    public FocusArbitrationPolicy(MediaFocusArbiter inner);
    // The inner arbiter is retained verbatim; the policy only translates
    // events into Resolve() calls and writes winner into the context.
}

public sealed class ManualSelectionLockPolicy : IPresentationPolicy
{
    public ManualSelectionLockPolicy(TimeSpan lockDuration);
    // Replaces _selectedSessionKey / _selectionLockUntilUtc logic.
}

public sealed class StabilizationPolicy : IPresentationPolicy
{
    public StabilizationPolicy();
    // Placeholder in the shipped path. MediaService.Stabilization owns
    // SkipTransition / NaturalEnding / fresh-track gates; the machine reads
    // MediaSessionSnapshot.IsStabilizing and projects Switching/Confirming.
}

public sealed class AiOverridePolicy : IPresentationPolicy
{
    public AiOverridePolicy(IAiOverrideResolver resolver);
    // Maintains _lastAiResolveIdentity / _lastAiOverrideLookupIdentity /
    // _lastAiOverrideLookupResult equivalents internally; surfaces both
    // context.ActiveAiOverride (winner view) and context.AiOverrideLookup
    // (per-snapshot view used by EmitFrame — see §12.4).
}

public sealed class NotificationOverlayPolicy : IPresentationPolicy
{
    public NotificationOverlayPolicy();
    // Drives NotifyingOverlay wrapping + Resume emission.
}
```

### 11.7 Helper types

```csharp
// Thumbnail hash computation lives in MediaService, not in the machine.
namespace wisland.Services.Media;

internal static class ThumbnailHasher
{
    public static Task<string?> ComputeAsync(IRandomAccessStreamReference? reference);
    // ComputeThumbnailHashAsync(thumbnail): xxhash64 of first 4KB; null on failure.
}
```

---

## 12. Per-phase file-by-file change list

> Every phase can be merged and shipped independently. Numbers = change
> ordinal.

### P1 · Skeleton migration (behavior-neutral; dual subscription)

New files:
1. `Services/Media/Presentation/MediaPresentationMachine.cs` (skeleton + event
   loop; emits no frames yet)
2. `Services/Media/Presentation/MediaPresentationMachineContext.cs`
3. `Services/Media/Presentation/IPresentationPolicy.cs`
4. `Services/Media/Presentation/PresentationEvent.cs` (all records from
   §11.3)
5. `Services/Media/Presentation/SwitchIntent.cs`
6. `Services/Media/Presentation/Policies/FocusArbitrationPolicy.cs` (wraps
   `MediaFocusArbiter`)
7. `Services/Media/Presentation/Policies/ManualSelectionLockPolicy.cs`
8. `Services/Media/Presentation/Policies/StabilizationPolicy.cs` (placeholder
   in the shipped path; `MediaService.Stabilization` still owns the gate)
9. `Services/Media/Presentation/Policies/AiOverridePolicy.cs`
10. `Services/Media/Presentation/Policies/NotificationOverlayPolicy.cs`
11. `Models/MediaPresentationFrame.cs` (contains `PresentationKind`,
    `FrameTransitionKind`, `MediaTrackFingerprint`, `AiOverrideSnapshot`,
    `NotificationPayload`)
12. `wisland.Tests/MediaPresentationMachineTests.cs` (empty placeholder)

Changes:
13. `MainWindow.xaml.cs`: instantiate the machine but **do not consume its
    frames**; subscribe only to log.
14. `MainWindow.Lifetime.cs`: on shutdown call `machine.Dispose()` before
    `mediaService.Dispose()`.

Exit criterion: build + existing tests pass; the machine's event-stream log
can be compared with the old `SyncMediaUI` behavior.
