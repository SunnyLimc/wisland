using System;

namespace wisland.Services.Media.Presentation.Policies
{
    /// <summary>
    /// P1 skeleton. Owns notification overlay lifecycle (replaces
    /// _controller.IsNotifying usage with a policy-driven IsForcedExpanded
    /// in P2).
    /// </summary>
    public sealed class NotificationOverlayPolicy : IPresentationPolicy
    {
        public void OnAttach(MediaPresentationMachine machine) { }

        public void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context)
        {
            switch (evt)
            {
                case NotificationBeginEvent begin:
                    context.ActiveNotification = begin.Payload;
                    break;
                case NotificationEndEvent:
                    context.ActiveNotification = null;
                    break;
            }
        }

        public void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context) { }
    }
}
