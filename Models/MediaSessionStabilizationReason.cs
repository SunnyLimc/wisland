namespace wisland.Models
{
    /// <summary>
    /// Explains why a media session snapshot is temporarily frozen at a pre-transition
    /// state instead of reflecting the latest raw GSMTC data.
    /// </summary>
    public enum MediaSessionStabilizationReason
    {
        None = 0,
        /// <summary>
        /// User-initiated skip next/previous; waiting for the real next track to arrive
        /// as Playing near track start.
        /// </summary>
        SkipTransition = 1,
        /// <summary>
        /// Previous track reached the end of its timeline; suppressing intermediate
        /// metadata (e.g. other browser tab) until the natural next track arrives.
        /// </summary>
        NaturalEnding = 2,
    }
}
