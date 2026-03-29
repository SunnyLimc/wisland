namespace wisland.Models
{
    /// <summary>
    /// Runtime-measured overlay content size. This is used to refine window sizing only.
    /// Placement continues to anchor against the header chip geometry.
    /// </summary>
    public readonly record struct SessionPickerOverlayLayoutMetrics(
        double Width,
        double Height);
}
