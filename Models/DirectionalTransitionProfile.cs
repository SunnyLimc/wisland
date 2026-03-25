namespace wisland.Models
{
    public readonly record struct DirectionalTransitionProfile(
        int DurationMs,
        float OutgoingOffset,
        float IncomingOffset,
        float OutgoingScale,
        float IncomingScale,
        float IncomingDelayProgress,
        float OutgoingFadeEndProgress,
        float OutgoingTravelProgress,
        float ClipInsetRatio,
        float ClipInsetMin,
        float ClipInsetMax);
}
