using System;

namespace wisland.Services.Media.Presentation.Policies
{
    /// <summary>
    /// P1 skeleton. Will absorb ArmSkipStabilization / TryArmNaturalEndingStabilization /
    /// Confirming settle logic in P2 + P3. Holds pendingThumbnail/pendingThumbnailHash
    /// for C6 leak fix.
    /// </summary>
    public sealed class StabilizationPolicy : IPresentationPolicy
    {
        public void OnAttach(MediaPresentationMachine machine) { }
        public void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context) { }
        public void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context) { }
    }
}
