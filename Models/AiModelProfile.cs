namespace wisland.Models
{
    public sealed class AiModelProfile
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Provider { get; set; } = nameof(AiModelProvider.OpenAICompatible);
        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;

        /// <summary>
        /// Google AI Studio only: reasoning effort level (low / medium / high).
        /// Null means use model default.
        /// </summary>
        public string? ReasoningEffort { get; set; }

        /// <summary>
        /// Google AI Studio only: whether Google grounding search is enabled.
        /// </summary>
        public bool GoogleGroundingEnabled { get; set; } = true;
    }
}
