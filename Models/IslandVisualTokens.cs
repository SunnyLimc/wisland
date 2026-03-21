using Windows.UI;

namespace island.Models
{
    public readonly record struct IslandVisualTokens(
        Color SurfaceColor,
        Color PrimaryTextColor,
        Color SecondaryTextColor,
        Color IconColor,
        ProgressBarPalette ProgressBarPalette);
}
