using System;
using System.Collections.Generic;
using wisland.Models;

namespace wisland.Services.Media.Presentation
{
    /// <summary>
    /// Internal state carried by the machine between events. P2 uses a flat
    /// "Steady + isStabilizing hint" model: the machine leans on MediaService
    /// to decide when a snapshot is frozen (via <see cref="MediaSessionSnapshot.IsStabilizing"/>)
    /// and only emits frames when the fingerprint actually changes out of a
    /// stabilizing window. Explicit Pending/Confirming states are introduced
    /// in P3 once StabilizationPolicy owns that responsibility.
    /// </summary>
    internal sealed class MediaPresentationState
    {
        public bool Initialized;
        public MediaSessionSnapshot? DisplayedSnapshot;
        public MediaTrackFingerprint DisplayedFingerprint = MediaTrackFingerprint.Empty;
        public PresentationKind DisplayedKind = PresentationKind.Empty;

        // Overlay state (orthogonal to displayed state)
        public bool IsNotifying;
        public NotificationPayload? NotificationPayload;
        /// <summary>Fingerprint/snapshot tracked while a notification is on screen.
        /// On NotificationEnd we diff this against DisplayedFingerprint to pick
        /// the resume transition.</summary>
        public MediaSessionSnapshot? InnerSnapshot;
        public MediaTrackFingerprint InnerFingerprint = MediaTrackFingerprint.Empty;
        public PresentationKind InnerKind = PresentationKind.Empty;
        public bool InnerChangedDuringOverlay;

        // Switch intent (replaces _pendingMediaTransitionDirection, longer deadline)
        public SwitchIntent? Intent;
    }
}
