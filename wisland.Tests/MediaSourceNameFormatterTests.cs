using wisland.Services;
using Xunit;

namespace wisland.Tests
{
    public sealed class MediaSourceNameFormatterTests
    {
        [Theory]
        [InlineData(null, "Media")]
        [InlineData("", "Media")]
        [InlineData("   ", "Media")]
        [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
        [InlineData(@"C:\Apps\my_media.player.exe", "My Media Player")]
        [InlineData("/usr/bin/firefox", "Firefox")]
        public void ResolveFormatsSourceLabels(string? rawSourceName, string expected)
            => Assert.Equal(expected, MediaSourceNameFormatter.Resolve(rawSourceName));
    }
}
