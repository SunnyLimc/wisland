using System;
using System.Collections.Generic;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services
{
    public static class SessionPickerRowProjector
    {
        public static IReadOnlyList<SessionPickerRowModel> Project(
            IReadOnlyList<MediaSessionSnapshot> sessions,
            string? selectedSessionKey)
        {
            if (sessions.Count == 0)
            {
                return Array.Empty<SessionPickerRowModel>();
            }

            SessionPickerRowModel[] rows = new SessionPickerRowModel[sessions.Count];
            for (int i = 0; i < sessions.Count; i++)
            {
                MediaSessionSnapshot session = sessions[i];
                rows[i] = new SessionPickerRowModel(
                    SessionKey: session.SessionKey,
                    SourceAppId: session.SourceAppId,
                    SourceName: session.SourceName,
                    Title: ResolveTitle(session),
                    Subtitle: ResolveSubtitle(session),
                    StatusText: ResolveStatusText(session),
                    IsSelected: string.Equals(session.SessionKey, selectedSessionKey, StringComparison.Ordinal));
            }

            return rows;
        }

        public static string ResolveMonogram(string sourceName)
        {
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                foreach (char c in sourceName)
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        return char.ToUpperInvariant(c).ToString();
                    }
                }
            }

            return "M";
        }

        private static string ResolveTitle(MediaSessionSnapshot session)
            => string.IsNullOrWhiteSpace(session.Title)
                ? session.SourceName
                : session.Title;

        private static string ResolveSubtitle(MediaSessionSnapshot session)
            => session.IsWaitingForReconnect
                ? Loc.GetString("Media/WaitingForReconnect")
                : string.IsNullOrWhiteSpace(session.Artist)
                    ? string.Empty
                    : session.Artist;

        private static string ResolveStatusText(MediaSessionSnapshot session)
            => session.IsWaitingForReconnect
                ? Loc.GetString("Media/Waiting")
                : session.PlaybackStatus switch
                {
                    Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => Loc.GetString("Media/Playing"),
                    Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => Loc.GetString("Media/Paused"),
                    _ => Loc.GetString("Media/Idle")
                };
    }
}
