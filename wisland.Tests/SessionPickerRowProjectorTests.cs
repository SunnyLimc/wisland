using System;
using Windows.Media.Control;
using wisland.Models;
using wisland.Services;
using Xunit;

namespace wisland.Tests
{
    public sealed class SessionPickerRowProjectorTests
    {
        [Fact]
        public void ProjectsStatusSubtitleAndSelection()
        {
            MediaSessionSnapshot selected = CreateSession(
                "selected",
                title: "One",
                artist: "Artist One",
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            MediaSessionSnapshot waiting = CreateSession(
                "waiting",
                title: "Two",
                artist: string.Empty,
                playbackStatus: GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                presence: MediaSessionPresence.WaitingForReconnect,
                missingSinceUtc: DateTimeOffset.UtcNow);

            var rows = SessionPickerRowProjector.Project(
                new[] { selected, waiting },
                selected.SessionKey);

            Assert.Collection(
                rows,
                row =>
                {
                    Assert.Equal("selected", row.SessionKey);
                    Assert.Equal("Playing", row.StatusText);
                    Assert.Equal("Artist One", row.Subtitle);
                    Assert.True(row.IsSelected);
                },
                row =>
                {
                    Assert.Equal("waiting", row.SessionKey);
                    Assert.Equal("Waiting", row.StatusText);
                    Assert.Equal("Waiting for reconnect", row.Subtitle);
                    Assert.False(row.IsSelected);
                });
        }

        [Theory]
        [InlineData("Spotify", "S")]
        [InlineData("  chrome", "C")]
        [InlineData("!@", "M")]
        public void ResolveMonogramBuildsFallbackBadgeText(string sourceName, string expected)
            => Assert.Equal(expected, SessionPickerRowProjector.ResolveMonogram(sourceName));

        private static MediaSessionSnapshot CreateSession(
            string sessionKey,
            string title,
            string artist,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus,
            MediaSessionPresence presence = MediaSessionPresence.Active,
            DateTimeOffset? missingSinceUtc = null)
            => new(
                SessionKey: sessionKey,
                SourceAppId: "chrome.exe",
                SourceName: "Chrome",
                Title: title,
                Artist: artist,
                PlaybackStatus: playbackStatus,
                Progress: 0.5,
                HasTimeline: true,
                IsSystemCurrent: false,
                LastActivityUtc: new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero),
                Presence: presence,
                LastSeenUtc: new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero),
                MissingSinceUtc: missingSinceUtc);
    }
}
