using Windows.UI;

namespace wisland.Models
{
    internal readonly record struct AlbumArtPalette(
        Color Dominant,
        Color Secondary,
        Color Average,
        Color LeftEdge,
        Color RightEdge,
        Color BottomEdge)
    {
        public static readonly AlbumArtPalette Default = new(
            Color.FromArgb(255, 40, 40, 50),
            Color.FromArgb(255, 25, 25, 35),
            Color.FromArgb(255, 32, 32, 42),
            Color.FromArgb(255, 40, 40, 50),
            Color.FromArgb(255, 32, 32, 42),
            Color.FromArgb(255, 32, 32, 42));
    }
}
