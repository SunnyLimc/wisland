using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using wisland.Helpers;
using wisland.Models;
using Windows.Media.Control;

namespace wisland.Services
{
    /// <summary>
    /// Encapsulates Windows GSMTC (Global System Media Transport Controls) API.
    /// Tracks stable logical media sources even when raw GSMTC sessions temporarily disappear.
    /// </summary>
    public sealed partial class MediaService : IDisposable
    {
        private const string UnknownTrackTitle = "Unknown Track";
        private const string UnknownArtistName = "Unknown Artist";

        private readonly object _gate = new();
        private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
        private readonly Dictionary<GlobalSystemMediaTransportControlsSession, TrackedSource> _trackedSourcesBySession =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, TrackedSource> _trackedSourcesByKey =
            new(StringComparer.Ordinal);
        private readonly Timer _waitingExpiryTimer;
        private readonly TimeSpan _missingSourceGrace = TimeSpan.FromMilliseconds(IslandConfig.MediaMissingGraceMs);
        private readonly TimeSpan _refreshBurstDuration = TimeSpan.FromMilliseconds(IslandConfig.MediaRefreshBurstDurationMs);
        private readonly TimeSpan _refreshBurstInterval = TimeSpan.FromMilliseconds(IslandConfig.MediaRefreshBurstIntervalMs);

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private IReadOnlyList<MediaSessionSnapshot> _sessions = Array.Empty<MediaSessionSnapshot>();
        private string? _systemCurrentSessionKey;
        private string? _displayedSessionKey;
        private string? _lastSystemCurrentTrackSignature;
        private PendingTransportContinuation? _pendingTransportContinuation;
        private int _nextSessionOrdinal = 1;
        private CancellationTokenSource? _refreshBurstCts;
        private bool _isDisposed;

        public MediaService()
            => _waitingExpiryTimer = new Timer(OnWaitingExpiryTimer);

        public IReadOnlyList<MediaSessionSnapshot> Sessions
        {
            get
            {
                lock (_gate)
                {
                    return _sessions;
                }
            }
        }

        public string? SystemCurrentSessionKey
        {
            get
            {
                lock (_gate)
                {
                    return _systemCurrentSessionKey;
                }
            }
        }

        public bool HasAnySession
        {
            get
            {
                lock (_gate)
                {
                    return _trackedSourcesByKey.Count > 0;
                }
            }
        }

        public event Action? MediaChanged;
        public event Action? SessionsChanged;
        public event Action<string, string>? TrackChanged;

        public async Task InitializeAsync()
        {
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                GlobalSystemMediaTransportControlsSessionManager? manager =
                    await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (manager == null)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_isDisposed || _manager != null)
                    {
                        return;
                    }

                    manager.CurrentSessionChanged += OnCurrentSessionChanged;
                    manager.SessionsChanged += OnSessionsChanged;
                    _manager = manager;
                }

                await RefreshSessionsAndStatesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize media manager");
            }
        }

        /// <summary>
        /// Smoothly advances tracked progress locally for active playing sessions.
        /// </summary>
        public void Tick(double dt)
        {
            if (dt <= 0)
            {
                return;
            }

            lock (_gate)
            {
                foreach (TrackedSource tracked in _trackedSourcesByKey.Values)
                {
                    if (tracked.Presence != MediaSessionPresence.Active
                        || tracked.HasPendingReconnect
                        || tracked.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        || tracked.DurationSeconds <= 0)
                    {
                        continue;
                    }

                    double nextPosition = Math.Min(tracked.DurationSeconds, tracked.CurrentPositionSeconds + dt);
                    if (Math.Abs(nextPosition - tracked.CurrentPositionSeconds) <= 0.0001)
                    {
                        continue;
                    }

                    tracked.CurrentPositionSeconds = nextPosition;
                    tracked.Progress = nextPosition / tracked.DurationSeconds;
                }
            }
        }

        public MediaSessionSnapshot? GetSessionSnapshot(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return null;
            }

            lock (_gate)
            {
                return _trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked)
                    ? CreateSnapshot(tracked)
                    : null;
            }
        }

        public bool HasSession(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return false;
            }

            lock (_gate)
            {
                return _trackedSourcesByKey.ContainsKey(sessionKey);
            }
        }

        public bool ShouldAnimateProgress(string? sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return false;
            }

            lock (_gate)
            {
                return _trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked)
                    && tracked.Presence == MediaSessionPresence.Active
                    && !tracked.HasPendingReconnect
                    && tracked.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && tracked.DurationSeconds > 0;
            }
        }

        public void SetDisplayedSessionKey(string? sessionKey)
        {
            lock (_gate)
            {
                _displayedSessionKey = sessionKey;
                if (!string.IsNullOrWhiteSpace(sessionKey)
                    && _trackedSourcesByKey.TryGetValue(sessionKey, out TrackedSource? tracked))
                {
                    tracked.LastDisplayedUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        public async Task PlayPauseAsync(string? sessionKey)
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session = GetSession(sessionKey);
                if (session != null)
                {
                    await session.TryTogglePlayPauseAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayPause failed");
            }
        }

        public async Task SkipNextAsync(string? sessionKey)
        {
            try
            {
                ArmTransportContinuation(sessionKey);
                GlobalSystemMediaTransportControlsSession? session = GetSession(sessionKey);
                if (session != null)
                {
                    await session.TrySkipNextAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SkipNext failed");
            }
            finally
            {
                StartRefreshBurst();
            }
        }

        public async Task SkipPreviousAsync(string? sessionKey)
        {
            try
            {
                ArmTransportContinuation(sessionKey);
                GlobalSystemMediaTransportControlsSession? session = GetSession(sessionKey);
                if (session != null)
                {
                    await session.TrySkipPreviousAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SkipPrevious failed");
            }
            finally
            {
                StartRefreshBurst();
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            _waitingExpiryTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _waitingExpiryTimer.Dispose();

            CancellationTokenSource? refreshBurstCts;
            lock (_gate)
            {
                refreshBurstCts = _refreshBurstCts;
                _refreshBurstCts = null;
            }

            refreshBurstCts?.Cancel();
            refreshBurstCts?.Dispose();

            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
            }

            GlobalSystemMediaTransportControlsSession[] sessionsToDetach;
            lock (_gate)
            {
                sessionsToDetach = _trackedSourcesBySession.Keys.ToArray();
                _trackedSourcesBySession.Clear();
                _trackedSourcesByKey.Clear();
                _sessions = Array.Empty<MediaSessionSnapshot>();
                _systemCurrentSessionKey = null;
                _displayedSessionKey = null;
            }

            foreach (GlobalSystemMediaTransportControlsSession session in sessionsToDetach)
            {
                DetachSession(session);
            }

            _manager = null;
            _refreshSemaphore.Dispose();
        }


    }
}
