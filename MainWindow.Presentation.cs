using System;
using Microsoft.UI.Dispatching;
using wisland.Services.Media.Presentation;

namespace wisland
{
    /// <summary>
    /// P1 wiring: create and own the MediaPresentationMachine alongside the
    /// legacy MediaService flow. The machine currently only logs events; the
    /// UI is still driven by MainWindow.Media's SyncMediaUI path until P2.
    /// </summary>
    public sealed partial class MainWindow
    {
        private MediaPresentationMachine? _presentationMachine;

        private void InitializePresentationMachine()
        {
            var policies = new IPresentationPolicy[]
            {
                new Services.Media.Presentation.Policies.ManualSelectionLockPolicy(),
                new Services.Media.Presentation.Policies.FocusArbitrationPolicy(),
                new Services.Media.Presentation.Policies.StabilizationPolicy(),
                new Services.Media.Presentation.Policies.AiOverridePolicy(),
                new Services.Media.Presentation.Policies.NotificationOverlayPolicy()
            };
            _presentationMachine = new MediaPresentationMachine(
                policies,
                new DispatcherQueuePoster(DispatcherQueue));
            _presentationMachine.Start();
        }

        private void DisposePresentationMachine()
        {
            _presentationMachine?.Dispose();
            _presentationMachine = null;
        }

        private sealed class DispatcherQueuePoster : IDispatcherPoster
        {
            private readonly DispatcherQueue _dispatcherQueue;
            public DispatcherQueuePoster(DispatcherQueue dispatcherQueue) => _dispatcherQueue = dispatcherQueue;
            public void Post(Action action) => _dispatcherQueue.TryEnqueue(() => action());
        }
    }
}
