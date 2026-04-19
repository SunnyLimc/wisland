using wisland.Models;

namespace wisland.Services.Media.Presentation
{
    /// <summary>
    /// Abstraction over <c>AiSongResolverService</c> used by
    /// <c>AiOverridePolicy</c>. Lets the policy participate in frame emission
    /// without taking a direct dependency on the resolver service or the host
    /// settings. Unit tests leave this null.
    /// </summary>
    public interface IAiOverrideResolver
    {
        /// <summary>Whether AI override is currently enabled in user settings.</summary>
        bool IsEnabled { get; }

        /// <summary>Synchronous cache lookup. Returns null when not cached or
        /// when AI override is disabled.</summary>
        AiSongResult? TryGetCached(string sourceAppId, string title, string artist);

        /// <summary>Kick off an asynchronous resolve. Implementations must
        /// cancel any previous in-flight request for a different identity and
        /// must <see cref="MediaPresentationMachine.Dispatch"/> an
        /// <see cref="AiResolveCompletedEvent"/> back to the machine when the
        /// resolve completes (success or null).</summary>
        void BeginResolve(MediaSessionSnapshot session);
    }
}
