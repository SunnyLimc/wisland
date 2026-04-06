using System;
using wisland.Helpers;

namespace wisland.Models
{
    public enum AiModelProvider
    {
        OpenAICompatible,
        GoogleAIStudio,
    }

    public static class AiModelProviderNames
    {
        public static string Normalize(string? provider)
        {
            if (string.Equals(provider, nameof(AiModelProvider.GoogleAIStudio), StringComparison.OrdinalIgnoreCase))
            {
                return nameof(AiModelProvider.GoogleAIStudio);
            }

            return nameof(AiModelProvider.OpenAICompatible);
        }

        public static string GetDisplayName(string? provider)
            => Normalize(provider) switch
            {
                nameof(AiModelProvider.GoogleAIStudio) => Loc.GetString("AiModels/ProviderGoogle"),
                _ => Loc.GetString("AiModels/ProviderOpenAI"),
            };
    }
}
