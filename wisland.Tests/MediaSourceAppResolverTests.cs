using wisland.Helpers;
using Xunit;

namespace wisland.Tests
{
    public sealed class MediaSourceAppResolverTests
    {
        [Theory]
        [InlineData(null, null, 0)]
        [InlineData("", null, 0)]
        [InlineData(",0", null, 0)]
        [InlineData("\"C:\\Icons\\app.ico\",4", @"C:\Icons\app.ico", 4)]
        [InlineData("C:\\Apps\\App.exe,0", @"C:\Apps\App.exe", 0)]
        public void ParseIconLocationParsesPathAndIndex(
            string? iconLocation,
            string? expectedPath,
            int expectedIndex)
        {
            MediaSourceAppResolver.ParsedIconLocation parsed =
                MediaSourceAppResolver.ParseIconLocation(iconLocation);

            Assert.Equal(expectedPath, parsed.Path);
            Assert.Equal(expectedIndex, parsed.ResourceIndex);
        }

        [Theory]
        [InlineData(null, @"C:\Apps\App.exe", false)]
        [InlineData(",0", @"C:\Apps\App.exe", false)]
        [InlineData("C:\\Apps\\App.exe,0", @"C:\Apps\App.exe", false)]
        [InlineData("C:\\Apps\\App.exe,1", @"C:\Apps\App.exe", true)]
        [InlineData("C:\\Icons\\custom.ico,0", @"C:\Apps\App.exe", true)]
        [InlineData("%ProgramFiles%\\App\\App.exe,0", @"C:\Program Files\App\App.exe", false)]
        public void HasCustomShortcutIconLocationDetectsNonDefaultShortcutIcons(
            string? iconLocation,
            string? targetPath,
            bool expected)
            => Assert.Equal(
                expected,
                MediaSourceAppResolver.HasCustomShortcutIconLocation(iconLocation, targetPath));
    }
}
