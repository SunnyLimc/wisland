using Windows.UI;

namespace wisland.Models
{
    public readonly record struct IslandVisualTokens(
        Color SurfaceColor,
        Color PrimaryTextColor,
        Color SecondaryTextColor,
        Color IconColor,
        ProgressBarPalette ProgressBarPalette,
        LinePalette LinePalette);
}
