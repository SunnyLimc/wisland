using System;

namespace wisland.Models
{
    public sealed class AiSongResult
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public DateTimeOffset ResolvedAtUtc { get; set; }
    }
}
