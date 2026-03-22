using Windows.UI;

namespace island.Models
{
    public readonly record struct LinePalette(
        Color TopHighlightColor,
        Color TrackBackgroundColor,
        Color ProgressFillColor,
        Color ProgressHeadColor,
        Color BottomShadowColor,
        Color EdgeOutlineColor);
}
