using System;
using System.Collections.Generic;

namespace wisland.Models
{
    /// <summary>
    /// Represents a language option available for AI song override prompt preference.
    /// </summary>
    public sealed class AiPromptLanguage
    {
        public string Code { get; }
        public string EnglishName { get; }
        public string NativeName { get; }
        public bool HasNativePrompt { get; }

        private AiPromptLanguage(string code, string englishName, string nativeName, bool hasNativePrompt)
        {
            Code = code;
            EnglishName = englishName;
            NativeName = nativeName;
            HasNativePrompt = hasNativePrompt;
        }

        public string DisplayLabel =>
            string.Equals(EnglishName, NativeName, StringComparison.Ordinal)
                ? EnglishName
                : $"{NativeName} ({EnglishName})";

        /// <summary>
        /// Default target market name used when the user has not configured one.
        /// </summary>
        public string DefaultMarket { get; init; } = string.Empty;

        /// <summary>
        /// All supported languages for the AI preferred-language selector.
        /// Languages with native prompts are indicated with <see cref="HasNativePrompt"/>.
        /// </summary>
        public static IReadOnlyList<AiPromptLanguage> All { get; } = BuildAll();

        private static List<AiPromptLanguage> BuildAll()
        {
            return new List<AiPromptLanguage>
            {
                // Languages with native prompt templates
                new("zh-Hans", "Chinese (Simplified)", "简体中文", hasNativePrompt: true) { DefaultMarket = "Mainland China" },
                new("zh-Hant", "Chinese (Traditional)", "繁體中文", hasNativePrompt: true) { DefaultMarket = "Taiwan / Hong Kong" },
                new("ja", "Japanese", "日本語", hasNativePrompt: true) { DefaultMarket = "Japan" },

                // Major music market languages — generic English prompt with language/market substitution
                new("en", "English", "English", hasNativePrompt: false) { DefaultMarket = "United States" },
                new("ko", "Korean", "한국어", hasNativePrompt: false) { DefaultMarket = "South Korea" },
                new("es", "Spanish", "Español", hasNativePrompt: false) { DefaultMarket = "Spain" },
                new("pt", "Portuguese", "Português", hasNativePrompt: false) { DefaultMarket = "Brazil" },
                new("fr", "French", "Français", hasNativePrompt: false) { DefaultMarket = "France" },
                new("de", "German", "Deutsch", hasNativePrompt: false) { DefaultMarket = "Germany" },
                new("it", "Italian", "Italiano", hasNativePrompt: false) { DefaultMarket = "Italy" },
                new("ru", "Russian", "Русский", hasNativePrompt: false) { DefaultMarket = "Russia" },
                new("ar", "Arabic", "العربية", hasNativePrompt: false) { DefaultMarket = "Saudi Arabia" },
                new("hi", "Hindi", "हिन्दी", hasNativePrompt: false) { DefaultMarket = "India" },
                new("th", "Thai", "ไทย", hasNativePrompt: false) { DefaultMarket = "Thailand" },
                new("vi", "Vietnamese", "Tiếng Việt", hasNativePrompt: false) { DefaultMarket = "Vietnam" },
                new("id", "Indonesian", "Bahasa Indonesia", hasNativePrompt: false) { DefaultMarket = "Indonesia" },
                new("tr", "Turkish", "Türkçe", hasNativePrompt: false) { DefaultMarket = "Turkey" },
                new("pl", "Polish", "Polski", hasNativePrompt: false) { DefaultMarket = "Poland" },
                new("nl", "Dutch", "Nederlands", hasNativePrompt: false) { DefaultMarket = "Netherlands" },
                new("sv", "Swedish", "Svenska", hasNativePrompt: false) { DefaultMarket = "Sweden" },
            };
        }
    }
}
