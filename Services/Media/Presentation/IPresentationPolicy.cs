using System;

namespace wisland.Services.Media.Presentation
{
    /// <summary>
    /// A pluggable behavior attached to the MediaPresentationMachine. Policies
    /// observe events and mutate the shared <see cref="MediaPresentationMachineContext"/>
    /// to influence the machine's decisions (focus choice, manual lock,
    /// stabilization, AI overrides, notifications).
    ///
    /// Policies must be pure-ish: no UI access, no long-running async inside
    /// OnEvent. Async work (e.g. AI resolve) is dispatched back as events.
    /// </summary>
    public interface IPresentationPolicy
    {
        /// <summary>Called once when the machine starts. Policies store any
        /// references they need here (but never the context itself — it may be
        /// mutated between calls).</summary>
        void OnAttach(MediaPresentationMachine machine);

        /// <summary>Called for every event in-order, before the machine decides
        /// state transitions. Policies update the context fields they own.</summary>
        void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context);

        /// <summary>Called on every machine tick (timers, periodic) so time-based
        /// state (selection lock expiry, auto-focus debounce) can advance without
        /// waiting for an external event.</summary>
        void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context);
    }
}
