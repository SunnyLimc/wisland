using System;
using System.Collections.Generic;
using System.ClientModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using GTypes = Google.GenAI.Types;
using OpenAI;
using OpenAI.Chat;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services
{
    public sealed class AiSongResolverService
    {
        private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };
        private static readonly string CachePath = Helpers.SafePaths.Combine("ai-song-cache.json");

        private static readonly BinaryData StructuredOutputSchema = BinaryData.FromBytes(
            """
            {
                "type": "object",
                "properties": {
                    "title": {
                        "type": "string"
                    },
                    "title-alt": {
                        "type": "string"
                    },
                    "title-alt2": {
                        "type": "string"
                    },
                    "artist": {
                        "type": "string"
                    },
                    "artist-alt": {
                        "type": "string"
                    },
                    "artist-alt2": {
                        "type": "string"
                    }
                },
                "required": ["title", "title-alt", "title-alt2", "artist", "artist-alt", "artist-alt2"],
                "additionalProperties": false
            }
            """u8.ToArray());

        private readonly SettingsService _settings;
        private readonly Dictionary<string, AiSongResult> _cache = new(StringComparer.Ordinal);
        private readonly List<string> _cacheInsertionOrder = new();
        private readonly object _gate = new();

        public AiSongResolverService(SettingsService settings)
        {
            _settings = settings;
            LoadCache();
        }

        public int CacheCount
        {
            get { lock (_gate) return _cache.Count; }
        }

        public event Action? CacheChanged;

        public AiSongResult? TryGetCached(string sourceAppId, string rawTitle, string rawArtist)
        {
            string key = BuildCacheKey(sourceAppId, rawTitle, rawArtist);
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var result))
                {
                    Logger.Trace($"AI cache hit: '{rawTitle}' by '{rawArtist}' -> '{result.Title}' by '{result.Artist}'");
                    return result;
                }
                Logger.Trace($"AI cache miss: '{rawTitle}' by '{rawArtist}'");
                return null;
            }
        }

        public async Task<AiSongResult?> ResolveAsync(
            string sourceAppId, string rawTitle, string rawArtist,
            string sourceName, double durationSeconds,
            CancellationToken ct)
        {
            string key = BuildCacheKey(sourceAppId, rawTitle, rawArtist);
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return cached;
            }

            AiModelProfile? profile = GetActiveProfile();
            if (profile == null)
                return null;

            Logger.Info($"AI song resolution requested: '{rawTitle}' by '{rawArtist}' from '{sourceName}' ({sourceAppId})");

            var (userMessage, templateName) = AiSongPromptBuilder.BuildUserMessage(
                rawTitle, rawArtist, durationSeconds, sourceName,
                _settings.AiPreferredLanguage,
                _settings.AiTargetMarket,
                _settings.AiPreferNativePrompt);

            try
            {
                string provider = AiModelProviderNames.Normalize(profile.Provider);
                bool isGoogle = string.Equals(provider, nameof(AiModelProvider.GoogleAIStudio), StringComparison.Ordinal);
                Logger.Debug(isGoogle
                    ? $"AI request: template={templateName}, provider={provider}, model={profile.ModelId}, temperature={profile.Temperature:F1}, thinking={profile.ReasoningEffort ?? "default"}, grounding={profile.GoogleGroundingEnabled}"
                    : $"AI request: template={templateName}, provider={provider}, model={profile.ModelId}, endpoint={profile.Endpoint}, temperature={profile.Temperature:F1}");
                Logger.Trace($"AI user message:\n{userMessage}");

                string? responseJson;

                if (isGoogle)
                    responseJson = (await CallGoogleGenAiAsync(profile, userMessage, ct)).Text;
                else
                    responseJson = await CallOpenAiCompatibleAsync(profile, userMessage, ct);

                if (string.IsNullOrWhiteSpace(responseJson))
                    return null;

                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                string resolvedTitle = root.TryGetProperty("title", out var t) ? t.GetString() ?? rawTitle : rawTitle;
                string resolvedArtist = root.TryGetProperty("artist", out var a) ? a.GetString() ?? rawArtist : rawArtist;

                var result = new AiSongResult
                {
                    Title = resolvedTitle,
                    Artist = resolvedArtist,
                    ResolvedAtUtc = DateTimeOffset.UtcNow
                };

                Logger.Info($"AI song resolved: '{rawTitle}' by '{rawArtist}' -> '{resolvedTitle}' by '{resolvedArtist}'");
                LogAlternatives(root);

                lock (_gate)
                {
                    _cache[key] = result;
                    _cacheInsertionOrder.Remove(key);
                    _cacheInsertionOrder.Add(key);
                }

                SaveCache();
                CacheChanged?.Invoke();
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"AI song resolution failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Tests a model profile by sending a simple song resolution request.
        /// Returns the parsed result or throws on failure.
        /// </summary>
        public record TestModelResult(
            string Title, string Artist,
            string? TitleAlt, string? TitleAlt2,
            string? ArtistAlt, string? ArtistAlt2,
            bool? GroundingUsed)
        {
            public string FormatAlternatives()
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(TitleAlt)) parts.Add($"title-alt: {TitleAlt}");
                if (!string.IsNullOrEmpty(TitleAlt2)) parts.Add($"title-alt2: {TitleAlt2}");
                if (!string.IsNullOrEmpty(ArtistAlt)) parts.Add($"artist-alt: {ArtistAlt}");
                if (!string.IsNullOrEmpty(ArtistAlt2)) parts.Add($"artist-alt2: {ArtistAlt2}");
                return string.Join(" | ", parts);
            }
        }

        public static async Task<TestModelResult?> TestModelAsync(
            AiModelProfile profile, string testTitle, string testArtist,
            double durationSeconds, string sourceName,
            string? preferredLanguage, string? targetMarket, bool preferNativePrompt,
            CancellationToken ct)
        {
            var (userMessage, templateName) = AiSongPromptBuilder.BuildUserMessage(
                testTitle, testArtist, durationSeconds, sourceName,
                preferredLanguage, targetMarket, preferNativePrompt);

            string provider = AiModelProviderNames.Normalize(profile.Provider);
            bool isGoogle = string.Equals(provider, nameof(AiModelProvider.GoogleAIStudio), StringComparison.Ordinal);

            Logger.Info($"AI test requested: '{testTitle}' by '{testArtist}' from '{sourceName}'");
            Logger.Debug(isGoogle
                ? $"AI request: template={templateName}, provider={provider}, model={profile.ModelId}, temperature={profile.Temperature:F1}, thinking={profile.ReasoningEffort ?? "default"}, grounding={profile.GoogleGroundingEnabled}"
                : $"AI request: template={templateName}, provider={provider}, model={profile.ModelId}, endpoint={profile.Endpoint}, temperature={profile.Temperature:F1}");
            Logger.Trace($"AI user message:\n{userMessage}");

            string? responseJson;
            bool? groundingUsed = null;

            if (isGoogle)
            {
                var result = await CallGoogleGenAiAsync(profile, userMessage, ct);
                responseJson = result.Text;
                groundingUsed = result.GroundingUsed;
            }
            else
            {
                responseJson = await CallOpenAiCompatibleAsync(profile, userMessage, ct);
            }

            if (string.IsNullOrWhiteSpace(responseJson))
            {
                Logger.Warn("AI test: empty response from model");
                return null;
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            var testResult = new TestModelResult(
                root.TryGetProperty("title", out var t) ? t.GetString() ?? testTitle : testTitle,
                root.TryGetProperty("artist", out var a) ? a.GetString() ?? testArtist : testArtist,
                root.TryGetProperty("title-alt", out var ta) ? ta.GetString() : null,
                root.TryGetProperty("title-alt2", out var ta2) ? ta2.GetString() : null,
                root.TryGetProperty("artist-alt", out var aa) ? aa.GetString() : null,
                root.TryGetProperty("artist-alt2", out var aa2) ? aa2.GetString() : null,
                groundingUsed);

            Logger.Info($"AI song resolved: '{testTitle}' by '{testArtist}' -> '{testResult.Title}' by '{testResult.Artist}'"
                + (groundingUsed.HasValue ? $" [grounding: {(groundingUsed.Value ? "✓" : "✗")}]" : ""));
            LogAlternatives(root);

            return testResult;
        }

        public void ClearCache()
        {
            lock (_gate)
            {
                _cache.Clear();
                _cacheInsertionOrder.Clear();
            }
            SaveCache();
            CacheChanged?.Invoke();
        }

        public void ClearLastEntry()
        {
            lock (_gate)
            {
                if (_cacheInsertionOrder.Count == 0)
                    return;
                string lastKey = _cacheInsertionOrder[^1];
                _cacheInsertionOrder.RemoveAt(_cacheInsertionOrder.Count - 1);
                _cache.Remove(lastKey);
            }
            SaveCache();
            CacheChanged?.Invoke();
        }

        private AiModelProfile? GetActiveProfile()
        {
            string? activeId = _settings.ActiveAiModelId;
            if (string.IsNullOrEmpty(activeId))
                return null;
            return _settings.AiModels.FirstOrDefault(m => string.Equals(m.Id, activeId, StringComparison.Ordinal));
        }

        // ---- Google GenAI native path ----

        private static async Task<(string? Text, bool? GroundingUsed)> CallGoogleGenAiAsync(
            AiModelProfile profile, string userMessage, CancellationToken ct)
        {
            var client = new Google.GenAI.Client(apiKey: profile.ApiKey);

            var config = new GTypes.GenerateContentConfig
            {
                ResponseMimeType = "application/json",
                ResponseSchema = new GTypes.Schema
                {
                    Type = GTypes.Type.Object,
                    Properties = new Dictionary<string, GTypes.Schema>
                    {
                        ["title"] = new GTypes.Schema { Type = GTypes.Type.String },
                        ["title-alt"] = new GTypes.Schema { Type = GTypes.Type.String },
                        ["title-alt2"] = new GTypes.Schema { Type = GTypes.Type.String },
                        ["artist"] = new GTypes.Schema { Type = GTypes.Type.String },
                        ["artist-alt"] = new GTypes.Schema { Type = GTypes.Type.String },
                        ["artist-alt2"] = new GTypes.Schema { Type = GTypes.Type.String }
                    },
                    Required = ["title", "title-alt", "title-alt2", "artist", "artist-alt", "artist-alt2"],
                    PropertyOrdering = ["title", "title-alt", "title-alt2", "artist", "artist-alt", "artist-alt2"]
                },
                Temperature = (float)profile.Temperature
            };

            if (ShouldUseGoogleGrounding(profile))
            {
                config.Tools = [new GTypes.Tool { GoogleSearch = new GTypes.GoogleSearch() }];
            }

            if (TryApplyThinkingConfig(profile, config))
            {
            }

            Logger.Debug($"Google GenAI config: model={profile.ModelId}, temperature={config.Temperature:F1}"
                + $", schema=6-field JSON, grounding={config.Tools?.Count > 0}"
                + (config.ThinkingConfig != null
                    ? $", thinking={config.ThinkingConfig.ThinkingLevel?.ToString() ?? $"budget={config.ThinkingConfig.ThinkingBudget}"}"
                    : ""));
            Logger.Trace($"Google GenAI prompt:\n{userMessage}");

            GTypes.GenerateContentResponse response;
            bool groundingFellBack = false;
            try
            {
                response = await client.Models.GenerateContentAsync(
                    model: profile.ModelId,
                    contents: userMessage,
                    config: config,
                    cancellationToken: ct);
            }
            catch (Exception ex) when (config.Tools?.Count > 0 && !ct.IsCancellationRequested)
            {
                Logger.Warn($"Google GenAI grounding request failed, retrying without grounding: {ex.Message}");
                config.Tools = null;
                groundingFellBack = true;
                response = await client.Models.GenerateContentAsync(
                    model: profile.ModelId,
                    contents: userMessage,
                    config: config,
                    cancellationToken: ct);
            }

            var text = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            bool? groundingUsed = null;
            if (!groundingFellBack && ShouldUseGoogleGrounding(profile))
            {
                var grounding = response.Candidates?.FirstOrDefault()?.GroundingMetadata;
                groundingUsed = grounding?.WebSearchQueries?.Count > 0
                                || grounding?.GroundingChunks?.Count > 0;
                Logger.Info(groundingUsed == true
                    ? $"Google GenAI grounding: active ({grounding?.WebSearchQueries?.Count ?? 0} queries, {grounding?.GroundingChunks?.Count ?? 0} chunks)"
                    : "Google GenAI grounding: requested but not used by model");
            }
            else if (groundingFellBack)
            {
                groundingUsed = false;
                Logger.Warn("Google GenAI grounding: fell back to non-grounded request");
            }

            var usage = response.UsageMetadata;
            if (usage != null)
            {
                Logger.Debug($"Google GenAI usage: prompt={usage.PromptTokenCount}, candidates={usage.CandidatesTokenCount}, total={usage.TotalTokenCount}");
            }

            return (text, groundingUsed);
        }

        // ---- OpenAI-compatible path ----

        private static async Task<string?> CallOpenAiCompatibleAsync(
            AiModelProfile profile, string userMessage, CancellationToken ct)
        {
            ChatClient client = CreateOpenAiChatClient(profile);
            var messages = new List<ChatMessage>
            {
                new UserChatMessage(userMessage)
            };

            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "song_metadata",
                    jsonSchema: StructuredOutputSchema,
                    jsonSchemaIsStrict: true),
                Temperature = (float)profile.Temperature
            };

            Logger.Debug($"OpenAI config: model={profile.ModelId}, endpoint={profile.Endpoint}, temperature={profile.Temperature:F1}, schema=song_metadata (strict)");
            Logger.Trace($"OpenAI prompt:\n{userMessage}");

            ChatCompletion completion = await client.CompleteChatAsync(messages, options, ct);
            if (completion.Content.Count == 0 || string.IsNullOrWhiteSpace(completion.Content[0].Text))
                return null;
            return completion.Content[0].Text;
        }

        private static ChatClient CreateOpenAiChatClient(AiModelProfile profile)
        {
            var credential = new System.ClientModel.ApiKeyCredential(profile.ApiKey);
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(profile.Endpoint)
            };
            return new ChatClient(profile.ModelId, credential, options);
        }

        private static string BuildCacheKey(string sourceAppId, string rawTitle, string rawArtist)
            => $"{sourceAppId}\x1f{rawTitle}\x1f{rawArtist}";

        private static void LogAlternatives(JsonElement root)
        {
            string? titleAlt = root.TryGetProperty("title-alt", out var ta) ? ta.GetString() : null;
            string? titleAlt2 = root.TryGetProperty("title-alt2", out var ta2) ? ta2.GetString() : null;
            string? artistAlt = root.TryGetProperty("artist-alt", out var aa) ? aa.GetString() : null;
            string? artistAlt2 = root.TryGetProperty("artist-alt2", out var aa2) ? aa2.GetString() : null;
            if (!string.IsNullOrEmpty(titleAlt) || !string.IsNullOrEmpty(artistAlt))
            {
                Logger.Debug($"AI alternatives: title-alt='{titleAlt}', title-alt2='{titleAlt2}', artist-alt='{artistAlt}', artist-alt2='{artistAlt2}'");
            }
        }

        private static bool ShouldUseGoogleGrounding(AiModelProfile profile)
        {
            return profile.GoogleGroundingEnabled && SupportsGoogleGrounding(profile.ModelId);
        }

        private static bool SupportsGoogleGrounding(string modelId)
        {
            return modelId.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase)
                || modelId.StartsWith("gemini-4", StringComparison.OrdinalIgnoreCase)
                || modelId.StartsWith("gemini-5", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryApplyThinkingConfig(AiModelProfile profile, GTypes.GenerateContentConfig config)
        {
            switch (profile.ReasoningEffort?.Trim().ToLowerInvariant())
            {
                case "minimal":
                    config.ThinkingConfig = new GTypes.ThinkingConfig { ThinkingLevel = GTypes.ThinkingLevel.Minimal };
                    return true;
                case "low":
                    config.ThinkingConfig = new GTypes.ThinkingConfig { ThinkingLevel = GTypes.ThinkingLevel.Low };
                    return true;
                case "medium":
                    config.ThinkingConfig = new GTypes.ThinkingConfig { ThinkingLevel = GTypes.ThinkingLevel.Medium };
                    return true;
                case "high":
                    config.ThinkingConfig = new GTypes.ThinkingConfig { ThinkingLevel = GTypes.ThinkingLevel.High };
                    return true;
                case "none":
                    config.ThinkingConfig = new GTypes.ThinkingConfig { ThinkingBudget = 0 };
                    return true;
                default:
                    return false;
            }
        }

        private void LoadCache()
        {
            try
            {
                if (!System.IO.File.Exists(CachePath))
                    return;
                string json = System.IO.File.ReadAllText(CachePath);
                var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);
                if (entries == null) return;
                lock (_gate)
                {
                    _cache.Clear();
                    _cacheInsertionOrder.Clear();
                    foreach (var e in entries)
                    {
                        if (string.IsNullOrEmpty(e.Key)) continue;
                        var result = new AiSongResult
                        {
                            Title = e.Title ?? string.Empty,
                            Artist = e.Artist ?? string.Empty,
                            ResolvedAtUtc = e.ResolvedAtUtc
                        };
                        _cache[e.Key] = result;
                        _cacheInsertionOrder.Add(e.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load AI song cache: {ex.Message}");
            }
        }

        private void SaveCache()
        {
            try
            {
                var dir = Path.GetDirectoryName(CachePath)!;
                var tempPath = CachePath + ".tmp";
                Directory.CreateDirectory(dir);

                List<CacheEntry> entries;
                lock (_gate)
                {
                    entries = new List<CacheEntry>(_cacheInsertionOrder.Count);
                    foreach (var key in _cacheInsertionOrder)
                    {
                        if (_cache.TryGetValue(key, out var result))
                        {
                            entries.Add(new CacheEntry
                            {
                                Key = key,
                                Title = result.Title,
                                Artist = result.Artist,
                                ResolvedAtUtc = result.ResolvedAtUtc
                            });
                        }
                    }
                }

                string json = JsonSerializer.Serialize(entries, CacheJsonOptions);
                System.IO.File.WriteAllText(tempPath, json);
                System.IO.File.Move(tempPath, CachePath, true);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save AI song cache");
            }
        }

        private sealed class CacheEntry
        {
            public string? Key { get; set; }
            public string? Title { get; set; }
            public string? Artist { get; set; }
            public DateTimeOffset ResolvedAtUtc { get; set; }
        }
    }
}
