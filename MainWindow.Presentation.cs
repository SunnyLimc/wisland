using System;
using Microsoft.UI.Dispatching;
using wisland.Services.Media;
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
        private AiOverrideResolverAdapter? _aiOverrideResolverAdapter;

        private void InitializePresentationMachine()
        {
            // The adapter needs a reference to the machine (to dispatch
            // AiResolveCompletedEvent), and AiOverridePolicy needs the adapter.
            // Break the cycle by constructing the adapter first with a deferred
            // machine binding, then constructing the machine with the policy.
            if (_aiSongResolver != null)
            {
                _aiOverrideResolverAdapter = new AiOverrideResolverAdapter(
                    _aiSongResolver, _settings);
            }

            var policies = new IPresentationPolicy[]
            {
                new Services.Media.Presentation.Policies.ManualSelectionLockPolicy(),
                new Services.Media.Presentation.Policies.FocusArbitrationPolicy(),
                new Services.Media.Presentation.Policies.StabilizationPolicy(),
                new Services.Media.Presentation.Policies.AiOverridePolicy(_aiOverrideResolverAdapter),
                new Services.Media.Presentation.Policies.NotificationOverlayPolicy()
            };
            _presentationMachine = new MediaPresentationMachine(
                policies,
                new DispatcherQueuePoster(DispatcherQueue));
            _aiOverrideResolverAdapter?.AttachMachine(_presentationMachine);
            _presentationMachine.Start();
        }

        private void DisposePresentationMachine()
        {
            _aiOverrideResolverAdapter?.Dispose();
            _aiOverrideResolverAdapter = null;
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
