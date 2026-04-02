namespace wisland.Models
{
    public enum SessionPickerOverlayDismissKind
    {
        Passive,
        Selection,
        Toggle
    }

    public readonly record struct SessionPickerOverlayDismissMotion(
        int DurationMs,
        float TargetOpacity,
        float OffsetY)
    {
        public static SessionPickerOverlayDismissMotion FromKind(SessionPickerOverlayDismissKind kind)
            => kind switch
            {
                SessionPickerOverlayDismissKind.Selection => new(
                    IslandConfig.SessionPickerOverlaySelectionDismissDurationMs,
                    (float)IslandConfig.SessionPickerOverlaySelectionDismissTargetOpacity,
                    (float)IslandConfig.SessionPickerOverlaySelectionDismissOffsetY),
                SessionPickerOverlayDismissKind.Toggle => new(
                    IslandConfig.SessionPickerOverlayToggleDismissDurationMs,
                    (float)IslandConfig.SessionPickerOverlayToggleDismissTargetOpacity,
                    (float)IslandConfig.SessionPickerOverlayToggleDismissOffsetY),
                _ => new(
                    IslandConfig.SessionPickerOverlayPassiveDismissDurationMs,
                    (float)IslandConfig.SessionPickerOverlayPassiveDismissTargetOpacity,
                    (float)IslandConfig.SessionPickerOverlayPassiveDismissOffsetY)
            };
    }
}
