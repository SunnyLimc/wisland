using wisland.Helpers;
using wisland.Services;
using Xunit;

namespace wisland.Tests
{
    public sealed class MediaSourceNameFormatterTests
    {
        public MediaSourceNameFormatterTests()
        {
            // Loc.Initialize is a no-op in the test host (no WinRT), so
            // GetString returns the resource key as its fallback value.
            Loc.Initialize(null);
        }

        [Theory]
        [InlineData(null, "Media/MediaFallback")]
        [InlineData("", "Media/MediaFallback")]
        [InlineData("   ", "Media/MediaFallback")]
        [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
        [InlineData(@"C:\Apps\my_media.player.exe", "My Media Player")]
        [InlineData("/usr/bin/firefox", "Firefox")]
        public void ResolveFormatsSourceLabels(string? rawSourceName, string expected)
            => Assert.Equal(expected, MediaSourceNameFormatter.Resolve(rawSourceName));
    }
}
