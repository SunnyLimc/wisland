using System;

namespace wisland.Services.Media.Presentation.Policies
{
    /// <summary>
    /// P1 skeleton. Will wrap AiSongResolverService + ApplyAiOverride cache in P2.
    /// Dispatches AiResolveCompletedEvent back to the machine when async resolves
    /// complete.
    /// </summary>
    public sealed class AiOverridePolicy : IPresentationPolicy
    {
        public void OnAttach(MediaPresentationMachine machine) { }
        public void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context) { }
        public void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context) { }
    }
}
