using System;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services.Media.Presentation.Policies
{
    /// <summary>
    /// Owns the AI song override state: looks up cached overrides for the
    /// currently arbitrated winner, writes the result into
    /// <see cref="MediaPresentationMachineContext.ActiveAiOverride"/> so the
    /// machine can embed it in the next frame, and kicks off asynchronous
    /// resolves via the injected <see cref="IAiOverrideResolver"/> when no
    /// cache entry exists for a new identity.
    ///
    /// Replaces the pre-P4d MainWindow.Media helpers
    /// <c>ApplyAiOverrideToContext</c> / <c>TryRequestAiResolveForFrame</c> /
    /// <c>GetCachedAiOverride</c>.
    /// </summary>
    public sealed class AiOverridePolicy : IPresentationPolicy
    {
        private readonly IAiOverrideResolver? _resolver;
        private string? _lastResolveIdentity;

        public AiOverridePolicy(IAiOverrideResolver? resolver = null)
        {
            _resolver = resolver;
        }

        public void OnAttach(MediaPresentationMachine machine) { }

        public void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context)
        {
            if (evt is SettingsChangedEvent s && s.Scope == SettingsChangeScope.AiOverride)
            {
                context.ActiveAiOverride = null;
                _lastResolveIdentity = null;
            }

            if (_resolver == null || !_resolver.IsEnabled)
            {
                context.ActiveAiOverride = null;
                _lastResolveIdentity = null;
                return;
            }

            // Use the arbiter's winner as the source of truth for "which track
            // will be displayed". FocusArbitrationPolicy runs before this one
            // in the policy chain so ArbitratedWinnerKey is current.
            var winner = FindSession(context, context.ArbitratedWinnerKey);
            if (!winner.HasValue)
            {
                context.ActiveAiOverride = null;
                _lastResolveIdentity = null;
                return;
            }

            var session = winner.Value;
            string identity = BuildIdentity(session);

            var cached = _resolver.TryGetCached(session.SourceAppId, session.Title, session.Artist);
            if (cached != null)
            {
                context.ActiveAiOverride = new AiOverrideSnapshot(cached.Title, cached.Artist);
                _lastResolveIdentity = identity;
                return;
            }

            // No cache entry. Clear any stale override from a different track,
            // then trigger resolve once per identity.
            context.ActiveAiOverride = null;
            if (!string.Equals(_lastResolveIdentity, identity, StringComparison.Ordinal))
            {
                _lastResolveIdentity = identity;
                try
                {
                    _resolver.BeginResolve(session);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AiOverridePolicy] BeginResolve threw: {ex.Message}");
                }
            }
        }

        public void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context) { }

        private static MediaSessionSnapshot? FindSession(
            MediaPresentationMachineContext context, string? sessionKey)
        {
            if (string.IsNullOrEmpty(sessionKey)) return null;
            foreach (var s in context.Sessions)
            {
                if (string.Equals(s.SessionKey, sessionKey, StringComparison.Ordinal))
                    return s;
            }
            return null;
        }

        private static string BuildIdentity(MediaSessionSnapshot session)
            => $"{session.SourceAppId}|{session.Title}|{session.Artist}";
    }
}

