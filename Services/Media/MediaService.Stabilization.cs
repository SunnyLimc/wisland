using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using wisland.Helpers;
using wisland.Models;
using Windows.Media.Control;

namespace wisland.Services
{
    public sealed partial class MediaService
    {
        private readonly Timer _stabilizationTimer;

        /// <summary>
        /// Arms skip stabilization for the given session. While armed, GSMTC updates
        /// continue to be applied to raw fields but snapshots emitted to subscribers
        /// remain frozen at the pre-skip state until a fresh next track arrives
        /// (Playing + position near start + metadata differs from baseline) or the
        /// timeout expires.
        /// </summary>
        public void ArmSkipStabilization(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return;
            }

            lock (_gate)
            {
                if (_isDisposed || !_trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked))
                {
                    return;
                }

                if (tracked.Presence != MediaSessionPresence.Active
                    || tracked.HasPendingReconnect
                    || !HasConcreteMetadata(tracked.Title))
                {
                    Logger.Trace($"ArmSkipStabilization skipped for '{sessionKey}': not in a stabilizable state");
                    return;
                }

                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

                if (tracked.StabilizationReason == MediaSessionStabilizationReason.SkipTransition
                    && tracked.StabilizationExpiresAtUtc > nowUtc)
                {
                    // Re-arm for consecutive skip: update baseline + frozen to the
                    // current raw state ONLY when the raw state actually looks like a
                    // freshly-resolved next track (Playing + HasTimeline + position ≤
                    // fresh-track threshold + concrete metadata). Otherwise the raw
                    // title may still be the paused B tab that Chrome briefly reports
                    // between skips, and swallowing that into the baseline would cause
                    // the next re-arm + fresh-track check to never fire (baseline ==
                    // tab title, so tab metadata "matches" baseline and releases are
                    // suppressed). In that case we keep the original baseline and just
                    // extend the expiry so the real resolved track still has time.
                    tracked.StabilizationExpiresAtUtc = nowUtc.AddMilliseconds(IslandConfig.SkipTransitionTimeoutMs);

                    bool rawLooksLikeFreshTrack =
                        tracked.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        && tracked.HasTimeline
                        && tracked.DurationSeconds > 0
                        && tracked.CurrentPositionSeconds <= IslandConfig.SkipTransitionFreshTrackPositionSeconds
                        && HasConcreteMetadata(tracked.Title);

                    if (rawLooksLikeFreshTrack)
                    {
                        tracked.StabilizationBaselineTitle = tracked.Title;
                        tracked.StabilizationBaselineArtist = tracked.Artist;
                        tracked.FrozenSnapshot = CreateRawSnapshot(tracked) with
                        {
                            StabilizationReason = tracked.StabilizationReason
                        };
                        Logger.Debug($"Skip stabilization re-armed for '{sessionKey}' (baseline updated to fresh raw='{tracked.Title}')");
                    }
                    else
                    {
                        Logger.Debug($"Skip stabilization re-armed for '{sessionKey}' (baseline preserved='{tracked.StabilizationBaselineTitle}', raw='{tracked.Title}' not fresh)");
                    }
                    RescheduleStabilizationTimer_NoLock(nowUtc);
                    return;
                }

                ArmStabilization_NoLock(
                    tracked,
                    MediaSessionStabilizationReason.SkipTransition,
                    TimeSpan.FromMilliseconds(IslandConfig.SkipTransitionTimeoutMs),
                    nowUtc);
            }
        }

        public bool IsStabilizing(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return false;
            }

            lock (_gate)
            {
                return _trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked)
                    && tracked.StabilizationReason != MediaSessionStabilizationReason.None;
            }
        }

        private void ArmStabilization_NoLock(
            TrackedSource tracked,
            MediaSessionStabilizationReason reason,
            TimeSpan timeout,
            DateTimeOffset nowUtc)
        {
            tracked.StabilizationReason = reason;
            tracked.StabilizationArmedAtUtc = nowUtc;
            tracked.StabilizationExpiresAtUtc = nowUtc + timeout;
            tracked.StabilizationBaselineTitle = tracked.Title;
            tracked.StabilizationBaselineArtist = tracked.Artist;
            tracked.FrozenSnapshot = CreateRawSnapshot(tracked) with
            {
                StabilizationReason = reason
            };

            RescheduleStabilizationTimer_NoLock(nowUtc);
            Logger.Debug($"Stabilization armed for '{tracked.SessionKey}' (reason={reason}, timeout={timeout.TotalMilliseconds}ms, baseline='{tracked.Title}')");
        }

        private void ClearStabilization_NoLock(TrackedSource tracked, string releaseReason)
        {
            if (tracked.StabilizationReason == MediaSessionStabilizationReason.None)
            {
                return;
            }

            Logger.Debug($"Stabilization released for '{tracked.SessionKey}' (was={tracked.StabilizationReason}, release={releaseReason})");
            tracked.StabilizationReason = MediaSessionStabilizationReason.None;
            tracked.StabilizationBaselineTitle = string.Empty;
            tracked.StabilizationBaselineArtist = string.Empty;
            tracked.FrozenSnapshot = default;
        }

        /// <summary>
        /// Evaluates whether a freshly-written raw state should release stabilization.
        /// Returns true if the gate should open and the current raw state become visible.
        /// </summary>
        private static bool ShouldReleaseStabilization_NoLock(TrackedSource tracked, DateTimeOffset nowUtc)
        {
            if (tracked.StabilizationReason == MediaSessionStabilizationReason.None)
            {
                return false;
            }

            if (tracked.StabilizationExpiresAtUtc <= nowUtc)
            {
                return true;
            }

            if (tracked.HasPendingReconnect
                || tracked.Presence != MediaSessionPresence.Active)
            {
                return false;
            }

            bool metadataDifferentFromBaseline =
                !string.Equals(tracked.Title, tracked.StabilizationBaselineTitle, StringComparison.Ordinal)
                || !string.Equals(tracked.Artist, tracked.StabilizationBaselineArtist, StringComparison.Ordinal);
            if (!metadataDifferentFromBaseline)
            {
                return false;
            }

            if (!HasConcreteMetadata(tracked.Title))
            {
                return false;
            }

            bool looksLikeFreshTrack =
                tracked.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && tracked.HasTimeline
                && tracked.DurationSeconds > 0
                && tracked.CurrentPositionSeconds <= IslandConfig.SkipTransitionFreshTrackPositionSeconds;

            return looksLikeFreshTrack;
        }

        /// <summary>
        /// Called AFTER a raw field write in Apply*_NoLock.
        /// Returns true if subscribers should be notified (stabilization is idle or just released).
        /// Returns false to suppress emission while the gate is closed.
        /// <paramref name="isMetadataWrite"/> indicates the write was a title/artist change.
        /// When a non-metadata write (timeline/playback) first triggers fresh-track
        /// detection, the release is deferred via a short hold so the correct metadata
        /// has time to arrive (Chrome may report another tab's paused metadata before
        /// the real track's metadata). A subsequent metadata write that still satisfies
        /// fresh-track conditions releases immediately.
        /// </summary>
        private bool EvaluateStabilizationAfterWrite_NoLock(TrackedSource tracked, DateTimeOffset nowUtc, bool isMetadataWrite = false)
        {
            if (tracked.StabilizationReason == MediaSessionStabilizationReason.None)
            {
                return true;
            }

            if (ShouldReleaseStabilization_NoLock(tracked, nowUtc))
            {
                bool expired = tracked.StabilizationExpiresAtUtc <= nowUtc;
                if (expired)
                {
                    ClearStabilization_NoLock(tracked, releaseReason: "expired");
                    return true;
                }

                // For metadata writes the title/artist we see IS the latest from
                // GSMTC, so it is safe to release immediately. For non-metadata
                // writes (timeline position reset, playback status change) the
                // current title may still be stale intermediate data (e.g. Chrome
                // briefly reporting another tab). Do NOT release on non-metadata
                // writes; simply wait for the real metadata write or the full
                // stabilization timeout. The 80ms early-hold path was removed in
                // P3 because the Machine's Confirming settle (§4.6, 250ms) handles
                // the "wait for metadata" window at a higher layer.
                if (isMetadataWrite)
                {
                    ClearStabilization_NoLock(tracked, releaseReason: "fresh-track");
                    return true;
                }

                Logger.Trace($"Stabilization suppressed non-metadata release for '{tracked.SessionKey}': waiting for metadata write (title='{tracked.Title}')");
                return false;
            }

            // Gate closed: raw fields changed but UI must keep seeing the frozen snapshot.
            Logger.Trace($"Stabilization gate suppressed emit for '{tracked.SessionKey}' (title='{tracked.Title}', status={tracked.PlaybackStatus}, pos={tracked.CurrentPositionSeconds:F1}s)");
            return false;
        }

        /// <summary>
        /// Primes natural-ending stabilization when a metadata change is about to be
        /// written while the existing state is near the end of its timeline. Called
        /// from ApplyMediaProperties_NoLock BEFORE the raw write.
        /// </summary>
        private void TryArmNaturalEndingStabilization_NoLock(
            TrackedSource tracked,
            string incomingTitle,
            string incomingArtist,
            DateTimeOffset nowUtc)
        {
            if (tracked.StabilizationReason != MediaSessionStabilizationReason.None)
            {
                return;
            }

            if (tracked.Presence != MediaSessionPresence.Active
                || tracked.HasPendingReconnect
                || tracked.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                || !tracked.HasTimeline
                || tracked.DurationSeconds <= 0)
            {
                return;
            }

            if (!HasConcreteMetadata(tracked.Title))
            {
                return;
            }

            bool metadataWillChange =
                !string.Equals(tracked.Title, incomingTitle, StringComparison.Ordinal)
                || !string.Equals(tracked.Artist, incomingArtist, StringComparison.Ordinal);
            if (!metadataWillChange)
            {
                return;
            }

            double remainingSeconds = tracked.DurationSeconds - tracked.CurrentPositionSeconds;
            if (remainingSeconds > IslandConfig.NaturalEndingDetectionThresholdSeconds)
            {
                return;
            }

            ArmStabilization_NoLock(
                tracked,
                MediaSessionStabilizationReason.NaturalEnding,
                TimeSpan.FromMilliseconds(IslandConfig.NaturalEndingTransitionTimeoutMs),
                nowUtc);
        }

        private void RescheduleStabilizationTimer_NoLock(DateTimeOffset nowUtc)
        {
            DateTimeOffset? nextExpiryUtc = _trackedSourcesByKey.Values
                .Where(source => source.StabilizationReason != MediaSessionStabilizationReason.None)
                .Select(source => source.StabilizationExpiresAtUtc)
                .OrderBy(expiry => expiry)
                .Cast<DateTimeOffset?>()
                .FirstOrDefault();

            if (!nextExpiryUtc.HasValue)
            {
                _stabilizationTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            TimeSpan dueTime = nextExpiryUtc.Value - nowUtc;
            if (dueTime < TimeSpan.Zero)
            {
                dueTime = TimeSpan.Zero;
            }

            _stabilizationTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }

        private void OnStabilizationTimer(object? state)
        {
            ServiceChangeResult changeResult = default;
            bool anyReleased = false;

            lock (_gate)
            {
                if (_isDisposed)
                {
                    return;
                }

                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                foreach (TrackedSource tracked in _trackedSourcesByKey.Values.ToArray())
                {
                    if (tracked.StabilizationReason == MediaSessionStabilizationReason.None)
                    {
                        continue;
                    }

                    if (tracked.StabilizationExpiresAtUtc > nowUtc)
                    {
                        continue;
                    }

                    ClearStabilization_NoLock(tracked, releaseReason: "timer-expired");
                    anyReleased = true;
                }

                if (anyReleased)
                {
                    changeResult = PrepareStateChange_NoLock();
                }

                RescheduleStabilizationTimer_NoLock(nowUtc);
            }

            if (anyReleased)
            {
                DispatchChange(changeResult);
            }
        }

        /// <summary>
        /// Builds a snapshot from the raw tracked fields without any stabilization
        /// overlay. Used when arming stabilization (to capture the baseline) and
        /// internally by CreateSnapshot.
        /// </summary>
        private static MediaSessionSnapshot CreateRawSnapshot(TrackedSource tracked)
        {
            // Wall-clock catch-up: the UI render loop (which drives Tick) is suspended
            // when the island is idle or another session is displayed, so a backgrounded
            // session's CurrentPositionSeconds can become stale. Extrapolate from the
            // last-known anchor using wall-clock elapsed so switchback shows true
            // current progress instead of snapping back to the anchor value.
            double effectivePosition = tracked.CurrentPositionSeconds;
            double effectiveProgress = tracked.Progress;
            if (tracked.HasTimeline
                && tracked.DurationSeconds > 0
                && tracked.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && tracked.Presence == MediaSessionPresence.Active
                && !tracked.HasPendingReconnect
                && tracked.PositionUpdatedUtc != default)
            {
                double elapsed = (DateTimeOffset.UtcNow - tracked.PositionUpdatedUtc).TotalSeconds;
                if (elapsed > 0 && elapsed < 12 * 3600)
                {
                    effectivePosition = Math.Min(tracked.DurationSeconds, tracked.CurrentPositionSeconds + elapsed);
                    effectiveProgress = effectivePosition / tracked.DurationSeconds;
                }
            }

            return new MediaSessionSnapshot(
                tracked.SessionKey,
                tracked.SourceAppId,
                tracked.SourceName,
                tracked.Title,
                tracked.Artist,
                tracked.PlaybackStatus,
                effectiveProgress,
                tracked.HasTimeline,
                tracked.DurationSeconds,
                tracked.IsSystemCurrent,
                tracked.LastActivityUtc,
                tracked.Presence,
                tracked.LastSeenUtc,
                tracked.MissingSinceUtc,
                MediaSessionStabilizationReason.None,
                tracked.Thumbnail,
                tracked.ThumbnailHash);
        }
    }
}
