using System.Globalization;

namespace wisland.Services
{
    internal static class MediaSourceNameFormatter
    {
        private const string FallbackSourceName = "Media";

        public static string Resolve(string? rawSourceName)
        {
            if (string.IsNullOrWhiteSpace(rawSourceName))
            {
                return FallbackSourceName;
            }

            string source = rawSourceName.Trim();

            int bangIndex = source.LastIndexOf('!');
            if (bangIndex >= 0 && bangIndex < source.Length - 1)
            {
                source = source[(bangIndex + 1)..];
            }
            else
            {
                int slashIndex = System.Math.Max(source.LastIndexOf('\\'), source.LastIndexOf('/'));
                if (slashIndex >= 0 && slashIndex < source.Length - 1)
                {
                    source = source[(slashIndex + 1)..];
                }
            }

            if (source.EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase))
            {
                source = source[..^4];
            }

            source = source.Replace('_', ' ').Replace('.', ' ').Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                return FallbackSourceName;
            }

            TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;
            return textInfo.ToTitleCase(source.ToLowerInvariant());
        }
    }
}
