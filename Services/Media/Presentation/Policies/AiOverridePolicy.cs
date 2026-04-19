using System;
using System.Collections.Generic;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services.Media.Presentation.Policies
{
    /// <summary>
    /// Owns the AI song override state: populates a session-keyed override map
    /// on the <see cref="MediaPresentationMachineContext"/> (one entry per
    /// currently-tracked session whose override is cached) and triggers an
    /// asynchronous resolve for the arbitrated winner when its override is not
    /// yet cached. The per-session map lets <c>MediaPresentationMachine</c>
    /// attach the correct override to each emitted frame — critical during
    /// Confirming where the frame still carries the previous session.
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
                context.AiOverrideLookup = null;
                _lastResolveIdentity = null;
            }

            if (_resolver == null || !_resolver.IsEnabled)
            {
                context.ActiveAiOverride = null;
                context.AiOverrideLookup = null;
                _lastResolveIdentity = null;
                return;
            }

            // Expose a lookup that resolves each snapshot's override against
            // the resolver cache. Keying on the snapshot itself (source +
            // title + artist) avoids the "same session key, different track"
            // collision that can bite during Confirming, where the frame is
            // still the previous track but the arbiter winner is the next.
            var resolver = _resolver;
            context.AiOverrideLookup = snapshot =>
            {
                var cached = resolver.TryGetCached(snapshot.SourceAppId, snapshot.Title, snapshot.Artist);
                return cached == null ? null : new AiOverrideSnapshot(cached.Title, cached.Artist);
            };

            // ActiveAiOverride stays meaningful as "override for the arbiter
            // winner" for any policy that wants that view; it is NOT used by
            // EmitFrame after this change.
            var winner = FindSession(context, context.ArbitratedWinnerKey);
            if (!winner.HasValue)
            {
                context.ActiveAiOverride = null;
                _lastResolveIdentity = null;
                return;
            }

            var session = winner.Value;
            string identity = BuildIdentity(session);

            var winnerCached = _resolver.TryGetCached(session.SourceAppId, session.Title, session.Artist);
            if (winnerCached != null)
            {
                context.ActiveAiOverride = new AiOverrideSnapshot(winnerCached.Title, winnerCached.Artist);
                _lastResolveIdentity = identity;
                return;
            }

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


