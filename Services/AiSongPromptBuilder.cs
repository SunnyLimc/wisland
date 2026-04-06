using System;
using System.Collections.Generic;
using System.Linq;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services
{
    /// <summary>
    /// Builds the system prompt and user message for AI song resolution
    /// based on preferred language, target market, and native prompt settings.
    /// </summary>
    internal static class AiSongPromptBuilder
    {
        private const string GenericSystemTemplate =
            """
            You are a music metadata resolver. Given raw media session information from a media player, determine the correct, clean song title and artist name.

            Rules:
            - Remove quality/platform tags like "(Official Video)", "(Official Audio)", "(Lyrics)", "(HD)", "(MV)", "[Official MV]", "(Audio)", "(Visualizer)", "(Live)", "【MV】", "| Official Music Video", etc.
            - Remove platform-specific prefixes or suffixes appended by the media source application.
            - For songs with featured artists, keep them in the artist field using standard notation, e.g. "Artist feat. Guest".
            - If the raw title contains both the song name and artist (common in browser tab titles like "Artist - Song"), separate them correctly into the respective fields.
            - Return the most commonly recognized official release name for the song and artist.
            - If you cannot confidently determine the correct metadata, return the raw input unchanged.
            - Do not invent or guess information that is not present in the input.
            """;

        private const string GenericUserTemplate =
            """
            Identify the song's official release title and official artist name(s).
            If there is an official name in {0} used in {1}, prefer that; otherwise, use the original official name.
            Use only names found in official releases, official platforms, rights-holder metadata, or label/distributor information. Do not use machine translations, fan translations, unofficial aliases, or self-normalized variants.
            If multiple regional official names exist for the same work, prefer the official name used in {1}; if none exists, use the original official release name that most directly corresponds to the work.
            If the provided song is a cover version, use the official credited cover artist name(s) for that release, and do not replace them with the original performer's name.
            For multiple artists, preserve the official joint formatting, character names, CV credits, separators, and ordering.

            Song: `{2}`
            Artist: `{3}`
            Duration: `{4}`
            Source: `{5}`
            """;

        private static readonly Dictionary<string, string> NativeUserPrompts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-Hans"] =
                """
                请识别以下歌曲的官方发行曲名与官方艺名。
                若存在中国大陆官方使用的简体中文名称，优先使用该名称；若不存在，则使用原始官方名称。
                仅采用正式发行、官方平台、版权方或唱片信息中使用的名称，不要使用机器翻译、民间译名、非官方别称或自行简化。
                若同一作品存在多个地区版本名称，优先采用中国大陆正式使用名称；若无，则采用该作品最直接对应的原始官方发行名称。
                若提供的歌曲为翻唱版本，艺名应使用该翻唱版本在官方发行信息中对应的官方翻唱艺名，不要替换为原唱艺名。
                多人艺名请保留官方并列写法、角色名、CV 标注、连接符号与顺序。

                歌曲：`{0}`
                艺人：`{1}`
                时长：`{2}`
                来源：`{3}`
                """,

            ["zh-Hant"] =
                """
                請識別以下歌曲的官方發行曲名與官方藝名。
                若存在繁體中文地區官方使用的繁體中文名稱，優先使用該名稱；若不存在，則使用原始官方名稱。
                僅採用正式發行、官方平台、版權方或唱片資訊中使用的名稱，不要使用機器翻譯、民間譯名、非官方別稱或自行簡化。
                若同一作品存在多個地區版本名稱，優先採用繁體中文地區正式使用名稱；若無，則採用該作品最直接對應的原始官方發行名稱。
                若提供的歌曲為翻唱版本，藝名應使用該翻唱版本在官方發行資訊中對應的官方翻唱藝名，不要替換為原唱藝名。
                多人藝名請保留官方並列寫法、角色名、CV 標註、連接符號與順序。

                歌曲：`{0}`
                藝人：`{1}`
                時長：`{2}`
                來源：`{3}`
                """,

            ["ja"] =
                """
                以下の楽曲について、公式リリース上の楽曲名と公式アーティスト名を特定してください。
                日本で公式に使用されている日本語名称がある場合はそれを優先し、ない場合は元の公式名称を使用してください。
                名称は、正式リリース、公式プラットフォーム、権利元メタデータ、レーベルまたは配給元情報に記載されたもののみを採用してください。機械翻訳、ファン翻訳、非公式な別名、独自に簡略化した表記は使用しないでください。
                同一作品に複数の地域別公式名称がある場合は、日本で正式に使用されている名称を優先し、それがない場合は当該作品に直接対応する元の公式リリース名称を使用してください。
                指定された楽曲がカバー版である場合、アーティスト名はそのカバー版の公式リリース情報に記載された公式クレジット名義を使用し、原曲アーティスト名に置き換えないでください。
                複数アーティストの場合は、公式の並列表記、キャラクター名、CV表記、区切り記号、順序をそのまま保持してください。

                楽曲：`{0}`
                アーティスト：`{1}`
                再生時間：`{2}`
                出典：`{3}`
                """,
        };

        public static string BuildSystemPrompt() => GenericSystemTemplate;

        public static string BuildUserMessage(
            string rawTitle,
            string rawArtist,
            double durationSeconds,
            string sourceName,
            string? preferredLanguageCode,
            string? targetMarket,
            bool preferNativePrompt)
        {
            string duration = FormatDuration(durationSeconds);

            // If no preferred language is set, use the old minimal format
            if (string.IsNullOrEmpty(preferredLanguageCode))
            {
                return $"Song: `{rawTitle}`\nArtist: `{rawArtist}`\nDuration: `{duration}`\nSource: `{sourceName}`";
            }

            // Try native prompt if user prefers it
            if (preferNativePrompt
                && NativeUserPrompts.TryGetValue(preferredLanguageCode!, out string? nativeTemplate))
            {
                return string.Format(nativeTemplate, rawTitle, rawArtist, duration, sourceName);
            }

            // Generic English template with language & market substitution
            AiPromptLanguage? lang = AiPromptLanguage.All
                .FirstOrDefault(l => string.Equals(l.Code, preferredLanguageCode, StringComparison.OrdinalIgnoreCase));

            string languageName = lang?.EnglishName ?? preferredLanguageCode!;
            string market = !string.IsNullOrWhiteSpace(targetMarket)
                ? targetMarket!
                : lang?.DefaultMarket ?? "the target region";

            return string.Format(GenericUserTemplate, languageName, market, rawTitle, rawArtist, duration, sourceName);
        }

        private static string FormatDuration(double totalSeconds)
        {
            if (totalSeconds <= 0)
                return "Unknown";
            int seconds = (int)Math.Round(totalSeconds);
            int m = seconds / 60;
            int s = seconds % 60;
            return $"{m}:{s:D2}";
        }
    }
}
