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
        private static readonly string CachePath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Wisland", "ai-song-cache.json");

        private static readonly BinaryData StructuredOutputSchema = BinaryData.FromBytes(
            """
            {
                "type": "object",
                "properties": {
                    "title": {
                        "type": "string",
                        "description": "The clean, official song title without any tags, annotations, or platform artifacts"
                    },
                    "artist": {
                        "type": "string",
                        "description": "The clean artist name, including featured artists if applicable"
                    }
                },
                "required": ["title", "artist"],
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

            string systemPrompt = AiSongPromptBuilder.BuildSystemPrompt();
            string userMessage = AiSongPromptBuilder.BuildUserMessage(
                rawTitle, rawArtist, durationSeconds, sourceName,
                _settings.AiPreferredLanguage,
                _settings.AiTargetMarket,
                _settings.AiPreferNativePrompt);

            Logger.Debug($"AI system prompt:\n{systemPrompt}");
            Logger.Debug($"AI user message:\n{userMessage}");

            try
            {
                string provider = AiModelProviderNames.Normalize(profile.Provider);
                bool isGoogle = string.Equals(provider, nameof(AiModelProvider.GoogleAIStudio), StringComparison.Ordinal);
                Logger.Debug(isGoogle
                    ? $"AI request: provider={provider}, model={profile.ModelId}"
                    : $"AI request: provider={provider}, model={profile.ModelId}, endpoint={profile.Endpoint}");

                string? responseJson;

                if (isGoogle)
                    responseJson = await CallGoogleGenAiAsync(profile, systemPrompt, userMessage, ct);
                else
                    responseJson = await CallOpenAiCompatibleAsync(profile, systemPrompt, userMessage, ct);

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
        public static async Task<AiSongResult?> TestModelAsync(
            AiModelProfile profile, string testTitle, string testArtist, CancellationToken ct)
        {
            string systemPrompt = AiSongPromptBuilder.BuildSystemPrompt();
            string userMessage = AiSongPromptBuilder.BuildUserMessage(
                testTitle, testArtist, 0, "Test", null, null, false);

            string provider = AiModelProviderNames.Normalize(profile.Provider);
            string? responseJson;

            if (string.Equals(provider, nameof(AiModelProvider.GoogleAIStudio), StringComparison.Ordinal))
                responseJson = await CallGoogleGenAiAsync(profile, systemPrompt, userMessage, ct);
            else
                responseJson = await CallOpenAiCompatibleAsync(profile, systemPrompt, userMessage, ct);

            if (string.IsNullOrWhiteSpace(responseJson))
                return null;

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            return new AiSongResult
            {
                Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? testTitle : testTitle,
                Artist = root.TryGetProperty("artist", out var a) ? a.GetString() ?? testArtist : testArtist,
                ResolvedAtUtc = DateTimeOffset.UtcNow
            };
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

        private static async Task<string?> CallGoogleGenAiAsync(
            AiModelProfile profile, string systemPrompt, string userMessage, CancellationToken ct)
        {
            var client = new Google.GenAI.Client(apiKey: profile.ApiKey);

            var config = new GTypes.GenerateContentConfig
            {
                SystemInstruction = new GTypes.Content
                {
                    Parts = [new GTypes.Part { Text = systemPrompt }]
                },
                ResponseMimeType = "application/json",
                ResponseSchema = new GTypes.Schema
                {
                    Type = GTypes.Type.Object,
                    Properties = new Dictionary<string, GTypes.Schema>
                    {
                        ["title"] = new GTypes.Schema
                        {
                            Type = GTypes.Type.String,
                            Description = "The clean, official song title without any tags, annotations, or platform artifacts"
                        },
                        ["artist"] = new GTypes.Schema
                        {
                            Type = GTypes.Type.String,
                            Description = "The clean artist name, including featured artists if applicable"
                        }
                    },
                    Required = ["title", "artist"]
                },
                Temperature = 0.2
            };

            if (ShouldUseGoogleGrounding(profile))
            {
                config.Tools = [new GTypes.Tool { GoogleSearch = new GTypes.GoogleSearch() }];
            }

            if (TryNormalizeThinkingBudget(profile, out int budget))
            {
                config.ThinkingConfig = new GTypes.ThinkingConfig { ThinkingBudget = budget };
            }

            GTypes.GenerateContentResponse response;
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
                response = await client.Models.GenerateContentAsync(
                    model: profile.ModelId,
                    contents: userMessage,
                    config: config,
                    cancellationToken: ct);
            }

            var text = response.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            var usage = response.UsageMetadata;
            if (usage != null)
            {
                Logger.Debug($"Google GenAI usage: prompt={usage.PromptTokenCount}, candidates={usage.CandidatesTokenCount}, total={usage.TotalTokenCount}");
            }

            return text;
        }

        // ---- OpenAI-compatible path ----

        private static async Task<string?> CallOpenAiCompatibleAsync(
            AiModelProfile profile, string systemPrompt, string userMessage, CancellationToken ct)
        {
            ChatClient client = CreateOpenAiChatClient(profile);
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userMessage)
            };

            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "song_metadata",
                    jsonSchema: StructuredOutputSchema,
                    jsonSchemaIsStrict: true)
            };

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

        private static bool TryNormalizeThinkingBudget(AiModelProfile profile, out int budget)
        {
            budget = 0;

            switch (profile.ReasoningEffort?.Trim().ToLowerInvariant())
            {
                case "none": budget = 0; return true;
                case "low": budget = 1024; return true;
                case "medium": budget = 8192; return true;
                case "high": budget = 24576; return true;
                default: return false;
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
