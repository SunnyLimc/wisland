using System;
using System.Collections.Generic;
using System.ClientModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wisland", "ai-song-cache.json");
        private static readonly BinaryData GoogleGroundingExtraBody = BinaryData.FromBytes(
            """
            {
                "google": {
                    "tools": [
                        {
                            "google_search": {}
                        }
                    ]
                }
            }
            """u8.ToArray());

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

            try
            {
                ChatClient client = CreateChatClient(profile);
                Logger.Debug($"AI request: provider={AiModelProviderNames.Normalize(profile.Provider)}, model={profile.ModelId}, endpoint={profile.Endpoint}");

                string systemPrompt = AiSongPromptBuilder.BuildSystemPrompt();
                string userMessage = AiSongPromptBuilder.BuildUserMessage(
                    rawTitle, rawArtist, durationSeconds, sourceName,
                    _settings.AiPreferredLanguage,
                    _settings.AiTargetMarket,
                    _settings.AiPreferNativePrompt);

                Logger.Debug($"AI system prompt:\n{systemPrompt}");
                Logger.Debug($"AI user message:\n{userMessage}");

                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(systemPrompt),
                    new UserChatMessage(userMessage)
                };

                bool useGoogleGrounding = ShouldUseGoogleGrounding(profile);
                ChatCompletion completion;

                try
                {
                    completion = await client.CompleteChatAsync(
                        messages,
                        CreateChatCompletionOptions(profile, useGoogleGrounding),
                        ct);
                }
                catch (ClientResultException ex) when (useGoogleGrounding)
                {
                    Logger.Warn($"Google AI Studio grounding request failed, retrying without grounding: {ex.Message}");
                    completion = await client.CompleteChatAsync(
                        messages,
                        CreateChatCompletionOptions(profile, enableGoogleGrounding: false),
                        ct);
                }

                if (completion.Content.Count == 0 || string.IsNullOrWhiteSpace(completion.Content[0].Text))
                    return null;

                using var doc = JsonDocument.Parse(completion.Content[0].Text);
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

        private static ChatClient CreateChatClient(AiModelProfile profile)
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

        private static ChatCompletionOptions CreateChatCompletionOptions(AiModelProfile profile, bool enableGoogleGrounding)
        {
            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "song_metadata",
                    jsonSchema: StructuredOutputSchema,
                    jsonSchemaIsStrict: true)
            };

            if (TryNormalizeReasoningEffort(profile, out string reasoningEffort))
            {
#pragma warning disable SCME0001
                options.Patch.Set("$.reasoning_effort"u8, reasoningEffort);
#pragma warning restore SCME0001
            }

            if (enableGoogleGrounding)
            {
#pragma warning disable OPENAI001
                options.WebSearchOptions = new ChatWebSearchOptions();
#pragma warning restore OPENAI001
#pragma warning disable SCME0001
                options.Patch.Set("$.extra_body"u8, GoogleGroundingExtraBody);
#pragma warning restore SCME0001
            }

            return options;
        }

        private static bool ShouldUseGoogleGrounding(AiModelProfile profile)
        {
            return string.Equals(
                       AiModelProviderNames.Normalize(profile.Provider),
                       nameof(AiModelProvider.GoogleAIStudio),
                       StringComparison.Ordinal)
                   && profile.GoogleGroundingEnabled
                   && SupportsGoogleGrounding(profile.ModelId);
        }

        private static bool SupportsGoogleGrounding(string modelId)
        {
            return modelId.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase)
                || modelId.StartsWith("gemini-4", StringComparison.OrdinalIgnoreCase)
                || modelId.StartsWith("gemini-5", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryNormalizeReasoningEffort(AiModelProfile profile, out string reasoningEffort)
        {
            reasoningEffort = string.Empty;

            if (!string.Equals(
                AiModelProviderNames.Normalize(profile.Provider),
                nameof(AiModelProvider.GoogleAIStudio),
                StringComparison.Ordinal))
            {
                return false;
            }

            switch (profile.ReasoningEffort?.Trim().ToLowerInvariant())
            {
                case "none":
                case "minimal":
                case "low":
                case "medium":
                case "high":
                    reasoningEffort = profile.ReasoningEffort.Trim().ToLowerInvariant();
                    return true;
                default:
                    return false;
            }
        }

        private void LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                    return;
                string json = File.ReadAllText(CachePath);
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
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, CachePath, true);
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
