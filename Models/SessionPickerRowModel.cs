namespace wisland.Models
{
    public readonly record struct SessionPickerRowModel(
        string SessionKey,
        string SourceAppId,
        string SourceName,
        string Title,
        string Subtitle,
        string StatusText,
        bool IsSelected);
}
