using Windows.UI;

namespace wisland.Models
{
    public readonly record struct ProgressBarPalette(
        Color BaseColor,
        Color ShimmerStartColor,
        Color ShimmerHighlightColor,
        Color ShimmerEndColor,
        Color TailStartColor,
        Color TailMidColor,
        Color TailNearEndColor,
        Color TailEndColor,
        Color LeadingEdgeColor);
}
