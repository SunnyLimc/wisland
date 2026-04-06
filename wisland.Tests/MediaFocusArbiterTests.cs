using System;
using System.Collections.Generic;
using wisland.Models;
using wisland.Services;
using Windows.Media.Control;
using Xunit;

namespace wisland.Tests
{
    public sealed class MediaFocusArbiterTests
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

        [Fact]
        public void KeepsDisplayedWaitingSourceDuringGrace()
        {
            MediaFocusArbiter arbiter = new(Debounce, Grace);
            DateTimeOffset now = new(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);
            MediaSessionSnapshot displayedWaiting = CreateSession(
                "a",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                presence: MediaSessionPresence.WaitingForReconnect,
                missingSinceUtc: now - TimeSpan.FromSeconds(2));
            MediaSessionSnapshot otherPlaying = CreateSession(
                "b",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                isSystemCurrent: true);

            MediaFocusDecision decision = arbiter.Resolve(
                new[] { displayedWaiting, otherPlaying },
                currentDisplayedKey: displayedWaiting.SessionKey,
                manualLockedKey: null,
                hasManualLock: false,
                nowUtc: now);

            Assert.Equal(displayedWaiting.SessionKey, decision.DisplayedSession?.SessionKey);
        }

        [Fact]
        public void WinnerChangeRequiresFreshDebounceBeforeSwitchingBack()
        {
            MediaFocusArbiter arbiter = new(Debounce, Grace);
            DateTimeOffset now = new(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);

            MediaSessionSnapshot sourceAPlaying = CreateSession(
                "a",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            MediaSessionSnapshot sourceBPlaying = CreateSession(
                "b",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                isSystemCurrent: true);

            MediaFocusDecision firstDecision = arbiter.Resolve(
                new[] { sourceAPlaying, sourceBPlaying },
                currentDisplayedKey: sourceAPlaying.SessionKey,
                manualLockedKey: null,
                hasManualLock: false,
                nowUtc: now);
            Assert.Equal(sourceAPlaying.SessionKey, firstDecision.DisplayedSession?.SessionKey);

            MediaFocusDecision secondDecision = arbiter.Resolve(
                new[] { sourceAPlaying, sourceBPlaying },
                currentDisplayedKey: sourceAPlaying.SessionKey,
                manualLockedKey: null,
                hasManualLock: false,
                nowUtc: now + Debounce + TimeSpan.FromMilliseconds(1));
            Assert.Equal(sourceBPlaying.SessionKey, secondDecision.DisplayedSession?.SessionKey);

            MediaSessionSnapshot sourceAPaused = CreateSession(
                "a",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                isSystemCurrent: true);
            MediaSessionSnapshot sourceBPaused = CreateSession(
                "b",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);

            MediaFocusDecision thirdDecision = arbiter.Resolve(
                new[] { sourceAPaused, sourceBPaused },
                currentDisplayedKey: sourceBPlaying.SessionKey,
                manualLockedKey: null,
                hasManualLock: false,
                nowUtc: now + Debounce + TimeSpan.FromMilliseconds(2));
            Assert.Equal(sourceBPlaying.SessionKey, thirdDecision.DisplayedSession?.SessionKey);

            MediaFocusDecision fourthDecision = arbiter.Resolve(
                new[] { sourceAPaused, sourceBPaused },
                currentDisplayedKey: sourceBPlaying.SessionKey,
                manualLockedKey: null,
                hasManualLock: false,
                nowUtc: now + (Debounce * 2) + TimeSpan.FromMilliseconds(5));
            Assert.Equal(sourceAPaused.SessionKey, fourthDecision.DisplayedSession?.SessionKey);
        }

        [Fact]
        public void ManualLockOverridesAutoWinner()
        {
            MediaFocusArbiter arbiter = new(Debounce, Grace);
            DateTimeOffset now = new(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);
            MediaSessionSnapshot manual = CreateSession(
                "manual",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
            MediaSessionSnapshot autoWinner = CreateSession(
                "winner",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                isSystemCurrent: true);

            MediaFocusDecision decision = arbiter.Resolve(
                new[] { manual, autoWinner },
                currentDisplayedKey: autoWinner.SessionKey,
                manualLockedKey: manual.SessionKey,
                hasManualLock: true,
                nowUtc: now);

            Assert.Equal(manual.SessionKey, decision.DisplayedSession?.SessionKey);
            Assert.Null(decision.PendingAutoSwitchDueUtc);
        }

        [Fact]
        public void ActivePlaceholderWithinGraceStillPinsDisplayedSource()
        {
            MediaFocusArbiter arbiter = new(Debounce, Grace);
            DateTimeOffset now = new(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);
            MediaSessionSnapshot provisionalDisplayed = CreateSession(
                "displayed",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                presence: MediaSessionPresence.Active,
                missingSinceUtc: now - TimeSpan.FromSeconds(1));
            MediaSessionSnapshot otherPlaying = CreateSession(
                "other",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                isSystemCurrent: true);

            MediaFocusDecision decision = arbiter.Resolve(
                new[] { provisionalDisplayed, otherPlaying },
                currentDisplayedKey: provisionalDisplayed.SessionKey,
                manualLockedKey: null,
                hasManualLock: false,
                nowUtc: now);

            Assert.Equal(provisionalDisplayed.SessionKey, decision.DisplayedSession?.SessionKey);
        }

        private static MediaSessionSnapshot CreateSession(
            string sessionKey,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus,
            bool isSystemCurrent = false,
            MediaSessionPresence presence = MediaSessionPresence.Active,
            DateTimeOffset? missingSinceUtc = null)
            => new(
                SessionKey: sessionKey,
                SourceAppId: "chrome.exe",
                SourceName: "Chrome",
                Title: $"Title-{sessionKey}",
                Artist: $"Artist-{sessionKey}",
                PlaybackStatus: playbackStatus,
                Progress: 0.5,
                HasTimeline: true,
                DurationSeconds: 180,
                IsSystemCurrent: isSystemCurrent,
                LastActivityUtc: new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero),
                Presence: presence,
                LastSeenUtc: new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero),
                MissingSinceUtc: missingSinceUtc);
    }
}
