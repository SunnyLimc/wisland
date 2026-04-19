using System;
using System.Threading;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;
using wisland.Services.Media.Presentation;

namespace wisland.Services.Media
{
    /// <summary>
    /// Production implementation of <see cref="IAiOverrideResolver"/> that
    /// wraps <see cref="AiSongResolverService"/> and dispatches
    /// <see cref="AiResolveCompletedEvent"/> back to the machine on completion.
    /// Owns the single in-flight CancellationTokenSource so successive resolve
    /// requests for different tracks supersede each other.
    /// </summary>
    public sealed class AiOverrideResolverAdapter : IAiOverrideResolver, IDisposable
    {
        private readonly AiSongResolverService _resolver;
        private readonly SettingsService _settings;
        private MediaPresentationMachine? _machine;
        private CancellationTokenSource? _inflight;
        private bool _disposed;

        public AiOverrideResolverAdapter(
            AiSongResolverService resolver,
            SettingsService settings)
        {
            _resolver = resolver;
            _settings = settings;
        }

        /// <summary>Bind the machine after construction. Required before
        /// <see cref="BeginResolve"/> will dispatch completion events.</summary>
        public void AttachMachine(MediaPresentationMachine machine)
        {
            _machine = machine;
        }

        public bool IsEnabled => _settings.AiSongOverrideEnabled;

        public AiSongResult? TryGetCached(string sourceAppId, string title, string artist)
        {
            if (!IsEnabled) return null;
            return _resolver.TryGetCached(sourceAppId, title, artist);
        }

        public void BeginResolve(MediaSessionSnapshot session)
        {
            if (_disposed || !IsEnabled) return;

            // Cancel any previous resolve so the most recent identity wins.
            _inflight?.Cancel();
            _inflight?.Dispose();
            var cts = new CancellationTokenSource();
            _inflight = cts;

            string sourceAppId = session.SourceAppId;
            string title = session.Title;
            string artist = session.Artist;
            string sourceName = MediaSourceAppResolver.TryResolveDisplayName(sourceAppId) ?? session.SourceName;
            double duration = session.DurationSeconds;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _resolver.ResolveAsync(
                        sourceAppId, title, artist, sourceName, duration, cts.Token).ConfigureAwait(false);

                    if (cts.Token.IsCancellationRequested) return;

                    _machine?.Dispatch(new AiResolveCompletedEvent(sourceAppId, title, artist, result));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.Warn($"[AiOverrideResolverAdapter] Resolve failed: {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inflight?.Cancel();
            _inflight?.Dispose();
            _inflight = null;
        }
    }
}
