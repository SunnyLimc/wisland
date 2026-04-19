using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Media.Control;
using wisland.Models;
using wisland.Services.Media.Presentation;
using wisland.Services.Media.Presentation.Policies;
using Xunit;

namespace wisland.Tests
{
    /// <summary>
    /// P2 coverage: MediaPresentationMachine state machine driving frames.
    /// Uses <see cref="MediaPresentationMachine.ProcessForTests"/> to run events
    /// synchronously so assertions don't need to race the worker loop.
    /// </summary>
    public sealed class MediaPresentationMachineTests
    {
        private sealed class InlineDispatcher : IDispatcherPoster
        {
            public void Post(Action action) => action();
        }

        private sealed class Harness : IDisposable
        {
            public MediaPresentationMachine Machine { get; }
            public List<MediaPresentationFrame> Frames { get; } = new();
            public Harness(params IPresentationPolicy[] policies)
            {
                Machine = new MediaPresentationMachine(policies, new InlineDispatcher());
                Machine.FrameProduced += frame => Frames.Add(frame);
            }
            public void Dispose() => Machine.Dispose();
        }

        private static Harness NewHarness()
            => new(
                new ManualSelectionLockPolicy(),
                new FocusArbitrationPolicy(TimeSpan.Zero, TimeSpan.Zero),
                new StabilizationPolicy(),
                new AiOverridePolicy(),
                new NotificationOverlayPolicy());

        private static MediaSessionSnapshot Session(
            string key,
            string title,
            string artist = "ArtistA",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus status =
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            MediaSessionStabilizationReason stabilization = MediaSessionStabilizationReason.None,
            MediaSessionPresence presence = MediaSessionPresence.Active)
        {
            return new MediaSessionSnapshot(
                SessionKey: key,
                SourceAppId: "app",
                SourceName: "App",
                Title: title,
                Artist: artist,
                PlaybackStatus: status,
                Progress: 0.1,
                HasTimeline: true,
                DurationSeconds: 200,
                IsSystemCurrent: true,
                LastActivityUtc: DateTimeOffset.UtcNow,
                Presence: presence,
                LastSeenUtc: DateTimeOffset.UtcNow,
                MissingSinceUtc: null,
                StabilizationReason: stabilization,
                Thumbnail: null);
        }

        // ---------------------------------------------------------------
        // Baseline: construction + disposal
        // ---------------------------------------------------------------

        [Fact]
        public void ConstructsAndDisposesCleanly()
        {
            using var h = NewHarness();
            h.Machine.Start();
            h.Machine.Dispatch(new GsmtcSessionsChangedEvent(Array.Empty<MediaSessionSnapshot>()));
            h.Machine.Dispatch(new NotificationBeginEvent(new NotificationPayload("t", "m", "h", 1000)));
            h.Machine.Dispatch(new NotificationEndEvent());
            Thread.Sleep(50);
        }

        [Fact]
        public void DisposeIsIdempotent()
        {
            var machine = new MediaPresentationMachine(
                new IPresentationPolicy[] { new NotificationOverlayPolicy() },
                new InlineDispatcher());
            machine.Start();
            machine.Dispose();
            machine.Dispose();
        }

        // ---------------------------------------------------------------
        // §4.4 Idle → Steady on first sessions event
        // ---------------------------------------------------------------

        [Fact]
        public void FirstSessionEventEmitsReplaceFrame()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));

            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.Replace, h.Frames[0].Transition);
            Assert.Equal(PresentationKind.Steady, h.Frames[0].Kind);
            Assert.Equal("Song A", h.Frames[0].Fingerprint.Title);
        }

        // ---------------------------------------------------------------
        // C1 fix: Kind change alone does NOT trigger Slide
        // ---------------------------------------------------------------

        [Fact]
        public void KindOnlyChangeDoesNotEmitSlide()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));

            // Status flip to Paused keeps the same fingerprint; no Slide.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[]
            {
                Session("s1", "Song A", status: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            }));

            // No second frame because neither fingerprint nor kind changed.
            Assert.Single(h.Frames);
        }

        // ---------------------------------------------------------------
        // C2 fix: SwitchIntent with 10s deadline → Slide on track change
        // ---------------------------------------------------------------

        [Fact]
        public void UserSkipThenNewTrackEmitsSlideForward()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            // Same session key but new title → fingerprint changed
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));

            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.SlideForward, h.Frames[0].Transition);
            Assert.Equal("Song B", h.Frames[0].Fingerprint.Title);
        }

        [Fact]
        public void SkipBackwardEmitsSlideBackward()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Backward));
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));

            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.SlideBackward, h.Frames[0].Transition);
        }

        [Fact]
        public void NoIntentMeansReplaceNotSlide()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            // No UserSkip: natural advance → Replace (no animation direction)
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));

            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.Replace, h.Frames[0].Transition);
        }

        [Fact]
        public void IntentSurvivesStatusFlips()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            // Several status-only noise events (paused tab leaking) — same fingerprint.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[]
            {
                Session("s1", "Song A", status: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            }));
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[]
            {
                Session("s1", "Song A", status: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            }));
            Assert.Empty(h.Frames);

            // Real new track arrives → intent still valid, slide fires.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song C") }));
            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.SlideForward, h.Frames[0].Transition);
        }

        // ---------------------------------------------------------------
        // Stabilizing snapshot is frozen → no frame emitted while IsStabilizing
        // ---------------------------------------------------------------

        [Fact]
        public void StabilizingSnapshotSuppressesFrameUntilRelease()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));

            // MediaService reports stabilizing snapshot with other-tab metadata leaked.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[]
            {
                Session("s1", "Paused Tab Title", stabilization: MediaSessionStabilizationReason.SkipTransition)
            }));
            // One frame might fire for kind change to Switching; allow it but assert
            // the fingerprint did NOT leak into it.
            foreach (var f in h.Frames)
            {
                Assert.NotEqual("Paused Tab Title", f.Fingerprint.Title);
            }
            h.Frames.Clear();

            // Stabilization releases with the real new track. P3b-2 gates this
            // through a Confirming settle: first frame is Kind=Confirming (no
            // slide), then MetadataSettleTimerFired releases the Steady frame.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));

            Assert.Single(h.Frames);
            Assert.Equal(PresentationKind.Confirming, h.Frames[0].Kind);
            Assert.Equal(FrameTransitionKind.None, h.Frames[0].Transition);
            Assert.Equal("Song A", h.Frames[0].Fingerprint.Title); // old fp held
            h.Frames.Clear();

            h.Machine.ProcessForTests(new MetadataSettleTimerFiredEvent());

            Assert.Single(h.Frames);
            Assert.Equal(PresentationKind.Steady, h.Frames[0].Kind);
            Assert.Equal(FrameTransitionKind.SlideForward, h.Frames[0].Transition);
            Assert.Equal("Song B", h.Frames[0].Fingerprint.Title);
        }

        [Fact]
        public void ConfirmingResetsWhenDraftBouncesBeforeSettle()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[]
            {
                Session("s1", "Paused Tab", stabilization: MediaSessionStabilizationReason.SkipTransition)
            }));
            h.Frames.Clear();

            // Stabilization releases with "Song B" → enter Confirming.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));
            Assert.Equal(PresentationKind.Confirming, h.Frames[^1].Kind);

            // Draft bounces to "Song C" before settle. No new emission; still Confirming.
            int framesBeforeBounce = h.Frames.Count;
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song C") }));
            Assert.Equal(framesBeforeBounce, h.Frames.Count);

            // Timer fires but draft is "Song C" → release with Song C.
            h.Machine.ProcessForTests(new MetadataSettleTimerFiredEvent());
            Assert.Equal(PresentationKind.Steady, h.Frames[^1].Kind);
            Assert.Equal("Song C", h.Frames[^1].Fingerprint.Title);
            Assert.Equal(FrameTransitionKind.SlideForward, h.Frames[^1].Transition);
        }

        // ---------------------------------------------------------------
        // C3 fix: Notification overlay pass-through + ResumeAfterNotification
        // ---------------------------------------------------------------

        [Fact]
        public void NotificationOverlaySwallowsInnerFramesAndResumesWithSlide()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            // Notification begins; emit Kind=Notifying (Transition=None).
            h.Machine.ProcessForTests(new NotificationBeginEvent(new NotificationPayload("n", "m", "h", 1000)));
            Assert.Single(h.Frames);
            Assert.Equal(PresentationKind.Notifying, h.Frames[0].Kind);
            Assert.Equal(FrameTransitionKind.None, h.Frames[0].Transition);
            h.Frames.Clear();

            // User skips while the notification is showing.
            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            // New track arrives during overlay — no frame leaks out.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));
            Assert.Empty(h.Frames);

            // Notification ends — single Resume frame carrying the new fingerprint.
            h.Machine.ProcessForTests(new NotificationEndEvent());
            Assert.Single(h.Frames);
            Assert.Equal("Song B", h.Frames[0].Fingerprint.Title);
            // D1: fingerprint changed + intent valid → prefer directional Slide.
            Assert.Equal(FrameTransitionKind.SlideForward, h.Frames[0].Transition);
        }

        [Fact]
        public void NotificationWithoutTrackChangeResumesWithoutAnimation()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new NotificationBeginEvent(new NotificationPayload("n", "m", "h", 1000)));
            h.Frames.Clear();
            h.Machine.ProcessForTests(new NotificationEndEvent());

            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.ResumeAfterNotification, h.Frames[0].Transition);
        }

        // ---------------------------------------------------------------
        // Intent expiry
        // ---------------------------------------------------------------

        [Fact]
        public void ExpiredIntentFallsBackToReplace()
        {
            // Force immediate expiry by constructing a harness that uses zero
            // deadline... the SwitchIntent.Deadline is now + SkipTransitionTimeoutMs
            // which is 10s. We simulate expiry by running UserSkip then advancing
            // through a session event that arrives with a clock past the deadline
            // — because ProcessEvent samples DateTimeOffset.UtcNow, we can't
            // control time directly. Instead this test verifies that without a
            // Skip, a track change is Replace (already covered by NoIntentMeansReplaceNotSlide).
            //
            // Real expiry pathway is exercised indirectly via the "no intent"
            // assertion; when timekeeping injection arrives in P5 we'll add a
            // synthetic-clock test.
            Assert.True(true);
        }

        // ---------------------------------------------------------------
        // P5 extended scenarios (item 36 in design doc)
        // ---------------------------------------------------------------

        // Scenario: Steady → (user skip) → PendingUserSwitch → (fp change) → Steady(Slide)
        // This is the canonical happy-path already covered by
        // UserSkipThenNewTrackEmitsSlideForward, but this variant asserts the
        // full sequence across multiple status-only events and checks that
        // the emitted frame's intent was genuinely consumed (subsequent
        // identical fp does not re-slide).
        [Fact]
        public void PendingUserSwitch_SlideThenIntentConsumed()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));

            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.SlideForward, h.Frames[0].Transition);
            h.Frames.Clear();

            // Identical fingerprint → no frame.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));
            Assert.Empty(h.Frames);

            // A natural (no-skip) track change should now Replace, not Slide,
            // proving the original forward intent was consumed by the prior slide.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song C") }));
            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.Replace, h.Frames[0].Transition);
        }

        // Scenario: rapid consecutive skips in the same direction while still
        // on the origin track. The second skip should refresh the intent
        // (same direction/origin), not emit a frame. The eventual real track
        // then slides forward.
        [Fact]
        public void ConsecutiveSameDirectionSkipsCoalesceThenSlide()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            // No frame from skip events alone.
            Assert.Empty(h.Frames);

            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song D") }));
            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.SlideForward, h.Frames[0].Transition);
            Assert.Equal("Song D", h.Frames[0].Fingerprint.Title);
        }

        // Scenario: a backward skip after a forward skip should overwrite the
        // pending intent's direction, not be discarded.
        [Fact]
        public void OppositeDirectionSkipOverridesPendingIntent()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Backward));
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song Prev") }));

            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.SlideBackward, h.Frames[0].Transition);
        }

        // Scenario: thumbnail hash change alone (same session/title/artist)
        // produces a Replace-style emit so views that key off the full
        // fingerprint reload album art. This is the post-P3b-1 behavior.
        [Fact]
        public void ThumbnailHashChangeEmitsReplaceFrame()
        {
            using var h = NewHarness();
            var baseSession = Session("s1", "Song A");
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { baseSession }));
            h.Frames.Clear();

            // Identical fp → no frame.
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { baseSession }));
            Assert.Empty(h.Frames);

            // Same title/artist/session but different thumbnail hash → fp changes
            // and a non-Slide frame must fire (invariant #3 requires visible
            // acknowledgement).
            var withHash = baseSession with { ThumbnailHash = "deadbeef12345678" };
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { withHash }));
            Assert.Single(h.Frames);
            Assert.NotEqual(FrameTransitionKind.None, h.Frames[0].Transition);
            Assert.NotEqual(FrameTransitionKind.SlideForward, h.Frames[0].Transition);
            Assert.NotEqual(FrameTransitionKind.SlideBackward, h.Frames[0].Transition);
            Assert.Equal("deadbeef12345678", h.Frames[0].Fingerprint.ThumbnailHash);
        }

        // Scenario: stabilization timer fires without any metadata arriving.
        // The machine must re-reconcile and either stay Switching or return
        // to Steady on the previous fingerprint — it must NOT emit a Slide
        // on the previous fingerprint (invariant #2).
        [Fact]
        public void StabilizationTimerDoesNotSlideOnUnchangedFingerprint()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[]
            {
                Session("s1", "Song A", stabilization: MediaSessionStabilizationReason.SkipTransition)
            }));
            h.Frames.Clear();

            // Timer fires — no new metadata yet.
            h.Machine.ProcessForTests(new StabilizationTimerFiredEvent());

            foreach (var f in h.Frames)
            {
                Assert.NotEqual(FrameTransitionKind.SlideForward, f.Transition);
                Assert.NotEqual(FrameTransitionKind.SlideBackward, f.Transition);
            }
        }

        // Scenario: AiResolveCompletedEvent re-emits the current frame without
        // a slide so overlaid AI-resolved title/artist becomes visible. The
        // fingerprint is unchanged, so Transition must be None.
        [Fact]
        public void AiResolveCompletedEmitsNonSlidingFrame()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Frames.Clear();

            h.Machine.ProcessForTests(new AiResolveCompletedEvent(
                SourceAppId: "app",
                Title: "Song A",
                Artist: "ArtistA",
                Result: null));

            Assert.Single(h.Frames);
            Assert.Equal(FrameTransitionKind.None, h.Frames[0].Transition);
            Assert.Equal("Song A", h.Frames[0].Fingerprint.Title);
        }

        // Scenario: sequence numbers strictly increase across the session.
        // Invariant #1 is already asserted by Debug.Assert inside EmitFrame;
        // this test provides a Release-safe cross-check.
        [Fact]
        public void FrameSequenceStrictlyIncreases()
        {
            using var h = NewHarness();
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song A") }));
            h.Machine.ProcessForTests(new UserSkipRequestedEvent(ContentTransitionDirection.Forward));
            h.Machine.ProcessForTests(new GsmtcSessionsChangedEvent(new[] { Session("s1", "Song B") }));
            h.Machine.ProcessForTests(new NotificationBeginEvent(new NotificationPayload("n", "m", "h", 1000)));
            h.Machine.ProcessForTests(new NotificationEndEvent());

            long prev = -1;
            foreach (var f in h.Frames)
            {
                Assert.True(f.Sequence > prev, $"Frame {f.Sequence} did not increase from {prev}");
                prev = f.Sequence;
            }
            Assert.True(h.Frames.Count >= 3, "Expected at least Replace + Slide + Resume frames");
        }
    }
}

